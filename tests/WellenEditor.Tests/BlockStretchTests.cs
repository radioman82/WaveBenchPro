using WellenEditor;
using NAudio.Wave;
using System.IO;
using Xunit;

namespace WellenEditorTests;

public sealed class BlockStretchTests
{
    [Theory]
    [InlineData(100, .1)]
    [InlineData(100, 10)]
    [InlineData(1023, 1.4)]
    [InlineData(1024, .5)]
    [InlineData(4097, .8)]
    [InlineData(4097, 1.4)]
    [InlineData(10000, 2.2)]
    public void SequentialBlocksMatchExistingStretchIncludingStereoPhase(int frames, double scale)
    {
        var input = Enumerable.Range(0, frames).SelectMany(i =>
        {
            var sample = .4f * MathF.Sin(i * .07f) + (i % 313 == 0 ? .3f : 0);
            return new[] { sample, -sample * .8f };
        }).ToArray();
        var expected = AudioTimeStretch.Stretch(input, scale);
        using var source = new BlockStretchSource(new MemoryAudioSource(input, 2, 48000), scale);
        Assert.Equal(expected.Length / 2, source.FrameCount);
        var block = new float[257 * 2];
        var position = 0;
        while (position < source.FrameCount)
        {
            var read = source.ReadFrames(position, block, 0, 257);
            Assert.True(read > 0);
            for (var i = 0; i < read * 2; i++)
                Assert.InRange(Math.Abs(expected[position * 2 + i] - block[i]), 0, 1e-6f);
            position += read;
        }
    }

    [Theory]
    [InlineData(44100, 48000, .8, 1)]
    [InlineData(48000, 44100, 1.4, 2)]
    [InlineData(48000, 48000, 2.1, 1)]
    [InlineData(44100, 44100, .3, 2)]
    public void MixedRateAndStretchedTracksMatchLegacyMix(int sourceRate, int projectRate, double scale, int channels)
    {
        using var reference = AudioDocument.FromSamples(new float[10], 1, projectRate);
        var input = Enumerable.Range(0, 4097).SelectMany(i => channels == 1
            ? new[] { .4f * MathF.Sin(i * .07f) }
            : new[] { .4f * MathF.Sin(i * .07f), -.3f * MathF.Sin(i * .07f) }).ToArray();
        using var sound = AudioDocument.FromSamples(input, channels, sourceRate);
        var project = new MultitrackProject(); project.Add(reference); project.Add(sound);
        project.Tracks[1].StartFrame = 17;
        project.Tracks[1].Clips[0].DurationScale = scale;
        var expected = project.RenderMixdown();
        using var renderer = new BlockMixdownRenderer(project, 257);
        var buffer = new float[514];
        var offset = 0;
        int count;
        while ((count = renderer.Read(buffer, 257)) > 0)
        {
            for (var i = 0; i < count * 2; i++)
                Assert.InRange(Math.Abs(expected[offset * 2 + i] - buffer[i]), 0, 1e-6f);
            offset += count;
        }
        Assert.Equal(expected.Length, offset * 2);
    }

    [Fact]
    public void VariedSourceLengthsAndRatiosMatchLegacyAcrossIrregularReads()
    {
        var random = new Random(1947);
        for (var caseIndex = 0; caseIndex < 30; caseIndex++)
        {
            var frames = random.Next(50, 6000);
            var scale = .25 + random.NextDouble() * 2.5;
            var input = Enumerable.Range(0, frames * 2).Select(_ => (float)(random.NextDouble() - .5)).ToArray();
            var expected = AudioTimeStretch.Stretch(input, scale);
            using var source = new BlockStretchSource(new MemoryAudioSource(input, 2, 48000), scale);
            var block = new float[263 * 2];
            var offset = 0;
            while (offset < source.FrameCount)
            {
                var read = source.ReadFrames(offset, block, 0, random.Next(1, 264));
                Assert.True(read > 0);
                for (var i = 0; i < read * 2; i++)
                    Assert.InRange(Math.Abs(expected[offset * 2 + i] - block[i]), 0, 1e-6f);
                offset += read;
            }
        }
    }

