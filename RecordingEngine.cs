using NAudio.CoreAudioApi;
using NAudio.Wave;
using System.IO;

namespace WellenEditor;

/// <summary>
/// Nimmt über WASAPI vom gewählten Eingabegerät auf und liefert beim Stoppen
/// ein formatfestes AudioSelectionData. Das Pegel-Event kommt vom Capture-Thread;
/// UI-Abonnenten müssen selbst auf den Dispatcher marshallen.
/// </summary>
public sealed class RecordingEngine : IDisposable
{
    private readonly ManualResetEventSlim stopped = new(false);
    private WasapiCapture? capture;
    private WaveFileWriter? recorded;
    private string? recordingPath;
    private WaveFormat? format;
    private bool isRecording;
    private Exception? recordingError;

    public bool IsRecording => isRecording;
    public WaveFormat? CaptureFormat => format;
    public float LastPeak { get; private set; }
    public event EventHandler<float[]>? LevelsChanged;

    public void Start(string? deviceId)
    {
        if (isRecording) throw new InvalidOperationException("Eine Aufnahme läuft bereits.");
        ReleaseCapture();

        using var enumerator = new MMDeviceEnumerator();
        var device = string.IsNullOrWhiteSpace(deviceId)
            ? enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia)
            : enumerator.GetDevice(deviceId);

        capture = new WasapiCapture(device);
        format = capture.WaveFormat;
        stopped.Reset();
        recordingError = null;
        LastPeak = 0f;
        try
        {
            recordingPath = Path.Combine(Path.GetTempPath(), $"WaveBenchPro_Recording_{Guid.NewGuid():N}.wav");
            recorded = new WaveFileWriter(recordingPath, format);
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
        capture?.StopRecording();
        if (!stopped.Wait(TimeSpan.FromSeconds(3)))
        {
            ReleaseCapture();
            DiscardRecordingFile();
            throw new TimeoutException("Das Audiogerät hat das Ende der Aufnahme nicht innerhalb von drei Sekunden bestätigt. Die unvollständige Aufnahme wurde verworfen.");
        }
        ReleaseCapture();

        if (recordingError is { } error)
        {
            DiscardRecordingFile();
            throw new InvalidOperationException("Die Audioaufnahme wurde vom Audiogerät oder Treiber abgebrochen.", error);
        }

        recorded?.Dispose();
        recorded = null;
        if (recordingPath is null || format is null) return null;
        try
        {
            using var reader = new WaveFileReader(recordingPath);
            var channels = reader.WaveFormat.Channels;
            var frameCount = reader.Length / reader.WaveFormat.BlockAlign;
            var sampleCount = checked((int)(frameCount * channels));
            var samples = new float[sampleCount];
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
        if (e.BytesRecorded <= 0 || format is null) return;
        recorded?.Write(e.Buffer, 0, e.BytesRecorded);

        var block = AudioFormatConverter.Decode(e.Buffer, e.BytesRecorded, format);
        var channels = Math.Max(1, format.Channels);
        var peaks = new float[channels];
        for (var sample = 0; sample < block.Length; sample++)
        {
            var channel = sample % channels;
            peaks[channel] = Math.Max(peaks[channel], Math.Abs(block[sample]));
        }
        LastPeak = peaks.Length > 0 ? peaks.Max() : 0f;
        LevelsChanged?.Invoke(this, peaks);
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        recordingError = e.Exception;
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
        recorded?.Dispose();
        recorded = null;
        var path = recordingPath;
        recordingPath = null;
        if (path is null) return;
        try { File.Delete(path); } catch { /* temporäre Datei beim nächsten Start nicht mehr verwenden */ }
    }

    public void Dispose()
    {
        if (isRecording) Cancel();
        ReleaseCapture();
        DiscardRecordingFile();
        stopped.Dispose();
    }
}
