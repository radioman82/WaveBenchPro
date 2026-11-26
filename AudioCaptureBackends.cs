using System.IO;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace WellenEditor;

internal sealed class WasapiRecordingCapture : ISpanRecordingCapture
{
    private readonly MMDevice device;
    private readonly WasapiRecorder recorder;
    private readonly AudioDeviceConfiguration configuration;
    private readonly string deviceId, deviceName;
    private bool nativeDisposed;
    public WasapiRecordingCapture(AudioDeviceConfiguration configuration)
    {
        this.configuration = configuration;
        device = AudioHardwareBackend.Resolve(configuration, AudioDirection.Input);
        deviceId = device.ID; deviceName = device.FriendlyName;
        try
        {
            var builder = new WasapiRecorderBuilder().WithDevice(device).WithSharedMode()
                .WithBufferLength(configuration.BufferMilliseconds).WithEventSync().WithMmcssThreadPriority("Pro Audio");
            if (configuration.Backend == AudioBackendKind.WasapiLoopback) builder.WithLoopbackCapture();
            if (configuration.SampleRate != 0)
            {
                using var client = device.CreateAudioClient();
                builder.WithFormat(WaveFormat.CreateIeeeFloatWaveFormat(configuration.SampleRate,
                    client.MixFormat.Channels));
            }
            recorder = builder.Build();
            recorder.DataAvailable += OnData;
            recorder.RecordingStopped += OnStopped;
        }
        catch { device.Dispose(); throw; }
    }
    public WaveFormat WaveFormat => recorder.WaveFormat;
    public AudioStreamStatus Status => new(deviceId, deviceName, configuration.Backend, recorder.WaveFormat,
        configuration.BufferMilliseconds, recorder.LatencyMilliseconds);
    public event CapturePacketHandler? PacketAvailable;
    // The legacy event is retained only for injected test doubles. Production uses spans.
    public event EventHandler<WaveInEventArgs>? DataAvailable { add => throw new NotSupportedException("Span-Capture verwenden."); remove { } }
    public event EventHandler<StoppedEventArgs>? RecordingStopped;
    private void OnData(ReadOnlySpan<byte> bytes, AudioClientBufferFlags flags, long position, long qpc) =>
        PacketAvailable?.Invoke(bytes, (flags & AudioClientBufferFlags.DataDiscontinuity) != 0, position);
    private void OnStopped(object? sender, StoppedEventArgs args) => RecordingStopped?.Invoke(this, args);
    public void StartRecording() => recorder.StartRecording();
    public void StopRecording() => recorder.StopRecording();
    public void Dispose()
    {
        if (!nativeDisposed)
        {
            recorder.Dispose(); nativeDisposed = true;
            recorder.DataAvailable -= OnData; recorder.RecordingStopped -= OnStopped;
        }
        device.Dispose();
    }
}

internal sealed class AsioRecordingCapture : ISpanRecordingCapture
{
    private readonly AsioDevice device;
    private readonly AudioControlThread control = new();
    private readonly AudioDeviceConfiguration configuration;
    private readonly byte[] interleaved;
    private int discontinuity;
    private bool nativeDisposed;
    public AsioRecordingCapture(AudioDeviceConfiguration configuration)
    {
        this.configuration = configuration;
        try { device = control.Invoke(() => AsioDevice.Open(configuration.DeviceId!)); }
        catch { control.Dispose(); throw; }
        try
        {
            var channels = configuration.Channels.Length == 0 ? new[] { 0, 1 } : configuration.Channels;
            control.Invoke(() => device.InitRecording(new AsioRecordingOptions { InputChannels = channels,
                SampleRate = configuration.SampleRate == 0 ? null : configuration.SampleRate, BufferSize = configuration.AsioBufferFrames }));
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(control.Invoke(() => device.CurrentSampleRate), channels.Length);
            interleaved = new byte[checked(control.Invoke(() => device.FramesPerBuffer) * WaveFormat.BlockAlign)];
            device.AudioCaptured += OnCaptured;
            device.Stopped += OnStopped;
            device.DriverResetRequest += OnReset;
            device.ResyncOccurred += OnResync;
        }
        catch { try { control.Invoke(device.Dispose); } finally { control.Dispose(); } throw; }
    }
    public WaveFormat WaveFormat { get; }
    public AudioStreamStatus Status => control.Invoke(() => new AudioStreamStatus(configuration.DeviceId!, device.DriverName, AudioBackendKind.Asio, WaveFormat,
        configuration.BufferMilliseconds, device.FramesPerBuffer * 1000d / WaveFormat.SampleRate, device.FramesPerBuffer,
        device.Capabilities.InputChannelInfos[configuration.Channels.Length == 0 ? 0 : configuration.Channels[0]].type.ToString(), configuration.AsioBufferFrames));
    public event CapturePacketHandler? PacketAvailable;
    public event EventHandler<WaveInEventArgs>? DataAvailable { add => throw new NotSupportedException("Span-Capture verwenden."); remove { } }
    public event EventHandler<StoppedEventArgs>? RecordingStopped;
    private void OnCaptured(object? sender, AsioAudioCapturedEventArgs args)
    {
        var floats = MemoryMarshal.Cast<byte, float>(interleaved);
        for (var channel = 0; channel < args.ChannelCount; channel++)
        {
            var samples = args.GetChannel(channel);
            for (var frame = 0; frame < args.Frames; frame++) floats[frame * args.ChannelCount + channel] = samples[frame];
        }
        PacketAvailable?.Invoke(interleaved.AsSpan(0, args.Frames * WaveFormat.BlockAlign),
            Volatile.Read(ref discontinuity) != 0, args.SamplePosition);
    }
    private void OnResync(object? sender, EventArgs args) => Volatile.Write(ref discontinuity, 1);
    private void OnReset(object? sender, EventArgs args) => RecordingStopped?.Invoke(this,
        new StoppedEventArgs(new IOException("ASIO verlangt einen Reset. Teilaufnahme sichern und Gerät erneut auswählen.")));
    private void OnStopped(object? sender, StoppedEventArgs args) => RecordingStopped?.Invoke(this, args);
    public void StartRecording() => control.Invoke(device.Start);
    public void StopRecording() => control.Invoke(device.Stop);
    public void Dispose()
    {
        if (!nativeDisposed)
        {
            control.Invoke(device.Dispose); nativeDisposed = true;
            device.AudioCaptured -= OnCaptured; device.Stopped -= OnStopped;
            device.DriverResetRequest -= OnReset; device.ResyncOccurred -= OnResync;
        }
        control.Dispose();
    }
}
