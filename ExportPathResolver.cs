using System.IO;

namespace WellenEditor;

internal static class ExportPathResolver
{
    internal static string DirectoryFor(string? sourcePath)
    {
        if (!string.IsNullOrWhiteSpace(sourcePath))
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(sourcePath));
            if (!string.IsNullOrWhiteSpace(directory)) return directory;
        }
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return string.IsNullOrWhiteSpace(documents)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : documents;
    }

    internal static string Resolve(string target, string? sourcePath) =>
        Path.GetFullPath(target, DirectoryFor(sourcePath));
}
