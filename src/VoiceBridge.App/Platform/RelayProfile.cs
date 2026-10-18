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
    public const string PortableFileName = "VoiceBridge-sender.vbrelay";
    public static string PortableProfilePath => Path.Combine(AppContext.BaseDirectory, PortableFileName);
    public static string? ResolvePath(string localPath, string portablePath) => File.Exists(localPath) ? localPath : File.Exists(portablePath) ? portablePath : null;
    public static string? ActivePath => ResolvePath(ProfilePath, PortableProfilePath);
    public static bool IsConfigured => ActivePath is not null;
    public static bool RestorePortableIfMissing(string localPath, string portablePath)
    {
        if (File.Exists(localPath) || !File.Exists(portablePath)) return false;
        Import(portablePath, localPath); return true;
    }
    public static RelayProfile Load()
    {
        if (ActivePath is not { } path) throw new InvalidOperationException("尚未导入中继配置。请点击“导入中继配置”，或把本机专用的 VoiceBridge-sender.vbrelay 放在 EXE 旁边。朋友仅收听时无需配置。");
        return LoadFile(path);
    }
    public static RelayProfile LoadFile(string path)
    {
        if (new FileInfo(path).Length > 8192) throw new InvalidDataException("中继配置文件过大。");
        var profile = JsonSerializer.Deserialize<RelayProfile>(File.ReadAllText(path)) ?? throw new InvalidDataException("中继配置无效。");
        if (profile.Version != 1 || !Uri.TryCreate(profile.Origin, UriKind.Absolute, out var origin) || origin.Scheme != "https" || origin.AbsolutePath != "/" || origin.Query.Length != 0 || origin.Fragment.Length != 0 || origin.UserInfo.Length != 0)
            throw new InvalidDataException("中继配置必须包含 HTTPS 根地址。");
        profile.Origin = origin.GetLeftPart(UriPartial.Authority);
        return profile;
    }
    public static RelayProfile Import(string sourcePath, string destinationPath)
    {
        var profile = LoadFile(sourcePath);
        _ = profile.ReadPublishKey(); // Reject credentials from a different user/origin before replacing anything.
        string serialized = JsonSerializer.Serialize(profile, new JsonSerializerOptions { WriteIndented = true });
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destinationPath))!);
        if (File.Exists(destinationPath)) File.Copy(destinationPath, destinationPath + ".previous", true);
        string temporary = destinationPath + ".tmp";
        File.WriteAllText(temporary, serialized, new UTF8Encoding(false));
        File.Move(temporary, destinationPath, true);
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
