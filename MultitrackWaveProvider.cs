using NAudio.Wave;
using System.Runtime.InteropServices;

namespace WellenEditor;

/// <summary>Prepared clip audio; only summing and reading atomic mixer controls happen in Read.</summary>
internal sealed class MultitrackWaveProvider : IRewindableWaveProvider
{
    private sealed record Voice(MultitrackTrack Track, long Start, float[] Samples);
    private readonly Voice[] voices;
    private readonly MultitrackTrack[] tracks;
    private sealed class TrackMix
    {
        public required MultitrackTrack Track;
        public required Voice[] Voices;
        public readonly float[] Buffer = new float[4096 * 2];
        public required TrackEffectProcessor Effects;
    }
    private readonly TrackMix[] trackMixes;
    public void UpdateTrackEffects(MultitrackTrack track)
    {
        var processor = new TrackEffectProcessor(track.Effects, WaveFormat.SampleRate);
        foreach (var mix in trackMixes)
            if (ReferenceEquals(mix.Track, track)) Volatile.Write(ref mix.Effects, processor);
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

    public MultitrackWaveProvider(MultitrackProject project)
    {
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(project.SampleRate, 2);
        tracks = project.Tracks.ToArray();
        voices = tracks.SelectMany(track => track.Clips.Select(clip =>
            new Voice(track, clip.StartFrame, Prepare(clip, project.SampleRate, track.Effects)))).ToArray();
        if (voices.Length == 0) throw new InvalidOperationException("Bitte zuerst ein Sample in die Mehrspuransicht ziehen.");
        trackMixes = tracks.Select(track => new TrackMix { Track = track, Voices = voices.Where(v => ReferenceEquals(v.Track, track)).ToArray(), Effects = new TrackEffectProcessor(track.Effects, project.SampleRate) }).ToArray();
        FrameCount = voices.Max(v => v.Start + v.Samples.Length / 2);
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

    private static float[] Prepare(MultitrackClip clip, int rate, IReadOnlyList<TrackEffectSlot> effects)
    {
        var source = clip.Source;
        var input = source.Samples;
        var frames = checked((int)Math.Ceiling(source.FrameCount * (double)rate / source.SampleRate));
        var stereo = new float[checked(frames * 2)];
        for (var frame = 0; frame < frames; frame++)
        {
            var offset = frame * (double)source.SampleRate / rate;
            var a = Math.Min(source.FrameCount - 1, (int)offset);
            var b = Math.Min(source.FrameCount - 1, a + 1);
            var fraction = (float)(offset - a);
            for (var channel = 0; channel < 2; channel++)
            {
                var c = Math.Min(channel, source.Channels - 1);
                stereo[frame * 2 + channel] = input[a * source.Channels + c] * (1 - fraction) + input[b * source.Channels + c] * fraction;
            }
        }
        stereo = AudioTimeStretch.Stretch(stereo, clip.DurationScale);
        foreach (var effect in effects.Where(effect => effect.Enabled && effect.Vst is not null))
        {
            try
            {
                var vstSource = new ArraySampleProvider(stereo, WaveFormat.CreateIeeeFloatWaveFormat(rate, 2));
                var rendered = Vst3OfflineRenderer.Render(effect.Vst!, vstSource, rate, 2, stereo.Length / 2, effect.VstState);
                if (rendered.Length == stereo.Length) stereo = rendered;
            }
            catch
            {
                // Ein nicht ladbares Plugin darf die Mehrspur-Wiedergabe nicht blockieren.
            }
        }
        return stereo;
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
        foreach (var track in tracks) anySolo |= track.Solo;
        foreach (var mix in trackMixes)
        {
            var track = mix.Track;
            if (track.Muted || (anySolo && !track.Solo)) continue;
            var trackBuffer = mix.Buffer.AsSpan(0, frames * 2);
            trackBuffer.Clear();
            var gain = (float)track.Gain;
            var pan = (float)track.Pan;
            var left = gain * (pan > 0 ? 1 - pan : 1);
            var right = gain * (pan < 0 ? 1 + pan : 1);
            foreach (var voice in mix.Voices)
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
            Volatile.Read(ref mix.Effects).Process(trackBuffer);
            for (var i = 0; i < trackBuffer.Length; i += 2)
            {
                output[i] += trackBuffer[i] * left;
                output[i + 1] += trackBuffer[i + 1] * right;
            }
        }
        for (var i = 0; i < output.Length; i++) output[i] = Math.Clamp(output[i], -1f, 1f);
    }
    public void Rewind() => position = Volatile.Read(ref pendingWindow)!.Start;
}

/// <summary>Waveform-similarity overlap-add, shared alignment for both channels to preserve stereo.</summary>
internal static class AudioTimeStretch
{
    public static float[] Stretch(float[] stereo, double scale)
    {
        if (Math.Abs(scale - 1) < 0.00001) return stereo;
        var inputFrames = stereo.Length / 2;
        var outputFrames = checked((int)Math.Max(1, Math.Round(inputFrames * scale)));
        var output = new float[checked(outputFrames * 2)];
        const int window = 1024, hop = 512, search = 192;
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
