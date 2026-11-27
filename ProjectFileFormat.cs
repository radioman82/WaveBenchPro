using System.Text.Json;
using System.Text.Json.Serialization;
using System.IO;

namespace WellenEditor;

// This manifest is validated before it can be used to construct an editor project.
internal static class ProjectFileFormat
{
    internal const int Version = 1;
    internal const int MaximumBytes = 4 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
        MaxDepth = 32
    };

    internal sealed record Manifest(int FormatVersion, string Name, int SampleRate, double Bpm,
        Source[] Sources, Track[] Tracks);
    internal sealed record Source(Guid Id, string Path, long LengthBytes, string Sha256);
    internal sealed record Track(Guid Id, string Name, double Gain, double Pan, bool Muted, bool Solo,
        Clip[] Clips);
    internal sealed record Clip(Guid Id, Guid SourceId, long StartFrame, double DurationScale);

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

    private static void Validate(Manifest value)
    {
        if (value.FormatVersion != Version) throw new InvalidDataException("Nicht unterstützte Projektversion.");
        if (string.IsNullOrWhiteSpace(value.Name) || value.Name.Length > 256 || value.SampleRate is < 8000 or > 384000 ||
            !double.IsFinite(value.Bpm) || value.Bpm is < 30 or > 300 || value.Sources is null || value.Tracks is null ||
            value.Sources.Length > 10000 || value.Tracks.Length > 1000)
            throw new InvalidDataException("Ungültige Projekteinstellungen.");
        var ids = new HashSet<Guid>();
        var sourceIds = new HashSet<Guid>();
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
            if (track is null || track.Id == Guid.Empty || !ids.Add(track.Id) || string.IsNullOrWhiteSpace(track.Name) ||
                track.Name.Length > 256 || !double.IsFinite(track.Gain) || track.Gain is < 0 or > 4 ||
                !double.IsFinite(track.Pan) || track.Pan is < -1 or > 1 || track.Muted && track.Solo || track.Clips is null)
                throw new InvalidDataException("Ungültige Projektspur.");
            clips = checked(clips + track.Clips.Length);
            if (clips > 100000) throw new InvalidDataException("Zu viele Clips.");
            foreach (var clip in track.Clips)
                if (clip is null || clip.Id == Guid.Empty || !ids.Add(clip.Id) || !sourceIds.Contains(clip.SourceId) ||
                    clip.StartFrame < 0 || !double.IsFinite(clip.DurationScale) || clip.DurationScale is <= 0 or > 100)
                    throw new InvalidDataException("Ungültiger Projektclip.");
        }
    }
}
