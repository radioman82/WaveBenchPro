using System;
using NAudio.Wave;

namespace WellenEditor;

/// <summary>
/// Endlosschleife über einen fertigen Sample-Puffer – Quelle für die Live-VST-Vorschau.
/// Liefert bei jedem Read genau <paramref name="count"/> Samples (zyklisch).
/// </summary>
public sealed class LoopingSampleProvider : ISampleProvider
{
    private readonly float[] data;
    private int position;

    public LoopingSampleProvider(float[] data, int channels, int sampleRate)
    {
        this.data = data ?? throw new ArgumentNullException(nameof(data));
        if (this.data.Length == 0) throw new ArgumentException("Puffer darf nicht leer sein.", nameof(data));
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate > 0 ? sampleRate : 44100, channels);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        var total = 0;
        while (total < count)
        {
            var n = Math.Min(count - total, data.Length - position);
            Array.Copy(data, position, buffer, offset + total, n);
            position += n;
            total += n;
            if (position >= data.Length) position = 0;
        }
        return count;
    }

    public int Read(Span<float> buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var n = Math.Min(buffer.Length - total, data.Length - position);
            data.AsSpan(position, n).CopyTo(buffer.Slice(total, n));
            position += n;
            total += n;
            if (position >= data.Length) position = 0;
        }
        return buffer.Length;
    }
}
