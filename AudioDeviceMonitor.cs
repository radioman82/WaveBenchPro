using NAudio.CoreAudioApi;

namespace WellenEditor;

internal enum AudioDeviceChangeKind { Unavailable, DefaultChanged, FormatChanged }
internal sealed record AudioDeviceChange(string DeviceId, AudioDeviceChangeKind Kind, DataFlow? Flow = null);

internal static class AudioDeviceChangePolicy
{
    internal static bool RequiresStop(AudioDeviceChange change, AudioDeviceConfiguration selected, AudioStreamStatus active, AudioDirection direction)
    {
        if (selected.Backend == AudioBackendKind.Asio) return false;
        if (change.Kind == AudioDeviceChangeKind.DefaultChanged)
        {
            var flow = direction == AudioDirection.Output || selected.Backend == AudioBackendKind.WasapiLoopback ? DataFlow.Render : DataFlow.Capture;
            return selected.DeviceId is null && change.Flow == flow && change.DeviceId != active.DeviceId;
        }
        return change.DeviceId == active.DeviceId;
    }
}

internal sealed class AudioDeviceMonitor : IDisposable
{
    private readonly MMDeviceEnumerator enumerator = new();
    private readonly MMDeviceNotificationClient client;
    internal event Action<AudioDeviceChange>? Changed;
    internal AudioDeviceMonitor()
    {
        client = enumerator.CreateNotificationClient();
        client.DeviceRemoved += (_, e) => Changed?.Invoke(new(e.DeviceId, AudioDeviceChangeKind.Unavailable));
        client.DeviceStateChanged += (_, e) => { if (e.NewState != DeviceState.Active) Changed?.Invoke(new(e.DeviceId, AudioDeviceChangeKind.Unavailable)); };
        client.DefaultDeviceChanged += (_, e) => { if (e.Role == Role.Multimedia) Changed?.Invoke(new(e.DeviceId, AudioDeviceChangeKind.DefaultChanged, e.Flow)); };
        client.PropertyValueChanged += (_, e) => { if (e.PropertyKey.Equals(PropertyKeys.PKEY_AudioEngine_DeviceFormat)) Changed?.Invoke(new(e.DeviceId, AudioDeviceChangeKind.FormatChanged)); };
    }
    public void Dispose() { client.Dispose(); enumerator.Dispose(); }
}
