using System;
using System.Collections.Generic;

namespace WellenEditor;

public enum NativeEffectKind
{
    Gain,
    Equalizer,
    HighPass,
    LowPass,
    Compressor,
    Limiter
}

/// <summary>Parameter für die nativen Effekte. Alle Pegelangaben in dBFS.</summary>
public sealed record EffectSettings
{
    public double GainDb { get; init; }
    public double Frequency { get; init; } = 1000;
    public double Q { get; init; } = 1;
    public double ThresholdDb { get; init; } = -18;
    public double Ratio { get; init; } = 4;
    public double AttackMs { get; init; } = 10;
    public double ReleaseMs { get; init; } = 100;
    public double MakeupDb { get; init; }

    public static EffectSettings For(NativeEffectKind kind) => kind switch
    {
        NativeEffectKind.Gain => new EffectSettings { GainDb = 3 },
        NativeEffectKind.Equalizer => new EffectSettings { Frequency = 1000, Q = 1, GainDb = 3 },
        NativeEffectKind.HighPass => new EffectSettings { Frequency = 80 },
        NativeEffectKind.LowPass => new EffectSettings { Frequency = 12000 },
        NativeEffectKind.Compressor => new EffectSettings { ThresholdDb = -18, Ratio = 4, AttackMs = 10, ReleaseMs = 100 },
        NativeEffectKind.Limiter => new EffectSettings { ThresholdDb = -1 },
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    public static IReadOnlyList<KeyValuePair<string, EffectSettings>> Presets(NativeEffectKind kind) => kind switch
    {
        NativeEffectKind.Gain => new[]
        {
            new KeyValuePair<string, EffectSettings>("Sanft (-3 dB)", new EffectSettings { GainDb = -3 }),
            new KeyValuePair<string, EffectSettings>("Standard (+3 dB)", For(kind)),
            new KeyValuePair<string, EffectSettings>("Stark (+6 dB)", new EffectSettings { GainDb = 6 })
        },
        NativeEffectKind.Equalizer => new[]
        {
            new KeyValuePair<string, EffectSettings>("Tief (250 Hz)", new EffectSettings { Frequency = 250, Q = 0.7, GainDb = 6 }),
            new KeyValuePair<string, EffectSettings>("Mitte (1 kHz)", For(kind)),
            new KeyValuePair<string, EffectSettings>("Höhen (4 kHz)", new EffectSettings { Frequency = 4000, Q = 2, GainDb = -9 })
        },
        NativeEffectKind.HighPass => new[]
        {
            new KeyValuePair<string, EffectSettings>("Tief (40 Hz)", new EffectSettings { Frequency = 40 }),
            new KeyValuePair<string, EffectSettings>("Standard (80 Hz)", For(kind)),
            new KeyValuePair<string, EffectSettings>("Stark (160 Hz)", new EffectSettings { Frequency = 160 })
        },
        NativeEffectKind.LowPass => new[]
        {
            new KeyValuePair<string, EffectSettings>("Hoch (18 kHz)", new EffectSettings { Frequency = 18000 }),
            new KeyValuePair<string, EffectSettings>("Standard (12 kHz)", For(kind)),
            new KeyValuePair<string, EffectSettings>("Stark (6 kHz)", new EffectSettings { Frequency = 6000 })
        },
        NativeEffectKind.Compressor => new[]
        {
            new KeyValuePair<string, EffectSettings>("Sanft", new EffectSettings { ThresholdDb = -24, Ratio = 2, AttackMs = 20, ReleaseMs = 200 }),
            new KeyValuePair<string, EffectSettings>("Standard", For(kind)),
            new KeyValuePair<string, EffectSettings>("Stark", new EffectSettings { ThresholdDb = -12, Ratio = 8, AttackMs = 5, ReleaseMs = 80, MakeupDb = 3 })
        },
        NativeEffectKind.Limiter => new[]
        {
            new KeyValuePair<string, EffectSettings>("Sanft (-3 dB)", new EffectSettings { ThresholdDb = -3 }),
            new KeyValuePair<string, EffectSettings>("Standard (-1 dB)", For(kind)),
            new KeyValuePair<string, EffectSettings>("Hart (-0,3 dB)", new EffectSettings { ThresholdDb = -0.3 })
        },
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
}

/// <summary>Reine, testbare DSP-Funktionen für die nativen Effekte von 0.4.</summary>
public static class NativeEffects
{
    public static string DisplayName(NativeEffectKind kind) => kind switch
    {
        NativeEffectKind.Gain => "Verstärkung",
        NativeEffectKind.Equalizer => "Equalizer",
        NativeEffectKind.HighPass => "Hochpass",
        NativeEffectKind.LowPass => "Tiefpass",
        NativeEffectKind.Compressor => "Kompressor",
        NativeEffectKind.Limiter => "Limiter",
        _ => kind.ToString()
    };

    public static float[] Process(EffectSettings settings, float[] block, int channels, int sampleRate, NativeEffectKind kind)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(block);
        if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels));
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (block.Length % channels != 0) throw new ArgumentException("Die Sample-Anzahl ist kein Vielfaches der Kanalanzahl.", nameof(block));

