using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using NAudio.Wave;
using NAudio.Wave.Asio;
using WellenEditor;

internal static class AsioHandleMeasurements
{
    internal static int Run(string stage, int cycles)
    {
        if (stage is not ("open" or "buffer" or "start" or "adapter") || cycles is < 1 or > 1000)
            throw new ArgumentException("asio-handles open|buffer|start|adapter 1..1000");
        var configuration = new AudioDeviceConfiguration { Backend = AudioBackendKind.Asio,
            DeviceId = "BEHRINGER USB AUDIO", SampleRate = 48000, AsioBufferFrames = 256 };
        var signal = new SilentProvider();
        using var process = Process.GetCurrentProcess();
        var watch = Stopwatch.StartNew();
        void Cycle()
        {
            if (stage == "adapter")
            {
                using var playback = AudioHardwareBackend.Default.CreatePlayback(configuration);
                playback.Init(signal);
                playback.Play();
                Thread.Sleep(30);
                playback.Stop();
                return;
            }
            using var control = new AudioControlThread();
            control.Invoke(() =>
            {
                var raw = AsioDriver.GetAsioDriverByName(configuration.DeviceId!);
                AsioDriverExt device;
                try { device = new AsioDriverExt(raw); }
                catch { raw.ReleaseComAsioDriver(); throw; }
                if (stage == "open") { raw.ReleaseComAsioDriver(); return; }

                var created = false;
                var started = false;
                try
                {
                    if (!device.IsSampleRateSupported(48000)) throw new NotSupportedException("48 kHz unavailable.");
                    if ((int)raw.GetSampleRate() != 48000) device.SetSampleRate(48000);
                    device.CreateBuffers(2, 0, 256);
                    created = true;
                    var silence = new byte[1024]; // BEHRINGER's observed Int32LSB.
                    var native = device.Capabilities.OutputChannelInfos.Take(2);
                    if (native.Count() != 2 || native.Any(ch => ch.type != AsioSampleType.Int32LSB))
                        throw new NotSupportedException("Unexpected native output format.");
                    device.FillBufferCallback = (_, outputs) =>
                    {
                        foreach (var output in outputs) Marshal.Copy(silence, 0, output, silence.Length);
                    };
                    if (stage == "start") { started = true; device.Start(); Thread.Sleep(30); }
                }
                finally
                {
                    // Abort this measurement on native failure; preserve live buffers/COM ownership.
                    if (started) Check(raw.Stop(), "stop");
                    if (created) Check(raw.DisposeBuffers(), "dispose buffers");
                    raw.ReleaseComAsioDriver();
                    GC.KeepAlive(device);
                }
            });
        }
        for (var i = 0; i < 5; i++) Cycle();
        Snapshot(0);
        for (var index = 1; index <= cycles; index++)
        {
            Cycle();
            if (index is 100 or 500 or 1000 || index == cycles) Snapshot(index);
        }
        return 0;

        void Snapshot(int cycle)
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            Thread.Sleep(300);
            process.Refresh();
            Console.WriteLine(JsonSerializer.Serialize(new { stage, cycle, elapsed_s = watch.Elapsed.TotalSeconds,
                handles = process.HandleCount, threads = process.Threads.Count, types = ProcessHandleTypes.Read(process.Handle) }));
        }
    }

    private static void Check(AsioError value, string operation)
    {
        if (value is not (AsioError.ASE_OK or AsioError.ASE_SUCCESS))
            throw new InvalidOperationException($"ASIO {operation}: {value}");
    }

    private sealed class SilentProvider : IWaveProvider, IHardwareReadyWaveProvider
    {
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        public bool HardwareReady => true;
        public int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public int Read(Span<byte> buffer) { buffer.Clear(); return buffer.Length; }
    }
}

internal static class ProcessHandleTypes
{
    // Local process snapshot only. Query object types without resolving names or inspecting other processes.
    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr process, int infoClass, IntPtr buffer, int size, out int required);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryObject(IntPtr handle, int infoClass, IntPtr buffer, int size, out int required);

    internal static Dictionary<string, int> Read(IntPtr process)
    {
        const int processHandleInformation = 51;
        const int objectTypeInformation = 2;
        var capacity = 64 * 1024;
        var typeBuffer = Marshal.AllocHGlobal(4096);
        try
        {
            while (capacity <= 16 * 1024 * 1024)
            {
                var entries = Marshal.AllocHGlobal(capacity);
                try
                {
                    var status = NtQueryInformationProcess(process, processHandleInformation, entries, capacity, out var required);
                    if (status == unchecked((int)0xc0000004))
                    { capacity = Math.Max(capacity * 2, required); continue; }
                    if (status != 0) throw new InvalidOperationException($"Process handle snapshot failed: 0x{status:X8}");
                    var names = new Dictionary<int, string>();
                    var counts = new Dictionary<string, int>();
                    var count = Marshal.ReadIntPtr(entries).ToInt64();
                    var entrySize = 3 * IntPtr.Size + 4 * sizeof(int);
                    for (var i = 0; i < count; i++)
                    {
                        var entry = IntPtr.Add(entries, 2 * IntPtr.Size + i * entrySize);
                        var index = Marshal.ReadInt32(entry, 3 * IntPtr.Size + sizeof(int));
                        if (!names.TryGetValue(index, out var name))
                        {
                            var result = NtQueryObject(Marshal.ReadIntPtr(entry), objectTypeInformation, typeBuffer, 4096, out _);
                            var namePointer = result == 0 ? Marshal.ReadIntPtr(typeBuffer, IntPtr.Size) : IntPtr.Zero;
                            name = namePointer != IntPtr.Zero ? Marshal.PtrToStringUni(namePointer, Marshal.ReadInt16(typeBuffer) / 2) : null;
                            if (name is not null) names[index] = name;
                        }
                        name ??= $"Type {index}";
                        counts[name] = counts.GetValueOrDefault(name) + 1;
                    }
                    return counts;
                }
                finally { Marshal.FreeHGlobal(entries); }
            }
            throw new InvalidOperationException("Handle snapshot exceeded the diagnostic limit.");
        }
        finally { Marshal.FreeHGlobal(typeBuffer); }
    }
}
