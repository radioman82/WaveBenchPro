using System.Diagnostics;
using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Vst3;
using NAudio.Wave;
using WellenEditor;

internal static class PlaybackMeasurements
{
    public static int CompareMusic(string path)
    {
        using var audio = AudioDocument.Load(path);
        var project = new MultitrackProject(); project.Add(audio);
        project.Tracks[0].Clips[0].DurationScale = 1.25;
        var expected = project.RenderMixdown();
        using var session = new PreparedMultitrackSession(project.Snapshot(), project.Tracks);
        using var renderer = session.OpenRenderer(0, session.FrameCount, false, 1024);
        var block = new float[2048];
        var offset = 0; var maximum = 0d; var squares = 0d;
        int read;
        while ((read = renderer.Read(block, 1024)) > 0)
        {
            for (var i = 0; i < read * 2; i++)
            {
                var delta = Math.Abs(expected[offset + i] - block[i]);
                maximum = Math.Max(maximum, delta); squares += delta * delta;
            }
            offset += read * 2;
        }
        Console.WriteLine($"music={path} stretch=1.25 rate={audio.SampleRate} expected_frames={expected.Length / 2} actual_frames={offset / 2} max={maximum:R} rms={Math.Sqrt(squares / offset):R}");
        return offset == expected.Length && maximum <= 1e-6 ? 0 : 1;
    }
    public static int CheckSymlink()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"WaveBenchSymlink_{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        var source = Path.Combine(folder, "source.wav");
        var link = Path.Combine(folder, "target.wav");
        try
        {
            using (var writer = new WaveFileWriter(source, WaveFormat.CreateIeeeFloatWaveFormat(48000, 1)))
                writer.WriteSamples(new[] { .125f, -.25f }, 0, 2);
            try { File.CreateSymbolicLink(link, source); }
            catch (Exception error) when (error is UnauthorizedAccessException or IOException)
            { Console.WriteLine($"symlink_test=not_executed reason={error.Message}"); return 2; }
            using var audio = AudioDocument.Load(source);
            var project = new MultitrackProject(); project.Add(audio);
            var before = File.ReadAllBytes(source);
            try { BlockMixdownRenderer.WriteWave(project, link, CancellationToken.None); return 1; }
            catch (IOException)
            {
                var intact = before.SequenceEqual(File.ReadAllBytes(source));
                Console.WriteLine($"symlink_target_protected={intact}");
                return intact ? 0 : 1;
            }
        }
        finally { if (File.Exists(link)) File.Delete(link); if (File.Exists(source)) File.Delete(source); Directory.Delete(folder); }
    }
    public static async Task<int> Run(int seconds)
    {
        if (seconds is < 1 or > 3600) throw new ArgumentOutOfRangeException(nameof(seconds));
        using var devices = new MMDeviceEnumerator();
        using var output = devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        Console.WriteLine($"device={output.FriendlyName}");
        var documents = new List<AudioDocument>();
        var project = new MultitrackProject();
        try
        {
            for (var track = 0; track < 8; track++)
            {
                var frequency = 110 + track * 31;
                var values = Enumerable.Range(0, 48000 * 4).SelectMany(i =>
                {
                    var sample = .005f * MathF.Sin(i * 2 * MathF.PI * frequency / 48000);
                    return new[] { sample, sample };
                }).ToArray();
                var document = AudioDocument.FromSamples(values, 2, 48000);
                documents.Add(document); project.Add(document);
            }
            using var session = new PreparedMultitrackSession(project.Snapshot(), project.Tracks);
            using var provider = new PreparedMultitrackPlayback(session);
            await provider.ConfigurePlaybackAsync(0, session.FrameCount, true);
            var meter = new CallbackMeasure(provider);
            using var player = new WasapiOut(output, AudioClientShareMode.Shared, false, 50);
            var stopped = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
            player.PlaybackStopped += (_, args) => stopped.TrySetResult(args.Exception);
            player.Init(meter);
            using var process = Process.GetCurrentProcess();
            var cpuStart = process.TotalProcessorTime;
            var watch = Stopwatch.StartNew();
            player.Play();
            for (var i = 0; i < seconds * 10; i++)
            {
                await Task.Delay(100);
                if (provider.WorkerError is { } error) throw new InvalidOperationException("Playback worker failed", error);
                if (stopped.Task.IsCompleted) throw new InvalidOperationException("Unexpected device stop", await stopped.Task);
            }
            player.Stop();
            await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
            watch.Stop(); process.Refresh();
            Console.WriteLine($"mode=hardware tracks=8 rate=48000 blocks=8 block_frames=1024 latency_requested_ms=50 elapsed_s={watch.Elapsed.TotalSeconds:F2} cpu_s={(process.TotalProcessorTime - cpuStart).TotalSeconds:F3} peak_working_set_bytes={process.PeakWorkingSet64} underruns={provider.UnderrunCount} callbacks={meter.Calls} callback_max_us={meter.MaxTicks * 1e6 / Stopwatch.Frequency:F2} callback_allocated_bytes={meter.Allocated}");
            for (var cycle = 0; cycle < 100; cycle++)
            {
                await provider.ConfigurePlaybackAsync((cycle * 997) % (48000 * 3), 48000, true);
                using var restart = new WasapiOut(output, AudioClientShareMode.Shared, false, 50);
                var end = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
                restart.PlaybackStopped += (_, args) => end.TrySetResult(args.Exception);
                restart.Init(provider);
                restart.Play();
                await Task.Delay(30);
                restart.Stop();
                var error = await end.Task.WaitAsync(TimeSpan.FromSeconds(5));
                if (error is not null) throw error;
            }
            Console.WriteLine($"hardware_start_stop_cycles=100 worker_error={provider.WorkerError?.Message ?? "none"} underruns_total={provider.UnderrunCount}");
            return provider.WorkerError is null ? 0 : 1;
        }
        finally { foreach (var document in documents) document.Dispose(); }
    }

    public static int CheckVst(string path)
    {
        using var module = Vst3Module.Load(path);
        var info = module.GetClasses().First(item => item.Category == "Audio Module Class");
        using var plugin = module.CreatePlugin(info, 48000, 1024);
        var parameters = plugin.Parameters.ToArray();
        var state = plugin.SaveState();
        var before = parameters.Select(item => item.NormalizedValue).ToArray();
        if (parameters.Length > 0) parameters[0].NormalizedValue = before[0] < .5 ? .75 : .25;
        plugin.LoadState(state);
        var difference = parameters.Select((item, i) => Math.Abs(item.NormalizedValue - before[i])).DefaultIfEmpty(0).Max();
        var input = new float[2048]; input[0] = input[1] = .5f;
        var output = new float[2048];
        plugin.Process(input, output, 1024);
        float[] Render(bool compensate)
        {
            using var instance = module.CreatePlugin(info, 48000, 1024);
            instance.LoadState(state);
            var signal = new float[8192]; signal[0] = signal[1] = .5f;
            var provider = new Vst3EffectSampleProvider(new SampleInput(signal), instance)
            { CompensateLatency = compensate, RenderTail = true, MaxTailDuration = TimeSpan.FromSeconds(1) };
            var result = new float[signal.Length];
            var offset = 0;
            while (offset < result.Length)
            {
                var read = provider.Read(result.AsSpan(offset, Math.Min(2048, result.Length - offset)));
                if (read == 0) break;
                offset += read;
            }
            return result;
        }
        var raw = Render(false);
        var aligned = Render(true);
        static int Peak(float[] values) => Enumerable.Range(0, values.Length / 2).MaxBy(i => Math.Abs(values[i * 2]));
        var rawPeak = Peak(raw); var alignedPeak = Peak(aligned);
        var latencyError = Math.Abs((rawPeak - alignedPeak) - plugin.LatencySamples);
        Console.WriteLine($"plugin={info.Name} path={path} state_bytes={state.Length} parameters={parameters.Length} state_parameter_max_error={difference:R} latency_samples={plugin.LatencySamples} impulse_raw_peak_frame={rawPeak} impulse_aligned_peak_frame={alignedPeak} latency_error_frames={latencyError}");
        var testSignal = new float[8192]; testSignal[0] = testSignal[1] = .5f;
        using var audio = AudioDocument.FromSamples(testSignal, 2, 48000);
        var project = new MultitrackProject(); project.Add(audio);
        var descriptor = new Vst3EffectDescriptor(info.Name, info.Vendor, path, info, "Fx");
        var vst = TrackEffectSlot.ForVst(descriptor) with { VstState = state };
        var gain = new TrackEffectSlot(Guid.NewGuid(), NativeEffectKind.Gain, new EffectSettings { GainDb = -6 });
        project.Tracks[0].SetEffects(new[] { gain, vst });
        var expected = project.RenderMixdown();
        var maximum = 0d;
        using (var session = new PreparedMultitrackSession(project.Snapshot(), project.Tracks))
        using (var renderer = session.OpenRenderer(0, session.FrameCount, false, 1024))
        {
            var block = new float[2048];
            var offset = 0;
            int read;
            while ((read = renderer.Read(block, 1024)) > 0)
            {
                for (var i = 0; i < read * 2; i++) maximum = Math.Max(maximum, Math.Abs(expected[offset + i] - block[i]));
                offset += read * 2;
            }
            if (offset != expected.Length) throw new InvalidOperationException("VST frame length differs.");
        }
        project.Tracks[0].SetEffects(new[] { vst with { Enabled = false } });
        var bypass = project.RenderMixdown();
        var bypassError = testSignal.Zip(bypass, (a, b) => Math.Abs(a - b)).Max();
        Console.WriteLine($"vst_insert_order=gain_then_vst reference_vs_prepared_max_error={maximum:R} frames={expected.Length / 2} disabled_vst_bypass_max_error={bypassError:R}");
        return difference <= 1e-6 && latencyError <= 1 && maximum <= 1e-6 && bypassError == 0 && output.All(float.IsFinite) ? 0 : 1;
    }

    private sealed class CallbackMeasure(IWaveProvider source) : IWaveProvider
    {
        public WaveFormat WaveFormat => source.WaveFormat;
        public long Calls, Allocated, MaxTicks;
        public int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public int Read(Span<byte> buffer)
        {
            var allocation = GC.GetAllocatedBytesForCurrentThread();
            var start = Stopwatch.GetTimestamp();
            var read = source.Read(buffer);
            var elapsed = Stopwatch.GetTimestamp() - start;
            Interlocked.Add(ref Allocated, GC.GetAllocatedBytesForCurrentThread() - allocation);
            Interlocked.Increment(ref Calls);
            if (elapsed > MaxTicks) Interlocked.Exchange(ref MaxTicks, elapsed);
            return read;
        }
    }
    private sealed class SampleInput(float[] values) : ISampleProvider
    {
        private int position;
        public WaveFormat WaveFormat => WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        public int Read(float[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public int Read(Span<float> buffer)
        {
            var count = Math.Min(buffer.Length, values.Length - position);
            values.AsSpan(position, count).CopyTo(buffer); position += count; return count;
        }
    }
}
