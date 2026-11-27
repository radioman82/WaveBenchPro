using System.Text.Json;
using NAudio.Wave;
using WellenEditor;

internal static class AsioDuplexProbe
{
    internal static int Run(int rate)
    {
        if (rate is not (44100 or 48000)) throw new ArgumentOutOfRangeException(nameof(rate));
        using var control = new AudioControlThread();
        AsioDevice? device = null;
        var stopped = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tone = Enumerable.Range(0, rate).Select(i => .005f * MathF.Sin(2 * MathF.PI * 220 * i / rate)).ToArray();
        long frames = 0, callbacks = 0, allocated = 0, position = 0;
        int sample = 0, channelsVerified = 0;
        void Process(in AsioProcessBuffers buffers)
        {
            var start = GC.GetAllocatedBytesForCurrentThread();
            var left = buffers.GetInput(0);
            var right = buffers.GetInput(1);
            var outputLeft = buffers.GetOutput(0);
            var outputRight = buffers.GetOutput(1);
            if (left.Length != 256 || right.Length != 256 || outputLeft.Length != 256 || outputRight.Length != 256) return;
            for (var i = 0; i < left.Length; i++)
            {
                if (!float.IsFinite(left[i]) || !float.IsFinite(right[i])) return;
                // Independent test tone. Input is never routed to output.
                outputLeft[i] = outputRight[i] = tone[sample];
                if (++sample == tone.Length) sample = 0;
            }
            Volatile.Write(ref channelsVerified, 1);
            Interlocked.Add(ref frames, buffers.Frames);
            Interlocked.Exchange(ref position, buffers.SamplePosition);
            if (Interlocked.Increment(ref callbacks) > 8)
                Interlocked.Add(ref allocated, GC.GetAllocatedBytesForCurrentThread() - start);
        }
        try
        {
            control.Invoke(() =>
            {
                device = AsioDevice.Open("BEHRINGER USB AUDIO");
                device.Stopped += (_, args) => stopped.TrySetResult(args.Exception);
                device.InitDuplex(new AsioDuplexOptions { InputChannels = [0, 1], OutputChannels = [0, 1],
                    SampleRate = rate, BufferSize = 256, Processor = Process });
                Console.WriteLine(JsonSerializer.Serialize(new { device.DriverName, device.CurrentSampleRate,
                    device.FramesPerBuffer, device.InputLatencySamples, device.OutputLatencySamples, monitoring = false }));
                device.Start();
            });
            var unexpectedStop = stopped.Task.Wait(TimeSpan.FromSeconds(5));
            control.Invoke(device!.Stop);
            var error = stopped.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            Console.WriteLine(JsonSerializer.Serialize(new { frames, callbacks, position, allocated,
                channels_verified = channelsVerified == 1, unexpected_stop = unexpectedStop, error = error?.Message }));
            return error is null && !unexpectedStop && channelsVerified == 1 && frames >= rate * 4 && allocated == 0 ? 0 : 1;
        }
        finally { if (device is not null) control.Invoke(device.Dispose); }
    }
}
