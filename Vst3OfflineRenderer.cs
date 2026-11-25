using System;
using NAudio.Vst3;
using NAudio.Wave;
using System.IO;
using System.Threading;

namespace WellenEditor;

/// <summary>Rendert ein VST3-Plugin offline über einen Sample-Block (Datei oder Auswahl).</summary>
public static class Vst3OfflineRenderer
{
    private const int BlockSize = 4096;
    private const long MaxVstRenderWorkingBytes = 256L * 1024 * 1024;

    /// <summary>
    /// Liefert die bearbeiteten Samples in der Kanalanzahl der Quelle zurück.
    /// Der Host rechnet intern stereo; Mono-Spuren werden auf Stereo hoch- und danach
    /// mit einem Mittelwert beider Plugin-Ausgänge wieder auf Mono zurückgerechnet.
    /// </summary>
    public static float[] Render(Vst3EffectDescriptor descriptor, ISampleProvider source, int sampleRate, int sourceChannels, int frames, byte[]? state = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(source);
        if (frames <= 0) return Array.Empty<float>();
        if (sourceChannels is < 1 or > 2) throw new ArgumentOutOfRangeException(nameof(sourceChannels), "Nur Mono und Stereo werden unterstützt.");
        var rate = sampleRate > 0 ? sampleRate : source.WaveFormat.SampleRate;
        if (source.WaveFormat.Channels != sourceChannels || source.WaveFormat.SampleRate != rate)
            throw new ArgumentException("Quellformat, Kanalzahl und VST-Hostformat müssen übereinstimmen.", nameof(source));
        var stereoSampleCount = checked(frames * 2);
        var resultSampleCount = checked(frames * sourceChannels);
        var workingBytes = checked((long)frames * sizeof(float) * (sourceChannels == 2 ? 4 : 5));
        if (workingBytes > MaxVstRenderWorkingBytes)
            throw new NotSupportedException("Der VST3-Render überschreitet das Speicherlimit von 256 MiB.");

        var stereo = ReadStereoInput(source, rate, sourceChannels, frames, cancellationToken);

        using var module = Vst3Module.Load(descriptor.Path);
        var classInfo = Vst3EffectCatalog.ResolveClassInfo(module, descriptor.ClassInfo.ClassId);
        using var plugin = module.CreatePlugin(classInfo, rate, BlockSize);
        if (state != null && state.Length > 0)
            plugin.LoadState(state);
        var effect = new Vst3EffectSampleProvider(
            new ArraySampleProvider(stereo, WaveFormat.CreateIeeeFloatWaveFormat(rate, 2)),
            plugin)
        {
            RenderTail = true,
            MaxTailDuration = TimeSpan.FromSeconds(2),
            CompensateLatency = true
        };

        var resultStereo = sourceChannels == 2 ? new float[resultSampleCount] : new float[stereoSampleCount];
        var output = new float[BlockSize * 2];
        var renderedSamples = 0;
        while (renderedSamples < stereoSampleCount)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = effect.Read(output.AsSpan(0, Math.Min(output.Length, stereoSampleCount - renderedSamples)));
            if (count <= 0) break;
            if (count > Math.Min(output.Length, stereoSampleCount - renderedSamples))
                throw new InvalidDataException("Das VST3-Plugin meldete mehr Samples als angefordert.");
            if (count % 2 != 0) throw new InvalidDataException("Das VST3-Plugin lieferte einen unvollständigen Stereo-Frame.");
            output.AsSpan(0, count).CopyTo(resultStereo.AsSpan(renderedSamples));
            renderedSamples += count;
        }
        if (renderedSamples != stereoSampleCount)
            throw new EndOfStreamException($"Das VST3-Plugin lieferte nur {renderedSamples / 2} von {frames} benötigten Frames.");

        if (sourceChannels == 2) return resultStereo;
        var result = new float[resultSampleCount];
        for (var frame = 0; frame < frames; frame++)
        {
            for (var channel = 0; channel < sourceChannels; channel++)
            {
                var leftIndex = frame * 2;
                var rightIndex = leftIndex + 1;
                var left = resultStereo[leftIndex];
                var right = resultStereo[rightIndex];
                result[frame * sourceChannels + channel] = sourceChannels == 1
                    ? (left + right) * 0.5f
                    : channel == 0 ? left : right;
            }
        }
        return result;
    }

    internal static float[] ReadStereoInput(ISampleProvider source, int rate, int sourceChannels, int frames, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (sourceChannels is < 1 or > 2) throw new ArgumentOutOfRangeException(nameof(sourceChannels));
        if (frames <= 0) return Array.Empty<float>();
        var stereoSampleCount = checked(frames * 2);
        if (source.WaveFormat.Channels != sourceChannels || source.WaveFormat.SampleRate != rate)
            throw new ArgumentException("Quellformat, Kanalzahl und VST-Hostformat müssen übereinstimmen.", nameof(source));

        var stereo = new float[stereoSampleCount];
        var input = new float[BlockSize * sourceChannels];
        var writtenFrames = 0;
        while (writtenFrames < frames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var wantedSamples = Math.Min((frames - writtenFrames) * sourceChannels, input.Length);
            var filled = 0;
            while (filled < wantedSamples)
            {
                var read = source.Read(input.AsSpan(filled, wantedSamples - filled));
                if (read <= 0) break;
                if (read > wantedSamples - filled) throw new InvalidDataException("Der SampleProvider meldete mehr Samples als angefordert.");
                filled += read;
            }
            if (filled != wantedSamples)
                throw new EndOfStreamException(filled % sourceChannels != 0
                    ? "Der SampleProvider endete mitten in einem Audioframe."
                    : $"Der SampleProvider lieferte nur {writtenFrames + filled / sourceChannels} von {frames} angeforderten Frames.");
            var readFrames = filled / sourceChannels;
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

        return stereo;
    }
}
