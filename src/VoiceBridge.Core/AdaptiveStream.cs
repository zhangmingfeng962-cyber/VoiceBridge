namespace VoiceBridge.Core;

public sealed class AdaptiveStream(int channels)
{
    private readonly object gate = new();
    private readonly float[] ring = new float[48000 * channels * 2];
    private long read, write, lastArrival, lastAdjustment;
    private double fraction, jitter, targetMs = 100;
    private bool playing;
    private long underruns, lateTrims;
    public int Channels { get; } = channels;
    public bool AutoBuffer { get; set; } = true;
    public double BufferMs { get { lock (gate) return (write - read) * 1000.0 / 48000; } }
    public double TargetMs { get { lock (gate) return targetMs; } }
    public long Underruns => Interlocked.Read(ref underruns);
    public long LateTrims => Interlocked.Read(ref lateTrims);
    public void Push(ReadOnlySpan<float> samples, long nowMilliseconds)
    {
        if (samples.Length % Channels != 0) throw new ArgumentException("Incomplete frames.");
        lock (gate)
        {
            if (lastArrival > 0) jitter += (Math.Abs(nowMilliseconds - lastArrival - 20) - jitter) / 16;
            lastArrival = nowMilliseconds;
            if (AutoBuffer)
            {
                double desired = Math.Clamp(60 + jitter * 4, 60, 400);
                if (desired > targetMs) targetMs = desired;
                else if (nowMilliseconds - lastAdjustment > 5000) { targetMs = Math.Max(desired, targetMs - 10); lastAdjustment = nowMilliseconds; }
            }
            int frames = samples.Length / Channels;
            if (write - read + frames > 48000 * .8)
            { read = Math.Max(read, write - (long)(targetMs * 48)); fraction = 0; Interlocked.Increment(ref lateTrims); }
            for (int n = 0; n < frames; n++, write++)
                for (int c = 0; c < Channels; c++) ring[(write % 96000) * Channels + c] = float.IsFinite(samples[n * Channels + c]) ? samples[n * Channels + c] : 0;
        }
    }
    public void Mix(Span<float> stereo, float gain)
    {
        lock (gate)
        {
            if (!playing && write - read >= targetMs * 48) playing = true;
            if (!playing) return;
            double occupancy = write - read, rate = occupancy > (targetMs + 35) * 48 ? 1.002 : occupancy < (targetMs - 25) * 48 ? .998 : 1;
            for (int n = 0; n < stereo.Length / 2; n++)
            {
                if (read + 1 >= write)
                { playing = false; fraction = 0; Interlocked.Increment(ref underruns); if (AutoBuffer) targetMs = Math.Min(400, targetMs + 40); break; }
                for (int c = 0; c < 2; c++) { int channel = Math.Min(c, Channels - 1); float a = ring[(read % 96000) * Channels + channel], b = ring[((read + 1) % 96000) * Channels + channel]; stereo[n * 2 + c] += (a + (b - a) * (float)fraction) * gain; }
                fraction += rate; while (fraction >= 1) { read++; fraction--; }
            }
        }
    }
}
