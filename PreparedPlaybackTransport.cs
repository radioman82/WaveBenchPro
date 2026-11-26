using NAudio.Wave;

namespace WellenEditor;

/// <summary>Owns every producer, including queues that never reached publication.</summary>
internal sealed class PreparedMultitrackPlayback : IWaveProvider, IDisposable
{
    private readonly IPreparedPlaybackSession session;
    private readonly SemaphoreSlim transportGate = new(1);
    private readonly object publicationGate = new();
    private readonly List<BufferedBlockWaveProvider> ownedQueues = new();
    private readonly CancellationTokenSource stopping = new();
    private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TimeSpan prefillTimeout, retirementTimeout;
    private BufferedBlockWaveProvider? queue, preparingQueue;
    private int generation, publishedGeneration, disposed;
    private long underruns, requestedFrame;
    private Exception? transportError;
    private PlaybackRegion? region;

    private sealed record PlaybackRegion(long Start, long Count, bool Loop);
    public Task Completion => completion.Task;
    public WaveFormat WaveFormat { get; }
    public long FrameCount => session.FrameCount;
    public long UnderrunCount => Interlocked.Read(ref underruns) + (Volatile.Read(ref queue)?.UnderrunCount ?? 0);
    public Exception? WorkerError => Volatile.Read(ref queue)?.WorkerError ?? Volatile.Read(ref transportError);
    public int BufferBlocks { get; }
    public int BlockFrames { get; }

    public PreparedMultitrackPlayback(IPreparedPlaybackSession session, int blockFrames = 1024, int bufferBlocks = 8,
        TimeSpan? prefillTimeout = null, TimeSpan? retirementTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (blockFrames is < 1 or > 65536 || bufferBlocks is < 2 or > 256) throw new ArgumentOutOfRangeException();
        this.session = session; BlockFrames = blockFrames; BufferBlocks = bufferBlocks;
        this.prefillTimeout = prefillTimeout ?? TimeSpan.FromSeconds(5);
        this.retirementTimeout = retirementTimeout ?? TimeSpan.FromSeconds(2);
        if (this.prefillTimeout < TimeSpan.Zero || this.retirementTimeout < TimeSpan.Zero) throw new ArgumentOutOfRangeException();
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(session.SampleRate, 2);
    }

    public Task ConfigurePlaybackAsync(long start, long count, bool loop, CancellationToken cancellationToken = default)
        => ConfigureAsync(start, count, loop, start, cancellationToken);

    public Task SeekAsync(long frame)
    {
        var selected = Volatile.Read(ref region) ?? new PlaybackRegion(0, FrameCount, false);
        // Seeking never changes the selection/loop bounds. An outside click on
        // a selection loop returns to its start; a non-looping selection clamps.
        var end = selected.Start + selected.Count;
        var initial = frame >= selected.Start && frame < end ? frame
            : selected.Loop ? selected.Start : Math.Clamp(frame, selected.Start, end - 1);
        return ConfigureAsync(selected.Start, selected.Count, selected.Loop, initial);
    }

