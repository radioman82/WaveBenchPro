using System.IO;
using WellenEditor;
using Xunit;

namespace WellenEditorTests;

public sealed class VstCacheLeaseTests
{
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
