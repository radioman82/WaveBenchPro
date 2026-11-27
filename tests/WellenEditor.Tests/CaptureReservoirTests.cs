using System.IO;
using NAudio.Wave;
using Xunit;

namespace WellenEditor.Tests;

public sealed class CaptureReservoirTests
{
    [Theory]
    [InlineData(44100, 256, 2, 112, 2048)]
    [InlineData(48000, 256, 2, 122, 2048)]
    [InlineData(192000, 32, 2, 256, 256)]
    [InlineData(48000, 65536, 64, 0, 0)]
    public void ReservoirUsesActualPacketSizeAndStaysWithinMemoryBudget(int rate, int frames, int channels, int packets, int bytes)
    {
        var format = WaveFormat.CreateIeeeFloatWaveFormat(rate, channels);
        if (packets == 0)
        {
            Assert.Throws<NotSupportedException>(() => CaptureQueueLayout.Create(format, frames));
            return;
        }
        var result = CaptureQueueLayout.Create(format, frames);
        Assert.Equal(new CaptureQueueLayout(packets, bytes), result);
        Assert.True((long)(result.WaitingPackets + 1) * result.PacketBytes <= 16 * 1024 * 1024);
    }

    [Theory]
    [InlineData(64)]
    [InlineData(128)]
    [InlineData(256)]
    public void FixedPacketReservoirAbsorbsAStalledWriterAndRetainsTheAcceptedPackets(int capacity)
    {
        var path = Path.Combine(Path.GetTempPath(), "WaveBenchReservoirTest_" + Guid.NewGuid().ToString("N"));
        var capture = new FixedCapture();
        using var enteredWrite = new ManualResetEventSlim();
        using var resumeWrite = new ManualResetEventSlim();
        using var engine = new RecordingEngine(_ => capture, path, count => new float[count],
            sinkFactory: (file, format) => new BlockingSink(file, format, enteredWrite, resumeWrite),
            captureQueueCandidate: capacity);
        try
        {
            engine.Start(null);
            var packet = new byte[256 * 8];
            capture.Push(packet);
            Assert.True(enteredWrite.Wait(TimeSpan.FromSeconds(5)));
            for (var i = 0; i < capacity; i++) capture.Push(packet);
            Assert.Equal(capacity + 1, engine.PeakQueueSlots);
            Assert.Equal(0, engine.OverflowPackets);
            capture.Push(packet);
            Assert.Equal(1, engine.OverflowPackets);
            resumeWrite.Set();
            Assert.Throws<InvalidOperationException>(() => engine.FinishToSink());
            Assert.Equal((capacity + 1L) * packet.Length, engine.WrittenBytes);
            using var recovered = new WaveFileReader(Assert.Single(Directory.GetFiles(path, "*.wav")));
            Assert.Equal(engine.WrittenBytes, recovered.Length);
        }
        finally
        {
            resumeWrite.Set();
            engine.Dispose();
            if (Directory.Exists(path))
            {
                foreach (var file in Directory.GetFiles(path)) File.Delete(file);
                Directory.Delete(path);
            }
        }
    }

    private sealed class BlockingSink(string path, WaveFormat format, ManualResetEventSlim entered, ManualResetEventSlim resume) : IRecordingSink
    {
        private readonly WaveFileRecordingSink sink = new(path, format);
        public void Write(byte[] buffer, int offset, int count)
        {
            entered.Set();
            if (!resume.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
            sink.Write(buffer, offset, count);
        }
        public void Dispose() => sink.Dispose();
    }

    private sealed class FixedCapture : ISpanRecordingCapture, IFixedPacketRecordingCapture
    {
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        public int PacketFrames => 256;
        public AudioStreamStatus Status => new("test", "test", AudioBackendKind.Asio, WaveFormat, 0, 5.33, PacketFrames);
        public event CapturePacketHandler? PacketAvailable;
        public event EventHandler<WaveInEventArgs>? DataAvailable { add { } remove { } }
        public event EventHandler<StoppedEventArgs>? RecordingStopped;
        public void StartRecording() { }
        public void StopRecording() => RecordingStopped?.Invoke(this, new StoppedEventArgs());
        public void Push(ReadOnlySpan<byte> packet) => PacketAvailable?.Invoke(packet, false, 0);
        public void Dispose() { }
    }
}
