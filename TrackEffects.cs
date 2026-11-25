using System.IO;
using System.Threading;

namespace WellenEditor;

internal sealed record TrackEffectSlot(Guid Id, NativeEffectKind Kind, EffectSettings Settings, bool Enabled = true, Vst3EffectDescriptor? Vst = null, byte[]? VstState = null)
{
    public static TrackEffectSlot ForVst(Vst3EffectDescriptor descriptor, byte[]? state = null) =>
        new(Guid.NewGuid(), NativeEffectKind.Gain, new EffectSettings(), true, descriptor with { }, state);
}

/// <summary>Stateful stereo insert chain. Constructed outside Read; no allocations in Process.</summary>
internal sealed class TrackEffectProcessor
{
    private readonly Stage[] stages;
    public TrackEffectProcessor(IEnumerable<TrackEffectSlot> effects, int rate) =>
        stages = effects.Where(fx => fx.Enabled && fx.Vst is null).Select(fx => new Stage(fx, rate)).ToArray();
    public void Process(Span<float> samples)
    {
        foreach (var stage in stages) stage.Process(samples);
    }
    private sealed class Stage
    {
        private readonly NativeEffectKind kind;
        private readonly double gain, threshold, ratio, attack, release;
        private readonly double b0, b1, b2, a1, a2;
        private readonly double[] x1 = new double[2], x2 = new double[2], y1 = new double[2], y2 = new double[2];
        private double envelope;
        public Stage(TrackEffectSlot fx, int rate)
        {
            kind = fx.Kind; var s = fx.Settings;
            NativeEffects.ValidateSettings(s, kind, rate);
            gain = Math.Pow(10, (kind == NativeEffectKind.Compressor ? s.MakeupDb : s.GainDb) / 20);
            threshold = Math.Pow(10, s.ThresholdDb / 20); ratio = Math.Max(1, s.Ratio);
            attack = 1 - Math.Exp(-1 / (Math.Max(.1, s.AttackMs) / 1000 * rate));
            release = 1 - Math.Exp(-1 / (Math.Max(.1, s.ReleaseMs) / 1000 * rate));
            var f = kind switch
            {
                NativeEffectKind.Equalizer => NativeEffects.Biquad.Peaking(s.Frequency, s.Q, s.GainDb, rate),
                NativeEffectKind.HighPass => NativeEffects.Biquad.HighPass(s.Frequency, Math.Sqrt(.5), rate),
                NativeEffectKind.LowPass => NativeEffects.Biquad.LowPass(s.Frequency, Math.Sqrt(.5), rate),
                _ => new NativeEffects.Biquad(1, 0, 0, 1, 0, 0)
            };
            b0 = f.B0 / f.A0; b1 = f.B1 / f.A0; b2 = f.B2 / f.A0; a1 = f.A1 / f.A0; a2 = f.A2 / f.A0;
        }
        public void Process(Span<float> samples)
        {
            for (var i = 0; i < samples.Length; i += 2)
            {
                var factor = gain;
                if (kind == NativeEffectKind.Compressor)
                {
                    var peak = Math.Max(Math.Abs(samples[i]), Math.Abs(samples[i + 1]));
                    envelope += (peak > envelope ? attack : release) * (peak - envelope);
                    if (envelope > threshold) factor *= Math.Pow(envelope / threshold, 1 / ratio - 1);
                }
                if (kind == NativeEffectKind.Limiter)
                {
                    var peak = Math.Max(Math.Abs(samples[i]), Math.Abs(samples[i + 1]));
                    factor = peak > threshold ? threshold / peak : 1;
                }
                for (var c = 0; c < 2; c++)
                {
                    var input = samples[i + c];
                    if (kind is NativeEffectKind.Equalizer or NativeEffectKind.HighPass or NativeEffectKind.LowPass)
                    {
                        var output = b0 * input + b1 * x1[c] + b2 * x2[c] - a1 * y1[c] - a2 * y2[c];
                        x2[c] = x1[c]; x1[c] = input; y2[c] = y1[c]; y1[c] = output;
                        samples[i + c] = (float)output;
                    }
                    else samples[i + c] = (float)(input * factor);
                }
            }
        }
    }
}

/// <summary>Offline track-wide FX chain used when a track contains VST3 inserts.</summary>
internal static class TrackEffectChainRenderer
{
    internal delegate float[] VstRenderer(TrackEffectSlot slot, float[] input, int sampleRate);

    public static float[] Process(
        float[] input,
        IReadOnlyList<TrackEffectSlot> effects,
        int sampleRate,
        Action<string> reportWarning,
        VstRenderer? vstRenderer = null,
        bool failOnVstError = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(effects);
        ArgumentNullException.ThrowIfNull(reportWarning);
        if (input.Length % 2 != 0) throw new ArgumentException("Das Spur-Audio muss stereo sein.", nameof(input));

        var result = input;
        foreach (var effect in effects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!effect.Enabled) continue;
            if (effect.Vst is not null)
            {
                try
                {
                    var processed = vstRenderer is null
                        ? RenderVst(effect, result, sampleRate, cancellationToken)
                        : vstRenderer(effect, result, sampleRate);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (processed.Length != result.Length)
                    {
                        if (failOnVstError)
                            throw new InvalidDataException($"VST3-Effekt '{effect.Vst.DisplayName}' lieferte eine unerwartete Ausgabelänge.");
                        reportWarning($"VST3-Effekt '{effect.Vst.DisplayName}' lieferte eine unerwartete Ausgabelänge; Effekt wurde übersprungen.");
                        continue;
                    }
                    result = processed;
                }
                catch (Exception exception)
                {
                    if (failOnVstError) throw new InvalidOperationException($"VST3-Effekt '{effect.Vst.DisplayName}' konnte nicht angewendet werden.", exception);
                    reportWarning($"VST3-Effekt '{effect.Vst.DisplayName}' konnte nicht angewendet werden ({exception.Message}); Effekt wurde übersprungen.");
                }
                continue;
            }

            var native = new TrackEffectProcessor(new[] { effect }, sampleRate);
            native.Process(result);
        }
        return result;
    }

    private static float[] RenderVst(TrackEffectSlot effect, float[] input, int sampleRate, CancellationToken cancellationToken)
    {
        var provider = new ArraySampleProvider(input, NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2));
        return Vst3OfflineRenderer.Render(effect.Vst!, provider, sampleRate, 2, input.Length / 2, effect.VstState, cancellationToken);
    }
}
