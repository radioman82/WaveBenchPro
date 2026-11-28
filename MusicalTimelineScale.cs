namespace WellenEditor;

internal sealed class MusicalTimelineScale
{
    public const int BeatsPerBar = 4;
    public double PixelsPerBeat { get; private set; } = 64;
    public double VerticalScale { get; private set; } = 1;
    public double PixelsPerSecond(double bpm) => PixelsPerBeat * bpm / 60;
    public double BeatToX(double beat) => beat * PixelsPerBeat;
    public double XToBeat(double x) => x / PixelsPerBeat;
    public void ZoomHorizontal(double factor) => PixelsPerBeat = Math.Clamp(PixelsPerBeat * factor, 16, 256);
    public void ZoomVertical(double factor) => VerticalScale = Math.Clamp(VerticalScale * factor, .8, 2.5);
    public void ResetHorizontal() => PixelsPerBeat = 64;
    public void ResetVertical() => VerticalScale = 1;
    internal void Restore(double horizontal, double vertical) { PixelsPerBeat = Math.Clamp(horizontal, 16, 256); VerticalScale = Math.Clamp(vertical, .8, 2.5); }
}
