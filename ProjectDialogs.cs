using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace WellenEditor;

internal enum RecoveryChoice { Later, Restore, Discard }
internal sealed class ProjectRecoveryDialog : Window
{
    private readonly ListBox entries = new() { MinHeight = 110, MaxHeight = 300 };
    internal ProjectRecovery.Entry? Selected => (entries.SelectedItem as RecoveryRow)?.Entry;
    internal RecoveryChoice Choice { get; private set; }
    private sealed record RecoveryRow(ProjectRecovery.Entry Entry)
    { public override string ToString() => $"{Entry.Name} — {Entry.SavedUtc.ToLocalTime():g}\n{Entry.OriginalPath ?? "Noch nicht gespeichert"}"; }
    internal ProjectRecoveryDialog(Window? owner, IReadOnlyList<ProjectRecovery.Entry> candidates)
    {
        if (owner is not null) Owner = owner; Title = "Projekt wiederherstellen"; Width = 650; SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = "Unbestätigte Projekt-Sicherungen. Wiederherstellen ändert die Originaldatei nicht.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
        foreach (var candidate in candidates) entries.Items.Add(new RecoveryRow(candidate));
        if (entries.Items.Count > 0) entries.SelectedIndex = 0; panel.Children.Add(entries);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        foreach (var (label, choice) in new[] { ("Wiederherstellen", RecoveryChoice.Restore), ("Verwerfen", RecoveryChoice.Discard), ("Später", RecoveryChoice.Later) })
        {
            var button = new Button { Content = label, MinWidth = 120, Margin = new Thickness(4), IsCancel = choice == RecoveryChoice.Later };
            button.Click += (_, _) => { Choice = choice; DialogResult = choice != RecoveryChoice.Later; }; buttons.Children.Add(button);
        }
        panel.Children.Add(buttons); Content = panel;
    }
}
internal sealed class ProjectRelinkDialog : Window
{
    private readonly Dictionary<Guid, TextBox> paths = new();
    private readonly CancellationTokenSource cancellation = new();
    internal IReadOnlyDictionary<Guid, string>? Result { get; private set; }
    internal ProjectRelinkDialog(Window? owner, IReadOnlyList<ProjectStorage.SourceIssue> issues)
    {
        if (owner is not null) Owner = owner; Title = "Projektquellen neu zuordnen"; Width = 760; MaxHeight = 650;
        SizeToContent = SizeToContent.Height; WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = "Quellen fehlen oder wurden verändert. Wähle byteidentische Originaldateien. Abbrechen lässt das aktuelle Projekt unverändert.", TextWrapping = TextWrapping.Wrap });
        var rows = new StackPanel();
        foreach (var issue in issues)
        {
            rows.Children.Add(new TextBlock { Text = issue.Reason + ": " + issue.Path, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 3) });
            var row = new DockPanel(); var browse = new Button { Content = "Suchen …", Margin = new Thickness(8, 0, 0, 0), MinWidth = 85 };
            DockPanel.SetDock(browse, Dock.Right); row.Children.Add(browse);
            var box = new TextBox { Text = issue.Path, MinWidth = 400 }; paths.Add(issue.Id, box); row.Children.Add(box);
            browse.Click += (_, _) => { var dialog = new OpenFileDialog { Title = "Originalquelle zuordnen", Filter = "Audiodateien|*.wav;*.flac;*.mp3;*.ogg;*.opus;*.aac;*.m4a;*.wma|Alle Dateien|*.*" }; if (dialog.ShowDialog(this) == true) box.Text = dialog.FileName; }; rows.Children.Add(row);
        }
        panel.Children.Add(new ScrollViewer { Content = rows, MaxHeight = 400, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var error = new TextBlock { Foreground = Brushes.Firebrick, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) }; panel.Children.Add(error);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var ok = new Button { Content = "Prüfen und öffnen", MinWidth = 150, Margin = new Thickness(4), IsDefault = true };
        var cancel = new Button { Content = "Abbrechen", MinWidth = 100, Margin = new Thickness(4), IsCancel = true };
        ok.Click += async (_, _) =>
        {
            ok.IsEnabled = false; error.Text = "Originalidentität wird geprüft …";
            try { var result = paths.ToDictionary(pair => pair.Key, pair => pair.Value.Text); await ProjectStorage.ValidateRelinksAsync(issues, result, cancellation.Token); Result = result; DialogResult = true; }
            catch (OperationCanceledException) { }
            catch (Exception e) { error.Text = e.Message; }
            finally { ok.IsEnabled = true; }
        };
        Closed += (_, _) => cancellation.Cancel(); buttons.Children.Add(ok); buttons.Children.Add(cancel); panel.Children.Add(buttons); Content = panel;
    }
}
