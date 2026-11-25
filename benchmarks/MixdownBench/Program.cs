using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using NAudio.Wave;
using WellenEditor;

if (args.Length != 2 || (args[0] != "old" && args[0] != "block" && args[0] != "block-full") || !int.TryParse(args[1], out var seconds) || seconds < 1)
{
    Console.Error.WriteLine("Usage: MixdownBench old|block|block-full <seconds>");
    return 2;
}
const int rate = 48000;
var sourcePath = Path.Combine(Path.GetTempPath(), $"WaveBenchBenchSource_{Guid.NewGuid():N}.wav");
if (args[0] == "block-full")
{
    using var file = new FileStream(sourcePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
    if (!SparseFile.MarkSparse(file.SafeFileHandle, 0x000900C4, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
        throw new IOException("Could not create a sparse benchmark source.");
    using var header = new BinaryWriter(file, System.Text.Encoding.ASCII, leaveOpen: true);
    var bytes = checked((uint)((long)seconds * rate * 8));
    header.Write(0x46464952); header.Write(checked(bytes + 36)); header.Write(0x45564157);
    header.Write(0x20746D66); header.Write(16); header.Write((short)3); header.Write((short)2);
    header.Write(rate); header.Write(rate * 8); header.Write((short)8); header.Write((short)32);
    header.Write(0x61746164); header.Write(bytes); header.Flush();
    file.SetLength(44L + bytes);
}
using var sound = args[0] == "block-full" ? AudioDocument.Load(sourcePath) : AudioDocument.FromSamples(new float[rate * 2], 2, rate);
var project = new MultitrackProject();
for (var index = 0; index < 8; index++)
{
    project.Add(sound);
    if (seconds > 1 && args[0] != "block-full") project.Tracks[index].Clips.Add(new MultitrackClip(sound, (long)(seconds - 1) * rate));
}
var path = Path.Combine(Path.GetTempPath(), $"WaveBenchBench_{Guid.NewGuid():N}.wav");
try
{
    var watch = Stopwatch.StartNew();
    if (args[0] != "old")
        BlockMixdownRenderer.WriteWave(project, path, CancellationToken.None);
    else
    {
        var samples = project.RenderMixdown();
        using var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(rate, 2));
        var buffer = new byte[65536];
        for (var offset = 0; offset < samples.Length;)
        {
            var count = Math.Min(buffer.Length / sizeof(float), samples.Length - offset);
            Buffer.BlockCopy(samples, offset * sizeof(float), buffer, 0, count * sizeof(float));
            writer.Write(buffer, 0, count * sizeof(float));
            offset += count;
        }
    }
    watch.Stop();
    using var process = Process.GetCurrentProcess();
    process.Refresh();
    Console.WriteLine($"mode={args[0]} seconds={seconds} tracks=8 elapsed_ms={watch.Elapsed.TotalMilliseconds:F0} peak_working_set_bytes={process.PeakWorkingSet64} output_bytes={new FileInfo(path).Length} allocated_bytes={GC.GetTotalAllocatedBytes(true)}");
    return 0;
}
finally { sound.Dispose(); if (File.Exists(path)) File.Delete(path); if (File.Exists(sourcePath)) File.Delete(sourcePath); }

internal static class SparseFile
{
    [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    internal static extern bool MarkSparse(SafeFileHandle device, uint code, IntPtr input, uint inputLength, IntPtr output, uint outputLength, out uint returned, IntPtr overlapped);
}
