using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NAudio.Vst3;

namespace WellenEditor;

/// <summary>Ein im Dateisystem gefundenes, ladbares VST3-Effektmodul.</summary>
public sealed record Vst3EffectDescriptor(string Name, string Vendor, string Path, Vst3ClassInfo ClassInfo, string SubCategories)
{
    public string DisplayName => string.IsNullOrWhiteSpace(Vendor) ? Name : $"{Name} — {Vendor}";
}

/// <summary>Durchsucht die VST3-Ordner und liest die exportierten Effekt-Klassen aus.</summary>
public static class Vst3EffectCatalog
{
    private static readonly string[] UserFolders =
    {
        @"G:\vst3",
        @"G:\vst3-64",
        @"G:\vst-plugins",
        @"G:\vst-plugins\VST3"
    };

    public static IReadOnlyList<Vst3EffectDescriptor> Scan()
    {
        var folders = UserFolders.Concat(Vst3PluginScanner.DefaultSearchPaths);
        var modules = new Dictionary<string, Vst3ModuleInfo>(StringComparer.OrdinalIgnoreCase);

        foreach (var folder in folders)
        {
            foreach (var module in Vst3PluginScanner.EnumerateIn(folder))
            {
                modules.TryAdd(module.Path, module);
            }
        }

        var result = new List<Vst3EffectDescriptor>();
        foreach (var module in modules.Values.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                using var loaded = Vst3Module.Load(module.Path);
                foreach (var info in loaded.GetClasses())
                {
                    if (!string.Equals(info.Category, "Audio Module Class", StringComparison.OrdinalIgnoreCase)) continue;
                    if ((info.SubCategories ?? string.Empty).Contains("Instrument", StringComparison.OrdinalIgnoreCase)) continue;
                    result.Add(new Vst3EffectDescriptor(
                        string.IsNullOrWhiteSpace(info.Name) ? (module.Name ?? module.Path) : info.Name,
                        info.Vendor ?? string.Empty,
                        module.Path,
                        info,
                        info.SubCategories ?? string.Empty));
                }
            }
            catch (Exception ex) when (ex is BadImageFormatException or FileNotFoundException or DllNotFoundException or InvalidOperationException or UnauthorizedAccessException)
            {
                // 32-Bit-Plugins in einem 64-Bit-Host und beschädigte Bundles werden übersprungen.
            }
        }

        return result;
    }
}
