using NAudio.Lame;
using NAudio.SoundFile;
using NAudio.Wave;
using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WellenEditor;

public sealed class AudioDocument : IDisposable
{
    private const int WaveformPreviewFrames = 8192;
    private static int orphanUndoCleanupDone;
    private readonly Func<float[], string> snapshotWriter;

    private sealed record AudioSnapshot(string Path);
    private readonly Stack<AudioSnapshot> undoHistory = new();
    private readonly Stack<AudioSnapshot> redoHistory = new();
    private float[] samples;
    private float[] waveformSamples;
    private string? streamingWavPath;
    private int streamingFrameCount;
    private List<AudioSourceSegment>? streamingSegments;
    private readonly Stack<List<AudioSourceSegment>> streamingUndoHistory = new();
    private readonly Stack<List<AudioSourceSegment>> streamingRedoHistory = new();
    private readonly object peakCacheGate = new();
    private CancellationTokenSource? peakCacheCancellation;
    private long peakCacheGeneration;
    private CancellationTokenSource? spectrogramCancellation;
    private readonly object spectrogramGate = new();
    private long spectrogramGeneration;
    private bool spectrogramBuilding;

    private AudioDocument(string sourcePath, float[] samples, int channels, int sampleRate, Func<float[], string>? snapshotWriter = null)
    {
        this.snapshotWriter = snapshotWriter ?? WriteSnapshot;
        SourcePath = sourcePath;
        this.samples = samples;
        waveformSamples = samples;
        Channels = channels;
        SampleRate = sampleRate;
        DisplayName = string.IsNullOrWhiteSpace(sourcePath) ? string.Empty : Path.GetFileName(sourcePath);
        IsModified = false;
    }

    private AudioDocument(string sourcePath, WavFileAudioSource source, float[] preview)
    {
        snapshotWriter = WriteSnapshot;
        SourcePath = sourcePath;
        samples = Array.Empty<float>();
        waveformSamples = preview;
        streamingWavPath = sourcePath;
        streamingFrameCount = checked((int)source.FrameCount);
        OriginalWaveBitsPerSample = source.BitsPerSample;
        OriginalWaveIsIeeeFloat = source.IsIeeeFloat;
        streamingSegments = new List<AudioSourceSegment> { new(0, source.FrameCount) };
        Channels = source.Channels;
        SampleRate = source.SampleRate;
        DisplayName = Path.GetFileName(sourcePath);
        IsModified = false;
    }

    private string sourcePath = string.Empty;
    private long? sourceLengthAtOpen;
    private DateTime? sourceWriteAtOpen;
    public string SourcePath
    {
        get => sourcePath;
        private set
        {
            sourcePath = value;
            var info = string.IsNullOrWhiteSpace(value) ? null : new FileInfo(value);
            sourceLengthAtOpen = info?.Exists == true ? info.Length : null;
            sourceWriteAtOpen = info?.Exists == true ? info.LastWriteTimeUtc : null;
        }
    }
    internal bool OriginalSourceChanged
    {
        get
        {
            if (sourceLengthAtOpen is null) return false;
            var info = new FileInfo(SourcePath);
            return !info.Exists || info.Length != sourceLengthAtOpen || info.LastWriteTimeUtc != sourceWriteAtOpen;
        }
    }
    /// <summary>
    /// Vollständige Samples für Bearbeitung und Export. Bei einer gestreamten WAV-Datei
    /// wird erst beim expliziten Bearbeitungszugriff materialisiert.
    /// </summary>
    public float[] Samples
    {
        get
        {
            EnsureMaterialized();
            return samples;
        }
        private set => samples = value;
    }
    /// <summary>Kleine Anzeige-Repräsentation für die Wellenform; lädt keine ganze WAV-Datei.</summary>
    public float[] WaveformSamples => waveformSamples;
    public bool IsStreaming => streamingWavPath is not null;
    internal WaveformPeakCache? PeakCache { get; private set; }
    internal event EventHandler? PeakCacheReady;
    internal SpectrogramCache? Spectrogram { get; private set; }
    internal event EventHandler? SpectrogramReady;
    public string DisplayName { get; set; }
    private bool modified;
    internal long ContentVersion { get; private set; }
    public bool IsModified { get => modified; private set { modified = value; if (value) ContentVersion++; } }
    public int Channels { get; }
    public int SampleRate { get; }
    public int? OriginalWaveBitsPerSample { get; private set; }
    public bool OriginalWaveIsIeeeFloat { get; private set; }
    public int FrameCount => IsStreaming ? streamingFrameCount : samples.Length / Channels;
    public TimeSpan Duration => TimeSpan.FromSeconds(FrameCount / (double)SampleRate);
    public bool CanUndo => streamingUndoHistory.Count > 0 || undoHistory.Count > 0;
    public bool CanRedo => streamingRedoHistory.Count > 0 || redoHistory.Count > 0;

