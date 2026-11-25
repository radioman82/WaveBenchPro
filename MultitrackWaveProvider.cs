using NAudio.Wave;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace WellenEditor;

/// <summary>Prepared clip audio; only summing and reading atomic mixer controls happen in Read.</summary>
internal sealed class MultitrackWaveProvider : IRewindableWaveProvider
{
    private const long MaxPreparedTrackBytes = 256L * 1024 * 1024;
    private const long MaxPreparedProjectBytes = 512L * 1024 * 1024;
    private sealed record Voice(MultitrackTrack Track, long Start, float[] Samples);
    private readonly MultitrackTrack[] tracks;
    private sealed record TrackRenderState(Voice[] Voices, TrackEffectProcessor Effects, bool OfflineVst);
    private readonly record struct TrackMemoryEstimate(long DryBytes, long EndFrame, long WithVstBytes);
    private sealed class TrackMix
    {
        public required MultitrackTrack Track;
        public required TrackRenderState State;
        public readonly float[] Buffer = new float[4096 * 2];
        public int PreparationVersion;
        public readonly object PreparationGate = new();
        public CancellationTokenSource? PreparationCancellation;
    }
    private readonly TrackMix[] trackMixes;
    private readonly ConcurrentQueue<string> preparationWarnings = new();
    private readonly TrackEffectChainRenderer.VstRenderer? vstRenderer;
    private readonly bool failOnVstPreparationError;
    public IReadOnlyList<string> PreparationWarnings => preparationWarnings.ToArray();
    public Task UpdateTrackEffects(MultitrackTrack track)
    {
        foreach (var mix in trackMixes)
        {
            if (!ReferenceEquals(mix.Track, track)) continue;
            lock (mix.PreparationGate)
            {
                var version = ++mix.PreparationVersion;
                mix.PreparationCancellation?.Cancel();
                mix.PreparationCancellation?.Dispose();
                mix.PreparationCancellation = null;
                var currentState = Volatile.Read(ref mix.State);
                var effects = track.Effects.Select(effect => effect with { VstState = effect.VstState?.ToArray() }).ToArray();
                if (!currentState.OfflineVst && !effects.Any(effect => effect.Enabled && effect.Vst is not null))
                {
                    Volatile.Write(ref mix.State, new TrackRenderState(currentState.Voices, new TrackEffectProcessor(effects, WaveFormat.SampleRate), false));
                    return Task.CompletedTask;
                }
                var cancellation = new CancellationTokenSource();
                mix.PreparationCancellation = cancellation;
                var clips = SnapshotClips(track);
                var localWarnings = new ConcurrentQueue<string>();
                var task = Task.Run(() => PrepareTrack(track, effects, clips, cancellation.Token, localWarnings.Enqueue), cancellation.Token);
                return PublishPreparedTrackAsync(mix, track.Name, version, cancellation, task, localWarnings);
            }
        }
        return Task.CompletedTask;
    }

    private async Task PublishPreparedTrackAsync(TrackMix mix, string trackName, int version, CancellationTokenSource cancellation, Task<TrackRenderState> task, ConcurrentQueue<string> localWarnings)
    {
        try
        {
            var state = await task.ConfigureAwait(false);
            lock (mix.PreparationGate)
                if (mix.PreparationVersion == version && !cancellation.IsCancellationRequested)
                {
                    Volatile.Write(ref mix.State, state);
                    while (localWarnings.TryDequeue(out var warning)) preparationWarnings.Enqueue(warning);
                }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            lock (mix.PreparationGate)
                if (mix.PreparationVersion == version)
                    preparationWarnings.Enqueue($"Spur '{trackName}': Effekte konnten nicht neu vorbereitet werden ({exception.GetBaseException().Message}); bisherige Wiedergabe bleibt aktiv.");
        }
        finally
        {
            lock (mix.PreparationGate)
                if (mix.PreparationVersion == version && ReferenceEquals(mix.PreparationCancellation, cancellation))
                {
                    mix.PreparationCancellation.Dispose();
                    mix.PreparationCancellation = null;
                }
        }
    }
    private long position;
    private long rangeStart;
    private long rangeEnd;
    private sealed record PlaybackWindow(long Start, long End, bool Loop);
    private PlaybackWindow? pendingWindow;
    private PlaybackWindow? activeWindow;
    private long totalFramesRead;
    public void ConfigurePlayback(long startFrame, long frameCount, bool loop)
    {
        if (startFrame < 0) throw new ArgumentOutOfRangeException(nameof(startFrame));
        if (frameCount <= 0) throw new ArgumentOutOfRangeException(nameof(frameCount));
        Volatile.Write(ref pendingWindow, new PlaybackWindow(startFrame, checked(startFrame + frameCount), loop));
    }
    public long AudibleFrame(int latencyFrames)
    {
        var window = Volatile.Read(ref activeWindow);
        if (window is null) return 0;
        var frame = Volatile.Read(ref position) - latencyFrames;
        if (Volatile.Read(ref totalFramesRead) <= latencyFrames) return window.Start;
        if (!window.Loop) return Math.Clamp(frame, window.Start, window.End);
        var length = window.End - window.Start;
        return window.Start + ((frame - window.Start) % length + length) % length;
    }
    public long FrameCount { get; }
    public WaveFormat WaveFormat { get; }

