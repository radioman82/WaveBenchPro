using NAudio.Wave;
using System.Runtime.InteropServices;

namespace WellenEditor;

internal interface IBlockAudioRenderer : IDisposable
{
    int SampleRate { get; }
    int BlockFrames { get; }
    int Read(float[] output, int frames);
}

internal interface ICancellableBlockAudioRenderer : IBlockAudioRenderer
{
    int Read(float[] output, int frames, CancellationToken cancellationToken);
}

/// <summary>Experimental SPSC playback queue; the callback reads prepared blocks only.</summary>
internal sealed class BufferedBlockWaveProvider : IWaveProvider, IDisposable
{
    private readonly IBlockAudioRenderer renderer;
    private readonly float[][] blocks;
    private readonly int[] sizes;
    private readonly CancellationTokenSource cancellation = new();
    private readonly Task worker;
    private readonly object lifetimeGate = new();
    private long produced, consumed, underruns, audioFrames, deviceFrames;
    private int blockOffset;
    private int complete;
    private int disposed;
    private bool workerFinished;
    private Exception? workerError;

    public WaveFormat WaveFormat { get; }
    public long UnderrunCount => Interlocked.Read(ref underruns);
    public long AudioFramesRead => Interlocked.Read(ref audioFrames);
    public long DeviceFramesRead => Interlocked.Read(ref deviceFrames);
    // Underrun pauses the source timeline; the device still consumes silent frames.
    public long TimelineFramesRead => AudioFramesRead;
    public Exception? WorkerError => Volatile.Read(ref workerError);
    internal Task Completion => worker;
    internal int Capacity => blocks.Length;

    /// <summary>Prepares blocks outside the audio callback; false indicates timeout.</summary>
    internal async Task<bool> PrefillAsync(int minimumBlocks, TimeSpan timeout, CancellationToken token = default)
    {
        if (minimumBlocks < 1 || minimumBlocks > blocks.Length) throw new ArgumentOutOfRangeException(nameof(minimumBlocks));
        if (timeout < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (Volatile.Read(ref complete) != 0)
            {
                if (WorkerError is { } error) throw new InvalidOperationException("Die Audiovorbereitung ist fehlgeschlagen.", error);
                return Volatile.Read(ref produced) > Volatile.Read(ref consumed);
            }
            if (Volatile.Read(ref produced) - Volatile.Read(ref consumed) >= minimumBlocks) return true;
            if (stopwatch.Elapsed >= timeout) return false;
            await Task.Delay(5, token).ConfigureAwait(false);
        }
    }

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
                var count = renderer is ICancellableBlockAudioRenderer cancellable
                    ? cancellable.Read(blocks[slot], renderer.BlockFrames, cancellation.Token)
                    : renderer.Read(blocks[slot], renderer.BlockFrames);
                if (count == 0) break;
                if (count < 0 || count > renderer.BlockFrames) throw new InvalidOperationException("Renderer lieferte eine ungültige Blocklänge.");
                sizes[slot] = count * 2;
                Volatile.Write(ref produced, write + 1);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception exception) { Volatile.Write(ref workerError, exception); }
        finally
        {
            try { renderer.Dispose(); }
            catch (Exception exception)
            {
                var existing = Volatile.Read(ref workerError);
                Volatile.Write(ref workerError, existing is null ? exception : new AggregateException(existing, exception));
            }
            Volatile.Write(ref complete, 1);
            lock (lifetimeGate)
            {
                workerFinished = true;
                if (disposed != 0) cancellation.Dispose();
            }
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
                Interlocked.Add(ref deviceFrames, output.Length / 2);
                return output.Length * sizeof(float);
            }
            var slot = (int)(read % blocks.Length);
            var samples = Math.Min(output.Length - written, sizes[slot] - blockOffset);
            blocks[slot].AsSpan(blockOffset, samples).CopyTo(output[written..]);
            written += samples; blockOffset += samples;
            Interlocked.Add(ref audioFrames, samples / 2);
            if (blockOffset == sizes[slot]) { blockOffset = 0; Volatile.Write(ref consumed, read + 1); }
        }
        Interlocked.Add(ref deviceFrames, written / 2);
        return written * sizeof(float);
    }

    public void Dispose()
    {
        lock (lifetimeGate)
        {
            if (disposed != 0) return;
            disposed = 1;
            cancellation.Cancel();
            if (workerFinished) cancellation.Dispose();
        }
    }

    internal async Task<bool> StopAsync(TimeSpan timeout)
    {
        if (timeout < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        Dispose();
        return await Task.WhenAny(worker, Task.Delay(timeout)).ConfigureAwait(false) == worker;
    }
}
