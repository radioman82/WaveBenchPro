using System.IO;
using System.Runtime.InteropServices;
using NAudio.Wave;

namespace WellenEditor;

internal interface IPreparedPlaybackSession : IDisposable
{
    int SampleRate { get; }
    long FrameCount { get; }
    Task Completion { get; }
    IBlockAudioRenderer OpenRenderer(long start, long count, bool loop, int blockFrames, long? initialFrame = null);
}

/// <summary>
/// Private, immutable post-insert track files. WSOLA, resampling and DSP run
/// continuously once; seeks read that exact output without repeating the prefix.
/// Gain, pan, mute and solo are deliberately applied after the cached inserts.
/// </summary>
internal sealed class PreparedMultitrackSession : IPreparedPlaybackSession
{
    private sealed record TrackFile(MultitrackTrack Controls, string Path, long Frames);
    private readonly List<TrackFile> files = new();
    private readonly VstCacheLease lease;
    private readonly object gate = new();
    private int readers;
    private bool closing;
    private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task Completion => completion.Task;
    public int SampleRate { get; }
    public long FrameCount { get; }
    internal long PreparedBytes => files.Sum(file => new FileInfo(file.Path).Length);

    public PreparedMultitrackSession(MultitrackProject snapshot, IReadOnlyList<MultitrackTrack> controls,
        CancellationToken token = default)
    {
        if (snapshot.Tracks.Count != controls.Count) throw new ArgumentException("Ungültiger Spur-Snapshot.");
        SampleRate = snapshot.SampleRate;
        FrameCount = snapshot.TimelineFrames;
        if (FrameCount <= 0) throw new InvalidOperationException("Keine Audioclips vorhanden.");
        lease = VstCacheLease.Open();
        try
        {
            for (var index = 0; index < snapshot.Tracks.Count; index++)
            {
                token.ThrowIfCancellationRequested();
                var track = snapshot.Tracks[index];
                if (!track.HasClip) continue;
                var bytes = checked(track.EndFrame * 8 + 1024);
                if (bytes > uint.MaxValue) throw new NotSupportedException("Eine vorbereitete Spur überschreitet das WAV-Limit.");
                lease.Reserve(bytes);
                var path = Path.Combine(lease.DirectoryPath, $"WaveBenchVst_{Guid.NewGuid():N}.wav");
                // Build ALL tracks, including muted tracks, so live controls never
                // need to decode or prepare an insert from the audio callback.
                track.Gain = 1; track.Pan = 0; track.Muted = false; track.Solo = false;
                BlockMixdownRenderer.WriteWave(snapshot.IsolateTrack(track), path, token);
                files.Add(new TrackFile(controls[index], path, track.EndFrame));
            }
        }
        catch (Exception primary)
        {
            try { lease.Dispose(); } catch (Exception cleanup) { primary.Data["MixdownCleanupError"] = cleanup.Message; }
            throw;
        }
    }

    IBlockAudioRenderer IPreparedPlaybackSession.OpenRenderer(long start, long count, bool loop, int blockFrames, long? initialFrame)
        => OpenRenderer(start, count, loop, blockFrames, initialFrame);

    public Renderer OpenRenderer(long start, long count, bool loop, int blockFrames, long? initialFrame = null)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(closing, this);
            var renderer = new Renderer(this, start, count, loop, blockFrames, initialFrame);
            readers++;
            return renderer;
        }
    }

    private void Release()
    {
        bool cleanup;
        lock (gate)
        {
            cleanup = --readers == 0 && closing;
        }
        if (cleanup) FinishCleanup();
    }
    public void Dispose()
    {
        bool cleanup;
        lock (gate)
        {
            if (closing) return;
            closing = true;
            cleanup = readers == 0;
        }
        if (cleanup) FinishCleanup();
    }

    private void FinishCleanup()
    {
        try { lease.Dispose(); completion.TrySetResult(); }
        catch (Exception error) { completion.TrySetException(error); throw; }
    }

    internal sealed class Renderer : ICancellableBlockAudioRenderer, IPositionedBlockRenderer
    {
        private readonly PreparedMultitrackSession session;
        private readonly List<(TrackFile File, WavFileAudioSource Source)> tracks = new();
        private readonly float[] scratch;
        private readonly long start, end;
        private readonly bool loop;
        private long position;
        private bool disposed;
        public long Position => position;
        public int SampleRate => session.SampleRate;
        public int BlockFrames { get; }
        internal Renderer(PreparedMultitrackSession session, long start, long count, bool loop, int blockFrames, long? initialFrame)
        {
            if (start < 0 || start >= session.FrameCount || count <= 0 || count > session.FrameCount - start)
                throw new ArgumentOutOfRangeException(nameof(start), "Auswahl liegt außerhalb des Projekts.");
            if (blockFrames is < 1 or > 65536) throw new ArgumentOutOfRangeException(nameof(blockFrames));
            this.session = session; this.start = start; end = start + count;
            this.loop = loop; position = initialFrame ?? start; BlockFrames = blockFrames;
            if (position < start || position >= end) throw new ArgumentOutOfRangeException(nameof(initialFrame));
            scratch = new float[blockFrames * 2];
            try
            {
                foreach (var file in session.files) tracks.Add((file, new WavFileAudioSource(file.Path)));
            }
            catch { foreach (var track in tracks) track.Source.Dispose(); throw; }
        }
        public int Read(float[] output, int frames) => Read(output, frames, CancellationToken.None);
        public int Read(float[] output, int frames, CancellationToken token)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            token.ThrowIfCancellationRequested();
            if (frames < 0 || frames > BlockFrames || output.Length < frames * 2) throw new ArgumentOutOfRangeException(nameof(frames));
            if (frames == 0) return 0;
            if (position == end)
            {
                if (!loop) return 0;
                position = start;
            }
            var count = (int)Math.Min(frames, end - position);
            Array.Clear(output, 0, count * 2);
            var solo = tracks.Any(track => track.File.Controls.Solo);
            foreach (var (file, source) in tracks)
            {
                token.ThrowIfCancellationRequested();
                var controls = file.Controls;
                if (controls.Muted || (solo && !controls.Solo)) continue;
                var wanted = (int)Math.Min(count, Math.Max(0, file.Frames - position));
                if (wanted == 0) continue;
                if (source.ReadFrames(position, scratch, 0, wanted) != wanted) throw new EndOfStreamException("Vorbereitete Spur endete vorzeitig.");
                var gain = (float)controls.Gain;
                var pan = (float)controls.Pan;
                var left = gain * (pan > 0 ? 1 - pan : 1);
                var right = gain * (pan < 0 ? 1 + pan : 1);
                for (var i = 0; i < wanted * 2; i += 2)
                {
                    output[i] += scratch[i] * left;
                    output[i + 1] += scratch[i + 1] * right;
                }
            }
            // Expose the NEXT block's start before the queue captures it.
            position += count;
            if (loop && position == end) position = start;
            return count;
        }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            List<Exception>? errors = null;
            foreach (var track in tracks)
                try { track.Source.Dispose(); } catch (Exception error) { (errors ??= new()).Add(error); }
            try { session.Release(); } catch (Exception error) { (errors ??= new()).Add(error); }
            if (errors is not null) throw new AggregateException(errors);
        }
    }
}
