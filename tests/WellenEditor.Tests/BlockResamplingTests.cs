using WellenEditor;
using NAudio.Wave;
using System.IO;
using Xunit;

namespace WellenEditorTests;

public sealed class BlockResamplingTests
{
    [Theory]
    [InlineData(44100, 48000, 1)]
    [InlineData(48000, 44100, 2)]
    [InlineData(192000, 44100, 2)]
    [InlineData(8000, 192000, 1)]
    public void BlockAndRandomAccessMatchReference(int fromRate, int toRate, int channels)
    {
        var samples = Enumerable.Range(0, 1234 * channels).Select(i => .5f * MathF.Sin(i * .13f)).ToArray();
        var expected = AudioFormatConverter.Resample(samples, channels, fromRate, toRate);
        using var source = new BlockResamplingSource(new MemoryAudioSource(samples, channels, fromRate), toRate);
        Assert.Equal(expected.Length / channels, source.FrameCount);
        var block = new float[257 * channels];
        var offset = 0;
        while (offset < source.FrameCount)
        {
            var count = source.ReadFrames(offset, block, 0, 257);
            for (var i = 0; i < count * channels; i++)
                Assert.InRange(Math.Abs(block[i] - expected[offset * channels + i]), 0, 1e-6f);
            offset += count;
        }
        var start = Math.Max(0, (int)source.FrameCount / 2 - 20);
        var read = source.ReadFrames(start, block, 0, 40);
        for (var i = 0; i < read * channels; i++)
            Assert.InRange(Math.Abs(block[i] - expected[start * channels + i]), 0, 1e-6f);
    }

    [Theory]
    [InlineData(44100, 48000)]
    [InlineData(48000, 44100)]
    [InlineData(192000, 44100)]
    [InlineData(8000, 192000)]
    public void MixedRateTrackMatchesLegacyMixIncludingShortTail(int sourceRate, int projectRate)
    {
        using var reference = AudioDocument.FromSamples(new float[100], 1, projectRate);
        using var sound = AudioDocument.FromSamples(Enumerable.Range(0, 1234).Select(i => .4f * MathF.Sin(i * .11f)).ToArray(), 1, sourceRate);
        var project = new MultitrackProject(); project.Add(reference); project.Add(sound);
        project.Tracks[1].StartFrame = 17;
        project.Tracks[1].SetEffects(new[] { new TrackEffectSlot(Guid.NewGuid(), NativeEffectKind.HighPass, new EffectSettings { Frequency = 200 }) });
        var expected = project.RenderMixdown();
        using var renderer = new BlockMixdownRenderer(project, 256);
        var block = new float[512];
        var actual = new float[expected.Length];
        var offset = 0;
        int read;
        while ((read = renderer.Read(block, 256)) > 0)
        {
            Array.Copy(block, 0, actual, offset * 2, read * 2);
            offset += read;
        }
        Assert.Equal(expected.Length, offset * 2);
        for (var i = 0; i < expected.Length; i++) Assert.InRange(Math.Abs(expected[i] - actual[i]), 0, 1e-6f);
    }

    [Fact]
    public void StreamingWaveResamplesAcrossIrregularBlocksAfterNonDestructiveCut()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wavebench_resample_{Guid.NewGuid():N}.wav");
        try
        {
            var samples = Enumerable.Range(0, 12000).Select(i => .5f * MathF.Sin(i * .11f)).ToArray();
            using (var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(48000, 1)))
                writer.WriteSamples(samples, 0, samples.Length);
            using var source = AudioDocument.Load(path);
            source.Delete(new Selection(500, 3500));
            using var first = AudioDocument.FromSamples(new float[10], 1, 44100);
            var project = new MultitrackProject(); project.Add(first); project.Add(source);
            var expected = project.RenderMixdown();
            // RenderMixdown materializes the source in the reference path; reopen to test actual streaming.
            using var streaming = AudioDocument.Load(path);
            streaming.Delete(new Selection(500, 3500));
            var streamedProject = new MultitrackProject(); streamedProject.Add(first); streamedProject.Add(streaming);
            using var renderer = new BlockMixdownRenderer(streamedProject, 257);
            var block = new float[514];
            var offset = 0;
            int read;
            while ((read = renderer.Read(block, 257)) > 0)
            {
                for (var i = 0; i < read * 2; i++) Assert.InRange(Math.Abs(block[i] - expected[offset * 2 + i]), 0, 1e-6f);
                offset += read;
            }
            Assert.True(streaming.IsStreaming);
            Assert.Equal(expected.Length, offset * 2);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
