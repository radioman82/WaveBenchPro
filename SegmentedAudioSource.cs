namespace WellenEditor;

/// <summary>Ein unveränderlicher Ausschnitt der Originalquelle samt linearem Gain-Verlauf.</summary>
internal readonly record struct AudioSourceSegment(long SourceStartFrame, long FrameCount, float GainAtStart = 1f, float GainAtEnd = 1f)
{
    public AudioSourceSegment Slice(long offset, long length)
    {
        if (offset < 0 || length < 0 || offset + length > FrameCount) throw new ArgumentOutOfRangeException(nameof(offset));
        var startProgress = FrameCount <= 1 ? 0f : offset / (float)(FrameCount - 1);
        var endProgress = FrameCount <= 1 ? 0f : (offset + length - 1) / (float)(FrameCount - 1);
        var gainStart = GainAtStart + (GainAtEnd - GainAtStart) * startProgress;
        var gainEnd = GainAtStart + (GainAtEnd - GainAtStart) * endProgress;
        return new AudioSourceSegment(SourceStartFrame + offset, length, gainStart, gainEnd);
    }
}

/// <summary>
/// Präsentiert eine Edit-Decision-List als eine lineare Audioquelle. Die Originalquelle
/// bleibt unangetastet; nur die angeforderten Frames werden gelesen und mit Segment-Gains versehen.
/// </summary>
internal sealed class SegmentedAudioSource : IAudioSource
{
    private readonly IAudioSource source;
    private readonly AudioSourceSegment[] segments;
    private readonly long[] timelineStarts;

    public SegmentedAudioSource(IAudioSource source, IEnumerable<AudioSourceSegment> segments)
    {
        this.source = source ?? throw new ArgumentNullException(nameof(source));
        this.segments = segments?.Where(segment => segment.FrameCount > 0).ToArray() ?? throw new ArgumentNullException(nameof(segments));
        timelineStarts = new long[this.segments.Length];
        for (var index = 0; index < this.segments.Length; index++)
        {
            timelineStarts[index] = FrameCount;
            FrameCount += this.segments[index].FrameCount;
        }
    }

    public int SampleRate => source.SampleRate;
    public int Channels => source.Channels;
    public int BitsPerSample => source.BitsPerSample;
    public long FrameCount { get; }

    public int ReadFrames(long startFrame, float[] buffer, int bufferOffsetFrames, int frameCount)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (startFrame < 0) throw new ArgumentOutOfRangeException(nameof(startFrame));
        if (bufferOffsetFrames < 0) throw new ArgumentOutOfRangeException(nameof(bufferOffsetFrames));
        if (frameCount < 0) throw new ArgumentOutOfRangeException(nameof(frameCount));
        if (startFrame >= FrameCount || frameCount == 0) return 0;

        var remaining = (int)Math.Min(frameCount, FrameCount - startFrame);
        if (buffer.Length < ((long)bufferOffsetFrames + remaining) * Channels)
            throw new ArgumentException("Puffer ist zu klein für den angeforderten Bereich.", nameof(buffer));

        var readTotal = 0;
        var segmentIndex = FindSegment(startFrame);
        var timelinePosition = startFrame;
        while (remaining > 0 && segmentIndex < segments.Length)
        {
            var segment = segments[segmentIndex];
            var segmentOffset = timelinePosition - timelineStarts[segmentIndex];
            var requested = (int)Math.Min(remaining, segment.FrameCount - segmentOffset);
            var read = source.ReadFrames(segment.SourceStartFrame + segmentOffset, buffer, bufferOffsetFrames + readTotal, requested);
            if (read <= 0) break;
            ApplyGain(buffer, bufferOffsetFrames + readTotal, segment, segmentOffset, read);
            readTotal += read;
            remaining -= read;
            timelinePosition += read;
            if (read < requested) break;
            segmentIndex++;
        }
        return readTotal;
    }

    private int FindSegment(long frame)
    {
        for (var index = 0; index < segments.Length; index++)
            if (frame < timelineStarts[index] + segments[index].FrameCount) return index;
        return segments.Length;
    }

    private void ApplyGain(float[] buffer, int bufferOffsetFrames, AudioSourceSegment segment, long segmentOffset, int frames)
    {
        if (segment.GainAtStart == 1f && segment.GainAtEnd == 1f) return;
        for (var frame = 0; frame < frames; frame++)
        {
            var progress = segment.FrameCount <= 1 ? 0f : (segmentOffset + frame) / (float)(segment.FrameCount - 1);
            var gain = segment.GainAtStart + (segment.GainAtEnd - segment.GainAtStart) * progress;
            var sampleOffset = (bufferOffsetFrames + frame) * Channels;
            for (var channel = 0; channel < Channels; channel++) buffer[sampleOffset + channel] *= gain;
        }
    }

    public void Dispose() => source.Dispose();
}
