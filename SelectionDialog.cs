using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace WellenEditor;

/// <summary>Direkte Zeiteingabe für eine samplescharfe Auswahl.</summary>
internal sealed class SelectionDialog : Window
{
    private readonly TextBox startBox = new() { MinWidth = 140 };
    private readonly TextBox endBox = new() { MinWidth = 140 };
    private readonly int sampleRate;
    private readonly int frameCount;

    public SelectionDialog(Window owner, int sampleRate, int frameCount, Selection? current)
    {
        this.sampleRate = sampleRate;
        this.frameCount = frameCount;
        Owner = owner;
        Title = "Auswahl präzise setzen";
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        ShowInTaskbar = false;
        Background = (Brush)FindResource("Surface");
        Foreground = (Brush)FindResource("Text");

        var start = current?.StartSample ?? 0;
        var end = current?.EndSample ?? frameCount;
        startBox.Text = Format(start);
        endBox.Text = Format(end);

        var grid = new Grid { Margin = new Thickness(18) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        AddRow(grid, 0, "Start", startBox);
        AddRow(grid, 1, "Ende", endBox);

        var note = new TextBlock { Text = "Format: Minuten:Sekunden.Millisekunden", Foreground = (Brush)FindResource("Muted"), Margin = new Thickness(0, 3, 0, 12) };
        Grid.SetRow(note, 2); Grid.SetColumn(note, 1); grid.Children.Add(note);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var ok = new Button { Content = "Übernehmen", IsDefault = true, MinWidth = 105, Margin = new Thickness(0, 0, 8, 0) };
        ok.Click += Ok_Click;
        var cancel = new Button { Content = "Abbrechen", IsCancel = true, MinWidth = 95 };
        cancel.Click += (_, _) => DialogResult = false;
        buttons.Children.Add(ok); buttons.Children.Add(cancel);
        Grid.SetRow(buttons, 3); Grid.SetColumnSpan(buttons, 2); grid.Children.Add(buttons);
        Content = grid;
    }

    public Selection Result { get; private set; }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (!TryParse(startBox.Text, out var start) || !TryParse(endBox.Text, out var end) || end <= start)
        {
            MessageBox.Show(this, "Bitte einen gültigen Bereich eingeben; Ende muss nach Start liegen.", "WaveBench Pro", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        Result = new Selection(start, end);
        DialogResult = true;
    }

    private bool TryParse(string text, out int frame)
    {
        if (!TimeSpan.TryParse(text.Trim(), CultureInfo.InvariantCulture, out var time) || time < TimeSpan.Zero)
        {
            frame = 0;
            return false;
        }
        frame = (int)Math.Clamp(Math.Round(time.TotalSeconds * sampleRate), 0, frameCount);
        return true;
    }

    private string Format(int frame) => TimeSpan.FromSeconds(frame / (double)sampleRate).ToString(@"m\:ss\.fff", CultureInfo.InvariantCulture);

    private static void AddRow(Grid grid, int row, string label, Control control)
    {
        var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 8) };
        Grid.SetRow(text, row); grid.Children.Add(text);
        control.Margin = new Thickness(0, 0, 0, 8);
        Grid.SetRow(control, row); Grid.SetColumn(control, 1); grid.Children.Add(control);
    }
}
