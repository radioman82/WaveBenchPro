using System.IO;
using NAudio.Wave;
using WellenEditor;
using Xunit;

namespace WellenEditorTests;

public sealed class SpanRecordingTests
{
    [Fact]
    public void FailedNativeStartAfterFirstPacketPreservesThatPacket()
    {
        var root = Path.Combine(Path.GetTempPath(), "WaveBenchSpanTest_" + Guid.NewGuid().ToString("N"));
        var capture = new SpanCapture { FailStartAfterPacket = true };
        using var engine = new RecordingEngine(_ => capture, root, count => new float[count]);
        try
        {
            var error = Assert.Throws<InvalidOperationException>(() => engine.Start(null));
            Assert.Contains("gesichert unter", error.Message); Assert.False(engine.IsRecording);
            var file = Assert.Single(Directory.GetFiles(root, "*.wav"));
            using var reader = new WaveFileReader(file); Assert.Equal(8, reader.Length);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Fact]
    public void NativeSpanCaptureCopiesBeforeCallbackReturnsAndAllocatesNothing()
    {
        var root = Path.Combine(Path.GetTempPath(), "WaveBenchSpanTest_" + Guid.NewGuid().ToString("N"));
        var capture = new SpanCapture();
        using var engine = new RecordingEngine(_ => capture, root, count => new float[count]);
        var bytes = new[] { .25f, -.5f }.SelectMany(BitConverter.GetBytes).ToArray();
        try
        {
            engine.Start(null);
            var before = GC.GetAllocatedBytesForCurrentThread();
            capture.Push(bytes);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Array.Clear(bytes); // The driver may immediately reuse its native buffer.
            var result = engine.Finish()!.Value;
            Assert.Equal(0, allocated); Assert.Equal(new[] { .25f, -.5f }, result.Samples);
            Assert.Equal(8, engine.ReceivedBytes); Assert.Equal(8, engine.WrittenBytes);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Fact]
    public void DeviceLossPreservesPartialCaptureInsteadOfRerouting()
    {
        var root = Path.Combine(Path.GetTempPath(), "WaveBenchSpanTest_" + Guid.NewGuid().ToString("N"));
        var capture = new SpanCapture();
        using var engine = new RecordingEngine(_ => capture, root, count => new float[count]);
        try
        {
            engine.Start(null); capture.Push(new[] { .25f, -.5f }.SelectMany(BitConverter.GetBytes).ToArray());
            engine.DeviceLost(new IOException("device removed"));
            var error = Assert.Throws<InvalidOperationException>(() => engine.Finish());
            Assert.Contains("device removed", error.InnerException!.Message);
            var file = Assert.Single(Directory.GetFiles(root, "*.wav"));
            using var reader = new WaveFileReader(file); Assert.Equal(8, reader.Length);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Fact]
    public void DiscontinuousNativePacketStopsAndPreservesEarlierPackets()
    {
        var root = Path.Combine(Path.GetTempPath(), "WaveBenchSpanTest_" + Guid.NewGuid().ToString("N"));
        var capture = new SpanCapture();
        using var engine = new RecordingEngine(_ => capture, root, count => new float[count]);
        try
        {
            engine.Start(null); capture.Push(new[] { .25f, -.5f }.SelectMany(BitConverter.GetBytes).ToArray());
            capture.Push(new byte[8], true);
            var error = Assert.Throws<InvalidOperationException>(() => engine.Finish());
            Assert.Contains("Aufnahmelücke", error.InnerException!.Message);
            Assert.Single(Directory.GetFiles(root, "*.wav"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private sealed class SpanCapture : ISpanRecordingCapture
    {
        public bool FailStartAfterPacket;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 1);
        public AudioStreamStatus Status => new("fixed", "fake", AudioBackendKind.WasapiShared, WaveFormat, 50, 50);
        public event CapturePacketHandler? PacketAvailable;
        public event EventHandler<WaveInEventArgs>? DataAvailable { add => throw new InvalidOperationException("Legacy allocation path must not be used"); remove { } }
        public event EventHandler<StoppedEventArgs>? RecordingStopped;
        public void Push(ReadOnlySpan<byte> bytes, bool discontinuity = false) => PacketAvailable?.Invoke(bytes, discontinuity, 0);
        public void StartRecording()
        {
            if (!FailStartAfterPacket) return;
            Push(new byte[8]); throw new IOException("late native start failure");
        }
        public void StopRecording() => RecordingStopped?.Invoke(this, new StoppedEventArgs(null));
        public void Dispose() { }
    }
}
