using WellenEditor;
using Xunit;

namespace WellenEditorTests;

public sealed class SequencerTests
{
    [Fact]
    public void NativeLoopRepeatsAndAcceptsLiveRangeChangesWithoutRestart()
    {
        using var sound = AudioDocument.FromSamples(Enumerable.Range(0, 20).Select(i => i / 100f).ToArray(), 1, 1000);
        var project = new MultitrackProject(); project.Add(sound);
        var source = new MultitrackWaveProvider(project);
        float[] Read(int frames)
        {
            var bytes = new byte[frames * 8]; Assert.Equal(bytes.Length, source.Read(bytes, 0, bytes.Length));
            return System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(bytes).ToArray().Where((_, i) => i % 2 == 0).ToArray();
        }
        source.ConfigurePlayback(5, 3, true);
        Assert.Equal(new[] { .05f, .06f, .07f, .05f, .06f, .07f, .05f, .06f }, Read(8));
        source.ConfigurePlayback(10, 2, true);
        Assert.Equal(new[] { .10f, .11f, .10f, .11f, .10f }, Read(5));
        for (var i = 0; i < 1000; i++) Assert.Equal(17, Read(17).Length);
        source.ConfigurePlayback(17, 6, true); // retain musical length beyond the last audio sample
        Assert.Equal(new[] { .17f, .18f, .19f, 0f, 0f, 0f, .17f }, Read(7));
    }

    [Fact]
    public void NativeLoopCanBeDisabledDuringPlaybackAndThenEnds()
    {
        using var sound = AudioDocument.FromSamples(new float[20], 1, 1000);
        var project = new MultitrackProject(); project.Add(sound);
        var source = new MultitrackWaveProvider(project);
        source.ConfigurePlayback(3, 2, true);
        var bytes = new byte[80];
        Assert.Equal(80, source.Read(bytes, 0, bytes.Length));
        source.ConfigurePlayback(3, 2, false);
        Assert.Equal(16, source.Read(bytes, 0, bytes.Length));
        Assert.Equal(0, source.Read(bytes, 0, bytes.Length));
    }

    [Fact]
    public void MultitrackSelectionLoopsOnlySelectedFramesAcrossAudioBlocks()
    {
        using var sound = AudioDocument.FromSamples(Enumerable.Range(0, 20).Select(i => i / 100f).ToArray(), 1, 1000);
        var project = new MultitrackProject(); project.Add(sound);
        var source = new MultitrackWaveProvider(project);
        source.SetPlaybackRange(5, 3);
        using var loop = new LoopingWaveProvider(source);
        float[] Read(int frames)
        {
            var buffer = new byte[frames * 8];
            Assert.Equal(buffer.Length, loop.Read(buffer, 0, buffer.Length));
            return System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(buffer).ToArray().Where((_, i) => i % 2 == 0).ToArray();
        }
        Assert.Equal(new[] { .05f, .06f, .07f, .05f }, Read(4));
        Assert.Equal(new[] { .06f, .07f, .05f, .06f, .07f }, Read(5));
        project.Tracks[0].Muted = true;
        Assert.All(Read(5), sample => Assert.Equal(0, sample));
        project.Tracks[0].Muted = false;
        Assert.Equal(new[] { .07f, .05f, .06f }, Read(3));
    }

