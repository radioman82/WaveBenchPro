using System.IO;
using System.Text;
using FluentAssertions;
using WellenEditor;
using Xunit;

namespace WellenEditorTests;

/// <summary>
/// Tests für <see cref="WavFileAudioSource"/> mit temporären WAV-Dateien:
/// Metadaten, blockweises Lesen, Lesen am Dateiende und Nachweis der Disk-Streaming-Eigenschaft
/// (kein komplettes Laden der Datei in den Speicher).
/// </summary>
public sealed class WavFileAudioSourceTests : IDisposable
{
    private readonly List<string> tempFiles = new();

    public void Dispose()
    {
        foreach (var f in tempFiles)
        {
            try { File.Delete(f); } catch { /* temporäre Datei löschen, egal */ }
        }
    }

    private string CreateTempWav(short formatTag, int channels, int sampleRate, int bits, byte[] data)
    {
        string path = Path.Combine(Path.GetTempPath(), $"wavsrc_{Guid.NewGuid():N}.wav");
        tempFiles.Add(path);

        int blockAlign = channels * bits / 8;
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var w = new BinaryWriter(fs, Encoding.ASCII);
        w.Write(0x46464952);                 // 'RIFF'
        w.Write(36 + data.Length);            // RIFF-Größe
        w.Write(0x45564157);                 // 'WAVE'
        w.Write(0x20746D66);                 // 'fmt '
        w.Write(16);                         // fmt-Chunk-Größe
        w.Write(formatTag);                  // 1 = PCM, 3 = IEEE-Float
        w.Write((short)channels);
        w.Write(sampleRate);
        w.Write(sampleRate * blockAlign);    // avgBytesPerSec
        w.Write((short)blockAlign);
        w.Write((short)bits);
        w.Write(0x61746164);                 // 'data'
        w.Write(data.Length);
        w.Write(data);
        return path;
    }

    private string CreateExtensiblePcm16Wav(Guid subFormat)
    {
        const int dataBytes = 8;
        var path = Path.Combine(Path.GetTempPath(), $"wavsrc_ext_{Guid.NewGuid():N}.wav");
        tempFiles.Add(path);
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var w = new BinaryWriter(fs, Encoding.ASCII);
        w.Write(0x46464952); w.Write(4 + 8 + 40 + 8 + dataBytes); w.Write(0x45564157);
        w.Write(0x20746D66); w.Write(40); w.Write(unchecked((short)0xFFFE));
        w.Write((short)2); w.Write(48000); w.Write(192000); w.Write((short)4); w.Write((short)16);
        w.Write((short)22); w.Write((short)16); w.Write(3); w.Write(subFormat.ToByteArray());
        w.Write(0x61746164); w.Write(dataBytes); w.Write(new byte[dataBytes]);
        return path;
    }

    /// <summary>Erzeugt PCM16-Daten: Frame i, Kanal c = (i * channels + c) als int16.</summary>
    private static byte[] Pcm16Data(int frames, int channels)
    {
        var data = new byte[frames * channels * 2];
        for (int i = 0; i < frames * channels; i++)
            BitConverter.GetBytes((short)((i % 30000) - 15000)).CopyTo(data, i * 2);
        return data;
    }

    private static byte[] Float32Data(int frames, int channels)
    {
        var data = new byte[frames * channels * 4];
        for (int i = 0; i < frames * channels; i++)
            BitConverter.GetBytes((i % 100) / 200f - 0.25f).CopyTo(data, i * 4);
        return data;
    }

    [Fact]
    public void OpenReadsMetadataFromPcm16Wav()
    {
        string path = CreateTempWav(1, channels: 2, sampleRate: 48000, bits: 16, Pcm16Data(1000, 2));

        using var src = new WavFileAudioSource(path);

        src.SampleRate.Should().Be(48000);
        src.Channels.Should().Be(2);
        src.BitsPerSample.Should().Be(16);
        src.FrameCount.Should().Be(1000L); // long, nicht int
    }

    [Fact]
    public void OpenReadsMetadataFromIeeeFloat32Wav()
    {
        string path = CreateTempWav(3, channels: 1, sampleRate: 44100, bits: 32, Float32Data(5000, 1));

        using var src = new WavFileAudioSource(path);

        src.SampleRate.Should().Be(44100);
        src.Channels.Should().Be(1);
        src.BitsPerSample.Should().Be(32);
        src.FrameCount.Should().Be(5000L);
    }

    [Fact]
    public void ExtensiblePcmRequiresAndReadsTheCompletePcmSubtypeGuid()
    {
        var path = CreateExtensiblePcm16Wav(new Guid("00000001-0000-0010-8000-00aa00389b71"));

        using var source = new WavFileAudioSource(path);
        var decoded = new float[4];

        source.ReadFrames(0, decoded, 0, 2).Should().Be(2);
        source.BitsPerSample.Should().Be(16);
        decoded.Should().Equal(0f, 0f, 0f, 0f);
    }

