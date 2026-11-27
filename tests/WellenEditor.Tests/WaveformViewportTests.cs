using Xunit;
using NAudio.Wave;
using System.IO;

namespace WellenEditor.Tests;

public sealed class WaveformViewportTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SavingStreamingEditsPreservesUndo(bool overwriteSource)
    {
        var path = Path.Combine(Path.GetTempPath(), $"wavebench-save-{Guid.NewGuid():N}.wav");
        var destination = overwriteSource ? path : path + ".copy.wav";
        try
        {
            using (var writer = new WaveFileWriter(path, new WaveFormat(44100, 16, 1)))
                writer.Write(new byte[4000], 0, 4000);
            using var document = AudioDocument.Load(path);
            document.Delete(new Selection(200, 700));
            document.SaveAsWave(destination, 16);
            document.UpdateSourcePath(destination);
            Assert.True(document.IsStreaming);
            Assert.True(document.CanUndo);
            document.Undo();
            Assert.Equal(2000, document.FrameCount);
            Assert.True(document.CanRedo);
            document.Redo();
            Assert.Equal(1500, document.FrameCount);
        }
        finally
        {
            File.Delete(path);
            if (!overwriteSource) File.Delete(destination);
        }
    }

    [Fact]
    public void RefreshingAStreamingEditPreservesUndoAndTimeline()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wavebench-undo-{Guid.NewGuid():N}.wav");
        try
        {
            using (var writer = new WaveFileWriter(path, new WaveFormat(44100, 16, 1)))
                writer.Write(new byte[4000], 0, 4000);
            using var document = AudioDocument.Load(path);
            using var control = new AudioControlThread();
            control.Invoke(() =>
            {
                var view = new WaveformView();
                view.SetStreamingPreview(document.WaveformSamples, document.Channels, document.SampleRate, document.FrameCount);
                document.Delete(new Selection(200, 700));
                view.UpdateDocument(document);
                Assert.True(document.IsStreaming);
                Assert.True(document.CanUndo);
                Assert.Equal(1500, view.Viewport.Count);
                document.Undo();
                view.UpdateDocument(document);
                Assert.Equal(2000, document.FrameCount);
                Assert.True(document.CanRedo);
                document.EnsureMaterialized();
                Assert.True(document.CanRedo);
                document.Redo();
                Assert.Equal(1500, document.FrameCount);
                document.Undo();
                Assert.Equal(2000, document.FrameCount);
            });
        }
        finally { File.Delete(path); }
    }
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
