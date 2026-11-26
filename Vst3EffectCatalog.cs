using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using NAudio.Vst3;

namespace WellenEditor;

/// <summary>Ein im Dateisystem gefundenes, ladbares VST3-Effektmodul.</summary>
public sealed record Vst3EffectDescriptor(string Name, string Vendor, string Path, Vst3ClassInfo ClassInfo, string SubCategories)
{
    public string DisplayName => string.IsNullOrWhiteSpace(Vendor) ? Name : $"{Name} — {Vendor}";
}

/// <summary>Fortschritt eines VST3-Scans. <paramref name="Total"/> ist in der Prüf-Phase die Gesamtzahl der Dateien, sonst 0.</summary>
public sealed record Vst3ScanProgress(int Examined, int Found, string CurrentPath, string Phase = "Prüfe", int Total = 0);

/// <summary>Durchsucht Verzeichnisse rekursiv und liest die exportierten Effekt-Klassen aus.</summary>
public static class Vst3EffectCatalog
{
    /// <summary>Alle bekannten Standard-VST3-Ordner plus die Wurzel jedes fixen Laufwerks.</summary>
    public static IReadOnlyList<string> GetDefaultFolders()
    {
        var folders = new HashSet<string>(Vst3PluginScanner.DefaultSearchPaths, StringComparer.OrdinalIgnoreCase);
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
            CollectVst3FilesRecursive(root, files, cancellationToken, progress);
        }

        var ordered = files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        return ScanFiles(ordered, progress, cancellationToken);
    }

    /// <summary>Nur angehakte Ordner, ohne deren bereits angehakte Kinder doppelt einzusammeln.</summary>
    internal static List<string> CollectCheckedPaths(IEnumerable<ScanFolderNode> nodes)
    {
        var acc = new List<string>();
        void Walk(ScanFolderNode node)
        {
            if (node.IsChecked) { acc.Add(node.FullPath); return; } // Kinder eines angehakten Ordners sind im Scan inkludiert
            foreach (var child in node.Children) Walk(child);
        }
        foreach (var node in nodes) Walk(node);
        return acc;
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

    private static void CollectVst3FilesRecursive(string root, HashSet<string> files, CancellationToken cancellationToken, IProgress<Vst3ScanProgress>? progress = null)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        var examined = 0;
        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dir = stack.Pop();
            examined++;
            if (examined % 50 == 0)
                progress?.Report(new Vst3ScanProgress(examined, files.Count, dir, "Durchsuche"));
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
        IReadOnlyList<string> files,
        IProgress<Vst3ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        var result = new List<Vst3EffectDescriptor>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var examined = 0;
        var total = files.Count;
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            examined++;
            progress?.Report(new Vst3ScanProgress(examined, result.Count, file, "Prüfe", total));
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
        progress?.Report(new Vst3ScanProgress(examined, result.Count, "Fertig.", "Fertig", total));
        return result;
    }

    /// <summary>
    /// Löst die echte <see cref="Vst3ClassInfo"/> aus einem geladenen Modul anhand der ClassId (TUID-Hex).
    /// Damit funktioniert CreatePlugin auch mit einer aus dem JSON-Cache rekonstruierten ClassInfo.
    /// </summary>
    public static Vst3ClassInfo ResolveClassInfo(Vst3Module module, string classId)
    {
        foreach (var info in module.GetClasses())
        {
            if (string.Equals(info.ClassId, classId, StringComparison.OrdinalIgnoreCase))
                return info;
        }
        throw new InvalidOperationException($"ClassId '{classId}' wurde im Modul nicht gefunden.");
    }

    // ---------- Persistenz: gefundene Plugins als JSON-Cache ----------

    private sealed class CacheEntry
    {
        public string Name { get; set; } = "";
        public string Vendor { get; set; } = "";
        public string Path { get; set; } = "";
        public string SubCategories { get; set; } = "";
        public string ClassId { get; set; } = "";
        public string Category { get; set; } = "Audio Module Class";
        public string ClassName { get; set; } = "";
        public string ClassVendor { get; set; } = "";
        public string Version { get; set; } = "";
        public string SdkVersion { get; set; } = "";
    }

    private static string CacheFilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     "WaveBenchPro", "vst3-cache.json");

    /// <summary>Lädt den gespeicherten Plugin-Katalog (nur noch vorhandene Dateien).</summary>
    public static IReadOnlyList<Vst3EffectDescriptor> LoadCache()
    {
        try
        {
            var path = CacheFilePath;
            if (!File.Exists(path)) return Array.Empty<Vst3EffectDescriptor>();
            var entries = JsonSerializer.Deserialize<List<CacheEntry>>(File.ReadAllText(path)) ?? new();
            var result = new List<Vst3EffectDescriptor>();
            foreach (var e in entries)
            {
                if (string.IsNullOrWhiteSpace(e.Path) || !File.Exists(e.Path)) continue;
                var classInfo = new Vst3ClassInfo(e.ClassId, e.Category, e.ClassName, e.ClassVendor, e.Version, e.SdkVersion, e.SubCategories);
                result.Add(new Vst3EffectDescriptor(e.Name, e.Vendor, e.Path, classInfo, e.SubCategories));
            }
            return result;
        }
        catch { return Array.Empty<Vst3EffectDescriptor>(); }
    }

    /// <summary>Speichert die Liste dauerhaft (angehakt übernommene und neu gefundene).</summary>
    public static void SaveCache(IEnumerable<Vst3EffectDescriptor> descriptors)
    {
        try
        {
            var path = CacheFilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var entries = descriptors.Select(d => new CacheEntry
            {
                Name = d.Name,
                Vendor = d.Vendor,
                Path = d.Path,
                SubCategories = d.SubCategories,
                ClassId = d.ClassInfo.ClassId,
                Category = d.ClassInfo.Category,
                ClassName = d.ClassInfo.Name,
                ClassVendor = d.ClassInfo.Vendor,
                Version = d.ClassInfo.Version,
                SdkVersion = d.ClassInfo.SdkVersion
            }).ToList();
            File.WriteAllText(path, JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* Cache ist best-effort */ }
    }
}
