using System.IO;
using NAudio.Vst3;
using NAudio.Wave;
using WellenEditor;
using Xunit;

namespace WellenEditorTests;

public sealed class BlockVstTrackTests
{
    private static TrackEffectSlot Vst()
    {
        var info = new Vst3ClassInfo("fake", "Audio Module Class", "Fake", "Tests", "1", "", "Fx");
        return TrackEffectSlot.ForVst(new Vst3EffectDescriptor("Fake", "Tests", "fake.vst3", info, "Fx"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BlockwiseVstRespectsInsertOrderAndMatchesLegacyTrack(bool gainFirst)
    {
        using var sound = AudioDocument.FromSamples(Enumerable.Range(0, 8000).Select(i => .35f * MathF.Sin(i * .12f)).ToArray(), 1, 48000);
        var project = new MultitrackProject(); project.Add(sound);
        project.Tracks[0].Clips.Add(new MultitrackClip(sound, 3000));
        var gain = new TrackEffectSlot(Guid.NewGuid(), NativeEffectKind.Gain, new EffectSettings { GainDb = 6 });
        var vst = Vst();
        project.Tracks[0].SetEffects(gainFirst ? new[] { gain, vst } : new[] { vst, gain });
        var expected = project.RenderMixdown((_, input, _) => input.Select(x => Math.Clamp(x, -.4f, .4f)).ToArray());
        using var renderer = new BlockMixdownRenderer(project, 257, vstFactory: (_, source) => new ClampProvider(source));
        var block = new float[514];
        var position = 0;
        int count;
        while ((count = renderer.Read(block, 257)) > 0)
        {
            for (var i = 0; i < count * 2; i++) Assert.InRange(Math.Abs(expected[position * 2 + i] - block[i]), 0, 1e-6f);
            position += count;
        }
        Assert.Equal(expected.Length, position * 2);
    }

    [Fact]
    public void ShortVstOutputFailsAndTemporaryFilesAreRemoved()
    {
        var root = Path.Combine(Path.GetTempPath(), $"wavebench_vst_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            using var sound = AudioDocument.FromSamples(new float[8000], 1, 48000);
            var project = new MultitrackProject(); project.Add(sound); project.Tracks[0].SetEffects(new[] { Vst() });
            Assert.Throws<EndOfStreamException>(() => new BlockMixdownRenderer(project, vstFactory: (_, source) => new EmptyProvider(source.WaveFormat), cacheDirectory: root));
            Assert.Empty(Directory.GetFiles(root));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void VstTemporaryTrackExistsOnlyForLifetimeOfRenderer()
    {
        var root = Path.Combine(Path.GetTempPath(), $"wavebench_vst_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            using var sound = AudioDocument.FromSamples(new float[8000], 1, 48000);
            var project = new MultitrackProject(); project.Add(sound); project.Tracks[0].SetEffects(new[] { Vst() });
            using (var renderer = new BlockMixdownRenderer(project, vstFactory: (_, source) => new ClampProvider(source), cacheDirectory: root))
                Assert.Single(Directory.GetFiles(root));
            Assert.Empty(Directory.GetFiles(root));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void CancelDuringVstPreparationPreservesTargetAndDeletesCache()
    {
        var root = Path.Combine(Path.GetTempPath(), $"wavebench_vst_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var target = Path.Combine(root, "mix.wav");
        File.WriteAllText(target, "original");
        try
        {
            using var sound = AudioDocument.FromSamples(new float[50000], 1, 48000);
            var project = new MultitrackProject(); project.Add(sound); project.Tracks[0].SetEffects(new[] { Vst() });
            using var cancellation = new CancellationTokenSource();
            var progress = new InlineProgress(_ => cancellation.Cancel());
            Assert.Throws<OperationCanceledException>(() => BlockMixdownRenderer.WriteWave(project, target, cancellation.Token, progress, vstFactory: (_, source) => new ClampProvider(source), cacheDirectory: root));
            Assert.Equal("original", File.ReadAllText(target));
            Assert.Single(Directory.GetFiles(root));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void RiffLimitIsCheckedBeforePreparingVstOrCreatingFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), $"wavebench_vst_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var target = Path.Combine(root, "mix.wav");
            File.WriteAllText(target, "original");
            using var sound = AudioDocument.FromSamples(new[] { .1f }, 1, 48000);
            var project = new MultitrackProject(); project.Add(sound);
            project.Tracks[0].Clips[0].StartFrame = 536_870_784;
            project.Tracks[0].SetEffects(new[] { Vst() });
            var invoked = false;
            Assert.Throws<NotSupportedException>(() => BlockMixdownRenderer.WriteWave(project, target, CancellationToken.None,
                vstFactory: (_, source) => { invoked = true; return new ClampProvider(source); }, cacheDirectory: root));
            Assert.False(invoked);
            Assert.Equal("original", File.ReadAllText(target));
            Assert.Single(Directory.GetFiles(root));
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class InlineProgress(Action<double> callback) : IProgress<double> { public void Report(double value) => callback(value); }
    private sealed class EmptyProvider(WaveFormat format) : ISampleProvider
    {
        public WaveFormat WaveFormat => format;
        public int Read(float[] buffer, int offset, int count) => 0;
        public int Read(Span<float> buffer) => 0;
    }
    private sealed class ClampProvider(ISampleProvider source) : ISampleProvider
    {
        public WaveFormat WaveFormat => source.WaveFormat;
        public int Read(float[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public int Read(Span<float> buffer)
        {
            var read = source.Read(buffer);
            for (var i = 0; i < read; i++) buffer[i] = Math.Clamp(buffer[i], -.4f, .4f);
            return read;
        }
    }
}
