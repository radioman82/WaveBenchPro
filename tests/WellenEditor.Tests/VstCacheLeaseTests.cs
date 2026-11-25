using System.IO;
using WellenEditor;
using Xunit;

namespace WellenEditorTests;

public sealed class VstCacheLeaseTests
{
    [Fact]
    public async Task ConcurrentSessionsReserveWithoutOverwritingEachOther()
    {
        var root = Path.Combine(Path.GetTempPath(), $"wavebench_lease_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            using var first = VstCacheLease.Open(root);
            using var second = VstCacheLease.Open(root);
            await Task.WhenAll(Task.Run(() => first.Reserve(4096)), Task.Run(() => second.Reserve(8192)));
            Assert.Equal("4096", File.ReadAllText(Path.Combine(first.DirectoryPath, "reservation.txt")));
            Assert.Equal("8192", File.ReadAllText(Path.Combine(second.DirectoryPath, "reservation.txt")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void GlobalBudgetCountsExistingReservationsAndReleasesOnDispose()
    {
        var root = Path.Combine(Path.GetTempPath(), $"wavebench_lease_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            using var first = VstCacheLease.Open(root);
            using var second = VstCacheLease.Open(root);
            // Simulate an active reservation without allocating disk blocks.
            File.WriteAllText(Path.Combine(first.DirectoryPath, "reservation.txt"), (16L * 1024 * 1024 * 1024).ToString());
            Assert.Throws<IOException>(() => second.Reserve(1));
            first.Dispose();
            second.Reserve(1);
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public void CleanupSkipsActiveAndUnrecognizedFilesButRemovesOwnedOrphanAudio()
    {
        var root = Path.Combine(Path.GetTempPath(), $"wavebench_lease_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            using var active = VstCacheLease.Open(root);
            var activeAudio = Path.Combine(active.DirectoryPath, $"WaveBenchVst_{Guid.NewGuid():N}.wav");
            File.WriteAllText(activeAudio, "active");
            var orphan = Path.Combine(root, $"wbvst-{Guid.NewGuid():N}");
            Directory.CreateDirectory(orphan);
            File.WriteAllText(Path.Combine(orphan, "owner.marker"), "WaveBenchVstCache/v1");
            var staleAudio = Path.Combine(orphan, $"WaveBenchVst_{Guid.NewGuid():N}.wav");
            File.WriteAllText(staleAudio, "stale");
            var unrelated = Path.Combine(orphan, "do-not-delete.txt");
            File.WriteAllText(unrelated, "keep");

            using (var another = VstCacheLease.Open(root))
            {
                Assert.True(File.Exists(activeAudio));
                Assert.False(File.Exists(staleAudio));
                Assert.True(File.Exists(unrelated));
                Assert.True(File.Exists(Path.Combine(orphan, "owner.marker")));
            }
        }
        finally { Directory.Delete(root, true); }
    }
}
