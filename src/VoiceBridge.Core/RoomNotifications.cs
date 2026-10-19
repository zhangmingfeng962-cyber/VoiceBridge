namespace VoiceBridge.Core;
public static class RoomNotifications
{
    public static float[] Create(bool joined)
    {
        const int frames = 8640; var samples = new float[frames * 2];
        for (int n = 0; n < frames; n++)
        {
            double time = n / 48000.0, frequency = n < frames / 2 ? 660 : joined ? 880 : 440;
            int local = n % (frames / 2);
            double envelope = Math.Min(1, local / 480.0) * Math.Min(1, (frames / 2 - local) / 1200.0);
            float value = (float)(.055 * envelope * Math.Sin(2 * Math.PI * frequency * time));
            samples[n * 2] = samples[n * 2 + 1] = value;
        }
        return samples;
    }
}
