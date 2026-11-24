namespace WellenEditor;

/// <summary>
/// Blockweise lesbare Audioquelle mit 64-Bit-Frame-Positionen (Dateien &gt; 2 GB adressierbar).
/// Implementierungen dürfen die Audiodaten <b>nicht</b> vollständig in den Speicher laden;
/// <see cref="ReadFrames"/> dekodiert nur den angeforderten Block.
/// </summary>
public interface IAudioSource : IDisposable
{
    /// <summary>Abtastrate in Hz.</summary>
    int SampleRate { get; }

    /// <summary>Anzahl der Kanäle (interleaved).</summary>
    int Channels { get; }

    /// <summary>Gesamtzahl der Frames (Sample-Punkte pro Kanal) als long.</summary>
    long FrameCount { get; }

    /// <summary>Bits pro Sample in der Quelldatei.</summary>
    int BitsPerSample { get; }

    /// <summary>
    /// Liest bis zu <paramref name="frameCount"/> Frames ab der Frame-Position
    /// <paramref name="startFrame"/> und dekodiert sie als interleaved float-Werte
    /// (-1..1) in <paramref name="buffer"/> ab <paramref name="bufferOffsetFrames"/> (Offset in Frames).
    /// Liefert die tatsächlich gelesene Frame-Zahl; 0 am Dateiende.
    /// </summary>
    int ReadFrames(long startFrame, float[] buffer, int bufferOffsetFrames, int frameCount);
}
