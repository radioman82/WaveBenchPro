using System.IO;
using WellenEditor;
using Xunit;

namespace WellenEditorTests;

public sealed class PreparedPlaybackLifetimeTests
{
    [Fact]
    public async Task TimedOutUnpublishedProducerPreventsAdditionalGenerationsUntilItFinishes()
    {
        using var source = new ControlledSession();
        using var provider = NewProvider(source);
        await Assert.ThrowsAsync<TimeoutException>(() => provider.ConfigurePlaybackAsync(100, 1000, true));
        Assert.Equal(100, provider.AudibleFrame(0));
        await Assert.ThrowsAsync<TimeoutException>(() => provider.ConfigurePlaybackAsync(200, 1000, true));
        Assert.Equal(1, source.Opens);
        Assert.Equal(1, source.ActiveReaders);
        source.ReleaseRead.Set();
        await source.FirstReaderDisposed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await provider.ConfigurePlaybackAsync(300, 1000, true);
        Assert.Equal(2, source.Opens);
        Assert.Equal(300, provider.AudibleFrame(0));
        Assert.True(await provider.StopAsync(TimeSpan.FromSeconds(2)));
        Assert.True(source.Completion.IsCompletedSuccessfully);
        Assert.Equal(0, source.ActiveReaders);
    }

    [Fact]
    public async Task StopDuringPrefillWaitsForLateWorkerAndSessionLeaseCleanup()
    {
        using var source = new ControlledSession { BlockCleanup = true };
        using var provider = NewProvider(source, TimeSpan.FromSeconds(2));
        var preparation = provider.ConfigurePlaybackAsync(100, 1000, true);
        await source.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(await provider.StopAsync(TimeSpan.FromMilliseconds(50)));
        Assert.False(provider.Completion.IsCompleted);
        Assert.Equal(1, source.ActiveReaders);
        source.ReleaseRead.Set();
        await preparation.WaitAsync(TimeSpan.FromSeconds(2));
        await source.CleanupEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, source.ActiveReaders);
        Assert.False(await provider.StopAsync(TimeSpan.FromMilliseconds(30)));
        source.ReleaseCleanup.Set();
        Assert.True(await provider.StopAsync(TimeSpan.FromSeconds(2)));
        Assert.True(source.Completion.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task SupersededPrefillCannotLeakWorkersOrPublishItsOldPosition()
    {
        using var source = new ControlledSession();
        using var provider = NewProvider(source, TimeSpan.FromSeconds(2));
        var first = provider.ConfigurePlaybackAsync(100, 1000, true);
        await source.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = provider.ConfigurePlaybackAsync(700, 1000, true);
        Assert.Equal(700, provider.AudibleFrame(0));
        await first.WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAsync<TimeoutException>(() => second);
        Assert.Equal(1, source.Opens);
        source.ReleaseRead.Set();
        Assert.True(await provider.StopAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(1, source.Opens);
    }

    [Fact]
    public async Task LeaseCleanupFailureIsAnErrorRatherThanAStopTimeout()
    {
        using var source = new ControlledSession { FailCleanup = true };
        source.ReleaseRead.Set();
        using var provider = NewProvider(source);
        await provider.ConfigurePlaybackAsync(0, 1000, true);
        var error = await Assert.ThrowsAsync<AggregateException>(() => provider.StopAsync(TimeSpan.FromSeconds(2)));
        Assert.Contains("lease", error.ToString());
        Assert.NotNull(provider.WorkerError);
        Assert.True(provider.Completion.IsFaulted);
    }

    private static PreparedMultitrackPlayback NewProvider(ControlledSession source, TimeSpan? prefill = null)
        => new(source, 16, 2, prefill ?? TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(30));

    private sealed class ControlledSession : IPreparedPlaybackSession
    {
        private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int readers, opens, closing;
        public int SampleRate => 48000;
        public long FrameCount => 4096;
        public Task Completion => completion.Task;
        public int Opens => Volatile.Read(ref opens);
        public int ActiveReaders => Volatile.Read(ref readers);
        public bool BlockCleanup, FailCleanup;
        public ManualResetEventSlim ReleaseRead { get; } = new();
        public ManualResetEventSlim ReleaseCleanup { get; } = new();
        public TaskCompletionSource ReadEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FirstReaderDisposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CleanupEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IBlockAudioRenderer OpenRenderer(long start, long count, bool loop, int frames, long? initialFrame = null)
        {
            Interlocked.Increment(ref readers);
            var index = Interlocked.Increment(ref opens);
            return new Reader(this, index, frames, initialFrame ?? start);
        }
        private sealed class Reader(ControlledSession owner, int index, int frames, long start) : IBlockAudioRenderer, IPositionedBlockRenderer
        {
            public int SampleRate => owner.SampleRate;
            public int BlockFrames => frames;
            public long Position { get; private set; } = start;
            public int Read(float[] output, int count)
            {
                if (index == 1)
                {
                    owner.ReadEntered.TrySetResult();
                    if (!owner.ReleaseRead.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("test reader not released");
                }
                Array.Fill(output, .25f);
                Position += count;
                return count;
            }
            public void Dispose()
            {
                Interlocked.Decrement(ref owner.readers);
                owner.FirstReaderDisposed.TrySetResult();
            }
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref closing, 1) != 0) return;
            CleanupEntered.TrySetResult();
            if (BlockCleanup && !ReleaseCleanup.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("test cleanup not released");
            if (FailCleanup)
            {
                var error = new IOException("injected lease cleanup failure");
                completion.TrySetException(error); throw error;
            }
            completion.TrySetResult();
        }
    }
}
