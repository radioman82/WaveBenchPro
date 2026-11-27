using System.IO;
using System.Runtime.InteropServices;
using System.Buffers.Binary;
using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.Asio;

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

internal sealed class AsioRecordingCapture : ISpanRecordingCapture, IFixedPacketRecordingCapture
{
    private readonly AsioDriverExt device;
    private readonly AudioControlThread control = new();
    private readonly AudioDeviceConfiguration configuration;
    private readonly byte[] interleaved;
    private readonly byte[] nativeChannel;
    private readonly AsioSampleType nativeFormat;
    private readonly AsioInputMonitor? monitor;
    private readonly int frames;
    public int PacketFrames => frames;
    internal bool StoppedWithRetainedBuffers => bufferRetirement.HasFailed && Volatile.Read(ref running) == 0;
    private readonly string name;
    private readonly Timer notifications;
    private int discontinuity;
    private int running, stopping, activeCallbacks, driverFault;
    private bool nativeDisposed;
    private readonly AsioBufferRetirement bufferRetirement = new();
    private long callbacks, allocatedBytes, betweenCallbackAllocatedBytes, lastExitAllocated;
    private long previousPacketTick, maximumPacketGapTicks;
    private long latestPacketTick;
    private long previousDevicePosition, devicePositionTransitions, devicePositionDeltasOutsidePacketSize;
    private int lastCallbackThread;
    internal long CallbackCount => Interlocked.Read(ref callbacks);
    internal long CallbackAllocatedBytes => Interlocked.Read(ref allocatedBytes);
    internal long BetweenCallbackAllocatedBytes => Interlocked.Read(ref betweenCallbackAllocatedBytes);
    internal double MaximumPacketGapMs => Interlocked.Read(ref maximumPacketGapTicks) * 1000d / Stopwatch.Frequency;
    internal long DevicePositionTransitions => Interlocked.Read(ref devicePositionTransitions);
    internal long DevicePositionDeltasOutsidePacketSize => Interlocked.Read(ref devicePositionDeltasOutsidePacketSize);
    private bool devicePositionAvailable;
    public AsioRecordingCapture(AudioDeviceConfiguration configuration, int[]? monitorChannels = null)
    {
        this.configuration = configuration;
        // NAudio 3.3.0 InitRecording allocates AsioAudioCapturedEventArgs per packet.
        // Its public raw buffer callback works for both input-only and shared-clock monitoring.
        try { device = control.Invoke(() =>
        {
            var raw = AsioDriver.GetAsioDriverByName(configuration.DeviceId!);
            try { return new AsioDriverExt(raw); } catch { raw.ReleaseComAsioDriver(); throw; }
        }); }
        catch { control.Dispose(); throw; }
        try
        {
            var channels = configuration.Channels.Length == 0 ? new[] { 0, 1 } : configuration.Channels;
            var setup = control.Invoke(() =>
            {
                var capabilities = device.Capabilities;
                if (channels.Any(channel => channel >= capabilities.NbInputChannels)) throw new NotSupportedException("ASIO-Eingangskanal nicht vorhanden.");
                var format = capabilities.InputChannelInfos[channels[0]].type;
                if (channels.Any(channel => capabilities.InputChannelInfos[channel].type != format)) throw new NotSupportedException("Gemischte native ASIO-Eingangsformate werden nicht unterstützt.");
                var width = NativeAsioCaptureConversion.BytesPerSample(format);
                var rate = configuration.SampleRate == 0 ? (int)device.Driver.GetSampleRate() : configuration.SampleRate;
                if (!device.IsSampleRateSupported(rate)) throw new NotSupportedException($"ASIO unterstützt {rate} Hz nicht.");
                if ((int)device.Driver.GetSampleRate() != rate) device.SetSampleRate(rate);
                var outputFormats = monitorChannels is null ? [] : capabilities.OutputChannelInfos.Select(channel => channel.type).ToArray();
                var selectedOutputs = monitorChannels is { Length: 0 } ? Enumerable.Range(0, channels.Length).ToArray() : monitorChannels;
                if (selectedOutputs is not null && (selectedOutputs.Length != channels.Length || selectedOutputs.Any(channel => channel >= outputFormats.Length)))
                    throw new NotSupportedException("ASIO-Monitoring benötigt für jeden Eingang einen ausgewählten Ausgang.");
                foreach (var outputFormat in outputFormats) NativeAsioCaptureConversion.BytesPerSample(outputFormat);
                var count = configuration.AsioBufferFrames is { } requested ? device.CreateBuffers(outputFormats.Length, channels.Length, requested)
                    : device.CreateBuffers(outputFormats.Length, channels.Length, false);
                device.SetChannelMapping(Enumerable.Range(0, outputFormats.Length).ToArray(), channels);
                return (format, width, count, rate, outputFormats, selectedOutputs, name: device.Driver.GetDriverName());
            });
            nativeFormat = setup.format; frames = setup.count; name = setup.name;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(setup.rate, channels.Length);
            interleaved = new byte[checked(frames * WaveFormat.BlockAlign)];
            nativeChannel = new byte[checked(frames * setup.width)];
            if (setup.selectedOutputs is not null) monitor = new AsioInputMonitor(frames, setup.outputFormats, setup.selectedOutputs);
            device.FillBufferCallback = OnCaptured;
            device.ResetRequestCallback = () => Volatile.Write(ref driverFault, 1);
            device.BufferSizeChangedCallback = _ => Volatile.Write(ref driverFault, 1);
            device.LatenciesChangedCallback = () => Volatile.Write(ref driverFault, 1);
            device.ResyncRequestCallback = () => Volatile.Write(ref discontinuity, 1);
            notifications = new Timer(CheckDriverFault, null, 100, 100);
        }
        catch { try { control.Invoke(device.ReleaseDriver); } finally { control.Dispose(); } throw; }
    }
    public WaveFormat WaveFormat { get; }
    public AudioStreamStatus Status => new(configuration.DeviceId!, name, AudioBackendKind.Asio, WaveFormat,
        configuration.BufferMilliseconds, frames * 1000d / WaveFormat.SampleRate, frames, nativeFormat.ToString(), configuration.AsioBufferFrames);
    public event CapturePacketHandler? PacketAvailable;
    public event EventHandler<WaveInEventArgs>? DataAvailable { add => throw new NotSupportedException("Span-Capture verwenden."); remove { } }
    public event EventHandler<StoppedEventArgs>? RecordingStopped;
    private void OnCaptured(IntPtr[] inputs, IntPtr[] outputs)
    {
        Interlocked.Increment(ref activeCallbacks);
        try
        {
            if (Volatile.Read(ref stopping) != 0 || Volatile.Read(ref driverFault) != 0)
            { monitor?.Write(outputs, []); return; }
            if ((int)device.Capabilities.SampleRate != WaveFormat.SampleRate)
            { Volatile.Write(ref driverFault, 1); monitor?.Write(outputs, []); return; }
            var allocation = GC.GetAllocatedBytesForCurrentThread();
            var thread = Environment.CurrentManagedThreadId;
            var warmed = CallbackCount >= 8;
            var now = Stopwatch.GetTimestamp();
            Volatile.Write(ref latestPacketTick, now);
            if (warmed) Interlocked.Exchange(ref maximumPacketGapTicks, Math.Max(maximumPacketGapTicks, now - previousPacketTick));
            previousPacketTick = now;
            if (warmed && thread == lastCallbackThread)
                Interlocked.Add(ref betweenCallbackAllocatedBytes, Math.Max(0, allocation - lastExitAllocated));
            var floats = MemoryMarshal.Cast<byte, float>(interleaved);
            for (var channel = 0; channel < inputs.Length; channel++)
            {
                Marshal.Copy(inputs[channel], nativeChannel, 0, nativeChannel.Length);
                NativeAsioCaptureConversion.Interleave(nativeChannel, floats, channel, inputs.Length, nativeFormat);
            }
            var hasPosition = device.TryGetSamplePosition(out var position, out _);
            if (hasPosition && devicePositionAvailable)
            {
                Interlocked.Increment(ref devicePositionTransitions);
                if (position - previousDevicePosition != frames)
                    Interlocked.Increment(ref devicePositionDeltasOutsidePacketSize);
            }
            devicePositionAvailable = hasPosition;
            previousDevicePosition = position;
            PacketAvailable?.Invoke(interleaved, Volatile.Read(ref discontinuity) != 0, position);
            monitor?.Write(outputs, Volatile.Read(ref discontinuity) != 0 || Volatile.Read(ref stopping) != 0 ? [] : floats);
            var end = GC.GetAllocatedBytesForCurrentThread();
            if (warmed) Interlocked.Add(ref allocatedBytes, end - allocation);
            lastCallbackThread = thread; lastExitAllocated = end;
            Interlocked.Increment(ref callbacks);
        }
        catch { Volatile.Write(ref driverFault, 1); }
        finally { Interlocked.Decrement(ref activeCallbacks); }
    }
    private void CheckDriverFault(object? unused)
    {
        if (Volatile.Read(ref running) == 0 || Volatile.Read(ref stopping) != 0) return;
        var packetTimeout = TimeSpan.FromSeconds(Math.Max(2, 4d * frames / WaveFormat.SampleRate));
        var timedOut = Stopwatch.GetElapsedTime(Volatile.Read(ref latestPacketTick)) > packetTimeout;
        if (Volatile.Read(ref driverFault) == 0 && !timedOut) return;
        if (Interlocked.Exchange(ref stopping, 1) != 0) return;
        var reason = timedOut ? "Der ASIO-Treiber liefert keine Aufnahmepakete mehr." : "ASIO-Aufnahmefehler oder Treiber-Reset.";
        try { StopRecordingCore(new IOException(reason + " Teilaufnahme sichern und Gerät erneut auswählen.")); }
        catch (Exception error) { RecordingStopped?.Invoke(this, new StoppedEventArgs(error)); }
    }
    public void StartRecording() => control.Invoke(() =>
    {
        Volatile.Write(ref latestPacketTick, Stopwatch.GetTimestamp());
        Volatile.Write(ref driverFault, 0);
        Volatile.Write(ref stopping, 0);
        Volatile.Write(ref running, 1);
        device.Start();
    });
    private void StopRecordingCore(Exception? error) => control.Invoke(() =>
    {
        if (Volatile.Read(ref running) == 0) return;
        Volatile.Write(ref stopping, 1);
        if (device.Driver.Stop() is var result && result != AsioError.ASE_OK && result != AsioError.ASE_SUCCESS)
            throw new IOException($"ASIO-Stopp fehlgeschlagen: {result}. Ressourcen bleiben gebunden.");
        while (Volatile.Read(ref activeCallbacks) != 0) Thread.Sleep(1);
        Volatile.Write(ref running, 0);
        RecordingStopped?.Invoke(this, new StoppedEventArgs(error));
    });
    public void StopRecording() { if (!nativeDisposed) StopRecordingCore(null); }
    public void Dispose()
    {
        if (nativeDisposed) return;
        StopRecording();
        notifications.DisposeAsync().AsTask().GetAwaiter().GetResult();
        if (!nativeDisposed)
        {
            control.Invoke(() =>
            {
                device.FillBufferCallback = null;
                bufferRetirement.DisposeBuffers(device.Driver.DisposeBuffers);
                device.Driver.ReleaseComAsioDriver();
            });
            nativeDisposed = true;
        }
        control.Dispose();
    }
}

