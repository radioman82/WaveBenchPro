using WellenEditor;
using System.Diagnostics;
using Xunit;

namespace WellenEditorTests;

public sealed class SegmentedAudioSourceTests
{
    [Fact]
    public void ReadsManySegmentsAtBoundariesWithCorrectTimelineAndGain()
    {
        var source = new ArrayAudioSource(Enumerable.Range(0, 512).Select(value => (float)value).ToArray());
        var segments = Enumerable.Range(0, 128)
            .Select(index => new AudioSourceSegment(index * 4, 2, .5f, .5f))
            .ToArray();
        using var segmented = new SegmentedAudioSource(source, segments);
        var output = new float[8];

        var read = segmented.ReadFrames(0, output, 0, 8);
        Assert.Equal(8, read);
        Assert.Equal(new[] { 0f, .5f, 2f, 2.5f, 4f, 4.5f, 6f, 6.5f }, output);

        var later = new float[4];
        Assert.Equal(4, segmented.ReadFrames(200, later, 0, 4));
        Assert.Equal(new[] { 200f, 200.5f, 202f, 202.5f }, later);
    }

    [Fact]
    public void RejectsNegativeOutOfRangeAndOverflowingSegments()
    {
        var source = new ArrayAudioSource(new float[16]);

        Assert.Throws<ArgumentOutOfRangeException>(() => new SegmentedAudioSource(source, new[] { new AudioSourceSegment(-1, 1) }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SegmentedAudioSource(source, new[] { new AudioSourceSegment(15, 2) }));
        Assert.Throws<OverflowException>(() => new SegmentedAudioSource(source, new[] { new AudioSourceSegment(long.MaxValue, 1) }));
        Assert.Throws<OverflowException>(() => new AudioSourceSegment(0, 4).Slice(long.MaxValue, 1));
    }

    [Fact]
    public void RandomReadsAcrossFiftyThousandSegmentsBenchmarkBinaryLookup()
    {
        const int segmentCount = 50_000;
        var source = new ArrayAudioSource(Enumerable.Range(0, segmentCount).Select(value => (float)value).ToArray());
        using var segmented = new SegmentedAudioSource(source,
            Enumerable.Range(0, segmentCount).Select(index => new AudioSourceSegment(index, 1)).ToArray());
        var random = new Random(42);
        var buffer = new float[1];
        var stopwatch = Stopwatch.StartNew();
        for (var read = 0; read < 10_000; read++)
        {
            var frame = random.Next(segmentCount);
            Assert.Equal(1, segmented.ReadFrames(frame, buffer, 0, 1));
            Assert.Equal((float)frame, buffer[0]);
        }
        stopwatch.Stop();

        Console.WriteLine($"Segment lookup benchmark: 10,000 random reads across {segmentCount:N0} segments in {stopwatch.Elapsed.TotalMilliseconds:F1} ms.");
    }

    private sealed class ArrayAudioSource(float[] samples) : IAudioSource
    {
        public int SampleRate => 48000;
        public int Channels => 1;
        public long FrameCount => samples.Length;
        public int BitsPerSample => 32;
        public int ReadFrames(long startFrame, float[] buffer, int bufferOffsetFrames, int frameCount)
        {
            var count = (int)Math.Min(frameCount, Math.Max(0, samples.Length - startFrame));
            Array.Copy(samples, startFrame, buffer, bufferOffsetFrames, count);
            return count;
        }
        public void Dispose() { }
    }
}
