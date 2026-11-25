using System.IO;
using System.Runtime.InteropServices;
using NAudio.Wave;

namespace WellenEditor;

/// <summary>
/// Private, immutable post-insert track files. WSOLA, resampling and DSP run
/// continuously once; seeks read that exact output without repeating the prefix.
/// Gain, pan, mute and solo are deliberately applied after the cached inserts.
/// </summary>
internal sealed class PreparedMultitrackSession : IDisposable
{
    private sealed record TrackFile(MultitrackTrack Controls, string Path, long Frames);
    private readonly List<TrackFile> files = new();
    private readonly VstCacheLease lease;
    private readonly object gate = new();
    private int readers;
    private bool closing;
    public int SampleRate { get; }
    public long FrameCount { get; }

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

    public Renderer OpenRenderer(long start, long count, bool loop, int blockFrames)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(closing, this);
            var renderer = new Renderer(this, start, count, loop, blockFrames);
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
        if (cleanup) lease.Dispose();
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
        if (cleanup) lease.Dispose();
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
        internal Renderer(PreparedMultitrackSession session, long start, long count, bool loop, int blockFrames)
        {
            if (start < 0 || start >= session.FrameCount || count <= 0 || count > long.MaxValue - start)
                throw new ArgumentOutOfRangeException(nameof(start), "Auswahl liegt außerhalb des Projekts.");
            if (blockFrames is < 1 or > 65536) throw new ArgumentOutOfRangeException(nameof(blockFrames));
            this.session = session; this.start = start; end = start + count;
            this.loop = loop; position = start; BlockFrames = blockFrames;
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

/// <summary>Control-thread publication with independent queues for transport generations.</summary>
internal sealed class PreparedMultitrackPlayback : IWaveProvider, IDisposable
{
    private readonly PreparedMultitrackSession session;
    private readonly SemaphoreSlim transportGate = new(1);
    private readonly object publicationGate = new();
    private BufferedBlockWaveProvider? queue;
    private int generation, disposed;
    private int publishedGeneration;
    private bool transportFailed;
    private BufferedBlockWaveProvider? retiringQueue;
    private Task sessionCleanup = Task.CompletedTask;
    private Exception? cleanupError;
    private long underruns;
    public WaveFormat WaveFormat { get; }
    public long FrameCount => session.FrameCount;
    public long UnderrunCount => Interlocked.Read(ref underruns) + (Volatile.Read(ref queue)?.UnderrunCount ?? 0);
    public Exception? WorkerError => Volatile.Read(ref queue)?.WorkerError ?? Volatile.Read(ref cleanupError);
    public int BufferBlocks { get; }
    public int BlockFrames { get; }
    public PreparedMultitrackPlayback(PreparedMultitrackSession session, int blockFrames = 1024, int bufferBlocks = 8)
    {
        if (blockFrames is < 1 or > 65536 || bufferBlocks is < 2 or > 256) throw new ArgumentOutOfRangeException();
        this.session = session; BlockFrames = blockFrames; BufferBlocks = bufferBlocks;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(session.SampleRate, 2);
    }
    public async Task ConfigurePlaybackAsync(long start, long count, bool loop)
    {
        var version = Interlocked.Increment(ref generation);
        await transportGate.WaitAsync().ConfigureAwait(false);
        BufferedBlockWaveProvider? next = null;
        try
        {
            if (disposed != 0 || version != Volatile.Read(ref generation)) return;
            if (transportFailed) throw new InvalidOperationException("Der bisherige Audioworker muss erst beendet werden.");
            // Stop and finish the old producer before creating a new one. A stuck
            // source cannot accumulate workers on repeated transport requests.
            var previous = Interlocked.Exchange(ref queue, null);
            if (previous is not null)
            {
                Volatile.Write(ref retiringQueue, previous);
                Interlocked.Add(ref underruns, previous.UnderrunCount);
                if (!await previous.StopAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false))
                {
                    transportFailed = true;
                    throw new TimeoutException("Der bisherige Audioworker antwortet nicht. Wiedergabe bitte stoppen.");
                }
            }
            next = new BufferedBlockWaveProvider(session.OpenRenderer(start, count, loop, BlockFrames), BufferBlocks);
            if (!await next.PrefillAsync(Math.Min(2, BufferBlocks), TimeSpan.FromSeconds(5)).ConfigureAwait(false))
                throw new TimeoutException("Der Audiopuffer konnte nicht rechtzeitig vorbereitet werden.");
            lock (publicationGate)
            {
                if (disposed != 0 || version != Volatile.Read(ref generation)) return;
                Volatile.Write(ref publishedGeneration, version);
                Volatile.Write(ref queue, next);
                next = null;
            }
        }
        finally { next?.Dispose(); transportGate.Release(); }
    }
    public long AudibleFrame(int latencyFrames) => Volatile.Read(ref queue)?.AudibleFrame(latencyFrames) ?? 0;
    public int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public int Read(Span<byte> buffer)
    {
        var current = Volatile.Read(ref queue);
        var version = Volatile.Read(ref generation);
        if (current is null || Volatile.Read(ref publishedGeneration) != version)
        { buffer.Clear(); return disposed == 0 ? buffer.Length : 0; }
        var read = current.Read(buffer);
        if (version != Volatile.Read(ref generation) || !ReferenceEquals(current, Volatile.Read(ref queue)))
        { buffer.Clear(); return buffer.Length; }
        return read;
    }
    public void Dispose()
    {
        lock (publicationGate)
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            Interlocked.Increment(ref generation);
            var current = Interlocked.Exchange(ref queue, null);
            if (current is not null) Volatile.Write(ref retiringQueue, current);
            current?.Dispose();
            sessionCleanup = Task.Run(() =>
            {
                try { session.Dispose(); }
                catch (Exception error) { Volatile.Write(ref cleanupError, error); }
            });
        }
    }
    public async Task<bool> StopAsync(TimeSpan timeout)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Dispose();
        if (!await transportGate.WaitAsync(timeout).ConfigureAwait(false)) return false;
        try
        {
            var remaining = timeout - clock.Elapsed;
            if (remaining <= TimeSpan.Zero) return false;
            var previous = Volatile.Read(ref retiringQueue);
            if (previous is not null && !await previous.StopAsync(remaining).ConfigureAwait(false)) return false;
            remaining = timeout - clock.Elapsed;
            if (remaining <= TimeSpan.Zero) return false;
            return await Task.WhenAny(sessionCleanup, Task.Delay(remaining)).ConfigureAwait(false) == sessionCleanup;
        }
        finally { transportGate.Release(); }
    }
}
