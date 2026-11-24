using WellenEditor;
using Xunit;

namespace WellenEditorTests;

public sealed class MusicalGridTests
{
    [Theory]
    [InlineData(120, 4)]
    [InlineData(126, 16)]
    [InlineData(126, 32)]
    public void SampleLengthBeatGridAndZoomUseSameMusicalScale(int bpm, int beats)
    {
        var scale = new MusicalTimelineScale();
        var seconds = beats * 60d / bpm;
        Assert.Equal(scale.BeatToX(beats), seconds * scale.PixelsPerSecond(bpm), 6);
        Assert.Equal(4 * scale.PixelsPerBeat, scale.BeatToX(MusicalTimelineScale.BeatsPerBar));
        scale.ZoomHorizontal(1.25);
        Assert.Equal(scale.BeatToX(beats), seconds * scale.PixelsPerSecond(bpm), 6);
        Assert.Equal(beats, scale.XToBeat(seconds * scale.PixelsPerSecond(bpm)), 6);
    }

    [Fact]
    public void NamedSampleTempoSetsFirstProjectTempoAndConvertsSubsequentClips()
    {
        using var source = AudioDocument.FromSamples(new float[336000 * 2], 2, 44100);
        source.DisplayName = "SLTN_126_Full_Drum_Loop_01.wav";
        var project = new MultitrackProject();
        var track = project.AddEmptyTrack();
        project.PutClipOnBeat(track, source, 0);
        Assert.Equal(126, project.Bpm);
        Assert.Equal(1, track.Clips[0].DurationScale);
        project.SetBpm(140);
        project.PutClipOnBeat(track, source, 16);
        Assert.Equal(126d / 140, track.Clips[1].DurationScale, 8);
        Assert.InRange(Math.Abs(track.Clips[1].StartFrame - 16 * project.FramesPerBeat), 0, .5);
    }
}
