using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WellenEditor;

/// <summary>Strict, bounded on-disk representation of a WaveBench multitrack project.</summary>
internal static class ProjectFileFormat
{
    internal const int Version = 2;
    internal const int MaximumBytes = 4 * 1024 * 1024;
    internal const int MaximumPluginStateBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
        MaxDepth = 32
    };

    internal sealed record Manifest(int FormatVersion, string Name, int SampleRate, double Bpm,
        Source[] Sources, Track[] Tracks, int BeatsPerBar = 4, int BeatUnit = 4);
    internal sealed record Source(Guid Id, string Path, long LengthBytes, string Sha256, bool IsAsset = false);
    internal sealed record Track(Guid Id, string Name, double Gain, double Pan, bool Muted, bool Solo,
        Clip[] Clips, Effect[]? Effects = null);
    internal sealed record Clip(Guid Id, Guid SourceId, long StartFrame, double DurationScale,
        long SourceOffsetFrames = 0, long? SourceLengthFrames = null);
    internal sealed record Effect(Guid Id, string Kind, bool Enabled, Settings Settings,
        Vst? Vst = null, string? StateBase64 = null);
    internal sealed record Settings(double GainDb, double Frequency, double Q, double ThresholdDb,
        double Ratio, double AttackMs, double ReleaseMs, double MakeupDb);
    // A VST reference is identification only. The loader resolves it from the local scanned cache;
    // a project never supplies a DLL/bundle path to execute.
    internal sealed record Vst(string ClassId, string Name, string Vendor);

    internal static byte[] Serialize(Manifest manifest)
    {
        Validate(manifest);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, Json);
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("Projektdatei ist zu groß.");
        return bytes;
    }

    internal static Manifest Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("Projektdatei ist zu groß.");
        var manifest = JsonSerializer.Deserialize<Manifest>(bytes, Json)
            ?? throw new InvalidDataException("Projektdatei ist leer.");
        Validate(manifest);
        return manifest;
    }

    internal static Settings From(EffectSettings s) => new(s.GainDb, s.Frequency, s.Q, s.ThresholdDb, s.Ratio, s.AttackMs, s.ReleaseMs, s.MakeupDb);
    internal static EffectSettings ToSettings(Settings s) => new()
    {
        GainDb = s.GainDb, Frequency = s.Frequency, Q = s.Q, ThresholdDb = s.ThresholdDb,
        Ratio = s.Ratio, AttackMs = s.AttackMs, ReleaseMs = s.ReleaseMs, MakeupDb = s.MakeupDb
    };

    private static void Validate(Manifest value)
    {
        if (value.FormatVersion != Version) throw new InvalidDataException("Nicht unterstützte Projektversion.");
        if (string.IsNullOrWhiteSpace(value.Name) || value.Name.Length > 256 || value.SampleRate is < 8000 or > 384000 ||
            !double.IsFinite(value.Bpm) || value.Bpm is < 30 or > 300 || value.BeatsPerBar is < 1 or > 32 ||
            value.BeatUnit is not (1 or 2 or 4 or 8 or 16) || value.Sources is null || value.Tracks is null ||
            value.Sources.Length > 10000 || value.Tracks.Length > 1000)
            throw new InvalidDataException("Ungültige Projekteinstellungen.");
        var ids = new HashSet<Guid>(); var sourceIds = new HashSet<Guid>();
        foreach (var source in value.Sources)
        {
            if (source is null || source.Id == Guid.Empty || !ids.Add(source.Id) || string.IsNullOrWhiteSpace(source.Path) ||
                source.Path.Length > 32767 || source.LengthBytes < 0 || source.Sha256 is null || source.Sha256.Length != 64 ||
                !source.Sha256.All(Uri.IsHexDigit) || Uri.TryCreate(source.Path, UriKind.Absolute, out var uri) && !uri.IsFile)
                throw new InvalidDataException("Ungültige Projektquelle.");
            sourceIds.Add(source.Id);
        }
        var clips = 0;
        foreach (var track in value.Tracks)
        {
            if (track is null || track.Id == Guid.Empty || !ids.Add(track.Id) || string.IsNullOrWhiteSpace(track.Name) || track.Name.Length > 256 ||
                !double.IsFinite(track.Gain) || track.Gain is < 0 or > 4 || !double.IsFinite(track.Pan) || track.Pan is < -1 or > 1 ||
                track.Muted && track.Solo || track.Clips is null || track.Effects is { Length: > 128 })
                throw new InvalidDataException("Ungültige Projektspur.");
            clips = checked(clips + track.Clips.Length); if (clips > 100000) throw new InvalidDataException("Zu viele Clips.");
            foreach (var clip in track.Clips)
                if (clip is null || clip.Id == Guid.Empty || !ids.Add(clip.Id) || !sourceIds.Contains(clip.SourceId) || clip.StartFrame < 0 ||
                    clip.SourceOffsetFrames < 0 || clip.SourceLengthFrames is < 0 || !double.IsFinite(clip.DurationScale) || clip.DurationScale is <= 0 or > 100)
                    throw new InvalidDataException("Ungültiger Projektclip.");
            foreach (var effect in track.Effects ?? Array.Empty<Effect>()) ValidateEffect(effect, ids);
        }
    }

    private static void ValidateEffect(Effect effect, HashSet<Guid> ids)
    {
        if (effect is null || effect.Id == Guid.Empty || !ids.Add(effect.Id) || !Enum.TryParse<NativeEffectKind>(effect.Kind, out _) || effect.Settings is null ||
            !double.IsFinite(effect.Settings.GainDb) || !double.IsFinite(effect.Settings.Frequency) || !double.IsFinite(effect.Settings.Q) ||
            !double.IsFinite(effect.Settings.ThresholdDb) || !double.IsFinite(effect.Settings.Ratio) || !double.IsFinite(effect.Settings.AttackMs) ||
            !double.IsFinite(effect.Settings.ReleaseMs) || !double.IsFinite(effect.Settings.MakeupDb)) throw new InvalidDataException("Ungültiger Effekt.");
        if (effect.Vst is not null && (string.IsNullOrWhiteSpace(effect.Vst.ClassId) || effect.Vst.ClassId.Length > 256 || effect.Vst.Name.Length > 256 || effect.Vst.Vendor.Length > 256))
            throw new InvalidDataException("Ungültige VST3-Referenz.");
        if (effect.StateBase64 is not null)
        {
            try { if (Convert.FromBase64String(effect.StateBase64).Length > MaximumPluginStateBytes) throw new InvalidDataException("VST3-State ist zu groß."); }
            catch (FormatException) { throw new InvalidDataException("Ungültiger VST3-State."); }
        }
    }
}
