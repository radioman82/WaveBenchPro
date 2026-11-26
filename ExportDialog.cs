using Microsoft.Win32;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace WellenEditor;

internal enum ExportFormat { Wav, Mp3, Flac, Ogg, Opus }

/// <summary>Kompakter Export-Dialog. Erzeugt eine Kopie und lässt das Dokument unangetastet.</summary>
internal sealed class ExportDialog : Window
{
    private readonly AudioDocument document;
    private readonly Selection? selection;
    private readonly TextBox pathBox = new() { MinWidth = 330 };
    private readonly ComboBox formatBox = new() { Width = 150 };
    private readonly ComboBox rangeBox = new() { Width = 250 };
    private readonly ComboBox qualityBox = new() { Width = 150 };
    private readonly TextBlock hintBlock = new() { TextWrapping = TextWrapping.Wrap };

    public ExportDialog(Window owner, AudioDocument document, Selection? selection)
    {
        this.document = document;
        this.selection = selection;

        Owner = owner;
        Title = "WaveBench Pro – Exportieren";
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        MinWidth = 490;
        ShowInTaskbar = false;
        Background = (Brush)FindResource("Surface");
        Foreground = (Brush)FindResource("Text");
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 11;

        formatBox.Items.Add("WAV");
        formatBox.Items.Add("MP3");
        formatBox.Items.Add("FLAC");
        formatBox.Items.Add("OGG Vorbis");
        formatBox.Items.Add("Opus");
        formatBox.SelectedIndex = 0;

        rangeBox.Items.Add("Gesamte Datei");
        if (selection.HasValue && selection.Value.Length > 0)
        {
            var start = selection.Value.StartSample / (double)document.SampleRate;
            var end = selection.Value.EndSample / (double)document.SampleRate;
            rangeBox.Items.Add($"Nur Auswahl ({TimeText(start)} – {TimeText(end)})");
        }
        rangeBox.SelectedIndex = 0;

        hintBlock.Foreground = (Brush)FindResource("Muted");
        hintBlock.Margin = new Thickness(0, 0, 0, 8);
        hintBlock.Text = "Der Export erstellt eine Kopie. Das geöffnete Dokument bleibt unverändert.";

        BuildQualityItems();
        pathBox.Text = ExportPathResolver.Resolve($"{DefaultStem}_export{Extension}", document.SourcePath);

        var browseButton = new Button { Content = "Durchsuchen...", Margin = new Thickness(8, 0, 0, 0), MinWidth = 110 };
        browseButton.Click += Browse_Click;

        var exportButton = new Button { Content = "Exportieren", IsDefault = true, MinWidth = 120 };
        exportButton.Click += Export_Click;
        var cancelButton = new Button { Content = "Abbrechen", IsCancel = true, MinWidth = 110 };
        cancelButton.Click += (_, _) => { DialogResult = false; Close(); };

        var grid = new Grid { Margin = new Thickness(18) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        AddRow(grid, 0, "Zieldatei", pathBox, browseButton);
        AddRow(grid, 1, "Format", formatBox, null);
        AddRow(grid, 2, "Bereich", rangeBox, null);
        AddRow(grid, 3, "Qualität", qualityBox, null);

        Grid.SetRow(hintBlock, 4);
        Grid.SetColumn(hintBlock, 1);
        Grid.SetColumnSpan(hintBlock, 2);
        grid.Children.Add(hintBlock);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        Grid.SetRow(buttons, 5);
        Grid.SetColumn(buttons, 0);
        Grid.SetColumnSpan(buttons, 3);
        buttons.Children.Add(exportButton);
        buttons.Children.Add(cancelButton);
        grid.Children.Add(buttons);

        Content = grid;
        formatBox.SelectionChanged += (_, _) =>
        {
            BuildQualityItems();
            pathBox.Text = WithExtension(pathBox.Text, Extension);
        };
    }

    public ExportFormat Format => (ExportFormat)formatBox.SelectedIndex;
    public bool IsMp3 => Format == ExportFormat.Mp3;
    public bool ExportSelection => rangeBox.SelectedIndex == 1 && selection.HasValue;
    public string TargetPath => pathBox.Text.Trim();
    public int BitsPerSample => qualityBox.SelectedIndex switch { 0 => 16, 1 => 24, _ => 32 };
    public int BitRate => qualityBox.SelectedIndex == 1 ? 320 : 192;
    public double VbrQuality => qualityBox.SelectedIndex switch { 0 => .4, 2 => .8, 3 => 1.0, _ => .6 };

    private string Extension => Format switch
    {
        ExportFormat.Mp3 => ".mp3",
        ExportFormat.Flac => ".flac",
        ExportFormat.Ogg => ".ogg",
        ExportFormat.Opus => ".opus",
        _ => ".wav"
    };

    private string DefaultStem
    {
        get
        {
            var name = Path.GetFileNameWithoutExtension(document.DisplayName);
            if (string.IsNullOrWhiteSpace(name)) name = "Audio";
            var invalid = Path.GetInvalidFileNameChars();
            return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        }
    }

    private static void AddRow(Grid grid, int row, string label, FrameworkElement control, UIElement? extra)
    {
        var text = new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
            Foreground = (Brush)Application.Current.FindResource("Muted")
        };
        Grid.SetRow(text, row);
        Grid.SetColumn(text, 0);
        grid.Children.Add(text);

        control.Margin = new Thickness(0, 0, 0, 8);
        Grid.SetRow(control, row);
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);

