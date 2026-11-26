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

internal interface IPositionedBlockRenderer
{
    long Position { get; }
}

/// <summary>Experimental SPSC playback queue; the callback reads prepared blocks only.</summary>
internal sealed class BufferedBlockWaveProvider : IWaveProvider, IDisposable
{
    private readonly IBlockAudioRenderer renderer;
    private readonly float[][] blocks;
    private readonly int[] sizes;
    private readonly long[] starts;
    private readonly long[] timelineHistory;
    private readonly long initialFrame;
    private readonly AutoResetEvent spaceAvailable = new(false);
    private readonly CancellationTokenSource cancellation = new();
    private readonly Task worker;
    private readonly object lifetimeGate = new();
    private long produced, consumed, underruns, audioFrames, deviceFrames;
    private int blockOffset;
    private int complete;
    private int disposed;
    private Task cancellationTask = Task.CompletedTask;
    private Task cancellationCompletion = Task.CompletedTask;
    private Task resourceCompletion = Task.CompletedTask;
    private int activeReads;
    internal bool HandlesReleased => spaceAvailable.SafeWaitHandle.IsClosed;
    private Exception? workerError;

    public WaveFormat WaveFormat { get; }
    public long UnderrunCount => Interlocked.Read(ref underruns);
    public long AudioFramesRead => Interlocked.Read(ref audioFrames);
    public long DeviceFramesRead => Interlocked.Read(ref deviceFrames);
    // Underrun pauses the source timeline; the device still consumes silent frames.
    public long TimelineFramesRead => AudioFramesRead;
    public Exception? WorkerError => Volatile.Read(ref workerError);
    internal Task Completion => worker;
    internal Task ShutdownCompletion
    {
        get { lock (lifetimeGate) return Task.WhenAll(worker, cancellationCompletion, resourceCompletion); }
    }
    internal int Capacity => blocks.Length;
    internal long AudibleFrame(int latencyFrames)
    {
        var total = DeviceFramesRead;
        if (total == 0) return initialFrame;
        var frame = Math.Max(0, total - 1 - Math.Clamp(latencyFrames, 0, timelineHistory.Length - 1));
        return Interlocked.Read(ref timelineHistory[frame % timelineHistory.Length]);
    }

    /// <summary>Prepares blocks outside the audio callback; false indicates timeout.</summary>
    internal async Task<bool> PrefillAsync(int minimumBlocks, TimeSpan timeout, CancellationToken token = default, Func<bool>? superseded = null)
    {
        if (minimumBlocks < 1 || minimumBlocks > blocks.Length) throw new ArgumentOutOfRangeException(nameof(minimumBlocks));
        if (timeout < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (superseded?.Invoke() == true) throw new OperationCanceledException("Die Wiedergabevorbereitung wurde ersetzt.");
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
        starts = new long[capacity];
        timelineHistory = new long[checked(renderer.SampleRate * 2)];
        initialFrame = renderer is IPositionedBlockRenderer positioned ? positioned.Position : 0;
        worker = Task.Factory.StartNew(Produce, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    private void Produce()
    {
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                var write = Volatile.Read(ref produced);
                if (write - Volatile.Read(ref consumed) >= blocks.Length)
                {
                    WaitHandle.WaitAny(new[] { spaceAvailable, cancellation.Token.WaitHandle });
                    continue;
                }
                var slot = (int)(write % blocks.Length);
                starts[slot] = renderer is IPositionedBlockRenderer positioned ? positioned.Position : write * renderer.BlockFrames;
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
        catch (Exception exception)
        {
            lock (lifetimeGate)
            {
                var existing = workerError;
                Volatile.Write(ref workerError, existing is null ? exception : new AggregateException(existing, exception));
            }
        }
        finally
        {
            try { renderer.Dispose(); }
            catch (Exception exception)
            {
                lock (lifetimeGate)
                {
                    var existing = workerError;
                    Volatile.Write(ref workerError, existing is null ? exception : new AggregateException(existing, exception));
                }
            }
            Volatile.Write(ref complete, 1);
        }
    }

    public int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public int Read(Span<byte> buffer)
    {
        Interlocked.Increment(ref activeReads);
        try { return Volatile.Read(ref disposed) != 0 ? 0 : ReadCore(buffer); }
        finally { Interlocked.Decrement(ref activeReads); }
    }
    private int ReadCore(Span<byte> buffer)
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
                var device = DeviceFramesRead;
                var last = written > 0 ? Interlocked.Read(ref timelineHistory[(device + written / 2 - 1) % timelineHistory.Length])
                    : device > 0 ? Interlocked.Read(ref timelineHistory[(device - 1) % timelineHistory.Length]) : initialFrame;
                for (var frame = written / 2; frame < output.Length / 2; frame++)
                    Interlocked.Exchange(ref timelineHistory[(device + frame) % timelineHistory.Length], last);
                Interlocked.Increment(ref underruns);
                Interlocked.Add(ref deviceFrames, output.Length / 2);
                return output.Length * sizeof(float);
            }
            var slot = (int)(read % blocks.Length);
            var samples = Math.Min(output.Length - written, sizes[slot] - blockOffset);
            blocks[slot].AsSpan(blockOffset, samples).CopyTo(output[written..]);
            var deviceOffset = DeviceFramesRead + written / 2;
            for (var frame = 0; frame < samples / 2; frame++)
                Interlocked.Exchange(ref timelineHistory[(deviceOffset + frame) % timelineHistory.Length], starts[slot] + blockOffset / 2 + frame);
            written += samples; blockOffset += samples;
            Interlocked.Add(ref audioFrames, samples / 2);
            if (blockOffset == sizes[slot])
            {
                blockOffset = 0; Volatile.Write(ref consumed, read + 1);
                // SafeWaitHandle closes the event after the queue is unreachable.
                // An in-flight callback may still use it after producer completion.
                spaceAvailable.Set();
            }
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
            cancellationTask = cancellation.CancelAsync();
            cancellationCompletion = FinishCancellationAsync();
            resourceCompletion = FinishResourcesAsync();
        }
    }

    private async Task FinishCancellationAsync()
    {
        try { await cancellationTask.ConfigureAwait(false); }
        catch (Exception exception)
        {
            lock (lifetimeGate)
            {
                var existing = workerError;
                Volatile.Write(ref workerError, existing is null ? exception : new AggregateException(existing, exception));
            }
        }
    }

    private async Task FinishResourcesAsync()
    {
        await Task.WhenAll(worker, cancellationCompletion).ConfigureAwait(false);
        while (Volatile.Read(ref activeReads) != 0) await Task.Delay(1).ConfigureAwait(false);
        // New reads see disposed before touching either handle. Native readers and
        // cancellation callbacks have actually finished; closing is never on Read.
        spaceAvailable.Dispose();
        cancellation.Dispose();
    }

    internal async Task<bool> StopAsync(TimeSpan timeout)
    {
        if (timeout < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        Dispose();
        var completion = ShutdownCompletion;
        if (await Task.WhenAny(completion, Task.Delay(timeout)).ConfigureAwait(false) != completion) return false;
        await completion.ConfigureAwait(false);
        return true;
    }
}
