using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace WellenEditor;

/// <summary>Ein Ordner-Knoten im Scan-Baum. Kinder werden erst beim Aufklappen geladen.</summary>
internal sealed class ScanFolderNode : INotifyPropertyChanged
{
    public string Name { get; }
    public string FullPath { get; }
    public ObservableCollection<ScanFolderNode> Children { get; } = new();
    public ScanFolderNode? Parent { get; private set; }
    public bool ChildrenLoaded { get; private set; }

    private bool isChecked;
    public bool IsChecked
    {
        get => isChecked;
        set
        {
            if (isChecked == value) return;
            SetCheckedSilent(value);
            if (value)
            {
                // Hierarchisch exklusiv: Anhaken entfernt Haken bei allen Oberordnern
                // (sonst würde z. B. angehaktes "C:\" rekursiv ALLES scannen) und bei
                // allen geladenen Unterordnern (redundant, Parent inkludiert sie bereits).
                var p = Parent;
                while (p != null) { p.SetCheckedSilent(false); p = p.Parent; }
                ClearDescendants(this);
            }
        }
    }

    private void SetCheckedSilent(bool value)
    {
        if (isChecked == value) return;
        isChecked = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
    }

    private static void ClearDescendants(ScanFolderNode node)
    {
        foreach (var child in node.Children)
        {
            child.SetCheckedSilent(false);
            ClearDescendants(child);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ScanFolderNode(string name, string fullPath)
    {
        Name = name;
        FullPath = fullPath;
    }

    public void EnsureChildrenLoaded()
    {
        if (ChildrenLoaded) return;
        ChildrenLoaded = true;
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(FullPath))
            {
                try
                {
                    var dirName = Path.GetFileName(dir);
                    if (string.IsNullOrWhiteSpace(dirName)) continue;
                    Children.Add(new ScanFolderNode(dirName, dir) { Parent = this });
                }
                catch { }
            }
        }
        catch { }
    }
}

/// <summary>Ein gefundenes Plugin in der Ergebnisliste mit Anhaken-Status.</summary>
internal sealed class VstResultEntry : INotifyPropertyChanged
{
    public Vst3EffectDescriptor Descriptor { get; }
    public string DisplayName => Descriptor.DisplayName;

    private bool isTaken;
    public bool IsTaken
    {
        get => isTaken;
        set
        {
            if (isTaken == value) return;
            isTaken = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsTaken)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public VstResultEntry(Vst3EffectDescriptor descriptor) => Descriptor = descriptor;
}

/// <summary>
/// Dialog: Ordnerbaum (Unterordner werden erst beim Aufklappen geladen), nur angehakte Ordner
/// werden gescannt, gefundene Plugins werden einzeln angehakt und übernommen.
/// </summary>
internal sealed class VstScanDialog : Window
{
    private readonly TreeView tree;
    private readonly TextBlock statusText;
    private readonly ProgressBar progressBar;
    private readonly Button scanButton;
    private readonly Button takeButton;
    private readonly ListView resultList;
    private readonly ObservableCollection<ScanFolderNode> roots = new();
    private readonly ObservableCollection<VstResultEntry> resultEntries = new();
    private CancellationTokenSource? cts;

    public IReadOnlyList<Vst3EffectDescriptor>? Result { get; private set; }

    public VstScanDialog(Window owner)
    {
        Owner = owner;
        Title = "VST3 scannen – Ordner auswählen";
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Width = 780;
        Height = 580;
        MinWidth = 560;
        MinHeight = 420;
        ShowInTaskbar = false;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13;
        Background = (Brush)(Application.Current.TryFindResource("Surface") ?? Brushes.Black);
        Foreground = (Brush)(Application.Current.TryFindResource("Text") ?? Brushes.White);

        var rootPanel = new DockPanel { Margin = new Thickness(10) };

        var header = new TextBlock
        {
            Text = "Ordner aufklappen und anhaken, die durchsucht werden. Nach dem Scan die gefundenen Plugins anhaken und übernehmen.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)(Application.Current.TryFindResource("Muted") ?? Brushes.Gray),
            Margin = new Thickness(0, 0, 0, 8)
        };
        DockPanel.SetDock(header, Dock.Top);
        rootPanel.Children.Add(header);

        // Unten: Fortschritt + Buttons
        var bottom = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        statusText = new TextBlock { Foreground = (Brush)(Application.Current.TryFindResource("Muted") ?? Brushes.Gray), TextWrapping = TextWrapping.Wrap };
        progressBar = new ProgressBar { Height = 8, Minimum = 0, Maximum = 100, Value = 0, Margin = new Thickness(0, 4, 0, 6) };
        bottom.Children.Add(statusText);
        bottom.Children.Add(progressBar);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        scanButton = new Button { Content = "Ausgewählte Ordner scannen", IsDefault = true, MinWidth = 190, Margin = new Thickness(0, 0, 8, 0) };
        scanButton.Click += async (_, _) => await RunScanAsync();
        takeButton = new Button { Content = "Angehakte übernehmen", MinWidth = 170, Margin = new Thickness(0, 0, 8, 0), IsEnabled = false };
        takeButton.Click += (_, _) => TakeSelected();
        var closeButton = new Button { Content = "Schließen", IsCancel = true, MinWidth = 90 };
        closeButton.Click += (_, _) => { CancelScan(); Close(); };
        buttons.Children.Add(scanButton);
        buttons.Children.Add(takeButton);
        buttons.Children.Add(closeButton);
        bottom.Children.Add(buttons);
        DockPanel.SetDock(bottom, Dock.Bottom);
        rootPanel.Children.Add(bottom);

        // Mitte: zwei Spalten
        var mainGrid = new Grid();
        mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(370) });
        mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        DockPanel.SetDock(mainGrid, Dock.Top);
        rootPanel.Children.Add(mainGrid);