    public MultitrackWaveProvider(MultitrackProject project, TrackEffectChainRenderer.VstRenderer? vstRenderer = null, bool failOnVstPreparationError = false)
    {
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(project.SampleRate, 2);
        this.vstRenderer = vstRenderer;
        this.failOnVstPreparationError = failOnVstPreparationError;
        tracks = project.Tracks.ToArray();
        var estimatedProjectBytes = 0L;
        foreach (var track in tracks)
        {
            var effects = track.Effects;
            var clips = SnapshotClips(track);
            var estimate = EstimateTrackMemory(track, effects, clips, WaveFormat.SampleRate);
            if (estimate.DryBytes > MaxPreparedTrackBytes)
                throw new NotSupportedException($"Die vorbereiteten Clips in Spur '{track.Name}' überschreiten das Speicherlimit von 256 MiB.");
            var hasVst = effects.Any(effect => effect.Enabled && effect.Vst is not null);
            var vstFits = estimate.EndFrame <= Array.MaxLength / 2 && estimate.WithVstBytes <= MaxPreparedTrackBytes;
            if (hasVst && !vstFits && failOnVstPreparationError)
                throw new NotSupportedException($"Die VST3-Vorbereitung von Spur '{track.Name}' überschreitet das Speicherlimit von 256 MiB.");
            estimatedProjectBytes = checked(estimatedProjectBytes + (hasVst && vstFits ? estimate.WithVstBytes : estimate.DryBytes));
        }
        if (estimatedProjectBytes > MaxPreparedProjectBytes)
            throw new NotSupportedException("Die Mehrspurvorbereitung überschreitet das Projekt-Speicherlimit von 512 MiB.");
        trackMixes = tracks.Select(track => new TrackMix { Track = track, State = PrepareTrack(track, track.Effects, SnapshotClips(track), CancellationToken.None, preparationWarnings.Enqueue) }).ToArray();
        if (trackMixes.All(mix => mix.State.Voices.Length == 0)) throw new InvalidOperationException("Bitte zuerst ein Sample in die Mehrspuransicht ziehen.");
        FrameCount = trackMixes.SelectMany(mix => mix.State.Voices).Max(v => v.Start + v.Samples.Length / 2);
        rangeEnd = FrameCount;
        pendingWindow = activeWindow = new PlaybackWindow(0, FrameCount, false);
    }

    public void SetPlaybackRange(long startFrame, long frameCount)
    {
        if (startFrame < 0 || startFrame >= FrameCount) throw new ArgumentOutOfRangeException(nameof(startFrame));
        if (frameCount <= 0) throw new ArgumentOutOfRangeException(nameof(frameCount));
        rangeStart = startFrame;
        rangeEnd = startFrame + Math.Min(frameCount, FrameCount - startFrame);
        position = rangeStart;
        pendingWindow = activeWindow = new PlaybackWindow(rangeStart, rangeEnd, false);
    }

