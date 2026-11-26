using System.IO;
using FluentAssertions;
using Xunit;

namespace WellenEditor.Tests;

public sealed class ExportPathResolverTests
{
    [Fact]
    public void RelativeExportUsesSourceFolderInsteadOfLauncherWorkingDirectory()
    {
        var sourceFolder = Path.Combine(Path.GetTempPath(), "WaveBenchExportPathTest", "sources");
        var source = Path.Combine(sourceFolder, "source.wav");
        ExportPathResolver.Resolve("copy.wav", source).Should().Be(Path.Combine(sourceFolder, "copy.wav"));
    }

    [Fact]
    public void ExplicitDestinationKeepsItsOwnFolder()
    {
        var destination = Path.Combine(Path.GetTempPath(), "WaveBenchExportPathTest", "exports", "copy.wav");
        var source = Path.Combine(Path.GetTempPath(), "WaveBenchExportPathTest", "sources", "source.wav");
        ExportPathResolver.Resolve(destination, source).Should().Be(destination);
    }

    [Fact]
    public void UnsavedDocumentUsesUserFolderInsteadOfLauncherWorkingDirectory()
    {
        var expected = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrWhiteSpace(expected)) expected = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        ExportPathResolver.Resolve("copy.wav", null).Should().Be(Path.Combine(expected, "copy.wav"));
    }
}
