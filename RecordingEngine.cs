using NAudio.CoreAudioApi;
using NAudio.Wave;
using System.IO;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace WellenEditor;

internal interface IRecordingCapture : IDisposable
{
    WaveFormat WaveFormat { get; }
    event EventHandler<WaveInEventArgs>? DataAvailable;
    event EventHandler<StoppedEventArgs>? RecordingStopped;
    void StartRecording();
    void StopRecording();
}

internal interface IRecordingSink : IDisposable
{
    void Write(byte[] buffer, int offset, int count);
}

internal interface IFixedPacketRecordingCapture : IRecordingCapture
{
    int PacketFrames { get; }
}

internal readonly record struct CaptureQueueLayout(int WaitingPackets, int PacketBytes)
{
    internal static CaptureQueueLayout Create(WaveFormat format, int? packetFrames, int? candidate = null)
    {
        if (packetFrames is not { } frames)
            return new(8, Math.Max(64 * 1024, format.AverageBytesPerSecond / 2));

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frames);
        var bytes = checked(frames * format.BlockAlign);
        var packets = candidate ?? Math.Clamp((int)Math.Ceiling(format.SampleRate * .65 / frames), 64, 256);
        if (packets is < 64 or > 256) throw new ArgumentOutOfRangeException(nameof(candidate));
        const int budget = 16 * 1024 * 1024;
        var bounded = Math.Min(packets, budget / bytes - 1);
        if (bounded < 1) throw new NotSupportedException("Der ASIO-Aufnahmepuffer überschreitet das Speicherlimit.");
        return new(bounded, bytes);
    }
}

internal sealed class WaveFileRecordingSink : IRecordingSink
{
    private readonly WaveFileWriter writer;
    public WaveFileRecordingSink(string path, WaveFormat format) => writer = new WaveFileWriter(path, format);
    public void Write(byte[] buffer, int offset, int count) => writer.Write(buffer, offset, count);
    public void Dispose() => writer.Dispose();
}

/// <summary>
/// Nimmt über WASAPI vom gewählten Eingabegerät auf und liefert beim Stoppen
/// ein formatfestes AudioSelectionData. Capture-Daten werden non-blocking in eine begrenzte Queue kopiert;
/// UI-Abonnenten müssen selbst auf den Dispatcher marshallen.
/// </summary>
public sealed class RecordingEngine : IDisposable
{
    private readonly ManualResetEventSlim stopped = new(false);
    private readonly object writerGate = new();
    private readonly object captureGate = new();
    private readonly Func<string?, IRecordingCapture> captureFactory;
    private readonly Func<int, float[]> sampleAllocator;
    private readonly string recoveryDirectory;
    private readonly TimeSpan stopTimeout;
    private IRecordingCapture? capture;
    private IRecordingSink? recorded;
    private CapturePacketQueue? captureQueue;
    private int captureFailure;
    private long receivedBytes, writtenBytes, packets;
    private long discontinuities, overflowPackets, queueWaitMaxTicks, writeMaxTicks;
    private int peakQueueSlots;
    private readonly int? captureQueueCandidate;
    private readonly bool measureCaptureTiming;
    private Task? captureWriterTask;
    private Task? deferredWriterCleanup;
    private bool disposed;
    private string? recordingPath;
    private WaveFormat? format;
    private bool isRecording;
    private Exception? recordingError;
    private int acceptingCaptureData;
    private readonly Func<string, WaveFormat, IRecordingSink> sinkFactory;

    public bool IsRecording => isRecording;
    public WaveFormat? CaptureFormat => format;
    public float LastPeak { get; private set; }
    public event EventHandler<float[]>? LevelsChanged;
    public event EventHandler? RecordingFaulted;
    public long ReceivedBytes => Interlocked.Read(ref receivedBytes);
    public long WrittenBytes => Interlocked.Read(ref writtenBytes);
    internal CaptureQueueLayout QueueLayout { get; private set; }
    internal int PeakQueueSlots => captureQueue?.HighWater ?? Volatile.Read(ref peakQueueSlots);
    internal long CapturePackets => Interlocked.Read(ref packets);
    internal long Discontinuities => Interlocked.Read(ref discontinuities);
    internal long OverflowPackets => Interlocked.Read(ref overflowPackets);
    internal double MaximumQueueWaitMs => Interlocked.Read(ref queueWaitMaxTicks) * 1000d / Stopwatch.Frequency;
    internal double MaximumWriteMs => Interlocked.Read(ref writeMaxTicks) * 1000d / Stopwatch.Frequency;
    internal AudioStreamStatus? HardwareStatus => (capture as ISpanRecordingCapture)?.Status;
    internal void DeviceLost(Exception error)
    {
        if (!isRecording) return;
        SetRecordingError(error);
        Volatile.Write(ref acceptingCaptureData, 0);
        try { StopHardwareCapture(); } catch (Exception stopError) { SetRecordingError(stopError); }
        RecordingFaulted?.Invoke(this, EventArgs.Empty);
    }
    internal Task? PendingFinalization { get { lock (writerGate) return deferredWriterCleanup; } }
    public bool IsFinalizationPending { get { lock (writerGate) return deferredWriterCleanup is { IsCompleted: false }; } }

