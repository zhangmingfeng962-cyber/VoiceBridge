using System.Buffers.Binary;

namespace VoiceBridge.Core;

// VBM1: 16-byte little-endian header, followed by interleaved IEEE float32 PCM.
// No lossy codec, AGC, EQ, or voice processing is applied to this transport.
public static class MusicPacket
{
    public const int SampleRate = 48000, Channels = 2, Frames = 480, HeaderBytes = 16;
    public const int Samples = Frames * Channels, PacketBytes = HeaderBytes + Samples * sizeof(float);
    public static byte[] Encode(ReadOnlySpan<float> samples, uint sequence)
    {
        if (samples.Length != Samples) throw new ArgumentException("A packet must contain 10 ms of stereo PCM.", nameof(samples));
        byte[] result = new byte[PacketBytes];
        "VBM1"u8.CopyTo(result);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), sequence);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(8), SampleRate);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(12), Channels);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(14), Frames);
        for (int i = 0; i < samples.Length; i++)
            BinaryPrimitives.WriteSingleLittleEndian(result.AsSpan(HeaderBytes + i * 4), float.IsFinite(samples[i]) ? samples[i] : 0);
        return result;
    }
}
