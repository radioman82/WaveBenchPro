using System.IO;
using NAudio.Vst3;
using NAudio.Wave;

namespace WellenEditor;

/// <summary>Per-export VST track rendering to a temporary float WAV. No cache is reused.</summary>
internal static class BlockVstTrackRenderer
{
    internal sealed record Clip(IAudioSource Source, long Start);
    internal delegate ISampleProvider VstFactory(TrackEffectSlot effect, ISampleProvider source);

    public static void Render(string path, IReadOnlyList<Clip> clips, IReadOnlyList<TrackEffectSlot> effects, long frames, int rate, CancellationToken cancellation, VstFactory? factory = null, Action<double>? progress = null)
    {
        var resources = new List<IDisposable>();
        try
        {
            ISampleProvider chain = new TrackSource(clips, frames, rate);
            foreach (var effect in effects)
            {
                cancellation.ThrowIfCancellationRequested();
                if (!effect.Enabled) continue;
                if (effect.Vst is null)
                {
                    chain = new NativeStage(chain, effect, rate);
                    continue;
                }
                if (factory is not null) chain = factory(effect, chain);
                else
                {
                    var module = Vst3Module.Load(effect.Vst.Path);
                    resources.Add(module);
                    var info = Vst3EffectCatalog.ResolveClassInfo(module, effect.Vst.ClassInfo.ClassId);
                    var plugin = module.CreatePlugin(info, rate, 4096);
                    resources.Add(plugin);
                    if (effect.VstState is { Length: > 0 }) plugin.LoadState(effect.VstState);
                    chain = new Vst3EffectSampleProvider(chain, plugin)
                    {
                        RenderTail = true, MaxTailDuration = TimeSpan.FromSeconds(2), CompensateLatency = true
                    };
                }
                if (chain.WaveFormat.Channels != 2 || chain.WaveFormat.SampleRate != rate)
                    throw new NotSupportedException($"VST3 '{effect.Vst.DisplayName}' unterstützt dieses Stereoformat nicht.");
                chain = new LengthLimitedStage(chain, frames);
            }
            using var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(rate, 2));
            var block = new float[4096 * 2];
            var bytes = new byte[block.Length * sizeof(float)];
            long remaining = checked(frames * 2);
            var lastPercent = -1;
            while (remaining > 0)
            {
                cancellation.ThrowIfCancellationRequested();
                var wanted = (int)Math.Min(block.Length, remaining);
                var read = chain.Read(block.AsSpan(0, wanted));
                if (read <= 0 || read > wanted || read % 2 != 0)
                    throw new EndOfStreamException("Die VST-Effektkette lieferte unvollständige Audiodaten; der Export wurde abgebrochen.");
                Buffer.BlockCopy(block, 0, bytes, 0, read * sizeof(float));
                writer.Write(bytes, 0, read * sizeof(float));
                remaining -= read;
                var percent = (int)((1d - remaining / (double)(frames * 2)) * 100);
                if (percent != lastPercent) { lastPercent = percent; progress?.Invoke(1d - remaining / (double)(frames * 2)); }
            }
            cancellation.ThrowIfCancellationRequested();
        }
        finally { for (var i = resources.Count - 1; i >= 0; i--) resources[i].Dispose(); }
    }

    private sealed class TrackSource(IReadOnlyList<Clip> clips, long frames, int rate) : ISampleProvider
    {
        private readonly ActiveClipIndex<Clip> index = new(clips, clip => clip.Start, clip => clip.Source.FrameCount);
        private readonly float[] scratch = new float[4096 * 2];
        private long position;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(rate, 2);
        public int Read(float[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public int Read(Span<float> buffer)
        {
            var requested = Math.Min(buffer.Length / 2, (int)Math.Min(int.MaxValue, frames - position));
            var done = 0;
            while (done < requested)
            {
                var count = Math.Min(4096, requested - done);
                var target = buffer.Slice(done * 2, count * 2);
                target.Clear();
                foreach (var clip in index.At(position, count))
                {
                    var from = Math.Max(position, clip.Start);
                    var to = Math.Min(position + count, checked(clip.Start + clip.Source.FrameCount));
                    if (to <= from) continue;
                    var wanted = checked((int)(to - from));
                    var read = clip.Source.ReadFrames(from - clip.Start, scratch, 0, wanted);
                    if (read != wanted) throw new EndOfStreamException("Die VST-Spurquelle endete vorzeitig.");
                    var outputOffset = checked((int)(from - position) * 2);
                    for (var i = 0; i < read; i++)
                    {
                        var input = i * clip.Source.Channels;
                        target[outputOffset + i * 2] += scratch[input];
                        target[outputOffset + i * 2 + 1] += scratch[input + (clip.Source.Channels == 1 ? 0 : 1)];
                    }
                }
                position += count; done += count;
            }
            return done * 2;
        }
    }

    private sealed class NativeStage(ISampleProvider source, TrackEffectSlot effect, int rate) : ISampleProvider
    {
        private readonly TrackEffectProcessor processor = new(new[] { effect }, rate);
        public WaveFormat WaveFormat => source.WaveFormat;
        public int Read(float[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public int Read(Span<float> buffer)
        {
            var read = source.Read(buffer);
            if (read % 2 != 0) throw new InvalidDataException("Die Effektkette lieferte einen unvollständigen Stereo-Frame.");
            processor.Process(buffer[..read]);
            return read;
        }
    }

    private sealed class LengthLimitedStage(ISampleProvider source, long frames) : ISampleProvider
    {
        private long remaining = checked(frames * 2);
        public WaveFormat WaveFormat => source.WaveFormat;
        public int Read(float[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public int Read(Span<float> buffer)
        {
            if (remaining == 0 || buffer.Length == 0) return 0;
            var wanted = (int)Math.Min(buffer.Length, remaining);
            var read = source.Read(buffer[..wanted]);
            if (read <= 0 || read > wanted || read % 2 != 0) throw new EndOfStreamException("Ein VST3-Effekt lieferte eine unvollständige Ausgabe.");
            remaining -= read;
            return read;
        }
    }
}
