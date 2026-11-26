namespace WellenEditor;

/// <summary>Preallocated SPSC capture queue. One extra slot lets the writer retain its current block.</summary>
internal sealed class CapturePacketQueue : IDisposable
{
    internal sealed class Block(int bytes) { internal readonly byte[] Buffer = new byte[bytes]; internal int BytesRecorded; }
    private readonly Block[] blocks;
    private readonly AutoResetEvent available = new(false);
    private long read, write;
    private int completed;
    private int activeWrites;
    internal CapturePacketQueue(int capacity, int blockBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(blockBytes);
        blocks = Enumerable.Range(0, capacity + 1).Select(_ => new Block(blockBytes)).ToArray();
    }
    internal bool TryWrite(ReadOnlySpan<byte> data)
    {
        Interlocked.Increment(ref activeWrites);
        try
        {
        var next = Volatile.Read(ref write);
        if (Volatile.Read(ref completed) != 0 || next - Volatile.Read(ref read) >= blocks.Length || data.Length > blocks[0].Buffer.Length) return false;
        var block = blocks[(int)(next % blocks.Length)];
        data.CopyTo(block.Buffer); block.BytesRecorded = data.Length;
        Volatile.Write(ref write, next + 1); available.Set(); return true;
        }
        finally { Interlocked.Decrement(ref activeWrites); }
    }
    internal bool TryRead(out Block? block)
    {
        while (true)
        {
            var next = Volatile.Read(ref read);
            if (next < Volatile.Read(ref write)) { block = blocks[(int)(next % blocks.Length)]; return true; }
            if (Volatile.Read(ref completed) != 0)
            {
                if (Volatile.Read(ref activeWrites) == 0 && next == Volatile.Read(ref write)) { block = null; return false; }
                available.WaitOne(1); // A packet admitted before completion still owns its slot.
            }
            else available.WaitOne();
        }
    }
    internal void ReleaseRead() => Volatile.Write(ref read, Volatile.Read(ref read) + 1);
    internal void Complete() { Volatile.Write(ref completed, 1); available.Set(); }
    public void Dispose()
    {
        Volatile.Write(ref completed, 1);
        SpinWait.SpinUntil(() => Volatile.Read(ref activeWrites) == 0);
        available.Dispose();
    }
}
