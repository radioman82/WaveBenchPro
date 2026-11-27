using NAudio.Wave.Asio;
using System.Runtime.InteropServices;
using Xunit;

namespace WellenEditor.Tests;

public sealed class AsioMonitoringTests
{
    [Fact]
    public void MonitorUsesOneRateAndOneBufferForTheSameDriver()
    {
        var input = new AudioDeviceConfiguration { Backend = AudioBackendKind.Asio, DeviceId = "Interface", Channels = [1, 0] };
        var output = input with { SampleRate = 48000, AsioBufferFrames = 256 };

        var session = AsioMonitoringConfiguration.Combine(input, output);
        Assert.Equal(48000, session.SampleRate);
        Assert.Equal(256, session.AsioBufferFrames);
        Assert.Equal(input.Channels, session.Channels);
        Assert.Throws<NotSupportedException>(() => AsioMonitoringConfiguration.Combine(input with { SampleRate = 44100 }, output));
        Assert.Throws<NotSupportedException>(() => AsioMonitoringConfiguration.Combine(input with { AsioBufferFrames = 512 }, output));
        Assert.Throws<NotSupportedException>(() => AsioMonitoringConfiguration.Combine(input, output with { DeviceId = "Other" }));
        Assert.Throws<NotSupportedException>(() => AsioMonitoringConfiguration.Combine(input, output with { Backend = AudioBackendKind.WasapiShared }));
    }

    [Theory]
    [InlineData(AsioSampleType.Int16LSB)]
    [InlineData(AsioSampleType.Int24LSB)]
    [InlineData(AsioSampleType.Int32LSB)]
    [InlineData(AsioSampleType.Float32LSB)]
    public void MonitorWritesOnlyChosenOutputAndSilencesItOnStop(AsioSampleType format)
    {
        const int frames = 3;
        var bytes = frames * NativeAsioCaptureConversion.BytesPerSample(format);
        var samples = new[] { .5f, -2f, float.NaN };
        var native = new[] { Marshal.AllocHGlobal(bytes), Marshal.AllocHGlobal(bytes) };
        try
        {
            var monitor = new AsioInputMonitor(frames, [format, format], [1]);
            monitor.Write(native, samples);
            var buffer = new byte[bytes];
            var output = new float[frames];
            Marshal.Copy(native[0], buffer, 0, bytes);
            Assert.All(buffer, value => Assert.Equal(0, value));
            Marshal.Copy(native[1], buffer, 0, bytes);
            NativeAsioCaptureConversion.Interleave(buffer, output, 0, 1, format);
            Assert.Equal(new[] { .125f, -.25f, 0f }, output);

            monitor.Write(native, []);
            Marshal.Copy(native[1], buffer, 0, bytes);
            Assert.All(buffer, value => Assert.Equal(0, value));
        }
        finally { foreach (var pointer in native) Marshal.FreeHGlobal(pointer); }
    }
}
