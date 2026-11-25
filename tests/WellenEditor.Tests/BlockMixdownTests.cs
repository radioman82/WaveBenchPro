using System.IO;
using NAudio.Wave;
using NAudio.Vst3;
using WellenEditor;
using Xunit;

namespace WellenEditorTests;

public sealed class BlockMixdownTests
{
    [Theory]
    [InlineData(256, 44100)]
    [InlineData(1024, 48000)]
    [InlineData(4096, 96000)]
    [InlineData(8192, 192000)]
    public void BlocksMatchReferenceWithOverlapsGapsPanAndStatefulFilter(int blockSize, int rate)
    {
        var signal = Enumerable.Range(0, 10000).Select(i => .4f * MathF.Sin(i * .07f)).ToArray();
        using var first = AudioDocument.FromSamples(signal, 1, rate);
        using var second = AudioDocument.FromSamples(signal.SelectMany(x => new[] { x, -x }).ToArray(), 2, rate);
        var project = new MultitrackProject();
        project.Add(first); project.Add(second);
        project.Tracks[0].Clips.Add(new MultitrackClip(first, 9000));
        project.Tracks[1].StartFrame = 4000;
        project.Tracks[0].Gain = .7; project.Tracks[1].Pan = -.4;
        project.Tracks[0].SetEffects(new[] { new TrackEffectSlot(Guid.NewGuid(), NativeEffectKind.HighPass, new EffectSettings { Frequency = 200 }) });
        var expected = project.RenderMixdown();
        using var renderer = new BlockMixdownRenderer(project, blockSize);
        var actual = new float[expected.Length];
        var block = new float[blockSize * 2];
        var written = 0;
        int count;
        while ((count = renderer.Read(block, blockSize)) > 0)
        {
            Array.Copy(block, 0, actual, written * 2, count * 2);
            written += count;
        }
        Assert.Equal(expected.Length, written * 2);
        for (var index = 0; index < expected.Length; index++)
            Assert.InRange(Math.Abs(expected[index] - actual[index]), 0, 1e-6f);
    }

