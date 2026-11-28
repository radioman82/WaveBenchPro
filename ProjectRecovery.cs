using System.IO;
using System.Text.Json;

namespace WellenEditor;

/// <summary>Owned recovery generations. Open generations stay leased until editor close.</summary>
internal static class ProjectRecovery
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly Dictionary<string, int> Leased = new(StringComparer.OrdinalIgnoreCase);
    internal static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WaveBenchPro", "Recovery");
    internal sealed record Entry(Guid ProjectId, string ProjectPath, string Name, DateTime SavedUtc, string? OriginalPath);
    private sealed record Metadata(Guid ProjectId, string Name, DateTime SavedUtc, string? OriginalPath);
    internal static Task SaveAsync(MultitrackProject project, CancellationToken token) => SaveAsync(project, null, token);
    internal static async Task SaveAsync(MultitrackProject project, string? originalPath, CancellationToken token, string? recoveryRoot = null)
    {
        var root = Path.GetFullPath(recoveryRoot ?? Root); ProjectStorage.EnsureNoReparse(root);
        await Gate.WaitAsync(token).ConfigureAwait(false); string? directory = null;
        try
        {
            directory = Path.Combine(root, project.Id.ToString("N"), DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfffffff") + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            await ProjectStorage.SaveAsync(Path.Combine(directory, "recovery.wbproj"), project, ProjectSaveMode.Portable, token).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(directory, "recovery.json"), JsonSerializer.Serialize(new Metadata(project.Id, project.Name, DateTime.UtcNow, originalPath)), token).ConfigureAwait(false);
            Prune(Path.GetDirectoryName(directory)!);
        }
        catch { if (directory is not null) DeleteOwned(directory, root); throw; }
        finally { Gate.Release(); }
    }
    internal static IReadOnlyList<Entry> Find(string? recoveryRoot = null)
    {
        var root = Path.GetFullPath(recoveryRoot ?? Root);
        if (!Directory.Exists(root)) return Array.Empty<Entry>();
        ProjectStorage.EnsureNoReparse(root); var entries = new List<Entry>();
        foreach (var project in Directory.EnumerateDirectories(root))
        {
            if (!Guid.TryParseExact(Path.GetFileName(project), "N", out var id)) continue;
            ProjectStorage.EnsureNoReparse(project);
            foreach (var directory in Directory.EnumerateDirectories(project).OrderByDescending(Path.GetFileName, StringComparer.Ordinal))
            {
                try
                {
                    ProjectStorage.EnsureNoReparse(directory);
                    if (File.Exists(Path.Combine(directory, "resolved"))) continue;
                    var manifestPath = Path.Combine(directory, "recovery.wbproj"); var info = new FileInfo(manifestPath);
                    if (!info.Exists || info.Length > ProjectFileFormat.MaximumBytes) continue;
                    var manifest = ProjectFileFormat.Parse(File.ReadAllBytes(manifestPath)); var metaPath = Path.Combine(directory, "recovery.json");
                    if (manifest.ProjectId.HasValue && manifest.ProjectId != id) continue;
                    Metadata? meta = File.Exists(metaPath) && new FileInfo(metaPath).Length < 32768 ? JsonSerializer.Deserialize<Metadata>(File.ReadAllText(metaPath)) : null;
                    if (meta is not null && meta.ProjectId != id) continue;
                    entries.Add(new(id, manifestPath, meta?.Name ?? manifest.Name, meta?.SavedUtc ?? info.LastWriteTimeUtc, meta?.OriginalPath)); break;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
            }
        }
        return entries.OrderByDescending(entry => entry.SavedUtc).ToArray();
    }
    internal static IDisposable Lease(Entry entry)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(entry.ProjectPath))!; ProjectStorage.EnsureNoReparse(directory);
        lock (Leased) Leased[directory] = Leased.GetValueOrDefault(directory) + 1; return new LeaseHandle(directory);
    }
    internal static void Reactivate(Entry entry) => File.Delete(Path.Combine(Path.GetDirectoryName(entry.ProjectPath)!, "resolved"));
    internal static async Task ResolveAsync(Guid id, CancellationToken token, string? recoveryRoot = null)
    {
        var root = Path.GetFullPath(recoveryRoot ?? Root); var project = Path.Combine(root, id.ToString("N"));
        await Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ProjectStorage.EnsureNoReparse(project); if (!Directory.Exists(project)) return;
            foreach (var directory in Directory.EnumerateDirectories(project))
            {
                ProjectStorage.EnsureNoReparse(directory); File.WriteAllText(Path.Combine(directory, "resolved"), "Resolved by explicit save/discard.");
                lock (Leased) { if (Leased.ContainsKey(directory)) continue; }
                DeleteOwned(directory, root);
            }
        }
        finally { Gate.Release(); }
    }
    private static void Prune(string project)
    {
        var unleased = 0;
        foreach (var directory in Directory.EnumerateDirectories(project).OrderByDescending(Path.GetFileName, StringComparer.Ordinal))
        {
            lock (Leased) { if (Leased.ContainsKey(directory)) continue; }
            if (++unleased > 3 || File.Exists(Path.Combine(directory, "resolved"))) DeleteOwned(directory, Path.GetDirectoryName(project)!);
        }
    }
    private static void DeleteOwned(string directory, string root)
    {
        var full = Path.GetFullPath(directory);
        if (!full.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Recovery-Pfad außerhalb des eigenen Verzeichnisses.");
        if (!Directory.Exists(full)) return;
        ProjectStorage.EnsureNoReparse(full); ValidateTree(full); Directory.Delete(full, true);
    }
    private static void ValidateTree(string directory)
    {
        foreach (var item in Directory.EnumerateFileSystemEntries(directory))
        { ProjectStorage.EnsureNoReparse(item); if (Directory.Exists(item)) ValidateTree(item); }
    }
    private sealed class LeaseHandle(string directory) : IDisposable
    {
        private bool disposed;
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            lock (Leased) { if (Leased[directory] > 1) Leased[directory]--; else Leased.Remove(directory); }
            if (!Gate.Wait(0)) return;
            try { Prune(Path.GetDirectoryName(directory)!); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            finally { Gate.Release(); }
        }
    }
}
