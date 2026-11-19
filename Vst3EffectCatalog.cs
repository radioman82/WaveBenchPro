using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using NAudio.Vst3;

namespace WellenEditor;

/// <summary>Ein im Dateisystem gefundenes, ladbares VST3-Effektmodul.</summary>
public sealed record Vst3EffectDescriptor(string Name, string Vendor, string Path, Vst3ClassInfo ClassInfo, string SubCategories)
{
    public string DisplayName => string.IsNullOrWhiteSpace(Vendor) ? Name : $"{Name} — {Vendor}";
}

/// <summary>Fortschritt eines VST3-Scans.</summary>
public sealed record Vst3ScanProgress(int Examined, int Found, string CurrentPath);

/// <summary>Durchsucht Verzeichnisse rekursiv und liest die exportierten Effekt-Klassen aus.</summary>
public static class Vst3EffectCatalog
{
    private static readonly string[] UserFolders =
    {
        @"G:\vst3", @"G:\vst3-64", @"G:\vst-plugins", @"G:\vst-plugins\VST3"
    };

    /// <summary>Alle bekannten Standard-VST3-Ordner plus die Wurzel jedes fixen Laufwerks.</summary>
    public static IReadOnlyList<string> GetDefaultFolders()
    {
        var folders = new HashSet<string>(Vst3PluginScanner.DefaultSearchPaths, StringComparer.OrdinalIgnoreCase);
        foreach (var folder in UserFolders) folders.Add(folder);
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed))
        {
            var root = drive.RootDirectory.FullName;
            folders.Add(root);
            folders.Add(Path.Combine(root, "Program Files", "Common Files", "VST3"));
        }
        return folders.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Scannt die bekannten Ordner (Rückwärtskompatibilität zum Start-Scan).</summary>
    public static IReadOnlyList<Vst3EffectDescriptor> Scan() => Scan(GetDefaultFolders());

    /// <summary>Scannt eine Liste von Startordnern rekursiv nach *.vst3.</summary>
    public static IReadOnlyList<Vst3EffectDescriptor> Scan(
        IEnumerable<string> roots,
        IProgress<Vst3ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;
            CollectVst3FilesRecursive(root, files, cancellationToken);
        }

        return ScanFiles(files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase), progress, cancellationToken);
    }

    /// <summary>Scannt eine einzelne Datei oder ein Verzeichnis (vom Verzeichnisbaum-Dialog).</summary>
    public static IReadOnlyList<Vst3EffectDescriptor> ScanPath(
        string path,
        IProgress<Vst3ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path)) return Array.Empty<Vst3EffectDescriptor>();
        if (File.Exists(path) && path.EndsWith(".vst3", StringComparison.OrdinalIgnoreCase))
            return ScanFiles(new[] { path }, progress, cancellationToken);
        return Scan(new[] { path }, progress, cancellationToken);
    }

    private static void CollectVst3FilesRecursive(string root, HashSet<string> files, CancellationToken cancellationToken)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dir = stack.Pop();
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir, "*.vst3", SearchOption.TopDirectoryOnly))
                    files.Add(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Nicht lesbarer Ordner – einfach überspringen, andere weiterverfolgen.
            }
            try
            {
                foreach (var sub in Directory.EnumerateDirectories(dir))
                    stack.Push(sub);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Nicht lesbare Unterordner überspringen.
            }
        }
    }

    private static IReadOnlyList<Vst3EffectDescriptor> ScanFiles(
        IEnumerable<string> files,
        IProgress<Vst3ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        var result = new List<Vst3EffectDescriptor>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var examined = 0;
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            examined++;
            progress?.Report(new Vst3ScanProgress(examined, result.Count, file));
            try
            {
                using var loaded = Vst3Module.Load(file);
                foreach (var info in loaded.GetClasses())
                {
                    if (!string.Equals(info.Category, "Audio Module Class", StringComparison.OrdinalIgnoreCase)) continue;
                    if ((info.SubCategories ?? string.Empty).Contains("Instrument", StringComparison.OrdinalIgnoreCase)) continue;
                    var key = $"{file}|{info.Name}|{info.Vendor}";
                    if (!seen.Add(key)) continue;
                    result.Add(new Vst3EffectDescriptor(
                        string.IsNullOrWhiteSpace(info.Name) ? Path.GetFileNameWithoutExtension(file) : info.Name,
                        info.Vendor ?? string.Empty,
                        file,
                        info,
                        info.SubCategories ?? string.Empty));
                }
            }
            catch (Exception ex) when (ex is BadImageFormatException or FileNotFoundException or DllNotFoundException
                                       or InvalidOperationException or UnauthorizedAccessException or IOException)
            {
                // 32-Bit-Plugins im 64-Bit-Host und beschädigte Bundles werden übersprungen.
            }
        }
        progress?.Report(new Vst3ScanProgress(examined, result.Count, "Fertig."));
        return result;
    }
}