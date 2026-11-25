using System.IO;
using System.Text;

namespace WellenEditor;

/// <summary>
/// <see cref="IAudioSource"/> für RIFF/WAVE-Dateien (PCM 16/24/32 Bit, IEEE-Float 32/64 Bit,
/// inkl. WAVEFORMATEXTENSIBLE). Der data-Chunk bleibt auf Disk: <see cref="ReadFrames"/>
/// seeked in die Datei und dekodiert nur den angeforderten Block. Dadurch sind auch
/// WAV-Dateien &gt; 2 GB über long-Frame-Positionen ohne komplettes Laden nutzbar.
/// Nicht unterstützt: komprimierte Formate (z. B. ADPCM, MPEG) und RF64.
/// </summary>
public sealed class WavFileAudioSource : IAudioSource
{
    private const short FormatPcm = 1;
    private const short FormatIeeeFloat = 3;
    private const short FormatExtensible = unchecked((short)0xFFFE);

    /// <summary>Maximale Byte-Größe eines einzelnen Lese-/Dekodier-Schritts innerhalb von ReadFrames.</summary>
    private const int MaxChunkBytes = 1024 * 1024;

    private readonly FileStream stream;
    private readonly object readLock = new();
    private long dataOffset;   // Byte-Offset des data-Chunks in der Datei
    private int blockAlign;    // Bytes pro Frame (Channels * BitsPerSample / 8)
    private short formatTag;   // effektiver Format-Tag (bei Extensible aus der SubFormat-GUID)
    private byte[]? decodeBuffer;

    public int SampleRate { get; private set; }
    public int Channels { get; private set; }
    public int BitsPerSample { get; private set; }
    public bool IsIeeeFloat => formatTag == FormatIeeeFloat;
    public long FrameCount { get; private set; }

    /// <summary>
    /// Summe der bisher von Disk gelesenen Audiodaten-Bytes (nur data-Chunk, ohne Header).
    /// Ermöglicht den Nachweis, dass nicht die komplette Datei geladen wird.
    /// </summary>
    internal long BytesReadFromDisk { get; private set; }

