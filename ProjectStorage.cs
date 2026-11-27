using System.IO;
using System.Security.Cryptography;
using NAudio.Vst3;

namespace WellenEditor;

internal enum ProjectSaveMode { Reference, Portable }

/// <summary>Atomic project persistence. Asset writes finish before the manifest is replaced.</summary>
internal static class ProjectStorage
{
    private static readonly SemaphoreSlim SaveGate = new(1, 1);
    internal sealed record LoadedProject(MultitrackProject Project, IReadOnlyList<AudioDocument> Documents, IReadOnlyList<string> Warnings);

    internal static Task SaveReferenceAsync(string path, MultitrackProject project, CancellationToken cancellationToken) =>
        SaveAsync(path, project, ProjectSaveMode.Reference, cancellationToken);

    internal static async Task SaveAsync(string path, MultitrackProject project, ProjectSaveMode mode, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        var fullPath = EnsureProjectExtension(Path.GetFullPath(path));
        var directory = Path.GetDirectoryName(fullPath)!;
        var snapshot = project.Snapshot(); // UI-side immutable routing snapshot; the audio itself is written/read below.
        Directory.CreateDirectory(directory);
        await SaveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? temporary = null;
        try
        {
            var documents = snapshot.Tracks.SelectMany(track => track.Clips).Select(clip => clip.Source).Distinct().ToArray();
            var sourceIds = documents.ToDictionary(document => document, _ => Guid.NewGuid());
            var assets = Path.Combine(directory, Path.GetFileNameWithoutExtension(fullPath) + ".assets");
            var sources = new List<ProjectFileFormat.Source>();
            foreach (var document in documents)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var useAsset = mode == ProjectSaveMode.Portable || document.IsModified || string.IsNullOrWhiteSpace(document.SourcePath) || !File.Exists(document.SourcePath);
                string sourcePath;
                if (useAsset)
                {
                    Directory.CreateDirectory(assets);
                    var assetName = sourceIds[document].ToString("N") + ".wav";
                    sourcePath = Path.Combine(assets, assetName);
                    // Assets are owned by this project and are atomically replaced individually.
                    var pending = sourcePath + ".tmp-" + Guid.NewGuid().ToString("N");
                    try
                    {
                        await Task.Run(() => document.SaveAsWave(pending, 32), cancellationToken).ConfigureAwait(false);
                        File.Move(pending, sourcePath, true);
                    }
                    finally { if (File.Exists(pending)) File.Delete(pending); }
                }
                else sourcePath = Path.GetFullPath(document.SourcePath!);
                var identity = await IdentityAsync(sourcePath, cancellationToken).ConfigureAwait(false);
                var storedPath = Path.IsPathFullyQualified(sourcePath) && !IsDescendant(directory, sourcePath)
                    ? sourcePath : Path.GetRelativePath(directory, sourcePath);
                sources.Add(new(sourceIds[document], storedPath, identity.Length, identity.Hash, useAsset));
            }
            var tracks = snapshot.Tracks.Select(track => new ProjectFileFormat.Track(track.Id, track.Name, track.Gain, track.Pan, track.Muted, track.Solo,
                track.Clips.Select(clip => new ProjectFileFormat.Clip(clip.Id, sourceIds[clip.Source], clip.StartFrame, clip.DurationScale)).ToArray(),
                track.Effects.Select(ToManifestEffect).ToArray())).ToArray();
            var manifest = new ProjectFileFormat.Manifest(ProjectFileFormat.Version, Path.GetFileNameWithoutExtension(fullPath), snapshot.SampleRate, snapshot.Bpm,
                sources.ToArray(), tracks);
            var bytes = ProjectFileFormat.Serialize(manifest);
            temporary = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false); stream.Flush(true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, fullPath, true); temporary = null;
        }
        finally
        {
            if (temporary is not null && File.Exists(temporary)) File.Delete(temporary);
            SaveGate.Release();
        }
    }

    internal static async Task<LoadedProject> LoadReferenceAsync(string path, CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path); var directory = Path.GetDirectoryName(fullPath)!;
        byte[] bytes;
        await using (var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            if (stream.Length > ProjectFileFormat.MaximumBytes) throw new InvalidDataException("Projektdatei ist zu groß.");
            bytes = new byte[checked((int)stream.Length)]; await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        }
        var manifest = ProjectFileFormat.Parse(bytes); var documents = new Dictionary<Guid, AudioDocument>(); var warnings = new List<string>();
        try
        {
            foreach (var source in manifest.Sources)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sourcePath = ResolveSourcePath(directory, source.Path);
                if (!File.Exists(sourcePath)) throw new FileNotFoundException($"Projektquelle fehlt: {Path.GetFileName(sourcePath)}", sourcePath);
                if ((File.GetAttributes(sourcePath) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Projektquelle darf kein Reparse Point sein.");
                var identity = await IdentityAsync(sourcePath, cancellationToken).ConfigureAwait(false);
                if (identity.Length != source.LengthBytes || !string.Equals(identity.Hash, source.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Projektquelle wurde verändert: {Path.GetFileName(sourcePath)}");
                documents.Add(source.Id, AudioDocument.Load(sourcePath));
            }
            var project = MultitrackProject.Restore(manifest.SampleRate, manifest.Bpm, manifest.Name);
            var catalog = Vst3EffectCatalog.LoadCache();
            foreach (var savedTrack in manifest.Tracks)
            {
                var track = project.AddEmptyTrack(); track.Id = savedTrack.Id;
                track.Name = savedTrack.Name; track.Gain = savedTrack.Gain; track.Pan = savedTrack.Pan; track.Muted = savedTrack.Muted; track.Solo = savedTrack.Solo;
                foreach (var savedClip in savedTrack.Clips)
                {
                    track.SetClip(documents[savedClip.SourceId], savedClip.StartFrame); track.Clips[^1].Id = savedClip.Id; track.Clips[^1].DurationScale = savedClip.DurationScale;
                    _ = track.EndFrame;
                }
                track.SetEffects((savedTrack.Effects ?? Array.Empty<ProjectFileFormat.Effect>()).Select(effect => FromManifestEffect(effect, catalog, warnings)));
            }
            return new(project, documents.Values.ToArray(), warnings);
        }
        catch { foreach (var document in documents.Values) document.Dispose(); throw; }
    }

    private static ProjectFileFormat.Effect ToManifestEffect(TrackEffectSlot effect) => new(effect.Id, effect.Kind.ToString(), effect.Enabled,
        ProjectFileFormat.From(effect.Settings), effect.Vst is null ? null : new(effect.Vst.ClassInfo.ClassId, effect.Vst.Name, effect.Vst.Vendor),
        effect.VstState is null ? null : Convert.ToBase64String(effect.VstState));

    private static TrackEffectSlot FromManifestEffect(ProjectFileFormat.Effect effect, IReadOnlyList<Vst3EffectDescriptor> catalog, List<string> warnings)
    {
        var kind = Enum.Parse<NativeEffectKind>(effect.Kind); var state = effect.StateBase64 is null ? null : Convert.FromBase64String(effect.StateBase64);
        if (effect.Vst is null) return new(effect.Id, kind, ProjectFileFormat.ToSettings(effect.Settings), effect.Enabled);
        var descriptor = catalog.FirstOrDefault(item => string.Equals(item.ClassInfo.ClassId, effect.Vst.ClassId, StringComparison.OrdinalIgnoreCase));
        if (descriptor is null)
        {
            warnings.Add($"VST3 fehlt und blieb deaktiviert: {effect.Vst.Name}");
            descriptor = new Vst3EffectDescriptor(effect.Vst.Name, effect.Vst.Vendor, "", new Vst3ClassInfo(effect.Vst.ClassId, "Audio Module Class", effect.Vst.Name, effect.Vst.Vendor, "", "", ""), "");
            return new(effect.Id, kind, ProjectFileFormat.ToSettings(effect.Settings), false, descriptor, state);
        }
        return new(effect.Id, kind, ProjectFileFormat.ToSettings(effect.Settings), effect.Enabled, descriptor, state);
    }

    private static async Task<(long Length, string Hash)> IdentityAsync(string path, CancellationToken token)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Projektquelle darf kein Reparse Point sein.");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var length = stream.Length; var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
        if (new FileInfo(path).Length != length) throw new IOException("Audioquelle wurde während der Prüfung verändert.");
        return (length, hash);
    }

    private static string ResolveSourcePath(string directory, string path)
    {
        if (Path.IsPathFullyQualified(path)) return Path.GetFullPath(path);
        if (path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(part => part == "..")) throw new InvalidDataException("Relative Projektquelle darf das Projektverzeichnis nicht verlassen.");
        var full = Path.GetFullPath(path, directory); if (!IsDescendant(directory, full)) throw new InvalidDataException("Ungültiger Quellenpfad."); return full;
    }
    private static bool IsDescendant(string root, string path) => path.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static string EnsureProjectExtension(string path) => string.Equals(Path.GetExtension(path), ".wbproj", StringComparison.OrdinalIgnoreCase) ? path : path + ".wbproj";
}
