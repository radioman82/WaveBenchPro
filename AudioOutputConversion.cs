using System.Buffers.Binary;
using System.Runtime.InteropServices;
using NAudio.Wave;

namespace WellenEditor;

/// <summary>Explicit, bounded PCM conversion; never resamples or allocates in Read.</summary>
internal sealed class AudioOutputConversion : IWaveProvider
{
    private readonly IWaveProvider source;
    private readonly byte[] scratch;
    public WaveFormat WaveFormat { get; }
    internal AudioOutputConversion(IWaveProvider source, AudioSampleEncoding encoding)
    {
        if (source.WaveFormat.Encoding != WaveFormatEncoding.IeeeFloat || source.WaveFormat.BitsPerSample != 32)
            throw new NotSupportedException("Explizite Ausgabeumwandlung benötigt eine Float32-Quelle.");
        this.source = source;
        var bits = encoding switch { AudioSampleEncoding.Pcm16 => 16, AudioSampleEncoding.Pcm24 => 24, AudioSampleEncoding.Pcm32 => 32, _ => throw new ArgumentException("PCM-Format erforderlich.") };
        WaveFormat = new WaveFormat(source.WaveFormat.SampleRate, bits, source.WaveFormat.Channels);
        scratch = new byte[8192 * source.WaveFormat.BlockAlign];
    }
    public int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public int Read(Span<byte> buffer)
    {
        var written = 0;
        var bytesPerSample = WaveFormat.BitsPerSample / 8;
        while (buffer.Length - written >= WaveFormat.BlockAlign)
        {
            var frames = Math.Min(8192, (buffer.Length - written) / WaveFormat.BlockAlign);
            var read = source.Read(scratch.AsSpan(0, frames * source.WaveFormat.BlockAlign));
            var samples = MemoryMarshal.Cast<byte, float>(scratch.AsSpan(0, read));
            for (var i = 0; i < samples.Length; i++)
            {
                var value = float.IsFinite(samples[i]) ? Math.Clamp(samples[i], -1f, 1f) : 0f;
                var scale = bytesPerSample switch { 2 => 32768d, 3 => 8388608d, _ => 2147483648d };
                var pcm = (int)Math.Clamp(Math.Round(value * scale), -scale, scale - 1);
                var target = buffer.Slice(written + i * bytesPerSample, bytesPerSample);
                if (bytesPerSample == 2) BinaryPrimitives.WriteInt16LittleEndian(target, (short)pcm);
                else if (bytesPerSample == 4) BinaryPrimitives.WriteInt32LittleEndian(target, pcm);
                else { target[0] = (byte)pcm; target[1] = (byte)(pcm >> 8); target[2] = (byte)(pcm >> 16); }
            }
            written += samples.Length * bytesPerSample;
            if (read < frames * source.WaveFormat.BlockAlign) break;
        }
        return written;
    }
    internal static IWaveProvider Adapt(IWaveProvider source, AudioSampleEncoding encoding) =>
        encoding is AudioSampleEncoding.Source or AudioSampleEncoding.Float32 ? source : new AudioOutputConversion(source, encoding);
}