    [Fact]
    public void CancellationPreservesExistingDestinationAndRemovesTemporaryFile()
    {
        var root = Path.Combine(Path.GetTempPath(), $"wavebench_block_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var target = Path.Combine(root, "mix.wav");
            File.WriteAllText(target, "original");
            using var sound = AudioDocument.FromSamples(new float[10000], 1, 48000);
            var project = new MultitrackProject(); project.Add(sound);
            using var token = new CancellationTokenSource();
            var progress = new InlineProgress(_ => token.Cancel());

            Assert.Throws<OperationCanceledException>(() => BlockMixdownRenderer.WriteWave(project, target, token.Token, progress));

            Assert.Equal("original", File.ReadAllText(target));
            Assert.Single(Directory.GetFiles(root));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void WritesFloatWaveWithExpectedSamplesAndReplacesOldTarget()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wavebench_mix_{Guid.NewGuid():N}.wav");
        try
        {
            File.WriteAllText(path, "previous");
            using var sound = AudioDocument.FromSamples(new[] { .25f, -.5f, 1.25f }, 1, 48000);
            var project = new MultitrackProject(); project.Add(sound);
            BlockMixdownRenderer.WriteWave(project, path, CancellationToken.None);
            using var reader = new WaveFileReader(path);
            var bytes = new byte[(int)reader.Length];
            Assert.Equal(bytes.Length, reader.Read(bytes, 0, bytes.Length));
            var samples = AudioFormatConverter.Decode(bytes, bytes.Length, reader.WaveFormat);
            Assert.Equal(new[] { .25f, .25f, -.5f, -.5f, 1.25f, 1.25f }, samples);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void MutedAndSoloTracksMatchExistingMixer()
    {
        using var a = AudioDocument.FromSamples(Enumerable.Repeat(.1f, 4000).ToArray(), 1, 48000);
        using var b = AudioDocument.FromSamples(Enumerable.Repeat(.3f, 4000).ToArray(), 1, 48000);
        var project = new MultitrackProject(); project.Add(a); project.Add(b);
        project.Tracks[0].Muted = true; project.Tracks[1].Solo = true;
        var expected = project.RenderMixdown();
        using var renderer = new BlockMixdownRenderer(project);
        var actual = new float[expected.Length];
        Assert.Equal(4000, renderer.Read(actual, 4000));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void StreamingEditedWaveUsesItsSegmentTimeline()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wavebench_blocksource_{Guid.NewGuid():N}.wav");
        try
        {
            using (var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(48000, 1)))
                writer.WriteSamples(Enumerable.Range(0, 10000).Select(i => i / 10000f).ToArray(), 0, 10000);
            using var document = AudioDocument.Load(path);
            document.Delete(new Selection(3000, 5000));
            var project = new MultitrackProject(); project.Add(document);
            using var renderer = new BlockMixdownRenderer(project, 1024);
            var block = new float[2048];
            var output = new List<float>();
            int read;
            while ((read = renderer.Read(block, 1024)) > 0) output.AddRange(block.Take(read * 2));
            Assert.Equal(16000, output.Count);
            Assert.Equal(.2999f, output[2999 * 2], 5);
            Assert.Equal(.5f, output[3000 * 2], 5);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void ActiveVstIsNeverSilentlySkipped()
    {
        using var sound = AudioDocument.FromSamples(new float[100], 1, 48000);
        var project = new MultitrackProject(); project.Add(sound);
        var info = new Vst3ClassInfo("fake", "Audio Module Class", "Fake", "Tests", "1", "", "Fx");
        project.Tracks[0].SetEffects(new[] { TrackEffectSlot.ForVst(new Vst3EffectDescriptor("Fake", "Tests", "fake.vst3", info, "Fx")) });
        var error = Assert.Throws<NotSupportedException>(() => new BlockMixdownRenderer(project));
        Assert.Contains("VST", error.Message);
    }

    [Fact]
    public void LockedDestinationRemainsUnchangedAndTempIsRemoved()
    {
        var root = Path.Combine(Path.GetTempPath(), $"wavebench_locked_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var target = Path.Combine(root, "mix.wav");
        File.WriteAllText(target, "original");
        try
        {
            using var sound = AudioDocument.FromSamples(new float[100], 1, 48000);
            var project = new MultitrackProject(); project.Add(sound);
            using (var locked = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.None))
                Assert.Throws<UnauthorizedAccessException>(() => BlockMixdownRenderer.WriteWave(project, target, CancellationToken.None));
            Assert.Equal("original", File.ReadAllText(target));
            Assert.Single(Directory.GetFiles(root));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void InjectedDiskWriteFailurePreservesOldDestinationAndCleansTemp()
    {
        var root = Path.Combine(Path.GetTempPath(), $"wavebench_full_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var target = Path.Combine(root, "mix.wav");
        File.WriteAllText(target, "original");
        try
        {
            using var sound = AudioDocument.FromSamples(new float[10000], 1, 48000);
            var project = new MultitrackProject(); project.Add(sound);
            Assert.Throws<IOException>(() => BlockMixdownRenderer.WriteWave(project, target, CancellationToken.None, openOutput: path => new FailingStream(path)));
            Assert.Equal("original", File.ReadAllText(target));
            Assert.Single(Directory.GetFiles(root));
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class FailingStream(string path) : Stream
    {
        private readonly FileStream inner = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        public override bool CanRead => false;
        public override bool CanSeek => true;
        public override bool CanWrite => true;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (inner.Position + count > 1024) throw new IOException("simulierter voller Datenträger");
            inner.Write(buffer, offset, count);
        }
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }

    private sealed class InlineProgress(Action<double> callback) : IProgress<double>
    {
        public void Report(double value) => callback(value);
    }

    [Fact]
    public void SourcePathAndUnsupportedStretchAreRejectedWithoutReplacingTarget()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wavebench_source_{Guid.NewGuid():N}.wav");
        File.WriteAllText(path, "original");
        try
        {
            using var sound = AudioDocument.FromSamples(new float[100], 1, 48000, path);
            var project = new MultitrackProject(); project.Add(sound);
            Assert.Throws<IOException>(() => BlockMixdownRenderer.WriteWave(project, path, CancellationToken.None));
            project.Tracks[0].Clips[0].DurationScale = 2;
            Assert.Throws<NotSupportedException>(() => new BlockMixdownRenderer(project));
            Assert.Equal("original", File.ReadAllText(path));
        }
        finally { File.Delete(path); }
    }
}