        // Links: Ordnerbaum
        var leftPanel = new DockPanel();
        var leftHeader = new TextBlock { Text = "Ordner", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 4) };
        DockPanel.SetDock(leftHeader, Dock.Top);
        leftPanel.Children.Add(leftHeader);
        tree = new TreeView
        {
            Background = (Brush)(Application.Current.TryFindResource("Panel") ?? Brushes.Transparent),
            BorderBrush = (Brush)(Application.Current.TryFindResource("Line") ?? Brushes.Gray),
            Foreground = (Brush)(Application.Current.TryFindResource("Text") ?? Brushes.White), // EXPLIZIT WEISS FÜR TREEVIEW
            ItemsSource = roots,
            ItemTemplate = CreateNodeTemplate(),
            ItemContainerStyle = CreateItemContainerStyle()
        };
        leftPanel.Children.Add(tree);
        var leftCell = new Border { Child = leftPanel, Margin = new Thickness(0, 0, 8, 0) };
        Grid.SetColumn(leftCell, 0);
        mainGrid.Children.Add(leftCell);

        // Rechts: Ergebnisse
        var rightPanel = new DockPanel();
        var rightHeader = new TextBlock { Text = "Gefundene VST3-Plugins", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 4) };
        DockPanel.SetDock(rightHeader, Dock.Top);
        rightPanel.Children.Add(rightHeader);
        resultList = new ListView
        {
            Background = (Brush)(Application.Current.TryFindResource("Panel") ?? Brushes.Transparent),
            BorderBrush = (Brush)(Application.Current.TryFindResource("Line") ?? Brushes.Gray),
            Foreground = Foreground,
            ItemsSource = resultEntries
        };
        var gv = new GridView();
        var checkCol = new GridViewColumn { Header = "", Width = 32 };
        var checkFactory = new FrameworkElementFactory(typeof(CheckBox));
        checkFactory.SetBinding(CheckBox.IsCheckedProperty, new Binding(nameof(VstResultEntry.IsTaken)) { Mode = BindingMode.TwoWay });
        checkFactory.SetValue(CheckBox.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        checkCol.CellTemplate = new DataTemplate { VisualTree = checkFactory };
        var nameCol = new GridViewColumn { Header = "Plugin", Width = 260, DisplayMemberBinding = new Binding(nameof(VstResultEntry.DisplayName)) };
        gv.Columns.Add(checkCol);
        gv.Columns.Add(nameCol);
        resultList.View = gv;
        rightPanel.Children.Add(resultList);
        Grid.SetColumn(rightPanel, 1);
        mainGrid.Children.Add(rightPanel);

        Content = rootPanel;

        LoadDriveRoots();
        statusText.Text = "Laufwerke und Ordner aufklappen, anhaken und scannen.";
    }

    private DataTemplate CreateNodeTemplate()
    {
        var panel = new FrameworkElementFactory(typeof(StackPanel));
        panel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);

        var check = new FrameworkElementFactory(typeof(CheckBox));
        check.SetBinding(CheckBox.IsCheckedProperty, new Binding(nameof(ScanFolderNode.IsChecked)) { Mode = BindingMode.TwoWay });
        check.SetValue(CheckBox.VerticalAlignmentProperty, VerticalAlignment.Center);

        var label = new FrameworkElementFactory(typeof(TextBlock));
        label.SetBinding(TextBlock.TextProperty, new Binding(nameof(ScanFolderNode.Name)));
        label.SetValue(TextBlock.MarginProperty, new Thickness(6, 0, 0, 0));
        label.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);

        panel.AppendChild(check);
        panel.AppendChild(label);

        return new HierarchicalDataTemplate
        {
            ItemsSource = new Binding(nameof(ScanFolderNode.Children)),
            VisualTree = panel
        };
    }

    private Style CreateItemContainerStyle()
    {
        var style = new Style(typeof(TreeViewItem));
        // Der Standard-TreeViewItem-Template erbt sonst schwarzen Text (SystemColors.WindowTextBrushKey).
        style.Setters.Add(new Setter(TreeViewItem.ForegroundProperty,
            (Brush)(Application.Current.TryFindResource("Text") ?? Brushes.White)));
        style.Setters.Add(new EventSetter(TreeViewItem.ExpandedEvent, new RoutedEventHandler(OnItemExpanded)));
        return style;
    }

    private void OnItemExpanded(object sender, RoutedEventArgs e)
    {
        if (sender is TreeViewItem item && item.DataContext is ScanFolderNode node)
            node.EnsureChildrenLoaded();
    }

    private void LoadDriveRoots()
    {
        roots.Clear();
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady).OrderBy(d => d.Name))
        {
            var label = drive.Name;
            if (!string.IsNullOrWhiteSpace(drive.VolumeLabel)) label = $"{drive.VolumeLabel} ({drive.Name.TrimEnd('\\')})";
            roots.Add(new ScanFolderNode(label, drive.RootDirectory.FullName));
        }
    }

    private void CollectChecked(ScanFolderNode node, List<string> acc)
    {
        if (node.IsChecked) { acc.Add(node.FullPath); return; } // angehakter Ordner = inkl. aller Unterordner, keine Doppelung
        foreach (var child in node.Children) CollectChecked(child, acc);
    }

    private List<string> GetSelectedPaths()
    {
        var acc = new List<string>();
        foreach (var root in roots) CollectChecked(root, acc);
        return acc.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private async Task RunScanAsync()
    {
        var selected = GetSelectedPaths();
        if (selected.Count == 0)
        {
            statusText.Text = "Bitte mindestens einen Ordner anhaken.";
            return;
        }
        CancelScan();
        cts = new CancellationTokenSource();
        scanButton.IsEnabled = false;
        takeButton.IsEnabled = false;
        resultEntries.Clear();
        progressBar.IsIndeterminate = false;
        progressBar.Minimum = 0;
        progressBar.Maximum = 100;
        progressBar.Value = 0;
        statusText.Text = $"Scanne {selected.Count} Ordner: {string.Join(", ", selected.Select(s => Truncate(s, 40)))} …";
        var progress = new Progress<Vst3ScanProgress>(p =>
        {
            if (p.Total > 0)
            {
                // Prüfphase: echter Fortschritt 0–100 % über alle gefundenen Dateien
                var pct = (int)Math.Clamp(p.Examined * 100.0 / p.Total, 0, 100);
                progressBar.Value = pct;
                statusText.Text = $"[{p.Phase}] {p.Examined}/{p.Total} Dateien geprüft ({pct} %) | Gefunden: {p.Found} | {Truncate(p.CurrentPath, 80)}";
            }
            else
            {
                // Durchsuchphase: examenbasierter Balken + aktueller Ordner
                progressBar.IsIndeterminate = true;
                statusText.Text = $"[{p.Phase}] Geprüft: {p.Examined} Ordner | Gefunden: {p.Found} | {Truncate(p.CurrentPath, 90)}";
            }
        });
        try
        {
            var found = await Task.Run(() => Vst3EffectCatalog.Scan(selected, progress, cts.Token), cts.Token);
            foreach (var e in found) resultEntries.Add(new VstResultEntry(e));
            // Sichtbares Ende: Balken voll und nicht mehr indeterminate
            progressBar.IsIndeterminate = false;
            progressBar.Value = 100;
            statusText.Text = found.Count == 0
                ? $"Fertig – keine VST3-Effekte in {selected.Count} Ordner(n) gefunden."
                : $"Fertig: {found.Count} gefunden in {selected.Count} Ordner(n). Anhaken und übernehmen.";
            takeButton.IsEnabled = found.Count > 0;
        }
        catch (OperationCanceledException)
        {
            progressBar.IsIndeterminate = false;
            progressBar.Value = 0;
            statusText.Text = "Scan abgebrochen.";
        }
        catch (Exception ex)
        {
            progressBar.IsIndeterminate = false;
            progressBar.Value = 0;
            statusText.Text = $"Fehler: {ex.Message}";
        }
        finally
        {
            scanButton.IsEnabled = true;
            cts?.Dispose();
            cts = null;
        }
    }

    private void TakeSelected()
    {
        var taken = resultEntries.Where(e => e.IsTaken).Select(e => e.Descriptor).ToList();
        if (taken.Count == 0)
        {
            statusText.Text = "Bitte mindestens ein Plugin anhaken.";
            return;
        }
        // In den dauerhaften Katalog übernehmen: bisherige + neue, Duplikate nach Datei+Name entfernen.
        var merged = Vst3EffectCatalog.LoadCache()
            .Concat(taken)
            .GroupBy(d => d.Path + "|" + d.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(d => d.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        Vst3EffectCatalog.SaveCache(merged);
        Result = taken;
        DialogResult = true;
        Close();
    }

    private void CancelScan() => cts?.Cancel();

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}