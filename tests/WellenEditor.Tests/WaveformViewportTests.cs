using Xunit;

namespace WellenEditor.Tests;

public sealed class WaveformViewportTests
{
    [Fact]
    public void ShorterAudioAfterRecordingUndoRetainsAVisibleWindow()
    {
        using var control = new AudioControlThread();
        control.Invoke(() =>
        {
            var view = new WaveformView();
            view.SetAudio(new float[1000], 1, 100);
            view.ZoomIn();
            view.SetCursor(1000);
            Assert.True(view.Viewport.Start > 100);
            view.UpdateAudio(new float[100], 1, 100);
            Assert.Equal((0, 100), view.Viewport);
            Assert.Equal(100, view.CursorSample);
        });
    }

    [Fact]
    public void RemovingAllAudioLeavesAnEmptyViewAtZero()
    {
        using var control = new AudioControlThread();
        control.Invoke(() =>
        {
            var view = new WaveformView();
            view.SetAudio(new float[1000], 1, 100);
            view.ZoomIn(); view.SetCursor(1000);
            view.UpdateAudio([], 1, 100);
            Assert.Equal((0, 1), view.Viewport);
            Assert.Equal(0, view.CursorSample);
        });
    }
}
