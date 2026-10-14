namespace VoiceBridge.Core;

// All sources must deliver stereo float32 at 48 kHz. Only the render thread calls Read.
public sealed class MixBus : IFloatSource
{
    public const int SampleRate = 48000;
    public const int Channels = 2;
    private readonly IFloatSource microphone, music;
    private float[] micBuffer = new float[4096], musicBuffer = new float[4096];
    private float micCurrent, musicCurrent, micLimiter = 1;
    private float micTarget = 1, musicTarget = .35f;
    private volatile bool musicEnabled;
    private volatile bool microphoneEnabled = true;
    private float micPeak, musicPeak, outputPeak, reduction;
    public float MicGain { get => Volatile.Read(ref micTarget); set => Volatile.Write(ref micTarget, SafeGain(value, 2)); }
    public float MusicGain { get => Volatile.Read(ref musicTarget); set => Volatile.Write(ref musicTarget, SafeGain(value, 1)); }
    public bool MusicEnabled { get => musicEnabled; set => musicEnabled = value; }
    public bool MicrophoneEnabled { get => microphoneEnabled; set => microphoneEnabled = value; }
    public float MicPeak => Volatile.Read(ref micPeak);
    public float MusicPeak => Volatile.Read(ref musicPeak);
    public float OutputPeak => Volatile.Read(ref outputPeak);
    public float LimiterReductionDb => Volatile.Read(ref reduction);
    public MixBus(IFloatSource microphone, IFloatSource music) { this.microphone = microphone; this.music = music; }
    private static float SafeGain(float value, float max) => float.IsFinite(value) ? Math.Clamp(value, 0, max) : 0;
    private static float Finite(float value) => float.IsFinite(value) ? value : 0;
    private static float Ramp(float current, float target) => current + Math.Clamp(target - current, -1f / 240, 1f / 240);

    public int Read(float[] buffer, int offset, int count)
    {
        if (count % 2 != 0) throw new ArgumentException("Stereo frames required.");
        if (count > micBuffer.Length) { Array.Resize(ref micBuffer, count); Array.Resize(ref musicBuffer, count); }
        // Consume even while muted: unmuting always shares the current music position.
        int micRead = microphone.Read(micBuffer, 0, count);
        int musicRead = music.Read(musicBuffer, 0, count);
        Array.Clear(micBuffer, micRead, count - micRead);
        Array.Clear(musicBuffer, musicRead, count - musicRead);
        float mp = 0, qp = 0, op = 0;
        float mt = microphoneEnabled ? MicGain : 0, qt = musicEnabled ? MusicGain : 0;
        const float release = .00026f; // about 80 ms to recover most gain
        for (int i = 0; i < count; i += 2)
        {
            micCurrent = Ramp(micCurrent, mt);
            musicCurrent = Ramp(musicCurrent, qt);
            float ml = Finite(micBuffer[i]), mr = Finite(micBuffer[i + 1]);
            float ql = Finite(musicBuffer[i]), qr = Finite(musicBuffer[i + 1]);
            mp = Math.Max(mp, Math.Max(Math.Abs(ml), Math.Abs(mr)));
            qp = Math.Max(qp, Math.Max(Math.Abs(ql), Math.Abs(qr)));
            // Fixed 6 dB headroom preserves the music waveform and its dynamics.
            // Limit only the voice peaks: speech must never turn the music down.
            float voiceLeft = Math.Clamp(ml, -1, 1) * (micCurrent * .5f), voiceRight = Math.Clamp(mr, -1, 1) * (micCurrent * .5f);
            float voicePeak = Math.Max(Math.Abs(voiceLeft), Math.Abs(voiceRight));
            float needed = voicePeak > .45f ? .45f / voicePeak : 1;
            micLimiter = needed < micLimiter ? needed : micLimiter + (1 - micLimiter) * release;
            float l = voiceLeft * micLimiter + Math.Clamp(ql, -1, 1) * musicCurrent * .5f;
            float r = voiceRight * micLimiter + Math.Clamp(qr, -1, 1) * musicCurrent * .5f;
            buffer[offset + i] = Math.Clamp(l, -.95f, .95f);
            buffer[offset + i + 1] = Math.Clamp(r, -.95f, .95f);
            op = Math.Max(op, Math.Max(Math.Abs(l), Math.Abs(r)));
        }
        Volatile.Write(ref micPeak, mp); Volatile.Write(ref musicPeak, qp); Volatile.Write(ref outputPeak, op);
        Volatile.Write(ref reduction, -20 * MathF.Log10(Math.Max(micLimiter, .00001f)));
        return count;
    }
}
