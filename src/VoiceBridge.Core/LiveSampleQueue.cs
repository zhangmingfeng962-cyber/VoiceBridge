namespace VoiceBridge.Core;

public interface IFloatSource
{
    int Read(float[] buffer, int offset, int count);
}

// Single audio source; bounded in frames. Never blocks a render callback waiting for capture.
public sealed class LiveSampleQueue : IFloatSource
{
    private readonly object gate = new();
    private readonly float[] data;
    private readonly int channels;
    private int head, size;
    private long dropped, missing;
    public int SampleRate { get; }
    public int Channels => channels;
    public long DroppedSamples => Interlocked.Read(ref dropped);
    public long MissingSamples => Interlocked.Read(ref missing);
    public int BufferedSamples { get { lock (gate) return size; } }
    public double BufferedMilliseconds { get { lock (gate) return size * 1000.0 / channels / SampleRate; } }

    public LiveSampleQueue(int sampleRate, int channels, int capacityMs = 60)
    {
        if (sampleRate <= 0 || channels < 1 || capacityMs < 10) throw new ArgumentOutOfRangeException();
        SampleRate = sampleRate;
        this.channels = channels;
        data = new float[Math.Max(1, sampleRate * capacityMs / 1000) * channels];
    }

    public void Write(ReadOnlySpan<float> samples)
    {
        if (samples.Length % channels != 0) throw new ArgumentException("Only complete audio frames are allowed.");
        lock (gate)
        {
            if (samples.Length > data.Length)
            {
                Interlocked.Add(ref dropped, samples.Length - data.Length);
                samples = samples[^data.Length..];
            }
            int overflow = Math.Max(0, size + samples.Length - data.Length);
            head = (head + overflow) % data.Length;
            size -= overflow;
            Interlocked.Add(ref dropped, overflow);
            int tail = (head + size) % data.Length;
            int first = Math.Min(samples.Length, data.Length - tail);
            samples[..first].CopyTo(data.AsSpan(tail));
            samples[first..].CopyTo(data);
            size += samples.Length;
        }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        if (count % channels != 0) throw new ArgumentException("Only complete audio frames are allowed.");
        lock (gate)
        {
            int available = Math.Min(count, size);
            int first = Math.Min(available, data.Length - head);
            data.AsSpan(head, first).CopyTo(buffer.AsSpan(offset));
            data.AsSpan(0, available - first).CopyTo(buffer.AsSpan(offset + first));
            buffer.AsSpan(offset + available, count - available).Clear();
            head = (head + available) % data.Length;
            size -= available;
            Interlocked.Add(ref missing, count - available);
        }
        return count;
    }

    public void Clear() { lock (gate) { head = 0; size = 0; } }
}
