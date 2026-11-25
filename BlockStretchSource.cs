using System.IO;

namespace WellenEditor;

/// <summary>Sequential, bounded WSOLA implementation matching AudioTimeStretch.</summary>
internal sealed class BlockStretchSource : IAudioSource
{
    private const int Window = 1024, Hop = 512, Search = 192;
    private readonly IAudioSource source;
    private readonly double scale;
    private readonly float[] output = new float[Window * 2];
    private readonly float[] shortInput;
    private float[] input = Array.Empty<float>();
    private long position;
    private bool started;
    public int Channels => 2;
    public int SampleRate => source.SampleRate;
    public int BitsPerSample => 32;
    public long FrameCount { get; }

    public BlockStretchSource(IAudioSource source, double scale)
    {
        this.source = source ?? throw new ArgumentNullException(nameof(source));
        if (source.Channels != 2) throw new ArgumentException("Stereoquelle erforderlich.", nameof(source));
        if (!double.IsFinite(scale) || scale <= 0) throw new ArgumentOutOfRangeException(nameof(scale));
        this.scale = scale;
        FrameCount = checked((long)Math.Max(1, Math.Round(source.FrameCount * scale)));
        shortInput = source.FrameCount < Window ? new float[checked((int)source.FrameCount * 2)] : Array.Empty<float>();
        if (shortInput.Length > 0 && source.ReadFrames(0, shortInput, 0, (int)source.FrameCount) != source.FrameCount)
            throw new EndOfStreamException("Time-Stretch-Quelle endete vorzeitig.");
    }

    public int ReadFrames(long startFrame, float[] buffer, int bufferOffsetFrames, int frameCount)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (startFrame != position) throw new NotSupportedException("Der blockweise Time-Stretch unterstützt derzeit nur fortlaufendes Lesen ab dem Clipanfang.");
        if (frameCount < 0 || bufferOffsetFrames < 0) throw new ArgumentOutOfRangeException();
        var count = (int)Math.Min(frameCount, Math.Max(0, FrameCount - position));
        if (((long)bufferOffsetFrames + count) * 2 > buffer.Length) throw new ArgumentException("Der Ausgabepuffer ist zu klein.", nameof(buffer));
        if (shortInput.Length > 0)
        {
            for (var frame = 0; frame < count; frame++)
            {
                var sourcePosition = FrameCount <= 1 ? 0 : (position + frame) * (source.FrameCount - 1d) / (FrameCount - 1);
                var lower = (int)Math.Floor(sourcePosition);
                var upper = Math.Min(lower + 1, (int)source.FrameCount - 1);
                var fraction = (float)(sourcePosition - lower);
                for (var c = 0; c < 2; c++)
                    buffer[(bufferOffsetFrames + frame) * 2 + c] = shortInput[lower * 2 + c] + (shortInput[upper * 2 + c] - shortInput[lower * 2 + c]) * fraction;
            }
            position += count;
            return count;
        }
        if (!started && count > 0)
        {
            var first = (int)Math.Min(Window, Math.Min(source.FrameCount, FrameCount));
            if (source.ReadFrames(0, output, 0, first) != first) throw new EndOfStreamException("Time-Stretch-Quelle endete vorzeitig.");
            started = true;
        }
        var copied = 0;
        while (copied < count)
        {
            var offset = (int)(position % Hop);
            var n = Math.Min(count - copied, Hop - offset);
            Array.Copy(output, offset * 2, buffer, (bufferOffsetFrames + copied) * 2, n * 2);
            copied += n; position += n;
            if (position % Hop == 0 && position < FrameCount) PrepareNextHop();
        }
        return copied;
    }

    private void PrepareNextHop()
    {
        Array.Copy(output, Hop * 2, output, 0, Hop * 2);
        Array.Clear(output, Hop * 2, Hop * 2);
        var expected = checked((long)Math.Round(position / scale));
        if (expected >= source.FrameCount) return;
        var best = Math.Clamp(expected, 0, Math.Max(0, source.FrameCount - Window));
        var candidateFirst = Math.Max(0, expected - Search);
        var candidateLast = Math.Min(source.FrameCount - Window, expected + Search);
        var readStart = Math.Min(best, candidateFirst);
        var readEnd = Math.Min(source.FrameCount, Math.Max(best, candidateLast) + Window);
        var inputFrames = checked((int)(readEnd - readStart));
        if (input.Length < inputFrames * 2) input = new float[inputFrames * 2];
        if (source.ReadFrames(readStart, input, 0, inputFrames) != inputFrames) throw new EndOfStreamException("Time-Stretch-Quelle endete vorzeitig.");
        var score = double.NegativeInfinity;
        var overlap = (int)Math.Min(Hop, FrameCount - position);
        for (var candidate = candidateFirst; candidate <= candidateLast; candidate += 4)
        {
            double dot = 0, energy = 1e-12;
            var start = checked((int)(candidate - readStart) * 2);
            for (var i = 0; i < overlap; i += 8)
            {
                var a = output[i * 2] + output[i * 2 + 1];
                var b = input[start + i * 2] + input[start + i * 2 + 1];
                dot += a * b; energy += b * b;
            }
            var correlation = dot / Math.Sqrt(energy);
            if (correlation > score) { score = correlation; best = candidate; }
        }
        var length = (int)Math.Min(Window, Math.Min(source.FrameCount - best, FrameCount - position));
        var bestOffset = checked((int)(best - readStart) * 2);
        for (var i = 0; i < length; i++)
        {
            var blend = i < Hop ? i / (float)Hop : 1;
            for (var c = 0; c < 2; c++)
                output[i * 2 + c] = output[i * 2 + c] * (1 - blend) + input[bestOffset + i * 2 + c] * blend;
        }
    }

    public void Dispose() => source.Dispose();
}

internal sealed class StereoBlockSource(IAudioSource source) : IAudioSource
{
    private float[] mono = Array.Empty<float>();
    public int Channels => 2;
    public int SampleRate => source.SampleRate;
    public int BitsPerSample => source.BitsPerSample;
    public long FrameCount => source.FrameCount;
    public int ReadFrames(long startFrame, float[] buffer, int bufferOffsetFrames, int frameCount)
    {
        if (mono.Length < frameCount) mono = new float[frameCount];
        var read = source.ReadFrames(startFrame, mono, 0, frameCount);
        for (var i = 0; i < read; i++) buffer[(bufferOffsetFrames + i) * 2] = buffer[(bufferOffsetFrames + i) * 2 + 1] = mono[i];
        return read;
    }
    public void Dispose() => source.Dispose();
}
