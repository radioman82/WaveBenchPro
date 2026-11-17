using NAudio.Wave;
using System.IO;
using WellenEditor;
using Xunit;
using FluentAssertions;

namespace WellenEditorTests;

public sealed class AudioDocumentTests
{
    private static AudioDocument CreateDocument()
    {
        var samples = new float[2400 * 2];
        for (var index = 0; index < samples.Length; index++)
            samples[index] = MathF.Sin(index * 0.1f) * 0.5f;
        return AudioDocument.FromSamples(samples, channels: 2, sampleRate: 44100, sourcePath: "test.wav");
    }

    private static float[] CapturePlayback(AudioDocument document, int startFrame, int frames)
    {
        var buffer = new float[frames * document.Channels];
        var offset = 0;
        var provider = document.CreateSampleProvider(startFrame, frames);
        while (offset < buffer.Length)
        {
            var read = provider.Read(buffer, offset, buffer.Length - offset);
            if (read <= 0) break;
            offset += read;
        }
        return buffer;
    }

    [Fact]
    public void DeleteRemovesOnlySelectionAndUndoRestoresPreviousSamples()
    {
        var document = CreateDocument();
        var original = document.Samples.ToArray();
        var selection = new Selection(100, 200);

        document.Delete(selection);

        document.FrameCount.Should().Be(original.Length / 2 - 100);
        document.CanUndo.Should().BeTrue();
        document.Undo();
        document.Samples.Should().Equal(original);
    }

    [Fact]
    public void UndoRedoRestoresStateInBothDirections()
    {
        var document = CreateDocument();
        var original = document.Samples.ToArray();
        document.Delete(new Selection(100, 200));
        var deleted = document.Samples.ToArray();

        document.Undo();
        document.Samples.Should().Equal(original);
        document.CanRedo.Should().BeTrue();

        document.Redo();
        document.Samples.Should().Equal(deleted);
        document.CanUndo.Should().BeTrue();
    }

    [Fact]
    public void NewEditAfterUndoClearsRedoBranch()
    {
        var document = CreateDocument();
        var original = document.Samples.ToArray();
        document.Delete(new Selection(100, 200));
        document.Undo();
        document.Normalize(null);

        document.CanRedo.Should().BeFalse();
        document.Samples.Should().NotEqual(original);
    }

    [Fact]
    public void DeleteAndInsertReconstructSelectionAtBorders()
    {
        var document = CreateDocument();
        var original = document.Samples.ToArray();
        var selection = new Selection(120, 340);
        var extracted = document.Extract(selection);

        document.Delete(selection);
        document.Insert(50, extracted);

        document.FrameCount.Should().Be(original.Length / 2);
        var restored = CapturePlayback(document, 50, 220);
        var expected = new float[220 * 2];
        Array.Copy(original, 120 * 2, expected, 0, expected.Length);
        restored.Should().Equal(expected);
    }

    [Fact]
    public void KeepRetainsOnlySelection()
    {
        var document = CreateDocument();
        var original = document.Samples.ToArray();
        var selection = new Selection(120, 340);

        document.Keep(selection);

        document.FrameCount.Should().Be(220);
        document.Samples.Should().Equal(original.AsSpan(240, 440).ToArray());
    }

    [Fact]
    public void SaveAsWave16BitScalesAndClampsFullScale()
    {
        var document = AudioDocument.FromSamples(new float[] { 1f, -1f, 0.5f, -0.25f }, 2, 44100, "test.wav");
        var path = Path.Combine(Path.GetTempPath(), $"wavebench16_{Guid.NewGuid():N}.wav");
        try
        {
            document.SaveAsWave(path, 16);
            using var reader = new WaveFileReader(path);
            reader.WaveFormat.BitsPerSample.Should().Be(16);
            var bytes = new byte[8];
            reader.ReadExactly(bytes);
            bytes.Length.Should().Be(8);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void SaveAsWave24BitStoresPackedLittleEndianSamples()
    {
        var document = AudioDocument.FromSamples(new float[] { 0.5f, -0.5f, 0f, 0f }, 2, 44100, "test.wav");
        var path = Path.Combine(Path.GetTempPath(), $"wavebench24_{Guid.NewGuid():N}.wav");
        try
        {
            document.SaveAsWave(path, 24);
            using var reader = new WaveFileReader(path);
            reader.WaveFormat.BitsPerSample.Should().Be(24);
            reader.WaveFormat.BlockAlign.Should().Be(6);
            reader.Length.Should().BeGreaterThan(0);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void SaveAsWaveRejectsUnsupportedBitDepth()
    {
        var document = CreateDocument();
        var action = () => document.SaveAsWave("x.wav", 12);
        action.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void SaveAsWaveFloatRemainsSupported()
    {
        var document = CreateDocument();
        var path = Path.Combine(Path.GetTempPath(), $"wavebenchfloat_{Guid.NewGuid():N}.wav");
        try
        {
            document.SaveAsWave(path, 32);
            using var reader = new WaveFileReader(path);
            reader.WaveFormat.BitsPerSample.Should().Be(32);
            reader.WaveFormat.Encoding.Should().Be(WaveFormatEncoding.IeeeFloat);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void LoadRoundTripsWaveFile()
    {
        var samples = new float[1024 * 2];
        for (var index = 0; index < samples.Length; index++) samples[index] = index / (float)samples.Length;
        var source = AudioDocument.FromSamples(samples, 2, 44100, "test.wav");
        var path = Path.Combine(Path.GetTempPath(), $"wavebenchload_{Guid.NewGuid():N}.wav");
        try
        {
            source.SaveAsWave(path, 32);
            var loaded = AudioDocument.Load(path);
            loaded.Channels.Should().Be(2);
            loaded.SampleRate.Should().Be(44100);
            loaded.FrameCount.Should().Be(1024);
            loaded.Samples.Should().Equal(samples);
        }
        finally { File.Delete(path); }
    }
}