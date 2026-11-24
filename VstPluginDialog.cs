using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using NAudio.Vst3;

namespace WellenEditor;

/// <summary>Dialog für VST3-Plugins mit nativer Oberfläche, Parameter-Reglern und Live-Vorschau.</summary>
internal sealed class VstPluginDialog : Window
{
    private readonly Vst3EffectDescriptor descriptor;
    private Vst3Module? module;
    private Vst3PluginView? view;
    private Vst3Plugin? plugin;
    private readonly byte[]? initialState;
    private readonly List<Action> updaters = new();
    private byte[]? currentState;
    private bool updating;

    /// <summary>Wenn das Laden des Plugins fehlgeschlagen ist, hier die vollständige Fehlerausgabe.</summary>
    public string? LoadErrorText { get; private set; }

    public VstPluginDialog(Window owner, Vst3EffectDescriptor descriptor, int sampleRate = 44100)
    {
        Owner = owner;
        this.descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
        var rate = sampleRate > 0 ? sampleRate : 44100;

        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.CanResize;
        SizeToContent = SizeToContent.Manual;
        MinWidth = 480;
        MinHeight = 380;
        ShowInTaskbar = false;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13;
        Background = Application.Current.TryFindResource("Surface") as Brush ?? Brushes.Black;
        Foreground = Application.Current.TryFindResource("Text") as Brush ?? Brushes.White;

        try
        {
            module = Vst3Module.Load(descriptor.Path);
            // Echte ClassInfo aus dem Modul lösen (funktioniert auch mit Cache-ClassInfo, die nur die ClassId trägt)
            var classInfo = Vst3EffectCatalog.ResolveClassInfo(module, descriptor.ClassInfo.ClassId);
            plugin = module.CreatePlugin(classInfo, rate, 512);
            view = plugin.CreateView();
            initialState = plugin.SaveState();
            currentState = initialState;
            // HINWEIS: module/plugin/view leben bewusst über die Dialog-Lebensdauer hinaus
            // (Dispose in OnClosed). NICHT "using" – sonst ObjectDisposedException beim 2. Öffnen.
        }
        catch (Exception ex)
        {
            LoadErrorText = BuildErrorText(ex, descriptor);
            try { plugin?.Dispose(); } catch { }
            try { module?.Dispose(); } catch { }
            plugin = null; view = null; module = null;
        }

        Title = $"{descriptor.DisplayName} – VST3";
        try
        {
            BuildUi();
        }
        catch (Exception uiEx)
        {
            LoadErrorText = BuildErrorText(uiEx, descriptor);
            BuildErrorUi();
        }
    }

    /// <summary>Aktueller Plugin-State – wird beim OK-Klick gesichert, bevor das Plugin disposed wird.</summary>
    public byte[]? ResultState { get; private set; }

    /// <summary>Live-Vorschau: MainWindow bekommt das laufende Plugin-Objekt, während der Dialog offen ist.</summary>
    public event Action<Vst3Plugin?>? LivePreviewRequested;

