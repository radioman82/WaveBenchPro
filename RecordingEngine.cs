using NAudio.CoreAudioApi;
using NAudio.Wave;
using System.Buffers;
using System.Collections.Concurrent;
using System.IO;
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

internal sealed class WaveFileRecordingSink : IRecordingSink
{
    private readonly WaveFileWriter writer;
    public WaveFileRecordingSink(string path, WaveFormat format) => writer = new WaveFileWriter(path, format);
    public void Write(byte[] buffer, int offset, int count) => writer.Write(buffer, offset, count);
    public void Dispose() => writer.Dispose();
}

internal sealed class WasapiCaptureAdapter : IRecordingCapture
{
    private readonly WasapiCapture capture;
    public WasapiCaptureAdapter(WasapiCapture capture) => this.capture = capture;
    public WaveFormat WaveFormat => capture.WaveFormat;
    public event EventHandler<WaveInEventArgs>? DataAvailable
    {
        add => capture.DataAvailable += value;
        remove => capture.DataAvailable -= value;
    }
    public event EventHandler<StoppedEventArgs>? RecordingStopped
    {
        add => capture.RecordingStopped += value;
        remove => capture.RecordingStopped -= value;
    }
    public void StartRecording() => capture.StartRecording();
    public void StopRecording() => capture.StopRecording();
    public void Dispose() => capture.Dispose();
}

/// <summary>
/// Nimmt über WASAPI vom gewählten Eingabegerät auf und liefert beim Stoppen
/// ein formatfestes AudioSelectionData. Capture-Daten werden non-blocking in eine begrenzte Queue kopiert;
/// UI-Abonnenten müssen selbst auf den Dispatcher marshallen.
/// </summary>
public sealed class RecordingEngine : IDisposable
{
    private readonly record struct CaptureBlock(byte[] Buffer, int BytesRecorded);
    private const int CaptureQueueCapacity = 8;
    private readonly ManualResetEventSlim stopped = new(false);
    private readonly object writerGate = new();
    private readonly Func<string?, IRecordingCapture> captureFactory;
    private readonly Func<int, float[]> sampleAllocator;
    private readonly string recoveryDirectory;
    private readonly TimeSpan stopTimeout;
    private IRecordingCapture? capture;
    private IRecordingSink? recorded;
    private BlockingCollection<CaptureBlock>? captureQueue;
    private Task? captureWriterTask;
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

