using NAudio.Wave;
using System.Runtime.InteropServices;

namespace WellenEditor;

/// <summary>
/// Adaptiert eine blockweise <see cref="IAudioSource"/> an NAudio, ohne das
/// Quelldokument vollständig in den Speicher zu laden. Der interne Float-Puffer
/// entsteht einmal beim Erzeugen des Providers und wird im Audio-Thread wiederverwendet.
/// </summary>
internal interface IRewindableWaveProvider : IWaveProvider
{
    void Rewind();
}

internal sealed class StreamingWaveProvider : IRewindableWaveProvider, IDisposable
{
    private readonly IAudioSource source;
    private readonly long startFrame;
    private readonly long endFrame;
    private readonly float[] sampleBuffer;
    private readonly float[] peaks;
    private readonly float[] rms;
    private bool clipped;
    private long positionFrame;
    private bool disposed;

    public StreamingWaveProvider(IAudioSource source, long startFrame, long frameCount, int bufferFrames = 4096)
    {
        this.source = source ?? throw new ArgumentNullException(nameof(source));
        if (startFrame < 0) throw new ArgumentOutOfRangeException(nameof(startFrame));
        if (frameCount < 0) throw new ArgumentOutOfRangeException(nameof(frameCount));
        if (bufferFrames < 1) throw new ArgumentOutOfRangeException(nameof(bufferFrames));

        this.startFrame = Math.Min(startFrame, source.FrameCount);
        positionFrame = this.startFrame;
        endFrame = Math.Min(source.FrameCount, positionFrame + frameCount);
        sampleBuffer = new float[checked(bufferFrames * source.Channels)];
        peaks = new float[source.Channels];
        rms = new float[source.Channels];
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.SampleRate, source.Channels);
    }

    public WaveFormat WaveFormat { get; }
    public IReadOnlyList<float> Peaks => peaks;
    public IReadOnlyList<float> Rms => rms;
    public bool Clipped => clipped;

    public int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        return Read(buffer.AsSpan(offset, count));
    }

    public int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var bytesPerFrame = WaveFormat.BlockAlign;
        var requestedFrames = Math.Min(buffer.Length / bytesPerFrame, sampleBuffer.Length / WaveFormat.Channels);
        if (requestedFrames <= 0 || positionFrame >= endFrame) return 0;

        requestedFrames = (int)Math.Min(requestedFrames, endFrame - positionFrame);
        var framesRead = source.ReadFrames(positionFrame, sampleBuffer, 0, requestedFrames);
        if (framesRead <= 0) return 0;

        Array.Clear(peaks);
        Array.Clear(rms);
        clipped = false;
        for (var sample = 0; sample < framesRead * WaveFormat.Channels; sample++)
        {
            var channel = sample % WaveFormat.Channels;
            var value = sampleBuffer[sample];
            peaks[channel] = Math.Max(peaks[channel], Math.Abs(value));
            rms[channel] += value * value;
            clipped |= Math.Abs(value) >= 1f;
        }
        for (var channel = 0; channel < rms.Length; channel++) rms[channel] = MathF.Sqrt(rms[channel] / framesRead);

        var bytesRead = framesRead * bytesPerFrame;
        MemoryMarshal.AsBytes(sampleBuffer.AsSpan(0, framesRead * WaveFormat.Channels)).CopyTo(buffer);
        positionFrame += framesRead;
        return bytesRead;
    }

    public void Rewind()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        positionFrame = startFrame;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        source.Dispose();
    }
}

/// <summary>Wiederholt einen seekbaren Provider ohne Spieler-Neustart und ohne Lücke am Schleifenende.</summary>
internal sealed class LoopingWaveProvider : IWaveProvider, IDisposable
{
    private readonly IRewindableWaveProvider source;

    public LoopingWaveProvider(IRewindableWaveProvider source)
    {
        this.source = source ?? throw new ArgumentNullException(nameof(source));
        WaveFormat = source.WaveFormat;
    }

    public WaveFormat WaveFormat { get; }

    public int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        return Read(buffer.AsSpan(offset, count));
    }

    public int Read(Span<byte> buffer)
    {
        var written = 0;
        while (written < buffer.Length)
        {
            var read = source.Read(buffer.Slice(written));
            if (read > 0)
            {
                written += read;
                continue;
            }

            source.Rewind();
            read = source.Read(buffer.Slice(written));
            if (read <= 0) return written;
            written += read;
        }
        return written;
    }

    public void Dispose()
    {
        if (source is IDisposable disposable) disposable.Dispose();
    }
}

/// <summary>
/// Misst die Blöcke genau an der Stelle, an der WASAPI sie anfordert. Damit ist
/// das Meter nicht von einer UI-Zeitberechnung oder der Fenstergröße abhängig.
/// </summary>
internal sealed class MeteringWaveProvider : IWaveProvider, IDisposable
{
    private readonly IWaveProvider source;
    private readonly float[] peaks;
    private readonly float[] rms;
    private int clipped;

    public MeteringWaveProvider(IWaveProvider source)
    {
        this.source = source ?? throw new ArgumentNullException(nameof(source));
        WaveFormat = source.WaveFormat;
        peaks = new float[WaveFormat.Channels];
        rms = new float[WaveFormat.Channels];
    }

    public WaveFormat WaveFormat { get; }
    public IReadOnlyList<float> Peaks => peaks;
    public IReadOnlyList<float> Rms => rms;
    public bool Clipped => Volatile.Read(ref clipped) != 0;

    public int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        return Read(buffer.AsSpan(offset, count));
    }

    public int Read(Span<byte> buffer)
    {
        var bytesRead = source.Read(buffer);
        if (bytesRead <= 0) return bytesRead;

        Array.Clear(peaks);
        Array.Clear(rms);
        Volatile.Write(ref clipped, 0);
        var samples = MemoryMarshal.Cast<byte, float>(buffer.Slice(0, bytesRead));
        var frames = samples.Length / WaveFormat.Channels;
        for (var index = 0; index < samples.Length; index++)
        {
            var channel = index % WaveFormat.Channels;
            var value = samples[index];
            peaks[channel] = Math.Max(peaks[channel], Math.Abs(value));
            rms[channel] += value * value;
            if (Math.Abs(value) >= 1f) Volatile.Write(ref clipped, 1);
        }
        for (var channel = 0; channel < rms.Length; channel++) rms[channel] = MathF.Sqrt(rms[channel] / Math.Max(1, frames));
        return bytesRead;
    }

    public void Dispose()
    {
        if (source is IDisposable disposable) disposable.Dispose();
    }
}
