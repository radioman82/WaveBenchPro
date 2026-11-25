using System.IO;
using NAudio.Wave;

namespace WellenEditor;

/// <summary>Sequential offline mixer. Resampling, stretch and VST are rejected explicitly.</summary>
internal sealed class BlockMixdownRenderer : IDisposable
{
    private sealed record Clip(IAudioSource Source, long Start);
    private sealed record Track(Clip[] Clips, TrackEffectProcessor Effects, float Left, float Right);
    private readonly List<IAudioSource> sources = new();
    private readonly List<Track> tracks = new();
    private readonly float[] trackBuffer;
    private readonly float[] sourceBuffer;
    private long position;
    public long FrameCount { get; }
    public int SampleRate { get; }
    public int BlockFrames { get; }

    public BlockMixdownRenderer(MultitrackProject project, int blockFrames = 4096)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (blockFrames is < 1 or > 65536) throw new ArgumentOutOfRangeException(nameof(blockFrames));
        BlockFrames = blockFrames;
        SampleRate = project.SampleRate;
        trackBuffer = new float[checked(blockFrames * 2)];
        sourceBuffer = new float[checked(blockFrames * 2)];
        var solo = project.Tracks.Any(t => t.Solo && t.HasClip);
        var audible = project.Tracks.Where(t => t.HasClip && !t.Muted && (!solo || t.Solo)).ToArray();
        if (audible.Length == 0) throw new InvalidOperationException("Keine hörbare Spur vorhanden.");
        FrameCount = audible.Max(t => t.EndFrame);
        try
        {
            foreach (var track in audible)
            {
                if (track.Effects.Any(f => f.Enabled && f.Vst is not null))
                    throw new NotSupportedException("Der blockweise Mixdown unterstützt VST-Effekte noch nicht.");
                var clips = new List<Clip>();
                foreach (var clip in track.Clips)
                {
                    if (clip.StartFrame < 0 || clip.Source.SampleRate != SampleRate || clip.DurationScale != 1d || clip.Source.Channels is < 1 or > 2)
                        throw new NotSupportedException("Blockweiser Mixdown unterstützt derzeit Mono/Stereo mit gleicher Samplerate ohne Time-Stretch.");
                    var source = clip.Source.OpenBlockSource();
                    sources.Add(source);
                    clips.Add(new Clip(source, clip.StartFrame));
                }
                var gain = (float)track.Gain;
                var pan = (float)track.Pan;
                tracks.Add(new Track(clips.ToArray(), new TrackEffectProcessor(track.Effects, SampleRate), gain * (pan > 0 ? 1 - pan : 1), gain * (pan < 0 ? 1 + pan : 1)));
            }
        }
        catch { Dispose(); throw; }
    }

    public int Read(float[] output, int frames)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (frames < 0 || frames > BlockFrames || output.Length < frames * 2) throw new ArgumentOutOfRangeException(nameof(frames));
        var count = (int)Math.Min(frames, FrameCount - position);
        Array.Clear(output, 0, count * 2);
        foreach (var track in tracks)
        {
            Array.Clear(trackBuffer, 0, count * 2);
            foreach (var clip in track.Clips)
            {
                var from = Math.Max(position, clip.Start);
                var to = Math.Min(position + count, checked(clip.Start + clip.Source.FrameCount));
                if (to <= from) continue;
                var readFrames = checked((int)(to - from));
                var read = clip.Source.ReadFrames(from - clip.Start, sourceBuffer, 0, readFrames);
                if (read != readFrames) throw new EndOfStreamException("Die Audioquelle endete vorzeitig beim Mixdown.");
                var offset = checked((int)(from - position) * 2);
                for (var i = 0; i < readFrames; i++)
                {
                    var input = i * clip.Source.Channels;
                    trackBuffer[offset + i * 2] += sourceBuffer[input];
                    trackBuffer[offset + i * 2 + 1] += sourceBuffer[input + (clip.Source.Channels == 1 ? 0 : 1)];
                }
            }
            track.Effects.Process(trackBuffer.AsSpan(0, count * 2));
            for (var i = 0; i < count * 2; i += 2)
            {
                output[i] += trackBuffer[i] * track.Left;
                output[i + 1] += trackBuffer[i + 1] * track.Right;
            }
        }
        position += count;
        return count;
    }

    public void Dispose() { foreach (var source in sources) source.Dispose(); sources.Clear(); }

    public static void WriteWave(MultitrackProject project, string path, CancellationToken cancellationToken, IProgress<double>? progress = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var target = Path.GetFullPath(path);
        if (project.Tracks.SelectMany(t => t.Clips).Any(c => !string.IsNullOrWhiteSpace(c.Source.SourcePath) && string.Equals(Path.GetFullPath(c.Source.SourcePath), target, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("Der Mixdown darf keine geöffnete Quelldatei überschreiben.");
        using var renderer = new BlockMixdownRenderer(project);
        // RIFF 32-bit size, including the header written by WaveFileWriter.
        if (renderer.FrameCount > (uint.MaxValue - 1024L) / 8)
            throw new NotSupportedException("Der Mixdown überschreitet das RIFF-WAV-Limit; RF64 wird noch nicht unterstützt.");
        var temp = Path.Combine(Path.GetDirectoryName(target)!, $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var writer = new WaveFileWriter(temp, WaveFormat.CreateIeeeFloatWaveFormat(renderer.SampleRate, 2)))
            {
                var block = new float[renderer.BlockFrames * 2];
                long written = 0;
                var lastPercent = -1;
                while (written < renderer.FrameCount)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var count = renderer.Read(block, renderer.BlockFrames);
                    if (count <= 0) throw new EndOfStreamException("Mixdown endete vorzeitig.");
                    writer.WriteSamples(block, 0, count * 2);
                    written += count;
                    var percent = (int)(written * 100d / renderer.FrameCount);
                    if (percent != lastPercent) { lastPercent = percent; progress?.Report(written / (double)renderer.FrameCount); }
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temp, target, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