    public RecordingEngine()
        : this(CreateCapture, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "WaveBenchPro", "Recovered"), length => new float[length], TimeSpan.FromSeconds(3), (path, waveFormat) => new WaveFileRecordingSink(path, waveFormat)) { }

    internal RecordingEngine(Func<string?, IRecordingCapture> captureFactory, string recoveryDirectory, Func<int, float[]> sampleAllocator, TimeSpan? stopTimeout = null, Func<string, WaveFormat, IRecordingSink>? sinkFactory = null)
    {
        this.captureFactory = captureFactory ?? throw new ArgumentNullException(nameof(captureFactory));
        this.recoveryDirectory = recoveryDirectory ?? throw new ArgumentNullException(nameof(recoveryDirectory));
        this.sampleAllocator = sampleAllocator ?? throw new ArgumentNullException(nameof(sampleAllocator));
        this.stopTimeout = stopTimeout ?? TimeSpan.FromSeconds(3);
        this.sinkFactory = sinkFactory ?? ((path, waveFormat) => new WaveFileRecordingSink(path, waveFormat));
    }

    private static IRecordingCapture CreateCapture(string? deviceId)
    {
        using var enumerator = new MMDeviceEnumerator();
        var device = string.IsNullOrWhiteSpace(deviceId)
            ? enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia)
            : enumerator.GetDevice(deviceId);
        return new WasapiCaptureAdapter(new WasapiCapture(device));
    }

    public void Start(string? deviceId)
    {
        if (isRecording) throw new InvalidOperationException("Eine Aufnahme läuft bereits.");
        ReleaseCapture();

        capture = captureFactory(deviceId);
        format = capture.WaveFormat;
        stopped.Reset();
        recordingError = null;
        LastPeak = 0f;
        try
        {
            recordingPath = Path.Combine(Path.GetTempPath(), $"WaveBenchPro_Recording_{Guid.NewGuid():N}.wav");
            recorded = sinkFactory(recordingPath, format);
            captureQueue = new BlockingCollection<CaptureBlock>(CaptureQueueCapacity);
            Volatile.Write(ref acceptingCaptureData, 1);
            captureWriterTask = Task.Run(DrainCaptureQueue);
            capture.DataAvailable += OnDataAvailable;
            capture.RecordingStopped += OnRecordingStopped;
            capture.StartRecording();
            isRecording = true;
        }
        catch
        {
            ReleaseCapture();
            DiscardRecordingFile();
            throw;
        }
    }

    /// <summary>Beendet die Aufnahme und gibt die aufgezeichneten Samples zurück.</summary>
    public AudioSelectionData? Finish()
    {
        if (!isRecording) return null;
        isRecording = false;
        try { capture?.StopRecording(); }
        catch (Exception exception)
        {
            SetRecordingError(exception);
            ReleaseCapture();
            var recoveryPath = PreserveRecordingFile();
            throw new InvalidOperationException($"Das Audiogerät konnte nicht sauber gestoppt werden. Die bisherige Aufnahme wurde gesichert unter: {recoveryPath}", exception);
        }
        if (!stopped.Wait(stopTimeout))
        {
            ReleaseCapture();
            var recoveryPath = PreserveRecordingFile();
            throw new TimeoutException($"Das Audiogerät hat das Ende der Aufnahme nicht innerhalb von drei Sekunden bestätigt. Die bisherige Aufnahme wurde gesichert unter: {recoveryPath}");
        }
        ReleaseCapture();

        CloseWriter();
        if (recordingError is { } error)
        {
            var recoveryPath = PreserveRecordingFile();
            throw new InvalidOperationException($"Die Audioaufnahme konnte nicht sicher abgeschlossen werden. Die bisherige Aufnahme wurde gesichert unter: {recoveryPath}", error);
        }

        if (recordingPath is null || format is null) return null;
        try
        {
            using var reader = new WaveFileReader(recordingPath);
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
        capture?.StopRecording();
        stopped.Wait(TimeSpan.FromSeconds(3));
        ReleaseCapture();
        DiscardRecordingFile();
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0 || format is null || Volatile.Read(ref acceptingCaptureData) == 0) return;
        byte[]? rented = null;
        try
        {
            rented = ArrayPool<byte>.Shared.Rent(e.BytesRecorded);
            Buffer.BlockCopy(e.Buffer, 0, rented, 0, e.BytesRecorded);
            var queue = captureQueue;
            if (Volatile.Read(ref acceptingCaptureData) == 0 || queue is null || !queue.TryAdd(new CaptureBlock(rented, e.BytesRecorded)))
            {
                ArrayPool<byte>.Shared.Return(rented);
                rented = null;
                SetRecordingError(new IOException("Der Aufnahme-Puffer ist voll; die Aufnahme wurde angehalten und die bisherige WAV wird gesichert."));
                Volatile.Write(ref acceptingCaptureData, 0);
                try { capture?.StopRecording(); } catch { /* der Treiber könnte bereits beendet sein */ }
                return;
            }
        }
        catch (Exception exception)
        {
            if (rented is not null) ArrayPool<byte>.Shared.Return(rented);
            SetRecordingError(new IOException("Der Aufnahmeblock konnte nicht sicher in die Pufferwarteschlange übernommen werden.", exception));
            Volatile.Write(ref acceptingCaptureData, 0);
            try { capture?.StopRecording(); } catch { /* der Treiber könnte bereits beendet sein */ }
            return;
        }
    }

    private void DrainCaptureQueue()
    {
        var queue = captureQueue;
        if (queue is null) return;
        foreach (var item in queue.GetConsumingEnumerable())
        {
            try
            {
                recorded?.Write(item.Buffer, 0, item.BytesRecorded);
                var currentFormat = format;
                if (currentFormat is not null)
                {
                    var block = AudioFormatConverter.Decode(item.Buffer, item.BytesRecorded, currentFormat);
                    var channels = Math.Max(1, currentFormat.Channels);
                    var peaks = new float[channels];
                    for (var sample = 0; sample < block.Length; sample++)
                    {
                        var channel = sample % channels;
                        peaks[channel] = Math.Max(peaks[channel], Math.Abs(block[sample]));
                    }
                    LastPeak = peaks.Max();
                    LevelsChanged?.Invoke(this, peaks);
                }
            }
            catch (Exception exception)
            {
                SetRecordingError(exception);
                Volatile.Write(ref acceptingCaptureData, 0);
                try { capture?.StopRecording(); } catch { /* der Treiber könnte bereits beendet sein */ }
            }
            finally { ArrayPool<byte>.Shared.Return(item.Buffer); }
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null) SetRecordingError(e.Exception);
        Volatile.Write(ref acceptingCaptureData, 0);
        CompleteCaptureQueue();
        stopped.Set();
    }

    private void ReleaseCapture()
    {
        if (capture is null) return;
        capture.DataAvailable -= OnDataAvailable;
        capture.RecordingStopped -= OnRecordingStopped;
        try { capture.StopRecording(); } catch { /* Treiber bereits gestoppt */ }
        capture.Dispose();
        capture = null;
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
            Volatile.Write(ref acceptingCaptureData, 0);
            CompleteCaptureQueue();
            try { captureWriterTask?.GetAwaiter().GetResult(); }
            catch (Exception exception) { SetRecordingError(exception); }
            captureWriterTask = null;
            try { recorded?.Dispose(); }
            catch (Exception exception) { SetRecordingError(exception); }
            recorded = null;
            captureQueue?.Dispose();
            captureQueue = null;
        }
    }

    private void CompleteCaptureQueue()
    {
        var queue = captureQueue;
        if (queue is null || queue.IsAddingCompleted) return;
        try { queue.CompleteAdding(); } catch (InvalidOperationException) { }
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
        if (isRecording) Cancel();
        ReleaseCapture();
        DiscardRecordingFile();
        stopped.Dispose();
    }
}
