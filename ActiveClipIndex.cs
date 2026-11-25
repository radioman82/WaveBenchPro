namespace WellenEditor;

/// <summary>Sequential interval selection while preserving original summation order.</summary>
internal sealed class ActiveClipIndex<T>
{
    private readonly record struct Entry(int Index, long Start, long End);
    private readonly T[] items;
    private readonly Entry[] starts;
    private readonly PriorityQueue<Entry, long> endings = new();
    private readonly SortedSet<int> active = new();
    private int next;
    private long lastStart = -1;
    public ActiveClipIndex(IEnumerable<T> clips, Func<T, long> start, Func<T, long> length)
    {
        items = clips.ToArray();
        starts = items.Select((clip, index) => new Entry(index, start(clip), checked(start(clip) + length(clip))))
            .OrderBy(entry => entry.Start).ThenBy(entry => entry.Index).ToArray();
    }
    public IEnumerable<T> At(long start, int frames)
    {
        if (start < lastStart) throw new InvalidOperationException("Clipindex erwartet fortlaufende Positionen.");
        lastStart = start;
        var end = checked(start + frames);
        while (endings.TryPeek(out var item, out var endFrame) && endFrame <= start)
        { endings.Dequeue(); active.Remove(item.Index); }
        while (next < starts.Length && starts[next].Start < end)
        {
            var entry = starts[next++];
            if (entry.End <= start) continue;
            active.Add(entry.Index);
            endings.Enqueue(entry, entry.End);
        }
        foreach (var index in active) yield return items[index];
    }
}