    [Fact]
    public void MultitrackSelectionWithoutLoopStopsAtSelectionEnd()
    {
        using var sound = AudioDocument.FromSamples(new float[20], 1, 1000);
        var project = new MultitrackProject(); project.Add(sound);
        var source = new MultitrackWaveProvider(project);
        source.SetPlaybackRange(18, 10);
        var bytes = new byte[80];
        Assert.Equal(16, source.Read(bytes, 0, bytes.Length));
        Assert.Equal(0, source.Read(bytes, 0, bytes.Length));
        source.Rewind();
        Assert.Equal(16, source.Read(bytes, 0, bytes.Length));
        Assert.Throws<ArgumentOutOfRangeException>(() => source.SetPlaybackRange(20, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => source.SetPlaybackRange(1, 0));
    }

    [Fact]
    public void MixerReadsMuteSoloGainAndPanAgainForEveryAudioBlock()
    {
        using var drums = AudioDocument.FromSamples(Enumerable.Repeat(.2f, 1000).ToArray(), 1, 1000);
        using var synth = AudioDocument.FromSamples(Enumerable.Repeat(.4f, 1000).ToArray(), 1, 1000);
        var project = new MultitrackProject(); project.Add(drums); project.Add(synth);
        var provider = new MultitrackWaveProvider(project);
        float[] ReadFrame()
        {
            var bytes = new byte[8]; Assert.Equal(8, provider.Read(bytes, 0, 8));
            return System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(bytes).ToArray();
        }
        Assert.Equal(.6f, ReadFrame()[0], 5);
        project.Tracks[1].Muted = true;
        Assert.Equal(.2f, ReadFrame()[0], 5);
        project.Tracks[1].Solo = true;
        Assert.False(project.Tracks[1].Muted);
        Assert.Equal(.4f, ReadFrame()[0], 5);
        project.Tracks[1].Muted = true;
        Assert.False(project.Tracks[1].Solo);
        Assert.Equal(.2f, ReadFrame()[0], 5);
        project.Tracks[1].Muted = false;
        project.Tracks[1].Solo = true;
        Assert.Equal(.4f, ReadFrame()[0], 5);
        project.Tracks[1].Gain = .5; project.Tracks[1].Pan = 1;
        var frame = ReadFrame(); Assert.Equal(0, frame[0]); Assert.Equal(.2f, frame[1], 5);
    }

    [Fact]
    public void SoloCanCombineTracksButMuteAndSoloAreExclusiveOnEachTrack()
    {
        using var sound = AudioDocument.FromSamples(Enumerable.Repeat(.1f, 100).ToArray(), 1, 1000);
        var project = new MultitrackProject();
        project.Add(sound); project.Add(sound); project.Add(sound);
        var provider = new MultitrackWaveProvider(project);
        float ReadFrame()
        {
            var buffer = new byte[8]; provider.Read(buffer, 0, buffer.Length);
            return System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(buffer)[0];
        }
        project.Tracks[0].Solo = true; project.Tracks[1].Solo = true;
        Assert.Equal(.2f, ReadFrame(), 5);
        project.Tracks[0].Muted = true;
        Assert.False(project.Tracks[0].Solo);
        Assert.True(project.Tracks[1].Solo);
        Assert.Equal(.1f, ReadFrame(), 5);
        project.Tracks[0].Solo = true;
        Assert.False(project.Tracks[0].Muted);
        Assert.Equal(.2f, ReadFrame(), 5);
        project.Tracks[0].Solo = false; project.Tracks[1].Solo = false;
        Assert.Equal(.3f, ReadFrame(), 5);
        project.Tracks[0].Muted = true;
        project.Tracks[0].Solo = false; // switching off an inactive button preserves mute
        Assert.True(project.Tracks[0].Muted);
        project.Tracks[0].Solo = true;
        project.Tracks[0].Muted = false; // switching off an inactive button preserves solo
        Assert.True(project.Tracks[0].Solo);
    }

    [Fact]
    public void TempoChangeKeepsBeatPositionAndChangesDurationWithoutTransposingSine()
    {
        using var tone = AudioDocument.FromSamples(Enumerable.Range(0, 48000).Select(i => .5f * MathF.Sin(2 * MathF.PI * 440 * i / 48000)).ToArray(), 1, 48000);
        var project = new MultitrackProject(); project.Add(tone);
        project.Tracks[0].StartFrame = 96000;
        project.SetBpm(140);
        var clip = project.Tracks[0].Clips[0];
        Assert.InRange(Math.Abs(clip.StartFrame - 4 * project.FramesPerBeat), 0, .6);
        Assert.Equal(120d / 140, clip.DurationScale, 8);
        var provider = new MultitrackWaveProvider(project);
        Assert.InRange(provider.FrameCount - clip.StartFrame, 41142, 41144);
        var stereo = Enumerable.Range(0, 48000).SelectMany(i => new[] { tone.Samples[i], tone.Samples[i] }).ToArray();
        var stretched = AudioTimeStretch.Stretch(stereo, clip.DurationScale);
        var crossings = 0;
        for (var i = 9601; i < 33600; i++) if (stretched[(i - 1) * 2] <= 0 && stretched[i * 2] > 0) crossings++;
        Assert.InRange(crossings * 2, 430, 450);
        project.SetBpm(120);
        Assert.InRange(clip.StartFrame, 95999, 96001);
        Assert.Equal(1, clip.DurationScale, 8);
    }

    [Fact]
    public void MultipleClipsOnOneTrackAreMixedAtBeatPositionsWithAnotherTrack()
    {
        using var source = AudioDocument.FromSamples(new[] { .25f, .25f }, 1, 100);
        var project = new MultitrackProject(); // 44100 Hz project, 120 BPM = 22050 frames per beat
        var drums = project.AddEmptyTrack();
        project.PutClipOnBeat(drums, source, 0);
        project.PutClipOnBeat(drums, source, 1);
        var synth = project.AddEmptyTrack();
        project.PutClipOnBeat(synth, source, 1);
        var mix = project.RenderMixdown();
        Assert.Equal(2, drums.Clips.Count);
        Assert.Equal(22050, drums.Clips[1].StartFrame);
        Assert.Equal(.25f, mix[0]);
        Assert.Equal(0, mix[5000]);
        Assert.Equal(.5f, mix[22050 * 2]);
        drums.Clips[1].StartFrame = 44100;
        Assert.Equal(44982, project.TimelineFrames);
    }
}
