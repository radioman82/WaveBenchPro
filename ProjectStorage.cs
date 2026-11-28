using System.IO;
using System.Security.Cryptography;
using NAudio.Vst3;

namespace WellenEditor;

internal enum ProjectSaveMode { Reference, Portable }

/// <summary>Atomic project persistence. Asset writes finish before the manifest is replaced.</summary>
internal static class ProjectStorage
{
    private static readonly SemaphoreSlim SaveGate = new(1, 1);
    internal sealed record LoadedProject(MultitrackProject Project, IReadOnlyList<AudioDocument> Documents, IReadOnlyList<string> Warnings,
        bool Relinked = false, bool Portable = false, bool LegacyIdentity = false);
    internal sealed record SourceIssue(Guid Id, string Path, string Reason, long LengthBytes, string Sha256);
    internal delegate Task<IReadOnlyDictionary<Guid, string>?> RelinkSources(IReadOnlyList<SourceIssue> issues, CancellationToken token);
    internal sealed record IoHooks(Action? BeforeManifestCommit = null, Action? BeforeSourceLoad = null);

    internal static Task SaveReferenceAsync(string path, MultitrackProject project, CancellationToken cancellationToken) =>
        SaveAsync(path, project, ProjectSaveMode.Reference, cancellationToken);

    internal static async Task SaveAsync(string path, MultitrackProject project, ProjectSaveMode mode, CancellationToken cancellationToken, IoHooks? hooks = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        var fullPath = EnsureProjectExtension(Path.GetFullPath(path));
        var directory = Path.GetDirectoryName(fullPath)!;
        var snapshot = project.Snapshot(); // UI-side immutable routing snapshot; the audio itself is written/read below.
        var documents = snapshot.Tracks.SelectMany(track => track.Clips).Select(clip => clip.Source).Distinct().ToArray();
        var captures = new Dictionary<AudioDocument, IAudioSource>();
        EnsureNoReparse(directory);
        Directory.CreateDirectory(directory);
        var entered = false;
        string? temporary = null;
        var created = new List<string>();
        var committed = false;
        try
        {
            foreach (var document in documents)
            {
                if (document.OriginalSourceChanged) throw new IOException("Eine Originalquelle wurde außerhalb des Editors verändert. Bitte die Datei vor dem Speichern neu öffnen: " + Path.GetFileName(document.SourcePath));
                if (mode == ProjectSaveMode.Portable || document.IsModified || string.IsNullOrWhiteSpace(document.SourcePath) || !File.Exists(document.SourcePath))
                    captures.Add(document, document.CaptureProjectSource());
            }
            await SaveGate.WaitAsync(cancellationToken).ConfigureAwait(false); entered = true;
            var sourceIds = documents.ToDictionary(document => document, snapshot.SourceId);
            var assets = Path.Combine(directory, Path.GetFileNameWithoutExtension(fullPath) + ".assets");
            var sources = new List<ProjectFileFormat.Source>();
            foreach (var document in documents)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var useAsset = captures.ContainsKey(document);
                string sourcePath;
                if (useAsset)
                {
                    EnsureNoReparse(assets); Directory.CreateDirectory(assets);
                    var assetName = sourceIds[document].ToString("N") + ".wav";
                    sourcePath = Path.Combine(assets, assetName);
                    // Assets are owned by this project and are atomically replaced individually.
                    var pending = sourcePath + ".tmp-" + Guid.NewGuid().ToString("N");
                    try
                    {
                        await Task.Run(() => WriteCapturedWave(pending, captures[document], cancellationToken), cancellationToken).ConfigureAwait(false);
                        var assetIdentity = await IdentityAsync(pending, cancellationToken).ConfigureAwait(false);
                        sourcePath = Path.Combine(assets, "wbp-" + assetIdentity.Hash.ToLowerInvariant() + ".wav");
                        if (!File.Exists(sourcePath)) { File.Move(pending, sourcePath); created.Add(sourcePath); }
                        else if (await IdentityAsync(sourcePath, cancellationToken).ConfigureAwait(false) != assetIdentity)
                            throw new IOException("Vorhandenes Projektasset hat unerwarteten Inhalt.");
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
                sources.ToArray(), tracks, View: ToManifestView(snapshot), ProjectId: snapshot.Id, Portable: mode == ProjectSaveMode.Portable);
            var bytes = ProjectFileFormat.Serialize(manifest);
            temporary = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false); stream.Flush(true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            hooks?.BeforeManifestCommit?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            EnsureNoReparse(fullPath);
            File.Move(temporary, fullPath, true); temporary = null; committed = true;
        }
        finally
        {
            if (temporary is not null && File.Exists(temporary)) File.Delete(temporary);
            if (!committed) foreach (var asset in created) { try { File.Delete(asset); } catch (IOException) { } }
            foreach (var source in captures.Values) source.Dispose();
            if (entered) SaveGate.Release();
        }
    }

    internal static async Task<LoadedProject> LoadReferenceAsync(string path, CancellationToken cancellationToken,
        RelinkSources? relink = null, IoHooks? hooks = null)
    {
        var fullPath = Path.GetFullPath(path); var directory = Path.GetDirectoryName(fullPath)!;
        EnsureNoReparse(fullPath);
        byte[] bytes;
        await using (var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            if (stream.Length > ProjectFileFormat.MaximumBytes) throw new InvalidDataException("Projektdatei ist zu groß.");
            bytes = new byte[checked((int)stream.Length)]; await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        }
        var manifest = ProjectFileFormat.Parse(bytes); var documents = new Dictionary<Guid, AudioDocument>(); var warnings = new List<string>();
        var paths = new Dictionary<Guid, string>(); var issues = new List<SourceIssue>();
        foreach (var source in manifest.Sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var resolved = ResolveSourcePath(directory, source.Path); // reject traversal before offering a dialog
            paths[source.Id] = resolved;
            if (!File.Exists(resolved)) issues.Add(new(source.Id, resolved, "Datei fehlt", source.LengthBytes, source.Sha256));
            else
            {
                var identity = await IdentityAsync(resolved, cancellationToken).ConfigureAwait(false);
                if (identity.Length != source.LengthBytes || !identity.Hash.Equals(source.Sha256, StringComparison.OrdinalIgnoreCase))
                    issues.Add(new(source.Id, resolved, "Datei wurde verändert", source.LengthBytes, source.Sha256));
            }
        }
        var relinked = issues.Count > 0;
        if (relinked)
        {
            if (relink is null)
            {
                var first = issues[0];
                if (!File.Exists(first.Path)) throw new FileNotFoundException("Projektquelle fehlt: " + Path.GetFileName(first.Path), first.Path);
                throw new InvalidDataException("Projektquelle wurde verändert: " + Path.GetFileName(first.Path));
            }
            var choices = await relink(issues, cancellationToken).ConfigureAwait(false)
                ?? throw new OperationCanceledException("Quellenzuordnung abgebrochen.", cancellationToken);
            if (choices.Keys.Any(id => !issues.Any(issue => issue.Id == id))) throw new InvalidDataException("Unbekannte Quellenzuordnung.");
            foreach (var issue in issues)
                paths[issue.Id] = choices.TryGetValue(issue.Id, out var chosen) ? Path.GetFullPath(chosen)
                    : throw new InvalidDataException("Nicht alle Quellen wurden zugeordnet.");
        }
        hooks?.BeforeSourceLoad?.Invoke();
        var guards = new List<FileStream>();
        try
        {
            foreach (var source in manifest.Sources)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sourcePath = paths[source.Id];
                if (!File.Exists(sourcePath)) throw new FileNotFoundException($"Projektquelle fehlt: {Path.GetFileName(sourcePath)}", sourcePath);
                EnsureNoReparse(sourcePath);
                guards.Add(new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read));
                var identity = await IdentityAsync(sourcePath, cancellationToken).ConfigureAwait(false);
                if (identity.Length != source.LengthBytes || !string.Equals(identity.Hash, source.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Projektquelle wurde verändert: {Path.GetFileName(sourcePath)}");
                documents.Add(source.Id, AudioDocument.Load(sourcePath));
            }
            var project = MultitrackProject.Restore(manifest.SampleRate, manifest.Bpm, manifest.Name, manifest.ProjectId);
            foreach (var source in manifest.Sources) project.RegisterSource(documents[source.Id], source.Id);
            if (manifest.View is { } view)
            {
                project.SelectionStart = view.SelectionStart; project.SelectionEnd = view.SelectionEnd; project.CursorPosition = view.CursorFrame;
                project.VerticalZoom = view.VerticalZoom; project.ViewportStart = view.ViewportStart; project.ViewportFrames = view.ViewportFrames;
                project.HorizontalZoom = view.HorizontalZoom;
                project.LoopPlaybackEnabled = view.LoopPlaybackEnabled; project.LoopStartFrame = view.LoopStartFrame; project.LoopFrameCount = view.LoopFrameCount;
            }
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
            return new(project, documents.Values.ToArray(), warnings, relinked, manifest.Portable, manifest.ProjectId is null);
        }
        catch { foreach (var document in documents.Values) document.Dispose(); throw; }
        finally { foreach (var guard in guards) guard.Dispose(); }
    }

    internal static async Task ValidateRelinksAsync(IReadOnlyList<SourceIssue> issues, IReadOnlyDictionary<Guid, string> choices, CancellationToken token)
    {
        foreach (var issue in issues)
        {
            if (!choices.TryGetValue(issue.Id, out var path)) throw new InvalidDataException("Nicht alle Quellen sind zugeordnet.");
            var identity = await IdentityAsync(Path.GetFullPath(path), token).ConfigureAwait(false);
            if (identity.Length != issue.LengthBytes || !identity.Hash.Equals(issue.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Die gewählte Datei ist nicht die unveränderte Originalquelle: " + Path.GetFileName(path));
        }
    }
    private static ProjectFileFormat.Effect ToManifestEffect(TrackEffectSlot effect) => new(effect.Id, effect.Kind.ToString(), effect.Enabled,
        ProjectFileFormat.From(effect.Settings), effect.Vst is null ? null : new(effect.Vst.ClassInfo.ClassId, effect.Vst.Name, effect.Vst.Vendor),
        effect.VstState is null ? null : Convert.ToBase64String(effect.VstState));

    private static ProjectFileFormat.View ToManifestView(MultitrackProject project) => new(
        project.SelectionStart, project.SelectionEnd, project.CursorPosition,
        project.VerticalZoom, project.ViewportStart, project.ViewportFrames,
        project.LoopPlaybackEnabled, project.LoopStartFrame, project.LoopFrameCount, project.HorizontalZoom);

    private static void WriteCapturedWave(string path, IAudioSource source, CancellationToken token)
    {
        if (source.FrameCount > (uint.MaxValue - 128L) / (source.Channels * 4L))
            throw new NotSupportedException("Das Projektasset überschreitet die RIFF/WAV-Grenze; RF64 ist nicht Teil dieses Projektformats.");
        using var writer = new NAudio.Wave.WaveFileWriter(path, NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(source.SampleRate, source.Channels));
        var buffer = new float[8192 * source.Channels];
        for (long position = 0; position < source.FrameCount;)
        {
            token.ThrowIfCancellationRequested();
            var read = source.ReadFrames(position, buffer, 0, (int)Math.Min(8192, source.FrameCount - position));
            if (read <= 0) throw new EndOfStreamException("Projektquelle endet unerwartet.");
            writer.WriteSamples(buffer, 0, read * source.Channels); position += read;
        }
    }

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
        EnsureNoReparse(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var length = stream.Length; var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
        if (new FileInfo(path).Length != length) throw new IOException("Audioquelle wurde während der Prüfung verändert.");
        return (length, hash);
    }

    private static string ResolveSourcePath(string directory, string path)
    {
        if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal) || (path.Length > 2 && path.IndexOf(':', 2) >= 0))
            throw new InvalidDataException("Netzwerk-/Gerätepfade und alternative Datenströme werden nicht automatisch als Projektquelle geöffnet.");
        if (Path.IsPathFullyQualified(path)) return Path.GetFullPath(path);
        if (path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(part => part == "..")) throw new InvalidDataException("Relative Projektquelle darf das Projektverzeichnis nicht verlassen.");
        var full = Path.GetFullPath(path, directory); if (!IsDescendant(directory, full)) throw new InvalidDataException("Ungültiger Quellenpfad."); return full;
    }
    private static bool IsDescendant(string root, string path) => path.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    internal static void EnsureNoReparse(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Projektpfad darf keinen Reparse Point enthalten.");
    }
    private static string EnsureProjectExtension(string path) => string.Equals(Path.GetExtension(path), ".wbproj", StringComparison.OrdinalIgnoreCase) ? path : path + ".wbproj";
}
