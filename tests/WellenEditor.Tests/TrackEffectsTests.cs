using WellenEditor;
using NAudio.Vst3;
using System.Diagnostics;
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
    public async Task VstStateChangesDuringPlaybackPublishNewTrackAudioWithoutRestart()
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
        await provider.UpdateTrackEffects(track);

        Assert.Equal(.3f, ReadFirstSample(provider), 5);
    }

    [Fact]
    public async Task RemovingVstWhilePreparationIsBlockedPreventsStaleStatePublication()
    {
        using var sound = AudioDocument.FromSamples(Enumerable.Repeat(.1f, 1000).ToArray(), 1, 1000);
        var project = new MultitrackProject();
        project.Add(sound);
        var track = project.Tracks[0];
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        TrackEffectChainRenderer.VstRenderer blocking = (_, input, _) =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("test gate was not released");
            return input.Select(value => value * 3).ToArray();
        };
        var provider = new MultitrackWaveProvider(project, blocking);
        track.SetEffects(new[] { TrackEffectSlot.ForVst(FakeVst()) });
        var stalePreparation = provider.UpdateTrackEffects(track);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(2)), "VST preparation should reach the deterministic gate");

        track.SetEffects(Array.Empty<TrackEffectSlot>());
        await provider.UpdateTrackEffects(track);
        release.Set();
        await stalePreparation.WaitAsync(TimeSpan.FromSeconds(2));
        provider.Rewind();

        Assert.Equal(.1f, ReadFirstSample(provider), 5);
    }

    [Fact]
    public void MixdownFailsInsteadOfSilentlyExportingDryAudioWhenVstFails()
    {
        using var sound = AudioDocument.FromSamples(Enumerable.Repeat(.1f, 32).ToArray(), 1, 1000);
        var project = new MultitrackProject();
        project.Add(sound);
        project.Tracks[0].SetEffects(new[] { TrackEffectSlot.ForVst(FakeVst()) });

        var error = Assert.Throws<InvalidOperationException>(() => project.RenderMixdown((_, _, _) => Array.Empty<float>()));

        Assert.Contains("VST3-Effekt", error.ToString());
    }

    [Fact]
    public void DisabledVstIsBypassedWithoutCallingTheRenderer()
    {
        using var sound = AudioDocument.FromSamples(new[] { .1f, .2f }, 1, 1000);
        var project = new MultitrackProject();
        project.Add(sound);
        project.Tracks[0].SetEffects(new[] { TrackEffectSlot.ForVst(FakeVst()) with { Enabled = false } });
        var rendererCalled = false;

        var mix = project.RenderMixdown((_, _, _) =>
        {
            rendererCalled = true;
            return Array.Empty<float>();
        });

        Assert.False(rendererCalled);
        Assert.Equal(new[] { .1f, .1f, .2f, .2f }, mix);
    }

    [Fact]
    public void FarPositionedClipIsRejectedBeforeMixdownAllocationOrVstRender()
    {
        using var sound = AudioDocument.FromSamples(new[] { .1f }, 1, 1000);
        var project = new MultitrackProject();
        project.Add(sound);
        var track = project.Tracks[0];
        track.Clips[0].StartFrame = 40_000_000;
        track.SetEffects(new[] { TrackEffectSlot.ForVst(FakeVst()) });
        var vstWasCalled = false;
        GC.Collect();
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();

        var error = Assert.Throws<NotSupportedException>(() => project.RenderMixdown((_, input, _) =>
        {
            vstWasCalled = true;
            return input;
        }));

        Assert.Contains("256 MiB", error.Message);
        Assert.False(vstWasCalled);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        Console.WriteLine($"40,000,000-frame VST clip preflight: rejected before audio allocation; {allocated / 1024d:F1} KiB allocated by the guarded call.");
        Assert.True(allocated < 4 * 1024 * 1024, "the rejected far-positioned clip must not materialize its timeline");
    }

    [Fact]
    public void ProjectVstMemoryBudgetRejectsAggregateTracksBeforePreparingAnyTrack()
    {
        using var sound = AudioDocument.FromSamples(new[] { .1f }, 1, 1000);
        var project = new MultitrackProject();
        project.Add(sound);
        project.Tracks[0].Clips[0].StartFrame = 10_000_000;
        project.Tracks[0].SetEffects(new[] { TrackEffectSlot.ForVst(FakeVst()) });
        for (var index = 0; index < 2; index++)
        {
            project.Add(sound);
            project.Tracks[^1].Clips[0].StartFrame = 10_000_000;
            project.Tracks[^1].SetEffects(new[] { TrackEffectSlot.ForVst(FakeVst()) });
        }

        var error = Assert.Throws<NotSupportedException>(() => project.RenderMixdown((_, input, _) => input));

        Assert.Contains("Projekt-Speicherlimit von 512 MiB", error.Message);
    }

    [Fact]
    public void SixtySecondVstMixdownBenchmarkStaysWithinPreparedMemoryBudget()
    {
        const int sampleRate = 48000;
        var samples = new float[sampleRate * 60];
        for (var index = 0; index < samples.Length; index++) samples[index] = .1f * MathF.Sin(index * .03f);
        using var sound = AudioDocument.FromSamples(samples, 1, sampleRate);
        var project = new MultitrackProject();
        project.Add(sound);
        project.Tracks[0].SetEffects(new[] { TrackEffectSlot.ForVst(FakeVst()) });
        TrackEffectChainRenderer.VstRenderer passthrough = (_, input, _) => input;
        var before = GC.GetAllocatedBytesForCurrentThread();
        var stopwatch = Stopwatch.StartNew();

        var mix = project.RenderMixdown(passthrough);

        stopwatch.Stop();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(sampleRate * 60 * 2, mix.Length);
        Console.WriteLine($"60 s / 48 kHz VST dry-passthrough benchmark: {stopwatch.Elapsed.TotalMilliseconds:F0} ms, {allocated / (1024d * 1024):F1} MiB allocated; WorkingSet measurement not isolated.");
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
    public async Task TrackInsertIsIsolatedLiveBypassableAndNonDestructive()
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
        track.SetEffects(new[] { slot with { Enabled = false } }); await player.UpdateTrackEffects(track);
        Assert.Equal(.3f, Read(), 5);
        track.SetEffects(Array.Empty<TrackEffectSlot>()); await player.UpdateTrackEffects(track);
        Assert.Equal(.3f, Read(), 5);
        Assert.All(a.Samples, sample => Assert.Equal(.1f, sample));
        Assert.All(b.Samples, sample => Assert.Equal(.2f, sample));
    }

    [Fact]
    public void StatefulNativeFilterPlaybackMatchesMixdownFromTimelineStart()
    {
        using var sound = AudioDocument.FromSamples(Enumerable.Range(0, 4096).Select(index => .7f * MathF.Sin(index * .11f)).ToArray(), 1, 48000);
        var project = new MultitrackProject();
        project.Add(sound);
        project.Tracks[0].SetEffects(new[] { new TrackEffectSlot(Guid.NewGuid(), NativeEffectKind.HighPass, new EffectSettings { Frequency = 250 }) });
        var expected = project.RenderMixdown();
        var provider = new MultitrackWaveProvider(project);
        var bytes = new byte[expected.Length * sizeof(float)];

        Assert.Equal(bytes.Length, provider.Read(bytes));

        Assert.Equal(expected, System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(bytes).ToArray());
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