internal sealed class AsioInputMonitor(int frames, AsioSampleType[] formats, int[] selectedOutputs)
{
    private readonly byte[] scratch = new byte[checked(frames * formats.Max(NativeAsioCaptureConversion.BytesPerSample))];
    private readonly int[] inputChannels = Enumerable.Range(0, formats.Length).Select(output => Array.IndexOf(selectedOutputs, output)).ToArray();

    internal void Write(IntPtr[] outputs, ReadOnlySpan<float> input)
    {
        for (var output = 0; output < outputs.Length; output++)
        {
            if (outputs[output] == IntPtr.Zero) throw new IOException("ASIO hat keinen gültigen Ausgabepuffer geliefert.");
            var bytes = frames * NativeAsioCaptureConversion.BytesPerSample(formats[output]);
            var channel = inputChannels[output];
            if (channel < 0 || input.IsEmpty) scratch.AsSpan(0, bytes).Clear();
            else Encode(input, scratch.AsSpan(0, bytes), channel, selectedOutputs.Length, formats[output]);
            Marshal.Copy(scratch, 0, outputs[output], bytes);
        }
    }

    internal static void Encode(ReadOnlySpan<float> input, Span<byte> output, int channel, int channels, AsioSampleType format)
    {
        var width = NativeAsioCaptureConversion.BytesPerSample(format);
        for (var frame = 0; frame < output.Length / width; frame++)
        {
            var sample = input[frame * channels + channel];
            var level = float.IsFinite(sample) ? Math.Clamp(sample, -1f, 1f) * .25f : 0f;
            var bytes = output[(frame * width)..];
            switch (format)
            {
                case AsioSampleType.Int16LSB:
                    BinaryPrimitives.WriteInt16LittleEndian(bytes, (short)(level * 32768f));
                    break;
                case AsioSampleType.Int24LSB:
                    var value = (int)(level * 8388608f);
                    bytes[0] = (byte)value; bytes[1] = (byte)(value >> 8); bytes[2] = (byte)(value >> 16);
                    break;
                case AsioSampleType.Int32LSB:
                    BinaryPrimitives.WriteInt32LittleEndian(bytes, (int)(level * 2147483648d));
                    break;
                case AsioSampleType.Float32LSB:
                    BinaryPrimitives.WriteInt32LittleEndian(bytes, BitConverter.SingleToInt32Bits(level));
                    break;
            }
        }
    }
}

