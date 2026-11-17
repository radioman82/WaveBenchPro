using NAudio.Wave;
using System.IO;

namespace WellenEditor;

/// <summary>
/// Dezodiert rohe Capture-Blöcke und wandelt Audioformate kontrolliert um.
/// Der Konverter wird von Aufnahme und Zwischenablage gemeinsam genutzt.
/// </summary>
public static class AudioFormatConverter
{
    public static float[] Decode(byte[] buffer, int count, WaveFormat format)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (count < 0 || count > buffer.Length) throw new ArgumentOutOfRangeException(nameof(count));
        ArgumentNullException.ThrowIfNull(format);

        var bytesPerSample = format.BitsPerSample / 8;
        if (bytesPerSample <= 0) throw new InvalidDataException("Ungültiges Audio-Sampleformat.");
        var sampleCount = count / bytesPerSample;
        var result = new float[sampleCount];

        for (var index = 0; index < sampleCount; index++)
        {
            var position = index * bytesPerSample;
            result[index] = (format.Encoding, bytesPerSample) switch
            {
                (WaveFormatEncoding.IeeeFloat, 4) => BitConverter.ToSingle(buffer, position),
                (WaveFormatEncoding.Pcm, 1) => (buffer[position] - 128) / 128f,
                (WaveFormatEncoding.Pcm, 2) => BitConverter.ToInt16(buffer, position) / 32768f,
                (WaveFormatEncoding.Pcm, 3) => ReadPcm24(buffer, position) / 8388608f,
                (WaveFormatEncoding.Pcm, 4) => BitConverter.ToInt32(buffer, position) / 2147483648f,
                _ => throw new NotSupportedException(
                    $"Audioformat {format.Encoding} mit {format.BitsPerSample} Bit wird nicht unterstützt.")
            };
        }
        return result;
    }

    /// <summary>Lineares Resampling pro Kanal; Kanäle bleiben dabei getrennt.</summary>
    public static float[] Resample(float[] samples, int channels, int sourceRate, int targetRate)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels));
        if (sourceRate <= 0) throw new ArgumentOutOfRangeException(nameof(sourceRate));
        if (targetRate <= 0) throw new ArgumentOutOfRangeException(nameof(targetRate));
        if (samples.Length % channels != 0) throw new ArgumentException("Sample-Anzahl passt nicht zur Kanalanzahl.", nameof(samples));
        if (sourceRate == targetRate) return samples;

        var sourceFrames = samples.Length / channels;
        var targetFrames = (int)Math.Round(sourceFrames * targetRate / (double)sourceRate);
        var result = new float[targetFrames * channels];
        if (targetFrames == 0) return result;

        for (var frame = 0; frame < targetFrames; frame++)
        {
            var sourcePosition = frame * sourceRate / (double)targetRate;
            var lower = (int)Math.Floor(sourcePosition);
            var fraction = (float)(sourcePosition - lower);
            var upper = Math.Min(lower + 1, sourceFrames - 1);
            for (var channel = 0; channel < channels; channel++)
            {
                var first = samples[lower * channels + channel];
                var second = samples[upper * channels + channel];
                result[frame * channels + channel] = first + (second - first) * fraction;
            }
        }
        return result;
    }

    /// <summary>Mischt Kanäle nach unten oder dupliziert sie nach oben.</summary>
    public static float[] ConvertChannels(float[] samples, int sourceChannels, int targetChannels)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (sourceChannels <= 0) throw new ArgumentOutOfRangeException(nameof(sourceChannels));
        if (targetChannels <= 0) throw new ArgumentOutOfRangeException(nameof(targetChannels));
        if (samples.Length % sourceChannels != 0) throw new ArgumentException("Sample-Anzahl passt nicht zur Kanalanzahl.", nameof(samples));
        if (sourceChannels == targetChannels) return samples;

        var frames = samples.Length / sourceChannels;
        var result = new float[frames * targetChannels];
        for (var frame = 0; frame < frames; frame++)
        {
            for (var channel = 0; channel < targetChannels; channel++)
            {
                var first = channel * sourceChannels / targetChannels;
                var last = Math.Max(first + 1, (channel + 1) * sourceChannels / targetChannels);
                var sum = 0f;
                for (var source = first; source < last; source++) sum += samples[frame * sourceChannels + source];
                result[frame * targetChannels + channel] = sum / (last - first);
            }
        }
        return result;
    }

    public static AudioSelectionData Convert(AudioSelectionData source, int channels, int sampleRate)
    {
        ArgumentNullException.ThrowIfNull(source.Samples);
        if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels));
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (source.Channels == channels && source.SampleRate == sampleRate) return source;

        var resampled = Resample(source.Samples, source.Channels, source.SampleRate, sampleRate);
        return new AudioSelectionData(ConvertChannels(resampled, source.Channels, channels), channels, sampleRate);
    }

    private static int ReadPcm24(byte[] buffer, int position)
    {
        var value = buffer[position] | (buffer[position + 1] << 8) | (buffer[position + 2] << 16);
        return value >= 0x800000 ? value - 0x1000000 : value;
    }
}
