using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace WellenEditor;

/// <summary>Spurköpfe links, gemeinsame Zeitleiste rechts. Clips referenzieren Audiodokumente.</summary>
public sealed class SequencerView : UserControl
{
    private readonly StackPanel heads = new();
    private readonly SequencerTimeline timeline;
    private readonly ScrollViewer horizontal;
    private readonly ScrollViewer vertical;
    private MultitrackProject? project;
    private readonly MusicalTimelineScale scale = new();
    private double positionSeconds;
    private double? rangeAnchor;
    private double? rangeEdge;
    private bool restoringView;
    public event Action? ViewChanged;
    public event Action? PlaybackRangeChanged;
    public event Action<long>? SeekRequested;
    public (long Start, long Count)? GetPlaybackRange()
    {
        if (project is null || rangeAnchor is null || rangeEdge is null || rangeAnchor == rangeEdge) return null;
        var start = (long)Math.Round(Math.Min(rangeAnchor.Value, rangeEdge.Value) * project.FramesPerBeat);
        var end = (long)Math.Round(Math.Max(rangeAnchor.Value, rangeEdge.Value) * project.FramesPerBeat);
        return (start, end - start);
    }
    public event Action? ProjectChanged;
    public event Action<MultitrackTrack>? TrackSelected;
    public event Action<MultitrackTrack>? RemoveTrackRequested;
    public event Action<MultitrackTrack>? EffectsRequested;
    internal void CaptureProjectView()
    {
        if (project is null) return;
        var range = GetPlaybackRange();
        project.SelectionStart = range?.Start;
        project.SelectionEnd = range is null ? null : checked(range.Value.Start + range.Value.Count);
        project.CursorPosition = (long)Math.Clamp(Math.Round(positionSeconds * project.SampleRate), 0, long.MaxValue / 2);
        project.HorizontalZoom = scale.PixelsPerBeat; project.VerticalZoom = scale.VerticalScale;
        project.ViewportStart = (long)Math.Clamp(horizontal.HorizontalOffset / scale.PixelsPerSecond(project.Bpm) * project.SampleRate, 0, long.MaxValue / 2);
        project.ViewportFrames = horizontal.ViewportWidth > 0 ? (long)Math.Clamp(horizontal.ViewportWidth / scale.PixelsPerSecond(project.Bpm) * project.SampleRate, 1, long.MaxValue / 2) : null;
    }
    public double PositionSeconds
    {
        get => positionSeconds;
        set
        {
            positionSeconds = Math.Max(0, value);
            timeline.SetPosition(positionSeconds);
            var x = positionSeconds * timeline.PixelsPerSecond;
            var viewport = horizontal.ViewportWidth;
            if (viewport > 0 && (x < horizontal.HorizontalOffset || x > horizontal.HorizontalOffset + viewport * .8))
                horizontal.ScrollToHorizontalOffset(Math.Max(0, x - viewport * .2));
        }
    }
    public void ZoomHorizontal(double factor)
    {
        if (project is null) return;
        var beatAtLeft = scale.XToBeat(horizontal.HorizontalOffset);
        scale.ZoomHorizontal(factor);
        UpdateExtent();
        horizontal.ScrollToHorizontalOffset(scale.BeatToX(beatAtLeft));
        ViewChanged?.Invoke();
    }
    public void ZoomVertical(double factor)
    {
        scale.ZoomVertical(factor);
        if (project is not null) SetProject(project);
        ViewChanged?.Invoke();
    }

    public void ResetHorizontalZoom()
    {
        var beatAtLeft = scale.XToBeat(horizontal.HorizontalOffset);
        scale.ResetHorizontal();
        UpdateExtent();
        horizontal.ScrollToHorizontalOffset(scale.BeatToX(beatAtLeft));
        ViewChanged?.Invoke();
    }

    public void ResetVerticalZoom()
    {
        scale.ResetVertical();
        if (project is not null) SetProject(project);
        ViewChanged?.Invoke();
    }

