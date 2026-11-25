using NAudio.Wave;
using System.Runtime.InteropServices;

namespace WellenEditor;

internal interface IBlockAudioRenderer : IDisposable
{
    int SampleRate { get; }
    int BlockFrames { get; }
    int Read(float[] output, int frames);
}

/// <summary>Experimental SPSC playback queue; the callback reads prepared blocks only.</summary>
internal sealed class BufferedBlockWaveProvider : IWaveProvider, IDisposable
{
    private readonly IBlockAudioRenderer renderer;
    private readonly float[][] blocks;
    private readonly int[] sizes;
    private readonly CancellationTokenSource cancellation = new();
    private readonly Task worker;
    private long produced, consumed, underruns, audioFrames;
    private int blockOffset;
    private int complete;
    private int disposed;
    private Exception? workerError;

    public WaveFormat WaveFormat { get; }
    public long UnderrunCount => Interlocked.Read(ref underruns);
    public long AudioFramesRead => Interlocked.Read(ref audioFrames);
    public Exception? WorkerError => Volatile.Read(ref workerError);
    internal Task Completion => worker;

    public BufferedBlockWaveProvider(IBlockAudioRenderer renderer, int capacity = 8)
    {
        this.renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        if (capacity is < 2 or > 256) throw new ArgumentOutOfRangeException(nameof(capacity));
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(renderer.SampleRate, 2);
        blocks = Enumerable.Range(0, capacity).Select(_ => new float[checked(renderer.BlockFrames * 2)]).ToArray();
        sizes = new int[capacity];
        worker = Task.Run(Produce);
    }

    private void Produce()
    {
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                var write = Volatile.Read(ref produced);
                if (write - Volatile.Read(ref consumed) >= blocks.Length) { Thread.Sleep(1); continue; }
                var slot = (int)(write % blocks.Length);
                var count = renderer.Read(blocks[slot], renderer.BlockFrames);
                if (count == 0) break;
                if (count < 0 || count > renderer.BlockFrames) throw new InvalidOperationException("Renderer lieferte eine ungültige Blocklänge.");
                sizes[slot] = count * 2;
                Volatile.Write(ref produced, write + 1);
            }
        }
        catch (Exception exception) { Volatile.Write(ref workerError, exception); }
        finally
        {
            try { renderer.Dispose(); } catch (Exception exception) { Volatile.Write(ref workerError, exception); }
            Volatile.Write(ref complete, 1);
        }
    }

    public int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public int Read(Span<byte> buffer)
    {
        var output = MemoryMarshal.Cast<byte, float>(buffer[..(buffer.Length / 8 * 8)]);
        var written = 0;
        while (written < output.Length)
        {
            var read = Volatile.Read(ref consumed);
            if (read == Volatile.Read(ref produced))
            {
                if (Volatile.Read(ref complete) != 0)
                {
                    if (read == Volatile.Read(ref produced)) break;
                    continue;
                }
                output[written..].Clear();
                Interlocked.Increment(ref underruns);
                return output.Length * sizeof(float);
            }
            var slot = (int)(read % blocks.Length);
            var samples = Math.Min(output.Length - written, sizes[slot] - blockOffset);
            blocks[slot].AsSpan(blockOffset, samples).CopyTo(output[written..]);
            written += samples; blockOffset += samples;
            Interlocked.Add(ref audioFrames, samples / 2);
            if (blockOffset == sizes[slot]) { blockOffset = 0; Volatile.Write(ref consumed, read + 1); }
        }
        return written * sizeof(float);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0) cancellation.Cancel();
    }
}
