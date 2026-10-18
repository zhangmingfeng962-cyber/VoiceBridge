using Concentus;
using Concentus.Enums;

namespace VoiceBridge.Core;
public sealed class RoomEncoder : IDisposable
{
    private readonly IOpusEncoder encoder;
    private readonly byte[] buffer = new byte[1275];
    public int Channels { get; }
    public RoomEncoder(bool music)
    {
        Channels = music ? 2 : 1;
        encoder = OpusCodecFactory.CreateEncoder(48000, Channels, music ? OpusApplication.OPUS_APPLICATION_AUDIO : OpusApplication.OPUS_APPLICATION_VOIP);
        encoder.Bitrate = music ? 256000 : 48000; encoder.Complexity = 5; encoder.UseVBR = true;
    }
    public byte[] Encode(ReadOnlySpan<float> samples)
    {
        if (samples.Length != 960 * Channels) throw new ArgumentException("Audio must contain exactly 20 ms.");
        int length = encoder.Encode(samples, 960, buffer, buffer.Length); return buffer[..length];
    }
    public void Dispose() => encoder.Dispose();
}
public sealed class RoomDecoder : IDisposable
{
    private readonly IOpusDecoder decoder;
    private readonly float[] buffer;
    public RoomDecoder(bool music) { int channels = music ? 2 : 1; decoder = OpusCodecFactory.CreateDecoder(48000, channels); buffer = new float[960 * channels]; }
    public float[] Decode(ReadOnlySpan<byte> encoded)
    { int frames = decoder.Decode(encoded, buffer, 960, false); if (frames != 960) throw new InvalidDataException("Unexpected audio frame duration."); return buffer.ToArray(); }
    public void Dispose() => decoder.Dispose();
}
