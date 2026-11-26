using NAudio.Wave.Asio;
using Xunit;

namespace WellenEditor.Tests;

public sealed class NativeAsioCaptureConversionTests
{
    [Theory]
    [InlineData(AsioSampleType.Int16LSB, new byte[] { 0, 128, 0, 64 })]
    [InlineData(AsioSampleType.Int24LSB, new byte[] { 0, 0, 128, 0, 0, 64 })]
    [InlineData(AsioSampleType.Int32LSB, new byte[] { 0, 0, 0, 128, 0, 0, 0, 64 })]
    [InlineData(AsioSampleType.Float32LSB, new byte[] { 0, 0, 128, 191, 0, 0, 0, 63 })]
    public void NativeSamplesKeepTheirAmplitudeAndMappedChannelWithoutAllocations(AsioSampleType format, byte[] bytes)
    {
        var output = new float[] { .25f, 0, .75f, 0 };
        NativeAsioCaptureConversion.Interleave(bytes, output, 1, 2, format);
        Assert.Equal(new float[] { .25f, -1, .75f, .5f }, output);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++) NativeAsioCaptureConversion.Interleave(bytes, output, 1, 2, format);
        var after = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(before, after);
    }

    [Fact]
    public void UnsupportedNativeFormatIsRejectedBeforeCapture()
        => Assert.Throws<NotSupportedException>(() => NativeAsioCaptureConversion.BytesPerSample(AsioSampleType.Int16MSB));
}