    /// <summary>Baut eine vollständige Fehlerausgabe aus der Exception-Kette (inkl. InnerException + StackTrace).</summary>
    private static string BuildErrorText(Exception ex, Vst3EffectDescriptor descriptor)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"VST3-Plugin konnte nicht geladen werden: {descriptor.Name}");
        sb.AppendLine($"Pfad:        {descriptor.Path}");
        sb.AppendLine($"ClassInfo:   {descriptor.ClassInfo}");
        sb.AppendLine();

        var current = ex;
        int depth = 0;
        while (current != null)
        {
            if (depth > 0) sb.AppendLine("--- Innere Exception ---");
            sb.AppendLine($"Typ:        {current.GetType().FullName}");
            sb.AppendLine($"Nachricht:  {current.Message}");
            sb.AppendLine($"Source:     {current.Source}");
            if (!string.IsNullOrWhiteSpace(current.StackTrace))
                sb.AppendLine($"StackTrace:\n{current.StackTrace}");
            if (current.Data?.Count > 0)
            {
                sb.AppendLine("Data:");
                foreach (System.Collections.DictionaryEntry entry in current.Data)
                    sb.AppendLine($"  {entry.Key}: {entry.Value}");
            }
            current = current.InnerException;
            depth++;
        }

        sb.AppendLine();
        sb.AppendLine("--- Geladene VST3-relevante Module im Prozess ---");
        try
        {
            var vstDlls = System.Diagnostics.Process.GetCurrentProcess().Modules
                .OfType<System.Diagnostics.ProcessModule>()
                .Where(m => m.ModuleName?.IndexOf("vst", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            m.ModuleName?.IndexOf("naudio", StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(m => m.ModuleName, StringComparer.OrdinalIgnoreCase);
            foreach (var m in vstDlls)
                sb.AppendLine($"{m.ModuleName}  @ {m.FileName}");
            if (!vstDlls.Any())
                sb.AppendLine("(keine vst/naudio-Module im Prozess geladen)");
        }
        catch (Exception loadEx)
        {
            sb.AppendLine($"Modulauflistung fehlgeschlagen: {loadEx.Message}");
        }

        sb.AppendLine();
        sb.AppendLine("Tipp: Kopieren Sie diesen Text und senden Sie ihn zur Fehleranalyse.");
        return sb.ToString();
    }

    private void BuildUi()
    {
        // Fehlerfall: Zeige den Debug-Text statt des Plugin-UI
        if (LoadErrorText != null)
        {
            BuildErrorUi();
            return;
        }

        var scale = 1.0;
        if (PresentationSource.FromVisual(this) is HwndSource source)
            scale = source.CompositionTarget.TransformToDevice.M11;

        // Header
        var header = new TextBlock
        {
            Text = $"{descriptor.DisplayName} auf die Auswahl anwenden (ohne Auswahl auf den gesamten Clip).",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Application.Current.TryFindResource("Muted") as Brush ?? Brushes.Gray,
            Margin = new Thickness(0, 0, 0, 10)
        };

        // Plugin UI (links) + Parameter (rechts)
        var mainGrid = new Grid();
        mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(340) });

        // VST Editor Host
        var host = new VstEditorHost(view!, scale);
        var viewSize = view!.GetSize();
        host.Width = viewSize.Width * scale;
        host.Height = viewSize.Height * scale;
        host.MinWidth = viewSize.Width * scale;
        host.MinHeight = viewSize.Height * scale;
        Grid.SetColumn(host, 0);
        mainGrid.Children.Add(host);

        // Separator
        var separator = new GridSplitter { Width = 4, HorizontalAlignment = HorizontalAlignment.Center, Background = Application.Current.TryFindResource("Line") as Brush ?? Brushes.Gray };
        Grid.SetColumn(separator, 1);
        mainGrid.Children.Add(separator);

        // Parameter Panel (rechts)
        var paramPanel = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Margin = new Thickness(12, 0, 0, 0)
        };
        var paramStack = new StackPanel();
        paramPanel.Content = paramStack;
        Grid.SetColumn(paramPanel, 2);
        mainGrid.Children.Add(paramPanel);

        // Parameter-Regler generieren
        foreach (var param in plugin!.Parameters)
        {
            if (param.IsHidden) continue;
            AddParameterRow(paramStack, param);
        }

        // Buttons unten
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0)
        };
        var liveBtn = new Button { Content = "Live ▶", MinWidth = 80, Margin = new Thickness(0, 0, 8, 0), FontSize = 13, ToolTip = "Spielt die Auswahl in Schleife durch das Plugin ab – Regler im Plugin wirken sofort hörbar." };
        liveBtn.Click += (_, _) => LivePreviewRequested?.Invoke(plugin);
        var okBtn = new Button { Content = "OK", IsDefault = true, MinWidth = 96, Margin = new Thickness(0, 0, 8, 0), FontSize = 13 };
        okBtn.Click += (_, _) => { ResultState = plugin?.SaveState() ?? currentState ?? initialState; DialogResult = true; };
        var cancelBtn = new Button { Content = "Abbrechen", IsCancel = true, MinWidth = 110, FontSize = 13 };
        cancelBtn.Click += (_, _) => { DialogResult = false; };
        buttons.Children.Add(liveBtn);
        buttons.Children.Add(okBtn);
        buttons.Children.Add(cancelBtn);

        var buttonBorder = new Border { Child = buttons, Margin = new Thickness(0, 14, 0, 0) };
        Grid.SetColumn(buttonBorder, 0);
        Grid.SetColumnSpan(buttonBorder, 3);

        var outerGrid = new Grid();
        outerGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        outerGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        outerGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(header, 0);
        Grid.SetRow(mainGrid, 1);
        Grid.SetRow(buttonBorder, 2);
        outerGrid.Children.Add(header);
        outerGrid.Children.Add(mainGrid);
        outerGrid.Children.Add(buttonBorder);

        Content = outerGrid;
    }

    /// <summary>Zeigt den vollständigen Fehlertext in einer kopierbaren TextBox.</summary>
    private void BuildErrorUi()
    {
        var root = new DockPanel { Margin = new Thickness(12) };

        var header = new TextBlock
        {
            Text = "VST3-Plugin konnte nicht geladen werden – Debug-Informationen",
            FontWeight = FontWeights.Bold,
            Foreground = Application.Current.TryFindResource("Muted") as Brush ?? Brushes.Gray,
            Margin = new Thickness(0, 0, 0, 8)
        };
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        var textBox = new TextBox
        {
            Text = LoadErrorText,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 10,
            AcceptsReturn = true,
            Background = Application.Current.TryFindResource("Panel") as Brush ?? Brushes.DarkGray,
            Foreground = Application.Current.TryFindResource("Text") as Brush ?? Brushes.White,
            Height = 400
        };
        root.Children.Add(textBox);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };
        var okBtn = new Button { Content = "OK", IsDefault = true, MinWidth = 80, Margin = new Thickness(0, 0, 8, 0), FontSize = 13 };
        okBtn.Click += (_, _) => { DialogResult = true; };
        buttons.Children.Add(okBtn);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);

        Content = root;
    }

    private void AddParameterRow(StackPanel panel, Vst3Parameter param)
    {
        var valueText = new TextBlock
        {
            TextAlignment = TextAlignment.Right,
            MinWidth = 58,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        valueText.Text = param.DisplayValue;

        var slider = new Slider
        {
            Minimum = 0,
            Maximum = 1,
            Height = 24,
            VerticalAlignment = VerticalAlignment.Center,
            Value = param.NormalizedValue
        };

        var format = GetFormat(param);
        slider.ValueChanged += (_, _) =>
        {
            if (updating) return;
            param.NormalizedValue = slider.Value;
            valueText.Text = param.DisplayValue;
            UpdateState();
        };

        updaters.Add(() =>
        {
            updating = true;
            slider.Value = param.NormalizedValue;
            valueText.Text = param.DisplayValue;
            updating = false;
        });

        var label = new TextBlock { Text = param.Title, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0), MinWidth = 120 };
        var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(label, 0);
        Grid.SetColumn(slider, 1);
        Grid.SetColumn(valueText, 2);
        row.Children.Add(label);
        row.Children.Add(slider);
        row.Children.Add(valueText);
        panel.Children.Add(row);
    }

    private static string GetFormat(Vst3Parameter p)
    {
        if (p.Units.EndsWith("dB", StringComparison.OrdinalIgnoreCase) || p.Title.Contains("Gain", StringComparison.OrdinalIgnoreCase))
            return "0.0";
        if (p.StepCount > 0 && p.StepCount <= 10)
            return "0";
        return "0.00";
    }

    private void UpdateState()
    {
        if (plugin != null)
            currentState = plugin.SaveState();
    }

    protected override void OnClosed(EventArgs e)
    {
        // Zuerst die Live-Vorschau stoppen – sie läuft evtl. auf einem WasapiOut-Thread
        // durch DIESES Plugin und darf nicht auf einem disposed Plugin weiterlaufen.
        try { LivePreviewRequested?.Invoke(null); } catch { }
        // Reihenfolge kritisch gegen nativen Crash beim 2. Öffnen:
        // 1) View vom HWND lösen (Detach), 2) View disposen, 3) Plugin disposen, 4) Modul disposen.
        try { view?.Detach(); } catch { }
        try { view?.Dispose(); } catch { }
        try { plugin?.Dispose(); } catch { }
        try { module?.Dispose(); } catch { }
        view = null; plugin = null; module = null;
        base.OnClosed(e);
    }
}
