using System.IO;
using System.Text;

namespace WellenEditor;

/// <summary>Owns one private cache directory; inactive, identifiable leftovers can be reclaimed.</summary>
internal sealed class VstCacheLease : IDisposable
{
    private const string MarkerContent = "WaveBenchVstCache/v1";
    private readonly FileStream marker;
    private readonly string markerPath;
    public string DirectoryPath { get; }

    private VstCacheLease(string root)
    {
        Directory.CreateDirectory(root);
        CleanupOrphans(root);
        DirectoryPath = Path.Combine(root, $"wbvst-{Guid.NewGuid():N}");
        Directory.CreateDirectory(DirectoryPath);
        markerPath = Path.Combine(DirectoryPath, "owner.marker");
        marker = new FileStream(markerPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        var bytes = Encoding.ASCII.GetBytes(MarkerContent);
        marker.Write(bytes); marker.Flush();
    }

    public static VstCacheLease Open(string? root = null) => new(root ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WaveBenchPro", "VstCache"));

    private static void CleanupOrphans(string root)
    {
        foreach (var directory in Directory.EnumerateDirectories(root, "wbvst-*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(directory);
            if (name.Length != 38 || !Guid.TryParseExact(name[6..], "N", out _)) continue;
            try
            {
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
                var path = Path.Combine(directory, "owner.marker");
                using (var ownership = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    var bytes = new byte[MarkerContent.Length];
                    if (ownership.Read(bytes) != bytes.Length || ownership.Length != bytes.Length || Encoding.ASCII.GetString(bytes) != MarkerContent) continue;
                    foreach (var file in Directory.EnumerateFiles(directory, "WaveBenchVst_*.wav", SearchOption.TopDirectoryOnly))
                    {
                        var fileName = Path.GetFileNameWithoutExtension(file);
                        if (fileName.Length != 45 || !Guid.TryParseExact(fileName[13..], "N", out _)) continue;
                        if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0) File.Delete(file);
                    }
                }
                if (Directory.EnumerateFileSystemEntries(directory).Any(entry => !string.Equals(entry, path, StringComparison.OrdinalIgnoreCase))) continue;
                File.Delete(path);
                Directory.Delete(directory);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public void Dispose()
    {
        marker.Dispose();
        CleanupOrphans(Path.GetDirectoryName(DirectoryPath)!);
    }
}
