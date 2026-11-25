using System.Runtime.InteropServices;
using WellenEditor;
using Xunit;

namespace WellenEditorTests;

public sealed class BufferedBlockWaveProviderTests
{
    [Fact]
    public async Task CallbackReturnsSilenceAndCountsUnderrunWhileProducerIsBlocked()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var fake = new GateRenderer(entered, release);
        using var provider = new BufferedBlockWaveProvider(fake, 2);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
        var buffer = new byte[4 * 2 * sizeof(float)];
        try
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            var read = provider.Read(buffer);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(buffer.Length, read);
            Assert.Equal(0, allocated);
            Assert.Equal(1, provider.UnderrunCount);
            Assert.All(buffer, value => Assert.Equal(0, value));
            Assert.Equal(0, provider.AudioFramesRead);
        }
        finally { release.Set(); }

        await provider.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Null(provider.WorkerError);
        Assert.True(fake.Disposed);
        Assert.Equal(buffer.Length, provider.Read(buffer));
        Assert.Equal(new[] { .1f, -.1f, .2f, -.2f, .3f, -.3f, .4f, -.4f }, MemoryMarshal.Cast<byte, float>(buffer).ToArray());
        Assert.Equal(4, provider.AudioFramesRead);
        Assert.Equal(0, provider.Read(buffer));
    }

    [Fact]
    public async Task BufferedPlaybackMatchesOfflineRendererAndEnds()
    {
        using var sound = AudioDocument.FromSamples(Enumerable.Range(0, 4000).Select(i => .2f * MathF.Sin(i * .1f)).ToArray(), 1, 48000);
        var project = new MultitrackProject(); project.Add(sound);
        var expected = project.RenderMixdown();
        using var provider = new BufferedBlockWaveProvider(new BlockMixdownRenderer(project, 4096), 2);
        await provider.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        var bytes = new byte[expected.Length * sizeof(float)];
        Assert.Equal(bytes.Length, provider.Read(bytes));
        Assert.Equal(expected, MemoryMarshal.Cast<byte, float>(bytes).ToArray());
        Assert.Equal(0, provider.Read(bytes));
        Assert.Equal(0, provider.UnderrunCount);
    }

    [Fact]
    public async Task DisposingAFullQueueStopsTheWorkerAndReleasesItsRenderer()
    {
        var fake = new RepeatingRenderer();
        var provider = new BufferedBlockWaveProvider(fake, 2);
        Assert.True(fake.TwoReads.Wait(TimeSpan.FromSeconds(2)));
        provider.Dispose();
        provider.Dispose();
        await provider.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(fake.Disposed);
    }

    [Fact]
    public async Task WorkerFailureIsExposedWithoutBlockingTheCallback()
    {
        using var provider = new BufferedBlockWaveProvider(new FailingRenderer());
        await provider.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsType<InvalidOperationException>(provider.WorkerError);
        Assert.Equal(0, provider.Read(new byte[32]));
    }

    private sealed class FailingRenderer : IBlockAudioRenderer
    {
        public int SampleRate => 48000;
        public int BlockFrames => 4;
        public int Read(float[] output, int frames) => throw new InvalidOperationException("simulated renderer failure");
        public void Dispose() { }
    }

    private sealed class RepeatingRenderer : IBlockAudioRenderer
    {
        private int reads;
        public ManualResetEventSlim TwoReads { get; } = new();
        public int SampleRate => 48000;
        public int BlockFrames => 4;
        public bool Disposed { get; private set; }
        public int Read(float[] output, int frames)
        {
            if (Interlocked.Increment(ref reads) == 2) TwoReads.Set();
            return frames;
        }
        public void Dispose() => Disposed = true;
    }

    private sealed class GateRenderer(ManualResetEventSlim entered, ManualResetEventSlim release) : IBlockAudioRenderer
    {
        private bool done;
        public int SampleRate => 48000;
        public int BlockFrames => 4;
        public bool Disposed { get; private set; }
        public int Read(float[] output, int frames)
        {
            if (done) return 0;
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
            new[] { .1f, -.1f, .2f, -.2f, .3f, -.3f, .4f, -.4f }.CopyTo(output, 0);
            done = true;
            return 4;
        }
        public void Dispose() => Disposed = true;
    }
}
