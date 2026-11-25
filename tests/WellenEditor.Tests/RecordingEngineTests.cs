using System.IO;
using System.Diagnostics;
using System.Threading;
using NAudio.Wave;
using WellenEditor;
using Xunit;

namespace WellenEditorTests;

public sealed class RecordingEngineTests
{
    [Fact]
    public void DriverFailurePreservesRecordedWaveFile()
    {
        var capture = new FakeCapture(WaveFormat.CreateIeeeFloatWaveFormat(48000, 1));
        using var scope = new RecordingTestScope(capture);
        scope.Engine.Start(null);
        capture.Push(new[] { .5f, -.25f });
        capture.StopWith(new IOException("simulated driver failure"));

        var error = Assert.Throws<InvalidOperationException>(() => scope.Engine.Finish());
        var recovered = AssertRecoveryFile(error.Message);
        Assert.Equal(new[] { .5f, -.25f }, ReadSamples(recovered));
    }

    [Fact]
    public void StopTimeoutPreservesRecordedWaveFile()
    {
        var capture = new FakeCapture(WaveFormat.CreateIeeeFloatWaveFormat(48000, 1)) { RaiseStoppedOnStop = false };
        using var scope = new RecordingTestScope(capture, timeout: TimeSpan.FromMilliseconds(25));
        scope.Engine.Start(null);
        capture.Push(new[] { .125f, -.75f });

        var error = Assert.Throws<TimeoutException>(() => scope.Engine.Finish());
        var recovered = AssertRecoveryFile(error.Message);
        Assert.Equal(new[] { .125f, -.75f }, ReadSamples(recovered));
    }

    [Fact]
    public void UnsupportedCaptureFormatPreservesDataWhenCallbackDecodeFails()
    {
        var capture = new FakeCapture(WaveFormat.CreateALawFormat(8000, 1));
        using var scope = new RecordingTestScope(capture);
        scope.Engine.Start(null);
        capture.Push(new byte[] { 0x11, 0x22, 0x33, 0x44 });

        var error = Assert.Throws<InvalidOperationException>(() => scope.Engine.Finish());
        Assert.IsType<NotSupportedException>(error.InnerException);
        AssertRecoveryFile(error.Message);
    }

    [Fact]
    public void SampleBufferAllocationFailurePreservesRecordedWaveFile()
    {
        var capture = new FakeCapture(WaveFormat.CreateIeeeFloatWaveFormat(48000, 1));
        using var scope = new RecordingTestScope(capture, _ => throw new OutOfMemoryException("simulated sample-buffer exhaustion"));
        scope.Engine.Start(null);
        capture.Push(new[] { .25f, -.5f });
        capture.StopWith(null);

        var error = Assert.Throws<InvalidDataException>(() => scope.Engine.Finish());
        Assert.IsType<OutOfMemoryException>(error.InnerException);
        var recovered = AssertRecoveryFile(error.Message);
        Assert.Equal(new[] { .25f, -.5f }, ReadSamples(recovered));
    }

    [Fact]
    public void CaptureCallbackReturnsWhileInjectedDiskWriterIsBlocked()
    {
        var capture = new FakeCapture(WaveFormat.CreateIeeeFloatWaveFormat(48000, 1));
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var scope = new RecordingTestScope(capture, sinkFactory: (path, format) => new GateRecordingSink(path, format, entered, release));
        scope.Engine.Start(null);
        capture.Push(new[] { .5f, -.25f });
        Assert.True(entered.Wait(TimeSpan.FromSeconds(2)), "injected sink should be blocked inside its first disk write");

        var nextBlock = new byte[sizeof(float) * 2];
        Buffer.BlockCopy(new[] { .125f, -.75f }, 0, nextBlock, 0, nextBlock.Length);
        var allocationBefore = GC.GetAllocatedBytesForCurrentThread();
        var stopwatch = Stopwatch.StartNew();
        capture.Push(nextBlock);
        stopwatch.Stop();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocationBefore;
        Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(100), "capture callback should not wait for the blocked writer");
        Assert.True(allocated < 8192, $"capture callback allocated {allocated} bytes");
        Console.WriteLine($"Capture callback with blocked writer: {stopwatch.Elapsed.TotalMilliseconds:F2} ms, {allocated} bytes on the caller thread.");
        release.Set();
        capture.StopWith(null);

