using NAudio.CoreAudioApi;
using System.Windows;

namespace WellenEditor;

public partial class AudioHardwareWindow : Window
{
    public AudioHardwareWindow()
    {
        InitializeComponent();
        LoadDevices();
    }

    private void LoadDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        OutputDeviceBox.Items.Add(new AudioEndpoint("Windows-Standardgerät", null));
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)) OutputDeviceBox.Items.Add(new AudioEndpoint(device.FriendlyName, device.ID));
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)) InputDeviceBox.Items.Add(new AudioEndpoint(device.FriendlyName, device.ID));
        OutputDeviceBox.SelectedItem = OutputDeviceBox.Items.Cast<AudioEndpoint>().FirstOrDefault(item => item.Id == AudioHardwareSettings.OutputDeviceId) ?? OutputDeviceBox.Items[0];
        InputDeviceBox.SelectedItem = InputDeviceBox.Items.Cast<AudioEndpoint>().FirstOrDefault(item => item.Id == AudioHardwareSettings.InputDeviceId);
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        AudioHardwareSettings.OutputDeviceId = (OutputDeviceBox.SelectedItem as AudioEndpoint)?.Id;
        AudioHardwareSettings.InputDeviceId = (InputDeviceBox.SelectedItem as AudioEndpoint)?.Id;
        DialogResult = true;
    }
}

public sealed record AudioEndpoint(string Name, string? Id);

public static class AudioHardwareSettings
{
    public static string? OutputDeviceId { get; set; }
    public static string? InputDeviceId { get; set; }
}
