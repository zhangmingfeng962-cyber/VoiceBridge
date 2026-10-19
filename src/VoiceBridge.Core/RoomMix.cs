using System.Collections.Concurrent;
using System.Buffers;

namespace VoiceBridge.Core;
public sealed class RoomMix : IFloatSource, IDisposable
{
    private sealed class StreamState(bool music) : IDisposable
    { public readonly RoomDecoder Decoder = new(music); public readonly AdaptiveStream Buffer = new(music ? 2 : 1); public long LastSequence = -1; public void Dispose() => Decoder.Dispose(); }
    private readonly ConcurrentDictionary<string, StreamState> streams = new();
    private readonly ConcurrentDictionary<string, float> gains = new();
    private int disposed, closing;
    public void BeginClosing() => Interlocked.Exchange(ref closing, 1);
    private readonly LiveSampleQueue notifications = new(48000, 2, 400);
    public void Notify(bool joined) => notifications.Write(RoomNotifications.Create(joined));
    public float SpeakerGain { get; set; } = 1;
    public bool AutoBuffer { get; set; } = true;
    public float Peak { get; private set; }
    public void Receive(string member, byte kind, uint sequence, ReadOnlySpan<byte> opus, long now)
    {
        if (Volatile.Read(ref disposed) != 0) return;
        var stream = streams.GetOrAdd(member + ":" + kind, _ => new(kind == 1));
        lock (stream)
        {
            if (Volatile.Read(ref disposed) != 0) return;
            if (sequence <= stream.LastSequence) return;
            stream.LastSequence = sequence; stream.Buffer.AutoBuffer = AutoBuffer; stream.Buffer.Push(stream.Decoder.Decode(opus), now);
        }
    }
    public void SetGain(string member, byte kind, float percent) => gains[member + ":" + kind] = float.IsFinite(percent) ? Math.Clamp(percent / 100, 0, 2) : 1;
    public void Remove(string member)
    { foreach (var key in streams.Keys.Where(key => key.StartsWith(member + ":", StringComparison.Ordinal))) if (streams.TryRemove(key, out var stream)) { lock (stream) stream.Dispose(); } }
    public int Read(float[] buffer, int offset, int count)
    {
        var output = buffer.AsSpan(offset, count); output.Clear();
        if (Volatile.Read(ref closing) == 0) foreach (var pair in streams) pair.Value.Buffer.Mix(output, gains.TryGetValue(pair.Key, out var gain) ? gain : 1);
        var cue = ArrayPool<float>.Shared.Rent(count); try { notifications.Read(cue, 0, count); for (int i = 0; i < count; i++) output[i] += cue[i]; } finally { ArrayPool<float>.Shared.Return(cue); }
        float peak = 0; for (int i = 0; i < output.Length; i++) { float value = output[i] * SpeakerGain; peak = Math.Max(peak, Math.Abs(value)); output[i] = Math.Clamp(value, -1, 1); } Peak = peak; return count;
    }
    public object Statistics() => streams.Select(pair => new { id = pair.Key, targetMs = Math.Round(pair.Value.Buffer.TargetMs), bufferedMs = Math.Round(pair.Value.Buffer.BufferMs), underruns = pair.Value.Buffer.Underruns, trims = pair.Value.Buffer.LateTrims }).ToArray();
    public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) != 0) return; foreach (var stream in streams.Values) lock (stream) stream.Dispose(); streams.Clear(); }
}
