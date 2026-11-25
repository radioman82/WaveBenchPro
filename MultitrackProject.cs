namespace WellenEditor;

/// <summary>
/// Ein bewusst kleines Mehrspurprojekt: Clips verweisen auf offene Dokumente,
/// verändern diese nicht und werden erst beim Mixdown zusammen gerechnet.
/// </summary>
public sealed class MultitrackProject
{
    private const long MaxMixdownBytes = 256L * 1024 * 1024;
    private readonly List<MultitrackTrack> tracks = new();
    private int nextTrackNumber = 1;

    public IReadOnlyList<MultitrackTrack> Tracks => tracks;
    public int SampleRate { get; private set; } = 44100;
    public double Bpm { get; private set; } = 120;
    private bool explicitTempo;
    public double FramesPerBeat => SampleRate * 60d / Bpm;

    public void Add(AudioDocument source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (tracks.Count == 0) SampleRate = source.SampleRate;
        tracks.Add(new MultitrackTrack(source, nextTrackNumber++, SampleRate));
    }

    public MultitrackTrack AddEmptyTrack()
    {
        var track = new MultitrackTrack(null, nextTrackNumber++, SampleRate);
        tracks.Add(track);
        return track;
    }

    public void SetBpm(double bpm)
    {
        if (!double.IsFinite(bpm) || bpm is < 30 or > 300) throw new ArgumentOutOfRangeException(nameof(bpm), "BPM muss zwischen 30 und 300 liegen.");
        var scale = Bpm / bpm;
        foreach (var track in tracks)
            foreach (var clip in track.Clips)
            {
                clip.StartFrame = (long)Math.Round(clip.StartFrame * scale);
                clip.DurationScale *= scale;
            }
        Bpm = bpm;
        explicitTempo = true;
    }

    public void PutClipOnBeat(MultitrackTrack track, AudioDocument source, double beat)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(source);
        if (!tracks.Contains(track)) throw new InvalidOperationException("Die Spur gehört nicht zu diesem Projekt.");
        var sourceTempo = ReadSampleTempo(source);
        if (!explicitTempo && tracks.All(item => !item.HasClip) && sourceTempo.HasValue) Bpm = sourceTempo.Value;
        track.SetClip(source, (long)Math.Round(Math.Max(0, Math.Round(beat)) * FramesPerBeat));
        track.Clips[^1].DurationScale = sourceTempo.HasValue ? sourceTempo.Value / Bpm : 1;
    }

    internal static double? ReadSampleTempo(AudioDocument source)
    {
        var name = System.IO.Path.GetFileNameWithoutExtension(source.DisplayName);
        var match = System.Text.RegularExpressions.Regex.Match(name, @"(?:^|[_\s-])(?<bpm>\d{2,3})(?=[_\s-]|$)");
        return match.Success && double.TryParse(match.Groups["bpm"].Value, out var bpm) && bpm is >= 30 and <= 300 ? bpm : null;
    }

    public void Remove(MultitrackTrack track) => tracks.Remove(track);

    public long TimelineFrames => tracks.Where(track => track.HasClip).DefaultIfEmpty().Max(track => track?.EndFrame ?? 0);

    public float[] RenderMixdown()
        => RenderMixdown(null);

    internal float[] RenderMixdown(TrackEffectChainRenderer.VstRenderer? vstRenderer)
    {
        var solo = tracks.Any(track => track.Solo && track.HasClip);
        var audible = tracks.Where(track => track.HasClip && !track.Muted && (!solo || track.Solo)).ToArray();
        if (audible.Length == 0) throw new InvalidOperationException("Keine hörbare Spur vorhanden.");
        var frames = audible.Max(track => track.EndFrame);
        if (frames < 0 || frames > Array.MaxLength / 2 || checked(frames * sizeof(float) * 2) > MaxMixdownBytes)
            throw new NotSupportedException("Der Mixdown überschreitet das Speicherlimit von 256 MiB für einen einzelnen Samplepuffer.");
        var provider = new MultitrackWaveProvider(this, vstRenderer, failOnVstPreparationError: true);
        var result = new float[checked((int)(frames * 2))];
        provider.Read(System.Runtime.InteropServices.MemoryMarshal.AsBytes(result.AsSpan()));
        return result;
    }
}

