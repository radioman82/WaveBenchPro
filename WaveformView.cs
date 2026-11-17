using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace WellenEditor;

public sealed class WaveformView : FrameworkElement
{
    private float[] samples = Array.Empty<float>();
    private int channels = 1;
    private int sampleRate = 1;
    private int viewportStart;
    private int viewportFrames;
    private double verticalZoom = 1;
    private int? dragStart;
    private int? selectionStart;
    private int? selectionEnd;
    private int? hoverFrame;

    public event EventHandler? SelectionChanged;
    public int CursorSample => selectionEnd ?? selectionStart ?? viewportStart;

    public WaveformView()
    {
        Focusable = true;
        Cursor = Cursors.Cross;
        SnapsToDevicePixels = true;
    }

    public void SetAudio(float[] audioSamples, int audioChannels, int audioSampleRate)
    {
        UpdateAudio(audioSamples, audioChannels, audioSampleRate);
        viewportStart = 0;
        viewportFrames = FrameCount;
        verticalZoom = 1;
        selectionStart = null;
        selectionEnd = null;
        InvalidateVisual();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Wechselt die Audiodaten, behält aber Zoom, Viewport und Auswahl bei.</summary>
    public void UpdateAudio(float[] audioSamples, int audioChannels, int audioSampleRate)
    {
        samples = audioSamples;
        channels = audioChannels;
        sampleRate = audioSampleRate;
        viewportStart = Math.Clamp(viewportStart, 0, FrameCount);
        viewportFrames = Math.Clamp(viewportFrames, 1, Math.Max(1, FrameCount - viewportStart));
        selectionStart = selectionStart.HasValue ? Math.Clamp(selectionStart.Value, 0, FrameCount) : null;
        selectionEnd = selectionEnd.HasValue ? Math.Clamp(selectionEnd.Value, 0, FrameCount) : null;
        InvalidateVisual();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public Selection? GetSelection()
    {
        if (!selectionStart.HasValue || !selectionEnd.HasValue) return null;
        var start = Math.Min(selectionStart.Value, selectionEnd.Value);
        var end = Math.Max(selectionStart.Value, selectionEnd.Value);
        return end > start ? new Selection(start, end) : null;
    }

    public void SetCursor(int frame)
    {
        var cursor = Math.Clamp(frame, 0, FrameCount);
        selectionStart = cursor;
        selectionEnd = cursor;
        EnsureCursorVisible(cursor);
        InvalidateVisual();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Bewegt den Cursor sample-genau; mit extend wird die Auswahl verändert.</summary>
    public void MoveCursor(int deltaFrames, bool extend)
    {
        if (FrameCount == 0) return;
        if (extend)
        {
            var anchor = selectionStart ?? CursorSample;
            var end = selectionEnd ?? CursorSample;
            var target = Math.Clamp(end + deltaFrames, 0, FrameCount);
            selectionStart = anchor;
            selectionEnd = target;
            EnsureCursorVisible(target);
        }
        else
        {
            var target = Math.Clamp(CursorSample + deltaFrames, 0, FrameCount);
            selectionStart = target;
            selectionEnd = target;
            EnsureCursorVisible(target);
        }
        InvalidateVisual();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void EnsureCursorVisible(int frame)
    {
        if (frame < viewportStart) viewportStart = frame;
        else if (frame > viewportStart + viewportFrames) viewportStart = frame - viewportFrames;
        viewportStart = Math.Clamp(viewportStart, 0, Math.Max(0, FrameCount - viewportFrames));
    }

    public void SelectAll()
    {
        if (FrameCount == 0) return;
        selectionStart = 0;
        selectionEnd = FrameCount;
        InvalidateVisual();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ZoomIn() => Zoom(0.6);
    public void ZoomOut() => Zoom(1.65);
    public void ZoomVerticalIn() => ZoomVertical(1.45);
    public void ZoomVerticalOut() => ZoomVertical(1 / 1.45);
    public void ShowAll()
    {
        viewportStart = 0;
        viewportFrames = FrameCount;
        verticalZoom = 1;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext context)
    {
        base.OnRender(context);
        var bounds = new Rect(new Point(), RenderSize);
        context.DrawRectangle(new SolidColorBrush(Color.FromRgb(16, 22, 28)), null, bounds);
        if (FrameCount == 0 || ActualWidth < 2 || ActualHeight < 2)
        {
            DrawCenteredText(context, "Hier erscheint die Wellenform", bounds, Color.FromRgb(122, 145, 158));
            return;
        }

        var plot = GetPlotBounds();
        DrawRulers(context, bounds, plot);
        DrawGrid(context, plot);
        DrawSelection(context, plot);
        DrawWaveform(context, plot);
        DrawHover(context, plot);
        DrawCursor(context, plot);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        CaptureMouse();
        dragStart = FrameAt(e.GetPosition(this).X);
        selectionStart = dragStart;
        selectionEnd = dragStart;
        InvalidateVisual();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        hoverFrame = FrameAt(e.GetPosition(this).X);
        if (dragStart.HasValue && IsMouseCaptured) selectionEnd = hoverFrame;
        InvalidateVisual();
        if (dragStart.HasValue) SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!dragStart.HasValue) return;
        selectionEnd = FrameAt(e.GetPosition(this).X);
        if (selectionEnd == dragStart)
        {
            selectionStart = selectionEnd;
        }
        dragStart = null;
        ReleaseMouseCapture();
        InvalidateVisual();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        hoverFrame = null;
        InvalidateVisual();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        selectionStart = null;
        selectionEnd = FrameAt(e.GetPosition(this).X);
        InvalidateVisual();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (FrameCount == 0 || e.Delta == 0) return;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            ZoomVertical(e.Delta > 0 ? 1.45 : 1 / 1.45);
            e.Handled = true;
            return;
        }
        var anchor = FrameAt(e.GetPosition(this).X);
        var factor = e.Delta > 0 ? 0.6 : 1.65;
        var nextFrames = Math.Clamp((int)(viewportFrames * factor), 1, FrameCount);
        var relative = (anchor - viewportStart) / (double)Math.Max(1, viewportFrames);
        viewportStart = Math.Clamp(anchor - (int)(relative * nextFrames), 0, Math.Max(0, FrameCount - nextFrames));
        viewportFrames = nextFrames;
        InvalidateVisual();
        e.Handled = true;
    }

    private void Zoom(double factor)
    {
        if (FrameCount == 0) return;
        var anchor = viewportStart + viewportFrames / 2;
        var nextFrames = Math.Clamp((int)(viewportFrames * factor), 1, FrameCount);
        viewportStart = Math.Clamp(anchor - nextFrames / 2, 0, Math.Max(0, FrameCount - nextFrames));
        viewportFrames = nextFrames;
        InvalidateVisual();
    }

    private void ZoomVertical(double factor)
    {
        verticalZoom = Math.Clamp(verticalZoom * factor, 0.25, 16);
        InvalidateVisual();
    }

    private int FrameCount => samples.Length / channels;

    private void DrawGrid(DrawingContext context, Rect bounds)
    {
        var pen = new Pen(new SolidColorBrush(Color.FromRgb(37, 49, 60)), 1);
        var rows = Math.Max(1, channels);
        for (var channel = 0; channel < rows; channel++)
        {
            var top = bounds.Top + channel * bounds.Height / rows;
            var middle = top + bounds.Height / rows / 2;
            context.DrawLine(pen, new Point(bounds.Left, middle), new Point(bounds.Right, middle));
        }
        for (var column = 1; column < 8; column++)
        {
            var x = bounds.Left + bounds.Width * column / 8;
            context.DrawLine(pen, new Point(x, bounds.Top), new Point(x, bounds.Bottom));
        }
    }

    private void DrawSelection(DrawingContext context, Rect bounds)
    {
        var selection = GetSelection();
        if (!selection.HasValue) return;
        var start = XAt(selection.Value.StartSample, bounds);
        var end = XAt(selection.Value.EndSample, bounds);
        var color = dragStart.HasValue ? Color.FromArgb(170, 28, 151, 222) : Color.FromArgb(120, 35, 110, 165);
        context.DrawRectangle(new SolidColorBrush(color), null, new Rect(Math.Max(bounds.Left, start), bounds.Top, Math.Min(bounds.Right, end) - Math.Max(bounds.Left, start), bounds.Height));
        var handle = new Pen(new SolidColorBrush(Color.FromRgb(116, 224, 255)), 2);
        context.DrawLine(handle, new Point(start, bounds.Top), new Point(start, bounds.Bottom));
        context.DrawLine(handle, new Point(end, bounds.Top), new Point(end, bounds.Bottom));
    }

    private void DrawWaveform(DrawingContext context, Rect bounds)
    {
        var pen = new Pen(new SolidColorBrush(Color.FromRgb(73, 214, 191)), 1);
        var visibleEnd = Math.Min(FrameCount, viewportStart + viewportFrames);
        var framesPerPixel = Math.Max(1.0, (visibleEnd - viewportStart) / Math.Max(1, bounds.Width));
        for (var channel = 0; channel < channels; channel++)
        {
            var top = bounds.Top + channel * bounds.Height / channels;
            var halfHeight = Math.Max(1, (bounds.Height / channels / 2 - 3) * verticalZoom);
            var middle = top + bounds.Height / channels / 2;
            var geometry = new StreamGeometry();
            using var drawing = geometry.Open();
            for (var x = 0; x < (int)bounds.Width; x++)
            {
                var first = viewportStart + (int)(x * framesPerPixel);
                var last = Math.Min(visibleEnd, viewportStart + (int)((x + 1) * framesPerPixel) + 1);
                float min = 0, max = 0;
                for (var frame = first; frame < last; frame++)
                {
                    var value = samples[frame * channels + channel];
                    min = Math.Min(min, value);
                    max = Math.Max(max, value);
                }
                var screenX = bounds.Left + x;
                drawing.BeginFigure(new Point(screenX, middle - max * halfHeight), false, false);
                drawing.LineTo(new Point(screenX, middle - min * halfHeight), true, false);
            }
            geometry.Freeze();
            context.DrawGeometry(null, pen, geometry);
        }
    }

    private void DrawCursor(DrawingContext context, Rect bounds)
    {
        var cursor = CursorSample;
        if (cursor < viewportStart || cursor > viewportStart + viewportFrames) return;
        var pen = new Pen(new SolidColorBrush(Color.FromRgb(255, 192, 92)), 1);
        var x = XAt(cursor, bounds);
        context.DrawLine(pen, new Point(x, bounds.Top), new Point(x, bounds.Bottom));
    }

    private void DrawHover(DrawingContext context, Rect bounds)
    {
        if (!hoverFrame.HasValue || dragStart.HasValue) return;
        var x = XAt(hoverFrame.Value, bounds);
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(190, 215, 245, 245)), 1) { DashStyle = DashStyles.Dot };
        context.DrawLine(pen, new Point(x, bounds.Top), new Point(x, bounds.Bottom));
    }

    private void DrawRulers(DrawingContext context, Rect fullBounds, Rect plot)
    {
        var labelBrush = new SolidColorBrush(Color.FromRgb(157, 180, 188));
        var linePen = new Pen(new SolidColorBrush(Color.FromRgb(61, 83, 93)), 1);
        context.DrawLine(linePen, new Point(plot.Left, plot.Top), new Point(plot.Right, plot.Top));
        context.DrawLine(linePen, new Point(plot.Left, plot.Top), new Point(plot.Left, plot.Bottom));
        for (var tick = 0; tick <= 8; tick++)
        {
            var x = plot.Left + plot.Width * tick / 8;
            context.DrawLine(linePen, new Point(x, plot.Top), new Point(x, plot.Top - 5));
            var frame = viewportStart + viewportFrames * tick / 8;
            DrawSmallText(context, FormatRulerTime(frame / (double)sampleRate), new Point(x + 2, 3), labelBrush);
        }
        var dbMarks = new[] { "0", "-6", "-12", "-24", "-∞" };
        for (var channel = 0; channel < channels; channel++)
        {
            var channelTop = plot.Top + channel * plot.Height / channels;
            var channelHeight = plot.Height / channels;
            for (var mark = 0; mark < dbMarks.Length; mark++)
            {
                var y = channelTop + channelHeight * mark / (dbMarks.Length - 1);
                context.DrawLine(linePen, new Point(plot.Left - 5, y), new Point(plot.Left, y));
                DrawSmallText(context, dbMarks[mark], new Point(3, y - 6), labelBrush);
            }
        }
    }

    private void DrawSmallText(DrawingContext context, string value, Point location, Brush brush)
    {
        var text = new FormattedText(value, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Tahoma"), 10, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        context.DrawText(text, location);
    }

    private static string FormatRulerTime(double seconds)
    {
        if (seconds < 60) return $"{seconds:0.0}s";
        var duration = TimeSpan.FromSeconds(seconds);
        return $"{(int)duration.TotalMinutes}:{duration.Seconds:00}";
    }

    private void DrawCenteredText(DrawingContext context, string value, Rect bounds, Color color)
    {
        var text = new FormattedText(value, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 16, new SolidColorBrush(color), VisualTreeHelper.GetDpi(this).PixelsPerDip);
        context.DrawText(text, new Point(bounds.Left + (bounds.Width - text.Width) / 2, bounds.Top + (bounds.Height - text.Height) / 2));
    }

    private Rect GetPlotBounds() => new(46, 22, Math.Max(1, ActualWidth - 48), Math.Max(1, ActualHeight - 24));
    private int FrameAt(double x)
    {
        var plot = GetPlotBounds();
        return Math.Clamp(viewportStart + (int)Math.Round(Math.Clamp(x - plot.Left, 0, plot.Width) / plot.Width * viewportFrames), 0, FrameCount);
    }
    private double XAt(int frame, Rect bounds) => bounds.Left + (frame - viewportStart) / (double)Math.Max(1, viewportFrames) * bounds.Width;
}
