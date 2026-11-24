using NAudio.Wave;
using System.IO;
using System.Runtime.InteropServices;
using WellenEditor;
using Xunit;
using FluentAssertions;

namespace WellenEditorTests;

public sealed class AudioDocumentTests
{
    [Fact]
    public void MultitrackMixdownAppliesStartMuteSoloAndGain()
    {
        var first = AudioDocument.FromSamples(new[] { .5f, .5f, .5f, .5f }, 2, 48000);
        var second = AudioDocument.FromSamples(new[] { .25f, .25f, .25f, .25f }, 2, 48000);
        var project = new MultitrackProject();
        project.Add(first);
        project.Add(second);
        project.Tracks[1].StartFrame = 1;
        project.Tracks[1].Gain = 2;

        project.RenderMixdown().Should().Equal(.5f, .5f, 1f, 1f, .5f, .5f);

        project.Tracks[1].Muted = true;
        project.RenderMixdown().Should().Equal(.5f, .5f, .5f, .5f);
    }

    [Fact]
    public void LoopingWaveProviderRepeatsWithoutEndingAtClipBoundary()
    {
        var source = new ArrayWaveProvider(new[] { .25f, -.25f }, 0, 2, WaveFormat.CreateIeeeFloatWaveFormat(48000, 1));
        using var loop = new LoopingWaveProvider(source);
        var bytes = new byte[sizeof(float) * 6];

        loop.Read(bytes.AsSpan()).Should().Be(bytes.Length);

        MemoryMarshal.Cast<byte, float>(bytes).ToArray().Should().Equal(.25f, -.25f, .25f, -.25f, .25f, -.25f);
    }

    [Fact]
    public void SpectrogramCacheBuildsFrequencyImageFromAudioFrames()
    {
        var samples = Enumerable.Range(0, 8192).Select(index => MathF.Sin(index * .12f)).ToArray();
        using var source = new MemoryAudioSource(samples, 1, 48000);

        var cache = SpectrogramCache.Build(source, CancellationToken.None);

        cache.Image.PixelWidth.Should().BeGreaterThanOrEqualTo(192);
        cache.Image.PixelHeight.Should().Be(512);
    }

