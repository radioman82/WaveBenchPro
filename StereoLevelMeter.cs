using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace WellenEditor;

/// <summary>
/// Stereo-Pegelanzeige mit dB-Skala, Segmentierung und Peak-Hold.
/// Die Farben bleiben über die gesamte Breite ortsfest (grün → gelb → rot),
/// sodass ein Pegel immer derselben Farbe entspricht.
/// </summary>
public sealed class StereoLevelMeter : Control
{
    // -36 dB statt -60 dB: laute Passagen liegen nicht alle am rechten Rand,
// ein kurzer Peak (z. B. Bassdrum) wird dadurch sichtbar.
private const double MinimumDb = -36d;

    private readonly DispatcherTimer decayTimer = new() { Interval = TimeSpan.FromMilliseconds(25) };
    private readonly Brush trackBrush = new SolidColorBrush(Color.FromRgb(0x0B, 0x10, 0x16));
    private readonly Brush segmentBrush = new SolidColorBrush(Color.FromArgb(95, 0x10, 0x15, 0x1B));
    private readonly Brush peakBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xF3, 0xC4));
    private readonly Brush labelBrush = new SolidColorBrush(Color.FromRgb(0xD8, 0xE4, 0xEC));
    private readonly Brush scaleBrush = new SolidColorBrush(Color.FromRgb(0x8A, 0x99, 0xA6));
    private readonly Brush frameBrush = new SolidColorBrush(Color.FromRgb(0x35, 0x3D, 0x47));

    private LinearGradientBrush? gradient;
    private double gradientWidth = -1;
    private double left;
    private double right;
    private double leftPeak;
    private double rightPeak;
    private DateTime leftPeakTime = DateTime.MinValue;
    private DateTime rightPeakTime = DateTime.MinValue;

    public static readonly DependencyProperty LeftLevelProperty = DependencyProperty.Register(
        nameof(LeftLevel), typeof(double), typeof(StereoLevelMeter),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty RightLevelProperty = DependencyProperty.Register(
        nameof(RightLevel), typeof(double), typeof(StereoLevelMeter),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty LeftPeakProperty = DependencyProperty.Register(
        nameof(LeftPeak), typeof(double), typeof(StereoLevelMeter),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty RightPeakProperty = DependencyProperty.Register(
        nameof(RightPeak), typeof(double), typeof(StereoLevelMeter),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public double LeftLevel { get => (double)GetValue(LeftLevelProperty); private set => SetValue(LeftLevelProperty, value); }
    public double RightLevel { get => (double)GetValue(RightLevelProperty); private set => SetValue(RightLevelProperty, value); }
    public double LeftPeak { get => (double)GetValue(LeftPeakProperty); private set => SetValue(LeftPeakProperty, value); }
    public double RightPeak { get => (double)GetValue(RightPeakProperty); private set => SetValue(RightPeakProperty, value); }

    public StereoLevelMeter()
    {
        SnapsToDevicePixels = true;
        Loaded += (_, _) => decayTimer.Start();
        Unloaded += (_, _) => decayTimer.Stop();
        decayTimer.Tick += DecayTimer_Tick;
    }

    /// <summary>Übernimmt Spitzenpegel (0..1) pro Kanal; Mono wird auf beide Seiten gespiegelt.</summary>
    public void SetLevels(IReadOnlyList<float> peaks)
    {
        if (peaks.Count == 0) { SetTargets(0, 0); return; }

        double leftPeakValue;
        double rightPeakValue;
        if (peaks.Count == 1)
        {
            leftPeakValue = rightPeakValue = peaks[0];
        }
        else if (peaks.Count == 2)
        {
            leftPeakValue = peaks[0];
            rightPeakValue = peaks[1];
        }
        else
        {
            var leftSum = 0d;
            var rightSum = 0d;
            var leftCount = 0;
            var rightCount = 0;
            for (var index = 0; index < peaks.Count; index += 2)
            {
                leftSum += peaks[index];
                leftCount++;
                if (index + 1 < peaks.Count) { rightSum += peaks[index + 1]; rightCount++; }
            }
            leftPeakValue = leftSum / Math.Max(1, leftCount);
            rightPeakValue = rightSum / Math.Max(1, rightCount);
        }

        SetTargets(ToPosition(leftPeakValue), ToPosition(rightPeakValue));
    }

    public void Reset()
    {
        left = right = leftPeak = rightPeak = 0;
        leftPeakTime = rightPeakTime = DateTime.UtcNow;
        Publish();
    }

    private static double ToPosition(double peak)
    {
        if (!double.IsFinite(peak) || peak <= 0) return 0;
        var db = 20d * Math.Log10(peak);
        return Math.Clamp((db - MinimumDb) / -MinimumDb, 0, 1);
    }

    private void SetTargets(double targetLeft, double targetRight)
    {
        left = Math.Max(left, targetLeft); // sofortiger Anstieg, langsames Abfallen im Timer
        right = Math.Max(right, targetRight);
        var now = DateTime.UtcNow;
        if (targetLeft >= leftPeak) { leftPeak = targetLeft; leftPeakTime = now; }
        if (targetRight >= rightPeak) { rightPeak = targetRight; rightPeakTime = now; }
        Publish();
    }

    private void DecayTimer_Tick(object? sender, EventArgs e)
    {
        left = Decay(left, 0.92);
        right = Decay(right, 0.92);
        var now = DateTime.UtcNow;
        if (now - leftPeakTime > TimeSpan.FromMilliseconds(800)) leftPeak = Decay(leftPeak, 0.96);
        if (now - rightPeakTime > TimeSpan.FromMilliseconds(800)) rightPeak = Decay(rightPeak, 0.96);
        Publish();
    }

    private static double Decay(double value, double factor) =>
        value <= 0.001 ? 0 : Math.Max(0, value * factor - 0.001);

    private void Publish()
    {
        LeftLevel = left;
        RightLevel = right;
        LeftPeak = leftPeak;
        RightPeak = rightPeak;
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 2 || height <= 2) return;

        drawingContext.DrawRoundedRectangle(
            new SolidColorBrush(Color.FromRgb(0x12, 0x16, 0x1C)), null,
            new Rect(0, 0, width, height), 5, 5);
        drawingContext.DrawRoundedRectangle(
            null, new Pen(frameBrush, 1),
            new Rect(0.5, 0.5, Math.Max(0, width - 1), Math.Max(0, height - 1)), 5, 5);

        const double labelWidth = 30d;
        const double rowHeight = 21d;
        const double rightPadding = 10d;
        var trackX = labelWidth;
        var trackWidth = Math.Max(20, width - labelWidth - rightPadding);
        var brush = GetGradient(trackX, trackWidth);

        DrawRow(drawingContext, "L", trackX, 4, trackWidth, rowHeight, LeftLevel, LeftPeak, brush);
        DrawRow(drawingContext, "R", trackX, 30, trackWidth, rowHeight, RightLevel, RightPeak, brush);
        DrawScale(drawingContext, trackX, trackWidth, 62);
    }

    private LinearGradientBrush GetGradient(double x, double trackWidth)
    {
        if (gradient is not null && Math.Abs(gradientWidth - trackWidth) < 0.5) return gradient;

        var brush = new LinearGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            StartPoint = new Point(x, 0),
            EndPoint = new Point(x + trackWidth, 0)
        };
        // Klare Zonen: Grün nur bis ca. -25 dB, danach Gelb / Orange / Rot.
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(0x23, 0xA5, 0x5A), 0.00));
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(0x23, 0xA5, 0x5A), 0.29));
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(0xF2, 0xC8, 0x3C), 0.31));
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(0xF2, 0xC8, 0x3C), 0.68));
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(0xF0, 0x7A, 0x2E), 0.70));
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(0xF0, 0x7A, 0x2E), 0.86));
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(0xE7, 0x3C, 0x3C), 0.88));
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(0xE7, 0x3C, 0x3C), 1.00));
        brush.Freeze();
        gradient = brush;
        gradientWidth = trackWidth;
        return brush;
    }

    private void DrawRow(DrawingContext context, string label, double x, double y,
        double width, double height, double level, double peak, Brush gradient)
    {
        var track = new Rect(x, y, width, height);
        context.DrawRoundedRectangle(trackBrush, null, track, 3, 3);

        // Farbzonen bleiben auch ohne Signal sichtbar, jedoch gedimmt.
        context.PushOpacity(0.30);
        context.DrawRoundedRectangle(gradient, null, track, 3, 3);
        context.Pop();

        context.PushClip(new RectangleGeometry(new Rect(x, y, width * level, height)));
        context.DrawRoundedRectangle(gradient, null, track, 3, 3);
        context.Pop();

        // 60 feine Schritte (1 dB) statt grober 20er-Raster – die Anzeige wirkt kontinuierlich.
        for (var segment = 1; segment < 60; segment++)
        {
            var segmentX = x + width * segment / 60d;
            context.DrawRectangle(segmentBrush, null, new Rect(segmentX, y, 1, height));
        }

        var peakX = Math.Min(x + width * peak, x + width - 3);
        context.DrawRectangle(peakBrush, null, new Rect(peakX, y - 2, 3, height + 4));

        var labelText = new FormattedText(label, System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, new Typeface("Segoe UI"), 10, labelBrush, 1);
        context.DrawText(labelText, new Point(9, y + (height - labelText.Height) / 2d));
    }

    private void DrawScale(DrawingContext context, double x, double width, double y)
    {
        int[] marks = { -36, -30, -24, -18, -12, -6, 0 };
        foreach (var db in marks)
        {
            var markX = x + width * (db - MinimumDb) / -MinimumDb;
            context.DrawRectangle(scaleBrush, null, new Rect(markX, y, 1, 4));
            var text = new FormattedText(db.ToString(System.Globalization.CultureInfo.InvariantCulture),
                System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 10, scaleBrush, 1);
            context.DrawText(text, new Point(markX - text.Width / 2d, y + 5));
        }
    }
}
