using System.IO;

namespace WellenEditor;

/// <summary>Offline linear resampling with the same finite low-pass as AudioFormatConverter.</summary>
internal sealed class BlockResamplingSource : IAudioSource
{
    private readonly IAudioSource source;
    private readonly double[]? weights;
    private readonly int halfWidth;
    private float[] input = Array.Empty<float>();
    public int SampleRate { get; }
    public int Channels => source.Channels;
    public int BitsPerSample => 32;
    public long FrameCount { get; }

    public BlockResamplingSource(IAudioSource source, int targetRate)
    {
        this.source = source ?? throw new ArgumentNullException(nameof(source));
        if (source.SampleRate is < 8000 or > 192000 || targetRate is < 8000 or > 192000)
            throw new NotSupportedException("Blockweises Resampling unterstützt Abtastraten von 8 bis 192 kHz.");
        SampleRate = targetRate;
        FrameCount = checked((long)Math.Round((double)source.FrameCount * targetRate / source.SampleRate));
        if (targetRate >= source.SampleRate) return;
        var cutoff = .94 * targetRate / source.SampleRate;
        halfWidth = Math.Max(16, (int)Math.Ceiling(16 / cutoff));
        weights = new double[halfWidth * 2 + 1];
        for (var distance = -halfWidth; distance <= halfWidth; distance++)
        {
            var x = cutoff * distance;
            var sinc = Math.Abs(x) < 1e-12 ? 1d : Math.Sin(Math.PI * x) / (Math.PI * x);
            var p = distance / (double)halfWidth;
            weights[distance + halfWidth] = cutoff * sinc * (0.42 + 0.5 * Math.Cos(Math.PI * p) + 0.08 * Math.Cos(2 * Math.PI * p));
        }
    }

    public int ReadFrames(long startFrame, float[] buffer, int bufferOffsetFrames, int frameCount)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (startFrame < 0 || bufferOffsetFrames < 0 || frameCount < 0) throw new ArgumentOutOfRangeException();
        var count = (int)Math.Min(frameCount, Math.Max(0, FrameCount - startFrame));
        if (((long)bufferOffsetFrames + count) * Channels > buffer.Length) throw new ArgumentException("Der Ausgabepuffer ist zu klein.", nameof(buffer));
        if (count == 0) return 0;
        var firstLower = (long)Math.Floor((double)startFrame * source.SampleRate / SampleRate);
        var lastLower = (long)Math.Floor((double)(startFrame + count - 1) * source.SampleRate / SampleRate);
        var from = Math.Max(0, firstLower - halfWidth);
        var to = Math.Min(source.FrameCount, checked(lastLower + 2L + halfWidth));
        var sourceFrames = checked((int)(to - from));
        var sourceSamples = checked(sourceFrames * Channels);
        if (input.Length < sourceSamples) input = new float[sourceSamples];
        if (source.ReadFrames(from, input, 0, sourceFrames) != sourceFrames)
            throw new EndOfStreamException("Resampling-Quelle endete vorzeitig.");
        for (var frame = 0; frame < count; frame++)
        {
            var position = (double)(startFrame + frame) * source.SampleRate / SampleRate;
            var lower = (long)Math.Floor(position);
            var upper = Math.Min(lower + 1, source.FrameCount - 1);
            var fraction = (float)(position - lower);
            for (var channel = 0; channel < Channels; channel++)
            {
                var a = Sample(lower, channel, from);
                var b = Sample(upper, channel, from);
                buffer[(bufferOffsetFrames + frame) * Channels + channel] = a + (b - a) * fraction;
            }
        }
        return count;
    }

    private float Sample(long frame, int channel, long bufferStart)
    {
        if (weights is null) return input[checked((int)((frame - bufferStart) * Channels + channel))];
        var first = (int)Math.Max(-halfWidth, -frame);
        var last = (int)Math.Min(halfWidth, source.FrameCount - 1 - frame);
        double sumWeights = 0;
        float result = 0;
        for (var distance = first; distance <= last; distance++)
        {
            var weight = weights[distance + halfWidth];
            sumWeights += weight;
            result += (float)(input[checked((int)((frame + distance - bufferStart) * Channels + channel))] * weight);
        }
        return Math.Abs(sumWeights) > 1e-12 ? result / (float)sumWeights : result;
    }

    public void Dispose() => source.Dispose();
}
