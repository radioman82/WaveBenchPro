using NAudio.CoreAudioApi;
using NAudio.Wave;
using System.IO;

namespace WellenEditor;

public sealed record AudioEndpoint(string Name, string? Id);
internal sealed record AudioStreamStatus(string DeviceId, string DeviceName, AudioBackendKind Backend,
    WaveFormat Format, int RequestedBufferMilliseconds, double NegotiatedBufferMilliseconds, int? BufferFrames = null,
    string? NativeSampleFormat = null, int? RequestedBufferFrames = null)
{
    public override string ToString() => $"{DeviceName} · {Backend} · {Format.SampleRate:N0} Hz / {Format.Channels} Kanäle / {Format.BitsPerSample} Bit {Format.Encoding}"
        + (BufferFrames is { } frames ? $" · ASIO-Puffer {frames} Frames / {NegotiatedBufferMilliseconds:0.##} ms (angefordert {RequestedBufferFrames?.ToString() ?? "Treiberstandard"})" : $" · Puffer angefordert {RequestedBufferMilliseconds} ms, NAudio-API {NegotiatedBufferMilliseconds:0.##} ms (keine akustische Messung)")
        + (NativeSampleFormat is { } native ? $" · Treiberformat {native}" : "");
}

internal interface IAudioPlayback : IWavePlayer
{
    AudioStreamStatus Status { get; }
    long PositionFrames { get; }
    long ReadAheadUnderruns { get; }
    Exception? SourceError { get; }
    TimeSpan AverageLatency { get; }
}

internal delegate void CapturePacketHandler(ReadOnlySpan<byte> data, bool discontinuity, long devicePosition);
internal interface ISpanRecordingCapture : IRecordingCapture
{
    event CapturePacketHandler? PacketAvailable;
    AudioStreamStatus Status { get; }
}

internal interface IAudioHardwareBackend
{
    IReadOnlyList<AudioEndpoint> Enumerate(AudioBackendKind backend, AudioDirection direction);
    IAudioPlayback CreatePlayback(AudioDeviceConfiguration configuration);
    IRecordingCapture CreateCapture(AudioDeviceConfiguration configuration);
    string DescribeCapabilities(AudioDeviceConfiguration configuration, AudioDirection direction);
    void ShowControlPanel(string driverName);
}

internal sealed class AudioHardwareBackend : IAudioHardwareBackend
{
    internal static IAudioHardwareBackend Default { get; } = new AudioHardwareBackend();
    public IReadOnlyList<AudioEndpoint> Enumerate(AudioBackendKind backend, AudioDirection direction)
    {
        if (backend == AudioBackendKind.Asio)
            return AsioDevice.GetDriverNames().Select(name => new AudioEndpoint(name, name)).ToArray();
        using var enumerator = new MMDeviceEnumerator();
        var flow = direction == AudioDirection.Output || backend == AudioBackendKind.WasapiLoopback ? DataFlow.Render : DataFlow.Capture;
        var result = new List<AudioEndpoint>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
        { using (device) result.Add(new(device.FriendlyName, device.ID)); }
        return result;
    }

