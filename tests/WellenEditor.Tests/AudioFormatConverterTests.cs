using FluentAssertions;
using NAudio.Wave;
using WellenEditor;
using Xunit;

namespace WellenEditorTests;

public sealed class AudioFormatConverterTests
{
    [Fact]
    public void DecodePcm16InterpretsLittleEndianSignedSamples()
    {
        var bytes = new byte[] { 0x00, 0x40, 0x00, 0x80, 0xFF, 0x7F, 0x00, 0x00 };
        var decoded = AudioFormatConverter.Decode(bytes, bytes.Length, new WaveFormat(44100, 16, 2));

        decoded.Should().HaveCount(4);
        decoded[0].Should().BeApproximately(16384 / 32768f, 1e-6f);
        decoded[1].Should().BeApproximately(-1f, 1e-6f);
        decoded[2].Should().BeApproximately(32767 / 32768f, 1e-6f);
        decoded[3].Should().Be(0f);
    }

    [Fact]
    public void DecodePcm24UsesSignExtension()
    {
        var bytes = new byte[] { 0x00, 0x00, 0x01, 0x00, 0x00, 0x80 };
        var decoded = AudioFormatConverter.Decode(bytes, bytes.Length, new WaveFormat(44100, 24, 1));

        decoded.Should().HaveCount(2);
        decoded[0].Should().BeApproximately(65536 / 8388608f, 1e-9f);
        decoded[1].Should().BeApproximately(-1f, 1e-9f);
    }

    [Fact]
    public void DecodeIeeeFloatKeepsSampleValues()
    {
        var samples = new[] { 0.25f, -0.5f };
        var bytes = samples.SelectMany(BitConverter.GetBytes).ToArray();

        var decoded = AudioFormatConverter.Decode(bytes, bytes.Length, WaveFormat.CreateIeeeFloatWaveFormat(48000, 1));

        decoded.Should().Equal(samples);
    }

    [Fact]
    public void DecodeRejectsUnsupportedEncoding()
    {
        var bytes = new byte[8];
        var act = () => AudioFormatConverter.Decode(bytes, bytes.Length, WaveFormat.CreateALawFormat(8000, 1));

        act.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void ResampleScalesFrameCountAndKeepsChannelsSeparate()
    {
        var samples = new[] { 0f, 1f, 0f, -1f, 0f, 1f, 0f, -1f };

        var result = AudioFormatConverter.Resample(samples, 2, 44100, 22050);

        result.Should().HaveCount(4);
        result[0].Should().BeApproximately(0f, 1e-6f);
        result[1].Should().BeApproximately(1f, 1e-6f);
    }

    [Fact]
    public void ConvertChannelsDownmixesAndDuplicates()
    {
        var stereo = new[] { 1f, -1f, 1f, -1f };

        var mono = AudioFormatConverter.ConvertChannels(stereo, 2, 1);
        var back = AudioFormatConverter.ConvertChannels(mono, 1, 2);

        mono.Should().Equal(0f, 0f);
        back.Should().Equal(0f, 0f, 0f, 0f);
    }

    [Fact]
    public void ConvertHandlesRateAndChannelChangeTogether()
    {
        var source = new AudioSelectionData(new[] { 1f, -1f, 1f, -1f, 1f, -1f, 1f, -1f }, 2, 48000);

        var converted = AudioFormatConverter.Convert(source, 1, 24000);

        converted.Channels.Should().Be(1);
        converted.SampleRate.Should().Be(24000);
        converted.Samples.Length.Should().Be(2);
    }
}