    public SequencerView()
    {
        timeline = new SequencerTimeline(this) { VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left };
        horizontal = new ScrollViewer { Content = timeline, VerticalContentAlignment = VerticalAlignment.Top, HorizontalContentAlignment = HorizontalAlignment.Left, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(185) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.Children.Add(heads);
        Grid.SetColumn(horizontal, 1); grid.Children.Add(horizontal);
        vertical = new ScrollViewer { Content = grid, VerticalContentAlignment = VerticalAlignment.Top, HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Content = vertical;
        horizontal.SizeChanged += (_, _) => UpdateExtent();
        horizontal.ScrollChanged += (_, args) => { if (IsLoaded && !restoringView && args.HorizontalChange != 0) ViewChanged?.Invoke(); };
        SizeChanged += (_, _) => UpdateExtent();
        Background = new SolidColorBrush(Color.FromRgb(14, 20, 23));
    }

    public void SetProject(MultitrackProject value)
    {
        var changedProject = !ReferenceEquals(project, value);
        project = value;
        if (changedProject)
        {
            scale.Restore(value.HorizontalZoom ?? 64, value.VerticalZoom ?? 1);
            rangeAnchor = value.SelectionStart / value.FramesPerBeat; rangeEdge = value.SelectionEnd / value.FramesPerBeat;
            positionSeconds = (value.CursorPosition ?? 0) / (double)value.SampleRate;
            timeline.SetPosition(positionSeconds);
            restoringView = true;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                horizontal.ScrollToHorizontalOffset((value.ViewportStart ?? 0) / (double)value.SampleRate * scale.PixelsPerSecond(value.Bpm));
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle, new Action(() => restoringView = false));
            }));
        }
        heads.Children.Clear();
        heads.Children.Add(new TextBlock { Text = "SPUREN", Height = 28, Foreground = Brushes.LightGray, Margin = new Thickness(6, 0, 0, 0) });
        foreach (var track in value.Tracks)
        {
            var color = TrackColor(value.Tracks.ToList().IndexOf(track));
            var panel = new StackPanel { Margin = new Thickness(5, 3, 5, 3) };
            var title = new DockPanel();
            var expand = new Button { Content = track.Expanded ? "▾" : "▸", Width = 22, Padding = new Thickness(0), Margin = new Thickness(0, 0, 4, 0), ToolTip = "Spurregler ein-/ausklappen" };
            expand.Click += (_, _) => { track.Expanded = !track.Expanded; SetProject(value); };
            title.Children.Add(expand);
            var remove = new Button { Content = "×", Width = 20, Padding = new Thickness(0), Margin = new Thickness(2, 0, 0, 0), Foreground = Brushes.IndianRed, ToolTip = "Diese Spur entfernen" };
            DockPanel.SetDock(remove, Dock.Right);
            remove.Click += (_, _) => RemoveTrackRequested?.Invoke(track);
            title.Children.Add(remove);
            var name = new TextBox { Text = track.Name, FontSize = 11, Padding = new Thickness(2, 1, 2, 1), Background = Brushes.Transparent, BorderThickness = new Thickness(0), FontWeight = FontWeights.SemiBold };
            name.TextChanged += (_, _) => { track.Name = name.Text; ProjectChanged?.Invoke(); };
            title.Children.Add(name); panel.Children.Add(title);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            var mute = MixerButton("Stumm", track.Muted, Colors.DarkOrange);
            var solo = MixerButton("Solo", track.Solo, Colors.Turquoise);
            void RefreshMixerButtons()
            {
                PaintButton(mute, track.Muted, Colors.DarkOrange);
                PaintButton(solo, track.Solo, Colors.Turquoise);
                timeline.RefreshDrawing();
            }
            mute.Click += (_, _) => { track.Muted = !track.Muted; RefreshMixerButtons(); ProjectChanged?.Invoke(); };
            solo.Click += (_, _) => { track.Solo = !track.Solo; RefreshMixerButtons(); ProjectChanged?.Invoke(); };
            buttons.Children.Add(mute); buttons.Children.Add(solo);
            var activeFx = track.Effects.Count(fx => fx.Enabled);
            var fxButton = MixerButton(activeFx > 0 ? $"FX {activeFx}" : "FX", activeFx > 0, Colors.MediumPurple);
            fxButton.MinWidth = 35; fxButton.ToolTip = "Spur-Effekte bearbeiten";
            fxButton.Click += (_, _) => EffectsRequested?.Invoke(track);
            buttons.Children.Add(fxButton); panel.Children.Add(buttons);
            var sliders = new Grid(); sliders.ColumnDefinitions.Add(new ColumnDefinition()); sliders.ColumnDefinitions.Add(new ColumnDefinition());
            sliders.Children.Add(new RotaryKnob("Volume", 0, 2, track.Gain, 1, v => { track.Gain = v; ProjectChanged?.Invoke(); }));
            var pan = new RotaryKnob("Pan", -1, 1, track.Pan, 0, v => { track.Pan = v; ProjectChanged?.Invoke(); }); Grid.SetColumn(pan, 1); sliders.Children.Add(pan);
            if (track.Expanded) panel.Children.Add(sliders);
            var border = new Border { Height = TrackHeight(track), BorderBrush = new SolidColorBrush(color), BorderThickness = new Thickness(3, 0, 0, 1), Child = panel, Background = new SolidColorBrush(Color.FromRgb(23, 31, 37)) };
            border.MouseLeftButtonDown += (_, _) => TrackSelected?.Invoke(track);
            heads.Children.Add(border);
        }
        UpdateExtent();
        timeline.RefreshDrawing();
    }

    public void PlaceDocument(AudioDocument source, Point point)
    {
        if (project is null) return;
        var location = TranslatePoint(point, timeline);
        var row = RowAt(location.Y);
        var track = row < project.Tracks.Count ? project.Tracks[row] : project.AddEmptyTrack();
        project.PutClipOnBeat(track, source, scale.XToBeat(Math.Max(0, location.X)));
        TrackSelected?.Invoke(track);
        SetProject(project);
        ProjectChanged?.Invoke();
    }

    private void UpdateExtent()
    {
        if (project is null) return;
        var width = Math.Max(1, horizontal.ActualWidth - 2);
        // Both axes cover the viewport, including the empty area used to create tracks.
        timeline.Width = Math.Max(width, project.TimelineFrames / (double)project.SampleRate * timeline.PixelsPerSecond + 160);
        timeline.Height = Math.Max(Math.Max(100, ActualHeight - 20), 28 + project.Tracks.Sum(TrackHeight) + 88);
        timeline.RefreshDrawing();
    }

    private double TrackHeight(MultitrackTrack track) => (track.Expanded ? 128 : 62) * scale.VerticalScale;
    private static Color TrackColor(int row) => new[] { Color.FromRgb(76, 191, 176), Color.FromRgb(103, 153, 215), Color.FromRgb(207, 174, 97), Color.FromRgb(171, 132, 194) }[row % 4];
    private int RowAt(double y)
    {
        var top = 28d;
        if (project is null) return 0;
        for (var row = 0; row < project.Tracks.Count; row++) { top += TrackHeight(project.Tracks[row]); if (y < top) return row; }
        return project.Tracks.Count;
    }
    private static Button MixerButton(string label, bool active, Color color)
    {
        var button = new Button { Content = label, FontSize = 10, Padding = new Thickness(7, 2, 7, 2), Margin = new Thickness(0, 3, 5, 0), MinWidth = 49, ToolTip = label == "Solo" ? "Nur Solo-Spuren hören" : "Spur stummschalten" };
        PaintButton(button, active, color); return button;
    }
    private static void PaintButton(Button button, bool active, Color color)
    {
        button.Background = new SolidColorBrush(active ? color : Color.FromRgb(31, 42, 50));
        button.Foreground = active ? Brushes.Black : Brushes.LightGray;
        button.BorderBrush = new SolidColorBrush(active ? color : Color.FromRgb(60, 76, 85));
    }

    private sealed class SequencerTimeline : FrameworkElement
    {
        private readonly SequencerView owner;
        private double position;
        private MultitrackClip? moving;
        private double mouseOffset;
        private DrawingGroup? background;
        private bool selectingRange;
        private bool rangeMoved;
        private double pointerDownX;
        private double? priorAnchor, priorEdge;
        private enum RangeDrag { Create, Left, Right, Move }
        private RangeDrag rangeDrag;
        private double originalStart, originalEnd, dragBeat;
        public double PixelsPerSecond => owner.scale.PixelsPerSecond(owner.project?.Bpm ?? 120);
        public SequencerTimeline(SequencerView owner)
        {
            this.owner = owner; AllowDrop = true;
            DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent("WaveBench.AudioDocument") ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
            Drop += (_, e) =>
            {
                if (owner.project is not { } project || e.Data.GetData("WaveBench.AudioDocument") is not AudioDocument source) return;
                var point = e.GetPosition(this);
                var row = owner.RowAt(point.Y);
                var track = row < project.Tracks.Count ? project.Tracks[row] : project.AddEmptyTrack();
                project.PutClipOnBeat(track, source, owner.scale.XToBeat(Math.Max(0, point.X)));
                owner.TrackSelected?.Invoke(track); owner.SetProject(project); owner.ProjectChanged?.Invoke(); e.Handled = true;
            };
            MouseLeftButtonDown += (_, e) =>
            {
                if (owner.project is not { } project) return;
                var p = e.GetPosition(this); var row = owner.RowAt(p.Y);
                if (p.Y < 28)
                {
                    if (e.ClickCount == 2)
                    {
                        owner.rangeAnchor = owner.rangeEdge = null;
                        RefreshDrawing(); owner.PlaybackRangeChanged?.Invoke(); e.Handled = true; return;
                    }
                    dragBeat = owner.scale.XToBeat(p.X);
                    pointerDownX = p.X; rangeMoved = false;
                    priorAnchor = owner.rangeAnchor; priorEdge = owner.rangeEdge;
                    rangeDrag = RangeDrag.Create;
                    if (owner.rangeAnchor is { } a && owner.rangeEdge is { } b && a != b)
                    {
                        originalStart = Math.Min(a, b); originalEnd = Math.Max(a, b);
                        var left = owner.scale.BeatToX(originalStart); var right = owner.scale.BeatToX(originalEnd);
                        if (Math.Abs(p.X - left) <= 9) rangeDrag = RangeDrag.Left;
                        else if (Math.Abs(p.X - right) <= 9) rangeDrag = RangeDrag.Right;
                        else if (p.X > left && p.X < right) rangeDrag = RangeDrag.Move;
                    }
                    if (rangeDrag == RangeDrag.Create) owner.rangeAnchor = owner.rangeEdge = Math.Max(0, Math.Round(dragBeat));
                    selectingRange = true; CaptureMouse(); RefreshDrawing(); e.Handled = true; return;
                }
                if (p.Y >= 28 && row >= 0 && row < project.Tracks.Count)
                {
                    var track = project.Tracks[row]; owner.TrackSelected?.Invoke(track);
                    moving = track.Clips.LastOrDefault(c => p.X >= X(c.StartFrame) && p.X <= X(c.StartFrame) + c.Source.Duration.TotalSeconds * c.DurationScale * PixelsPerSecond);
                    if (moving is not null) { mouseOffset = p.X - X(moving.StartFrame); CaptureMouse(); }
                }
            };
            MouseMove += (_, e) =>
            {
                if (selectingRange && e.LeftButton == MouseButtonState.Pressed)
                {
                    if (!rangeMoved && Math.Abs(e.GetPosition(this).X - pointerDownX) < 3) return;
                    rangeMoved = true;
                    var beat = Math.Max(0, Math.Round(owner.scale.XToBeat(e.GetPosition(this).X)));
                    var oldAnchor = owner.rangeAnchor; var oldEdge = owner.rangeEdge;
                    switch (rangeDrag)
                    {
                        case RangeDrag.Left: owner.rangeAnchor = Math.Min(beat, originalEnd - 1); owner.rangeEdge = originalEnd; break;
                        case RangeDrag.Right: owner.rangeAnchor = originalStart; owner.rangeEdge = Math.Max(beat, originalStart + 1); break;
                        case RangeDrag.Move:
                            var shift = Math.Max(-originalStart, Math.Round(owner.scale.XToBeat(e.GetPosition(this).X) - dragBeat));
                            owner.rangeAnchor = originalStart + shift; owner.rangeEdge = originalEnd + shift; break;
                        default: owner.rangeEdge = beat; break;
                    }
                    RefreshDrawing();
                    if ((oldAnchor != owner.rangeAnchor || oldEdge != owner.rangeEdge) && owner.GetPlaybackRange() is not null)
                        owner.PlaybackRangeChanged?.Invoke();
                    e.Handled = true; return;
                }
                if (e.GetPosition(this) is var hover && hover.Y < 28 && owner.rangeAnchor is { } ra && owner.rangeEdge is { } re && ra != re)
                {
                    var left = owner.scale.BeatToX(Math.Min(ra, re)); var right = owner.scale.BeatToX(Math.Max(ra, re));
                    Cursor = Math.Abs(hover.X - left) <= 9 || Math.Abs(hover.X - right) <= 9 ? Cursors.SizeWE : hover.X > left && hover.X < right ? Cursors.SizeAll : Cursors.Cross;
                }
                else Cursor = Cursors.Arrow;
                if (moving is null || owner.project is not { } project || e.LeftButton != MouseButtonState.Pressed) return;
                var clipBeat = Math.Max(0, Math.Round(owner.scale.XToBeat(e.GetPosition(this).X - mouseOffset)));
                moving.StartFrame = (long)Math.Round(clipBeat * project.FramesPerBeat); RefreshDrawing();
            };
            MouseLeftButtonUp += (_, e) =>
            {
                if (selectingRange)
                {
                    selectingRange = false;
                    if (!rangeMoved && owner.project is { } project)
                    {
                        owner.rangeAnchor = priorAnchor; owner.rangeEdge = priorEdge;
                        var frame = (long)Math.Max(0, Math.Round(e.GetPosition(this).X / PixelsPerSecond * project.SampleRate));
                        owner.PositionSeconds = frame / (double)project.SampleRate;
                        owner.SeekRequested?.Invoke(frame);
                        ReleaseMouseCapture(); RefreshDrawing(); e.Handled = true; return;
                    }
                    ReleaseMouseCapture(); RefreshDrawing(); owner.PlaybackRangeChanged?.Invoke(); e.Handled = true; return;
                }
                if (moving is null) return;
                moving = null; ReleaseMouseCapture(); if (owner.project is { } p) owner.SetProject(p); owner.ProjectChanged?.Invoke();
            };
        }
        private double X(long frame) => frame / (double)owner.project!.SampleRate * PixelsPerSecond;
        public void SetPosition(double seconds) { position = seconds; InvalidateVisual(); }
        public void RefreshDrawing() { background = null; InvalidateVisual(); }
        protected override void OnRenderSizeChanged(SizeChangedInfo info) { base.OnRenderSizeChanged(info); RefreshDrawing(); }
        private void Text(DrawingContext dc, string text, double x, double y, Brush brush, double size = 10) => dc.DrawText(new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip), new Point(x, y));
        protected override void OnRender(DrawingContext dc)
        {
            if (background is null)
            {
                background = new DrawingGroup();
                using (var drawing = background.Open()) DrawBackground(drawing);
                background.Freeze();
            }
            dc.DrawDrawing(background);
            if (owner.rangeAnchor is { } anchor && owner.rangeEdge is { } edge && anchor != edge)
            {
                var left = owner.scale.BeatToX(Math.Min(anchor, edge));
                var width = owner.scale.BeatToX(Math.Abs(edge - anchor));
                dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(30, 57, 188, 168)), null, new Rect(left, 28, width, Math.Max(0, ActualHeight - 28)));
                dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(100, 57, 188, 168)), new Pen(Brushes.Turquoise, 1), new Rect(left, 0, width, 27));
                dc.DrawLine(new Pen(Brushes.Turquoise, 1), new Point(left, 0), new Point(left, ActualHeight));
                dc.DrawLine(new Pen(Brushes.Turquoise, 1), new Point(left + width, 0), new Point(left + width, ActualHeight));
                dc.DrawRectangle(Brushes.Turquoise, null, new Rect(left, 3, 5, 21));
                dc.DrawRectangle(Brushes.Turquoise, null, new Rect(left + width - 5, 3, 5, 21));
            }
            var cursor = position * PixelsPerSecond;
            dc.DrawLine(new Pen(Brushes.Orange, 1.5), new Point(cursor, 22), new Point(cursor, ActualHeight));
        }

        private void DrawBackground(DrawingContext dc)
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(9, 14, 16)), null, new Rect(RenderSize));
            if (owner.project is not { } project) return;
            var beatWidth = owner.scale.PixelsPerBeat;
            for (var beat = 0; beat * beatWidth < ActualWidth; beat++)
            {
                var x = beat * beatWidth;
                dc.DrawLine(new Pen(new SolidColorBrush(beat % 4 == 0 ? Color.FromRgb(87, 103, 113) : Color.FromRgb(42, 55, 63)), beat % 4 == 0 ? 1.2 : .7), new Point(x, 28), new Point(x, ActualHeight));
                if (beat % 4 == 0) Text(dc, $"Takt {beat / 4 + 1}", x + 4, 5, Brushes.LightGray);
                else if (beatWidth >= 32) Text(dc, $"{beat / 4 + 1}.{beat % 4 + 1}", x + 4, 5, Brushes.Gray, 9);
            }
            var top = 28d;
            for (var row = 0; row < project.Tracks.Count; row++)
            {
                var track = project.Tracks[row]; var height = owner.TrackHeight(track);
                dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(row % 2 == 0 ? (byte)14 : (byte)28, 120, 150, 170)), null, new Rect(0, top, ActualWidth, height));
                dc.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(76, 91, 103)), 1), new Point(0, top + height), new Point(ActualWidth, top + height));
                foreach (var clip in track.Clips)
                {
                    var x = X(clip.StartFrame); var width = Math.Max(3, clip.Source.Duration.TotalSeconds * clip.DurationScale * PixelsPerSecond);
                    var brush = new SolidColorBrush(track.Muted ? Colors.Gray : TrackColor(row));
                    dc.PushClip(new RectangleGeometry(new Rect(x, top + 5, width, height - 10)));
                    dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(19, 34, 39)), new Pen(brush, 1), new Rect(x, top + 5, width, height - 10), 3, 3);
                    var samples = clip.Source.WaveformSamples; var channels = clip.Source.Channels; var count = samples.Length / channels;
                    var geometry = new StreamGeometry();
                    using (var ctx = geometry.Open())
                    {
                        var columns = (int)Math.Min(1600, Math.Ceiling(width));
                        for (var col = 0; col < columns; col++)
                        {
                            float peak = 0;
                            var from = (int)((long)col * count / columns); var to = Math.Max(from + 1, (int)((long)(col + 1) * count / columns));
                            for (var i = from; i < Math.Min(to, count); i++) peak = Math.Max(peak, Math.Abs(samples[i * channels]));
                            var px = x + col * width / columns; var h = Math.Min(1, peak) * (height - 28) / 2;
                            ctx.BeginFigure(new Point(px, top + (height - 10) / 2 - h), false, false); ctx.LineTo(new Point(px, top + (height - 10) / 2 + h), true, false);
                        }
                    }
                    geometry.Freeze(); dc.DrawGeometry(null, new Pen(brush, 1), geometry);
                    Text(dc, clip.Source.DisplayName, x + 5, top + height - 20, brush); dc.Pop();
                }
                top += height;
            }
            for (var y = top + 62; y < ActualHeight; y += 62)
                dc.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(47, 61, 71)), .8), new Point(0, y), new Point(ActualWidth, y));
        }
    }
}