internal sealed class AsioBufferRetirement
{
    private Exception? failure;
    private bool disposed;
    internal bool HasFailed => failure is not null;

    internal void DisposeBuffers(Func<AsioError> release)
    {
        if (disposed) return;
        if (failure is not null)
            throw new InvalidOperationException("Die ASIO-Pufferfreigabe ist fehlgeschlagen. Dieser Treiberzustand erfordert einen Programmneustart.", failure);
        try
        {
            var result = release();
            if (result is not (AsioError.ASE_OK or AsioError.ASE_SUCCESS))
                throw new IOException($"ASIO-Pufferfreigabe fehlgeschlagen: {result}.");
            disposed = true;
        }
        catch (Exception error)
        {
            // NAudio 3.3 frees the callback table even if the native call fails.
            // Repeating the call could free that table twice.
            failure = error;
            throw;
        }
    }
}

internal static class NativeAsioCaptureConversion
{
    internal static int BytesPerSample(AsioSampleType format) => format switch
    {
        AsioSampleType.Int16LSB => 2, AsioSampleType.Int24LSB => 3,
        AsioSampleType.Int32LSB or AsioSampleType.Float32LSB => 4,
        _ => throw new NotSupportedException($"ASIO-Aufnahmeformat {format} wird nicht unterstützt.")
    };
    internal static void Interleave(ReadOnlySpan<byte> input, Span<float> output, int channel, int channels, AsioSampleType format)
    {
        var width = BytesPerSample(format);
        for (var frame = 0; frame < input.Length / width; frame++)
        {
            var bytes = input[(frame * width)..];
            output[frame * channels + channel] = format switch
            {
                AsioSampleType.Int16LSB => BinaryPrimitives.ReadInt16LittleEndian(bytes) / 32768f,
                AsioSampleType.Int24LSB => ((bytes[0] | bytes[1] << 8 | bytes[2] << 16) << 8 >> 8) / 8388608f,
                AsioSampleType.Int32LSB => (float)(BinaryPrimitives.ReadInt32LittleEndian(bytes) / 2147483648d),
                _ => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes))
            };
        }
    }
}
