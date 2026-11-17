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
    private readonly MemoryStream recorded = new();
    private readonly ManualResetEventSlim stopped = new(false);
    private WasapiCapture? capture;
    private WaveFormat? format;
    private bool isRecording;

    public bool IsRecording => isRecording;
    public WaveFormat? CaptureFormat => format;
    public float LastPeak { get; private set; }
    public event EventHandler<float>? LevelChanged;

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
        recorded.SetLength(0);
        recorded.Position = 0;
        stopped.Reset();
        LastPeak = 0f;
        capture.DataAvailable += OnDataAvailable;
        capture.RecordingStopped += OnRecordingStopped;
        capture.StartRecording();
        isRecording = true;
    }

    /// <summary>Beendet die Aufnahme und gibt die aufgezeichneten Samples zurück.</summary>
    public AudioSelectionData? Finish()
    {
        if (!isRecording) return null;
        isRecording = false;
        capture?.StopRecording();
        if (!stopped.Wait(TimeSpan.FromSeconds(3)))
        {
            // Einige Treiber melden RecordingStopped nicht zuverlässig.
            // Die bereits angekommenen Blöcke sind trotzdem gültig.
        }
        ReleaseCapture();

        var bytes = recorded.ToArray();
        if (bytes.Length == 0 || format is null) return null;
        var samples = AudioFormatConverter.Decode(bytes, bytes.Length, format);
        return samples.Length == 0 ? null : new AudioSelectionData(samples, format.Channels, format.SampleRate);
    }

    /// <summary>Verwirft die laufende Aufnahme ohne Samples zurückzugeben.</summary>
    public void Cancel()
    {
        if (!isRecording) return;
        isRecording = false;
        capture?.StopRecording();
        stopped.Wait(TimeSpan.FromSeconds(3));
        ReleaseCapture();
        recorded.SetLength(0);
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0 || format is null) return;
        recorded.Write(e.Buffer, 0, e.BytesRecorded);

        var block = AudioFormatConverter.Decode(e.Buffer, e.BytesRecorded, format);
        var peak = 0f;
        foreach (var sample in block) peak = Math.Max(peak, Math.Abs(sample));
        LastPeak = peak;
        LevelChanged?.Invoke(this, peak);
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e) => stopped.Set();

    private void ReleaseCapture()
    {
        if (capture is null) return;
        capture.DataAvailable -= OnDataAvailable;
        capture.RecordingStopped -= OnRecordingStopped;
        try { capture.StopRecording(); } catch { /* Treiber bereits gestoppt */ }
        capture.Dispose();
        capture = null;
    }

    public void Dispose()
    {
        if (isRecording) Cancel();
        ReleaseCapture();
        recorded.Dispose();
        stopped.Dispose();
    }
}
