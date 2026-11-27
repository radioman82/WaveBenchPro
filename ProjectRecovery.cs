using System.IO;

namespace WellenEditor;

/// <summary>Bounded recovery generations, deliberately separate from user project files.</summary>
internal static class ProjectRecovery
{
    private const int Generations = 3;
    private static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WaveBenchPro", "Recovery");

    internal static async Task SaveAsync(MultitrackProject project, CancellationToken token)
    {
        Directory.CreateDirectory(Root);
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff");
        var directory = Path.Combine(Root, project.Id.ToString("N"), stamp);
        Directory.CreateDirectory(directory);
        try
        {
            await ProjectStorage.SaveAsync(Path.Combine(directory, "recovery.wbproj"), project, ProjectSaveMode.Portable, token).ConfigureAwait(false);
            var parent = Directory.GetParent(directory)!;
            foreach (var old in parent.EnumerateDirectories().OrderByDescending(item => item.Name, StringComparer.Ordinal).Skip(Generations))
                try { old.Delete(true); } catch { /* Recovery cleanup is best effort and only owns this root. */ }
        }
        catch
        {
            try { if (Directory.Exists(directory)) Directory.Delete(directory, true); } catch { }
            throw;
        }
    }
}
