using NAudio.Wave;
using System.IO;

namespace WellenEditor;

public sealed class AudioDocument
{
    private readonly Stack<float[]> undoHistory = new();

    private AudioDocument(string sourcePath, float[] samples, int channels, int sampleRate)
    {
        SourcePath = sourcePath;
        Samples = samples;
        Channels = channels;
        SampleRate = sampleRate;
    }

    public string SourcePath { get; }
    public float[] Samples { get; private set; }
    public int Channels { get; }
    public int SampleRate { get; }
    public int FrameCount => Samples.Length / Channels;
    public TimeSpan Duration => TimeSpan.FromSeconds(FrameCount / (double)SampleRate);
    public bool CanUndo => undoHistory.Count > 0;

    public static AudioDocument Load(string path)
    {
        using var reader = new AudioFileReader(path);
        var samples = new List<float>();
        var buffer = new float[reader.WaveFormat.SampleRate * reader.WaveFormat.Channels];
        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            samples.AddRange(buffer.AsSpan(0, read).ToArray());
        }
        if (samples.Count == 0) throw new InvalidDataException("Die Audiodatei enthält keine Samples.");
        return new AudioDocument(path, samples.ToArray(), reader.WaveFormat.Channels, reader.WaveFormat.SampleRate);
    }

    public ISampleProvider CreateProvider(int startFrame, int frames)
    {
        var boundedStart = Math.Clamp(startFrame, 0, FrameCount);
        var boundedFrames = Math.Clamp(frames, 0, FrameCount - boundedStart);
        var selection = new float[boundedFrames * Channels];
        Array.Copy(Samples, boundedStart * Channels, selection, 0, selection.Length);
        return new ArraySampleProvider(selection, WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels));
    }

    public IWaveProvider CreateWaveProvider(int startFrame, int frames)
    {
        var boundedStart = Math.Clamp(startFrame, 0, FrameCount);
        var boundedFrames = Math.Clamp(frames, 0, FrameCount - boundedStart);
        return new ArrayWaveProvider(Samples, boundedStart * Channels, boundedFrames * Channels, WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels));
    }

    public AudioSelectionData Extract(Selection selection)
    {
        var length = selection.Length * Channels;
        var extracted = new float[length];
        Array.Copy(Samples, selection.StartSample * Channels, extracted, 0, length);
        return new AudioSelectionData(extracted, Channels, SampleRate);
    }

    public void Delete(Selection selection)
    {
        SaveUndo();
        var start = selection.StartSample * Channels;
        var count = selection.Length * Channels;
        var edited = new float[Samples.Length - count];
        Array.Copy(Samples, 0, edited, 0, start);
        Array.Copy(Samples, start + count, edited, start, Samples.Length - start - count);
        Samples = edited;
    }

    public void Keep(Selection selection)
    {
        SaveUndo();
        var edited = new float[selection.Length * Channels];
        Array.Copy(Samples, selection.StartSample * Channels, edited, 0, edited.Length);
        Samples = edited;
    }

    public void Insert(int frame, AudioSelectionData audio)
    {
        if (audio.Channels != Channels || audio.SampleRate != SampleRate)
        {
            throw new InvalidOperationException("Die Zwischenablage hat ein anderes Audioformat.");
        }
        SaveUndo();
        var insertionPoint = Math.Clamp(frame, 0, FrameCount) * Channels;
        var edited = new float[Samples.Length + audio.Samples.Length];
        Array.Copy(Samples, 0, edited, 0, insertionPoint);
        Array.Copy(audio.Samples, 0, edited, insertionPoint, audio.Samples.Length);
        Array.Copy(Samples, insertionPoint, edited, insertionPoint + audio.Samples.Length, Samples.Length - insertionPoint);
        Samples = edited;
    }

    public void Undo()
    {
        if (undoHistory.TryPop(out var restored)) Samples = restored;
    }

    public void Normalize(Selection? selection)
    {
        var range = GetRange(selection);
        var peak = 0f;
        for (var index = range.Start; index < range.End; index++) peak = Math.Max(peak, Math.Abs(Samples[index]));
        if (peak < 0.00001f) return;
        SaveUndo();
        var gain = 0.98f / peak;
        for (var index = range.Start; index < range.End; index++) Samples[index] *= gain;
    }

    public void FadeIn(Selection? selection) => ApplyFade(selection, false);
    public void FadeOut(Selection? selection) => ApplyFade(selection, true);

    private void ApplyFade(Selection? selection, bool fadeOut)
    {
        var frames = selection ?? new Selection(0, FrameCount);
        if (frames.Length == 0) return;
        SaveUndo();
        for (var frame = frames.StartSample; frame < frames.EndSample; frame++)
        {
            var progress = (frame - frames.StartSample) / (float)Math.Max(1, frames.Length - 1);
            var gain = fadeOut ? 1f - progress : progress;
            for (var channel = 0; channel < Channels; channel++) Samples[frame * Channels + channel] *= gain;
        }
    }

    private (int Start, int End) GetRange(Selection? selection)
    {
        var frames = selection ?? new Selection(0, FrameCount);
        return (frames.StartSample * Channels, frames.EndSample * Channels);
    }

    public void SaveAsWave(string path)
    {
        using var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels));
        writer.WriteSamples(Samples, 0, Samples.Length);
    }

    private void SaveUndo()
    {
        undoHistory.Push(Samples);
        if (undoHistory.Count <= 20) return;
        var retained = undoHistory.Take(20).Reverse().ToArray();
        undoHistory.Clear();
        foreach (var state in retained) undoHistory.Push(state);
    }
}

public readonly record struct Selection(int StartSample, int EndSample)
{
    public int Length => EndSample - StartSample;
}

public readonly record struct AudioSelectionData(float[] Samples, int Channels, int SampleRate);

public static class AudioClipboard
{
    public static AudioSelectionData? Current { get; private set; }
    public static void Set(AudioSelectionData audio) => Current = audio;
}

internal sealed class ArraySampleProvider : ISampleProvider
{
    private readonly float[] data;
    private int position;

    public ArraySampleProvider(float[] data, WaveFormat waveFormat)
    {
        this.data = data;
        WaveFormat = waveFormat;
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        var available = Math.Min(count, data.Length - position);
        Array.Copy(data, position, buffer, offset, available);
        position += available;
        return available;
    }
}

internal sealed class ArrayWaveProvider : IWaveProvider
{
    private readonly float[] data;
    private readonly int endBytePosition;
    private int bytePosition;

    public ArrayWaveProvider(float[] data, int sampleOffset, int sampleCount, WaveFormat waveFormat)
    {
        this.data = data;
        bytePosition = sampleOffset * sizeof(float);
        endBytePosition = bytePosition + sampleCount * sizeof(float);
        WaveFormat = waveFormat;
    }

    public WaveFormat WaveFormat { get; }

    public int Read(byte[] buffer, int offset, int count)
    {
        var available = Math.Min(count, endBytePosition - bytePosition);
        Buffer.BlockCopy(data, bytePosition, buffer, offset, available);
        bytePosition += available;
        return available;
    }
}
