using System.Numerics;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WellenEditor;

/// <summary>Vorgeberechnete FFT-Übersicht für die Spektralansicht.</summary>
internal sealed class SpectrogramCache
{
    private SpectrogramCache(BitmapSource image) => Image = image;

    public BitmapSource Image { get; }

    public static SpectrogramCache Build(IAudioSource source, CancellationToken cancellationToken)
    {
        const int fftSize = 1024;
        const int bins = fftSize / 2;
        var columns = (int)Math.Clamp(source.FrameCount / 2048, 192, 960);
        var pixels = new byte[checked(columns * bins * 4)];
        var interleaved = new float[fftSize * source.Channels];
        var spectrum = new Complex[fftSize];

        for (var column = 0; column < columns; column++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var center = (long)column * source.FrameCount / columns;
            var start = Math.Clamp(center - fftSize / 2, 0, Math.Max(0, source.FrameCount - fftSize));
            var read = source.ReadFrames(start, interleaved, 0, fftSize);
            for (var frame = 0; frame < fftSize; frame++)
            {
                var mono = frame < read
                    ? AverageChannels(interleaved, frame * source.Channels, source.Channels)
                    : 0f;
                var window = .5 - .5 * Math.Cos(2 * Math.PI * frame / (fftSize - 1));
                spectrum[frame] = new Complex(mono * window, 0);
            }
            Fft(spectrum);

            for (var bin = 1; bin < bins; bin++)
            {
                var db = 20 * Math.Log10(Math.Max(1e-8, spectrum[bin].Magnitude / fftSize));
                var normalized = Math.Clamp((db + 92) / 82, 0, 1);
                var row = bins - 1 - bin;
                var pixel = (row * columns + column) * 4;
                var color = HeatColor(normalized);
                pixels[pixel] = color.B;
                pixels[pixel + 1] = color.G;
                pixels[pixel + 2] = color.R;
                pixels[pixel + 3] = 255;
            }
        }

        var image = BitmapSource.Create(columns, bins, 96, 96, PixelFormats.Bgra32, null, pixels, columns * 4);
        image.Freeze();
        return new SpectrogramCache(image);
    }

    private static float AverageChannels(float[] values, int offset, int channels)
    {
        var sum = 0f;
        for (var channel = 0; channel < channels; channel++) sum += values[offset + channel];
        return sum / channels;
    }

    private static Color HeatColor(double value)
    {
        var r = (byte)Math.Clamp(255 * Math.Min(1, value * 2.2), 0, 255);
        var g = (byte)Math.Clamp(255 * Math.Min(1, Math.Max(0, (value - .25) * 2.1)), 0, 255);
        var b = (byte)Math.Clamp(255 * Math.Min(1, Math.Max(0, (value - .58) * 2.4)), 0, 255);
        return Color.FromRgb(r, g, b);
    }

    private static void Fft(Complex[] data)
    {
        var count = data.Length;
        for (int index = 1, reversed = 0; index < count; index++)
        {
            var bit = count >> 1;
            for (; (reversed & bit) != 0; bit >>= 1) reversed ^= bit;
            reversed ^= bit;
            if (index < reversed) (data[index], data[reversed]) = (data[reversed], data[index]);
        }
        for (var length = 2; length <= count; length <<= 1)
        {
            var root = Complex.FromPolarCoordinates(1, -2 * Math.PI / length);
            for (var start = 0; start < count; start += length)
            {
                var factor = Complex.One;
                for (var offset = 0; offset < length / 2; offset++)
                {
                    var even = data[start + offset];
                    var odd = data[start + offset + length / 2] * factor;
                    data[start + offset] = even + odd;
                    data[start + offset + length / 2] = even - odd;
                    factor *= root;
                }
            }
        }
    }
}
