using NAudio.Wave;
using WellenEditor;
using Xunit;

namespace WellenEditorTests;

public sealed class AudioPlaybackLifetimeTests
{
    [Fact]
    public void FailedNativeReleaseRetainsSourceAndBlocksRestartUntilActualRelease()
    {
        var player = new FakePlayer { FailDispose = true }; var source = new SourceLifetime();
        var lifetime = new AudioPlaybackLifetime();
        Assert.NotNull(lifetime.Retire(player, source));
        Assert.True(lifetime.HasPending); Assert.False(source.Disposed);
        Assert.Throws<InvalidOperationException>(lifetime.EnsureReady);
        player.FailDispose = false;
        Assert.Null(lifetime.Retry()); Assert.False(lifetime.HasPending); Assert.True(source.Disposed);
        lifetime.EnsureReady();
    }
    [Fact]
    public void StopErrorIsReportedButSuccessfulNativeDisposeAllowsSourceRelease()
    {
        var player = new FakePlayer { FailStop = true }; var source = new SourceLifetime();
        var lifetime = new AudioPlaybackLifetime();
        Assert.NotNull(lifetime.Retire(player, source));
        Assert.False(lifetime.HasPending); Assert.True(source.Disposed);
    }
    [Fact]
    public void SourceCleanupFailureDoesNotReopenOrDisposeNativeDeviceAgain()
    {
        var player = new FakePlayer(); var source = new SourceLifetime { Fail = true };
        var lifetime = new AudioPlaybackLifetime();
        Assert.NotNull(lifetime.Retire(player, source)); Assert.True(lifetime.HasPending);
        source.Fail = false; Assert.Null(lifetime.Retry());
        Assert.Equal(1, player.Disposals); Assert.True(source.Disposed);
    }
    private sealed class SourceLifetime : IDisposable
    {
        public bool Fail, Disposed;
        public void Dispose() { if (Fail) throw new InvalidOperationException("source cleanup"); Disposed = true; }
    }
    private sealed class FakePlayer : IWavePlayer
    {
        public bool FailStop, FailDispose;
        public int Disposals;
        public PlaybackState PlaybackState => PlaybackState.Playing;
        public WaveFormat OutputWaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        public float Volume { get; set; }
        public event EventHandler<StoppedEventArgs>? PlaybackStopped { add { } remove { } }
        public void Init(IWaveProvider source) { }
        public void Play() { }
        public void Pause() { }
        public void Stop() { if (FailStop) throw new InvalidOperationException("driver stop"); }
        public void Dispose() { Disposals++; if (FailDispose) throw new InvalidOperationException("driver dispose"); }
    }
}
