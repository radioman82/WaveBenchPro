using System.IO;
using System.Text;
using Xunit;

namespace WellenEditor.Tests;

public sealed class ProjectFileFormatTests
{
    [Fact]
    public void ManifestRoundTripPreservesFramePositionsAndSharedSource()
    {
        var source = Guid.NewGuid();
        var project = new ProjectFileFormat.Manifest(ProjectFileFormat.Version, "Test", 48000, 120,
            [new(source, "audio.wav", 100, new string('a', 64))],
            [new(Guid.NewGuid(), "Spur", 1, 0, false, false,
                [new(Guid.NewGuid(), source, 987654321, 1.25), new(Guid.NewGuid(), source, 17, 1)])]);
        var restored = ProjectFileFormat.Parse(ProjectFileFormat.Serialize(project));
        Assert.Equal(987654321, restored.Tracks[0].Clips[0].StartFrame);
        Assert.Equal(source, restored.Tracks[0].Clips[1].SourceId);
    }

    [Fact]
    public void RejectsNewVersionAndUnknownProperties()
    {
        var newer = Encoding.UTF8.GetBytes("{\"formatVersion\":999,\"name\":\"x\",\"sampleRate\":44100,\"bpm\":120,\"sources\":[],\"tracks\":[]}");
        Assert.Throws<InvalidDataException>(() => ProjectFileFormat.Parse(newer));
        var unknown = Encoding.UTF8.GetBytes("{\"formatVersion\":2,\"name\":\"x\",\"sampleRate\":44100,\"bpm\":120,\"sources\":[],\"tracks\":[],\"future\":true}");
        Assert.Throws<System.Text.Json.JsonException>(() => ProjectFileFormat.Parse(unknown));
    }
}
