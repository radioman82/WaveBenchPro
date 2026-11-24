using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace WellenEditor;

/// <summary>Compact rotary control: drag up/right, wheel for fine changes, double-click to reset.</summary>
internal sealed class RotaryKnob : FrameworkElement
{
    private readonly string label;
    private readonly double minimum, maximum, defaultValue;
    private readonly Action<double> changed;
    private double value, dragValue;
    private Point dragOrigin;
    public RotaryKnob(string label, double min, double max, double value, double defaultValue, Action<double> changed)
    {
        this.label = label; minimum = min; maximum = max; this.value = value; this.defaultValue = defaultValue; this.changed = changed;
        Width = 76; Height = 66; Cursor = Cursors.Hand; Focusable = true;
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2) SetValue(defaultValue);
            else { dragOrigin = e.GetPosition(this); dragValue = this.value; CaptureMouse(); }
            e.Handled = true;
        };
        MouseMove += (_, e) =>
        {
            if (!IsMouseCaptured) return;
            var p = e.GetPosition(this);
            var fine = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? .1 : 1;
            SetValue(dragValue + (dragOrigin.Y - p.Y + p.X - dragOrigin.X) / 150 * (maximum - minimum) * fine);
        };
        MouseLeftButtonUp += (_, e) => { if (IsMouseCaptured) ReleaseMouseCapture(); e.Handled = true; };
        MouseWheel += (_, e) => { SetValue(this.value + Math.Sign(e.Delta) * (maximum - minimum) / 100); e.Handled = true; };
        ToolTip = label;
    }
    private void SetValue(double next)
    {
        value = Math.Clamp(next, minimum, maximum);
        if (Math.Abs(value - defaultValue) < (maximum - minimum) * .005) value = defaultValue;
        changed(value); InvalidateVisual();
    }
    protected override void OnRender(DrawingContext dc)
    {
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        void Text(string text, double y, Brush brush, double size)
        {
            var t = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, brush, dpi);
            dc.DrawText(t, new Point((ActualWidth - t.Width) / 2, y));
        }
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        Text(label, 0, Brushes.LightGray, 9);
        var center = new Point(ActualWidth / 2, 33);
        dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(12, 20, 26)), new Pen(new SolidColorBrush(Color.FromRgb(69, 90, 102)), 1.3), center, 16, 16);
        var angle = (-225 + (value - minimum) / (maximum - minimum) * 270) * Math.PI / 180;
        dc.DrawLine(new Pen(Brushes.Turquoise, 2.3), center, new Point(center.X + Math.Cos(angle) * 12, center.Y + Math.Sin(angle) * 12));
        Text(minimum < 0 ? (Math.Abs(value) < .001 ? "Mitte" : $"{(value < 0 ? "L" : "R")} {Math.Abs(value):P0}") : $"{value:P0}", 50, Brushes.LightGray, 9);
    }
}
