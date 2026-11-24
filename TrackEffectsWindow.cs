using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace WellenEditor;

internal sealed class TrackEffectsWindow : Window
{
    private readonly MultitrackTrack track;
    private readonly Action publish, play, stop;
    private readonly StackPanel rows = new();
    public TrackEffectsWindow(Window owner, MultitrackTrack track, Action publish, Action play, Action stop)
    {
        Owner = owner; this.track = track; this.publish = publish; this.play = play; this.stop = stop;
        Title = $"Spur-Effekte – {track.Name}";
        Width = 540; Height = 420; MinWidth = 510; MinHeight = 280;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (Brush)Application.Current.FindResource("Surface");
        Foreground = (Brush)Application.Current.FindResource("Text");
        var root = new DockPanel { Margin = new Thickness(14) };
        var top = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        var kinds = Enum.GetValues<NativeEffectKind>();
        var chooser = new ComboBox { Width = 180, ItemsSource = kinds.Select(NativeEffects.DisplayName).ToArray(), SelectedIndex = 1 };
        top.Children.Add(chooser);
        var add = new Button { Content = "+ Hinzufügen" };
        add.Click += (_, _) => Edit(new TrackEffectSlot(Guid.NewGuid(), kinds[chooser.SelectedIndex], EffectSettings.For(kinds[chooser.SelectedIndex])), true);
        top.Children.Add(add); DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        var bottom = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var listen = new Button { Content = "▶ Anhören" }; listen.Click += (_, _) => play(); bottom.Children.Add(listen);
        var halt = new Button { Content = "■ Stopp" }; halt.Click += (_, _) => stop(); bottom.Children.Add(halt);
        var close = new Button { Content = "Schließen", IsCancel = true }; bottom.Children.Add(close);
        DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom);
        root.Children.Add(new ScrollViewer { Content = rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root; Refresh();
    }
    private void Edit(TrackEffectSlot slot, bool adding)
    {
        var before = track.Effects;
        if (adding) { track.SetEffects(before.Append(slot)); publish(); }
        var dialog = new EffectDialog(this, slot.Kind, slot.Settings, trackMode: true);
        void Update(EffectSettings settings)
        {
            track.SetEffects(track.Effects.Select(fx => fx.Id == slot.Id ? fx with { Settings = settings } : fx)); publish();
        }
        dialog.SettingsChanged += Update;
        dialog.PreviewRequested += settings => { Update(settings); play(); };
        if (dialog.ShowDialog() == true) Update(dialog.Settings);
        else { track.SetEffects(before); publish(); }
        Refresh();
    }
    private void Refresh()
    {
        rows.Children.Clear();
        if (track.Effects.Length == 0) rows.Children.Add(new TextBlock { Text = "Keine Spur-Effekte", Foreground = Brushes.Gray, Margin = new Thickness(5) });
        foreach (var slot in track.Effects)
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            var enabled = new CheckBox { IsChecked = slot.Enabled, Content = "Ein", Foreground = Foreground, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 12, 0) };
            enabled.Click += (_, _) => { track.SetEffects(track.Effects.Select(fx => fx.Id == slot.Id ? fx with { Enabled = enabled.IsChecked == true } : fx)); publish(); };
            row.Children.Add(enabled);
            var remove = new Button { Content = "×", ToolTip = "Effekt entfernen", Foreground = Brushes.IndianRed };
            remove.Click += (_, _) => { track.SetEffects(track.Effects.Where(fx => fx.Id != slot.Id)); publish(); Refresh(); };
            DockPanel.SetDock(remove, Dock.Right); row.Children.Add(remove);
            var edit = new Button { Content = "Bearbeiten…" };
            edit.Click += (_, _) => Edit(track.Effects.First(fx => fx.Id == slot.Id), false);
            DockPanel.SetDock(edit, Dock.Right); row.Children.Add(edit);
            row.Children.Add(new TextBlock { Text = NativeEffects.DisplayName(slot.Kind), VerticalAlignment = VerticalAlignment.Center });
            rows.Children.Add(new Border { Child = row, Padding = new Thickness(5), Background = (Brush)Application.Current.FindResource("Panel") });
        }
    }
}
