using System;
using FluentAssertions;
using WellenEditor;
using Xunit;

namespace WellenEditorTests;

public sealed class NativeEffectsTests
{
    private const int SampleRate = 44100;

    private static float[] Tone(double frequency, double seconds = 0.5)
    {
        var count = (int)(SampleRate * seconds);
        var samples = new float[count];
        for (var index = 0; index < count; index++)
            samples[index] = (float)(0.5 * Math.Sin(2 * Math.PI * frequency * index / SampleRate));
        return samples;
    }

    private static double TailRms(float[] samples)
    {
        var start = samples.Length / 2;
        double sum = 0;
        for (var index = start; index < samples.Length; index++) sum += samples[index] * samples[index];
        return Math.Sqrt(sum / (samples.Length - start));
    }

    [Fact]
    public void GainAddsDbToEverySample()
    {
        var input = new float[] { 0.1f, -0.2f, 0.3f };

        var output = NativeEffects.Process(new EffectSettings { GainDb = 6 }, input, 1, SampleRate, NativeEffectKind.Gain);

        output[0].Should().BeApproximately(0.199526f, 0.00001f);
        output[2].Should().BeApproximately(0.598579f, 0.00001f);
        input[0].Should().Be(0.1f);
    }

    [Fact]
    public void EqualizerBoostsCenterFrequencyMoreThanLowFrequency()
    {
        var settings = new EffectSettings { Frequency = 1000, Q = 1, GainDb = 12 };
        var center = NativeEffects.Process(settings, Tone(1000), 1, SampleRate, NativeEffectKind.Equalizer);
        var low = NativeEffects.Process(settings, Tone(100), 1, SampleRate, NativeEffectKind.Equalizer);

        var boosted = TailRms(center);
        var unprocessed = TailRms(Tone(1000));
        (boosted / unprocessed).Should().BeGreaterThan(2.5);
        TailRms(low).Should().BeLessThan(0.37);
    }

    [Fact]
    public void HighPassAttenuatesLowFrequencies()
    {
        var settings = new EffectSettings { Frequency = 200, Q = Math.Sqrt(0.5) };
        var low = NativeEffects.Process(settings, Tone(20), 1, SampleRate, NativeEffectKind.HighPass);
        var high = NativeEffects.Process(settings, Tone(2000), 1, SampleRate, NativeEffectKind.HighPass);

        TailRms(low).Should().BeLessThan(0.05);
        TailRms(high).Should().BeGreaterThan(0.3);
    }

    [Fact]
    public void LowPassAttenuatesHighFrequencies()
    {
        var settings = new EffectSettings { Frequency = 2000, Q = Math.Sqrt(0.5) };
        var high = NativeEffects.Process(settings, Tone(10000), 1, SampleRate, NativeEffectKind.LowPass);
        var low = NativeEffects.Process(settings, Tone(200), 1, SampleRate, NativeEffectKind.LowPass);

        TailRms(high).Should().BeLessThan(0.05);
        TailRms(low).Should().BeGreaterThan(0.3);
    }

    [Fact]
    public void CompressorReducesSustainedPeaksAboveThreshold()
    {
        var input = new float[SampleRate];
        Array.Fill(input, 1f);
        var settings = new EffectSettings { ThresholdDb = -20, Ratio = 10, AttackMs = 1, ReleaseMs = 10 };

        var output = NativeEffects.Process(settings, input, 1, SampleRate, NativeEffectKind.Compressor);

        output[0].Should().Be(1f); // Angriff greift nicht rückwirkend
        var tail = output.AsSpan(SampleRate / 2).ToArray();
        MaxAbsolute(tail).Should().BeLessThan(0.3f);
    }

    [Fact]
    public void LimiterCapsPeaksAtThreshold()
    {
        var input = new float[] { 1f, -1f, 0.2f };

        var output = NativeEffects.Process(new EffectSettings { ThresholdDb = -6 }, input, 1, SampleRate, NativeEffectKind.Limiter);

        MaxAbsolute(output).Should().BeLessThanOrEqualTo(0.5012f);
        output[2].Should().Be(0.2f);
    }

    [Fact]
    public void AudioDocumentAppliesEffectOnlyToSelectionAndSupportsUndo()
    {
        var samples = new float[600];
        Array.Fill(samples, 0.1f);
        var document = AudioDocument.FromSamples(samples, channels: 1, sampleRate: SampleRate, sourcePath: "test.wav");
        var original = document.Samples.ToArray();

        document.ApplyNativeEffect(new Selection(100, 300), NativeEffectKind.Gain, new EffectSettings { GainDb = 6 });

        document.Samples[99].Should().BeApproximately(0.1f, 0.000001f);
        document.Samples[100].Should().BeApproximately(0.199526f, 0.00001f);
        document.Samples[299].Should().BeApproximately(0.199526f, 0.00001f);
        document.Samples[300].Should().BeApproximately(0.1f, 0.000001f);
        document.IsModified.Should().BeTrue();

        document.Undo();
        document.Samples.Should().Equal(original);
    }

    [Fact]
    public void EveryEffectHasPresets()
    {
        foreach (NativeEffectKind kind in Enum.GetValues<NativeEffectKind>())
        {
            var presets = EffectSettings.Presets(kind);
            presets.Should().HaveCount(3);
            presets[1].Value.Should().Be(EffectSettings.For(kind));
        }
    }

    private static float MaxAbsolute(float[] samples)
    {
        var max = 0f;
        foreach (var sample in samples) max = Math.Max(max, Math.Abs(sample));
        return max;
    }
}
