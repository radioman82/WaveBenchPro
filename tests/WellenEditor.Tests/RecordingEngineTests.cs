using System.IO;
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

        public RecordingTestScope(FakeCapture capture, Func<int, float[]>? allocator = null, TimeSpan? timeout = null)
        {
            recovery = Path.Combine(root, "Recovered");
            Engine = new RecordingEngine(_ => capture, recovery, allocator ?? (length => new float[length]), timeout);
        }

        public void Dispose()
        {
            Engine.Dispose();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
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