        var result = (float[])block.Clone();
        switch (kind)
        {
            case NativeEffectKind.Gain:
                ApplyGain(result, settings.GainDb);
                break;
            case NativeEffectKind.Equalizer:
                ApplyBiquad(result, channels, sampleRate, Biquad.Peaking(settings.Frequency, settings.Q, settings.GainDb, sampleRate));
                break;
            case NativeEffectKind.HighPass:
                ApplyBiquad(result, channels, sampleRate, Biquad.HighPass(settings.Frequency, Math.Sqrt(0.5), sampleRate));
                break;
            case NativeEffectKind.LowPass:
                ApplyBiquad(result, channels, sampleRate, Biquad.LowPass(settings.Frequency, Math.Sqrt(0.5), sampleRate));
                break;
            case NativeEffectKind.Compressor:
                ApplyCompressor(result, channels, sampleRate, settings);
                break;
            case NativeEffectKind.Limiter:
                ApplyLimiter(result, channels, settings.ThresholdDb);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }
        return result;
    }

    public static void ApplyGain(float[] samples, double gainDb)
    {
        var factor = (float)Math.Pow(10, gainDb / 20d);
        for (var index = 0; index < samples.Length; index++) samples[index] *= factor;
    }

    public static void ApplyLimiter(float[] samples, int channels, double thresholdDb)
    {
        var threshold = (float)Math.Pow(10, thresholdDb / 20d);
        if (threshold <= 0) return;
        for (var frame = 0; frame < samples.Length / channels; frame++)
        {
            var peak = 0f;
            for (var channel = 0; channel < channels; channel++) peak = Math.Max(peak, Math.Abs(samples[frame * channels + channel]));
            if (peak <= threshold) continue;
            var factor = threshold / peak;
            for (var channel = 0; channel < channels; channel++) samples[frame * channels + channel] *= factor;
        }
    }

    private static void ApplyCompressor(float[] samples, int channels, int sampleRate, EffectSettings settings)
    {
        var threshold = Math.Pow(10, settings.ThresholdDb / 20d);
        var ratio = Math.Max(1d, settings.Ratio);
        var makeup = (float)Math.Pow(10, settings.MakeupDb / 20d);
        var attack = Coefficient(settings.AttackMs, sampleRate);
        var release = Coefficient(settings.ReleaseMs, sampleRate);
        var envelope = 0d;
        for (var frame = 0; frame < samples.Length / channels; frame++)
        {
            var peak = 0d;
            for (var channel = 0; channel < channels; channel++) peak = Math.Max(peak, Math.Abs(samples[frame * channels + channel]));
            var coefficient = peak > envelope ? attack : release;
            envelope += coefficient * (peak - envelope);
            var gain = 1d;
            if (envelope > threshold)
            {
                var reductionDb = (20 * Math.Log10(envelope / threshold)) * (1d / ratio - 1d);
                gain = Math.Pow(10, reductionDb / 20d);
            }
            gain *= makeup;
            for (var channel = 0; channel < channels; channel++) samples[frame * channels + channel] *= (float)gain;
        }
    }

    private static double Coefficient(double milliseconds, int sampleRate)
    {
        var seconds = Math.Max(0.1d, milliseconds) / 1000d;
        return 1d - Math.Exp(-1d / (seconds * sampleRate));
    }

    private static void ApplyBiquad(float[] samples, int channels, int sampleRate, Biquad filter)
    {
        var b0 = filter.B0 / filter.A0;
        var b1 = filter.B1 / filter.A0;
        var b2 = filter.B2 / filter.A0;
        var a1 = filter.A1 / filter.A0;
        var a2 = filter.A2 / filter.A0;
        var states = new BiquadState[channels];
        for (var channel = 0; channel < channels; channel++) states[channel] = new BiquadState();
        for (var frame = 0; frame < samples.Length / channels; frame++)
        {
            for (var channel = 0; channel < channels; channel++)
            {
                var index = frame * channels + channel;
                var state = states[channel];
                var input = samples[index];
                var output = b0 * input + b1 * state.X1 + b2 * state.X2 - a1 * state.Y1 - a2 * state.Y2;
                state.X2 = state.X1; state.X1 = input;
                state.Y2 = state.Y1; state.Y1 = output;
                samples[index] = (float)output;
            }
        }
    }

    private sealed class BiquadState
    {
        public double X1;
        public double X2;
        public double Y1;
        public double Y2;
    }

    internal readonly record struct Biquad(double B0, double B1, double B2, double A0, double A1, double A2)
    {
        public static Biquad LowPass(double frequency, double q, int sampleRate) => Standard(frequency, q, sampleRate, lowPass: true);

        public static Biquad HighPass(double frequency, double q, int sampleRate) => Standard(frequency, q, sampleRate, lowPass: false);

        private static Biquad Standard(double frequency, double q, int sampleRate, bool lowPass)
        {
            var clamped = Math.Clamp(frequency, 20d, Math.Min(20000d, sampleRate / 2d - 1d));
            var w0 = 2 * Math.PI * clamped / sampleRate;
            return Compute(w0, q, lowPass);
        }
        private static Biquad Compute(double w0, double q, bool lowPass)
        {
            var cosine = Math.Cos(w0);
            var alpha = Math.Sin(w0) / (2 * Math.Max(0.01d, q));
            if (lowPass)
            {
                var b0 = (1 - cosine) / 2;
                var b1 = 1 - cosine;
                var b2 = (1 - cosine) / 2;
                return new Biquad(b0, b1, b2, 1 + alpha, -2 * cosine, 1 - alpha);
            }
            var hpB0 = (1 + cosine) / 2;
            var hpB1 = -(1 + cosine);
            var hpB2 = (1 + cosine) / 2;
            return new Biquad(hpB0, hpB1, hpB2, 1 + alpha, -2 * cosine, 1 - alpha);
        }

        public static Biquad Peaking(double frequency, double q, double gainDb, int sampleRate)
        {
            var w0 = 2 * Math.PI * Math.Clamp(frequency, 20d, Math.Min(20000d, sampleRate / 2d - 1d)) / sampleRate;
            var cosine = Math.Cos(w0);
            var alpha = Math.Sin(w0) / (2 * Math.Max(0.01d, q));
            var a = Math.Pow(10, gainDb / 40d);
            return new Biquad(
                1 + alpha * a, -2 * cosine, 1 - alpha * a,
                1 + alpha / a, -2 * cosine, 1 - alpha / a);
        }
    }
}