    public WavFileAudioSource(string path)
    {
        // Der Hintergrund-Cache darf weder Löschen noch Umbenennen der Quelldatei blockieren.
        stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        try
        {
            ParseHeader();
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private void ParseHeader()
    {
        var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);

        if (reader.ReadUInt32() != 0x46464952) throw new InvalidDataException("Keine RIFF-Datei: 'RIFF'-Kennung fehlt.");
        var riffSize = reader.ReadUInt32();
        if (reader.ReadUInt32() != 0x45564157) throw new InvalidDataException("Keine WAVE-Datei: 'WAVE'-Kennung fehlt.");
        var riffEnd = Math.Min(checked((long)riffSize + 8), stream.Length);

        short fmtTag = 0;
        int channels = 0, sampleRate = 0, blockAlignLocal = 0, bits = 0;
        long dataLength = -1;

        while (stream.Position <= riffEnd - 8)
        {
            uint chunkId = reader.ReadUInt32();
            uint chunkSize = reader.ReadUInt32();
            long chunkDataStart = stream.Position;
            var paddedChunkEnd = checked(chunkDataStart + chunkSize + (chunkSize & 1));
            if (paddedChunkEnd > riffEnd)
                throw new InvalidDataException($"WAV-Chunk bei Byte {chunkDataStart - 8} reicht über das RIFF-Dateiende hinaus.");

            if (chunkId == 0x20746D66) // 'fmt '
            {
                if (chunkSize < 16) throw new InvalidDataException("WAV-fmt-Chunk ist kürzer als WAVEFORMAT.");
                fmtTag = reader.ReadInt16();
                channels = reader.ReadInt16();
                sampleRate = reader.ReadInt32();
                reader.ReadInt32(); // avgBytesPerSec
                blockAlignLocal = reader.ReadInt16();
                bits = reader.ReadInt16();
                if (fmtTag == FormatExtensible && chunkSize >= 40)
                {
                    var extraSize = reader.ReadUInt16();
                    var validBits = reader.ReadUInt16();
                    reader.ReadInt32(); // channelMask
                    var subFormat = reader.ReadBytes(16);
                    if (extraSize < 22 || extraSize > chunkSize - 18 || subFormat.Length != 16)
                        throw new InvalidDataException("WAVEFORMATEXTENSIBLE enthält keine vollständige SubFormat-GUID innerhalb des fmt-Chunks.");
                    var subFormatGuid = new Guid(subFormat);
                    var pcmGuid = new Guid("00000001-0000-0010-8000-00aa00389b71");
                    var floatGuid = new Guid("00000003-0000-0010-8000-00aa00389b71");
                    fmtTag = subFormatGuid == pcmGuid ? FormatPcm : subFormatGuid == floatGuid ? FormatIeeeFloat : (short)0;
                    if (fmtTag == FormatPcm && validBits != 0 && validBits > bits)
                        throw new InvalidDataException($"WAVEFORMATEXTENSIBLE meldet {validBits} gültige Bits in einem {bits}-Bit-PCM-Container.");
                    if (fmtTag == FormatIeeeFloat && validBits != 0 && validBits != bits)
                        throw new NotSupportedException($"WAVEFORMATEXTENSIBLE IEEE-Float mit {validBits} gültigen Bits in {bits}-Bit-Containern ist ungültig.");
                }
                else if (fmtTag == FormatExtensible)
                    throw new InvalidDataException("WAVEFORMATEXTENSIBLE-fmt-Chunk ist kürzer als 40 Byte.");
            }
            else if (chunkId == 0x61746164) // 'data'
            {
                dataOffset = chunkDataStart;
                // tamaño kann bei defekten/streaming-WAVs 0xFFFFFFFF sein -> auf Dateigröße clampen
                dataLength = Math.Min(chunkSize, riffEnd - chunkDataStart);
            }

            // zum nächsten Chunk (ungerade Chunk-Größen sind um 1 Pad-Byte erweitert)
            stream.Position = paddedChunkEnd;
        }

        if (fmtTag != FormatPcm && fmtTag != FormatIeeeFloat)
            throw new NotSupportedException($"WAV-Kompression (Format-Tag {fmtTag}) wird nicht unterstützt. Nur PCM und IEEE-Float.");
        if (!IsSupportedSampleFormat(fmtTag, bits))
            throw new NotSupportedException($"Sample-Format {fmtTag}/{bits} Bit wird nicht unterstützt (PCM 16/24/32, IEEE-Float 32/64).");
        if (channels < 1) throw new InvalidDataException("WAV hat keine Kanäle.");
        if (sampleRate < 1) throw new InvalidDataException("WAV hat keine gültige Abtastrate.");
        if (channels > ushort.MaxValue || sampleRate <= 0 || bits <= 0)
            throw new InvalidDataException("WAV enthält ungültige Formatparameter.");
        if (blockAlignLocal != checked(channels * bits / 8))
            throw new InvalidDataException($"Ungültiges blockAlign {blockAlignLocal} (erwartet {channels * bits / 8}).");
        if (dataLength < 0) throw new InvalidDataException("WAV enthält keinen data-Chunk.");

        formatTag = fmtTag;
        Channels = channels;
        SampleRate = sampleRate;
        BitsPerSample = bits;
        blockAlign = blockAlignLocal;
        FrameCount = dataLength / blockAlign;
    }

    private static bool IsSupportedSampleFormat(short tag, int bits) =>
        (tag == FormatPcm && bits is 16 or 24 or 32) || (tag == FormatIeeeFloat && bits is 32 or 64);

    /// <inheritdoc />
    public int ReadFrames(long startFrame, float[] buffer, int bufferOffsetFrames, int frameCount)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (startFrame < 0) throw new ArgumentOutOfRangeException(nameof(startFrame), "Frame-Position darf nicht negativ sein.");
        if (bufferOffsetFrames < 0) throw new ArgumentOutOfRangeException(nameof(bufferOffsetFrames));
        if (frameCount < 0) throw new ArgumentOutOfRangeException(nameof(frameCount));
        if (frameCount == 0) return 0;

        long available = FrameCount - startFrame;
        if (available <= 0) return 0; // an oder nach dem Dateiende

        int framesToRead = (int)Math.Min(frameCount, available);
        if (buffer.Length < ((long)bufferOffsetFrames + framesToRead) * Channels)
            throw new ArgumentException("Puffer ist zu klein für Offset + angeforderte Frames (interleaved: Frames * Channels).", nameof(buffer));

        lock (readLock)
        {
            int framesRead = 0;
            while (framesRead < framesToRead)
            {
                int chunkFrames = Math.Min(framesToRead - framesRead, MaxChunkBytes / blockAlign);
                int chunkBytes = chunkFrames * blockAlign;

                if (decodeBuffer == null || decodeBuffer.Length < chunkBytes)
                    decodeBuffer = new byte[chunkBytes];

                stream.Position = dataOffset + (startFrame + framesRead) * (long)blockAlign;
                int read = ReadExactly(stream, decodeBuffer, chunkBytes);
                if (read < chunkBytes)
                    throw new EndOfStreamException($"Unerwartetes Dateiende: {read} von {chunkBytes} Bytes gelesen (Frame {startFrame + framesRead}).");

                BytesReadFromDisk += chunkBytes;
                Decode(decodeBuffer, chunkBytes, buffer, (bufferOffsetFrames + framesRead) * Channels, chunkFrames);
                framesRead += chunkFrames;
            }
            return framesRead;
        }
    }

