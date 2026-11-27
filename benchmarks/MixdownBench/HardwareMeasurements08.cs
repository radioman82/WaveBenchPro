using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using WellenEditor;

internal sealed record HardwareRequest08
{
    public string Operation { get; init; } = "playback";
    public AudioDeviceConfiguration Configuration { get; init; } = new();
    public int Seconds { get; init; } = 3;
    public int Cycles { get; init; }
    public int RingBlocks { get; init; } = 8;
    public int? CaptureQueuePackets { get; init; }
}

internal static class HardwareMeasurements08
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    internal static async Task<int> Run(string requestPath)
    {
        var request = JsonSerializer.Deserialize<HardwareRequest08>(File.ReadAllText(requestPath), Json) ?? throw new InvalidDataException("Missing request");
        if (request.Seconds is < 1 or > 3600 || request.Cycles is < 0 or > 100 || request.RingBlocks is not (2 or 4 or 8 or 16))
            throw new ArgumentException("Expected 1–3600 seconds, 0–100 cycles and 2/4/8/16 ring blocks.");
        Console.WriteLine("request=" + JsonSerializer.Serialize(request, Json));
        try { return request.Operation == "capture" ? await Capture(request) : await Playback(request); }
        catch (Exception error)
        {
            Console.WriteLine(JsonSerializer.Serialize(new { result = "failed_or_unsupported", type = error.GetType().Name, hresult = $"0x{error.HResult:X8}", message = error.Message, detail = error.ToString() }, Json));
            return 1;
        }
    }
    private static async Task<int> Playback(HardwareRequest08 request)
    {
        var rate = request.Configuration.SampleRate == 0 ? 48000 : request.Configuration.SampleRate;
        var documents = new List<AudioDocument>();
        var project = new MultitrackProject();
        try
        {
            for (var track = 0; track < 8; track++)
            {
                var samples = new float[rate * 4 * 2];
                for (var frame = 0; frame < rate * 4; frame++)
                    samples[frame * 2] = samples[frame * 2 + 1] = .005f * MathF.Sin(frame * 2 * MathF.PI * (110 + track * 31) / rate);
                var document = AudioDocument.FromSamples(samples, 2, rate); documents.Add(document); project.Add(document);
            }
            using var measuredProcess = Process.GetCurrentProcess();
            var handlesBefore = measuredProcess.HandleCount;
            using var session = new PreparedMultitrackSession(project.Snapshot(), project.Tracks);
            using var source = new PreparedMultitrackPlayback(session, bufferBlocks: request.RingBlocks);
            await source.ConfigurePlaybackAsync(0, session.FrameCount, true);
            var meter = new MeasuredProvider(source);
            using (var player = AudioHardwareBackend.Default.CreatePlayback(request.Configuration))
            {
                var stopped = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
                player.PlaybackStopped += (_, args) => stopped.TrySetResult(args.Exception);
                player.Init(meter);
                Console.WriteLine("actual=" + player.Status);
                var process = Process.GetCurrentProcess(); var cpu = process.TotalProcessorTime;
                var watch = Stopwatch.StartNew();
                player.Play();
                var nextReport = 60;
                while (watch.Elapsed < TimeSpan.FromSeconds(request.Seconds))
                {
                    await Task.Delay(50);
                    if (source.WorkerError is { } workerError) throw workerError;
                    if (stopped.Task.IsCompleted) throw new InvalidOperationException("Unexpected hardware stop", await stopped.Task);
                    if (watch.Elapsed.TotalSeconds >= nextReport)
                    {
                        process.Refresh();
                        Console.WriteLine(JsonSerializer.Serialize(new { result = "playback_progress", elapsed_s = watch.Elapsed.TotalSeconds,
                            handles = process.HandleCount, cpu_s = (process.TotalProcessorTime - cpu).TotalSeconds,
                            peak_working_set_bytes = process.PeakWorkingSet64, underruns = source.UnderrunCount, callbacks = meter.Calls }));
                        nextReport += 60;
                    }
                }
                player.Stop();
                if (await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5)) is { } error) throw error;
                watch.Stop(); process.Refresh();
                Console.WriteLine(JsonSerializer.Serialize(new { result = "playback_completed", tracks = 8, rate,
                    ring_blocks = request.RingBlocks, elapsed_s = watch.Elapsed.TotalSeconds,
                    cpu_s = (process.TotalProcessorTime - cpu).TotalSeconds, peak_working_set_bytes = process.PeakWorkingSet64,
                    underruns = source.UnderrunCount, callbacks = meter.Calls, callback_max_us = meter.MaximumMicroseconds,
                    callback_p50_upper_us = meter.Percentile(.50), callback_p95_upper_us = meter.Percentile(.95), callback_p99_upper_us = meter.Percentile(.99),
                    callback_allocated_bytes = meter.Allocations }, Json));
            }
            // Compare restart resources to an initialized, fully stopped/collected device,
            // not process startup before CLR/COM/audio driver lazy initialization.
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            await Task.Delay(250); measuredProcess.Refresh();
            var handlesWarm = measuredProcess.HandleCount;
            var stoppedCycles = 0;
            var handleCheckpoints = new List<object>();
            for (var cycle = 0; cycle < request.Cycles; cycle++)
            {
                await source.ConfigurePlaybackAsync(cycle * 997 % (rate * 3), rate, true);
                using var player = AudioHardwareBackend.Default.CreatePlayback(request.Configuration);
                var stopped = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
                player.PlaybackStopped += (_, args) => stopped.TrySetResult(args.Exception);
                player.Init(source); player.Play(); await Task.Delay(30); player.Stop();
                if (await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5)) is { } error) throw error;
                stoppedCycles++;
                if (stoppedCycles % 25 == 0) { measuredProcess.Refresh(); handleCheckpoints.Add(new { cycle = stoppedCycles, handles = measuredProcess.HandleCount }); }
            }
            if (!await source.StopAsync(TimeSpan.FromSeconds(3))) throw new TimeoutException("Worker cleanup did not finish");
            var sourceError = source.WorkerError;
            if (sourceError is not null) throw sourceError;
            session.Dispose(); await session.Completion.WaitAsync(TimeSpan.FromSeconds(3));
            measuredProcess.Refresh();
            var handlesAfter = measuredProcess.HandleCount;
            // Diagnostic only, after hardware/worker teardown. Never collect in the audio path.
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            await Task.Delay(250); measuredProcess.Refresh();
            Console.WriteLine(JsonSerializer.Serialize(new { result = "cleanup_completed", cycles = stoppedCycles,
                underruns_total = source.UnderrunCount, handles_before = handlesBefore, handles_after = handlesAfter,
                handles_after_first_device_diagnostic_gc = handlesWarm,
                handles_after_diagnostic_gc = measuredProcess.HandleCount, handle_checkpoints = handleCheckpoints,
                worker_error = "none", session_cleanup = "completed" }, Json));
            return source.UnderrunCount == 0 && meter.Allocations == 0 ? 0 : 1;
        }
        finally { foreach (var document in documents) document.Dispose(); }
    }
    private static async Task<int> Capture(HardwareRequest08 request)
    {
        var recovery = Path.Combine(Path.GetTempPath(), "WaveBench08Capture_" + Guid.NewGuid().ToString("N"));
        VerifiedSink? sink = null;
        IRecordingCapture? hardware = null;
        using var recorder = new RecordingEngine(_ => hardware = AudioHardwareBackend.Default.CreateCapture(request.Configuration), recovery,
            _ => throw new InvalidOperationException("The capture benchmark must not decode the recording."), TimeSpan.FromSeconds(30),
            (path, format) => sink = new VerifiedSink(path, format), request.CaptureQueuePackets, measureCaptureTiming: true);
        var fault = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        recorder.RecordingFaulted += (_, _) => fault.TrySetResult();
        using var process = Process.GetCurrentProcess();
        var cpu = process.TotalProcessorTime;
        var gc = Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray();
        var gcPause = GC.GetTotalPauseDuration();
        recorder.Start(request.Configuration.DeviceId);
        var watch = Stopwatch.StartNew();
        Console.WriteLine("actual=" + recorder.HardwareStatus);
        var format = recorder.CaptureFormat!;
        while (watch.Elapsed.TotalSeconds < request.Seconds && !fault.Task.IsCompleted)
        {
            var remaining = TimeSpan.FromSeconds(request.Seconds) - watch.Elapsed;
            if (remaining <= TimeSpan.Zero) break;
            var delayMs = Math.Clamp((int)Math.Ceiling(remaining.TotalMilliseconds), 1, 60000);
            await Task.WhenAny(fault.Task, Task.Delay(delayMs));
            Console.WriteLine(JsonSerializer.Serialize(new { result = "capture_progress", elapsed_s = watch.Elapsed.TotalSeconds,
                received_bytes = recorder.ReceivedBytes, written_bytes = recorder.WrittenBytes, peak_queue_slots = recorder.PeakQueueSlots }));
        }
        var captureSeconds = watch.Elapsed.TotalSeconds;
        Exception? failure = null;
        bool completed = false;
        try { completed = await Task.Run(recorder.FinishToSink); }
        catch (Exception error) { failure = error; }
        watch.Stop();
        process.Refresh();
        Console.WriteLine(JsonSerializer.Serialize(new { result = completed ? "capture_completed" : "capture_failed", elapsed_s = watch.Elapsed.TotalSeconds,
            acquisition_s = captureSeconds, cpu_s = (process.TotalProcessorTime - cpu).TotalSeconds, peak_working_set_bytes = process.PeakWorkingSet64,
            gc_counts = Enumerable.Range(0, 3).Select(i => GC.CollectionCount(i) - gc[i]).ToArray(), gc_pause_ms = (GC.GetTotalPauseDuration() - gcPause).TotalMilliseconds,
            received_frames = recorder.ReceivedBytes / format.BlockAlign, written_frames = recorder.WrittenBytes / format.BlockAlign,
            sample_rate = format.SampleRate, channels = format.Channels, error = failure?.ToString(),
            received_bytes = recorder.ReceivedBytes, written_bytes = recorder.WrittenBytes, sink_bytes = sink?.Bytes,
            wav_payload_exact = sink?.Exact, wav_frames = sink?.Frames, payload_sha256 = sink?.Hash,
            header_exact = sink?.HeaderExact, packets = recorder.CapturePackets, discontinuities = recorder.Discontinuities, overflow_packets = recorder.OverflowPackets,
            queue_layout = recorder.QueueLayout, queue_peak_slots = recorder.PeakQueueSlots,
            queue_wait_max_ms = recorder.MaximumQueueWaitMs, sink_write_max_ms = recorder.MaximumWriteMs,
            asio_packet_gap_max_ms = (hardware as AsioRecordingCapture)?.MaximumPacketGapMs,
            asio_device_position_transitions = (hardware as AsioRecordingCapture)?.DevicePositionTransitions,
            asio_device_position_deltas_outside_packet_size = (hardware as AsioRecordingCapture)?.DevicePositionDeltasOutsidePacketSize,
            asio_callbacks = (hardware as AsioRecordingCapture)?.CallbackCount,
            asio_callback_allocated_bytes_after_8_warmup = (hardware as AsioRecordingCapture)?.CallbackAllocatedBytes,
            asio_between_callback_allocated_bytes_after_8_warmup = (hardware as AsioRecordingCapture)?.BetweenCallbackAllocatedBytes }, Json));
        var allocationFree = hardware is not AsioRecordingCapture asio || (asio.CallbackAllocatedBytes == 0 && asio.BetweenCallbackAllocatedBytes == 0);
        return completed && sink is { Exact: true, HeaderExact: true, Frames: > 0 } &&
            recorder.ReceivedBytes == recorder.WrittenBytes && recorder.WrittenBytes == sink.Bytes && allocationFree ? 0 : 1;
    }
    private sealed class VerifiedSink : IRecordingSink
    {
        private readonly WaveFileRecordingSink output;
        private readonly IncrementalHash expected = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private readonly string path;
        private readonly WaveFormat format;
        public long Bytes, Frames;
        public bool Exact;
        public bool HeaderExact;
        public string? Hash;
        public VerifiedSink(string path, WaveFormat format) { this.path = path; this.format = format; output = new(path, format); }
        public void Write(byte[] buffer, int offset, int count) { output.Write(buffer, offset, count); expected.AppendData(buffer, offset, count); Bytes += count; }
        public void Dispose()
        {
            output.Dispose();
            var expectedHash = expected.GetHashAndReset(); expected.Dispose();
            using var reader = new WaveFileReader(path); using var actual = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var bytes = new byte[65536]; int count;
            while ((count = reader.Read(bytes, 0, bytes.Length)) > 0) actual.AppendData(bytes, 0, count);
            Frames = reader.Length / reader.WaveFormat.BlockAlign;
            HeaderExact = reader.WaveFormat.SampleRate == format.SampleRate && reader.WaveFormat.Channels == format.Channels
                && reader.WaveFormat.BlockAlign == format.BlockAlign && reader.WaveFormat.BitsPerSample == format.BitsPerSample
                && reader.Length % format.BlockAlign == 0 && reader.WaveFormat.AsStandardWaveFormat().Encoding == format.AsStandardWaveFormat().Encoding;
            var hash = actual.GetHashAndReset(); Hash = Convert.ToHexString(hash);
            Exact = expectedHash.SequenceEqual(hash) && reader.Length == Bytes;
        }
    }
    private sealed class MeasuredProvider(IWaveProvider source) : IWaveProvider, IHardwareReadyWaveProvider
    {
        private readonly long[] histogram = new long[32];
        private long maxTicks;
        public long Calls, Allocations;
        public bool HardwareReady => true;
        public WaveFormat WaveFormat => source.WaveFormat;
        public int Read(byte[] bytes, int offset, int count) => Read(bytes.AsSpan(offset, count));
        public int Read(Span<byte> bytes)
        {
            var allocated = GC.GetAllocatedBytesForCurrentThread(); var start = Stopwatch.GetTimestamp();
            var read = source.Read(bytes); var ticks = Stopwatch.GetTimestamp() - start;
            Interlocked.Add(ref Allocations, GC.GetAllocatedBytesForCurrentThread() - allocated);
            Interlocked.Increment(ref Calls); if (ticks > maxTicks) Interlocked.Exchange(ref maxTicks, ticks);
            var us = ticks * 1000000d / Stopwatch.Frequency; var bucket = 0;
            while (bucket < histogram.Length - 1 && us > Math.Pow(2, bucket)) bucket++;
            Interlocked.Increment(ref histogram[bucket]);
            return read;
        }
        public double MaximumMicroseconds => maxTicks * 1000000d / Stopwatch.Frequency;
        public double Percentile(double fraction)
        {
            var threshold = (long)Math.Ceiling(Calls * fraction); long sum = 0;
            for (var i = 0; i < histogram.Length; i++) { sum += histogram[i]; if (sum >= threshold) return Math.Pow(2, i); }
            return 0;
        }
    }
}