    private static string CreateStreamingTestWave(int frames, int channels = 2)
    {
        var path = Path.Combine(Path.GetTempPath(), $"wavebench_stream_{Guid.NewGuid():N}.wav");
        using var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(48000, channels));
        var samples = new float[frames * channels];
        for (var index = 0; index < samples.Length; index++) samples[index] = (index % 101) / 100f - .5f;
        writer.WriteSamples(samples, 0, samples.Length);
        return path;
    }

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
            var read = provider.Read(buffer.AsSpan(offset, buffer.Length - offset));
            if (read <= 0) break;
            offset += read;
        }
        return buffer;
    }

    [Fact]
    public void LoadWaveKeepsLargeSourceStreamedAndCreatesBoundedWaveformPreview()
    {
        var path = CreateStreamingTestWave(20_000);
        try
        {
            var document = AudioDocument.Load(path);

            document.IsStreaming.Should().BeTrue();
            document.FrameCount.Should().Be(20_000);
            document.WaveformSamples.Length.Should().Be(8_192 * 2,
                "die Ansicht soll nur eine kleine Vorschau statt aller Samples halten");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void StreamedDocumentCreatesDiskBackedWaveProviderForSelectedRange()
    {
        var path = CreateStreamingTestWave(2_000);
        try
        {
            var document = AudioDocument.Load(path);
            var provider = document.CreateWaveProvider(100, 25);
            try
            {
                var bytes = new byte[25 * 2 * sizeof(float)];
                provider.Read(bytes.AsSpan()).Should().Be(bytes.Length);
                provider.Read(bytes.AsSpan()).Should().Be(0);
                document.IsStreaming.Should().BeTrue("Wiedergabe darf die Quelldatei nicht materialisieren");
            }
            finally { (provider as IDisposable)?.Dispose(); }
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void StreamedDeleteIsNonDestructiveAndUndoRedoRestoresTimeline()
    {
        var path = CreateStreamingTestWave(100);
        try
        {
            var document = AudioDocument.Load(path);

            document.Delete(new Selection(20, 50));
            document.IsStreaming.Should().BeTrue();
            document.FrameCount.Should().Be(70);
            document.CanUndo.Should().BeTrue();

            document.Undo();
            document.IsStreaming.Should().BeTrue();
            document.FrameCount.Should().Be(100);
            document.CanRedo.Should().BeTrue();

            document.Redo();
            document.FrameCount.Should().Be(70);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void StreamedFadeIsRenderedByPlaybackProviderWithoutMaterializingSource()
    {
        var path = CreateStreamingTestWave(16);
        try
        {
            var document = AudioDocument.Load(path);
            document.FadeIn(new Selection(0, 4));

            var provider = document.CreateWaveProvider(0, 4);
            try
            {
                var bytes = new byte[4 * 2 * sizeof(float)];
                provider.Read(bytes.AsSpan()).Should().Be(bytes.Length);
                BitConverter.ToSingle(bytes, 0).Should().Be(0f, "der erste Frame eines Fade-Ins hat Gain 0");
                document.IsStreaming.Should().BeTrue();
            }
            finally { (provider as IDisposable)?.Dispose(); }
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void StreamedWaveExportsEditedTimelineWithoutMaterializingDocument()
    {
        var sourcePath = CreateStreamingTestWave(100);
        var targetPath = Path.Combine(Path.GetTempPath(), $"wavebench_export_{Guid.NewGuid():N}.wav");
        try
        {
            var document = AudioDocument.Load(sourcePath);
            document.Delete(new Selection(30, 60));

            document.SaveAsWave(targetPath, 32);

            using var exported = new WavFileAudioSource(targetPath);
            exported.FrameCount.Should().Be(70);
            document.IsStreaming.Should().BeTrue("ein Export darf die Quelldatei nicht materialisieren");

            document.UpdateSourcePath(targetPath);
            File.Delete(sourcePath);
            var provider = document.CreateWaveProvider(0, 1);
            try
            {
                provider.Read(new byte[2 * sizeof(float)]).Should().Be(2 * sizeof(float));
            }
            finally { (provider as IDisposable)?.Dispose(); }
        }
        finally
        {
            File.Delete(sourcePath);
            if (File.Exists(targetPath)) File.Delete(targetPath);
        }
    }

    [Fact]
    public void DeleteMarksDocumentAsModified()
    {
        var document = CreateDocument();

        document.Delete(new Selection(100, 200));

        document.IsModified.Should().BeTrue();
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
    public void SaveRangeAsWaveWritesOnlySelectedFrames()
    {
        var document = CreateDocument();
        var expected = document.Samples.AsSpan(100 * 2, 200 * 2).ToArray();
        var path = Path.Combine(Path.GetTempPath(), $"wavebenchrange_{Guid.NewGuid():N}.wav");
        try
        {
            document.SaveRangeAsWave(path, 100, 200, 32);

            using var reader = new AudioFileReader(path);
            reader.WaveFormat.SampleRate.Should().Be(44100);
            reader.WaveFormat.Channels.Should().Be(2);
            var actual = new float[expected.Length];
            var read = reader.Read(actual.AsSpan());
            read.Should().Be(expected.Length);
            actual.Should().Equal(expected);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void SaveRangeAsWaveRejectsRangeBeyondDocument()
    {
        var document = CreateDocument();
        var action = () => document.SaveRangeAsWave("x.wav", 2300, 200, 16);
        action.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void SaveRangeAsMp3WritesNonEmptyFileForSelection()
    {
        var document = CreateDocument();
        var path = Path.Combine(Path.GetTempPath(), $"wavebenchrangemp3_{Guid.NewGuid():N}.mp3");
        try
        {
            document.SaveRangeAsMp3(path, 100, 200, 128);
            File.ReadAllBytes(path).Length.Should().BeGreaterThan(100);
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

    [Fact]
    public void InsertConvertsForeignRecordingFormatBeforeAppending()
    {
        var document = CreateDocument();
        var originalFrames = document.FrameCount;
        var recording = new AudioSelectionData(new[] { 1f, -1f, 1f, -1f }, Channels: 1, SampleRate: 22050);

        document.Insert(100, recording);

        document.Channels.Should().Be(2);
        document.SampleRate.Should().Be(44100);
        // 4 Mono-Frames bei 22,05 kHz ergeben 8 Frames bei 44,1 kHz.
        document.FrameCount.Should().Be(originalFrames + 8);
    }

    [Fact]
    public void SaveAsMp3WritesNonEmptyMpegStream()
    {
        var document = CreateDocument();
        var path = Path.Combine(Path.GetTempPath(), $"wavebenchmp3_{Guid.NewGuid():N}.mp3");
        try
        {
            document.SaveAsMp3(path, 128);

            var bytes = File.ReadAllBytes(path);
            bytes.Length.Should().BeGreaterThan(1000);
            var frameSync = bytes[0] == 0xFF && (bytes[1] & 0xE0) == 0xE0;
            var id3 = bytes[0] == (byte)'I' && bytes[1] == (byte)'D' && bytes[2] == (byte)'3';
            (frameSync || id3).Should().BeTrue();
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void SaveAsMp3RejectsInvalidBitrate()
    {
        var document = CreateDocument();
        var act = () => document.SaveAsMp3("x.mp3", 999);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(".flac")]
    [InlineData(".ogg")]
    [InlineData(".opus")]
    public void SoundFileFormatsExportAndLoadWithoutExternalSetup(string extension)
    {
        var samples = Enumerable.Range(0, 960).Select(index => MathF.Sin(index * .1f) * .4f).ToArray();
        var document = AudioDocument.FromSamples(samples, 2, 48000);
        var path = Path.Combine(Path.GetTempPath(), $"wavebench_format_{Guid.NewGuid():N}{extension}");
        try
        {
            switch (extension)
            {
                case ".flac": document.SaveAsFlac(path); break;
                case ".ogg": document.SaveAsOgg(path); break;
                case ".opus": document.SaveAsOpus(path); break;
            }

            File.Exists(path).Should().BeTrue();
            File.ReadAllBytes(path).Length.Should().BeGreaterThan(100);
            var loaded = AudioDocument.Load(path);
            loaded.Channels.Should().Be(2);
            loaded.SampleRate.Should().Be(48000);
            loaded.FrameCount.Should().BeGreaterThan(0);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
