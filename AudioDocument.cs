using NAudio.Lame;
using NAudio.Wave;
using System.IO;
using System.Runtime.InteropServices;

namespace WellenEditor;

public sealed class AudioDocument
{
    private const int MaxUndoSteps = 20;

    private readonly Stack<float[]> undoHistory = new();
    private readonly Stack<float[]> redoHistory = new();

    private AudioDocument(string sourcePath, float[] samples, int channels, int sampleRate)
    {
        SourcePath = sourcePath;
        Samples = samples;
        Channels = channels;
        SampleRate = sampleRate;
        DisplayName = string.IsNullOrWhiteSpace(sourcePath) ? string.Empty : Path.GetFileName(sourcePath);
        IsModified = false;
    }

    public string SourcePath { get; private set; }
    public float[] Samples { get; private set; }
    public string DisplayName { get; set; }
    public bool IsModified { get; private set; }
    public int Channels { get; }
    public int SampleRate { get; }
    public int FrameCount => Samples.Length / Channels;
    public TimeSpan Duration => TimeSpan.FromSeconds(FrameCount / (double)SampleRate);
    public bool CanUndo => undoHistory.Count > 0;
    public bool CanRedo => redoHistory.Count > 0;

    public static AudioDocument FromSamples(float[] samples, int channels, int sampleRate, string? sourcePath = null)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Length == 0) throw new InvalidDataException("Die Audiodatei enthält keine Samples.");
        if (samples.Length % channels != 0) throw new ArgumentException("Die Sample-Anzahl ist kein Vielfaches der Kanalanzahl.", nameof(samples));
        return new AudioDocument(sourcePath ?? string.Empty, samples, channels, sampleRate);
    }

    public static AudioDocument Load(string path)
    {
        var fullPath = Path.GetFullPath(path);
        using var reader = new AudioFileReader(fullPath);
        var samples = new List<float>();
        var buffer = new float[reader.WaveFormat.SampleRate * reader.WaveFormat.Channels];
        int read;
        while ((read = reader.Read(buffer.AsSpan())) > 0)
        {
            samples.AddRange(buffer.AsSpan(0, read).ToArray());
        }
        if (samples.Count == 0) throw new InvalidDataException("Die Audiodatei enthält keine Samples.");
        return new AudioDocument(fullPath, samples.ToArray(), reader.WaveFormat.Channels, reader.WaveFormat.SampleRate);
    }

    public ISampleProvider CreateSampleProvider(int startFrame, int frames)
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

    /// <summary>Ersetzt einen Sample-Bereich durch vorab gerendertes Audio (z. B. VST3-Effekt).</summary>
    public void ReplaceFrames(int startFrame, float[] processed, int frames)
    {
        if (frames <= 0) return;
        if (startFrame < 0 || startFrame + frames > FrameCount) throw new ArgumentOutOfRangeException(nameof(startFrame));
        if (processed.Length < frames * Channels) throw new ArgumentException("Zu wenige Samples für den Ersetzungsbereich.", nameof(processed));
        SaveUndo();
        Array.Copy(processed, 0, Samples, startFrame * Channels, frames * Channels);
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
        // Fremde Formate werden kontrolliert angepasst statt abgelehnt.
        var compatible = AudioFormatConverter.Convert(audio, Channels, SampleRate);
        SaveUndo();
        var insertionPoint = Math.Clamp(frame, 0, FrameCount) * Channels;
        var edited = new float[Samples.Length + compatible.Samples.Length];
        Array.Copy(Samples, 0, edited, 0, insertionPoint);
        Array.Copy(compatible.Samples, 0, edited, insertionPoint, compatible.Samples.Length);
        Array.Copy(Samples, insertionPoint, edited, insertionPoint + compatible.Samples.Length, Samples.Length - insertionPoint);
        Samples = edited;
    }

    public void Undo()
    {
        if (!undoHistory.TryPop(out var restored)) return;
        redoHistory.Push(Samples);
        Samples = restored;
    }

    public void Redo()
    {
        if (!redoHistory.TryPop(out var restored)) return;
        undoHistory.Push(Samples);
        Samples = restored;
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

    /// <summary>Wendet einen nativen Effekt auf die Auswahl oder den gesamten Clip an.</summary>
    public void ApplyNativeEffect(Selection? selection, NativeEffectKind kind, EffectSettings settings)
    {
        var (start, end) = GetRange(selection);
        if (start >= end) return;
        var block = new float[end - start];
        Array.Copy(Samples, start, block, 0, block.Length);
        var processed = NativeEffects.Process(settings, block, Channels, SampleRate, kind);
        SaveUndo();
        Array.Copy(processed, 0, Samples, start, processed.Length);
    }

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

    public void SaveAsWave(string path) => SaveAsWave(path, 32);

    public void SaveAsWave(string path, int bitsPerSample)
    {
        var tempPath = path + ".tmp";
        try
        {
            using (var writer = new WaveFileWriter(tempPath, CreateWaveFormat(bitsPerSample)))
            {
                WriteSamples(writer, Samples, 0, Samples.Length, bitsPerSample);
            }
            CommitTempFile(tempPath, path);
        }
        catch
        {
            DeleteTempFile(tempPath);
            throw;
        }
    }

    /// <summary>Exportiert exakt den angegebenen Frame-Bereich, ohne das Dokument zu verändern.</summary>
    public void SaveRangeAsWave(string path, int startFrame, int frameCount, int bitsPerSample)
    {
        ValidateRange(startFrame, frameCount);
        var offset = startFrame * Channels;
        var count = frameCount * Channels;
        var tempPath = path + ".tmp";
        try
        {
            using (var writer = new WaveFileWriter(tempPath, CreateWaveFormat(bitsPerSample)))
            {
                WriteSamples(writer, Samples, offset, count, bitsPerSample);
            }
            CommitTempFile(tempPath, path);
        }
        catch
        {
            DeleteTempFile(tempPath);
            throw;
        }
    }

    /// <summary>Sicherer MP3-Export über eine temporäre Datei (CBR, 32–320 kBit/s).</summary>
    public void SaveAsMp3(string path, int bitRate = 192)
    {
        ValidateBitRate(bitRate);
        var tempPath = path + ".tmp";
        try
        {
            using (var writer = new LameMP3FileWriter(tempPath, new WaveFormat(SampleRate, 16, Channels), bitRate))
            {
                WritePcm16(writer, Samples, 0, Samples.Length);
            }
            CommitTempFile(tempPath, path);
        }
        catch
        {
            DeleteTempFile(tempPath);
            throw;
        }
    }

    /// <summary>Exportiert nur den markierten Bereich als MP3.</summary>
    public void SaveRangeAsMp3(string path, int startFrame, int frameCount, int bitRate = 192)
    {
        ValidateRange(startFrame, frameCount);
        ValidateBitRate(bitRate);
        var offset = startFrame * Channels;
        var count = frameCount * Channels;
        var tempPath = path + ".tmp";
        try
        {
            using (var writer = new LameMP3FileWriter(tempPath, new WaveFormat(SampleRate, 16, Channels), bitRate))
            {
                WritePcm16(writer, Samples, offset, count);
            }
            CommitTempFile(tempPath, path);
        }
        catch
        {
            DeleteTempFile(tempPath);
            throw;
        }
    }

    private WaveFormat CreateWaveFormat(int bitsPerSample) => bitsPerSample switch
    {
        32 => WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels),
        24 => new WaveFormat(SampleRate, 24, Channels),
        16 => new WaveFormat(SampleRate, 16, Channels),
        _ => throw new ArgumentOutOfRangeException(nameof(bitsPerSample), bitsPerSample, "Unterstützt werden 16, 24 und 32 Bit.")
    };

    private void ValidateRange(int startFrame, int frameCount)
    {
        if (startFrame < 0) throw new ArgumentOutOfRangeException(nameof(startFrame));
        if (frameCount < 0 || startFrame + frameCount > FrameCount) throw new ArgumentOutOfRangeException(nameof(frameCount));
    }

    private static void ValidateBitRate(int bitRate)
    {
        if (bitRate < 32 || bitRate > 320) throw new ArgumentOutOfRangeException(nameof(bitRate), bitRate, "Erlaubt sind 32 bis 320 kBit/s.");
    }

    private static void CommitTempFile(string tempPath, string path)
    {
        if (File.Exists(path)) File.Replace(tempPath, path, null);
        else File.Move(tempPath, path);
    }

    private static void DeleteTempFile(string tempPath)
    {
        if (File.Exists(tempPath)) File.Delete(tempPath);
    }

    private static void WriteSamples(WaveFileWriter writer, float[] samples, int offset, int count, int bitsPerSample)
    {
        switch (bitsPerSample)
        {
            case 32: writer.WriteSamples(samples, offset, count); break;
            case 24: WritePcm24(writer, samples, offset, count); break;
            case 16: WritePcm16(writer, samples, offset, count); break;
        }
    }

    private static void WritePcm16(Stream writer, float[] samples) => WritePcm16(writer, samples, 0, samples.Length);

    private static void WritePcm16(Stream writer, float[] samples, int offset, int count)
    {
        var data = new byte[count * 2];
        for (var index = 0; index < count; index++)
        {
            var value = (short)Math.Clamp(MathF.Round(samples[offset + index] * 32767f), -32768f, 32767f);
            data[index * 2] = (byte)value;
            data[index * 2 + 1] = (byte)(value >> 8);
        }
        writer.Write(data);
    }

    private static void WritePcm24(Stream writer, float[] samples) => WritePcm24(writer, samples, 0, samples.Length);

    private static void WritePcm24(Stream writer, float[] samples, int offset, int count)
    {
        var data = new byte[count * 3];
        for (var index = 0; index < count; index++)
        {
            var value = (int)Math.Clamp(MathF.Round(samples[offset + index] * 8388607f), -8388608f, 8388607f);
            data[index * 3] = (byte)value;
            data[index * 3 + 1] = (byte)(value >> 8);
            data[index * 3 + 2] = (byte)(value >> 16);
        }
        writer.Write(data);
    }

    /// <summary>Markiert das Dokument als geändert (z. B. neue, ungespeicherte Aufnahme oder Ausschnitt).</summary>
    public void MarkModified() => IsModified = true;

    /// <summary>Setzt nach „Speichern unter“ den neuen Quelldateipfad und markiert das Dokument als gespeichert.</summary>
    public void UpdateSourcePath(string path)
    {
        SourcePath = path;
        DisplayName = Path.GetFileName(path);
        IsModified = false;
    }

    /// <summary>Markiert das Dokument nach einem erfolgreichen Speichervorgang als sauber.</summary>
    public void MarkSaved() => IsModified = false;

    private void SaveUndo()
    {
        IsModified = true;
        undoHistory.Push((float[])Samples.Clone());
        redoHistory.Clear();
        if (undoHistory.Count <= MaxUndoSteps) return;
        var retained = undoHistory.Take(MaxUndoSteps).Reverse().ToArray();
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

    public int Read(Span<float> buffer)
    {
        var available = Math.Min(buffer.Length, data.Length - position);
        data.AsSpan(position, available).CopyTo(buffer);
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

    public int Read(Span<byte> buffer)
    {
        var available = Math.Min(buffer.Length, endBytePosition - bytePosition);
        MemoryMarshal.AsBytes(data.AsSpan()).Slice(bytePosition, available).CopyTo(buffer);
        bytePosition += available;
        return available;
    }
}