    public RecordingEngine()
        : this(CreateCapture, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "WaveBenchPro", "Recovered"), length => new float[length], TimeSpan.FromSeconds(3), (path, waveFormat) => new WaveFileRecordingSink(path, waveFormat)) { }

    internal RecordingEngine(Func<string?, IRecordingCapture> captureFactory, string recoveryDirectory, Func<int, float[]> sampleAllocator, TimeSpan? stopTimeout = null, Func<string, WaveFormat, IRecordingSink>? sinkFactory = null,
        int? captureQueueCandidate = null, bool measureCaptureTiming = false)
    {
        this.captureFactory = captureFactory ?? throw new ArgumentNullException(nameof(captureFactory));
        this.recoveryDirectory = recoveryDirectory ?? throw new ArgumentNullException(nameof(recoveryDirectory));
        this.sampleAllocator = sampleAllocator ?? throw new ArgumentNullException(nameof(sampleAllocator));
        this.stopTimeout = stopTimeout ?? TimeSpan.FromSeconds(3);
        this.sinkFactory = sinkFactory ?? ((path, waveFormat) => new WaveFileRecordingSink(path, waveFormat));
        this.captureQueueCandidate = captureQueueCandidate;
        this.measureCaptureTiming = measureCaptureTiming;
    }

    private static IRecordingCapture CreateCapture(string? deviceId)
    {
        if (AudioHardwareSettings.InputReselectionRequired)
            throw new InvalidOperationException("Das Aufnahmegerät muss unter Audio-Hardware erneut ausgewählt werden.");
        return AudioHardwareBackend.Default.CreateCapture(AudioHardwareSettings.Input with { DeviceId = deviceId });
    }

    public void Start(string? deviceId)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        lock (writerGate)
            if (deferredWriterCleanup is { IsCompleted: false })
                throw new InvalidOperationException("Die vorherige Aufnahme wird noch im Hintergrund abgeschlossen. Bitte warten, bis der Datenträger wieder reagiert.");
        if (isRecording) throw new InvalidOperationException("Eine Aufnahme läuft bereits.");
        ReleaseCapture();

        capture = captureFactory(deviceId);
        format = capture.WaveFormat;
        stopped.Reset();
        recordingError = null;
        captureFailure = 0;
        receivedBytes = writtenBytes = packets = 0;
        discontinuities = overflowPackets = queueWaitMaxTicks = writeMaxTicks = 0;
        peakQueueSlots = 0;
        LastPeak = 0f;
        try
        {
            recordingPath = Path.Combine(Path.GetTempPath(), $"WaveBenchPro_Recording_{Guid.NewGuid():N}.wav");
            QueueLayout = CaptureQueueLayout.Create(format, (capture as IFixedPacketRecordingCapture)?.PacketFrames, captureQueueCandidate);
            captureQueue = new CapturePacketQueue(QueueLayout.WaitingPackets, QueueLayout.PacketBytes, measureCaptureTiming);
            recorded = sinkFactory(recordingPath, format);
            Volatile.Write(ref acceptingCaptureData, 1);
            // This worker blocks on a capture queue and synchronous file writes.
            // It must not wait for thread-pool injection while native capture is
            // already filling the bounded queue.
            captureWriterTask = Task.Factory.StartNew(DrainCaptureQueue, CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
            if (capture is ISpanRecordingCapture spanCapture) spanCapture.PacketAvailable += OnPacket;
            else capture.DataAvailable += OnDataAvailable;
            capture.RecordingStopped += OnRecordingStopped;
            isRecording = true;
            capture.StartRecording();
        }
        catch (Exception startupError)
        {
            isRecording = false;
            ReleaseCaptureOrPreserve(startupError);
            if (ReceivedBytes > 0)
            {
                var recovery = PreserveRecordingFile();
                throw new InvalidOperationException($"Der Aufnahmestart schlug nach den ersten Daten fehl. Die Teilaufnahme wurde gesichert unter: {recovery}", startupError);
            }
            DiscardRecordingFile();
            throw;
        }
    }

    /// <summary>Beendet die Aufnahme und gibt die aufgezeichneten Samples zurück.</summary>
    public AudioSelectionData? Finish()
    {
        if (!FinishCapture()) return null;
        if (recordingPath is null || format is null) return null;
        return ReadCompletedRecording();
    }

    // Hardware measurements verify the sink byte for byte without materializing an hour of audio.
    internal bool FinishToSink()
    {
        if (!FinishCapture()) return false;
        DiscardRecordingFile();
        return true;
    }

    private bool FinishCapture()
    {
        if (!isRecording) return false;
        isRecording = false;
        try { StopHardwareCapture(); }
        catch (Exception exception)
        {
            SetRecordingError(exception);
            ReleaseCaptureOrPreserve(exception);
            var recoveryPath = PreserveRecordingFile();
            throw new InvalidOperationException($"Das Audiogerät konnte nicht sauber gestoppt werden. Die bisherige Aufnahme wurde gesichert unter: {recoveryPath}", exception);
        }
        if (!stopped.Wait(stopTimeout))
        {
            ReleaseCaptureOrPreserve();
            var recoveryPath = PreserveRecordingFile();
            throw new TimeoutException($"Das Audiogerät hat das Ende der Aufnahme nicht innerhalb von drei Sekunden bestätigt. Die bisherige Aufnahme wurde gesichert unter: {recoveryPath}");
        }
        ReleaseCaptureOrPreserve();

        CloseWriter();
        if (recordingError is { } error)
        {
            var recoveryPath = PreserveRecordingFile();
            throw new InvalidOperationException($"Die Audioaufnahme konnte nicht sicher abgeschlossen werden. Die bisherige Aufnahme wurde gesichert unter: {recoveryPath}", error);
        }

        return true;
    }

    private AudioSelectionData? ReadCompletedRecording()
    {
        try
        {
            using var reader = new WaveFileReader(recordingPath!);
            var channels = reader.WaveFormat.Channels;
            var frameCount = reader.Length / reader.WaveFormat.BlockAlign;
            var sampleCount = checked((int)(frameCount * channels));
            var samples = sampleAllocator(sampleCount);
            if (samples.Length != sampleCount) throw new InvalidDataException("Der Aufnahme-Samplepuffer hat eine unerwartete Länge.");
            var blockAlign = reader.WaveFormat.BlockAlign;
            var bytes = new byte[Math.Max(blockAlign, 64 * 1024 / blockAlign * blockAlign)];
            var written = 0;
            while (true)
            {
                var count = reader.Read(bytes, 0, bytes.Length);
                if (count == 0) break;
                var decoded = AudioFormatConverter.Decode(bytes, count, reader.WaveFormat);
                Array.Copy(decoded, 0, samples, written, decoded.Length);
                written += decoded.Length;
            }
            if (written != samples.Length) throw new EndOfStreamException("Die temporäre Aufnahme-WAV war unvollständig.");
            return samples.Length == 0 ? null : new AudioSelectionData(samples, channels, reader.WaveFormat.SampleRate);
        }
        catch (Exception exception)
        {
            var recoveryPath = PreserveRecordingFile();
            throw new InvalidDataException($"Die Aufnahme konnte nicht vollständig eingelesen werden. Die WAV-Datei wurde gesichert unter: {recoveryPath}", exception);
        }
        finally { DiscardRecordingFile(); }
    }

    /// <summary>Verwirft die laufende Aufnahme ohne Samples zurückzugeben.</summary>
    public void Cancel()
    {
        if (!isRecording) return;
        isRecording = false;
        try
        {
            StopHardwareCapture();
            stopped.Wait(stopTimeout);
            ReleaseCaptureOrPreserve();
            DiscardRecordingFile();
        }
        catch (Exception error) when (recordingPath is not null)
        {
            SetRecordingError(error);
            ReleaseCaptureOrPreserve(error);
            var recovery = PreserveRecordingFile();
            throw new InvalidOperationException($"Abbruch des Aufnahmegeräts fehlgeschlagen. Die Teilaufnahme wurde gesichert unter: {recovery}", error);
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        OnPacket(e.Buffer.AsSpan(0, e.BytesRecorded), false, 0);
    }

    private void OnPacket(ReadOnlySpan<byte> bytes, bool discontinuity, long devicePosition)
    {
        if (bytes.Length == 0 || Volatile.Read(ref acceptingCaptureData) == 0) return;
        Interlocked.Add(ref receivedBytes, bytes.Length);
        var count = Interlocked.Increment(ref packets);
        if (discontinuity)
        {
            Interlocked.Increment(ref discontinuities);
            if (count > 1)
            {
                RejectPacket(2);
                return;
            }
        }
        if (captureQueue?.TryWrite(bytes) == true) return;
        if (Volatile.Read(ref acceptingCaptureData) == 0) return;
        Interlocked.Increment(ref overflowPackets);
        RejectPacket(1);
    }

    private void RejectPacket(int failure)
    {
        Volatile.Write(ref acceptingCaptureData, 0);
        Interlocked.CompareExchange(ref captureFailure, failure, 0);
        captureQueue?.Complete(); // Signal only: stopping the driver happens on the IO worker.
    }

    private void ObserveCaptureFailure()
    {
        var failure = Interlocked.Exchange(ref captureFailure, 0);
        if (failure == 0) return;
        SetRecordingError(new IOException(failure == 2
            ? "Das Audiogerät meldet eine Aufnahmelücke. Die Teilaufnahme wird gesichert; bitte Gerät erneut auswählen."
            : "Der Aufnahme-Puffer ist voll oder ein Paket ist zu groß; die Teilaufnahme wird gesichert."));
        try { StopHardwareCapture(); } catch (Exception e)
        {
            SetRecordingError(e);
            RecordingFaulted?.Invoke(this, EventArgs.Empty);
        }
    }

    private void DrainCaptureQueue()
    {
        var queue = captureQueue;
        if (queue is null) return;
        try
        {
            var channels = Math.Max(1, format!.Channels);
            var (encoding, bytesPerSample) = AudioFormatConverter.DecodeFormat(format);
            var peaks = new float[channels];
            var pendingMeterPeaks = new float[channels];
            var nextMeterTick = 0L;
            var writerFailed = false;
            while (queue.TryRead(out var captured))
            {
                var item = captured!;
                try
                {
                    ObserveCaptureFailure();
                    // Keep the contiguous prefix after a write failure, even if the disk later accepts more bytes.
                    if (writerFailed) continue;
                    var writeStarted = measureCaptureTiming ? Stopwatch.GetTimestamp() : 0;
                    if (measureCaptureTiming)
                        Interlocked.Exchange(ref queueWaitMaxTicks, Math.Max(queueWaitMaxTicks, writeStarted - item.EnqueuedAt));
                    recorded?.Write(item.Buffer, 0, item.BytesRecorded);
                    if (measureCaptureTiming)
                        Interlocked.Exchange(ref writeMaxTicks, Math.Max(writeMaxTicks, Stopwatch.GetTimestamp() - writeStarted));
                    Interlocked.Add(ref writtenBytes, item.BytesRecorded);
                    Array.Clear(peaks);
                    for (var sample = 0; sample < item.BytesRecorded / bytesPerSample; sample++)
                    {
                        var channel = sample % channels;
                        var value = AudioFormatConverter.DecodeSample(item.Buffer, sample * bytesPerSample, encoding, bytesPerSample);
                        peaks[channel] = Math.Max(peaks[channel], Math.Abs(value));
                    }
                    LastPeak = peaks.Max();
                    if (LevelsChanged is { } handler)
                    {
                        for (var channel = 0; channel < channels; channel++)
                            pendingMeterPeaks[channel] = Math.Max(pendingMeterPeaks[channel], peaks[channel]);
                        var now = Stopwatch.GetTimestamp();
                        if (now >= nextMeterTick)
                        {
                            handler(this, (float[])pendingMeterPeaks.Clone());
                            Array.Clear(pendingMeterPeaks);
                            nextMeterTick = now + Stopwatch.Frequency / 30;
                        }
                    }
                }
                catch (Exception exception)
                {
                    writerFailed = true;
                    SetRecordingError(exception);
                    Volatile.Write(ref acceptingCaptureData, 0);
                    CompleteCaptureQueue();
                    try { StopHardwareCapture(); } catch { RecordingFaulted?.Invoke(this, EventArgs.Empty); }
                }
                finally { queue.ReleaseRead(); }
            }
            ObserveCaptureFailure();
        }
        finally
        {
            try { recorded?.Dispose(); }
            catch (Exception exception) { SetRecordingError(exception); }
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null) SetRecordingError(e.Exception);
        Volatile.Write(ref acceptingCaptureData, 0);
        CompleteCaptureQueue();
        stopped.Set();
        if (recordingError is not null) RecordingFaulted?.Invoke(this, EventArgs.Empty);
    }

    private void StopHardwareCapture()
    {
        lock (captureGate) capture?.StopRecording();
    }

    private void ReleaseCapture()
    {
        lock (captureGate)
        {
            if (capture is null) return;
            if (capture is ISpanRecordingCapture spanCapture) spanCapture.PacketAvailable -= OnPacket;
            else capture.DataAvailable -= OnDataAvailable;
            capture.RecordingStopped -= OnRecordingStopped;
            try { capture.StopRecording(); } catch { /* Treiber bereits gestoppt */ }
            capture.Dispose();
            capture = null;
        }
    }

    private void ReleaseCaptureOrPreserve(Exception? prior = null)
    {
        try { ReleaseCapture(); }
        catch (Exception error)
        {
            SetRecordingError(error);
            Volatile.Write(ref acceptingCaptureData, 0);
            CompleteCaptureQueue();
            var recovery = recordingPath is null ? "keine temporäre Datei vorhanden" : PreserveRecordingFile();
            throw new InvalidOperationException($"Das Aufnahmegerät ist noch nicht sicher freigegeben; kein paralleler Neustart. Die Teilaufnahme wurde gesichert unter: {recovery}",
                prior is null ? error : new AggregateException(prior, error));
        }
    }

    private void DiscardRecordingFile()
    {
        CloseWriter();
        var path = recordingPath;
        recordingPath = null;
        if (path is null) return;
        try { File.Delete(path); } catch { /* temporäre Datei beim nächsten Start nicht mehr verwenden */ }
    }

    private void CloseWriter()
    {
        lock (writerGate)
        {
            if (deferredWriterCleanup is { IsCompleted: false })
                throw new TimeoutException("Die vorherige Aufnahme wird noch im Hintergrund abgeschlossen.");
            Volatile.Write(ref acceptingCaptureData, 0);
            CompleteCaptureQueue();
            var writerTask = captureWriterTask;
            if (writerTask is not null && Task.WhenAny(writerTask, Task.Delay(stopTimeout)).GetAwaiter().GetResult() != writerTask)
            {
                var path = recordingPath;
                recordingPath = null;
                var queue = captureQueue;
                // The worker still uses the fields: retain them until it has finished.
                deferredWriterCleanup = writerTask.ContinueWith(_ =>
                {
                    lock (writerGate)
                    {
                        queue?.Dispose();
                        peakQueueSlots = queue?.HighWater ?? 0;
                        recorded = null;
                        captureQueue = null;
                        captureWriterTask = null;
                    }
                }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
                throw new TimeoutException($"Der Datenträger antwortet nicht. Die Aufnahme wird noch im Hintergrund abgeschlossen. Die Datei bleibt erhalten unter: {path}. Erst nach Abschluss ist sie verwendbar.");
            }
            try { writerTask?.GetAwaiter().GetResult(); }
            catch (Exception exception) { SetRecordingError(exception); }
            if (writerTask is null)
            {
                try { recorded?.Dispose(); }
                catch (Exception exception) { SetRecordingError(exception); }
            }
            captureWriterTask = null;
            recorded = null;
            peakQueueSlots = captureQueue?.HighWater ?? peakQueueSlots;
            captureQueue?.Dispose();
            captureQueue = null;
        }
    }

    private void CompleteCaptureQueue()
    {
        var queue = captureQueue;
        queue?.Complete();
    }

    private void SetRecordingError(Exception exception) => Interlocked.CompareExchange(ref recordingError, exception, null);

    private string PreserveRecordingFile()
    {
        CloseWriter();
        var sourcePath = recordingPath ?? throw new InvalidOperationException("Es gibt keine temporäre Aufnahme mehr.");
        recordingPath = null;
        try
        {
            Directory.CreateDirectory(recoveryDirectory);
            var recoveryPath = Path.Combine(recoveryDirectory, $"Aufnahme_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.wav");
            File.Move(sourcePath, recoveryPath);
            return recoveryPath;
        }
        catch
        {
            // Falls der Wiederherstellungsordner nicht beschreibbar ist, den ursprünglichen
            // Temp-Pfad als Rettung behalten und niemals durch Dispose automatisch löschen.
            return sourcePath;
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        try
        {
            if (isRecording) Cancel();
            ReleaseCaptureOrPreserve();
            DiscardRecordingFile();
        }
        catch (TimeoutException)
        {
            // The deferred cleanup owns the open file until the writer returns.
            ReleaseCaptureOrPreserve();
        }
        // Failed native retirement leaves capture and its stop signal alive for retry.
        disposed = true;
        stopped.Dispose();
    }
}
