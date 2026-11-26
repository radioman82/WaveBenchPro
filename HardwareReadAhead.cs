using NAudio.Wave;
using System.IO;

namespace WellenEditor;

internal interface IHardwareReadyWaveProvider { bool HardwareReady { get; } }

/// <summary>Hardware boundary for file/plugin providers. The device callback only copies reserved blocks.</summary>
internal sealed class HardwareReadAhead : IWaveProvider, IDisposable, IHardwareReadyWaveProvider
{
    private readonly IWaveProvider source;
    private readonly byte[][] blocks;
    private readonly int[] lengths;
    private readonly AutoResetEvent space = new(false);
    private readonly Task producer;
    private long produced, consumed, underruns;
    private int offset, stopping, completed, handlesDisposed;
    private Exception? error;
    public WaveFormat WaveFormat => source.WaveFormat;
    public bool HardwareReady => true;
    public long Underruns => Interlocked.Read(ref underruns);
    public Exception? Error => Volatile.Read(ref error);
    internal Task Completion => producer;
    internal HardwareReadAhead(IWaveProvider source, int capacity = 8, int frames = 1024)
    {
        this.source = source;
        if (capacity is < 2 or > 256 || frames is < 1 or > 65536) throw new ArgumentOutOfRangeException(nameof(capacity));
        blocks = Enumerable.Range(0, capacity).Select(_ => new byte[checked(frames * source.WaveFormat.BlockAlign)]).ToArray();
        lengths = new int[capacity];
        producer = Task.Factory.StartNew(Produce, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }
    private void Produce()
    {
        try
        {
            while (Volatile.Read(ref stopping) == 0)
            {
                var write = Volatile.Read(ref produced);
                if (write - Volatile.Read(ref consumed) >= blocks.Length) { space.WaitOne(); continue; }
                var slot = (int)(write % blocks.Length);
                var read = source.Read(blocks[slot]);
                if (read == 0) break;
                if (read < 0 || read > blocks[slot].Length || read % WaveFormat.BlockAlign != 0)
                    throw new InvalidDataException("Der Audio-Provider lieferte einen ungültigen Block.");
                lengths[slot] = read;
                Volatile.Write(ref produced, write + 1);
            }
        }
        catch (Exception e) { Volatile.Write(ref error, e); }
        finally { Volatile.Write(ref completed, 1); }
    }
    internal async Task PrefillAsync(TimeSpan timeout)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (Volatile.Read(ref produced) < Math.Min(2, blocks.Length) && Volatile.Read(ref completed) == 0)
        {
            if (watch.Elapsed >= timeout) throw new TimeoutException("Audio-Vorlauf antwortet nicht. Der Worker bleibt bis zu seinem tatsächlichen Ende im Besitz der Quelle.");
            await Task.Delay(5).ConfigureAwait(false);
        }
        if (Error is { } failure) throw new InvalidOperationException("Audio-Vorlauf fehlgeschlagen.", failure);
    }
    public int Read(byte[] buffer, int start, int count) => Read(buffer.AsSpan(start, count));
    public int Read(Span<byte> target)
    {
        if (Volatile.Read(ref stopping) != 0) return 0;
        var written = 0;
        var usable = target.Length / WaveFormat.BlockAlign * WaveFormat.BlockAlign;
        while (written < usable)
        {
            var next = Volatile.Read(ref consumed);
            if (next == Volatile.Read(ref produced))
            {
                if (Volatile.Read(ref completed) != 0)
                {
                    if (next == Volatile.Read(ref produced)) break;
                    continue;
                }
                target[written..usable].Clear();
                Interlocked.Increment(ref underruns);
                return usable;
            }
            var slot = (int)(next % blocks.Length);
            var bytes = Math.Min(usable - written, lengths[slot] - offset);
            blocks[slot].AsSpan(offset, bytes).CopyTo(target[written..]);
            written += bytes; offset += bytes;
            if (offset == lengths[slot]) { offset = 0; Volatile.Write(ref consumed, next + 1); space.Set(); }
        }
        return written;
    }
    public void Dispose() { if (Interlocked.Exchange(ref stopping, 1) == 0) space.Set(); }
    internal async Task<bool> StopAsync(TimeSpan timeout)
    { Dispose(); return await Task.WhenAny(producer, Task.Delay(timeout)).ConfigureAwait(false) == producer; }
    internal void DrainAndDispose()
    {
        Dispose(); producer.GetAwaiter().GetResult(); // Never release a provider while its native Read is still running.
        if (Interlocked.Exchange(ref handlesDisposed, 1) == 0) space.Dispose();
    }
    internal static bool NeedsWorker(IWaveProvider source) => source is not IHardwareReadyWaveProvider { HardwareReady: true } && source is not ArrayWaveProvider;
}
