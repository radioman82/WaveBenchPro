using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using NAudio.Vst3;

namespace WellenEditor;

/// <summary>Dialog für VST3-Plugins mit nativer Oberfläche, Parameter-Reglern und Vorschau.</summary>
internal sealed class VstPluginDialog : Window
{
    private readonly Vst3EffectDescriptor descriptor;
    private readonly Vst3PluginView? view;
    private readonly Vst3Plugin? plugin;
    private readonly byte[]? initialState;
    private readonly List<Action> updaters = new();
    private byte[]? currentState;
    private bool updating;

    public VstPluginDialog(Window owner, Vst3EffectDescriptor descriptor)
    {
        Owner = owner;
        this.descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));

        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.CanResize;
        SizeToContent = SizeToContent.Manual;
        MinWidth = 480;
        MinHeight = 380;
        ShowInTaskbar = false;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13;
        Background = Application.Current.FindResource("Surface") as Brush ?? Brushes.Black;
        Foreground = Application.Current.FindResource("Text") as Brush ?? Brushes.White;

        using var module = Vst3Module.Load(descriptor.Path);
        plugin = module.CreatePlugin(descriptor.ClassInfo, 44100, 512);
        view = plugin.CreateView();
        initialState = plugin.SaveState();
        currentState = initialState;

        Title = $"{descriptor.DisplayName} – VST3";
        BuildUi();
    }

    public byte[]? ResultState => plugin?.SaveState() ?? initialState;

    public event Action<byte[]>? PreviewRequested;

    private void BuildUi()
    {
        var scale = 1.0;
        if (PresentationSource.FromVisual(this) is HwndSource source)
            scale = source.CompositionTarget.TransformToDevice.M11;

        var root = new DockPanel { Margin = new Thickness(12) };

        // Header
        var header = new TextBlock
        {
            Text = $"{descriptor.DisplayName} auf die Auswahl anwenden (ohne Auswahl auf den gesamten Clip).",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Application.Current.FindResource("Muted") as Brush ?? Brushes.Gray,
            Margin = new Thickness(0, 0, 0, 10)
        };
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

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
        var separator = new GridSplitter { Width = 4, HorizontalAlignment = HorizontalAlignment.Center, Background = Application.Current.FindResource("Border") as Brush ?? Brushes.Gray };
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
        var previewBtn = new Button { Content = "Vorschau", MinWidth = 100, Margin = new Thickness(0, 0, 8, 0), FontSize = 13 };
        previewBtn.Click += (_, _) => PreviewRequested?.Invoke(currentState ?? initialState!);
        var okBtn = new Button { Content = "OK", IsDefault = true, MinWidth = 96, Margin = new Thickness(0, 0, 8, 0), FontSize = 13 };
        okBtn.Click += (_, _) => { DialogResult = true; };
        var cancelBtn = new Button { Content = "Abbrechen", IsCancel = true, MinWidth = 110, FontSize = 13 };
        cancelBtn.Click += (_, _) => { DialogResult = false; };
        buttons.Children.Add(previewBtn);
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
        base.OnClosed(e);
        // view und plugin werden via HwndHost.Dispose() aufgeräumt
    }
}