public sealed class MultitrackTrack
{
    internal MultitrackTrack(AudioDocument? source, int number, int projectSampleRate)
    {
        Source = source;
        Number = number;
        ProjectSampleRate = projectSampleRate;
        name = source is null ? $"Spur {number}" : source.DisplayName;
        if (source is not null) Clips.Add(new MultitrackClip(source, 0));
    }

    private string name;
    public int Number { get; }
    public int ProjectSampleRate { get; }
    public AudioDocument? Source { get; private set; }
    public string Name { get => name; set => name = string.IsNullOrWhiteSpace(value) ? $"Spur {Number}" : value.Trim(); }
    public long StartFrame { get => Clips.FirstOrDefault()?.StartFrame ?? 0; set { if (Clips.Count > 0) Clips[0].StartFrame = Math.Max(0, value); } }
    public List<MultitrackClip> Clips { get; } = new();
    public double Gain { get; set; } = 1;
    // One atomic state keeps mixer reads consistent while the UI switches modes.
    private int playbackState; // 0 = normal, 1 = muted, 2 = solo
    public bool Muted
    {
        get => Volatile.Read(ref playbackState) == 1;
        set { if (value) Interlocked.Exchange(ref playbackState, 1); else Interlocked.CompareExchange(ref playbackState, 0, 1); }
    }
    public bool Solo
    {
        get => Volatile.Read(ref playbackState) == 2;
        set { if (value) Interlocked.Exchange(ref playbackState, 2); else Interlocked.CompareExchange(ref playbackState, 0, 2); }
    }
    public double Pan { get; set; }
    public bool Expanded { get; set; }
    private TrackEffectSlot[] effects = Array.Empty<TrackEffectSlot>();
    internal TrackEffectSlot[] Effects => Volatile.Read(ref effects);
    internal void SetEffects(IEnumerable<TrackEffectSlot> value) => Volatile.Write(ref effects, value.ToArray());
    public bool HasClip => Clips.Count > 0;
    public long EndFrame => Clips.Count == 0 ? 0 : Clips.Max(clip => checked(clip.StartFrame + (long)Math.Ceiling(clip.Source.FrameCount * clip.DurationScale * ProjectSampleRate / clip.Source.SampleRate)));
    public string StartText => TimeSpan.FromSeconds(StartFrame / (double)Math.Max(1, ProjectSampleRate)).ToString(@"m\:ss\.fff");
    public double ClipLeft { get; private set; }
    public double ClipWidth { get; private set; } = 36;
    public string ClipLabel => Source is null ? "Audio aus der linken Dateiliste hier ablegen" : Source.DisplayName;

    internal void SetClip(AudioDocument source, long startFrame)
    {
        Source = source;
        Clips.Add(new MultitrackClip(source, Math.Max(0, startFrame)));
        if (name.StartsWith("Spur ", StringComparison.Ordinal)) name = source.DisplayName;
    }

    internal void SetTimelineLayout(double framesPerBeat, double pixelsPerBeat)
    {
        if (Source is null) { ClipLeft = 0; ClipWidth = 760; return; }
        ClipLeft = Math.Round(StartFrame / Math.Max(1d, framesPerBeat) * pixelsPerBeat);
        ClipWidth = Math.Max(36, Math.Round((EndFrame - StartFrame) / Math.Max(1d, framesPerBeat) * pixelsPerBeat));
    }
}

public sealed class MultitrackClip
{
    public MultitrackClip(AudioDocument source, long startFrame) { Source = source; StartFrame = startFrame; }
    public AudioDocument Source { get; }
    public long StartFrame { get; set; }
    public double DurationScale { get; internal set; } = 1;
}
