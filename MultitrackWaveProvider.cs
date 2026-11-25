using NAudio.Wave;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace WellenEditor;

/// <summary>Prepared clip audio; only summing and reading atomic mixer controls happen in Read.</summary>
internal sealed class MultitrackWaveProvider : IRewindableWaveProvider
{
    private sealed record Voice(MultitrackTrack Track, long Start, float[] Samples);
    private readonly MultitrackTrack[] tracks;
    private sealed record TrackRenderState(Voice[] Voices, TrackEffectProcessor Effects, bool OfflineVst);
    private sealed class TrackMix
    {
        public required MultitrackTrack Track;
        public required TrackRenderState State;
        public readonly float[] Buffer = new float[4096 * 2];
        public int PreparationVersion;
    }
    private readonly TrackMix[] trackMixes;
    private readonly ConcurrentQueue<string> preparationWarnings = new();
    private readonly TrackEffectChainRenderer.VstRenderer? vstRenderer;
    public IReadOnlyList<string> PreparationWarnings => preparationWarnings.ToArray();
    public void UpdateTrackEffects(MultitrackTrack track)
    {
        foreach (var mix in trackMixes)
        {
            if (!ReferenceEquals(mix.Track, track)) continue;
            var currentState = Volatile.Read(ref mix.State);
            if (!currentState.OfflineVst && !track.Effects.Any(effect => effect.Enabled && effect.Vst is not null))
            {
                var nativeOnly = new TrackRenderState(currentState.Voices, new TrackEffectProcessor(track.Effects, WaveFormat.SampleRate), false);
                Volatile.Write(ref mix.State, nativeOnly);
                continue;
            }
            var version = Interlocked.Increment(ref mix.PreparationVersion);
            var effects = track.Effects;
            _ = Task.Run(() => PrepareTrack(track, effects))
                .ContinueWith(task =>
                {
                    if (task.IsCompletedSuccessfully && Volatile.Read(ref mix.PreparationVersion) == version)
                        Volatile.Write(ref mix.State, task.Result);
                    else if (task.IsFaulted && task.Exception is { } exception)
                        preparationWarnings.Enqueue($"Spur '{track.Name}': Effekte konnten nicht neu vorbereitet werden ({exception.GetBaseException().Message}); bisherige Wiedergabe bleibt aktiv.");
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
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

    public MultitrackWaveProvider(MultitrackProject project, TrackEffectChainRenderer.VstRenderer? vstRenderer = null)
    {
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(project.SampleRate, 2);
        this.vstRenderer = vstRenderer;
        tracks = project.Tracks.ToArray();
        trackMixes = tracks.Select(track => new TrackMix { Track = track, State = PrepareTrack(track, track.Effects) }).ToArray();
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

    private TrackRenderState PrepareTrack(MultitrackTrack track, IReadOnlyList<TrackEffectSlot> effects)
    {
        if (track.Clips.Count == 0)
            return new TrackRenderState(Array.Empty<Voice>(), new TrackEffectProcessor(effects, WaveFormat.SampleRate), false);

        var dryVoices = track.Clips.Select(clip => new Voice(track, clip.StartFrame, PrepareClip(clip, WaveFormat.SampleRate))).ToArray();
        var hasVst = effects.Any(effect => effect.Enabled && effect.Vst is not null);
        if (!hasVst)
            return new TrackRenderState(dryVoices, new TrackEffectProcessor(effects, WaveFormat.SampleRate), false);

        var frameCount = track.EndFrame;
        if (frameCount > Array.MaxLength / 2)
        {
            preparationWarnings.Enqueue($"Spur '{track.Name}': Die Spur ist zu lang für die Offline-VST3-Kette; VST3-Effekte wurden übersprungen.");
            return new TrackRenderState(dryVoices, new TrackEffectProcessor(effects.Where(effect => effect.Vst is null), WaveFormat.SampleRate), false);
        }

        try
        {
            var stereo = new float[checked((int)frameCount * 2)];
            foreach (var voice in dryVoices)
            {
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
                warning => preparationWarnings.Enqueue($"Spur '{track.Name}': {warning}"), vstRenderer);
            return new TrackRenderState(new[] { new Voice(track, 0, processed) }, new TrackEffectProcessor(Array.Empty<TrackEffectSlot>(), WaveFormat.SampleRate), true);
        }
        catch (Exception exception)
        {
            preparationWarnings.Enqueue($"Spur '{track.Name}': Die Offline-Effektkette ist fehlgeschlagen ({exception.Message}); VST3-Effekte wurden übersprungen.");
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
