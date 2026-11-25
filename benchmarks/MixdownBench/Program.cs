using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using NAudio.Wave;
using WellenEditor;

if (args.Length == 2 && args[0] == "vst-check") return PlaybackMeasurements.CheckVst(args[1]);
if (args.Length == 2 && args[0] == "compare") return PlaybackMeasurements.CompareMusic(args[1]);
if (args.Length == 1 && args[0] == "symlink-check") return PlaybackMeasurements.CheckSymlink();
if (args.Length == 2 && args[0] == "playback" && int.TryParse(args[1], out var playbackSeconds))
    return await PlaybackMeasurements.Run(playbackSeconds);

if (args.Length != 2 || (args[0] != "old" && args[0] != "block" && args[0] != "block-full" && args[0] != "block-rate" && args[0] != "block-stretch" && args[0] != "independent") || !int.TryParse(args[1], out var seconds) || seconds < 1)
{
    Console.Error.WriteLine("Usage: MixdownBench old|block|block-full|block-rate|block-stretch <seconds>");
    return 2;
}
const int rate = 48000;
if (args[0] == "independent") return MeasureIndependent(seconds);
var sourcePath = Path.Combine(Path.GetTempPath(), $"WaveBenchBenchSource_{Guid.NewGuid():N}.wav");
var fullSource = args[0] is "block-full" or "block-rate" or "block-stretch";
if (fullSource)
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
using var sound = fullSource ? AudioDocument.Load(sourcePath) : AudioDocument.FromSamples(new float[rate * 2], 2, rate);
using var rateAnchor = args[0] == "block-rate" ? AudioDocument.FromSamples(new float[2], 2, 44100) : null;
var project = new MultitrackProject();
if (rateAnchor is not null) { project.Add(rateAnchor); project.Tracks[0].Muted = true; }
for (var index = 0; index < 8; index++)
{
    project.Add(sound);
    var track = project.Tracks[^1];
    if (args[0] == "block-stretch") track.Clips[0].DurationScale = 1.05;
    if (seconds > 1 && !fullSource) track.Clips.Add(new MultitrackClip(sound, (long)(seconds - 1) * rate));
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

static int MeasureIndependent(int seconds)
{
    const int sampleRate = 48000;
    var directory = Path.Combine(Path.GetTempPath(), $"WaveBenchIndependent_{Guid.NewGuid():N}");
    var required = checked((long)seconds * sampleRate * 8 * 9 + 2L * 1024 * 1024 * 1024);
    if (new DriveInfo(Path.GetPathRoot(directory)!).AvailableFreeSpace < required)
        throw new IOException($"Benchmark requires at least {required} free bytes.");
    Directory.CreateDirectory(directory);
    var paths = new List<string>();
    var documents = new List<AudioDocument>();
    try
    {
        var block = new float[65536];
        for (var i = 0; i < block.Length / 2; i++) block[2 * i] = block[2 * i + 1] = .1f * MathF.Sin(i * .07f);
        var bytes = new byte[block.Length * sizeof(float)];
        Buffer.BlockCopy(block, 0, bytes, 0, bytes.Length);
        for (var track = 0; track < 8; track++)
        {
            var source = Path.Combine(directory, $"source{track}.wav");
            paths.Add(source);
            using var writer = new WaveFileWriter(source, WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2));
            long remaining = checked((long)seconds * sampleRate * 8);
            while (remaining > 0)
            {
                var count = (int)Math.Min(bytes.Length, remaining);
                writer.Write(bytes, 0, count);
                remaining -= count;
            }
        }
        var project = new MultitrackProject();
        foreach (var source in paths) { var document = AudioDocument.Load(source); documents.Add(document); project.Add(document); }
        var target = Path.Combine(directory, "output.wav");
        var watch = Stopwatch.StartNew();
        BlockMixdownRenderer.WriteWave(project, target, CancellationToken.None);
        watch.Stop();
        using var process = Process.GetCurrentProcess(); process.Refresh();
        Console.WriteLine($"mode=independent seconds={seconds} tracks=8 non_silent=true non_sparse=true elapsed_ms={watch.Elapsed.TotalMilliseconds:F0} peak_working_set_bytes={process.PeakWorkingSet64} source_bytes={paths.Sum(p => new FileInfo(p).Length)} output_bytes={new FileInfo(target).Length} allocated_bytes={GC.GetTotalAllocatedBytes(true)}");
        return 0;
    }
    finally
    {
        foreach (var document in documents) document.Dispose();
        if (Directory.Exists(directory))
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*.wav", SearchOption.TopDirectoryOnly)) File.Delete(file);
            Directory.Delete(directory);
        }
    }
}

internal static class SparseFile
{
    [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    internal static extern bool MarkSparse(SafeFileHandle device, uint code, IntPtr input, uint inputLength, IntPtr output, uint outputLength, out uint returned, IntPtr overlapped);
}
