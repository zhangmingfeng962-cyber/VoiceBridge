using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace VoiceBridge.Core;

public static class RelayChunk
{
    public const int Packets = 40, PlainBytes = MusicPacket.PacketBytes * Packets, HeaderBytes = 20, TagBytes = 16;
    public const int EncryptedBytes = PlainBytes + HeaderBytes + TagBytes;
    public static byte[] Encrypt(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> key, string roomId, uint sequence)
    {
        if (plaintext.Length != PlainBytes || key.Length != 32) throw new ArgumentException("Invalid music chunk/key length.");
        byte[] result = new byte[EncryptedBytes];
        "VBE1"u8.CopyTo(result); BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), sequence);
        RandomNumberGenerator.Fill(result.AsSpan(8, 12));
        using var cipher = new AesGcm(key, TagBytes);
        cipher.Encrypt(result.AsSpan(8, 12), plaintext, result.AsSpan(HeaderBytes, PlainBytes), result.AsSpan(HeaderBytes + PlainBytes, TagBytes), Encoding.ASCII.GetBytes(roomId + "|" + sequence.ToString(CultureInfo.InvariantCulture)));
        return result;
    }
    public static byte[] Decrypt(ReadOnlySpan<byte> encrypted, ReadOnlySpan<byte> key, string roomId)
    {
        if (encrypted.Length != EncryptedBytes || !encrypted[..4].SequenceEqual("VBE1"u8)) throw new ArgumentException("Invalid encrypted chunk.");
        uint sequence = BinaryPrimitives.ReadUInt32LittleEndian(encrypted[4..]); byte[] result = new byte[PlainBytes];
        using var cipher = new AesGcm(key, TagBytes);
        cipher.Decrypt(encrypted.Slice(8, 12), encrypted.Slice(HeaderBytes, PlainBytes), encrypted.Slice(HeaderBytes + PlainBytes, TagBytes), result, Encoding.ASCII.GetBytes(roomId + "|" + sequence.ToString(CultureInfo.InvariantCulture)));
        return result;
    }
}