    private async Task ConfigureAsync(long start, long count, bool loop, long initial, CancellationToken cancellationToken = default)
    {
        if (start < 0 || start >= FrameCount || count <= 0 || count > FrameCount - start)
            throw new ArgumentOutOfRangeException(nameof(count), "Auswahl liegt außerhalb des Projekts.");
        int version;
        lock (publicationGate)
        {
            ObjectDisposedException.ThrowIf(disposed != 0, this);
            version = ++generation;
            Volatile.Write(ref region, new PlaybackRegion(start, count, loop));
            Interlocked.Exchange(ref requestedFrame, initial);
        }
        await transportGate.WaitAsync().ConfigureAwait(false);
        BufferedBlockWaveProvider? next = null;
        try
        {
            if (disposed != 0 || version != Volatile.Read(ref generation)) return;
            cancellationToken.ThrowIfCancellationRequested();
            var previous = Interlocked.Exchange(ref queue, null);
            if (previous is not null) Interlocked.Add(ref underruns, previous.UnderrunCount);
            // A timed-out, failed or obsolete prefill remains in this list until
            // its worker AND cancellation finalization have actually finished.
            for (var i = ownedQueues.Count - 1; i >= 0; i--)
            {
                var old = ownedQueues[i];
                if (!await old.StopAsync(retirementTimeout).ConfigureAwait(false))
                    throw new TimeoutException("Der vorherige Audioworker hält noch Ressourcen. Bitte später erneut versuchen.");
                if (old.WorkerError is { } error) RememberError(error);
                ownedQueues.RemoveAt(i);
            }
            if (disposed != 0 || version != Volatile.Read(ref generation)) return;
            cancellationToken.ThrowIfCancellationRequested();
            if (WorkerError is { } priorError) throw new InvalidOperationException("Der vorherige Audioworker ist fehlgeschlagen.", priorError);
            // Opening source handles is never performed by the UI or callback.
            var renderer = await Task.Run(() => session.OpenRenderer(start, count, loop, BlockFrames, initial)).ConfigureAwait(false);
            try { next = new BufferedBlockWaveProvider(renderer, BufferBlocks); }
            catch { renderer.Dispose(); throw; }
            ownedQueues.Add(next);
            Volatile.Write(ref preparingQueue, next);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token, cancellationToken);
            if (!await next.PrefillAsync(Math.Min(2, BufferBlocks), prefillTimeout, linked.Token,
                () => version != Volatile.Read(ref generation)).ConfigureAwait(false))
                throw new TimeoutException("Der Audiopuffer konnte nicht rechtzeitig vorbereitet werden.");
            lock (publicationGate)
            {
                if (disposed != 0 || version != generation) return;
                Volatile.Write(ref publishedGeneration, version);
                Volatile.Write(ref queue, next);
                next = null;
            }
        }
        catch (OperationCanceledException) when (disposed != 0 || version != Volatile.Read(ref generation)) { }
        finally
        {
            try
            {
                Volatile.Write(ref preparingQueue, null);
                if (next is not null)
                {
                    if (await next.StopAsync(retirementTimeout).ConfigureAwait(false))
                    {
                        if (next.WorkerError is { } error) RememberError(error);
                        ownedQueues.Remove(next);
                    }
                    // Otherwise ownership is retained, preventing the next
                    // generation from creating any additional worker.
                }
            }
            finally { transportGate.Release(); }
        }
    }

    public long AudibleFrame(int latencyFrames)
    {
        var current = Volatile.Read(ref queue);
        return current is not null && Volatile.Read(ref publishedGeneration) == Volatile.Read(ref generation)
            ? current.AudibleFrame(latencyFrames) : Interlocked.Read(ref requestedFrame);
    }

    public int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public int Read(Span<byte> buffer)
    {
        var current = Volatile.Read(ref queue);
        var version = Volatile.Read(ref generation);
        if (current is null || Volatile.Read(ref publishedGeneration) != version)
        { buffer.Clear(); return Volatile.Read(ref disposed) == 0 ? buffer.Length / 8 * 8 : 0; }
        var read = current.Read(buffer);
        if (version != Volatile.Read(ref generation) || !ReferenceEquals(current, Volatile.Read(ref queue)))
        { buffer.Clear(); return Volatile.Read(ref disposed) == 0 ? buffer.Length / 8 * 8 : 0; }
        return read;
    }

    private void RememberError(Exception error)
    {
        lock (publicationGate)
        {
            if (ReferenceEquals(transportError, error)) return;
            Volatile.Write(ref transportError, transportError is null ? error : new AggregateException(transportError, error));
        }
    }

    public void Dispose()
    {
        Task cancellation;
        lock (publicationGate)
        {
            if (disposed != 0) return;
            var current = Volatile.Read(ref queue);
            if (current is not null) Interlocked.Exchange(ref requestedFrame, current.AudibleFrame(0));
            disposed = 1; generation++;
            current = Interlocked.Exchange(ref queue, null);
            current?.Dispose();
            Volatile.Read(ref preparingQueue)?.Dispose();
            cancellation = stopping.CancelAsync();
        }
        _ = Task.Run(() => FinalizeAsync(cancellation));
    }

    private async Task FinalizeAsync(Task cancellation)
    {
        await transportGate.WaitAsync().ConfigureAwait(false);
        var errors = new List<Exception>();
        try
        {
            foreach (var old in ownedQueues)
            {
                old.Dispose();
                await old.ShutdownCompletion.ConfigureAwait(false);
                if (old.WorkerError is { } error) errors.Add(error);
            }
            ownedQueues.Clear();
            if (Volatile.Read(ref transportError) is { } retiredError && !errors.Contains(retiredError)) errors.Add(retiredError);
            try { await cancellation.ConfigureAwait(false); } catch (Exception error) { errors.Add(error); }
            try { session.Dispose(); } catch (Exception error) { errors.Add(error); }
            // Dispose may only REQUEST cleanup while an external session reader
            // still exists. Completion includes that reader and the actual lease.
            try { await session.Completion.ConfigureAwait(false); }
            catch (Exception error) { if (!errors.Contains(error)) errors.Add(error); }
            if (errors.Count == 0) completion.TrySetResult();
            else
            {
                var error = new AggregateException("Audiowiedergabe konnte nicht vollständig freigegeben werden.", errors);
                RememberError(error); completion.TrySetException(error);
            }
        }
        catch (Exception error) { RememberError(error); completion.TrySetException(error); }
        finally { stopping.Dispose(); transportGate.Release(); }
    }

    public async Task<bool> StopAsync(TimeSpan timeout)
    {
        if (timeout < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        Dispose();
        if (await Task.WhenAny(Completion, Task.Delay(timeout)).ConfigureAwait(false) != Completion) return false;
        await Completion.ConfigureAwait(false); // Cleanup failure is NOT a timeout.
        return true;
    }
}