        if (extra is null) return;
        Grid.SetRow(extra, row);
        Grid.SetColumn(extra, 2);
        grid.Children.Add(extra);
    }

    private void BuildQualityItems()
    {
        qualityBox.Items.Clear();
        if (IsMp3)
        {
            qualityBox.Items.Add("192 kBit/s");
            qualityBox.Items.Add("320 kBit/s");
        }
        else if (Format == ExportFormat.Wav)
        {
            qualityBox.Items.Add("16 Bit PCM");
            qualityBox.Items.Add("24 Bit PCM");
            qualityBox.Items.Add("32 Bit Float");
        }
        else if (Format == ExportFormat.Flac)
        {
            qualityBox.Items.Add("Verlustfrei · Standard-Kompression");
        }
        else
        {
            qualityBox.Items.Add("40 % · kleinere Datei");
            qualityBox.Items.Add("60 % · Standard");
            qualityBox.Items.Add("80 % · hohe Qualität");
            qualityBox.Items.Add("100 % · höchste Qualität");
        }
        qualityBox.SelectedIndex = (Format is ExportFormat.Ogg or ExportFormat.Opus) ? 1 : 0;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Filter = $"{formatBox.SelectedItem}|*{Extension}",
            FileName = Path.GetFileName(pathBox.Text)
        };
        if (dialog.ShowDialog(this) != true) return;
        pathBox.Text = Path.GetFullPath(dialog.FileName);
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var raw = TargetPath;
        if (raw.Length == 0)
        {
            MessageBox.Show(this, "Bitte einen Zieldateinamen angeben.", "WaveBench Pro", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string fullPath;
        try { fullPath = ExportPathResolver.Resolve(WithExtension(raw, Extension), document.SourcePath); }
        catch (Exception exception)
        {
            MessageBox.Show(this, $"Ungültiger Zielpfad.\n\n{exception.Message}", "WaveBench Pro", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            MessageBox.Show(this, "Das Zielverzeichnis existiert nicht.", "WaveBench Pro", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (ExportSelection && (!selection.HasValue || selection.Value.Length <= 0))
        {
            MessageBox.Show(this, "Es ist keine Auswahl markiert.", "WaveBench Pro", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (File.Exists(fullPath) &&
            MessageBox.Show(this, $"„{Path.GetFileName(fullPath)}“ existiert bereits. Überschreiben?", "WaveBench Pro", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        pathBox.Text = fullPath;
        DialogResult = true;
        Close();
    }

    private static string WithExtension(string path, string extension)
    {
        var trimmed = path.Trim();
        if (trimmed.Length == 0) return trimmed;
        try
        {
            var without = Path.ChangeExtension(trimmed, null) ?? trimmed;
            return without + extension;
        }
        catch { return trimmed.EndsWith(extension, StringComparison.OrdinalIgnoreCase) ? trimmed : trimmed + extension; }
    }

    private static string TimeText(double seconds) => TimeSpan.FromSeconds(seconds).ToString(@"m\:ss\.fff");
}
