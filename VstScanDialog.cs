using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace WellenEditor;

/// <summary>Knoten im Verzeichnisbaum des Scan-Dialogs.</summary>
internal sealed class ScanTreeNode : System.ComponentModel.INotifyPropertyChanged
{
    public string Name { get; }
    public string FullPath { get; }
    public ObservableCollection<ScanTreeNode> Children { get; } = new();
    public bool IsDrive { get; }
    public bool IsDirectory { get; }

    private bool isChecked;
    public bool IsChecked
    {
        get => isChecked;
        set
        {
            if (isChecked == value) return;
            isChecked = value;
            PropertyChanged?.Invoke(this, new(nameof(IsChecked)));
            foreach (var child in Children) child.IsChecked = value;
        }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    public ScanTreeNode(string name, string fullPath, bool isDrive = false, bool isDirectory = true)
    {
        Name = name;
        FullPath = fullPath;
        IsDrive = isDrive;
        IsDirectory = isDirectory;
    }
}

/// <summary>Dialog zum Auswählen von Laufwerken/Ordnern und Scannen nach VST3-Plugins mit Fortschritt.</summary>
internal sealed class VstScanDialog : Window
{
    private readonly TreeView tree;
    private readonly TextBlock statusText;
    private readonly ProgressBar progressBar;
    private readonly Button scanButton;
    private readonly Button cancelButton;
    private CancellationTokenSource? cts;
    public IReadOnlyList<Vst3EffectDescriptor>? Result { get; private set; }

    public VstScanDialog(Window owner)
    {
        Owner = owner;
        Title = "VST3 scannen – Ordner auswählen";
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Width = 640;
        Height = 520;
        MinWidth = 480;
        MinHeight = 360;
        ShowInTaskbar = false;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13;
        Background = Application.Current.FindResource("Surface") as Brush ?? Brushes.Black;
        Foreground = Application.Current.FindResource("Text") as Brush ?? Brushes.White;

        var root = new DockPanel { Margin = new Thickness(10) };

        var header = new TextBlock
        {
            Text = "Laufwerke und Ordner auswählen, die nach VST3-Plugins durchsucht werden sollen:",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Application.Current.FindResource("Muted") as Brush ?? Brushes.Gray,
            Margin = new Thickness(0, 0, 0, 8)
        };
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        tree = new TreeView
        {
            Background = Application.Current.FindResource("Panel") as Brush ?? Brushes.Transparent,
            BorderBrush = Application.Current.FindResource("Line") as Brush ?? Brushes.Gray,
            Foreground = Application.Current.FindResource("Text") as Brush ?? Brushes.White
        };
        tree.ItemTemplate = BuildItemTemplate();
        tree.ItemsSource = BuildTree();
        DockPanel.SetDock(tree, Dock.Top);
        root.Children.Add(tree);

        // Fortschritt
        var progressPanel = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        statusText = new TextBlock { Foreground = Application.Current.FindResource("Muted") as Brush ?? Brushes.Gray, TextWrapping = TextWrapping.Wrap };
        progressBar = new ProgressBar { Height = 8, Minimum = 0, Maximum = 100, Value = 0, Margin = new Thickness(0, 4, 0, 0) };
        progressPanel.Children.Add(statusText);
        progressPanel.Children.Add(progressBar);
        DockPanel.SetDock(progressPanel, Dock.Bottom);
        root.Children.Add(progressPanel);

        // Buttons
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        scanButton = new Button { Content = "Scannen", IsDefault = true, MinWidth = 110, Margin = new Thickness(0, 0, 8, 0) };
        scanButton.Click += async (_, _) => await RunScanAsync();
        cancelButton = new Button { Content = "Schließen", IsCancel = true, MinWidth = 100 };
        cancelButton.Click += (_, _) => { CancelScan(); Close(); };
        buttons.Children.Add(scanButton);
        buttons.Children.Add(cancelButton);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);

        Content = root;
    }

    private DataTemplate BuildItemTemplate()
    {
        var factory = new FrameworkElementFactory(typeof(StackPanel));
        factory.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        var check = new FrameworkElementFactory(typeof(CheckBox));
        check.SetBinding(CheckBox.IsCheckedProperty, new System.Windows.Data.Binding(nameof(ScanTreeNode.IsChecked)) { Mode = System.Windows.Data.BindingMode.TwoWay });
        check.SetValue(CheckBox.VerticalAlignmentProperty, VerticalAlignment.Center);
        var label = new FrameworkElementFactory(typeof(TextBlock));
        label.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(ScanTreeNode.Name)));
        label.SetValue(TextBlock.MarginProperty, new Thickness(6, 0, 0, 0));
        label.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
        factory.AppendChild(check);
        factory.AppendChild(label);

        var itemTemplate = new HierarchicalDataTemplate { ItemsSource = new System.Windows.Data.Binding(nameof(ScanTreeNode.Children)), VisualTree = factory };
        return new DataTemplate { VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)) };
    }

    private ObservableCollection<ScanTreeNode> BuildTree()
    {
        var nodes = new ObservableCollection<ScanTreeNode>();
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady).OrderBy(d => d.Name))
        {
            var label = $"{drive.Name} {drive.DriveType}";
            if (!string.IsNullOrWhiteSpace(drive.VolumeLabel)) label = $"{drive.VolumeLabel} ({drive.Name.TrimEnd('\\')})";
            var node = new ScanTreeNode(label, drive.RootDirectory.FullName, isDrive: true) { IsChecked = true };
            try
            {
                foreach (var dir in Directory.EnumerateDirectories(drive.RootDirectory.FullName))
                {
                    try
                    {
                        var name = Path.GetFileName(dir);
                        if (string.IsNullOrWhiteSpace(name)) continue;
                        node.Children.Add(new ScanTreeNode(name, dir) { IsChecked = true });
                    }
                    catch { /* ignorieren */ }
                }
            }
            catch { /* Laufwerk nicht lesbar */ }
            nodes.Add(node);
        }
        return nodes;
    }

    private IEnumerable<string> GetSelectedPaths()
    {
        foreach (var node in tree.Items.OfType<ScanTreeNode>())
        {
            if (node.IsChecked) yield return node.FullPath;
            foreach (var child in node.Children.Where(c => c.IsChecked))
                yield return child.FullPath;
        }
    }

    private async Task RunScanAsync()
    {
        var selected = GetSelectedPaths().ToList();
        if (selected.Count == 0)
        {
            statusText.Text = "Bitte mindestens ein Laufwerk oder einen Ordner auswählen.";
            return;
        }
        CancelScan();
        cts = new CancellationTokenSource();
        scanButton.IsEnabled = false;
        progressBar.Value = 0;
        statusText.Text = "Scanne …";
        var progress = new Progress<Vst3ScanProgress>(p =>
        {
            statusText.Text = $"Geprüft: {p.Examined} | Gefunden: {p.Found} | {Truncate(p.CurrentPath, 80)}";
        });
        try
        {
            Result = await Task.Run(() => Vst3EffectCatalog.Scan(selected, progress, cts.Token), cts.Token);
            progressBar.Value = 100;
            statusText.Text = $"Fertig: {Result.Count} VST3-Effekte gefunden.";
            DialogResult = true;
        }
        catch (OperationCanceledException)
        {
            statusText.Text = "Scan abgebrochen.";
        }
        catch (Exception ex)
        {
            statusText.Text = $"Fehler: {ex.Message}";
        }
        finally
        {
            scanButton.IsEnabled = true;
            cts?.Dispose();
            cts = null;
        }
    }

    private void CancelScan() => cts?.Cancel();

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}