    internal static MMDevice Resolve(AudioDeviceConfiguration config, AudioDirection direction)
    {
        using var enumerator = new MMDeviceEnumerator();
        try
        {
            var flow = direction == AudioDirection.Output || config.Backend == AudioBackendKind.WasapiLoopback ? DataFlow.Render : DataFlow.Capture;
            if (direction == AudioDirection.Input && string.IsNullOrWhiteSpace(config.DeviceId))
                throw new InvalidOperationException("Bitte unter Optionen → Audio-Hardware ein Aufnahmegerät ausdrücklich auswählen.");
            var device = string.IsNullOrWhiteSpace(config.DeviceId) ? enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia) : enumerator.GetDevice(config.DeviceId);
            if (device.State != DeviceState.Active || device.DataFlow != flow)
            { device.Dispose(); throw new InvalidOperationException("Das gewählte Audiogerät ist nicht mehr aktiv oder gehört zur falschen Richtung."); }
            return device;
        }
        catch (Exception error) when (error is not InvalidOperationException)
        { throw new InvalidOperationException("Das gewählte Audiogerät ist nicht verfügbar. Bitte unter Audio-Hardware erneut auswählen; es wird kein anderes Gerät automatisch verwendet.", error); }
    }

    public IAudioPlayback CreatePlayback(AudioDeviceConfiguration configuration)
    {
        configuration.Validate(AudioDirection.Output);
        return configuration.Backend == AudioBackendKind.Asio ? new AsioPlayback(configuration) : new WasapiPlayback(configuration);
    }
    public IRecordingCapture CreateCapture(AudioDeviceConfiguration configuration)
    {
        configuration.Validate(AudioDirection.Input);
        return configuration.Backend == AudioBackendKind.Asio ? new AsioRecordingCapture(configuration) : new WasapiRecordingCapture(configuration);
    }
    public string DescribeCapabilities(AudioDeviceConfiguration configuration, AudioDirection direction)
    {
        if (configuration.Backend == AudioBackendKind.Asio)
        {
            using var control = new AudioControlThread();
            return control.Invoke(() => DescribeAsio(configuration));
        }
        using var device = Resolve(configuration, direction);
        using var client = device.CreateAudioClient();
        var supported = new List<string>();
        foreach (var rate in new[] { 44100, 48000, 96000, 192000 })
            foreach (var bits in new[] { 16, 24, 32 })
                if (client.IsFormatSupported(AudioClientShareMode.Exclusive, new WaveFormat(rate, bits, 2))) supported.Add($"{rate / 1000d:0.#} kHz / PCM{bits}");
        return $"Shared-Mix: {client.MixFormat}.\nGeräteperiode: {client.DefaultDevicePeriod / 10000d:0.##} ms; Minimum {client.MinimumDevicePeriod / 10000d:0.##} ms.\nExclusive-Stereoformatabfrage: {string.Join("; ", supported)}.\nFormatunterstützung allein bestätigt noch keinen erfolgreichen Exclusive-Start.";
    }
    private static string DescribeAsio(AudioDeviceConfiguration configuration)
    {
            using var driver = AsioDevice.Open(configuration.DeviceId ?? throw new InvalidOperationException("Bitte ASIO-Treiber wählen."));
            var c = driver.Capabilities;
            return $"{driver.DriverName}: {c.NbOutputChannels} Ausgänge, {c.NbInputChannels} Eingänge; Puffer {c.BufferMinSize}–{c.BufferMaxSize}, bevorzugt {c.BufferPreferredSize}, Schritt {c.BufferGranularity}; aktuell {driver.CurrentSampleRate} Hz.\n"
                + string.Join("; ", c.OutputChannelInfos.Select((channel, i) => $"Out {i + 1}: {channel.name} ({channel.type})")) + "\n"
                + string.Join("; ", c.InputChannelInfos.Select((channel, i) => $"In {i + 1}: {channel.name} ({channel.type})"));
    }
    public void ShowControlPanel(string driverName)
    { using var control = new AudioControlThread(); control.Invoke(() => { using var driver = AsioDevice.Open(driverName); driver.ShowControlPanel(); }); }
}

