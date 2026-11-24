using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace WellenEditor;

/// <summary>Dunkler Effekt-Dialog mit Presets, Reglern und Vorschau für Planpunkt 0.4.</summary>
internal sealed class EffectDialog : Window
{
    private readonly NativeEffectKind kind;
    private readonly List<Action<EffectSettings>> updaters = new();
    private readonly ComboBox presetBox = new();
    private readonly StackPanel parameterRows = new();
    private bool updating;
    private EffectSettings current;
    private readonly bool trackMode;

    public EffectDialog(Window owner, NativeEffectKind kind, EffectSettings? initial = null, bool trackMode = false)
    {
        Owner = owner;
        this.kind = kind;
        this.trackMode = trackMode;
        current = EffectSettings.For(kind);
        Settings = current;

        Title = $"{DisplayName(kind)} – Effekt";
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        MinWidth = 430;
        ShowInTaskbar = false;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13;
        Background = Application.Current.FindResource("Surface") as Brush ?? Brushes.Black;
        Foreground = Application.Current.FindResource("Text") as Brush ?? Brushes.White;

        BuildUi();
        ApplySettings(initial ?? current);
    }

    public EffectSettings Settings { get; private set; }

    public event Action<EffectSettings>? PreviewRequested;
    public event Action<EffectSettings>? SettingsChanged;