    private static MultitrackClip[] SnapshotClips(MultitrackTrack track) => track.Clips
        .Select(clip => new MultitrackClip(clip.Source, clip.StartFrame) { DurationScale = clip.DurationScale })
        .ToArray();

    private static TrackMemoryEstimate EstimateTrackMemory(MultitrackTrack track, IReadOnlyList<TrackEffectSlot> effects, IReadOnlyList<MultitrackClip> clips, int rate)
    {
        long dryBytes = 0;
        foreach (var clip in clips)
        {
            var source = clip.Source;
            var convertedFrames = checked((long)Math.Round((double)source.FrameCount * rate / source.SampleRate));
            var outputFrames = checked((long)Math.Max(1, Math.Round(convertedFrames * clip.DurationScale)));
            if (outputFrames <= 0 || outputFrames > Array.MaxLength / 2)
                throw new NotSupportedException($"Ein Clip in Spur '{track.Name}' ist zu lang für die Speicher-Vorbereitung.");
            var sourceBytes = checked(source.FrameCount * source.Channels * sizeof(float));
            if (source.IsStreaming) dryBytes = checked(dryBytes + sourceBytes);
            if (source.SampleRate != rate)
            {
                dryBytes = checked(dryBytes + convertedFrames * source.Channels * sizeof(float));
                if (rate < source.SampleRate) dryBytes = checked(dryBytes + sourceBytes);
            }
            var stereoBytes = checked(convertedFrames * sizeof(float) * 2);
            if (source.Channels != 2) dryBytes = checked(dryBytes + stereoBytes);
            if (Math.Abs(clip.DurationScale - 1) >= .00001)
                dryBytes = checked(dryBytes + stereoBytes + outputFrames * sizeof(float) * 2);
        }

        var endFrame = clips.Count == 0 ? 0 : clips.Max(clip => checked(clip.StartFrame + (long)Math.Ceiling(clip.Source.FrameCount * clip.DurationScale * rate / clip.Source.SampleRate)));
        var hasVst = effects.Any(effect => effect.Enabled && effect.Vst is not null);
        var withVstBytes = hasVst && endFrame <= Array.MaxLength / 2
            ? checked(dryBytes + endFrame * sizeof(float) * 2 * 3)
            : dryBytes;
        return new TrackMemoryEstimate(dryBytes, endFrame, withVstBytes);
    }

    private TrackRenderState PrepareTrack(MultitrackTrack track, IReadOnlyList<TrackEffectSlot> effects, IReadOnlyList<MultitrackClip> clips, CancellationToken cancellationToken, Action<string> reportWarning)
    {
        if (clips.Count == 0)
            return new TrackRenderState(Array.Empty<Voice>(), new TrackEffectProcessor(effects, WaveFormat.SampleRate), false);

        var estimate = EstimateTrackMemory(track, effects, clips, WaveFormat.SampleRate);
        var hasVst = effects.Any(effect => effect.Enabled && effect.Vst is not null);
        if (estimate.DryBytes > MaxPreparedTrackBytes)
            throw new NotSupportedException($"Die vorbereiteten Clips in Spur '{track.Name}' überschreiten das Speicherlimit von 256 MiB.");
        var endFrame = estimate.EndFrame;
        var skipVstForPlayback = hasVst && (endFrame > Array.MaxLength / 2 || estimate.WithVstBytes > MaxPreparedTrackBytes);
        if (skipVstForPlayback)
        {
            if (failOnVstPreparationError)
                throw new NotSupportedException($"Die VST3-Vorbereitung von Spur '{track.Name}' überschreitet das Speicherlimit von 256 MiB.");
        }

        var dryVoices = clips.Select(clip =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new Voice(track, clip.StartFrame, PrepareClip(clip, WaveFormat.SampleRate));
        }).ToArray();
        if (!hasVst || skipVstForPlayback)
        {
            if (skipVstForPlayback)
                reportWarning($"Spur '{track.Name}': Die Offline-VST3-Kette überschreitet das Speicherlimit von 256 MiB; Vorschau nutzt ersatzweise den nativen/dry Signalweg.");
            return new TrackRenderState(dryVoices, new TrackEffectProcessor(effects, WaveFormat.SampleRate), false);
        }