        var samples = scope.Engine.Finish()!.Value.Samples;
        Assert.Equal(new[] { .5f, -.25f, .125f, -.75f }, samples);
    }

    [Fact]
    public void FullCaptureQueueStopsAndPreservesARecoverablePartialWave()
    {
        var capture = new FakeCapture(WaveFormat.CreateIeeeFloatWaveFormat(48000, 1));
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var scope = new RecordingTestScope(capture, timeout: TimeSpan.FromSeconds(2), sinkFactory: (path, format) => new GateRecordingSink(path, format, entered, release));
        scope.Engine.Start(null);
        capture.Push(new[] { .1f, .1f });
        Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
        for (var block = 0; block < 8; block++) capture.Push(new[] { .2f, .2f });
        capture.Push(new[] { .3f, .3f }); // ninth queued block exceeds the bounded queue
        release.Set();

        var error = Assert.Throws<InvalidOperationException>(() => scope.Engine.Finish());
        Assert.Contains("Puffer ist voll", error.InnerException!.Message);
        var recovery = AssertRecoveryFile(error.Message);
        Assert.Equal(18, ReadSamples(recovery).Length);
    }

    private static string AssertRecoveryFile(string message)
    {
        const string marker = "gesichert unter: ";
        var index = message.LastIndexOf(marker, StringComparison.Ordinal);
        Assert.True(index >= 0, $"Recovery path missing: {message}");
        var path = message[(index + marker.Length)..].Trim();
        Assert.True(File.Exists(path), $"Recovery file missing: {path}");
        return path;
    }

    private static float[] ReadSamples(string path)
    {
        using var reader = new WaveFileReader(path);
        var bytes = new byte[checked((int)reader.Length)];
        var count = reader.Read(bytes, 0, bytes.Length);
        return AudioFormatConverter.Decode(bytes, count, reader.WaveFormat);
    }

    private sealed class RecordingTestScope : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), $"wavebench_recording_test_{Guid.NewGuid():N}");
        private readonly string recovery;
        public RecordingEngine Engine { get; }

        public RecordingTestScope(FakeCapture capture, Func<int, float[]>? allocator = null, TimeSpan? timeout = null, Func<string, WaveFormat, IRecordingSink>? sinkFactory = null)
        {
            recovery = Path.Combine(root, "Recovered");
            Engine = new RecordingEngine(_ => capture, recovery, allocator ?? (length => new float[length]), timeout, sinkFactory);
        }

        public void Dispose()
        {
            Engine.Dispose();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed class GateRecordingSink : IRecordingSink
    {
        private readonly WaveFileWriter writer;
        private readonly ManualResetEventSlim entered;
        private readonly ManualResetEventSlim release;
        private int hasWaited;

        public GateRecordingSink(string path, WaveFormat format, ManualResetEventSlim entered, ManualResetEventSlim release)
        {
            writer = new WaveFileWriter(path, format);
            this.entered = entered;
            this.release = release;
        }

        public void Write(byte[] buffer, int offset, int count)
        {
            if (Interlocked.Exchange(ref hasWaited, 1) == 0)
            {
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("test sink gate was not released");
            }
            writer.Write(buffer, offset, count);
        }

        public void Dispose() => writer.Dispose();
    }

    private sealed class FakeCapture : IRecordingCapture
    {
        public FakeCapture(WaveFormat waveFormat) => WaveFormat = waveFormat;
        public WaveFormat WaveFormat { get; }
        public bool RaiseStoppedOnStop { get; init; } = true;
        public event EventHandler<WaveInEventArgs>? DataAvailable;
        public event EventHandler<StoppedEventArgs>? RecordingStopped;
        public void StartRecording() { }
        public void StopRecording()
        {
            if (RaiseStoppedOnStop) StopWith(null);
        }
        public void Push(float[] samples) => Push(samples.SelectMany(BitConverter.GetBytes).ToArray());
        public void Push(byte[] bytes) => DataAvailable?.Invoke(this, new WaveInEventArgs(bytes, bytes.Length));
        public void StopWith(Exception? error) => RecordingStopped?.Invoke(this, new StoppedEventArgs(error));
        public void Dispose() { }
    }
}
