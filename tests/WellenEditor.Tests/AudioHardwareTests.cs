using System.IO;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using WellenEditor;
using Xunit;

namespace WellenEditorTests;

public sealed class AudioHardwareTests
{
    [Fact]
    public async Task AsioControlCallsKeepTheirStaThreadAcrossCallerThreads()
    {
        using var control = new AudioControlThread();
        var first = control.Invoke(() => (Environment.CurrentManagedThreadId, Thread.CurrentThread.GetApartmentState()));
        var second = await Task.Run(() => control.Invoke(() => (Environment.CurrentManagedThreadId, Thread.CurrentThread.GetApartmentState())));
        Assert.Equal(first, second); Assert.Equal(ApartmentState.STA, first.Item2);
        Assert.NotEqual(Environment.CurrentManagedThreadId, first.Item1);
    }
    [Fact]
    public void IndependentInputOutputConfigurationRoundTripsWithoutDeviceFallback()
    {
        var path = Path.Combine(Path.GetTempPath(), "WaveBenchHardwareTest_" + Guid.NewGuid().ToString("N"), "audio.json");
        var config = new AudioHardwareConfiguration
        {
            Output = new() { Backend = AudioBackendKind.WasapiExclusive, DeviceId = "missing-output", Encoding = AudioSampleEncoding.Pcm24, BufferMilliseconds = 20 },
            Input = new() { Backend = AudioBackendKind.Asio, DeviceId = "driver-name", Channels = [2, 3], AsioBufferFrames = 256, SampleRate = 48000 }
        };
        try
        {
            AudioHardwareSettingsStore.Save(path, config);
            var loaded = AudioHardwareSettingsStore.Load(path);
            Assert.Equal("missing-output", loaded.Output.DeviceId);
            Assert.Equal(AudioBackendKind.WasapiExclusive, loaded.Output.Backend);
            Assert.Equal(AudioSampleEncoding.Pcm24, loaded.Output.Encoding);
            Assert.Equal("driver-name", loaded.Input.DeviceId);
            Assert.Equal(new[] { 2, 3 }, loaded.Input.Channels);
            Assert.Equal(256, loaded.Input.AsioBufferFrames);
        }
        finally { if (File.Exists(path)) File.Delete(path); Directory.Delete(Path.GetDirectoryName(path)!); }
    }
    [Fact]
    public void InvalidConfigurationDoesNotReplacePreviousSettings()
    {
        var path = Path.Combine(Path.GetTempPath(), "WaveBenchHardwareTest_" + Guid.NewGuid().ToString("N"), "audio.json");
        try
        {
            AudioHardwareSettingsStore.Save(path, new());
            var before = File.ReadAllBytes(path);
            Assert.Throws<ArgumentException>(() => AudioHardwareSettingsStore.Save(path, new() { Output = new() { Channels = [0, 0] } }));
            Assert.Equal(before, File.ReadAllBytes(path));
        }
        finally { if (File.Exists(path)) File.Delete(path); Directory.Delete(Path.GetDirectoryName(path)!); }
    }
    [Fact]
    public void ExclusiveCaptureAndAsioWithoutNamedDriverAreRejected()
    {
        Assert.Throws<NotSupportedException>(() => new AudioDeviceConfiguration { Backend = AudioBackendKind.WasapiExclusive }.Validate(AudioDirection.Input));
        Assert.Throws<ArgumentException>(() => new AudioDeviceConfiguration { Backend = AudioBackendKind.Asio }.Validate(AudioDirection.Output));
        Assert.Equal(AudioBackendKind.WasapiShared, new AudioHardwareConfiguration().Output.Backend);
    }
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void LossOrFormatChangeStopsOnlyBoundEndpoint(int value)
    {
        var kind = (AudioDeviceChangeKind)value;
        var status = new AudioStreamStatus("bound", "test", AudioBackendKind.WasapiShared, WaveFormat.CreateIeeeFloatWaveFormat(48000, 2), 50, 50);
        Assert.True(AudioDeviceChangePolicy.RequiresStop(new("bound", kind), new() { DeviceId = "bound" }, status, AudioDirection.Input));
        Assert.False(AudioDeviceChangePolicy.RequiresStop(new("other", kind), new() { DeviceId = "bound" }, status, AudioDirection.Input));
    }
    [Fact]
    public void DefaultDeviceChangeDoesNotReroutePinnedRecording()
    {
        var status = new AudioStreamStatus("bound", "test", AudioBackendKind.WasapiShared, WaveFormat.CreateIeeeFloatWaveFormat(48000, 2), 50, 50);
        var change = new AudioDeviceChange("new-default", AudioDeviceChangeKind.DefaultChanged, DataFlow.Capture);
        Assert.False(AudioDeviceChangePolicy.RequiresStop(change, new() { DeviceId = "bound" }, status, AudioDirection.Input));
        Assert.True(AudioDeviceChangePolicy.RequiresStop(change, new(), status, AudioDirection.Input));
        Assert.False(AudioDeviceChangePolicy.RequiresStop(change, new(), status, AudioDirection.Output));
    }
    [Fact]
    public void CaptureQueuePreservesAllBytesAndRejectsOverflowWithoutAllocating()
    {
        using var queue = new CapturePacketQueue(2, 64);
        var input = Enumerable.Range(0, 64).Select(i => (byte)i).ToArray();
        queue.TryWrite(input);
        Assert.True(queue.TryRead(out var first));
        var before = GC.GetAllocatedBytesForCurrentThread();
        var second = queue.TryWrite(input);
        var third = queue.TryWrite(input);
        var overflow = queue.TryWrite(input);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(second && third); Assert.False(overflow); Assert.Equal(0, allocated);
        Assert.Equal(input, first!.Buffer);
        queue.ReleaseRead();
        Assert.True(queue.TryRead(out var next)); Assert.Equal(input, next!.Buffer); queue.ReleaseRead();
        queue.Complete(); Assert.True(queue.TryRead(out _)); queue.ReleaseRead(); Assert.False(queue.TryRead(out _));
    }
    [Theory]
    [InlineData(AudioSampleEncoding.Pcm16, 2)]
    [InlineData(AudioSampleEncoding.Pcm24, 3)]
    [InlineData(AudioSampleEncoding.Pcm32, 4)]
    public void ExplicitPcmConversionHasCorrectBoundsAndNoReadAllocations(AudioSampleEncoding encoding, int bytes)
    {
        var input = new RepeatingFloatProvider(new[] { -2f, -1f, 0f, 1f, 2f, float.NaN });
        var converted = new AudioOutputConversion(input, encoding);
        var target = new byte[6 * bytes];
        converted.Read(target);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var read = converted.Read(target);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(target.Length, read); Assert.Equal(0, allocated);
        var actual = AudioFormatConverter.Decode(target, read, converted.WaveFormat);
        Assert.Equal(-1f, actual[0]); Assert.Equal(-1f, actual[1]); Assert.Equal(0f, actual[2]);
        Assert.InRange(actual[3], .9999f, 1f); Assert.InRange(actual[4], .9999f, 1f); Assert.Equal(0f, actual[5]);
    }
    [Fact]
    public async Task HardwareReadAheadDoesNotRunSourceOnCallbackOrReleaseBlockedSource()
    {
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var source = new BlockedSource(entered, release);
        var buffered = new HardwareReadAhead(source);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
            var output = new byte[128];
            var before = GC.GetAllocatedBytesForCurrentThread();
            var count = buffered.Read(output);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(128, count); Assert.Equal(0, allocated); Assert.Equal(new byte[128], output);
            Assert.NotEqual(Environment.CurrentManagedThreadId, source.ReadThread);
            Assert.False(await buffered.StopAsync(TimeSpan.FromMilliseconds(20)));
            Assert.False(buffered.Completion.IsCompleted);
        }
        finally { release.Set(); await buffered.Completion.WaitAsync(TimeSpan.FromSeconds(2)); buffered.DrainAndDispose(); }
    }
    [Fact]
    public async Task HardwareReadAheadPreservesEndOfStreamWithoutExtraSamples()
    {
        var samples = new[] { .25f, -.5f, .125f, -.75f };
        var provider = new ArrayWaveProvider(samples, 0, samples.Length, WaveFormat.CreateIeeeFloatWaveFormat(48000, 1));
        var buffered = new HardwareReadAhead(provider);
        try
        {
            await buffered.PrefillAsync(TimeSpan.FromSeconds(2));
            var bytes = new byte[128]; var read = buffered.Read(bytes);
            Assert.Equal(samples, MemoryMarshal.Cast<byte, float>(bytes.AsSpan(0, read)).ToArray());
            Assert.Equal(0, buffered.Read(bytes)); Assert.Equal(0, buffered.Underruns);
        }
        finally { buffered.DrainAndDispose(); }
    }
    private sealed class RepeatingFloatProvider(float[] values) : IWaveProvider
    {
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 1);
        public int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public int Read(Span<byte> buffer)
        {
            var samples = MemoryMarshal.Cast<byte, float>(buffer);
            for (var i = 0; i < samples.Length; i++) samples[i] = values[i % values.Length];
            return samples.Length * 4;
        }
    }
    private sealed class BlockedSource(ManualResetEventSlim entered, ManualResetEventSlim release) : IWaveProvider
    {
        public int ReadThread;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        public int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public int Read(Span<byte> buffer) { ReadThread = Environment.CurrentManagedThreadId; entered.Set(); release.Wait(); return 0; }
    }
}
