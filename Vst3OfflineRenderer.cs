using System;
using System.Collections.Generic;
using System.Linq;
using NAudio.Vst3;
using NAudio.Wave;

namespace WellenEditor;

/// <summary>Rendert ein VST3-Plugin offline über einen Sample-Block (Datei oder Auswahl).</summary>
public static class Vst3OfflineRenderer
{
    private const int BlockSize = 4096;

    /// <summary>
    /// Liefert die bearbeiteten Samples in der Kanalanzahl der Quelle zurück.
    /// Der Host rechnet intern stereo; Mono-Spuren werden auf links Stereo hoch- und danach
    /// wieder auf Mono zurückgerechnet. Die Länge bleibt unverändert.
    /// </summary>
    public static float[] Render(Vst3EffectDescriptor descriptor, ISampleProvider source, int sourceChannels, int frames, byte[]? state = null)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(source);
        if (frames <= 0) return Array.Empty<float>();
        if (sourceChannels is < 1 or > 2) throw new ArgumentOutOfRangeException(nameof(sourceChannels), "Nur Mono und Stereo werden unterstützt.");

        var stereo = new float[frames * 2];
        var input = new float[BlockSize * sourceChannels];
        var writtenFrames = 0;
        while (writtenFrames < frames)
        {
            var wantedFrames = Math.Min(frames - writtenFrames, BlockSize);
            var read = source.Read(input.AsSpan(0, wantedFrames * sourceChannels));
            if (read <= 0) break;
            var readFrames = read / sourceChannels;
            for (var frame = 0; frame < readFrames; frame++)
            {
                var target = (writtenFrames + frame) * 2;
                if (sourceChannels == 1)
                {
                    stereo[target] = input[frame];
                    stereo[target + 1] = input[frame];
                }
                else
                {
                    stereo[target] = input[frame * 2];
                    stereo[target + 1] = input[frame * 2 + 1];
                }
            }
            writtenFrames += readFrames;
        }

        using var module = Vst3Module.Load(descriptor.Path);
        using var plugin = module.CreatePlugin(descriptor.ClassInfo, source.WaveFormat.SampleRate, BlockSize);
        if (state != null && state.Length > 0)
            plugin.LoadState(state);
        var effect = new Vst3EffectSampleProvider(
            new ArraySampleProvider(stereo, WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 2)),
            plugin)
        {
            RenderTail = true,
            MaxTailDuration = TimeSpan.FromSeconds(2),
            CompensateLatency = true
        };

        var rendered = new List<float>((frames + source.WaveFormat.SampleRate) * 2);
        var output = new float[BlockSize * 2];
        int count;
        while ((count = effect.Read(output.AsSpan())) > 0)
        {
            rendered.AddRange(output.AsSpan(0, count).ToArray());
        }

        var result = new float[frames * sourceChannels];
        for (var frame = 0; frame < frames; frame++)
        {
            for (var channel = 0; channel < sourceChannels; channel++)
            {
                var sourceChannel = sourceChannels == 1 ? 0 : channel;
                var index = frame * 2 + sourceChannel;
                result[frame * sourceChannels + channel] = index < rendered.Count ? rendered[index] : stereo[index];
            }
        }
        return result;
    }
}
