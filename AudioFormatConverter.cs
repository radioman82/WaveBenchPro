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

        var encoding = format.Encoding;
        if (encoding == WaveFormatEncoding.Extensible)
        {
            if (format is not WaveFormatExtensible extensible)
                throw new NotSupportedException("WAVEFORMATEXTENSIBLE ohne lesbare SubFormat-GUID.");
            encoding = extensible.SubFormat switch
            {
                var subFormat when subFormat == new Guid("00000001-0000-0010-8000-00aa00389b71") => WaveFormatEncoding.Pcm,
                var subFormat when subFormat == new Guid("00000003-0000-0010-8000-00aa00389b71") => WaveFormatEncoding.IeeeFloat,
                _ => throw new NotSupportedException($"WAVEFORMATEXTENSIBLE-SubFormat {extensible.SubFormat} wird nicht unterstützt.")
            };
        }

        var bytesPerSample = format.BitsPerSample / 8;
        if (bytesPerSample <= 0) throw new InvalidDataException("Ungültiges Audio-Sampleformat.");
        var sampleCount = count / bytesPerSample;
        var result = new float[sampleCount];

        for (var index = 0; index < sampleCount; index++)
        {
            var position = index * bytesPerSample;
            result[index] = (encoding, bytesPerSample) switch
            {
                (WaveFormatEncoding.IeeeFloat, 4) => BitConverter.ToSingle(buffer, position),
                (WaveFormatEncoding.Pcm, 1) => (buffer[position] - 128) / 128f,
                (WaveFormatEncoding.Pcm, 2) => BitConverter.ToInt16(buffer, position) / 32768f,
                (WaveFormatEncoding.Pcm, 3) => ReadPcm24(buffer, position) / 8388608f,
                (WaveFormatEncoding.Pcm, 4) => BitConverter.ToInt32(buffer, position) / 2147483648f,
                _ => throw new NotSupportedException(
                    $"Audioformat {encoding} mit {format.BitsPerSample} Bit wird nicht unterstützt.")
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
        var resampleSource = targetRate < sourceRate
            ? LowPassBeforeDownsampling(samples, channels, (double)targetRate / sourceRate)
            : samples;
        var targetFrames = checked((int)Math.Round((double)sourceFrames * targetRate / sourceRate));
        var result = new float[checked(targetFrames * channels)];
        if (targetFrames == 0) return result;

        for (var frame = 0; frame < targetFrames; frame++)
        {
            var sourcePosition = (double)frame * sourceRate / targetRate;
            var lower = (int)Math.Floor(sourcePosition);
            var fraction = (float)(sourcePosition - lower);
            var upper = Math.Min(lower + 1, sourceFrames - 1);
            for (var channel = 0; channel < channels; channel++)
            {
                var first = resampleSource[lower * channels + channel];
                var second = resampleSource[upper * channels + channel];
                result[frame * channels + channel] = first + (second - first) * fraction;
            }
        }
        return result;
    }

    private static float[] LowPassBeforeDownsampling(float[] samples, int channels, double rateRatio)
    {
        var frames = samples.Length / channels;
        var cutoff = 0.94 * rateRatio;
        var halfWidth = Math.Max(16, (int)Math.Ceiling(16 / cutoff));
        var filtered = new float[samples.Length];
        for (var frame = 0; frame < frames; frame++)
        {
            var weightSum = 0d;
            var first = Math.Max(-halfWidth, -frame);
            var last = Math.Min(halfWidth, frames - 1 - frame);
            for (var distance = first; distance <= last; distance++)
            {
                var x = cutoff * distance;
                var sinc = Math.Abs(x) < 1e-12 ? 1d : Math.Sin(Math.PI * x) / (Math.PI * x);
                var windowPosition = distance / (double)halfWidth;
                var window = 0.42 + 0.5 * Math.Cos(Math.PI * windowPosition) + 0.08 * Math.Cos(2 * Math.PI * windowPosition);
                var weight = cutoff * sinc * window;
                weightSum += weight;
                var sourceFrame = frame + distance;
                for (var channel = 0; channel < channels; channel++)
                    filtered[frame * channels + channel] += (float)(samples[sourceFrame * channels + channel] * weight);
            }
            if (Math.Abs(weightSum) > 1e-12)
                for (var channel = 0; channel < channels; channel++)
                    filtered[frame * channels + channel] /= (float)weightSum;
        }
        return filtered;
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
