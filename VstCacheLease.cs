using System.IO;
using System.Text;

namespace WellenEditor;

/// <summary>Owns one private cache directory; inactive, identifiable leftovers can be reclaimed.</summary>
internal sealed class VstCacheLease : IDisposable
{
    private const string MarkerContent = "WaveBenchVstCache/v1";
    private readonly FileStream marker;
    private readonly string markerPath;
    private readonly string rootPath;
    private long reservedBytes;
    private const long GlobalBudget = 16L * 1024 * 1024 * 1024;
    public string DirectoryPath { get; }

    private VstCacheLease(string root)
    {
        Directory.CreateDirectory(root);
        rootPath = root;
        using (AcquireBudgetLock()) CleanupOrphans(root);
        DirectoryPath = Path.Combine(root, $"wbvst-{Guid.NewGuid():N}");
        Directory.CreateDirectory(DirectoryPath);
        markerPath = Path.Combine(DirectoryPath, "owner.marker");
        marker = new FileStream(markerPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        var bytes = Encoding.ASCII.GetBytes(MarkerContent);
        marker.Write(bytes); marker.Flush();
    }

    public static VstCacheLease Open(string? root = null) => new(root ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WaveBenchPro", "VstCache"));

    public void Reserve(long bytes)
    {
        if (bytes < 0) throw new ArgumentOutOfRangeException(nameof(bytes));
        using var gate = AcquireBudgetLock();
        long occupied = 0;
        foreach (var directory in Directory.EnumerateDirectories(rootPath, "wbvst-*"))
        {
            var reservation = Path.Combine(directory, "reservation.txt");
            if (!File.Exists(reservation)) continue;
            if (!long.TryParse(File.ReadAllText(reservation), out var value) || value < 0)
                throw new IOException("Eine VST-Speicherreservierung ist ungültig und muss überprüft werden.");
            occupied = checked(occupied + value);
        }
        if (checked(occupied + bytes) > GlobalBudget)
            throw new IOException("Das gemeinsame VST-Zwischendatei-Budget von 16 GiB ist ausgeschöpft.");
        var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(rootPath))!);
        if (drive.AvailableFreeSpace < bytes + 64L * 1024 * 1024)
            throw new IOException("Nicht genügend freier Speicherplatz für die VST-Zwischendatei.");
        reservedBytes = checked(reservedBytes + bytes);
        File.WriteAllText(Path.Combine(DirectoryPath, "reservation.txt"), reservedBytes.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private FileStream AcquireBudgetLock()
    {
        var path = Path.Combine(rootPath, "budget.lock");
        var deadline = Environment.TickCount64 + 5000;
        while (true)
        {
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (Environment.TickCount64 < deadline) { Thread.Sleep(10); }
        }
    }

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
                    var reservation = Path.Combine(directory, "reservation.txt");
                    if (File.Exists(reservation)) File.Delete(reservation);
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
        using var gate = AcquireBudgetLock();
        marker.Dispose();
        CleanupOrphans(rootPath);
    }
}
