using NAudio.Vst3;
using System.Security.Cryptography;
using System.Text.Json;

// Exploratory measurement, not a bit-exact regression contract for modulated reverbs.
internal static class VstRepeatMeasurements08
{
    internal static int Run(string path)
    {
        using var module = Vst3Module.Load(path);
        var info = module.GetClasses().First(item => item.Category == "Audio Module Class");
        using var plugin = module.CreatePlugin(info, 48000, 1024);
        var state = plugin.SaveState();
        var parameters = plugin.Parameters.Select(item => item.NormalizedValue).ToArray();
        Console.WriteLine(JsonSerializer.Serialize(new { plugin = info.Name, state_bytes = state.Length,
            state_sha256 = Convert.ToHexString(SHA256.HashData(state)),
            latency_frames = plugin.LatencySamples,
            reset_limit = "ResetEventSchedule resets event timing, not DSP; LoadState brackets setProcessing but does not fully deactivate the component." }));
        var reference = Render(plugin);
        var finite = reference.All(float.IsFinite);
        double maximumParameterError = 0;
        for (var repeat = 0; repeat < 3; repeat++)
        {
            Measure("same_instance_no_reset", repeat, Render(plugin));
            plugin.LoadState(state);
            maximumParameterError = Math.Max(maximumParameterError,
                plugin.Parameters.Select((item, i) => Math.Abs(item.NormalizedValue - parameters[i])).DefaultIfEmpty().Max());
            Measure("same_instance_reload_state", repeat, Render(plugin));
            plugin.ResetEventSchedule();
            Measure("same_instance_event_schedule_reset_only", repeat, Render(plugin));
            using var fresh = module.CreatePlugin(info, 48000, 1024);
            fresh.LoadState(state);
            Measure("independent_instance_same_state", repeat, Render(fresh));
        }
        Console.WriteLine(JsonSerializer.Serialize(new { result = "measurement_completed", finite,
            state_parameter_max_error = maximumParameterError, bit_exact_required = false,
            signal_frames = 48000 * 8, tail_frames = 48000 * 6 }));
        return finite && maximumParameterError <= 1e-6 ? 0 : 1;

        void Measure(string mode, int repeat, float[] values)
        {
            double max = 0, squares = 0, tailSquares = 0, lastSquares = 0;
            for (var i = 0; i < values.Length; i++)
            {
                finite &= float.IsFinite(values[i]);
                var difference = values[i] - reference[i];
                max = Math.Max(max, Math.Abs(difference)); squares += difference * difference;
                if (i >= 48000 * 2 * 2) tailSquares += values[i] * (double)values[i];
                if (i >= 48000 * 7 * 2) lastSquares += values[i] * (double)values[i];
            }
            Console.WriteLine(JsonSerializer.Serialize(new { mode, repeat, max_difference = max,
                rms_difference = Math.Sqrt(squares / values.Length),
                tail_rms = Math.Sqrt(tailSquares / (48000 * 6 * 2)),
                final_second_rms = Math.Sqrt(lastSquares / (48000 * 2)) }));
        }
    }

    private static float[] Render(Vst3Plugin plugin)
    {
        var result = new float[48000 * 8 * 2];
        var input = new float[2048]; var output = new float[2048];
        for (var frame = 0; frame < result.Length / 2; frame += 1024)
        {
            var count = Math.Min(1024, result.Length / 2 - frame);
            Array.Clear(input);
            for (var i = 0; i < count && frame + i < 96000; i++)
                input[i * 2] = input[i * 2 + 1] = .05f * MathF.Sin((frame + i) * 2 * MathF.PI * 220 / 48000);
            plugin.Process(input, output, count);
            output.AsSpan(0, count * 2).CopyTo(result.AsSpan(frame * 2));
        }
        return result;
    }
}
