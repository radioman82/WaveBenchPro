using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WellenEditor;

public enum AudioBackendKind { WasapiShared, WasapiExclusive, Asio, WasapiLoopback }
public enum AudioSampleEncoding { Source, Pcm16, Pcm24, Pcm32, Float32 }
public enum AudioDirection { Output, Input }

public sealed record AudioDeviceConfiguration
{
    public AudioBackendKind Backend { get; init; } = AudioBackendKind.WasapiShared;
    public string? DeviceId { get; init; }
    public int SampleRate { get; init; }
    public int BufferMilliseconds { get; init; } = 50;
    public int? AsioBufferFrames { get; init; }
    public int[] Channels { get; init; } = [];
    public AudioSampleEncoding Encoding { get; init; } = AudioSampleEncoding.Source;

    public void Validate(AudioDirection direction)
    {
        if (!Enum.IsDefined(Backend) || !Enum.IsDefined(Encoding)) throw new ArgumentException("Unbekannter Audio-Modus.");
        if (BufferMilliseconds is < 5 or > 500) throw new ArgumentException("Der Gerätepuffer muss zwischen 5 und 500 ms liegen.");
        if (SampleRate != 0 && SampleRate is < 8000 or > 384000) throw new ArgumentException("Ungültige Abtastrate.");
        if (AsioBufferFrames is <= 0 or > 65536) throw new ArgumentException("Ungültige ASIO-Puffergröße.");
        if (Channels is null || Channels.Length > 64 || Channels.Any(c => c < 0 || c >= 64) || Channels.Distinct().Count() != Channels.Length)
            throw new ArgumentException("Kanalnummern müssen eindeutig sein und zwischen 1 und 64 liegen.");
        if (direction == AudioDirection.Output && Backend == AudioBackendKind.WasapiLoopback)
            throw new ArgumentException("Loopback ist nur für Aufnahme verfügbar.");
        if (direction == AudioDirection.Input && Backend == AudioBackendKind.WasapiExclusive)
            throw new NotSupportedException("Exclusive-Aufnahme ist noch nicht freigegeben. Bitte Shared oder ASIO wählen.");
        if (Backend == AudioBackendKind.Asio && string.IsNullOrWhiteSpace(DeviceId))
            throw new ArgumentException("Bitte einen installierten ASIO-Treiber auswählen.");
    }
}

public sealed record AudioHardwareConfiguration
{
    public AudioDeviceConfiguration Output { get; init; } = new();
    public AudioDeviceConfiguration Input { get; init; } = new();
}

internal static class AsioMonitoringConfiguration
{
    internal static AudioDeviceConfiguration Combine(AudioDeviceConfiguration input, AudioDeviceConfiguration output)
    {
        if (input.Backend != AudioBackendKind.Asio || output.Backend != AudioBackendKind.Asio ||
            string.IsNullOrEmpty(input.DeviceId) || !string.Equals(input.DeviceId, output.DeviceId, StringComparison.Ordinal))
            throw new NotSupportedException("ASIO-Monitoring benötigt denselben ASIO-Treiber für Aufnahme und Wiedergabe.");
        if (input.SampleRate != 0 && output.SampleRate != 0 && input.SampleRate != output.SampleRate ||
            input.AsioBufferFrames is { } inputFrames && output.AsioBufferFrames is { } outputFrames && inputFrames != outputFrames)
            throw new NotSupportedException("ASIO-Monitoring benötigt eine gemeinsame Abtastrate und Puffergröße.");
        return input with { SampleRate = input.SampleRate != 0 ? input.SampleRate : output.SampleRate,
            AsioBufferFrames = input.AsioBufferFrames ?? output.AsioBufferFrames };
    }
}

internal static class AudioHardwareSettingsStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    internal static AudioHardwareConfiguration Load(string path)
    {
        if (!File.Exists(path)) return new();
        var value = JsonSerializer.Deserialize<AudioHardwareConfiguration>(File.ReadAllText(path), Json)
            ?? throw new InvalidDataException("Leere Audio-Hardwarekonfiguration.");
        value.Output.Validate(AudioDirection.Output); value.Input.Validate(AudioDirection.Input);
        return value;
    }
    internal static void Save(string path, AudioHardwareConfiguration value)
    {
        value.Output.Validate(AudioDirection.Output); value.Input.Validate(AudioDirection.Input);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(value, Json)); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

public static class AudioHardwareSettings
{
    private static readonly string PathName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WaveBenchPro", "AudioHardware.json");
    private static AudioHardwareConfiguration configuration = Read();
    public static string? LoadError { get; private set; }
    public static AudioDeviceConfiguration Output => configuration.Output;
    public static AudioDeviceConfiguration Input => configuration.Input;
    public static string? OutputDeviceId => Output.DeviceId;
    public static string? InputDeviceId => Input.DeviceId;
    public static bool OutputReselectionRequired { get; private set; }
    public static bool InputReselectionRequired { get; private set; }
    internal static bool MonitorInput { get; private set; }

    private static AudioHardwareConfiguration Read()
    {
        try { return AudioHardwareSettingsStore.Load(PathName); }
        catch (Exception e) { LoadError = "Audio-Einstellungen konnten nicht geladen werden: " + e.Message; InputReselectionRequired = true; return new(); }
    }
    public static void Apply(AudioDeviceConfiguration output, AudioDeviceConfiguration input) => Apply(output, input, false);
    internal static void Apply(AudioDeviceConfiguration output, AudioDeviceConfiguration input, bool monitorInput)
    {
        if (monitorInput) AsioMonitoringConfiguration.Combine(input, output);
        var next = new AudioHardwareConfiguration { Output = output with { Channels = [.. output.Channels] }, Input = input with { Channels = [.. input.Channels] } };
        AudioHardwareSettingsStore.Save(PathName, next);
        configuration = next;
        MonitorInput = monitorInput;
        OutputReselectionRequired = InputReselectionRequired = false;
        LoadError = null;
    }
    internal static void RequireReselection(AudioDirection direction)
    { if (direction == AudioDirection.Output) OutputReselectionRequired = true; else InputReselectionRequired = true; }
}
