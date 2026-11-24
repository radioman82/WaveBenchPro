using System.Runtime.InteropServices;
using NAudio.Wave;
using WellenEditor;
using Xunit;

namespace WellenEditorTests;

public sealed class MeteringTests
{
    [Theory]
    [InlineData(.1f, -20)]
    [InlineData(.5f, -6.0206)]
    [InlineData(1f, 0)]
    public void SineHasExpectedPeakAndRmsInDbfs(float amplitude, double peakDb)
    {
        // Whole cycles, with exact positive and negative peaks.
        var samples = Enumerable.Range(0, 4800)
            .Select(i => amplitude * (float)Math.Sin(2 * Math.PI * i / 48)).ToArray();
        using var meter = new MeteringWaveProvider(new ArrayWaveProvider(samples, 0, samples.Length,
            WaveFormat.CreateIeeeFloatWaveFormat(48000, 1)));
        var bytes = new byte[samples.Length * sizeof(float)];
        Assert.Equal(bytes.Length, meter.Read(bytes, 0, bytes.Length));
        Assert.Equal(peakDb, 20 * Math.Log10(meter.Peaks[0]), 3);
        Assert.Equal(peakDb - 3.0103, 20 * Math.Log10(meter.Rms[0]), 3);
        Assert.Equal(amplitude >= 1, meter.Clipped);
        Assert.Equal(samples, MemoryMarshal.Cast<byte, float>(bytes).ToArray());
    }

    [Fact]
    public void StereoChannelsAndPartialBufferAreMeasuredIndependently()
    {
        var samples = new[] { .1f, -.5f, -.1f, .5f, 0f, 0f };
        using var meter = new MeteringWaveProvider(new ArrayWaveProvider(samples, 0, samples.Length,
            WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)));
        var bytes = new byte[80];
        Assert.Equal(16, meter.Read(bytes, 8, 16));
        Assert.Equal(new[] { .1f, .5f }, meter.Peaks);
        Assert.Equal(new[] { .1f, .5f }, meter.Rms);
        Assert.False(meter.Clipped);
        Assert.Equal(8, meter.Read(bytes, 8, 16));
        Assert.All(meter.Peaks, value => Assert.Equal(0, value));
        Assert.All(meter.Rms, value => Assert.Equal(0, value));
    }

    [Fact]
    public void MeterMeasuresFinalMixIncludingVolumeAndMute()
    {
        using var sound = AudioDocument.FromSamples(Enumerable.Repeat(.8f, 20).ToArray(), 1, 48000);
        var project = new MultitrackProject();
        project.Add(sound);
        var track = project.Tracks.Single();
        track.Gain = .125;
        var mix = new MultitrackWaveProvider(project);
        mix.ConfigurePlayback(0, 20, true);
        using var meter = new MeteringWaveProvider(mix);
        var bytes = new byte[80];
        meter.Read(bytes, 0, bytes.Length);
        Assert.All(meter.Peaks, value => Assert.Equal(-20, 20 * Math.Log10(value), 3));
        Assert.False(meter.Clipped);
        track.Muted = true;
        meter.Read(bytes, 0, bytes.Length);
        Assert.All(meter.Peaks, value => Assert.Equal(0, value));
    }
}
