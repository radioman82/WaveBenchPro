using System.IO;

namespace WellenEditor;

/// <summary>Transactional UI-facing project state, testable without a visible window.</summary>
internal sealed class ProjectWorkspace : IDisposable
{
    private readonly List<IDisposable> recoveryLeases = new();
    private long revision;
    private bool dirty;
    private Dictionary<AudioDocument, long> savedSources = new();
    internal MultitrackProject Project { get; private set; } = new();
    internal string? Path { get; private set; }
    internal ProjectSaveMode Mode { get; private set; }
    internal bool IsDirty { get => dirty || Versions(Project).Any(pair => !savedSources.TryGetValue(pair.Key, out var version) || version != pair.Value); private set => dirty = value; }
    internal bool IsBusy { get; private set; }
    internal void MarkDirty() { revision++; IsDirty = true; }
    internal void MarkClean() { savedSources = Versions(Project); IsDirty = false; }
    internal void New()
    {
        if (IsBusy) throw new InvalidOperationException("Projekt-I/O läuft noch.");
        Project = new(); savedSources.Clear(); Path = null; Mode = ProjectSaveMode.Reference; IsDirty = false; revision++;
    }
    internal async Task<ProjectStorage.LoadedProject> OpenAsync(string path, ProjectStorage.RelinkSources? relink, CancellationToken token)
    {
        Begin();
        try
        {
            var loaded = await ProjectStorage.LoadReferenceAsync(path, token, relink);
            Project = loaded.Project; Path = System.IO.Path.GetFullPath(path); Mode = loaded.Portable ? ProjectSaveMode.Portable : ProjectSaveMode.Reference;
            savedSources = Versions(Project); IsDirty = loaded.Relinked; revision++; return loaded;
        }
        finally { IsBusy = false; }
    }
    internal async Task SaveAsync(string path, ProjectSaveMode mode, CancellationToken token, ProjectStorage.IoHooks? hooks = null)
    {
        Begin(); var savedRevision = revision; var sourceVersions = Versions(Project);
        try
        {
            await ProjectStorage.SaveAsync(path, Project, mode, token, hooks);
            Path = System.IO.Path.GetFullPath(path.EndsWith(".wbproj", StringComparison.OrdinalIgnoreCase) ? path : path + ".wbproj");
            Mode = mode; Project.Name = System.IO.Path.GetFileNameWithoutExtension(Path); savedSources = sourceVersions; IsDirty = revision != savedRevision;
            try { await ProjectRecovery.ResolveAsync(Project.Id, CancellationToken.None); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        finally { IsBusy = false; }
    }
    internal async Task<ProjectStorage.LoadedProject> RecoverAsync(ProjectRecovery.Entry entry, ProjectStorage.RelinkSources? relink, CancellationToken token)
    {
        Begin(); var lease = ProjectRecovery.Lease(entry); ProjectStorage.LoadedProject? loaded = null;
        try
        {
            loaded = await ProjectStorage.LoadReferenceAsync(entry.ProjectPath, token, relink);
            if (loaded.LegacyIdentity) loaded.Project.RestoreRecoveryIdentity(entry.ProjectId);
            ProjectRecovery.Reactivate(entry);
            recoveryLeases.Add(lease); Project = loaded.Project; Path = entry.OriginalPath;
            Mode = ProjectSaveMode.Portable; savedSources = Versions(Project); IsDirty = true; revision++; return loaded;
        }
        catch { if (loaded is not null) foreach (var document in loaded.Documents) document.Dispose(); lease.Dispose(); throw; }
        finally { IsBusy = false; }
    }
    private void Begin() { if (IsBusy) throw new InvalidOperationException("Projekt-I/O läuft bereits."); IsBusy = true; }
    private static Dictionary<AudioDocument, long> Versions(MultitrackProject project) => project.Tracks.SelectMany(track => track.Clips).Select(clip => clip.Source).Distinct().ToDictionary(source => source, source => source.ContentVersion);
    public void Dispose() { foreach (var lease in recoveryLeases) lease.Dispose(); recoveryLeases.Clear(); }
}
