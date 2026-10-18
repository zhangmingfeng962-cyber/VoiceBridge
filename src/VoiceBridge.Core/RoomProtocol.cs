using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VoiceBridge.Core;
public sealed record RoomInvite(string Origin, string RoomId, string Token, byte[] Key)
{
    public static RoomInvite Parse(string value)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length > 0 || uri.Query.Length > 0)
            throw new ArgumentException("请粘贴完整的 HTTPS 房间邀请。");
        string[] path = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries), parts = uri.Fragment.TrimStart('#').Split('.');
        if (path.Length != 2 || path[0] != "room" || !RoomCipher.IsHex(path[1], 32) || parts.Length < 2 || !RoomCipher.IsHex(parts[0], 64) || !RoomCipher.IsHex(parts[1], 64)) throw new ArgumentException("邀请缺少房间编号或密钥，请重新复制完整链接。");
        return new(uri.GetLeftPart(UriPartial.Authority), path[1], parts[0], Convert.FromHexString(parts[1]));
    }
    public string Link => $"{Origin}/room/{RoomId}#{Token}.{Convert.ToHexString(Key).ToLowerInvariant()}";
    public string ControllerLink(string memberId, string secret) => Link + $".desktop.{memberId}.{secret}";
}
public sealed class RoomCipher(byte[] rootKey, string roomId)
{
    public static bool IsHex(string value, int length) => value.Length == length && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    public byte[] Derive(string purpose) => HKDF.DeriveKey(HashAlgorithmName.SHA256, rootKey, 32, Encoding.UTF8.GetBytes(roomId), Encoding.UTF8.GetBytes(purpose));
    public byte[] SealAudio(byte kind, string memberId, uint sequence, ReadOnlySpan<byte> opus)
    {
        if (kind > 1 || opus.Length is < 1 or > 1275) throw new ArgumentException("Invalid audio frame.");
        byte[] packet = new byte[21 + opus.Length + 16]; "VBA2"u8.CopyTo(packet); packet[4] = kind; BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(5), sequence); RandomNumberGenerator.Fill(packet.AsSpan(9, 12));
        using var aes = new AesGcm(Derive($"VoiceBridge audio v2|{kind}|{memberId}"), 16);
        aes.Encrypt(packet.AsSpan(9, 12), opus, packet.AsSpan(21, opus.Length), packet.AsSpan(21 + opus.Length, 16), Encoding.UTF8.GetBytes($"{roomId}|{kind}|{memberId}|{sequence}")); return packet;
    }
    public (byte Kind, uint Sequence, byte[] Opus) OpenAudio(string memberId, ReadOnlySpan<byte> packet)
    {
        if (packet.Length is < 38 or > 1312 || !packet[..4].SequenceEqual("VBA2"u8) || packet[4] > 1) throw new InvalidDataException("Invalid audio frame.");
        byte kind = packet[4]; uint sequence = BinaryPrimitives.ReadUInt32LittleEndian(packet[5..]); byte[] plain = new byte[packet.Length - 37];
        using var aes = new AesGcm(Derive($"VoiceBridge audio v2|{kind}|{memberId}"), 16);
        aes.Decrypt(packet.Slice(9, 12), packet.Slice(21, plain.Length), packet[^16..], plain, Encoding.UTF8.GetBytes($"{roomId}|{kind}|{memberId}|{sequence}")); return (kind, sequence, plain);
    }
    public object SealControl(string memberId, string connectionId, long sequence, object data, bool state)
    {
        string direction = state ? "state" : "control"; byte[] plain = JsonSerializer.SerializeToUtf8Bytes(data, JsonOptions), nonce = RandomNumberGenerator.GetBytes(12), encrypted = new byte[plain.Length + 16];
        using var aes = new AesGcm(Derive($"VoiceBridge {direction} v2|{memberId}|{connectionId}"), 16);
        aes.Encrypt(nonce, plain, encrypted.AsSpan(0, plain.Length), encrypted.AsSpan(plain.Length), Encoding.UTF8.GetBytes($"{roomId}|{direction}|{memberId}|{connectionId}|{sequence}"));
        return new { type = direction, connectionId, sequence, iv = Convert.ToBase64String(nonce), data = Convert.ToBase64String(encrypted) };
    }
    public JsonDocument OpenControl(string memberId, string connectionId, JsonElement message)
    {
        long sequence = message.GetProperty("sequence").GetInt64(); byte[] nonce = Convert.FromBase64String(message.GetProperty("iv").GetString()!), encrypted = Convert.FromBase64String(message.GetProperty("data").GetString()!);
        if (nonce.Length != 12 || encrypted.Length is < 16 or > 48000) throw new InvalidDataException("Invalid control packet."); byte[] plain = new byte[encrypted.Length - 16];
        using var aes = new AesGcm(Derive($"VoiceBridge control v2|{memberId}|{connectionId}"), 16);
        aes.Decrypt(nonce, encrypted.AsSpan(0, plain.Length), encrypted.AsSpan(plain.Length), plain, Encoding.UTF8.GetBytes($"{roomId}|control|{memberId}|{connectionId}|{sequence}")); return JsonDocument.Parse(plain);
    }
    public static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
}
