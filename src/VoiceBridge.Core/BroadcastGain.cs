namespace VoiceBridge.Core;

// Fixed user-selected gain, never driven by voice activity or signal loudness.
public sealed class BroadcastGain
{
    private float volume = 1;
    private bool muted;
    private float previous = 1;
    public float Volume { get => Volatile.Read(ref volume); set => Volatile.Write(ref volume, float.IsFinite(value) ? Math.Clamp(value, 0, 1) : 1); }
    public bool Muted { get => Volatile.Read(ref muted); set => Volatile.Write(ref muted, value); }
    public void Apply(Span<float> stereo)
    {
        float target = Muted ? 0 : Volume;
        if (previous == target)
        {
            if (target == 1) return; // Preserve source sample bits at unity gain.
            for (int i = 0; i < stereo.Length; i++) stereo[i] *= target;
        }
        else
        {
            int frames = stereo.Length / 2;
            for (int frame = 0; frame < frames; frame++)
            {
                float gain = previous + (target - previous) * (frame + 1) / frames;
                stereo[frame * 2] *= gain; stereo[frame * 2 + 1] *= gain;
            }
        }
        previous = target;
    }
}
