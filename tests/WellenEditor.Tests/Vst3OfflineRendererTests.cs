using System.IO;
using NAudio.Vst3;
using NAudio.Wave;
using WellenEditor;
using Xunit;

namespace WellenEditorTests;

public sealed class Vst3OfflineRendererTests
{
    [Fact]
    public async Task StereoInputAccumulatesPartialSampleReadsWithoutHangingOrLosingChannels()
    {
        var provider = new ChunkedSampleProvider(new[] { .1f, .2f, .3f, .4f }, WaveFormat.CreateIeeeFloatWaveFormat(48000, 2), 1);
        var render = Task.Run(() => Vst3OfflineRenderer.ReadStereoInput(provider, 48000, 2, 2));

        var result = await render.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(new[] { .1f, .2f, .3f, .4f }, result);
    }

    [Fact]
    public void StereoInputRejectsEndOfStreamMidFrameAndShortSource()
    {
        var midFrame = new ChunkedSampleProvider(new[] { .1f }, WaveFormat.CreateIeeeFloatWaveFormat(48000, 2), 1);
        var shortSource = new ChunkedSampleProvider(new[] { .1f, .2f }, WaveFormat.CreateIeeeFloatWaveFormat(48000, 2), 1);

        Assert.Throws<EndOfStreamException>(() => Vst3OfflineRenderer.ReadStereoInput(midFrame, 48000, 2, 1));
        Assert.Throws<EndOfStreamException>(() => Vst3OfflineRenderer.ReadStereoInput(shortSource, 48000, 2, 2));
    }

    [Fact]
    public void StereoInputRejectsProviderFormatMismatchAndSampleCountOverflow()
    {
        var provider = new ChunkedSampleProvider(Array.Empty<float>(), WaveFormat.CreateIeeeFloatWaveFormat(44100, 2), 1);

        Assert.Throws<ArgumentException>(() => Vst3OfflineRenderer.ReadStereoInput(provider, 48000, 2, 1));
        Assert.Throws<OverflowException>(() => Vst3OfflineRenderer.ReadStereoInput(provider, 44100, 2, int.MaxValue));
    }

    private sealed class ChunkedSampleProvider(float[] samples, WaveFormat format, int maximumRead) : ISampleProvider
    {
        private int position;
        public WaveFormat WaveFormat { get; } = format;

        public int Read(float[] buffer, int offset, int count)
        {
            var copied = Math.Min(Math.Min(count, maximumRead), samples.Length - position);
            Array.Copy(samples, position, buffer, offset, copied);
            position += copied;
            return copied;
        }

        public int Read(Span<float> buffer)
        {
            var copied = Math.Min(Math.Min(buffer.Length, maximumRead), samples.Length - position);
            samples.AsSpan(position, copied).CopyTo(buffer);
            position += copied;
            return copied;
        }
    }
}
