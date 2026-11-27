using System.IO;
using NAudio.Wave;
using Xunit;

namespace WellenEditor.Tests;

public sealed class ProjectStorageTests
{
    [Fact]
    public async Task ReferenceRoundTripPreservesSharedSourceAndRenderedAudio()
    {
        var directory = Path.Combine(Path.GetTempPath(), "wavebench-project-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var sourcePath = Path.Combine(directory, "source.wav");
        var projectPath = Path.Combine(directory, "project.wbproj");
        try
        {
            using (var writer = new WaveFileWriter(sourcePath, WaveFormat.CreateIeeeFloatWaveFormat(44100, 1)))
                writer.WriteSamples(Enumerable.Range(0, 500).Select(i => (float)Math.Sin(i * .1)).ToArray(), 0, 500);
            using var source = AudioDocument.Load(sourcePath);
            var project = MultitrackProject.Restore(44100, 130);
            var track = project.AddEmptyTrack();
            track.SetClip(source, 5); track.SetClip(source, 100);
            track.Clips[1].DurationScale = 1.25;
            track.Gain = .7; track.Pan = -.2;
            track.SetEffects(new[] { new TrackEffectSlot(Guid.NewGuid(), NativeEffectKind.Gain, new EffectSettings { GainDb = -2 }) });
            var expected = project.RenderMixdown();
            await ProjectStorage.SaveReferenceAsync(projectPath, project, CancellationToken.None);
            var loaded = await ProjectStorage.LoadReferenceAsync(projectPath, CancellationToken.None);
            try
            {
                Assert.Single(loaded.Documents);
                Assert.Same(loaded.Project.Tracks[0].Clips[0].Source, loaded.Project.Tracks[0].Clips[1].Source);
                Assert.Equal(expected, loaded.Project.RenderMixdown());
                Assert.Single(loaded.Project.Tracks[0].Effects);
            }
            finally { foreach (var document in loaded.Documents) document.Dispose(); }
            await File.AppendAllTextAsync(sourcePath, "changed");
            await Assert.ThrowsAsync<InvalidDataException>(() => ProjectStorage.LoadReferenceAsync(projectPath, CancellationToken.None));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task PortableSaveKeepsModifiedAudioAfterOriginalIsRemoved()
    {
        var directory = Path.Combine(Path.GetTempPath(), "wavebench-portable-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var sourcePath = Path.Combine(directory, "source.wav");
        var projectPath = Path.Combine(directory, "portable.wbproj");
        try
        {
            using (var writer = new WaveFileWriter(sourcePath, WaveFormat.CreateIeeeFloatWaveFormat(44100, 1))) writer.WriteSamples(new float[200], 0, 200);
            using var source = AudioDocument.Load(sourcePath);
            source.ReplaceFrames(0, Enumerable.Repeat(.25f, 20).ToArray(), 20);
            var project = new MultitrackProject(); project.Add(source);
            await ProjectStorage.SaveAsync(projectPath, project, ProjectSaveMode.Portable, CancellationToken.None);
            File.Delete(sourcePath);
            var loaded = await ProjectStorage.LoadReferenceAsync(projectPath, CancellationToken.None);
            try { Assert.Single(loaded.Documents); Assert.True(loaded.Project.Tracks[0].Clips[0].Source.IsModified == false); }
            finally { foreach (var document in loaded.Documents) document.Dispose(); }
        }
        finally { Directory.Delete(directory, true); }
    }
}
