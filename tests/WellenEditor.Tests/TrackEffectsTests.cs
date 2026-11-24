using WellenEditor;
using Xunit;

namespace WellenEditorTests;

public sealed class TrackEffectsTests
{
    [Fact]
    public void TrackInsertIsIsolatedLiveBypassableAndNonDestructive()
    {
        using var a = AudioDocument.FromSamples(Enumerable.Repeat(.1f, 1000).ToArray(), 1, 1000);
        using var b = AudioDocument.FromSamples(Enumerable.Repeat(.2f, 1000).ToArray(), 1, 1000);
        var project = new MultitrackProject(); project.Add(a); project.Add(b);
        var track = project.Tracks[0];
        var slot = new TrackEffectSlot(Guid.NewGuid(), NativeEffectKind.Gain, new EffectSettings { GainDb = 20 * Math.Log10(2) });
        track.SetEffects(new[] { slot });
        var player = new MultitrackWaveProvider(project);
        float Read()
        {
            var bytes = new byte[8]; player.Read(bytes, 0, 8);
            return System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(bytes)[0];
        }
        Assert.Equal(.4f, Read(), 5);
        Assert.Equal(.4f, project.RenderMixdown()[0], 5);
        track.SetEffects(new[] { slot with { Enabled = false } }); player.UpdateTrackEffects(track);
        Assert.Equal(.3f, Read(), 5);
        track.SetEffects(Array.Empty<TrackEffectSlot>()); player.UpdateTrackEffects(track);
        Assert.Equal(.3f, Read(), 5);
        Assert.All(a.Samples, sample => Assert.Equal(.1f, sample));
        Assert.All(b.Samples, sample => Assert.Equal(.2f, sample));
    }

    [Theory]
    [InlineData(NativeEffectKind.Gain)]
    [InlineData(NativeEffectKind.Equalizer)]
    [InlineData(NativeEffectKind.HighPass)]
    [InlineData(NativeEffectKind.LowPass)]
    [InlineData(NativeEffectKind.Compressor)]
    [InlineData(NativeEffectKind.Limiter)]
    public void InsertPreservesDspStateAcrossAudioBlocksAndMatchesExistingEffect(NativeEffectKind kind)
    {
        var samples = Enumerable.Range(0, 8192).Select(i => .6f * MathF.Sin(i * .13f)).ToArray();
        var settings = EffectSettings.For(kind);
        var expected = NativeEffects.Process(settings, samples, 2, 44100, kind);
        var actual = (float[])samples.Clone();
        var processor = new TrackEffectProcessor(new[] { new TrackEffectSlot(Guid.NewGuid(), kind, settings) }, 44100);
        for (var offset = 0; offset < actual.Length; offset += 128) processor.Process(actual.AsSpan(offset, 128));
        for (var i = 0; i < actual.Length; i++) Assert.InRange(Math.Abs(actual[i] - expected[i]), 0, .00001f);
    }
}
