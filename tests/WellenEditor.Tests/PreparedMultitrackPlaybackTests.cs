using System.Runtime.InteropServices;
using WellenEditor;
using Xunit;

namespace WellenEditorTests;

public sealed class PreparedMultitrackPlaybackTests
{
    [Theory]
    [InlineData(44100, 48000, .8, 256)]
    [InlineData(48000, 44100, 1.4, 1024)]
    [InlineData(96000, 48000, .7, 4096)]
    [InlineData(192000, 96000, 1.6, 8192)]
    public void RandomSeeksAndLoopsMatchContinuousReference(int sourceRate, int rate, double stretch, int blockSize)
    {
        using var anchor = AudioDocument.FromSamples(new float[2], 2, rate);
        var samples = Enumerable.Range(0, 5000).SelectMany(i =>
        {
            var value = .2f * MathF.Sin(i * .17f) + (i % 137 == 0 ? .4f : 0);
            return new[] { value, -value };
        }).ToArray();
        using var sound = AudioDocument.FromSamples(samples, 2, sourceRate);
        var project = new MultitrackProject(); project.Add(anchor); project.Add(sound);
        project.Tracks[1].Clips[0].StartFrame = 17;
        project.Tracks[1].Clips[0].DurationScale = stretch;
        var expected = project.RenderMixdown();
        using var session = new PreparedMultitrackSession(project.Snapshot(), project.Tracks);
        foreach (var start in new[] { 0, 17, 255, Math.Min(1023, expected.Length / 2 - 1) })
        {
            var length = Math.Min(513, expected.Length / 2 - start);
            using var renderer = session.OpenRenderer(start, length, true, blockSize);
            var block = new float[blockSize * 2];
            var offset = 0;
            for (var iteration = 0; iteration < 7; iteration++)
            {
                var read = renderer.Read(block, blockSize);
                Assert.True(read > 0);
                for (var i = 0; i < read * 2; i++)
                    Assert.InRange(Math.Abs(expected[(start + offset) * 2 + i] - block[i]), 0, 1e-6f);
                offset = (offset + read) % length;
            }
        }
    }

    [Fact]
    public void LiveControlsDoNotPrepareOrChangeInsertState()
    {
        using var a = AudioDocument.FromSamples(Enumerable.Repeat(.25f, 100).ToArray(), 1, 48000);
        using var b = AudioDocument.FromSamples(Enumerable.Repeat(.5f, 100).ToArray(), 1, 48000);
        var project = new MultitrackProject(); project.Add(a); project.Add(b);
        project.Tracks[1].Muted = true;
        using var session = new PreparedMultitrackSession(project.Snapshot(), project.Tracks);
        using var renderer = session.OpenRenderer(0, 100, true, 16);
        var block = new float[32];
        renderer.Read(block, 16); Assert.Equal(.25f, block[0]);
        project.Tracks[1].Solo = true; project.Tracks[1].Gain = .5; project.Tracks[1].Pan = 1;
        renderer.Read(block, 16); Assert.Equal(0, block[0]); Assert.Equal(.25f, block[1]);
        project.Tracks[1].Muted = true;
        renderer.Read(block, 16); Assert.Equal(.25f, block[0]);
    }

    [Fact]
    public async Task GenerationsSeekLoopEofAndOneHundredRestarts()
    {
        using var audio = AudioDocument.FromSamples(Enumerable.Range(0, 4096).Select(i => i / 4096f).ToArray(), 1, 48000);
        var project = new MultitrackProject(); project.Add(audio);
        using var session = new PreparedMultitrackSession(project.Snapshot(), project.Tracks);
        using var playback = new PreparedMultitrackPlayback(session, 256, 4);
        var bytes = new byte[128 * 8];
        for (var cycle = 0; cycle < 100; cycle++)
        {
            var start = cycle * 7;
            await playback.ConfigurePlaybackAsync(start, 128, false);
            Assert.Equal(bytes.Length, playback.Read(bytes));
            Assert.Equal(start / 4096f, MemoryMarshal.Cast<byte, float>(bytes)[0]);
            Assert.Equal(0, playback.Read(bytes));
            Assert.Null(playback.WorkerError);
        }
        var requests = Enumerable.Range(0, 20).Select(i => playback.ConfigurePlaybackAsync(i * 11, 128, true)).ToArray();
        await Task.WhenAll(requests);
        Assert.Equal(bytes.Length, playback.Read(bytes));
        Assert.Equal(209 / 4096f, MemoryMarshal.Cast<byte, float>(bytes)[0]);
        Assert.Equal(336, playback.AudibleFrame(0));
        Assert.Equal(286, playback.AudibleFrame(50));
    }

    [Fact]
    public void SessionKeepsFilesAliveForAnActiveRenderer()
    {
        using var audio = AudioDocument.FromSamples(Enumerable.Repeat(.2f, 100).ToArray(), 1, 48000);
        var project = new MultitrackProject(); project.Add(audio);
        var session = new PreparedMultitrackSession(project.Snapshot(), project.Tracks);
        using var renderer = session.OpenRenderer(0, 100, false, 16);
        session.Dispose();
        var block = new float[32];
        Assert.Equal(16, renderer.Read(block, 16));
        Assert.Equal(.2f, block[0]);
        Assert.Throws<ObjectDisposedException>(() => session.OpenRenderer(0, 100, false, 16));
    }

    [Fact]
    public void AntiPhaseStretchHasTheSameLeftChannelAsInPhaseStretch()
    {
        var mono = Enumerable.Range(0, 10000).Select(i => .3f * MathF.Sin(i * .19f) + (i % 131 == 0 ? .4f : 0)).ToArray();
        var inPhase = AudioTimeStretch.Stretch(mono.SelectMany(value => new[] { value, value }).ToArray(), 1.4);
        var antiPhase = AudioTimeStretch.Stretch(mono.SelectMany(value => new[] { value, -value }).ToArray(), 1.4);
        Assert.Equal(inPhase.Length, antiPhase.Length);
        for (var i = 0; i < inPhase.Length; i += 2)
        {
            Assert.Equal(inPhase[i], antiPhase[i]);
            Assert.Equal(antiPhase[i], -antiPhase[i + 1]);
        }
    }
}
