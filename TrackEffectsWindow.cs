using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace WellenEditor;

internal sealed class TrackEffectsWindow : Window
{
    private readonly MultitrackTrack track;
    private readonly Action publish, play, stop;
    private readonly IReadOnlyList<Vst3EffectDescriptor> vstEffects;
    private readonly StackPanel rows = new();

    public TrackEffectsWindow(Window owner, MultitrackTrack track, Action publish, Action play, Action stop, IReadOnlyList<Vst3EffectDescriptor>? vstEffects = null)
    {
        Owner = owner; this.track = track; this.publish = publish; this.play = play; this.stop = stop;
        this.vstEffects = vstEffects ?? Array.Empty<Vst3EffectDescriptor>();
        Title = $"Spur-Effekte – {track.Name}"; Width = 600; Height = 460; MinWidth = 540; MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (Brush)Application.Current.FindResource("Surface"); Foreground = (Brush)Application.Current.FindResource("Text");
        var root = new DockPanel { Margin = new Thickness(14) };
        var top = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        var choices = Enum.GetValues<NativeEffectKind>().Cast<object>().Concat(this.vstEffects.Cast<object>()).ToArray();
        var chooser = new ComboBox { Width = 290, ItemsSource = choices, SelectedIndex = choices.Length == 0 ? -1 : Math.Min(1, choices.Length - 1) };
        chooser.ItemTemplate = new DataTemplate { VisualTree = new FrameworkElementFactory(typeof(TextBlock)) };
        chooser.ItemTemplate.VisualTree.SetBinding(TextBlock.TextProperty, new Binding { Converter = new EffectChoiceConverter() });
        top.Children.Add(chooser);
        var add = new Button { Content = "+ Hinzufügen" };
        add.Click += (_, _) => { if (chooser.SelectedItem is Vst3EffectDescriptor vst) AddVst(vst); else if (chooser.SelectedItem is NativeEffectKind kind) Edit(new TrackEffectSlot(Guid.NewGuid(), kind, EffectSettings.For(kind)), true); };
        top.Children.Add(add); DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        var bottom = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var listen = new Button { Content = "▶ Anhören" }; listen.Click += (_, _) => play(); bottom.Children.Add(listen);
        var halt = new Button { Content = "■ Stopp" }; halt.Click += (_, _) => stop(); bottom.Children.Add(halt);
        bottom.Children.Add(new Button { Content = "Schließen", IsCancel = true }); DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom);
        root.Children.Add(new ScrollViewer { Content = rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }); Content = root; Refresh();
    }

    private void Edit(TrackEffectSlot slot, bool adding)
    {
        var before = track.Effects; if (adding) { track.SetEffects(before.Append(slot)); publish(); }
        var dialog = new EffectDialog(this, slot.Kind, slot.Settings, trackMode: true);
        void Update(EffectSettings settings) { track.SetEffects(track.Effects.Select(fx => fx.Id == slot.Id ? fx with { Settings = settings } : fx)); publish(); }
        dialog.SettingsChanged += Update; dialog.PreviewRequested += settings => { Update(settings); play(); };
        if (dialog.ShowDialog() == true) Update(dialog.Settings); else { track.SetEffects(before); publish(); } Refresh();
    }

    private void AddVst(Vst3EffectDescriptor descriptor)
    {
        var slot = TrackEffectSlot.ForVst(descriptor); var before = track.Effects; track.SetEffects(before.Append(slot)); publish(); EditVst(slot, true);
    }

    private void EditVst(TrackEffectSlot slot, bool adding)
    {
        if (slot.Vst is null) return; var before = track.Effects;
        try
        {
            var dialog = new VstPluginDialog(this, slot.Vst, track.ProjectSampleRate);
            if (dialog.LoadErrorText is not null) { dialog.ShowDialog(); if (adding) track.SetEffects(before); }
            else if (dialog.ShowDialog() == true) track.SetEffects(track.Effects.Select(fx => fx.Id == slot.Id ? fx with { VstState = dialog.ResultState } : fx));
            else if (adding) track.SetEffects(before);
        }
        catch (Exception ex) { MessageBox.Show(this, $"VST3-Effekt konnte nicht geladen werden.\n\n{ex.Message}", "WaveBench Pro", MessageBoxButton.OK, MessageBoxImage.Error); if (adding) track.SetEffects(before); }
        publish(); Refresh();
    }

    private void Refresh()
    {
        rows.Children.Clear();
        if (track.Effects.Length == 0) rows.Children.Add(new TextBlock { Text = "Keine Spur-Effekte", Foreground = Brushes.Gray, Margin = new Thickness(5) });
        foreach (var slot in track.Effects)
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            var enabled = new CheckBox { IsChecked = slot.Enabled, Content = "Ein", Foreground = Foreground, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 12, 0) };
            enabled.Click += (_, _) => { track.SetEffects(track.Effects.Select(fx => fx.Id == slot.Id ? fx with { Enabled = enabled.IsChecked == true } : fx)); publish(); }; row.Children.Add(enabled);
            var remove = new Button { Content = "×", ToolTip = "Effekt entfernen", Foreground = Brushes.IndianRed }; remove.Click += (_, _) => { track.SetEffects(track.Effects.Where(fx => fx.Id != slot.Id)); publish(); Refresh(); }; DockPanel.SetDock(remove, Dock.Right); row.Children.Add(remove);
            var edit = new Button { Content = "Bearbeiten…" }; edit.Click += (_, _) => { var current = track.Effects.First(fx => fx.Id == slot.Id); if (current.Vst is not null) EditVst(current, false); else Edit(current, false); }; DockPanel.SetDock(edit, Dock.Right); row.Children.Add(edit);
            row.Children.Add(new TextBlock { Text = slot.Vst is null ? NativeEffects.DisplayName(slot.Kind) : $"VST3: {slot.Vst.DisplayName}", VerticalAlignment = VerticalAlignment.Center });
            rows.Children.Add(new Border { Child = row, Padding = new Thickness(5), Background = (Brush)Application.Current.FindResource("Panel") });
        }
    }

    private sealed class EffectChoiceConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is Vst3EffectDescriptor vst ? $"VST3: {vst.DisplayName}" : NativeEffects.DisplayName((NativeEffectKind)value);
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}