internal sealed class WasapiPlayback : IAudioPlayback
{
    private readonly MMDevice device;
    private readonly WasapiPlayer player;
    private readonly AudioDeviceConfiguration configuration;
    private readonly string deviceId, deviceName;
    private int sourceRate;
    private bool initialized;
    private HardwareReadAhead? readAhead;
    public WasapiPlayback(AudioDeviceConfiguration configuration)
    {
        this.configuration = configuration;
        device = AudioHardwareBackend.Resolve(configuration, AudioDirection.Output);
        deviceId = device.ID; deviceName = device.FriendlyName;
        try
        {
            var builder = new WasapiPlayerBuilder().WithDevice(device).WithLatency(configuration.BufferMilliseconds)
                .WithPollingSync().WithMmcssThreadPriority("Pro Audio");
            if (configuration.Backend == AudioBackendKind.WasapiExclusive) builder.WithExclusiveMode(); else builder.WithSharedMode();
            player = builder.Build();
            player.PlaybackStopped += OnStopped;
        }
        catch { device.Dispose(); throw; }
    }
    public event EventHandler<StoppedEventArgs>? PlaybackStopped;
    private void OnStopped(object? sender, StoppedEventArgs args) => PlaybackStopped?.Invoke(this,
        args.Exception is null && SourceError is { } failure ? new StoppedEventArgs(failure) : args);
    public long ReadAheadUnderruns => readAhead?.Underruns ?? 0;
    public Exception? SourceError => readAhead?.Error;
    public PlaybackState PlaybackState => player.PlaybackState;
    public float Volume { get => player.Volume; set => player.Volume = value; }
    public WaveFormat OutputWaveFormat => player.OutputWaveFormat;
    public TimeSpan AverageLatency => player.AverageLatency;
    public TimeSpan CurrentLatency => player.CurrentLatency;
    public AudioStreamStatus Status => new(deviceId, deviceName, configuration.Backend,
        initialized ? player.OutputWaveFormat : player.DeviceMixFormat, configuration.BufferMilliseconds,
        initialized ? player.AverageLatency.TotalMilliseconds : 0);
    public long PositionFrames => initialized ? (long)(player.GetPosition() / (double)OutputWaveFormat.BlockAlign * sourceRate / OutputWaveFormat.SampleRate) : 0;
    public void Init(IWaveProvider source)
    {
        if (configuration.SampleRate != 0 && configuration.SampleRate != source.WaveFormat.SampleRate)
            throw new NotSupportedException($"Die Quelle hat {source.WaveFormat.SampleRate} Hz, gewählt sind {configuration.SampleRate} Hz. Bitte die Rate auf Quelle stellen oder ausdrücklich anpassen; es erfolgt kein stilles Resampling.");
        sourceRate = source.WaveFormat.SampleRate;
        if (HardwareReadAhead.NeedsWorker(source))
        {
            readAhead = new HardwareReadAhead(source);
            readAhead.PrefillAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
            source = readAhead;
        }
        var adapted = AudioOutputConversion.Adapt(source, configuration.Encoding);
        if (configuration.Backend == AudioBackendKind.WasapiExclusive)
        {
            var capability = player.GetPlaybackCapability(adapted.WaveFormat);
            if (!capability.Supported || capability.OutputFormat is not { } output ||
                output.SampleRate != adapted.WaveFormat.SampleRate || output.Channels != adapted.WaveFormat.Channels || output.BitsPerSample != adapted.WaveFormat.BitsPerSample || IsFloat(output) != IsFloat(adapted.WaveFormat))
                throw new NotSupportedException($"Exclusive unterstützt das gewählte Quell-/Ausgabeformat nicht: {adapted.WaveFormat}. Bitte ein unterstütztes PCM-Format oder Shared ausdrücklich auswählen. {capability.Reason}");
        }
        try { player.Init(adapted); initialized = true; }
        catch (Exception e) { throw new InvalidOperationException($"{configuration.Backend} konnte auf {deviceName} nicht initialisiert werden. Gerät eventuell belegt oder Format/Puffer nicht unterstützt. Shared kann unter Audio-Hardware ausdrücklich gewählt werden. {e.Message}", e); }
    }
    private static bool IsFloat(WaveFormat format) => format.Encoding == WaveFormatEncoding.IeeeFloat ||
        format is WaveFormatExtensible extensible && extensible.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71");
    public void Play() => player.Play();
    public void Stop() => player.Stop();
    public void Pause() => player.Pause();
    public void Dispose() { player.PlaybackStopped -= OnStopped; try { player.Dispose(); } finally { try { readAhead?.DrainAndDispose(); } finally { device.Dispose(); } } }
}

