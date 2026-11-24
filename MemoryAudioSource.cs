namespace WellenEditor;

internal sealed class MemoryAudioSource : IAudioSource
{
    private readonly float[] samples;

    public MemoryAudioSource(float[] samples, int channels, int sampleRate)
    {
        this.samples = samples;
        Channels = channels;
        SampleRate = sampleRate;
    }

    public int SampleRate { get; }
    public int Channels { get; }
    public long FrameCount => samples.Length / Channels;
    public int BitsPerSample => 32;

    public int ReadFrames(long startFrame, float[] destination, int destinationFrameOffset, int frames)
    {
        var available = (int)Math.Clamp(FrameCount - startFrame, 0, frames);
        if (available > 0)
            Array.Copy(samples, startFrame * Channels, destination, destinationFrameOffset * Channels, available * Channels);
        return available;
    }

    public void Dispose() { }
}
