using System.IO;

namespace WellenEditor;

/// <summary>
/// Mehrstufiger Min/Max-Cache für eine diskbasierte Audioquelle. Er enthält keine
/// Rohsamples und kann deshalb auch für lange WAV-Dateien im Hintergrund gebaut werden.
/// </summary>
internal sealed class WaveformPeakCache
{
    internal sealed record Level(int FramesPerPeak, float[] Minimums, float[] Maximums)
    {
        public int BucketCount => Minimums.Length;
    }

    private readonly List<Level> levels;

    private WaveformPeakCache(int channels, long frameCount, List<Level> levels)
    {
        Channels = channels;
        FrameCount = frameCount;
        this.levels = levels;
    }

    public int Channels { get; }
    public long FrameCount { get; }
    public IReadOnlyList<Level> Levels => levels;

    public Level SelectLevel(double framesPerPixel)
    {
        var selected = levels[0];
        foreach (var level in levels)
        {
            if (level.FramesPerPeak > framesPerPixel) break;
            selected = level;
        }
        return selected;
    }

    public static WaveformPeakCache Build(IAudioSource source, CancellationToken cancellationToken = default, int baseFramesPerPeak = 1024)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (baseFramesPerPeak < 1) throw new ArgumentOutOfRangeException(nameof(baseFramesPerPeak));

        var mins = new List<float>();
        var maxs = new List<float>();
        var currentMin = new float[source.Channels];
        var currentMax = new float[source.Channels];
        Array.Fill(currentMin, float.PositiveInfinity);
        Array.Fill(currentMax, float.NegativeInfinity);

        var readBuffer = new float[4096 * source.Channels];
        var inBucket = 0;
        for (long frame = 0; frame < source.FrameCount;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var requested = (int)Math.Min(4096, source.FrameCount - frame);
            var read = source.ReadFrames(frame, readBuffer, 0, requested);
            if (read <= 0) throw new EndOfStreamException("Die Audioquelle endete beim Aufbau des Wellenform-Caches vorzeitig.");

            for (var localFrame = 0; localFrame < read; localFrame++)
            {
                var sampleOffset = localFrame * source.Channels;
                for (var channel = 0; channel < source.Channels; channel++)
                {
                    var value = readBuffer[sampleOffset + channel];
                    currentMin[channel] = Math.Min(currentMin[channel], value);
                    currentMax[channel] = Math.Max(currentMax[channel], value);
                }

                if (++inBucket != baseFramesPerPeak && frame + localFrame + 1 < source.FrameCount) continue;
                for (var channel = 0; channel < source.Channels; channel++)
                {
                    mins.Add(currentMin[channel]);
                    maxs.Add(currentMax[channel]);
                    currentMin[channel] = float.PositiveInfinity;
                    currentMax[channel] = float.NegativeInfinity;
                }
                inBucket = 0;
            }
            frame += read;
        }

        var levels = new List<Level> { new(baseFramesPerPeak, mins.ToArray(), maxs.ToArray()) };
        while (levels[^1].BucketCount > source.Channels)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var previous = levels[^1];
            var bucketCount = (previous.BucketCount / source.Channels + 3) / 4;
            var nextMin = new float[bucketCount * source.Channels];
            var nextMax = new float[bucketCount * source.Channels];
            for (var bucket = 0; bucket < bucketCount; bucket++)
            {
                for (var channel = 0; channel < source.Channels; channel++)
                {
                    var min = float.PositiveInfinity;
                    var max = float.NegativeInfinity;
                    for (var part = 0; part < 4; part++)
                    {
                        var previousBucket = bucket * 4 + part;
                        if (previousBucket >= previous.BucketCount / source.Channels) break;
                        var index = previousBucket * source.Channels + channel;
                        min = Math.Min(min, previous.Minimums[index]);
                        max = Math.Max(max, previous.Maximums[index]);
                    }
                    nextMin[bucket * source.Channels + channel] = min;
                    nextMax[bucket * source.Channels + channel] = max;
                }
            }
            levels.Add(new(checked(previous.FramesPerPeak * 4), nextMin, nextMax));
        }

        return new WaveformPeakCache(source.Channels, source.FrameCount, levels);
    }
}