    [Fact]
    public void ReadFramesReturnsRequestedBlockWithBufferOffset()
    {
        const int frames = 1000, channels = 2;
        string path = CreateTempWav(1, channels, 44100, 16, Pcm16Data(frames, channels));

        using var src = new WavFileAudioSource(path);
        var buffer = new float[(100 + 50) * channels]; // 100 Offset-Frames + 50 Lese-Frames
        int read = src.ReadFrames(startFrame: 200, buffer, bufferOffsetFrames: 100, frameCount: 50);

        read.Should().Be(50);
        // Frame 200, Kanal 0: Sample-Index 400 -> (400 % 30000) - 15000 = -14600
        buffer[100 * channels].Should().BeApproximately(-14600 / 32768f, 1e-6f);
        // Frame 249, Kanal 1: Sample-Index 499 -> 499 - 15000 = -14501
        buffer[(100 + 49) * channels + 1].Should().BeApproximately(-14501 / 32768f, 1e-6f);
        // Vor dem Offset darf nichts stehen
        buffer[0].Should().Be(0f);
    }

    [Fact]
    public void ReadFramesAtAndBeyondEndReturnsZeroOrTruncatedCount()
    {
        const int frames = 100, channels = 1;
        string path = CreateTempWav(3, channels, 8000, 32, Float32Data(frames, channels));

        using var src = new WavFileAudioSource(path);
        var buffer = new float[200 * channels];

        src.ReadFrames(startFrame: frames, buffer, 0, 10).Should().Be(0, "genau am Dateiende ist nichts mehr zu lesen");
        src.ReadFrames(startFrame: frames + 500, buffer, 0, 10).Should().Be(0, "jenseits des Dateendes ist nichts zu lesen");

        int read = src.ReadFrames(startFrame: frames - 10, buffer, 0, 50);
        read.Should().Be(10, "Anfrage über das Ende hinaus wird auf die verfügbaren Frames gekürzt");
    }

    [Fact]
    public void ReadFramesDoesNotLoadWholeFileIntoMemory()
    {
        // 200.000 Frames Stereo 16-bit = 800.000 Byte Audiodaten (~0,8 MB Datei)
        const int frames = 200_000, channels = 2;
        string path = CreateTempWav(1, channels, 44100, 16, Pcm16Data(frames, channels));
        long fileSize = new FileInfo(path).Length;
        fileSize.Should().BeGreaterThan(800_000);

        using var src = new WavFileAudioSource(path);
        var buffer = new float[1000 * channels];

        int read = src.ReadFrames(startFrame: 100_000, buffer, 0, 1000);

        read.Should().Be(1000);
        // Nur der angeforderte Block (1000 Frames * 4 Byte) darf von Disk gelesen worden sein
        src.BytesReadFromDisk.Should().Be(1000L * channels * 2);
        src.BytesReadFromDisk.Should().BeLessThan(fileSize / 10,
            "beim Öffnen und beim Lesen eines Blocks darf nicht die komplette Datei geladen werden");
    }

    [Fact]
    public void PeakCacheBuildsMinMaxPyramidFromDiskBlocks()
    {
        var values = new[] { -.5f, .25f, .75f, -.25f, .1f, -.9f, .4f, .2f };
        var data = new byte[values.Length * sizeof(float)];
        Buffer.BlockCopy(values, 0, data, 0, data.Length);
        var path = CreateTempWav(3, channels: 1, sampleRate: 8000, bits: 32, data);

        using var source = new WavFileAudioSource(path);
        var cache = WaveformPeakCache.Build(source, baseFramesPerPeak: 2);

        cache.Levels.Should().HaveCountGreaterThan(1);
        cache.Levels[0].FramesPerPeak.Should().Be(2);
        cache.Levels[0].Minimums.Should().Equal(-.5f, -.25f, -.9f, .2f);
        cache.Levels[0].Maximums.Should().Equal(.25f, .75f, .1f, .4f);
        cache.SelectLevel(8).FramesPerPeak.Should().Be(8);
    }

    [Fact]
    public void BlockwiseReadingMatchesSingleFullRead()
    {
        const int frames = 5000, channels = 2;
        string path = CreateTempWav(3, channels, 48000, 32, Float32Data(frames, channels));

        using var src = new WavFileAudioSource(path);
        var full = new float[frames * channels];
        src.ReadFrames(0, full, 0, frames).Should().Be(frames);

        var blockwise = new float[frames * channels];
        int done = 0;
        while (done < frames)
        {
            int n = src.ReadFrames(done, blockwise, done, Math.Min(777, frames - done));
            n.Should().BeGreaterThan(0);
            done += n;
        }

        blockwise.Should().Equal(full);
    }

    [Fact]
    public void ReadFramesRejectsInvalidArguments()
    {
        string path = CreateTempWav(1, 1, 8000, 16, Pcm16Data(10, 1));
        using var src = new WavFileAudioSource(path);
        var buffer = new float[10];

        src.Invoking(s => s.ReadFrames(-1, buffer, 0, 1)).Should().Throw<ArgumentOutOfRangeException>();
        src.Invoking(s => s.ReadFrames(0, buffer, -1, 1)).Should().Throw<ArgumentOutOfRangeException>();
        src.Invoking(s => s.ReadFrames(0, buffer, 0, -1)).Should().Throw<ArgumentOutOfRangeException>();
        src.Invoking(s => s.ReadFrames(0, new float[2], 0, 5)).Should().Throw<ArgumentException>(
            "Puffer muss Frames * Channels Samples fassen");
        src.ReadFrames(0, buffer, 0, 0).Should().Be(0);
    }
}
