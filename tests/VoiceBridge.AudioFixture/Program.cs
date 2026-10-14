using NAudio.CoreAudioApi;
using NAudio.Wave;

// Integration fixture: sends a known tone ONLY to an explicitly supplied installed virtual cable.
// No physical microphone access and no default-device or volume changes.
if (args.Length != 3) return 2;
using var enumerator = new MMDeviceEnumerator();
using var device = enumerator.GetDevice(args[0]);
if (!device.DeviceFriendlyName.Equals("VB-Audio Virtual Cable", StringComparison.OrdinalIgnoreCase)) return 3;
double hz = double.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture);
int seconds = int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture);
using var output = new WasapiOut(device, AudioClientShareMode.Shared, true, 20);
output.Init(new Tone(hz).ToWaveProvider()); output.Play();
Console.WriteLine("READY"); Console.Out.Flush();
Thread.Sleep(TimeSpan.FromSeconds(seconds)); output.Stop();
return 0;

sealed class Tone(double hz) : ISampleProvider
{
    private long frame;
    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
    public int Read(float[] buffer, int offset, int count)
    {
        for (int i = 0; i < count; i += 2, frame++)
        {
            float sample = .06f * (float)Math.Sin(2 * Math.PI * hz * frame / 48000);
            buffer[offset + i] = sample; buffer[offset + i + 1] = sample;
        }
        return count;
    }
}