internal sealed class AsioPlayback : IAudioPlayback
{
    private readonly AudioDeviceConfiguration configuration;
    private readonly AsioDevice device;
    private readonly AudioControlThread control = new();
    private CountingProvider? source;
    private PlaybackState state;
    private HardwareReadAhead? readAhead;
    public long ReadAheadUnderruns => readAhead?.Underruns ?? 0;
    public Exception? SourceError => readAhead?.Error;
    public AsioPlayback(AudioDeviceConfiguration configuration)
    {
        this.configuration = configuration;
        try
        {
            device = control.Invoke(() => AsioDevice.Open(configuration.DeviceId!));
            device.Stopped += OnStopped; device.DriverResetRequest += OnReset;
        }
        catch { control.Dispose(); throw; }
    }
    private void OnStopped(object? sender, StoppedEventArgs args)
    {
        state = PlaybackState.Stopped;
        ThreadPool.QueueUserWorkItem(_ => PlaybackStopped?.Invoke(this,
            args.Exception is null && SourceError is { } failure ? new StoppedEventArgs(failure) : args));
    }
    private void OnReset(object? sender, EventArgs args) => OnStopped(this, new StoppedEventArgs(new IOException("Der ASIO-Treiber verlangt eine Neuinitialisierung. Bitte stoppen und das Gerät erneut auswählen.")));
    public event EventHandler<StoppedEventArgs>? PlaybackStopped;
    public PlaybackState PlaybackState => state;
    public float Volume { get => 1; set { if (value != 1) throw new NotSupportedException("Lautstärke wird über den vorhandenen Mixer geregelt."); } }
    public WaveFormat OutputWaveFormat => source?.WaveFormat ?? throw new InvalidOperationException("ASIO ist noch nicht initialisiert.");
    public TimeSpan AverageLatency => control.Invoke(() => TimeSpan.FromSeconds(device.OutputLatencySamples / (double)OutputWaveFormat.SampleRate));
    public TimeSpan CurrentLatency => AverageLatency;
    public long PositionFrames => control.Invoke(() => Math.Max(0, (source?.Frames ?? 0) - device.OutputLatencySamples - device.FramesPerBuffer));
    public AudioStreamStatus Status => control.Invoke(() => new AudioStreamStatus(configuration.DeviceId!, device.DriverName, AudioBackendKind.Asio,
        OutputWaveFormat, configuration.BufferMilliseconds, device.FramesPerBuffer * 1000d / OutputWaveFormat.SampleRate, device.FramesPerBuffer,
        device.Capabilities.OutputChannelInfos[configuration.Channels.Length == 0 ? 0 : configuration.Channels[0]].type.ToString(), configuration.AsioBufferFrames));
    public void Init(IWaveProvider provider)
    {
        if (configuration.SampleRate != 0 && configuration.SampleRate != provider.WaveFormat.SampleRate)
            throw new NotSupportedException("ASIO-Abtastrate und Quelle müssen übereinstimmen; kein automatisches Resampling.");
        if (HardwareReadAhead.NeedsWorker(provider))
        {
            readAhead = new HardwareReadAhead(provider);
            readAhead.PrefillAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
            provider = readAhead;
        }
        source = new CountingProvider(AudioOutputConversion.Adapt(provider, configuration.Encoding));
        control.Invoke(() => device.InitPlayback(new AsioPlaybackOptions { Source = source,
            OutputChannels = configuration.Channels.Length == 0 ? null : configuration.Channels,
            BufferSize = configuration.AsioBufferFrames, AutoStopOnEndOfStream = true }));
    }
    public void Play() { control.Invoke(device.Start); state = PlaybackState.Playing; }
    public void Stop() { control.Invoke(device.Stop); state = PlaybackState.Stopped; }
    public void Pause() { Stop(); state = PlaybackState.Paused; }
    public void Dispose() { device.Stopped -= OnStopped; device.DriverResetRequest -= OnReset; try { control.Invoke(device.Dispose); } finally { try { readAhead?.DrainAndDispose(); } finally { control.Dispose(); } } }
    private sealed class CountingProvider(IWaveProvider input) : IWaveProvider
    {
        private long frames;
        public WaveFormat WaveFormat => input.WaveFormat;
        public long Frames => Interlocked.Read(ref frames);
        public int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public int Read(Span<byte> buffer) { var read = input.Read(buffer); Interlocked.Add(ref frames, read / WaveFormat.BlockAlign); return read; }
    }
}
