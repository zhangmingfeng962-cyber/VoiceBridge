using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VoiceBridge.App.Sharing;

public sealed class RelayProfile
{
    public int Version { get; set; } = 1;
    public string Origin { get; set; } = "";
    public string ProtectedPublishKey { get; set; } = "";
    public static string ProfilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VoiceBridge", "relay-profile.json");
    public static bool IsConfigured => File.Exists(ProfilePath);
    public static RelayProfile Load()
    {
        if (!File.Exists(ProfilePath)) throw new InvalidOperationException("本机尚未配置中继发送凭据。请使用自己的中继配置；收听朋友的链接不需要发送配置。");
        var profile = JsonSerializer.Deserialize<RelayProfile>(File.ReadAllText(ProfilePath)) ?? throw new InvalidDataException("中继配置无效。");
        if (profile.Version != 1 || !Uri.TryCreate(profile.Origin, UriKind.Absolute, out var origin) || origin.Scheme != "https" || origin.AbsolutePath != "/" || origin.Query.Length != 0 || origin.Fragment.Length != 0 || origin.UserInfo.Length != 0)
            throw new InvalidDataException("中继配置必须包含 HTTPS 根地址。");
        profile.Origin = origin.GetLeftPart(UriPartial.Authority);
        return profile;
    }
    public string ReadPublishKey()
    {
        byte[] bytes = ProtectedData.Unprotect(Convert.FromBase64String(ProtectedPublishKey), Encoding.UTF8.GetBytes("VoiceBridge.Relay.v1|" + Origin), DataProtectionScope.CurrentUser);
        try
        {
            string key = Encoding.ASCII.GetString(bytes);
            if (key.Length != 64 || key.Any(c => !char.IsAsciiHexDigit(c))) throw new InvalidDataException("发送凭据无效。");
            return key;
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}