        var frameCount = endFrame;
        try
        {
            var stereo = new float[checked((int)frameCount * 2)];
            foreach (var voice in dryVoices)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var from = Math.Max(0L, voice.Start);
                var to = Math.Min(frameCount, voice.Start + voice.Samples.Length / 2);
                for (var frame = from; frame < to; frame++)
                {
                    var sourceSample = checked((int)((frame - voice.Start) * 2));
                    var targetSample = checked((int)(frame * 2));
                    stereo[targetSample] += voice.Samples[sourceSample];
                    stereo[targetSample + 1] += voice.Samples[sourceSample + 1];
                }
            }

            var processed = TrackEffectChainRenderer.Process(stereo, effects, WaveFormat.SampleRate,
                warning => reportWarning($"Spur '{track.Name}': {warning}"), vstRenderer, failOnVstPreparationError, cancellationToken);
            return new TrackRenderState(new[] { new Voice(track, 0, processed) }, new TrackEffectProcessor(Array.Empty<TrackEffectSlot>(), WaveFormat.SampleRate), true);
        }
        catch (Exception exception)
        {
            if (failOnVstPreparationError) throw;
            reportWarning($"Spur '{track.Name}': Die Offline-Effektkette ist fehlgeschlagen ({exception.Message}); VST3-Effekte wurden übersprungen.");
            return new TrackRenderState(dryVoices, new TrackEffectProcessor(effects.Where(effect => effect.Vst is null), WaveFormat.SampleRate), false);
        }
    }

    private static float[] PrepareClip(MultitrackClip clip, int rate)
    {
        var source = clip.Source;
        var resampled = AudioFormatConverter.Resample(source.Samples, source.Channels, source.SampleRate, rate);
        var stereo = AudioFormatConverter.ConvertChannels(resampled, source.Channels, 2);
        return AudioTimeStretch.Stretch(stereo, clip.DurationScale);
    }

    internal static float[] RenderTrackDry(MultitrackTrack track, int sampleRate)
    {
        var frames = track.EndFrame;
        if (frames > Array.MaxLength / 2)
            throw new NotSupportedException("Die Spur ist zu lang für eine Live-Vorschau im Speicher.");
        var result = new float[checked((int)frames * 2)];
        foreach (var clip in track.Clips)
        {
            var samples = PrepareClip(clip, sampleRate);
            var start = checked((int)clip.StartFrame * 2);
            var count = Math.Min(samples.Length, result.Length - start);
            for (var index = 0; index < count; index++) result[start + index] += samples[index];
        }
        return result;
    }

    public int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public int Read(Span<byte> buffer)
    {
        var window = Volatile.Read(ref pendingWindow)!;
        if (!ReferenceEquals(window, activeWindow))
        {
            if (position < window.Start || position >= window.End) position = window.Start;
            Volatile.Write(ref activeWindow, window);
        }
        var written = 0;
        var requested = buffer.Length / 8;
        while (written < requested)
        {
            if (position >= window.End)
            {
                if (!window.Loop) break;
                position = window.Start;
            }
            var frames = (int)Math.Min(4096, Math.Min(requested - written, window.End - position));
            RenderFrames(buffer.Slice(written * 8, frames * 8), frames);
            written += frames;
            position += frames;
            totalFramesRead += frames;
        }
        return written * 8;
    }
    private void RenderFrames(Span<byte> buffer, int frames)
    {
        var output = MemoryMarshal.Cast<byte, float>(buffer[..(frames * 8)]);
        output.Clear();
        var anySolo = false;
        foreach (var mix in trackMixes) anySolo |= mix.Track.Solo && Volatile.Read(ref mix.State).Voices.Length > 0;
        foreach (var mix in trackMixes)
        {
            var track = mix.Track;
            var state = Volatile.Read(ref mix.State);
            if (state.Voices.Length == 0) continue;
            if (track.Muted || (anySolo && !track.Solo)) continue;
            var trackBuffer = mix.Buffer.AsSpan(0, frames * 2);
            trackBuffer.Clear();
            var gain = (float)track.Gain;
            var pan = (float)track.Pan;
            var left = gain * (pan > 0 ? 1 - pan : 1);
            var right = gain * (pan < 0 ? 1 + pan : 1);
            foreach (var voice in state.Voices)
            {
            var from = Math.Max(position, voice.Start);
            var to = Math.Min(position + frames, voice.Start + voice.Samples.Length / 2);
            for (var frame = from; frame < to; frame++)
            {
                var input = (int)((frame - voice.Start) * 2);
                var target = (int)((frame - position) * 2);
                trackBuffer[target] += voice.Samples[input];
                trackBuffer[target + 1] += voice.Samples[input + 1];
            }
            }
            state.Effects.Process(trackBuffer);
            for (var i = 0; i < trackBuffer.Length; i += 2)
            {
                output[i] += trackBuffer[i] * left;
                output[i + 1] += trackBuffer[i + 1] * right;
            }
        }
        // Keep internal mix headroom. Hard clipping here would permanently distort
        // both playback and float mixdowns when several loud tracks are summed.
    }
    public void Rewind() => position = Volatile.Read(ref pendingWindow)!.Start;
}