    [Fact]
    public void VariedRatesAndStretchRatiosMatchLegacyMixForMonoAndStereo()
    {
        var random = new Random(1923);
        int[] rates = [8000, 44100, 48000, 96000, 192000];
        for (var trial = 0; trial < 20; trial++)
        {
            var projectRate = rates[random.Next(rates.Length)];
            var sourceRate = rates[random.Next(rates.Length)];
            var channels = random.Next(1, 3);
            var frames = random.Next(50, 1600);
            var scale = .5 + random.NextDouble() * 1.5;
            using var anchor = AudioDocument.FromSamples(new float[5], 1, projectRate);
            using var audio = AudioDocument.FromSamples(Enumerable.Range(0, frames * channels).Select(_ => (float)(random.NextDouble() - .5)).ToArray(), channels, sourceRate);
            var project = new MultitrackProject(); project.Add(anchor); project.Add(audio);
            project.Tracks[1].StartFrame = 23;
            project.Tracks[1].Clips[0].DurationScale = scale;
            var expected = project.RenderMixdown();
            using var renderer = new BlockMixdownRenderer(project, 257);
            var block = new float[514];
            var offset = 0;
            int read;
            while ((read = renderer.Read(block, 257)) > 0)
            {
                for (var i = 0; i < read * 2; i++)
                    Assert.InRange(Math.Abs(expected[offset * 2 + i] - block[i]), 0, 1e-6f);
                offset += read;
            }
            Assert.Equal(expected.Length, offset * 2);
        }
    }

    [Fact]
    public void StretchedResampledEditedStreamingWaveMatchesLegacyWithoutMaterializingNewPath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wavebench_stretch_{Guid.NewGuid():N}.wav");
        try
        {
            var samples = Enumerable.Range(0, 12000).Select(i => .5f * MathF.Sin(i * .13f)).ToArray();
            using (var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(48000, 1)))
                writer.WriteSamples(samples, 0, samples.Length);
            using var original = AudioDocument.Load(path);
            original.Delete(new Selection(1200, 2500));
            using var anchor = AudioDocument.FromSamples(new float[5], 1, 44100);
            var reference = new MultitrackProject(); reference.Add(anchor); reference.Add(original);
            reference.Tracks[1].Clips[0].DurationScale = 1.3;
            var expected = reference.RenderMixdown();
            using var streaming = AudioDocument.Load(path);
            streaming.Delete(new Selection(1200, 2500));
            var project = new MultitrackProject(); project.Add(anchor); project.Add(streaming);
            project.Tracks[1].Clips[0].DurationScale = 1.3;
            using var renderer = new BlockMixdownRenderer(project, 257);
            var block = new float[514];
            var offset = 0;
            int read;
            while ((read = renderer.Read(block, 257)) > 0)
            {
                for (var i = 0; i < read * 2; i++) Assert.InRange(Math.Abs(expected[offset * 2 + i] - block[i]), 0, 1e-6f);
                offset += read;
            }
            Assert.Equal(expected.Length, offset * 2);
            Assert.True(streaming.IsStreaming);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void RateAndBlockSizeMatrixWithStretchMatchesLegacy()
    {
        int[] rates = [44100, 48000, 96000, 192000];
        int[] blocks = [256, 1024, 4096, 8192];
        double[] scales = [.8, 1.4];
        var maximum = 0d;
        var sumSquares = 0d;
        long sampleCount = 0;
        foreach (var projectRate in rates)
        foreach (var sourceRate in rates)
        foreach (var blockSize in blocks)
        foreach (var scale in scales)
        {
            using var anchor = AudioDocument.FromSamples(new float[4], 1, projectRate);
            var input = Enumerable.Range(0, 2049).SelectMany(i =>
            {
                var s = .3f * MathF.Sin(i * .17f) + (i == 255 || i == 1024 ? .4f : 0);
                return new[] { s, -s * .7f };
            }).ToArray();
            using var sound = AudioDocument.FromSamples(input, 2, sourceRate);
            var project = new MultitrackProject(); project.Add(anchor); project.Add(sound);
            project.Tracks[1].StartFrame = 11;
            project.Tracks[1].Clips[0].DurationScale = scale;
            var expected = project.RenderMixdown();
            using var renderer = new BlockMixdownRenderer(project, blockSize);
            var block = new float[blockSize * 2];
            var offset = 0;
            int read;
            while ((read = renderer.Read(block, blockSize)) > 0)
            {
                for (var i = 0; i < read * 2; i++)
                {
                    var difference = Math.Abs(expected[offset * 2 + i] - block[i]);
                    maximum = Math.Max(maximum, difference);
                    sumSquares += difference * difference;
                    sampleCount++;
                }
                offset += read;
            }
            Assert.Equal(expected.Length, offset * 2);
        }
        var rms = Math.Sqrt(sumSquares / sampleCount);
        Console.WriteLine($"128 rate/block/stretch cases: max={maximum:R}, rms={rms:R}, samples={sampleCount}");
        Assert.InRange(maximum, 0, 1e-6);
        Assert.InRange(rms, 0, 1e-6);
    }
}
