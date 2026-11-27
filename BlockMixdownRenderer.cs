using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using NAudio.Wave;

namespace WellenEditor;

/// <summary>Sequential offline mixer with per-export VST track preparation.</summary>
internal sealed class BlockMixdownRenderer : ICancellableBlockAudioRenderer
{
    private sealed record Clip(IAudioSource Source, long Start);
    private sealed record Track(ActiveClipIndex<Clip> Clips, TrackEffectProcessor Effects, float Left, float Right);
    private readonly List<IAudioSource> sources = new();
    private readonly List<Track> tracks = new();
    private readonly List<string> temporaryFiles = new();
    private VstCacheLease? cacheLease;
    private readonly float[] trackBuffer;
    private readonly float[] sourceBuffer;
    private readonly long processingEnd;
    private long position;
    public long FrameCount { get; }
    public int SampleRate { get; }
    public int BlockFrames { get; }

    public BlockMixdownRenderer(MultitrackProject project, int blockFrames = 4096, CancellationToken cancellationToken = default, BlockVstTrackRenderer.VstFactory? vstFactory = null, IProgress<double>? progress = null, string? cacheDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (blockFrames is < 1 or > 65536) throw new ArgumentOutOfRangeException(nameof(blockFrames));
        BlockFrames = blockFrames;
        SampleRate = project.SampleRate;
        trackBuffer = new float[checked(blockFrames * 2)];
        sourceBuffer = new float[checked(blockFrames * 2)];
        var solo = project.Tracks.Any(t => t.Solo && t.HasClip);
        var audible = project.Tracks.Where(t => t.HasClip && !t.Muted && (!solo || t.Solo)).ToArray();
        if (audible.Length == 0) throw new InvalidOperationException("Keine hörbare Spur vorhanden.");
        foreach (var clip in project.Tracks.SelectMany(track => track.Clips))
            if (clip.StartFrame < 0 || !double.IsFinite(clip.DurationScale) || clip.DurationScale <= 0)
                throw new NotSupportedException("Blockweiser Mixdown benötigt eine gültige Clipposition und Clipdauer.");
        FrameCount = audible.Max(t => t.EndFrame);
        var vstTracks = audible.Where(t => t.Effects.Any(f => f.Enabled && f.Vst is not null)).ToArray();
        var vstFrames = vstTracks.Aggregate(0L, (total, track) => checked(total + track.EndFrame));
        if (vstFrames > 16L * 1024 * 1024 * 1024 / 8)
            throw new NotSupportedException("Die VST-Zwischendateien überschreiten das Limit von 16 GiB.");
        if (vstTracks.Any(t => t.EndFrame > (uint.MaxValue - 1024L) / 8))
            throw new NotSupportedException("Eine VST-Spur überschreitet das RIFF-WAV-Limit.");
        long preparedVstFrames = 0;
        processingEnd = project.Tracks.Where(t => t.HasClip).Max(track =>
            track.Effects.Any(effect => effect.Enabled && effect.Vst is not null)
                ? track.EndFrame
                : track.Clips.Max(clip =>
                {
                    var converted = clip.Source.SampleRate == SampleRate ? clip.Source.FrameCount : checked((long)Math.Round((double)clip.Source.FrameCount * SampleRate / clip.Source.SampleRate));
                    var stretched = Math.Abs(clip.DurationScale - 1) < .00001 ? converted : checked((long)Math.Max(1, Math.Round(converted * clip.DurationScale)));
                    return checked(clip.StartFrame + stretched);
                }));
        try
        {
            foreach (var track in audible)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var effects = track.Effects.Select(effect => effect with { VstState = effect.VstState?.ToArray() }).ToArray();
                var clips = new List<Clip>();
                foreach (var clip in track.Clips)
                {
                    if (clip.StartFrame < 0 || !double.IsFinite(clip.DurationScale) || clip.DurationScale <= 0 || clip.Source.Channels is < 1 or > 2)
                        throw new NotSupportedException("Blockweiser Mixdown benötigt Mono/Stereo und eine gültige Clipdauer.");
                    var raw = clip.Source.OpenBlockSource();
                    IAudioSource source = raw;
                    try
                    {
                        if (source.SampleRate != SampleRate) source = new BlockResamplingSource(source, SampleRate);
                        if (Math.Abs(clip.DurationScale - 1) >= .00001)
                        {
                            if (source.Channels == 1) source = new StereoBlockSource(source);
                            source = new BlockStretchSource(source, clip.DurationScale);
                        }
                    }
                    catch { source.Dispose(); throw; }
                    sources.Add(source);
                    clips.Add(new Clip(source, clip.StartFrame));
                }
                if (effects.Any(f => f.Enabled && f.Vst is not null))
                {
                    if (cacheDirectory is null) cacheLease ??= VstCacheLease.Open();
                    var cachePath = Path.Combine(cacheDirectory ?? cacheLease!.DirectoryPath, $"WaveBenchVst_{Guid.NewGuid():N}.wav");
                    temporaryFiles.Add(cachePath);
                    var currentTrackFrames = track.EndFrame;
                    cacheLease?.Reserve(checked(currentTrackFrames * 8 + 1024));
                    BlockVstTrackRenderer.Render(cachePath, clips.Select(c => new BlockVstTrackRenderer.Clip(c.Source, c.Start)).ToArray(), effects, currentTrackFrames, SampleRate, cancellationToken, vstFactory,
                        value => progress?.Report(.5 * (preparedVstFrames + currentTrackFrames * value) / vstFrames));
                    preparedVstFrames += currentTrackFrames;
                    foreach (var clip in clips) { clip.Source.Dispose(); sources.Remove(clip.Source); }
                    clips.Clear();
                    var cached = new WavFileAudioSource(cachePath);
                    sources.Add(cached);
                    clips.Add(new Clip(cached, 0));
                    effects = Array.Empty<TrackEffectSlot>();
                }
                var gain = (float)track.Gain;
                var pan = (float)track.Pan;
                tracks.Add(new Track(new ActiveClipIndex<Clip>(clips, clip => clip.Start, clip => clip.Source.FrameCount), new TrackEffectProcessor(effects, SampleRate), gain * (pan > 0 ? 1 - pan : 1), gain * (pan < 0 ? 1 + pan : 1)));
            }
        }
        catch (Exception exception)
        {
            try { Dispose(); }
            catch (Exception cleanup) { exception.Data["MixdownCleanupError"] = $"Freigabe der Mehrspurquellen fehlgeschlagen: {cleanup.Message}"; }
            throw;
        }
    }

    public int Read(float[] output, int frames) => Read(output, frames, CancellationToken.None);

    public int Read(float[] output, int frames, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(output);
        if (frames < 0 || frames > BlockFrames || output.Length < frames * 2) throw new ArgumentOutOfRangeException(nameof(frames));
        var count = (int)Math.Min(frames, FrameCount - position);
        Array.Clear(output, 0, count * 2);
        var processCount = (int)Math.Min(count, Math.Max(0, processingEnd - position));
        foreach (var track in tracks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Array.Clear(trackBuffer, 0, processCount * 2);
            foreach (var clip in track.Clips.At(position, processCount))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var from = Math.Max(position, clip.Start);
                var to = Math.Min(position + processCount, checked(clip.Start + clip.Source.FrameCount));
                if (to <= from) continue;
                var readFrames = checked((int)(to - from));
                var read = clip.Source.ReadFrames(from - clip.Start, sourceBuffer, 0, readFrames);
                if (read != readFrames) throw new EndOfStreamException("Die Audioquelle endete vorzeitig beim Mixdown.");
                var offset = checked((int)(from - position) * 2);
                for (var i = 0; i < readFrames; i++)
                {
                    var input = i * clip.Source.Channels;
                    trackBuffer[offset + i * 2] += sourceBuffer[input];
                    trackBuffer[offset + i * 2 + 1] += sourceBuffer[input + (clip.Source.Channels == 1 ? 0 : 1)];
                }
            }
            track.Effects.Process(trackBuffer.AsSpan(0, processCount * 2));
            for (var i = 0; i < processCount * 2; i += 2)
            {
                output[i] += trackBuffer[i] * track.Left;
                output[i + 1] += trackBuffer[i + 1] * track.Right;
            }
        }
        position += count;
        return count;
    }

    public void Dispose()
    {
        List<Exception>? errors = null;
        foreach (var source in sources)
            try { source.Dispose(); } catch (Exception exception) { (errors ??= new()).Add(exception); }
        sources.Clear();
        foreach (var path in temporaryFiles) { try { File.Delete(path); } catch (Exception exception) { (errors ??= new()).Add(exception); } }
        temporaryFiles.Clear();
        try { cacheLease?.Dispose(); } catch (Exception exception) { (errors ??= new()).Add(exception); }
        cacheLease = null;
        if (errors is not null) throw new AggregateException("Mehrspur-Ressourcen konnten nicht vollständig freigegeben werden.", errors);
    }

    public static void WriteWave(MultitrackProject project, string path, CancellationToken cancellationToken, IProgress<double>? progress = null, Func<string, Stream>? openOutput = null, BlockVstTrackRenderer.VstFactory? vstFactory = null, string? cacheDirectory = null, Action<string>? deleteTemp = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var target = Path.GetFullPath(path);
        var sourcePaths = project.Tracks.SelectMany(t => t.Clips).Select(c => c.Source.SourcePath)
            .Where(p => !string.IsNullOrWhiteSpace(p)).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var targetExisted = File.Exists(target);
        if (sourcePaths.Any(source => IsSameFile(source, target)))
            throw new IOException("Der Mixdown darf keine geöffnete Quelldatei überschreiben.");
        var anySolo = project.Tracks.Any(t => t.Solo && t.HasClip);
        var audible = project.Tracks.Where(t => t.HasClip && !t.Muted && (!anySolo || t.Solo)).ToArray();
        if (audible.Length == 0) throw new InvalidOperationException("Keine hörbare Spur vorhanden.");
        // RIFF 32-bit size, including the header written by WaveFileWriter.
        if (audible.Max(t => t.EndFrame) > (uint.MaxValue - 1024L) / 8)
            throw new NotSupportedException("Der Mixdown überschreitet das RIFF-WAV-Limit; RF64 wird noch nicht unterstützt.");
        var hasVst = audible.Any(t => t.Effects.Any(f => f.Enabled && f.Vst is not null));
        var renderer = new BlockMixdownRenderer(project, cancellationToken: cancellationToken, vstFactory: vstFactory, progress: progress, cacheDirectory: cacheDirectory);
        var temp = Path.Combine(Path.GetDirectoryName(target)!, $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.tmp");
        Exception? primaryError = null;
        try
        {
            using (var stream = openOutput is null ? new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None) : openOutput(temp))
            using (var writer = new WaveFileWriter(stream, WaveFormat.CreateIeeeFloatWaveFormat(renderer.SampleRate, 2)))
            {
                var block = new float[renderer.BlockFrames * 2];
                var bytes = new byte[renderer.BlockFrames * 2 * sizeof(float)];
                long written = 0;
                var lastPercent = -1;
                while (written < renderer.FrameCount)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var count = renderer.Read(block, renderer.BlockFrames);
                    if (count <= 0) throw new EndOfStreamException("Mixdown endete vorzeitig.");
                    Buffer.BlockCopy(block, 0, bytes, 0, count * 2 * sizeof(float));
                    writer.Write(bytes, 0, count * 2 * sizeof(float));
                    written += count;
                    var percent = (int)(written * 100d / renderer.FrameCount);
                    if (percent != lastPercent) { lastPercent = percent; var fraction = written / (double)renderer.FrameCount; progress?.Report(hasVst ? .5 + fraction * .5 : fraction); }
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            PublishWave(temp, target, targetExisted, sourcePaths);
        }
        catch (Exception exception) { primaryError = exception; throw; }
        finally
        {
            List<Exception>? cleanupErrors = null;
            var details = new List<string>();
            try { renderer.Dispose(); }
            catch (Exception cleanupError)
            {
                (cleanupErrors ??= new()).Add(cleanupError);
                details.Add($"Freigabe der Mehrspurquellen fehlgeschlagen: {cleanupError.Message}");
            }
            try { if (File.Exists(temp)) (deleteTemp ?? File.Delete)(temp); }
            catch (Exception cleanupError)
            {
                (cleanupErrors ??= new()).Add(cleanupError);
                details.Add($"Temporäre Datei blieb erhalten: {temp}; {cleanupError.Message}");
            }
            if (cleanupErrors is not null)
            {
                if (primaryError is null) throw new IOException(string.Join(" ", details), new AggregateException(cleanupErrors));
                primaryError.Data["MixdownCleanupError"] = $"{primaryError.Data["MixdownCleanupError"]} {string.Join(" ", details)}".Trim();
            }
        }
    }

    private static void PublishWave(string temp, string target, bool targetExisted, string[] sources)
    {
        var protectedSources = new List<SafeFileHandle>();
        try
        {
            // Omit FILE_SHARE_DELETE until the same-volume rename has finished. A late
            // destination alias cannot replace an input while this handle protects the file.
            foreach (var source in sources)
            {
                try { protectedSources.Add(File.OpenHandle(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)); }
                catch (FileNotFoundException) { } // An in-memory document can outlive its original path.
                catch (DirectoryNotFoundException) { }
            }
            if (sources.Any(source => IsSameFile(source, target)))
                throw new IOException("Das Mixdown-Ziel verweist inzwischen auf eine geöffnete Quelldatei.");
            File.Move(temp, target, overwrite: targetExisted);
        }
        finally
        {
            foreach (var handle in protectedSources) handle.Dispose();
        }
    }

    private static bool IsSameFile(string source, string target)
    {
        if (string.IsNullOrWhiteSpace(source)) return false;
        var fullSource = Path.GetFullPath(source);
        if (string.Equals(fullSource, target, StringComparison.OrdinalIgnoreCase)) return true;
        if (!File.Exists(fullSource) || !File.Exists(target)) return false;
        using var sourceHandle = File.OpenHandle(fullSource, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var targetHandle = File.OpenHandle(target, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (!GetFileInformationByHandle(sourceHandle, out var a) || !GetFileInformationByHandle(targetHandle, out var b))
            throw new IOException("Die Dateizugehörigkeit des Mixdown-Ziels konnte nicht sicher geprüft werden.");
        return a.VolumeSerialNumber == b.VolumeSerialNumber && a.FileIndexHigh == b.FileIndexHigh && a.FileIndexLow == b.FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
}