/// <summary>Waveform-similarity overlap-add, shared alignment for both channels to preserve stereo.</summary>
internal static class AudioTimeStretch
{
    public static float[] Stretch(float[] stereo, double scale)
    {
        ArgumentNullException.ThrowIfNull(stereo);
        if (stereo.Length % 2 != 0) throw new ArgumentException("Das Sample muss stereo und interleaved sein.", nameof(stereo));
        if (!double.IsFinite(scale) || scale <= 0) throw new ArgumentOutOfRangeException(nameof(scale));
        if (Math.Abs(scale - 1) < 0.00001) return stereo;
        var inputFrames = stereo.Length / 2;
        var outputFrames = checked((int)Math.Max(1, Math.Round(inputFrames * scale)));
        var output = new float[checked(outputFrames * 2)];
        if (inputFrames == 0) return output;
        const int window = 1024, hop = 512, search = 192;
        if (inputFrames < window)
        {
            for (var frame = 0; frame < outputFrames; frame++)
            {
                var sourcePosition = outputFrames <= 1 ? 0 : frame * (inputFrames - 1d) / (outputFrames - 1);
                var lower = (int)Math.Floor(sourcePosition);
                var upper = Math.Min(lower + 1, inputFrames - 1);
                var fraction = (float)(sourcePosition - lower);
                for (var channel = 0; channel < 2; channel++)
                    output[frame * 2 + channel] = stereo[lower * 2 + channel] + (stereo[upper * 2 + channel] - stereo[lower * 2 + channel]) * fraction;
            }
            return output;
        }
        var first = Math.Min(window, Math.Min(inputFrames, outputFrames));
        Array.Copy(stereo, output, first * 2);
        for (var destination = hop; destination < outputFrames; destination += hop)
        {
            var expected = (int)Math.Round(destination / scale);
            if (expected >= inputFrames) break;
            var best = Math.Clamp(expected, 0, Math.Max(0, inputFrames - window));
            var score = double.NegativeInfinity;
            var overlap = Math.Min(hop, outputFrames - destination);
            if (inputFrames >= window)
            {
                for (var candidate = Math.Max(0, expected - search); candidate <= Math.Min(inputFrames - window, expected + search); candidate += 4)
                {
                    double dot = 0, energy = 1e-12;
                    for (var i = 0; i < overlap; i += 8)
                    {
                        var a = output[(destination + i) * 2] + output[(destination + i) * 2 + 1];
                        var b = stereo[(candidate + i) * 2] + stereo[(candidate + i) * 2 + 1];
                        dot += a * b; energy += b * b;
                    }
                    var correlation = dot / Math.Sqrt(energy);
                    if (correlation > score) { score = correlation; best = candidate; }
                }
            }
            var length = Math.Min(window, Math.Min(inputFrames - best, outputFrames - destination));
            for (var i = 0; i < length; i++)
            {
                var blend = i < hop ? i / (float)hop : 1;
                for (var c = 0; c < 2; c++)
                {
                    var target = (destination + i) * 2 + c;
                    output[target] = output[target] * (1 - blend) + stereo[(best + i) * 2 + c] * blend;
                }
            }
        }
        return output;
    }
}
