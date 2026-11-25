using WellenEditor;
using NAudio.Vst3;
using Xunit;

namespace WellenEditorTests;

public sealed class TrackEffectsTests
{
    [Fact]
    public void OrderedVstAndNativeEffectsProcessTheOverlappedTrackAndMatchMixdown()
    {
        using var sound = AudioDocument.FromSamples(new[] { .25f, .25f }, 2, 1000);
        var project = new MultitrackProject();
        project.Add(sound);
        var track = project.Tracks[0];
        track.Clips.Add(new MultitrackClip(sound, 0));
        var vst = TrackEffectSlot.ForVst(FakeVst());
        var gain = new TrackEffectSlot(Guid.NewGuid(), NativeEffectKind.Gain, new EffectSettings { GainDb = 20 * Math.Log10(2) });
        TrackEffectChainRenderer.VstRenderer clipper = (_, input, _) => input.Select(value => Math.Clamp(value, -.6f, .6f)).ToArray();

        track.SetEffects(new[] { gain, vst });
        var gainThenVst = ReadFirstSample(new MultitrackWaveProvider(project, clipper));
        var gainThenVstMix = project.RenderMixdown(clipper)[0];

        track.SetEffects(new[] { vst, gain });
        var vstThenGain = ReadFirstSample(new MultitrackWaveProvider(project, clipper));
        var vstThenGainMix = project.RenderMixdown(clipper)[0];

        Assert.Equal(.6f, gainThenVst, 5);
        Assert.Equal(gainThenVst, gainThenVstMix, 5);
        Assert.Equal(1f, vstThenGain, 5);
        Assert.Equal(vstThenGain, vstThenGainMix, 5);
    }

    [Fact]
    public void VstStateChangesDuringPlaybackPublishNewTrackAudioWithoutRestart()
    {
        using var sound = AudioDocument.FromSamples(Enumerable.Repeat(.1f, 4000).ToArray(), 1, 1000);
        var project = new MultitrackProject();
        project.Add(sound);
        var track = project.Tracks[0];
        var slot = TrackEffectSlot.ForVst(FakeVst(), new byte[] { 2 });
        track.SetEffects(new[] { slot });
        TrackEffectChainRenderer.VstRenderer stateGain = (effect, input, _) =>
            input.Select(value => value * effect.VstState![0]).ToArray();
        var provider = new MultitrackWaveProvider(project, stateGain);
        provider.ConfigurePlayback(0, provider.FrameCount, true);
        Assert.Equal(.2f, ReadFirstSample(provider), 5);

        track.SetEffects(new[] { slot with { VstState = new byte[] { 3 } } });
        provider.UpdateTrackEffects(track);
        var changed = SpinWait.SpinUntil(() => Math.Abs(ReadFirstSample(provider) - .3f) < .00001f, TimeSpan.FromSeconds(3));

        Assert.True(changed, "der aktualisierte VST-State muss während der laufenden Schleife hörbar werden");
    }

    private static Vst3EffectDescriptor FakeVst()
    {
        var classInfo = new Vst3ClassInfo("fake-class-id", "Audio Module Class", "Fake", "Tests", "1", "", "Fx");
        return new Vst3EffectDescriptor("Fake", "Tests", "fake.vst3", classInfo, "Fx");
    }

    private static float ReadFirstSample(MultitrackWaveProvider provider)
    {
        var bytes = new byte[sizeof(float) * 2];
        var read = provider.Read(bytes, 0, bytes.Length);
        return read == 0 ? 0 : System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(bytes)[0];
    }

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
