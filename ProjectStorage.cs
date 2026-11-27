using System.IO;
using System.Security.Cryptography;

namespace WellenEditor;

// Internal reference-mode implementation. It is deliberately not wired to the UI
// until edited assets, effect state and recovery can all be restored safely.
internal static class ProjectStorage
{
    private static readonly SemaphoreSlim SaveGate = new(1, 1);

    internal sealed record LoadedProject(MultitrackProject Project, IReadOnlyList<AudioDocument> Documents);

    internal static async Task SaveReferenceAsync(string path, MultitrackProject project, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        var snapshot = project.Snapshot();
        if (snapshot.Tracks.Any(track => track.Effects.Length != 0))
            throw new NotSupportedException("Projekt-Effektketten können noch nicht sicher gespeichert werden.");
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var documents = snapshot.Tracks.SelectMany(track => track.Clips).Select(clip => clip.Source).Distinct().ToArray();
        if (documents.Any(document => document.IsModified || string.IsNullOrWhiteSpace(document.SourcePath)))
            throw new NotSupportedException("Bearbeitete oder ungespeicherte Quellen benötigen dauerhafte Projekt-Assets.");

        await SaveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? temporary = null;
        try
        {
            var sourceIds = documents.ToDictionary(document => document, _ => Guid.NewGuid());
            var sources = new List<ProjectFileFormat.Source>();
            foreach (var document in documents)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sourcePath = Path.GetFullPath(document.SourcePath);
                await using var stream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                    81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
                sources.Add(new(sourceIds[document], Path.GetRelativePath(directory, sourcePath), stream.Length, hash));
            }
            var tracks = snapshot.Tracks.Select(track => new ProjectFileFormat.Track(Guid.NewGuid(), track.Name,
                track.Gain, track.Pan, track.Muted, track.Solo,
                track.Clips.Select(clip => new ProjectFileFormat.Clip(Guid.NewGuid(), sourceIds[clip.Source],
                    clip.StartFrame, clip.DurationScale)).ToArray())).ToArray();
            var manifest = new ProjectFileFormat.Manifest(ProjectFileFormat.Version, Path.GetFileNameWithoutExtension(path),
                snapshot.SampleRate, snapshot.Bpm, sources.ToArray(), tracks);
            var bytes = ProjectFileFormat.Serialize(manifest);
            temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                81920, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, Path.GetFullPath(path), true);
            temporary = null;
        }
        finally
        {
            if (temporary is not null && File.Exists(temporary)) File.Delete(temporary);
            SaveGate.Release();
        }
    }

    internal static async Task<LoadedProject> LoadReferenceAsync(string path, CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        byte[] bytes;
        await using (var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            if (stream.Length > ProjectFileFormat.MaximumBytes) throw new InvalidDataException("Projektdatei ist zu groß.");
            bytes = new byte[checked((int)stream.Length)];
            await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        }
        var manifest = ProjectFileFormat.Parse(bytes);
        var directory = Path.GetDirectoryName(fullPath)!;
        var documents = new Dictionary<Guid, AudioDocument>();
        try
        {
            foreach (var source in manifest.Sources)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sourcePath = Path.GetFullPath(source.Path, directory);
                await using (var stream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                    81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
                    if (stream.Length != source.LengthBytes || !string.Equals(hash, source.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException($"Projektquelle wurde verändert: {Path.GetFileName(sourcePath)}");
                }
                documents.Add(source.Id, AudioDocument.Load(sourcePath));
            }
            var project = MultitrackProject.Restore(manifest.SampleRate, manifest.Bpm);
            foreach (var savedTrack in manifest.Tracks)
            {
                var track = project.AddEmptyTrack();
                track.Name = savedTrack.Name; track.Gain = savedTrack.Gain; track.Pan = savedTrack.Pan;
                track.Muted = savedTrack.Muted; track.Solo = savedTrack.Solo;
                foreach (var savedClip in savedTrack.Clips)
                {
                    track.SetClip(documents[savedClip.SourceId], savedClip.StartFrame);
                    track.Clips[^1].DurationScale = savedClip.DurationScale;
                    _ = track.EndFrame;
                }
            }
            return new(project, documents.Values.ToArray());
        }
        catch
        {
            foreach (var document in documents.Values) document.Dispose();
            throw;
        }
    }
}