    private void BuildUi()
    {
        var root = new StackPanel { Margin = new Thickness(18, 16, 18, 14) };
        root.Children.Add(new TextBlock
        {
            Text = trackMode ? $"{DisplayName(kind)} – Spur-Effekt" : $"{DisplayName(kind)} auf die Auswahl anwenden (ohne Auswahl auf den gesamten Clip).",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Application.Current.FindResource("Muted") as Brush ?? Brushes.Gray,
            Margin = new Thickness(0, 0, 0, 12)
        });

        presetBox.ItemsSource = EffectSettings.Presets(kind);
        var keyTemplate = new DataTemplate(typeof(KeyValuePair<string, EffectSettings>));
        var keyText = new FrameworkElementFactory(typeof(TextBlock));
        keyText.SetBinding(TextBlock.TextProperty, new Binding("Key"));
        keyTemplate.VisualTree = keyText;
        presetBox.ItemTemplate = keyTemplate;
        presetBox.SelectedIndex = 1;
        presetBox.SelectionChanged += (_, _) =>
        {
            if (updating || presetBox.SelectedItem is not KeyValuePair<string, EffectSettings> preset) return;
            ApplySettings(preset.Value);
        };
        root.Children.Add(Labeled("Preset", presetBox));
        root.Children.Add(parameterRows);

        switch (kind)
        {
            case NativeEffectKind.Gain:
                AddDb("Verstärkung", -24, 12, settings => settings.GainDb, (settings, value) => settings with { GainDb = value });
                break;
            case NativeEffectKind.Equalizer:
                AddFrequency("Frequenz", settings => settings.Frequency, (settings, value) => settings with { Frequency = value });
                AddLinear("Q", 0.3, 10, settings => settings.Q, (settings, value) => settings with { Q = value }, "0.00");
                AddDb("Anhebung / Absenkung", -18, 18, settings => settings.GainDb, (settings, value) => settings with { GainDb = value });
                break;
            case NativeEffectKind.HighPass:
            case NativeEffectKind.LowPass:
                AddFrequency("Grenzfrequenz", settings => settings.Frequency, (settings, value) => settings with { Frequency = value });
                break;
            case NativeEffectKind.Compressor:
                AddDb("Schwelle", -60, 0, settings => settings.ThresholdDb, (settings, value) => settings with { ThresholdDb = value });
                AddLinear("Ratio", 1, 20, settings => settings.Ratio, (settings, value) => settings with { Ratio = value }, "0.0");
                AddLinear("Angriff (ms)", 1, 100, settings => settings.AttackMs, (settings, value) => settings with { AttackMs = value }, "0");
                AddLinear("Freigabe (ms)", 20, 1000, settings => settings.ReleaseMs, (settings, value) => settings with { ReleaseMs = value }, "0");
                AddDb("Make-up", 0, 12, settings => settings.MakeupDb, (settings, value) => settings with { MakeupDb = value });
                break;
            case NativeEffectKind.Limiter:
                AddDb("Schwelle", -12, 0, settings => settings.ThresholdDb, (settings, value) => settings with { ThresholdDb = value });
                break;
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var preview = new Button { Content = trackMode ? "▶ Live" : "Vorschau", MinWidth = 100, Margin = new Thickness(0, 0, 8, 0), FontSize = 13 };
        preview.Click += (_, _) => PreviewRequested?.Invoke(current);
        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 96, Margin = new Thickness(0, 0, 8, 0), FontSize = 13 };
        ok.Click += (_, _) => { Settings = current; DialogResult = true; };
        var cancel = new Button { Content = "Abbrechen", IsCancel = true, MinWidth = 110, FontSize = 13 };
        cancel.Click += (_, _) => { DialogResult = false; };
        buttons.Children.Add(preview);
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);
        Content = root;
    }

    private static string DisplayName(NativeEffectKind kind) => NativeEffects.DisplayName(kind);

    private StackPanel Labeled(string label, UIElement control)
    {
        var row = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        row.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 3) });
        row.Children.Add(control);
        return row;
    }

    private void AddDb(string label, double min, double max, Func<EffectSettings, double> read, Func<EffectSettings, double, EffectSettings> update) =>
        AddLinear(label, min, max, read, update, "0.0");

    private void AddLinear(string label, double min, double max, Func<EffectSettings, double> read, Func<EffectSettings, double, EffectSettings> update, string format)
    {
        var value = new TextBlock { TextAlignment = TextAlignment.Right, MinWidth = 58, Margin = new Thickness(8, 0, 0, 0) };
        var slider = new Slider { Minimum = min, Maximum = max, Height = 24, VerticalAlignment = VerticalAlignment.Center };
        slider.ValueChanged += (_, _) =>
        {
            if (updating) return;
            current = update(current, slider.Value);
            SettingsChanged?.Invoke(current);
            value.Text = slider.Value.ToString(format, System.Globalization.CultureInfo.GetCultureInfo("de-DE"));
        };
        updaters.Add(settings => { updating = true; slider.Value = Math.Clamp(read(settings), min, max); updating = false; value.Text = slider.Value.ToString(format, System.Globalization.CultureInfo.GetCultureInfo("de-DE")); });
        parameterRows.Children.Add(Row(label, slider, value));
    }

    private void AddFrequency(string label, Func<EffectSettings, double> read, Func<EffectSettings, double, EffectSettings> update)
    {
        const double min = 0;
        const double max = 1000;
        var value = new TextBlock { TextAlignment = TextAlignment.Right, MinWidth = 58, Margin = new Thickness(8, 0, 0, 0) };
        var slider = new Slider { Minimum = min, Maximum = max, Height = 24, VerticalAlignment = VerticalAlignment.Center };
        static double ToFrequency(double position) => 20 * Math.Pow(1000, position / max);
        static double ToPosition(double frequency) => max * Math.Log(Math.Clamp(frequency, 20, 20000) / 20d) / Math.Log(1000);
        slider.ValueChanged += (_, _) =>
        {
            if (updating) return;
            current = update(current, ToFrequency(slider.Value));
            SettingsChanged?.Invoke(current);
            value.Text = $"{ToFrequency(slider.Value):0} Hz";
        };
        updaters.Add(settings =>
        {
            updating = true;
            slider.Value = Math.Clamp(ToPosition(read(settings)), min, max);
            updating = false;
            value.Text = $"{read(settings):0} Hz";
        });
        parameterRows.Children.Add(Row(label, slider, value));
    }

    private static Grid Row(string label, Slider slider, TextBlock value)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var labelBlock = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(labelBlock, 0);
        Grid.SetColumn(slider, 2);
        Grid.SetColumn(value, 3);
        grid.Children.Add(labelBlock);
        grid.Children.Add(slider);
        grid.Children.Add(value);
        return grid;
    }

    private void ApplySettings(EffectSettings settings)
    {
        current = settings;
        foreach (var update in updaters) update(settings);
        SettingsChanged?.Invoke(current);
    }
}
