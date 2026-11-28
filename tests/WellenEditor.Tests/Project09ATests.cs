using System.IO;
using System.Security.Cryptography;
using NAudio.Wave;
using WellenEditor;
using Xunit;

namespace WellenEditor.Tests;

public sealed class Project09ATests
{
    private sealed class Files : IDisposable
    {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "WB09A-" + Guid.NewGuid().ToString("N"));
        internal string Audio => Path.Combine(Root, "source.wav");
        internal string Project => Path.Combine(Root, "project.wbproj");
        internal readonly AudioDocument Document;
        internal Files()
        {
            Directory.CreateDirectory(Root);
            using (var writer = new WaveFileWriter(Audio, WaveFormat.CreateIeeeFloatWaveFormat(44100, 1)))
                writer.WriteSamples(Enumerable.Range(0, 100).Select(i => i / 100f).ToArray(), 0, 100);
            Document = AudioDocument.Load(Audio);
        }
        internal ProjectWorkspace Workspace() { var w = new ProjectWorkspace(); w.Project.Add(Document); w.MarkDirty(); return w; }
        public void Dispose() { Document.Dispose(); Directory.Delete(Root, true); }
    }
    private static void Dispose(ProjectStorage.LoadedProject loaded) { foreach (var document in loaded.Documents) document.Dispose(); }

    [Fact]
    public async Task ViewAndLoopRoundTripPreservesIdentityAnd64BitFrames()
    {
        using var f = new Files(); using var w = f.Workspace(); var p = w.Project;
        p.SelectionStart = 3000000000; p.SelectionEnd = 3000000100; p.CursorPosition = 3000000050;
        p.ViewportStart = 2999999999; p.ViewportFrames = 8192; p.VerticalZoom = 1.5; p.HorizontalZoom = 128;
        p.LoopPlaybackEnabled = true; p.LoopStartFrame = 3000000000; p.LoopFrameCount = 100;
        await w.SaveAsync(f.Project, ProjectSaveMode.Reference, default);
        var first = ProjectFileFormat.Parse(File.ReadAllBytes(f.Project));
        await w.SaveAsync(f.Project, ProjectSaveMode.Reference, default);
        var second = ProjectFileFormat.Parse(File.ReadAllBytes(f.Project));
        Assert.Equal(first.Sources[0].Id, second.Sources[0].Id);
        var loaded = await ProjectStorage.LoadReferenceAsync(f.Project, default);
        try
        {
            Assert.Equal(p.Id, loaded.Project.Id); Assert.Equal(p.SelectionStart, loaded.Project.SelectionStart);
            Assert.Equal(p.SelectionEnd, loaded.Project.SelectionEnd); Assert.Equal(p.CursorPosition, loaded.Project.CursorPosition);
            Assert.Equal(p.HorizontalZoom, loaded.Project.HorizontalZoom); Assert.Equal(p.VerticalZoom, loaded.Project.VerticalZoom);
            Assert.Equal(p.ViewportStart, loaded.Project.ViewportStart); Assert.Equal(p.ViewportFrames, loaded.Project.ViewportFrames);
            Assert.True(loaded.Project.LoopPlaybackEnabled); Assert.Equal(p.LoopStartFrame, loaded.Project.LoopStartFrame);
        }
        finally { Dispose(loaded); }
    }
    [Fact]
    public void InvalidViewAndUnimplementedClipOffsetsAreRejected()
    {
        var source = Guid.NewGuid();
        var manifest = new ProjectFileFormat.Manifest(3, "test", 44100, 120, [new(source, "source.wav", 10, new string('A', 64))],
            [new(Guid.NewGuid(), "Track", 1, 0, false, false, [new(Guid.NewGuid(), source, 0, 1)])]);
        Assert.Throws<InvalidDataException>(() => ProjectFileFormat.Serialize(manifest with { View = new(SelectionStart: 10, SelectionEnd: 2) }));
        Assert.Throws<InvalidDataException>(() => ProjectFileFormat.Serialize(manifest with { View = new(LoopStartFrame: long.MaxValue, LoopFrameCount: 1) }));
        Assert.Throws<InvalidDataException>(() => ProjectFileFormat.Serialize(manifest with { Tracks = [manifest.Tracks[0] with { Clips = [manifest.Tracks[0].Clips[0] with { SourceOffsetFrames = 1 }] }] }));
    }
    [Fact]
    public async Task LegacyVersionTwoLoadsAndMigratesOnSave()
    {
        using var f = new Files(); using var w = f.Workspace(); await w.SaveAsync(f.Project, ProjectSaveMode.Reference, default);
        var manifest = ProjectFileFormat.Parse(File.ReadAllBytes(f.Project));
        File.WriteAllBytes(f.Project, ProjectFileFormat.Serialize(manifest with { FormatVersion = 2, View = null, ProjectId = null }));
        using var next = new ProjectWorkspace(); var loaded = await next.OpenAsync(f.Project, null, default);
        try { await next.SaveAsync(f.Project, ProjectSaveMode.Reference, default); Assert.Equal(3, ProjectFileFormat.Parse(File.ReadAllBytes(f.Project)).FormatVersion); }
        finally { Dispose(loaded); }
    }
    [Fact]
    public async Task DiskFailureKeepsOldFilePathModeDirtyAndRemovesNewAssets()
    {
        using var f = new Files(); using var w = f.Workspace(); await w.SaveAsync(f.Project, ProjectSaveMode.Reference, default);
        var before = File.ReadAllBytes(f.Project); f.Document.ReplaceFrames(0, [.75f], 1); w.MarkDirty();
        var target = Path.Combine(f.Root, "failed.wbproj");
        await Assert.ThrowsAsync<IOException>(() => w.SaveAsync(target, ProjectSaveMode.Portable, default, new(() => throw new IOException("injected disk full"))));
        Assert.Equal(before, File.ReadAllBytes(f.Project)); Assert.False(File.Exists(target)); Assert.Equal(f.Project, w.Path);
        Assert.Equal(ProjectSaveMode.Reference, w.Mode); Assert.True(w.IsDirty); Assert.False(w.IsBusy);
        Assert.Empty(Directory.EnumerateFiles(f.Root, "*.tmp", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(f.Root, "failed.assets")));
    }
    [Fact]
    public async Task CancellationBeforeCommitKeepsPreviousManifest()
    {
        using var f = new Files(); using var w = f.Workspace(); await w.SaveAsync(f.Project, ProjectSaveMode.Reference, default);
        var before = File.ReadAllBytes(f.Project); using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => w.SaveAsync(f.Project, ProjectSaveMode.Portable, cancellation.Token, new(cancellation.Cancel)));
        Assert.Equal(before, File.ReadAllBytes(f.Project)); Assert.False(w.IsBusy);
    }
    [Fact]
    public async Task SaveCapturesAudioBeforeAwaitAndPreservesLaterDirtyEdits()
    {
        using var f = new Files(); using var w = f.Workspace(); var expected = w.Project.RenderMixdown();
        await w.SaveAsync(f.Project, ProjectSaveMode.Portable, default, new(() => f.Document.ReplaceFrames(0, [-.9f], 1)));
        Assert.True(w.IsDirty);
        var loaded = await ProjectStorage.LoadReferenceAsync(f.Project, default);
        try { Assert.Equal(expected, loaded.Project.RenderMixdown()); } finally { Dispose(loaded); }
        await w.SaveAsync(f.Project, ProjectSaveMode.Portable, default); Assert.False(w.IsDirty);
    }
    [Fact]
    public async Task RepeatedPortableSaveDeduplicatesUnchangedAssetsAndSurvivesMove()
    {
        using var f = new Files(); using var w = f.Workspace(); var sourceHash = SHA256.HashData(File.ReadAllBytes(f.Audio));
        var folder = Path.Combine(f.Root, "portable"); var path = Path.Combine(folder, "project.wbproj");
        await w.SaveAsync(path, ProjectSaveMode.Portable, default); await w.SaveAsync(path, ProjectSaveMode.Portable, default);
        Assert.Single(Directory.EnumerateFiles(Path.Combine(folder, "project.assets")));
        Assert.Equal(sourceHash, SHA256.HashData(File.ReadAllBytes(f.Audio)));
        var moved = Path.Combine(f.Root, "moved"); Directory.Move(folder, moved); File.Delete(f.Audio);
        using var next = new ProjectWorkspace(); var loaded = await next.OpenAsync(Path.Combine(moved, "project.wbproj"), null, default);
        try { Assert.Equal(ProjectSaveMode.Portable, next.Mode); Assert.Equal(100, loaded.Documents[0].FrameCount); } finally { Dispose(loaded); }
    }
    [Fact]
    public async Task RelinkOriginalByHashIsExplicitAndMakesProjectDirtyWithoutChangingManifest()
    {
        using var f = new Files(); using var w = f.Workspace(); await w.SaveAsync(f.Project, ProjectSaveMode.Reference, default);
        var before = File.ReadAllBytes(f.Project); var moved = Path.Combine(f.Root, "moved.wav"); File.Move(f.Audio, moved);
        using var next = new ProjectWorkspace();
        var loaded = await next.OpenAsync(f.Project, (issues, _) => Task.FromResult<IReadOnlyDictionary<Guid, string>?>(issues.ToDictionary(i => i.Id, _ => moved)), default);
        try { Assert.True(next.IsDirty); Assert.True(loaded.Relinked); Assert.Equal(before, File.ReadAllBytes(f.Project)); } finally { Dispose(loaded); }
    }
    [Fact]
    public async Task WrongRelinkAndCancelledDialogPreserveCurrentProject()
    {
        using var f = new Files(); using var w = f.Workspace(); await w.SaveAsync(f.Project, ProjectSaveMode.Reference, default);
        var wrong = Path.Combine(f.Root, "wrong.wav"); var bytes = File.ReadAllBytes(f.Audio); bytes[^1] ^= 1; File.WriteAllBytes(wrong, bytes); File.Delete(f.Audio);
        using var next = new ProjectWorkspace(); var previous = next.Project;
        await Assert.ThrowsAsync<InvalidDataException>(() => next.OpenAsync(f.Project, (issues, _) => Task.FromResult<IReadOnlyDictionary<Guid, string>?>(issues.ToDictionary(i => i.Id, _ => wrong)), default));
        Assert.Same(previous, next.Project); Assert.Null(next.Path); Assert.False(next.IsBusy);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => next.OpenAsync(f.Project, (_, _) => Task.FromResult<IReadOnlyDictionary<Guid, string>?>(null), default));
        Assert.Same(previous, next.Project);
    }
    [Fact]
    public async Task SourceMutationBetweenInspectionAndLoadIsRejected()
    {
        using var f = new Files(); using var w = f.Workspace(); await w.SaveAsync(f.Project, ProjectSaveMode.Reference, default);
        await Assert.ThrowsAsync<InvalidDataException>(() => ProjectStorage.LoadReferenceAsync(f.Project, default, hooks: new(BeforeSourceLoad: () => File.AppendAllText(f.Audio, "mutation"))));
    }
    [Fact]
    public async Task TraversalIsRejectedBeforeRelinkCallback()
    {
        using var f = new Files(); using var w = f.Workspace(); await w.SaveAsync(f.Project, ProjectSaveMode.Reference, default);
        File.WriteAllText(f.Project, File.ReadAllText(f.Project).Replace("source.wav", "../outside.wav")); var invoked = false;
        await Assert.ThrowsAsync<InvalidDataException>(() => ProjectStorage.LoadReferenceAsync(f.Project, default, (_, _) => { invoked = true; return Task.FromResult<IReadOnlyDictionary<Guid, string>?>(null); }));
        Assert.False(invoked);
    }
    [Fact]
    public async Task ExternalSourceModificationDoesNotSilentlyBecomeSavedProjectAudio()
    {
        using var f = new Files(); using var w = f.Workspace(); File.AppendAllText(f.Audio, "changed");
        await Assert.ThrowsAsync<IOException>(() => w.SaveAsync(f.Project, ProjectSaveMode.Reference, default)); Assert.False(File.Exists(f.Project));
    }
    [Fact]
    public async Task RecoveryKeepsThreeGenerationsSkipsCorruptionAndDoesNotChangeOriginal()
    {
        using var f = new Files(); using var w = f.Workspace(); await w.SaveAsync(f.Project, ProjectSaveMode.Reference, default);
        var before = File.ReadAllBytes(f.Project); var root = Path.Combine(f.Root, "recovery");
        for (var i = 0; i < 5; i++) await ProjectRecovery.SaveAsync(w.Project, f.Project, default, root);
        var projectRoot = Path.Combine(root, w.Project.Id.ToString("N")); Assert.Equal(3, Directory.GetDirectories(projectRoot).Length);
        var entry = Assert.Single(ProjectRecovery.Find(root)); Assert.Equal(f.Project, entry.OriginalPath);
        var corrupt = Path.Combine(projectRoot, "zz-corrupt"); Directory.CreateDirectory(corrupt); File.WriteAllText(Path.Combine(corrupt, "recovery.wbproj"), "invalid JSON");
        Assert.Equal(entry.ProjectPath, Assert.Single(ProjectRecovery.Find(root)).ProjectPath);
        Assert.Equal(before, File.ReadAllBytes(f.Project)); Assert.Single(ProjectRecovery.Find(root)); // later is non-destructive
    }
    [Fact]
    public async Task RecoveryRestoreIsDirtyAndLeasesProtectAssetsUntilLastOwnerCloses()
    {
        using var f = new Files(); using var w = f.Workspace(); var root = Path.Combine(f.Root, "recovery");
        await ProjectRecovery.SaveAsync(w.Project, null, default, root); var entry = Assert.Single(ProjectRecovery.Find(root));
        using var next = new ProjectWorkspace(); var loaded = await next.RecoverAsync(entry, null, default);
        var secondLease = ProjectRecovery.Lease(entry);
        try
        {
            Assert.True(next.IsDirty); Assert.Null(next.Path); Assert.Equal(ProjectSaveMode.Portable, next.Mode);
            await ProjectRecovery.ResolveAsync(entry.ProjectId, default, root); Assert.Empty(ProjectRecovery.Find(root)); Assert.True(File.Exists(entry.ProjectPath));
            next.Dispose(); Assert.True(File.Exists(entry.ProjectPath)); secondLease.Dispose(); Assert.False(File.Exists(entry.ProjectPath));
        }
        finally { Dispose(loaded); secondLease.Dispose(); }
    }
    [Fact]
    public async Task CancelledRecoveryDoesNotOfferIncompleteGeneration()
    {
        using var f = new Files(); using var w = f.Workspace(); var root = Path.Combine(f.Root, "recovery"); using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ProjectRecovery.SaveAsync(w.Project, null, cancellation.Token, root)); Assert.Empty(ProjectRecovery.Find(root));
    }
    [Fact]
    public void DuplicateJsonFieldsAreRejected()
    {
        var json = System.Text.Encoding.UTF8.GetBytes("{\"formatVersion\":3,\"formatVersion\":2}");
        Assert.Throws<InvalidDataException>(() => ProjectFileFormat.Parse(json));
    }
    [Fact]
    public async Task ParallelSavesRemainCompleteAndReadable()
    {
        using var f = new Files(); using var w = f.Workspace();
        await Task.WhenAll(Enumerable.Range(0, 4).Select(i => ProjectStorage.SaveAsync(f.Project, w.Project, ProjectSaveMode.Portable, default)));
        var loaded = await ProjectStorage.LoadReferenceAsync(f.Project, default);
        try { Assert.Equal(100, Assert.Single(loaded.Documents).FrameCount); } finally { Dispose(loaded); }
        Assert.Single(Directory.EnumerateFiles(Path.Combine(f.Root, "project.assets")));
    }
    [Fact]
    public void SequencerAndRecoveryDialogCanBeConstructedAndInspectedWithoutShowingWindows()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                var p = new MultitrackProject { SelectionStart = 100, SelectionEnd = 500, CursorPosition = 200, HorizontalZoom = 128, VerticalZoom = 1.5 };
                var view = new SequencerView(); view.SetProject(p); Assert.Equal((100L, 400L), view.GetPlaybackRange()); view.CaptureProjectView();
                Assert.Equal(128, p.HorizontalZoom); Assert.Equal(1.5, p.VerticalZoom); Assert.Equal(200, p.CursorPosition);
                var candidate = new ProjectRecovery.Entry(Guid.NewGuid(), "unused.wbproj", "Test", DateTime.UtcNow, null);
                var dialog = new ProjectRecoveryDialog(null, [candidate]);
                Assert.Equal(RecoveryChoice.Later, dialog.Choice); Assert.Equal(candidate, dialog.Selected); Assert.False(dialog.IsVisible);
                dialog.Close();
            }
            catch (Exception exception) { error = exception; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(15)));
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }
}
