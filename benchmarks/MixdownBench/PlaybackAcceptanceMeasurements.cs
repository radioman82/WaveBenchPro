using System.Runtime.InteropServices;
using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using WellenEditor;

internal static class PlaybackAcceptanceMeasurements
{
    public static async Task<int> CheckDevicesAndCapture()
    {
        using var enumerator = new MMDeviceEnumerator();
        using var primary = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        using var audio = AudioDocument.FromSamples(Enumerable.Range(0, 48000).SelectMany(i =>
        {
            var value = .01f * MathF.Sin(i * 2 * MathF.PI * 220 / 48000);
            return new[] { value, value };
        }).ToArray(), 2, 48000);
        var project = new MultitrackProject(); project.Add(audio);
        using var session = new PreparedMultitrackSession(project.Snapshot(), project.Tracks);
        using var playback = new PreparedMultitrackPlayback(session);
        var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToArray();
        try
        {
            foreach (var device in devices.Take(2))
            {
                await playback.ConfigurePlaybackAsync(0, session.FrameCount, true);
                using var player = AudioHardwareBackend.Default.CreatePlayback(new() { DeviceId = device.ID });
                var stopped = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
                player.PlaybackStopped += (_, e) => stopped.TrySetResult(e.Exception);
                player.Init(playback); player.Play();
                await Task.Delay(750);
                player.Stop();
                if (await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5)) is { } error) throw error;
                Console.WriteLine($"device_reinitialization=passed device={device.FriendlyName}");
            }
        }
        finally { foreach (var device in devices) device.Dispose(); }
        await playback.ConfigurePlaybackAsync(0, session.FrameCount, true);
        var recovery = Path.Combine(Path.GetTempPath(), $"WaveBenchCaptureCheck_{Guid.NewGuid():N}");
        VerifyRecordingSink? sink = null;
        using var recorder = new RecordingEngine(_ => AudioHardwareBackend.Default.CreateCapture(new() { Backend = AudioBackendKind.WasapiLoopback, DeviceId = primary.ID }),
            recovery, size => new float[size], TimeSpan.FromSeconds(5),
            (path, format) => sink = new VerifyRecordingSink(path, format));
        using (var player = AudioHardwareBackend.Default.CreatePlayback(new() { DeviceId = primary.ID }))
        {
            player.Init(playback);
            recorder.Start(null); player.Play();
            await Task.Delay(3000);
            var recorded = await Task.Run(recorder.Finish);
            player.Stop();
            Console.WriteLine($"loopback_recording_samples={recorded?.Samples.Length ?? 0} raw_file_bytes={sink?.Bytes ?? 0} wav_payload_exact={sink?.Exact ?? false} underruns={playback.UnderrunCount}");
            if (recorded is null || recorded.Value.Samples.Length == 0 || sink is not { Exact: true }) return 1;
        }
        return await playback.StopAsync(TimeSpan.FromSeconds(2)) ? 0 : 1;
    }

    private sealed class VerifyRecordingSink : IRecordingSink
    {
        private readonly string path;
        private readonly WaveFileRecordingSink sink;
        private readonly MemoryStream expected = new();
        public bool Exact { get; private set; }
        public long Bytes => expected.Length;
        public VerifyRecordingSink(string path, WaveFormat format) { this.path = path; sink = new(path, format); }
        public void Write(byte[] buffer, int offset, int count)
        { sink.Write(buffer, offset, count); expected.Write(buffer, offset, count); }
        public void Dispose()
        {
            sink.Dispose();
            using var reader = new WaveFileReader(path);
            var actual = new byte[checked((int)reader.Length)];
            var offset = 0;
            while (offset < actual.Length)
            {
                var read = reader.Read(actual, offset, actual.Length - offset);
                if (read == 0) break;
                offset += read;
            }
            Exact = offset == actual.Length && expected.ToArray().SequenceEqual(actual);
        }
    }

    public static async Task<int> MeasureControls()
    {
        foreach (var capacity in new[] { 4, 8, 16 })
        foreach (var solo in new[] { false, true })
        {
            using var first = AudioDocument.FromSamples(Enumerable.Repeat(.25f, 48000).ToArray(), 1, 48000);
            using var second = AudioDocument.FromSamples(Enumerable.Repeat(.5f, 48000).ToArray(), 1, 48000);
            var project = new MultitrackProject(); project.Add(first); project.Add(second);
            using var session = new PreparedMultitrackSession(project.Snapshot(), project.Tracks);
            using var playback = new PreparedMultitrackPlayback(session, 1024, capacity);
            await playback.ConfigurePlaybackAsync(0, session.FrameCount, true);
            await Task.Delay(100); // Let the producer fill the configured ring.
            if (solo) project.Tracks[1].Solo = true;
            else project.Tracks[0].Muted = true;
            var bytes = new byte[256 * 8];
            var frames = 0;
            var found = false;
            for (var iteration = 0; iteration < 100; iteration++)
            {
                playback.Read(bytes);
                var values = MemoryMarshal.Cast<byte, float>(bytes).ToArray();
                for (var frame = 0; frame < values.Length / 2; frame++)
                    if (values[frame * 2] == .5f) { frames += frame; found = true; break; }
                if (found) break;
                frames += 256;
                await Task.Delay(6); // Paced consumer; not a soundcard latency measurement.
            }
            Console.WriteLine($"control={(solo ? "solo" : "mute")} capacity={capacity} block_frames=1024 rate=48000 frames_until_change={frames} queue_response_ms={frames / 48d:F2} plus_requested_50ms_output={frames / 48d + 50:F2} underruns={playback.UnderrunCount} observed={found}");
            if (!found || playback.UnderrunCount != 0) return 1;
            if (!await playback.StopAsync(TimeSpan.FromSeconds(2))) return 1;
        }
        return 0;
    }
}