    private static int ReadExactly(Stream s, byte[] buffer, int count)
    {
        int total = 0;
        while (total < count)
        {
            int r = s.Read(buffer, total, count - total);
            if (r == 0) break;
            total += r;
        }
        return total;
    }

    /// <summary>Dekodiert einen gelesenen Block interleaved nach float (-1..1).</summary>
    private void Decode(byte[] src, int byteCount, float[] dst, int dstOffsetSamples, int frames)
    {
        int samples = frames * Channels;
        switch (formatTag, BitsPerSample)
        {
            case (FormatPcm, 16):
                for (int i = 0; i < samples; i++)
                    dst[dstOffsetSamples + i] = BitConverter.ToInt16(src, i * 2) / 32768f;
                break;

            case (FormatPcm, 24):
                for (int i = 0; i < samples; i++)
                {
                    int v = src[i * 3] | (src[i * 3 + 1] << 8) | (src[i * 3 + 2] << 16);
                    if ((v & 0x800000) != 0) v |= unchecked((int)0xFF000000); // Vorzeichenerweiterung
                    dst[dstOffsetSamples + i] = v / 8388608f;
                }
                break;

            case (FormatPcm, 32):
                for (int i = 0; i < samples; i++)
                    dst[dstOffsetSamples + i] = BitConverter.ToInt32(src, i * 4) / 2147483648f;
                break;

            case (FormatIeeeFloat, 32):
                // byteCount == samples * 4 -> direkt in den float-Puffer kopieren
                Buffer.BlockCopy(src, 0, dst, dstOffsetSamples * sizeof(float), byteCount);
                break;

            case (FormatIeeeFloat, 64):
                for (int i = 0; i < samples; i++)
                    dst[dstOffsetSamples + i] = (float)BitConverter.ToDouble(src, i * 8);
                break;

            default:
                throw new NotSupportedException($"Sample-Format {formatTag}/{BitsPerSample} Bit wird nicht unterstützt.");
        }
    }

    public void Dispose() => stream.Dispose();
}
