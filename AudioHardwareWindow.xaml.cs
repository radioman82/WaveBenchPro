using System.Windows;
using System.Windows.Controls;

namespace WellenEditor;

public partial class AudioHardwareWindow : Window
{
    private sealed record Choice<T>(string Name, T Value);
    private bool loading = true;
    public AudioHardwareWindow()
    {
        InitializeComponent();
#if BETA_PROFILE
        OutputBackendBox.ItemsSource = new[] { new Choice<AudioBackendKind>("WASAPI Shared", AudioBackendKind.WasapiShared), new("WASAPI Exclusive", AudioBackendKind.WasapiExclusive) };
        InputBackendBox.ItemsSource = new[] { new Choice<AudioBackendKind>("WASAPI Shared", AudioBackendKind.WasapiShared), new("WASAPI Loopback (Shared)", AudioBackendKind.WasapiLoopback) };
        MonitorInputBox.Visibility = Visibility.Collapsed;
        MonitoringHelpText.Text = "ASIO ist in dieser Beta bis zur Hardwareabnahme deaktiviert.";
        OutputAsioPanelButton.Visibility = InputAsioPanelButton.Visibility = Visibility.Collapsed;
        OutputChannelsBox.IsEnabled = OutputFramesBox.IsEnabled = false;
        InputChannelsBox.IsEnabled = InputFramesBox.IsEnabled = false;
#else
        OutputBackendBox.ItemsSource = new[] { new Choice<AudioBackendKind>("WASAPI Shared", AudioBackendKind.WasapiShared), new("WASAPI Exclusive", AudioBackendKind.WasapiExclusive), new("ASIO", AudioBackendKind.Asio) };
        InputBackendBox.ItemsSource = new[] { new Choice<AudioBackendKind>("WASAPI Shared", AudioBackendKind.WasapiShared), new("WASAPI Loopback (Shared)", AudioBackendKind.WasapiLoopback), new("ASIO", AudioBackendKind.Asio) };
#endif
        foreach (var box in new[] { OutputRateBox, InputRateBox })
            box.ItemsSource = new[] { new Choice<int>("Quelle / Gerätemix", 0), new("44,1 kHz", 44100), new("48 kHz", 48000), new("96 kHz", 96000), new("192 kHz", 192000) };
        OutputEncodingBox.ItemsSource = new[] { new Choice<AudioSampleEncoding>("Quellformat", AudioSampleEncoding.Source), new("PCM 16 Bit", AudioSampleEncoding.Pcm16), new("PCM 24 Bit", AudioSampleEncoding.Pcm24), new("PCM 32 Bit", AudioSampleEncoding.Pcm32), new("Float32", AudioSampleEncoding.Float32) };
        Select(OutputBackendBox, AudioHardwareSettings.Output.Backend); Select(InputBackendBox, AudioHardwareSettings.Input.Backend);
        Select(OutputRateBox, AudioHardwareSettings.Output.SampleRate); Select(InputRateBox, AudioHardwareSettings.Input.SampleRate);
        Select(OutputEncodingBox, AudioHardwareSettings.Output.Encoding);
        OutputBufferBox.Text = AudioHardwareSettings.Output.BufferMilliseconds.ToString(); InputBufferBox.Text = AudioHardwareSettings.Input.BufferMilliseconds.ToString();
        OutputChannelsBox.Text = string.Join(",", AudioHardwareSettings.Output.Channels.Select(x => x + 1)); InputChannelsBox.Text = string.Join(",", AudioHardwareSettings.Input.Channels.Select(x => x + 1));
        OutputFramesBox.Text = AudioHardwareSettings.Output.AsioBufferFrames?.ToString() ?? ""; InputFramesBox.Text = AudioHardwareSettings.Input.AsioBufferFrames?.ToString() ?? "";
        loading = false;
        LoadDevices(true, AudioHardwareSettings.Output.DeviceId); LoadDevices(false, AudioHardwareSettings.Input.DeviceId);
        HardwareErrorText.Text = AudioHardwareSettings.LoadError ?? "";
#if BETA_PROFILE
        if (AudioHardwareSettings.Output.Backend == AudioBackendKind.Asio || AudioHardwareSettings.Input.Backend == AudioBackendKind.Asio)
            HardwareErrorText.Text = "ASIO ist in dieser Beta deaktiviert. Bitte für beide Richtungen WASAPI auswählen.";
#endif
        MonitorInputBox.IsChecked = AudioHardwareSettings.MonitorInput;
    }
    private static T Value<T>(ComboBox box) => box.SelectedItem is Choice<T> item ? item.Value : throw new InvalidOperationException("Bitte einen Eintrag auswählen.");
    private static void Select<T>(ComboBox box, T value) => box.SelectedItem = box.Items.Cast<Choice<T>>().FirstOrDefault(x => EqualityComparer<T>.Default.Equals(x.Value, value));
    private void LoadDevices(bool output, string? desiredId)
    {
        try
        {
            var mode = Value<AudioBackendKind>(output ? OutputBackendBox : InputBackendBox);
            var box = output ? OutputDeviceBox : InputDeviceBox;
            var items = AudioHardwareBackend.Default.Enumerate(mode, output ? AudioDirection.Output : AudioDirection.Input).ToList();
            if (output && mode != AudioBackendKind.Asio) items.Insert(0, new("Windows-Standardgerät (beim Start gebunden)", null));
            if (desiredId is not null && items.All(x => x.Id != desiredId)) items.Add(new("Nicht verfügbar — gespeichertes Gerät", desiredId));
            box.ItemsSource = items;
            box.SelectedItem = items.FirstOrDefault(x => x.Id == desiredId);
        }
        catch (Exception e) { HardwareErrorText.Text = e.Message; }
    }
    private void OutputMode_Changed(object sender, SelectionChangedEventArgs e) { if (!loading) LoadDevices(true, null); }
    private void InputMode_Changed(object sender, SelectionChangedEventArgs e) { if (!loading) LoadDevices(false, null); }
    private AudioDeviceConfiguration Read(bool output)
    {
        var channelsText = (output ? OutputChannelsBox : InputChannelsBox).Text.Trim();
        var framesText = (output ? OutputFramesBox : InputFramesBox).Text.Trim();
        var result = new AudioDeviceConfiguration
        {
            Backend = Value<AudioBackendKind>(output ? OutputBackendBox : InputBackendBox),
            DeviceId = ((output ? OutputDeviceBox : InputDeviceBox).SelectedItem as AudioEndpoint)?.Id,
            SampleRate = Value<int>(output ? OutputRateBox : InputRateBox),
            BufferMilliseconds = int.Parse((output ? OutputBufferBox : InputBufferBox).Text),
            Encoding = output ? Value<AudioSampleEncoding>(OutputEncodingBox) : AudioSampleEncoding.Source,
            AsioBufferFrames = framesText.Length == 0 ? null : int.Parse(framesText),
            Channels = channelsText.Length == 0 ? [] : channelsText.Split(',').Select(x => int.Parse(x.Trim()) - 1).ToArray()
        };
        result.Validate(output ? AudioDirection.Output : AudioDirection.Input);
        return result;
    }
    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        try { AudioHardwareSettings.Apply(Read(true), Read(false), MonitorInputBox.IsChecked == true); DialogResult = true; }
        catch (Exception error) { HardwareErrorText.Text = error.Message; }
    }
    private void Capabilities(bool output)
    {
        try { CapabilitiesText.Text = AudioHardwareBackend.Default.DescribeCapabilities(Read(output), output ? AudioDirection.Output : AudioDirection.Input); }
        catch (Exception e) { CapabilitiesText.Text = e.Message; }
    }
    private void Panel(bool output)
    {
        try
        {
            var settings = Read(output);
            if (settings.Backend != AudioBackendKind.Asio) throw new InvalidOperationException("Das Control-Panel ist nur bei ausgewähltem ASIO-Treiber verfügbar.");
            AudioHardwareBackend.Default.ShowControlPanel(settings.DeviceId!);
        }
        catch (Exception e) { CapabilitiesText.Text = e.Message; }
    }
    private void OutputCapabilities_Click(object sender, RoutedEventArgs e) => Capabilities(true);
    private void InputCapabilities_Click(object sender, RoutedEventArgs e) => Capabilities(false);
    private void OutputPanel_Click(object sender, RoutedEventArgs e) => Panel(true);
    private void InputPanel_Click(object sender, RoutedEventArgs e) => Panel(false);
}