    public static AudioDocument FromSamples(float[] samples, int channels, int sampleRate, string? sourcePath = null)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Length == 0) throw new InvalidDataException("Die Audiodatei enthält keine Samples.");
        if (samples.Length % channels != 0) throw new ArgumentException("Die Sample-Anzahl ist kein Vielfaches der Kanalanzahl.", nameof(samples));
        return new AudioDocument(sourcePath ?? string.Empty, samples, channels, sampleRate);
    }

    internal static AudioDocument FromSamplesForSnapshotTest(float[] samples, int channels, int sampleRate, Func<float[], string> snapshotWriter)
    {
        ArgumentNullException.ThrowIfNull(snapshotWriter);
        if (samples.Length == 0) throw new InvalidDataException("Die Audiodatei enthält keine Samples.");
        if (channels < 1 || samples.Length % channels != 0) throw new ArgumentException("Ungültige Kanalanzahl oder Sampleanzahl.");
        return new AudioDocument(string.Empty, samples, channels, sampleRate, snapshotWriter);
    }

    public static AudioDocument Load(string path)
    {
        var fullPath = Path.GetFullPath(path);
#if BETA_PROFILE
        if (!string.Equals(Path.GetExtension(fullPath), ".wav", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("Diese Beta unterstützt ausschließlich WAV-Dateien.");
#endif
        if (string.Equals(Path.GetExtension(fullPath), ".wav", StringComparison.OrdinalIgnoreCase))
        {
            using var source = new WavFileAudioSource(fullPath);
            if (source.FrameCount == 0) throw new InvalidDataException("Die Audiodatei enthält keine Samples.");
            if (source.FrameCount > int.MaxValue)
                throw new NotSupportedException("WAV-Dateien mit mehr als 2.147.483.647 Frames werden in dieser Ansicht noch nicht unterstützt.");
            var document = new AudioDocument(fullPath, source, BuildWaveformPreview(source));
            document.StartPeakCacheBuild();
            return document;
        }

        if (Path.GetExtension(fullPath).ToLowerInvariant() is ".flac" or ".ogg" or ".opus" or ".mp3")
        {
            using var soundFileReader = new SoundFileReader(fullPath);
            return LoadDecodedSoundFile(fullPath, soundFileReader, soundFileReader.WaveFormat);
        }

        using var reader = new AudioFileReader(fullPath);
        return LoadDecodedSoundFile(fullPath, reader, reader.WaveFormat);
    }

    // Offline diagnostics need source data, without concurrent UI waveform jobs
    // contaminating disk, CPU and memory measurements.
    internal static AudioDocument OpenWaveForProcessing(string path)
    {
        var fullPath = Path.GetFullPath(path);
        using var source = new WavFileAudioSource(fullPath);
        if (source.FrameCount is <= 0 or > int.MaxValue) throw new NotSupportedException("Ungültige WAV-Länge.");
        return new AudioDocument(fullPath, source, Array.Empty<float>());
    }

    private static AudioDocument LoadDecodedSoundFile(string path, ISampleProvider reader, WaveFormat waveFormat)
    {
        var samples = new List<float>();
        var buffer = new float[waveFormat.SampleRate * waveFormat.Channels];
        int read;
        while ((read = reader.Read(buffer.AsSpan())) > 0)
        {
            samples.AddRange(buffer.AsSpan(0, read).ToArray());
        }
        if (samples.Count == 0) throw new InvalidDataException("Die Audiodatei enthält keine Samples.");
        return new AudioDocument(path, samples.ToArray(), waveFormat.Channels, waveFormat.SampleRate);
    }

    public ISampleProvider CreateSampleProvider(int startFrame, int frames)
    {
        EnsureMaterialized();
        var boundedStart = Math.Clamp(startFrame, 0, FrameCount);
        var boundedFrames = Math.Clamp(frames, 0, FrameCount - boundedStart);
        var selection = new float[boundedFrames * Channels];
        Array.Copy(Samples, boundedStart * Channels, selection, 0, selection.Length);
        return new ArraySampleProvider(selection, WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels));
    }

    public IWaveProvider CreateWaveProvider(int startFrame, int frames)
    {
        var boundedStart = Math.Clamp(startFrame, 0, FrameCount);
        var boundedFrames = Math.Clamp(frames, 0, FrameCount - boundedStart);
        if (streamingWavPath is not null)
            return new StreamingWaveProvider(CreateSegmentedSource(), boundedStart, boundedFrames);
        return new ArrayWaveProvider(Samples, boundedStart * Channels, boundedFrames * Channels, WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels));
    }

    /// <summary>Materialisiert eine gestreamte Datei für einen destruktiven Bearbeitungsvorgang.</summary>
    public void EnsureMaterialized()
    {
        if (streamingWavPath is null) return;

        var sampleCount = checked((long)streamingFrameCount * Channels);
        if (sampleCount > Array.MaxLength)
            throw new NotSupportedException("Diese Datei ist für die bisherige destruktive Bearbeitung zu groß. Wiedergabe bleibt per Disk-Streaming möglich.");

        var loaded = ReadStreamingSegments(streamingSegments!);
        ConvertStreamingHistoryToSnapshots();
        samples = loaded;
        waveformSamples = loaded;
        streamingWavPath = null;
        streamingFrameCount = 0;
        streamingSegments = null;
        InvalidatePeakCache();
    }

    private void ConvertStreamingHistoryToSnapshots()
    {
        var convertedUndo = new Stack<AudioSnapshot>();
        var convertedRedo = new Stack<AudioSnapshot>();
        try
        {
            foreach (var segments in streamingUndoHistory.Reverse())
                convertedUndo.Push(CreateSnapshot(ReadStreamingSegments(segments)));
            foreach (var segments in streamingRedoHistory.Reverse())
                convertedRedo.Push(CreateSnapshot(ReadStreamingSegments(segments)));
        }
        catch
        {
            ClearSnapshots(convertedUndo);
            ClearSnapshots(convertedRedo);
            throw;
        }

        foreach (var snapshot in convertedUndo.Reverse()) undoHistory.Push(snapshot);
        foreach (var snapshot in convertedRedo.Reverse()) redoHistory.Push(snapshot);
        streamingUndoHistory.Clear();
        streamingRedoHistory.Clear();
    }

    private float[] ReadStreamingSegments(List<AudioSourceSegment> segments)
    {
        var frameCount = checked((int)segments.Sum(segment => segment.FrameCount));
        var sampleCount = checked((long)frameCount * Channels);
        if (sampleCount > Array.MaxLength)
            throw new NotSupportedException("Ein Undo-Schritt ist für die destruktive Bearbeitung zu groß.");
        var loaded = new float[(int)sampleCount];
        using var source = new SegmentedAudioSource(new WavFileAudioSource(streamingWavPath!), segments.ToArray());
        const int blockFrames = 4096;
        for (var frame = 0; frame < frameCount; frame += blockFrames)
        {
            var count = Math.Min(blockFrames, frameCount - frame);
            if (source.ReadFrames(frame, loaded, frame, count) != count)
                throw new EndOfStreamException("Die WAV-Datei konnte nicht vollständig für die Bearbeitung gelesen werden.");
        }
        return loaded;
    }

    private static float[] BuildWaveformPreview(IAudioSource source)
    {
        var previewFrames = (int)Math.Min(WaveformPreviewFrames, source.FrameCount);
        var preview = new float[checked(previewFrames * source.Channels)];
        if (previewFrames == 0) return preview;

        for (var index = 0; index < previewFrames; index++)
        {
            var sourceFrame = index * source.FrameCount / previewFrames;
            if (source.ReadFrames(sourceFrame, preview, index, 1) != 1)
                throw new EndOfStreamException("Die WAV-Datei konnte nicht für die Wellenform gelesen werden.");
        }
        return preview;
    }

    private void StartPeakCacheBuild()
    {
        if (streamingWavPath is null || streamingFrameCount == 0) return;
        var path = streamingWavPath;
        var segments = streamingSegments!.ToArray();
        CancellationTokenSource cancellation;
        long generation;
        lock (peakCacheGate)
        {
            peakCacheCancellation?.Cancel();
            peakCacheCancellation = cancellation = new CancellationTokenSource();
            generation = ++peakCacheGeneration;
        }
        var token = cancellation.Token;
        _ = Task.Run(() =>
        {
            try
            {
                using var source = new SegmentedAudioSource(new WavFileAudioSource(path), segments);
                var cache = WaveformPeakCache.Build(source, token);
                var publish = false;
                lock (peakCacheGate)
                {
                    if (!token.IsCancellationRequested && generation == peakCacheGeneration)
                    {
                        PeakCache = cache;
                        publish = true;
                    }
                }
                if (publish) PeakCacheReady?.Invoke(this, EventArgs.Empty);
            }
            catch (OperationCanceledException) { }
            catch { /* Die sofortige Vorschau bleibt bei fehlerhaften Hintergrund-Scans nutzbar. */ }
            finally
            {
                lock (peakCacheGate)
                    if (ReferenceEquals(peakCacheCancellation, cancellation)) peakCacheCancellation = null;
                cancellation.Dispose();
            }
        });
    }

    internal void RequestSpectrogram()
    {
        if (FrameCount == 0) return;
        CancellationTokenSource cancellation;
        long generation;
        lock (spectrogramGate)
        {
            if (Spectrogram is not null || spectrogramBuilding) return;
            spectrogramCancellation?.Cancel();
            spectrogramCancellation = cancellation = new CancellationTokenSource();
            generation = ++spectrogramGeneration;
            spectrogramBuilding = true;
        }
        var token = cancellation.Token;
        var path = streamingWavPath;
        var segments = streamingSegments?.ToArray();
        var memory = streamingWavPath is null ? samples : null;
        _ = Task.Run(() =>
        {
            try
            {
                using IAudioSource source = path is not null
                    ? new SegmentedAudioSource(new WavFileAudioSource(path), segments!)
                    : new MemoryAudioSource(memory!, Channels, SampleRate);
                var cache = SpectrogramCache.Build(source, token);
                var publish = false;
                lock (spectrogramGate)
                {
                    if (generation == spectrogramGeneration && !token.IsCancellationRequested)
                    {
                        Spectrogram = cache;
                        publish = true;
                    }
                }
                if (publish)
                    SpectrogramReady?.Invoke(this, EventArgs.Empty);
            }
            catch (OperationCanceledException) { }
            catch { /* Das Spektrogramm ist optional; die Wellenform bleibt verfügbar. */ }
            finally
            {
                lock (spectrogramGate)
                {
                    if (generation == spectrogramGeneration) spectrogramBuilding = false;
                    if (ReferenceEquals(spectrogramCancellation, cancellation)) spectrogramCancellation = null;
                }
                cancellation.Dispose();
            }
        });
    }

    private SegmentedAudioSource CreateSegmentedSource()
    {
        if (streamingWavPath is null || streamingSegments is null) throw new InvalidOperationException("Das Dokument wird nicht per Disk-Streaming bereitgestellt.");
        return new SegmentedAudioSource(new WavFileAudioSource(streamingWavPath), streamingSegments.ToArray());
    }

    internal IAudioSource OpenBlockSource() => IsStreaming
        ? CreateSegmentedSource()
        : new MemoryAudioSource(samples, Channels, SampleRate);

    // Project saves retain immutable memory/segment state before their first await.
    internal IAudioSource CaptureProjectSource() => IsStreaming
        ? CreateSegmentedSource()
        : new MemoryAudioSource(samples.ToArray(), Channels, SampleRate);

    private void SaveStreamingUndo()
    {
        streamingUndoHistory.Push(streamingSegments!.ToList());
        streamingRedoHistory.Clear();
        ClearSnapshots(redoHistory);
    }

    private void ApplyStreamingEdit(List<AudioSourceSegment> segments)
    {
        SaveStreamingUndo();
        streamingSegments = segments.Where(segment => segment.FrameCount > 0).ToList();
        streamingFrameCount = checked((int)streamingSegments.Sum(segment => segment.FrameCount));
        IsModified = true;
        RefreshStreamingVisuals();
    }

    private void RefreshStreamingVisuals()
    {
        InvalidatePeakCache();
        InvalidateSpectrogram();
        using (var source = CreateSegmentedSource()) waveformSamples = BuildWaveformPreview(source);
        PeakCacheReady?.Invoke(this, EventArgs.Empty);
        StartPeakCacheBuild();
    }

    private List<AudioSourceSegment> SliceStreamingTimeline(long startFrame, long frameCount)
    {
        var result = new List<AudioSourceSegment>();
        var endFrame = startFrame + frameCount;
        var timeline = 0L;
        foreach (var segment in streamingSegments!)
        {
            var segmentEnd = timeline + segment.FrameCount;
            var overlapStart = Math.Max(startFrame, timeline);
            var overlapEnd = Math.Min(endFrame, segmentEnd);
            if (overlapEnd > overlapStart) result.Add(segment.Slice(overlapStart - timeline, overlapEnd - overlapStart));
            timeline = segmentEnd;
        }
        return result;
    }

    public void Dispose()
    {
        lock (peakCacheGate)
        {
            ++peakCacheGeneration;
            peakCacheCancellation?.Cancel();
            peakCacheCancellation?.Dispose();
            peakCacheCancellation = null;
        }
        lock (spectrogramGate)
        {
            ++spectrogramGeneration;
            spectrogramBuilding = false;
            spectrogramCancellation?.Cancel();
            spectrogramCancellation?.Dispose();
        }
        ClearSnapshots(undoHistory);
        ClearSnapshots(redoHistory);
    }

    public AudioSelectionData Extract(Selection selection)
    {
        var length = selection.Length * Channels;
        var extracted = new float[length];
        Array.Copy(Samples, selection.StartSample * Channels, extracted, 0, length);
        return new AudioSelectionData(extracted, Channels, SampleRate);
    }

    /// <summary>Ersetzt einen Sample-Bereich durch vorab gerendertes Audio (z. B. VST3-Effekt).</summary>
    public void ReplaceFrames(int startFrame, float[] processed, int frames)
    {
        if (frames <= 0) return;
        if (startFrame < 0 || startFrame + frames > FrameCount) throw new ArgumentOutOfRangeException(nameof(startFrame));
        if (processed.Length < frames * Channels) throw new ArgumentException("Zu wenige Samples für den Ersetzungsbereich.", nameof(processed));
        SaveUndo();
        Array.Copy(processed, 0, Samples, startFrame * Channels, frames * Channels);
    }

    public void Delete(Selection selection)
    {
        if (IsStreaming)
        {
            if (selection.Length <= 0) return;
            var before = SliceStreamingTimeline(0, selection.StartSample);
            before.AddRange(SliceStreamingTimeline(selection.EndSample, FrameCount - selection.EndSample));
            ApplyStreamingEdit(before);
            return;
        }
        SaveUndo();
        var start = selection.StartSample * Channels;
        var count = selection.Length * Channels;
        var edited = new float[Samples.Length - count];
        Array.Copy(Samples, 0, edited, 0, start);
        Array.Copy(Samples, start + count, edited, start, Samples.Length - start - count);
        Samples = edited;
    }

    public void Keep(Selection selection)
    {
        if (IsStreaming)
        {
            if (selection.Length <= 0) return;
            ApplyStreamingEdit(SliceStreamingTimeline(selection.StartSample, selection.Length));
            return;
        }
        SaveUndo();
        var edited = new float[selection.Length * Channels];
        Array.Copy(Samples, selection.StartSample * Channels, edited, 0, edited.Length);
        Samples = edited;
    }

    public void Insert(int frame, AudioSelectionData audio)
    {
        // Fremde Formate werden kontrolliert angepasst statt abgelehnt.
        var compatible = AudioFormatConverter.Convert(audio, Channels, SampleRate);
        SaveUndo();
        var insertionPoint = Math.Clamp(frame, 0, FrameCount) * Channels;
        var edited = new float[Samples.Length + compatible.Samples.Length];
        Array.Copy(Samples, 0, edited, 0, insertionPoint);
        Array.Copy(compatible.Samples, 0, edited, insertionPoint, compatible.Samples.Length);
        Array.Copy(Samples, insertionPoint, edited, insertionPoint + compatible.Samples.Length, Samples.Length - insertionPoint);
        Samples = edited;
    }

    public void Undo()
    {
        if (IsStreaming)
        {
            if (!streamingUndoHistory.TryPop(out var streamedRestored))
            {
                if (undoHistory.Count > 0) { EnsureMaterialized(); Undo(); }
                return;
            }
            streamingRedoHistory.Push(streamingSegments!.ToList());
            streamingSegments = streamedRestored;
            streamingFrameCount = checked((int)streamedRestored.Sum(segment => segment.FrameCount));
            RefreshStreamingVisuals();
            return;
        }
        if (!undoHistory.TryPop(out var restored)) return;
        AudioSnapshot? current = null;
        try
        {
            current = CreateSnapshot(Samples);
            var restoredSamples = LoadSnapshot(restored);
            redoHistory.Push(current);
            Samples = restoredSamples;
            current = null;
        }
        catch
        {
            if (current is not null) TryDeleteSnapshot(current);
            undoHistory.Push(restored);
            throw;
        }
    }

    public void Redo()
    {
        if (IsStreaming)
        {
            if (!streamingRedoHistory.TryPop(out var streamedRestored))
            {
                if (redoHistory.Count > 0) { EnsureMaterialized(); Redo(); }
                return;
            }
            streamingUndoHistory.Push(streamingSegments!.ToList());
            streamingSegments = streamedRestored;
            streamingFrameCount = checked((int)streamedRestored.Sum(segment => segment.FrameCount));
            RefreshStreamingVisuals();
            return;
        }
        if (!redoHistory.TryPop(out var restored)) return;
        AudioSnapshot? current = null;
        try
        {
            current = CreateSnapshot(Samples);
            var restoredSamples = LoadSnapshot(restored);
            undoHistory.Push(current);
            Samples = restoredSamples;
            current = null;
        }
        catch
        {
            if (current is not null) TryDeleteSnapshot(current);
            redoHistory.Push(restored);
            throw;
        }
    }

    public void Normalize(Selection? selection)
    {
        var range = GetRange(selection);
        var peak = 0f;
        for (var index = range.Start; index < range.End; index++) peak = Math.Max(peak, Math.Abs(Samples[index]));
        if (peak < 0.00001f) return;
        SaveUndo();
        var gain = 0.98f / peak;
        for (var index = range.Start; index < range.End; index++) Samples[index] *= gain;
    }

    public void FadeIn(Selection? selection) => ApplyFade(selection, false);
    public void FadeOut(Selection? selection) => ApplyFade(selection, true);

    /// <summary>Wendet einen nativen Effekt auf die Auswahl oder den gesamten Clip an.</summary>
    public void ApplyNativeEffect(Selection? selection, NativeEffectKind kind, EffectSettings settings)
    {
        var (start, end) = GetRange(selection);
        if (start >= end) return;
        var block = new float[end - start];
        Array.Copy(Samples, start, block, 0, block.Length);
        var processed = NativeEffects.Process(settings, block, Channels, SampleRate, kind);
        SaveUndo();
        Array.Copy(processed, 0, Samples, start, processed.Length);
    }

    private void ApplyFade(Selection? selection, bool fadeOut)
    {
        var frames = selection ?? new Selection(0, FrameCount);
        if (frames.Length == 0) return;
        if (IsStreaming)
        {
            var edited = new List<AudioSourceSegment>();
            var timeline = 0L;
            foreach (var segment in streamingSegments!)
            {
                var segmentEnd = timeline + segment.FrameCount;
                var overlapStart = Math.Clamp(frames.StartSample, timeline, segmentEnd);
                var overlapEnd = Math.Clamp(frames.EndSample, timeline, segmentEnd);
                if (overlapStart > timeline) edited.Add(segment.Slice(0, overlapStart - timeline));
                if (overlapEnd > overlapStart)
                {
                    var slice = segment.Slice(overlapStart - timeline, overlapEnd - overlapStart);
                    var startProgress = (overlapStart - frames.StartSample) / (float)Math.Max(1, frames.Length - 1);
                    var endProgress = (overlapEnd - frames.StartSample - 1) / (float)Math.Max(1, frames.Length - 1);
                    var fadeStart = fadeOut ? 1f - startProgress : startProgress;
                    var fadeEnd = fadeOut ? 1f - endProgress : endProgress;
                    edited.Add(slice with { GainAtStart = slice.GainAtStart * fadeStart, GainAtEnd = slice.GainAtEnd * fadeEnd });
                }
                if (overlapEnd < segmentEnd) edited.Add(segment.Slice(overlapEnd - timeline, segmentEnd - overlapEnd));
                timeline = segmentEnd;
            }
            ApplyStreamingEdit(edited);
            return;
        }
        SaveUndo();
        for (var frame = frames.StartSample; frame < frames.EndSample; frame++)
        {
            var progress = (frame - frames.StartSample) / (float)Math.Max(1, frames.Length - 1);
            var gain = fadeOut ? 1f - progress : progress;
            for (var channel = 0; channel < Channels; channel++) Samples[frame * Channels + channel] *= gain;
        }
    }

    private (int Start, int End) GetRange(Selection? selection)
    {
        var frames = selection ?? new Selection(0, FrameCount);
        return (frames.StartSample * Channels, frames.EndSample * Channels);
    }

    public void SaveAsWave(string path) => SaveAsWave(path, 32);

    /// <summary>Saves a WAV in its original bit depth when that depth is supported.</summary>
    public void SaveInSourceWaveFormat(string path)
    {
        var bits = OriginalWaveBitsPerSample is 16 or 24 or 32 or 64 ? OriginalWaveBitsPerSample.Value : 32;
        var isFloat = !OriginalWaveBitsPerSample.HasValue || OriginalWaveIsIeeeFloat;
        SaveWave(path, bits, isFloat);
        UpdateSourcePath(path);
    }

    public void SaveAsWave(string path, int bitsPerSample)
        => SaveWave(path, bitsPerSample, bitsPerSample == 32);

    private void SaveWave(string path, int bitsPerSample, bool isIeeeFloat)
    {
        if (IsStreaming && string.Equals(Path.GetFullPath(path), Path.GetFullPath(streamingWavPath!), StringComparison.OrdinalIgnoreCase))
            ConvertStreamingHistoryToSnapshots();
        if (IsStreaming)
        {
            SaveStreamingWave(path, 0, FrameCount, bitsPerSample, isIeeeFloat);
            return;
        }
        var tempPath = path + ".tmp";
        try
        {
            using (var writer = new WaveFileWriter(tempPath, CreateWaveFormat(bitsPerSample, isIeeeFloat)))
            {
                WriteSamples(writer, Samples, 0, Samples.Length, bitsPerSample, isIeeeFloat);
            }
            CommitTempFile(tempPath, path);
        }
        catch
        {
            DeleteTempFile(tempPath);
            throw;
        }
    }

    /// <summary>Exportiert exakt den angegebenen Frame-Bereich, ohne das Dokument zu verändern.</summary>
    public void SaveRangeAsWave(string path, int startFrame, int frameCount, int bitsPerSample)
    {
        ValidateRange(startFrame, frameCount);
        if (!string.IsNullOrWhiteSpace(SourcePath) && string.Equals(Path.GetFullPath(path), Path.GetFullPath(SourcePath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Die Auswahl kann nicht über die Quelldatei gespeichert werden. Wählen Sie einen anderen Dateinamen.");
        if (IsStreaming)
        {
            SaveStreamingWave(path, startFrame, frameCount, bitsPerSample);
            return;
        }
        var offset = startFrame * Channels;
        var count = frameCount * Channels;
        var tempPath = path + ".tmp";
        try
        {
            using (var writer = new WaveFileWriter(tempPath, CreateWaveFormat(bitsPerSample)))
            {
                WriteSamples(writer, Samples, offset, count, bitsPerSample, bitsPerSample == 32);
            }
            CommitTempFile(tempPath, path);
        }
        catch
        {
            DeleteTempFile(tempPath);
            throw;
        }
    }

    /// <summary>Sicherer MP3-Export über eine temporäre Datei (CBR, 32–320 kBit/s).</summary>
    public void SaveAsMp3(string path, int bitRate = 192)
    {
        ValidateBitRate(bitRate);
        if (IsStreaming)
        {
            SaveStreamingMp3(path, 0, FrameCount, bitRate);
            return;
        }
        var tempPath = path + ".tmp";
        try
        {
            using (var writer = new LameMP3FileWriter(tempPath, new WaveFormat(SampleRate, 16, Channels), bitRate))
            {
                WritePcm16(writer, Samples, 0, Samples.Length);
            }
            CommitTempFile(tempPath, path);
        }
        catch
        {
            DeleteTempFile(tempPath);
            throw;
        }
    }

    /// <summary>Exportiert nur den markierten Bereich als MP3.</summary>
    public void SaveRangeAsMp3(string path, int startFrame, int frameCount, int bitRate = 192)
    {
        ValidateRange(startFrame, frameCount);
        ValidateBitRate(bitRate);
        if (IsStreaming)
        {
            SaveStreamingMp3(path, startFrame, frameCount, bitRate);
            return;
        }
        var offset = startFrame * Channels;
        var count = frameCount * Channels;
        var tempPath = path + ".tmp";
        try
        {
            using (var writer = new LameMP3FileWriter(tempPath, new WaveFormat(SampleRate, 16, Channels), bitRate))
            {
                WritePcm16(writer, Samples, offset, count);
            }
            CommitTempFile(tempPath, path);
        }
        catch
        {
            DeleteTempFile(tempPath);
            throw;
        }
    }

    public void SaveAsFlac(string path, double compressionLevel = .5) =>
        SaveAsSoundFile(path, 0, FrameCount, SoundFileMajorFormat.Flac, new SoundFileWriterOptions { CompressionLevel = compressionLevel });

    public void SaveRangeAsFlac(string path, int startFrame, int frameCount, double compressionLevel = .5) =>
        SaveAsSoundFile(path, startFrame, frameCount, SoundFileMajorFormat.Flac, new SoundFileWriterOptions { CompressionLevel = compressionLevel });

    public void SaveAsOgg(string path, double quality = .6) =>
        SaveAsSoundFile(path, 0, FrameCount, SoundFileMajorFormat.OggVorbis, new SoundFileWriterOptions { VbrQuality = quality });

    public void SaveRangeAsOgg(string path, int startFrame, int frameCount, double quality = .6) =>
        SaveAsSoundFile(path, startFrame, frameCount, SoundFileMajorFormat.OggVorbis, new SoundFileWriterOptions { VbrQuality = quality });

    public void SaveAsOpus(string path, double quality = .6) =>
        SaveAsSoundFile(path, 0, FrameCount, SoundFileMajorFormat.Opus, new SoundFileWriterOptions { VbrQuality = quality });

    public void SaveRangeAsOpus(string path, int startFrame, int frameCount, double quality = .6) =>
        SaveAsSoundFile(path, startFrame, frameCount, SoundFileMajorFormat.Opus, new SoundFileWriterOptions { VbrQuality = quality });

    private WaveFormat CreateWaveFormat(int bitsPerSample) => CreateWaveFormat(bitsPerSample, bitsPerSample == 32);

    private WaveFormat CreateWaveFormat(int bitsPerSample, bool isIeeeFloat) => (bitsPerSample, isIeeeFloat) switch
    {
        (32, true) => WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels),
        (64, true) => WaveFormat.CreateCustomFormat(WaveFormatEncoding.IeeeFloat, SampleRate, Channels, SampleRate * Channels * 8, Channels * 8, 64),
        (16, false) => new WaveFormat(SampleRate, 16, Channels),
        (24, false) => new WaveFormat(SampleRate, 24, Channels),
        (32, false) => new WaveFormat(SampleRate, 32, Channels),
        _ => throw new ArgumentOutOfRangeException(nameof(bitsPerSample), bitsPerSample, "Unterstützt werden 16/24/32-Bit-PCM und 32/64-Bit-Float.")
    };

    private void SaveAsSoundFile(string path, int startFrame, int frameCount, SoundFileMajorFormat format, SoundFileWriterOptions options)
    {
        ValidateRange(startFrame, frameCount);
        var tempPath = path + ".tmp";
        try
        {
            using (var writer = new SoundFileWriter(tempPath, WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels), format, options))
            {
                if (IsStreaming)
                    RenderStreaming(startFrame, frameCount, (buffer, sampleCount) => writer.WriteSamples(buffer.AsSpan(0, sampleCount)));
                else
                    writer.WriteSamples(Samples.AsSpan(startFrame * Channels, frameCount * Channels));
            }
            CommitTempFile(tempPath, path);
        }
        catch
        {
            DeleteTempFile(tempPath);
            throw;
        }
    }

    private void SaveStreamingWave(string path, int startFrame, int frameCount, int bitsPerSample, bool? isIeeeFloat = null)
    {
        var floatingPoint = isIeeeFloat ?? bitsPerSample == 32;
        var tempPath = path + ".tmp";
        try
        {
            using (var writer = new WaveFileWriter(tempPath, CreateWaveFormat(bitsPerSample, floatingPoint)))
            {
                RenderStreaming(startFrame, frameCount, (buffer, sampleCount) => WriteSamples(writer, buffer, 0, sampleCount, bitsPerSample, floatingPoint));
            }
            CommitTempFile(tempPath, path);
        }
        catch
        {
            DeleteTempFile(tempPath);
            throw;
        }
    }

    private void SaveStreamingMp3(string path, int startFrame, int frameCount, int bitRate)
    {
        var tempPath = path + ".tmp";
        try
        {
            using (var writer = new LameMP3FileWriter(tempPath, new WaveFormat(SampleRate, 16, Channels), bitRate))
            {
                RenderStreaming(startFrame, frameCount, (buffer, sampleCount) => WritePcm16(writer, buffer, 0, sampleCount));
            }
            CommitTempFile(tempPath, path);
        }
        catch
        {
            DeleteTempFile(tempPath);
            throw;
        }
    }

    private void RenderStreaming(int startFrame, int frameCount, Action<float[], int> writeBlock)
    {
        using var source = CreateSegmentedSource();
        const int blockFrames = 4096;
        var buffer = new float[blockFrames * Channels];
        var position = startFrame;
        var remaining = frameCount;
        while (remaining > 0)
        {
            var requested = Math.Min(blockFrames, remaining);
            var read = source.ReadFrames(position, buffer, 0, requested);
            if (read != requested) throw new EndOfStreamException("Die gestreamte Quelle endete beim Export vorzeitig.");
            writeBlock(buffer, read * Channels);
            position += read;
            remaining -= read;
        }
    }

    private void ValidateRange(int startFrame, int frameCount)
    {
        if (startFrame < 0) throw new ArgumentOutOfRangeException(nameof(startFrame));
        if (frameCount < 0 || startFrame > FrameCount || frameCount > FrameCount - startFrame) throw new ArgumentOutOfRangeException(nameof(frameCount));
    }

    private static void ValidateBitRate(int bitRate)
    {
        if (bitRate < 32 || bitRate > 320) throw new ArgumentOutOfRangeException(nameof(bitRate), bitRate, "Erlaubt sind 32 bis 320 kBit/s.");
    }

    private static void CommitTempFile(string tempPath, string path)
    {
        if (File.Exists(path)) File.Replace(tempPath, path, null);
        else File.Move(tempPath, path);
    }

    private static void DeleteTempFile(string tempPath)
    {
        if (File.Exists(tempPath)) File.Delete(tempPath);
    }

    private static void WriteSamples(WaveFileWriter writer, float[] samples, int offset, int count, int bitsPerSample, bool isIeeeFloat)
    {
        switch (bitsPerSample, isIeeeFloat)
        {
            case (32, true): writer.WriteSamples(samples, offset, count); break;
            case (64, true): WriteFloat64(writer, samples, offset, count); break;
            case (32, false): WritePcm32(writer, samples, offset, count); break;
            case (24, false): WritePcm24(writer, samples, offset, count); break;
            case (16, false): WritePcm16(writer, samples, offset, count); break;
        }
    }

    private static void WritePcm32(Stream writer, float[] samples, int offset, int count)
    {
        var data = new byte[checked(count * sizeof(int))];
        for (var index = 0; index < count; index++)
        {
            var sample = Math.Clamp((double)samples[offset + index], -1d, 1d);
            var value = (int)Math.Clamp(Math.Round(sample * 2147483647d), int.MinValue, int.MaxValue);
            BitConverter.TryWriteBytes(data.AsSpan(index * sizeof(int), sizeof(int)), value);
        }
        writer.Write(data);
    }

    private static void WriteFloat64(Stream writer, float[] samples, int offset, int count)
    {
        var data = new byte[checked(count * sizeof(double))];
        for (var index = 0; index < count; index++)
            BitConverter.TryWriteBytes(data.AsSpan(index * sizeof(double), sizeof(double)), (double)samples[offset + index]);
        writer.Write(data);
    }

    private static void WritePcm16(Stream writer, float[] samples) => WritePcm16(writer, samples, 0, samples.Length);

    private static void WritePcm16(Stream writer, float[] samples, int offset, int count)
    {
        var data = new byte[count * 2];
        for (var index = 0; index < count; index++)
        {
            var value = (short)Math.Clamp(MathF.Round(samples[offset + index] * 32767f), -32768f, 32767f);
            data[index * 2] = (byte)value;
            data[index * 2 + 1] = (byte)(value >> 8);
        }
        writer.Write(data);
    }

    private static void WritePcm24(Stream writer, float[] samples) => WritePcm24(writer, samples, 0, samples.Length);

    private static void WritePcm24(Stream writer, float[] samples, int offset, int count)
    {
        var data = new byte[count * 3];
        for (var index = 0; index < count; index++)
        {
            var value = (int)Math.Clamp(MathF.Round(samples[offset + index] * 8388607f), -8388608f, 8388607f);
            data[index * 3] = (byte)value;
            data[index * 3 + 1] = (byte)(value >> 8);
            data[index * 3 + 2] = (byte)(value >> 16);
        }
        writer.Write(data);
    }

    /// <summary>Markiert das Dokument als geändert (z. B. neue, ungespeicherte Aufnahme oder Ausschnitt).</summary>
    public void MarkModified() => IsModified = true;

    /// <summary>Setzt nach „Speichern unter“ den neuen Quelldateipfad und markiert das Dokument als gespeichert.</summary>
    public void UpdateSourcePath(string path)
    {
        if (IsStreaming && Path.GetExtension(path).Equals(".wav", StringComparison.OrdinalIgnoreCase))
        {
            ConvertStreamingHistoryToSnapshots();
            // Nach „Speichern unter“ ist die WAV bereits als aktueller Schnittstand
            // gerendert. Die Segmentliste kann deshalb wieder auf diese neue Datei
            // zeigen, statt weiter von der alten Quelldatei abzuhängen.
            streamingWavPath = path;
            streamingSegments = streamingFrameCount == 0
                ? []
                : [new AudioSourceSegment(0, streamingFrameCount)];
            RefreshStreamingVisuals();
        }
        SourcePath = path;
        DisplayName = Path.GetFileName(path);
        if (Path.GetExtension(path).Equals(".wav", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
        {
            using var savedWave = new WavFileAudioSource(path);
            OriginalWaveBitsPerSample = savedWave.BitsPerSample;
            OriginalWaveIsIeeeFloat = savedWave.IsIeeeFloat;
        }
        IsModified = false;
    }

    /// <summary>Markiert das Dokument nach einem erfolgreichen Speichervorgang als sauber.</summary>
    public void MarkSaved() => IsModified = false;

    private void SaveUndo()
    {
        var snapshot = CreateSnapshot(Samples);
        undoHistory.Push(snapshot);
        ClearSnapshots(redoHistory);
        IsModified = true;
        InvalidateSpectrogram();
    }

    private static void CleanupOrphanedSnapshots()
    {
        if (Interlocked.Exchange(ref orphanUndoCleanupDone, 1) != 0) return;
        try
        {
            foreach (var path in Directory.EnumerateFiles(Path.GetTempPath(), "WaveBenchPro_Undo_*.bin"))
            {
                var parts = Path.GetFileNameWithoutExtension(path).Split('_');
                if (parts.Length >= 4 && int.TryParse(parts[2], out var processId))
                {
                    try { using var process = Process.GetProcessById(processId); }
                    catch (ArgumentException) { TryDeleteSnapshot(new AudioSnapshot(path)); }
                    catch (InvalidOperationException) { TryDeleteSnapshot(new AudioSnapshot(path)); }
                    catch { }
                }
                else
                {
                    try
                    {
                        if (File.GetLastWriteTimeUtc(path) < DateTime.UtcNow.AddDays(-30))
                            TryDeleteSnapshot(new AudioSnapshot(path));
                    }
                    catch { }
                }
            }
        }
        catch { /* Cleanup must never block an edit when temp storage is unavailable. */ }
    }

    private AudioSnapshot CreateSnapshot(float[] audio) => new(snapshotWriter(audio));

    private static string WriteSnapshot(float[] audio)
    {
        CleanupOrphanedSnapshots();
        var path = Path.Combine(Path.GetTempPath(), $"WaveBenchPro_Undo_{Environment.ProcessId}_{Guid.NewGuid():N}.bin");
        try
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.SequentialScan);
            stream.Write(MemoryMarshal.AsBytes(audio.AsSpan()));
            return path;
        }
        catch
        {
            try { File.Delete(path); } catch { }
            throw;
        }
    }

    private static float[] LoadSnapshot(AudioSnapshot snapshot)
    {
        using var stream = new FileStream(snapshot.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
        if (stream.Length % sizeof(float) != 0 || stream.Length / sizeof(float) > int.MaxValue)
            throw new InvalidDataException("Der Undo-Zwischenspeicher ist beschädigt oder größer als ein .NET-Samplepuffer.");
        var restored = new float[(int)(stream.Length / sizeof(float))];
        stream.ReadExactly(MemoryMarshal.AsBytes(restored.AsSpan()));
        stream.Dispose();
        File.Delete(snapshot.Path);
        return restored;
    }

    private static void ClearSnapshots(Stack<AudioSnapshot> snapshots)
    {
        while (snapshots.TryPop(out var snapshot))
            TryDeleteSnapshot(snapshot);
    }

    private static void TryDeleteSnapshot(AudioSnapshot snapshot)
    {
        try { File.Delete(snapshot.Path); } catch { /* temporäre Undo-Datei später bereinigen */ }
    }

    private void InvalidateSpectrogram()
    {
        lock (spectrogramGate)
        {
            ++spectrogramGeneration;
            spectrogramBuilding = false;
            spectrogramCancellation?.Cancel();
            Spectrogram = null;
        }
    }

    private void InvalidatePeakCache()
    {
        lock (peakCacheGate)
        {
            ++peakCacheGeneration;
            peakCacheCancellation?.Cancel();
            PeakCache = null;
        }
    }
}

public readonly record struct Selection(int StartSample, int EndSample)
{
    public int Length => EndSample - StartSample;
}

public readonly record struct AudioSelectionData(float[] Samples, int Channels, int SampleRate);

public static class AudioClipboard
{
    public static AudioSelectionData? Current { get; private set; }
    public static void Set(AudioSelectionData audio) => Current = audio;
}

internal sealed class ArraySampleProvider : ISampleProvider
{
    private readonly float[] data;
    private int position;

    public ArraySampleProvider(float[] data, WaveFormat waveFormat)
    {
        this.data = data;
        WaveFormat = waveFormat;
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        var available = Math.Min(count, data.Length - position);
        Array.Copy(data, position, buffer, offset, available);
        position += available;
        return available;
    }

    public int Read(Span<float> buffer)
    {
        var available = Math.Min(buffer.Length, data.Length - position);
        data.AsSpan(position, available).CopyTo(buffer);
        position += available;
        return available;
    }
}

internal sealed class ArrayWaveProvider : IRewindableWaveProvider
{
    private readonly float[] data;
    private readonly int startBytePosition;
    private readonly int endBytePosition;
    private int bytePosition;

    public ArrayWaveProvider(float[] data, int sampleOffset, int sampleCount, WaveFormat waveFormat)
    {
        this.data = data;
        startBytePosition = sampleOffset * sizeof(float);
        bytePosition = startBytePosition;
        endBytePosition = bytePosition + sampleCount * sizeof(float);
        WaveFormat = waveFormat;
    }

    public WaveFormat WaveFormat { get; }

    public int Read(byte[] buffer, int offset, int count)
    {
        var available = Math.Min(count, endBytePosition - bytePosition);
        Buffer.BlockCopy(data, bytePosition, buffer, offset, available);
        bytePosition += available;
        return available;
    }

    public int Read(Span<byte> buffer)
    {
        var available = Math.Min(buffer.Length, endBytePosition - bytePosition);
        MemoryMarshal.AsBytes(data.AsSpan()).Slice(bytePosition, available).CopyTo(buffer);
        bytePosition += available;
        return available;
    }

    public void Rewind() => bytePosition = startBytePosition;
}
