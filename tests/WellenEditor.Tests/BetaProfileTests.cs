#if BETA_PROFILE
using Xunit;

namespace WellenEditor.Tests;

public sealed class BetaProfileTests
{
    [Theory]
    [InlineData("test.mp3")]
    [InlineData("test.flac")]
    [InlineData("test.ogg")]
    [InlineData("test.opus")]
    public void UnsupportedFormatsFailBeforeDecoderLoading(string path)
    {
        var error = Assert.Throws<NotSupportedException>(() => AudioDocument.Load(path));
        Assert.Contains("WAV", error.Message);
    }

    [Fact]
    public void PersistedAsioConfigurationsCannotStartCaptureOrPlayback()
    {
        var configuration = new AudioDeviceConfiguration { Backend = AudioBackendKind.Asio, DeviceId = "test" };
        var backend = new AudioHardwareBackend();
        Assert.Throws<NotSupportedException>(() => backend.CreatePlayback(configuration));
        Assert.Throws<NotSupportedException>(() => backend.CreateCapture(configuration));
    }
}
#endif
