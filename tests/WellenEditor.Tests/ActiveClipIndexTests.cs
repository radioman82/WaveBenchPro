using System.Diagnostics;
using WellenEditor;
using Xunit;

namespace WellenEditorTests;

public sealed class ActiveClipIndexTests
{
    private sealed record Clip(long Start, long Length, int Id);

    [Fact]
    public void IntervalsMatchBruteForceAndPreserveOriginalOrder()
    {
        var random = new Random(44);
        var clips = Enumerable.Range(0, 2000).Select(i => new Clip(random.Next(0, 100000), random.Next(1, 3000), i)).ToArray();
        var index = new ActiveClipIndex<Clip>(clips, c => c.Start, c => c.Length);
        for (long position = 0; position < 105000; position += 257)
        {
            var expected = clips.Where(c => c.Start < position + 257 && c.Start + c.Length > position).Select(c => c.Id);
            Assert.Equal(expected, index.At(position, 257).Select(c => c.Id));
        }
    }

    [Fact]
    public void ManySparseClipsBenchmark()
    {
        var clips = Enumerable.Range(0, 20000).Select(i => new Clip((long)i * 4096, 512, i)).ToArray();
        var index = new ActiveClipIndex<Clip>(clips, c => c.Start, c => c.Length);
        var watch = Stopwatch.StartNew();
        long matches = 0;
        for (long position = 0; position < 20000L * 4096; position += 4096) matches += index.At(position, 4096).Count();
        watch.Stop();
        Assert.Equal(20000, matches);
        var indexedMs = watch.Elapsed.TotalMilliseconds;
        watch.Restart();
        long linearMatches = 0;
        for (long position = 0; position < 20000L * 4096; position += 4096)
            foreach (var clip in clips)
                if (clip.Start < position + 4096 && clip.Start + clip.Length > position) linearMatches++;
        watch.Stop();
        Assert.Equal(matches, linearMatches);
        Console.WriteLine($"20,000 clips/blocks indexed_ms={indexedMs:F1} linear_ms={watch.Elapsed.TotalMilliseconds:F1}");
    }
}
