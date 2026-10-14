using System.IO;
using System.Security.Cryptography;

namespace VoiceBridge.App.Platform;

public static class BundledAssets
{
    private const string Prefix = "VoiceBridge.Assets.";
    private static readonly System.Reflection.Assembly Assembly = typeof(BundledAssets).Assembly;
    public static Stream Open(string name) => Assembly.GetManifestResourceStream(Prefix + name) ?? throw new FileNotFoundException("缺少内置组件：" + name);
    public static string[] Names => Assembly.GetManifestResourceNames().Where(x => x.StartsWith(Prefix, StringComparison.Ordinal)).Select(x => x[Prefix.Length..]).ToArray();
    public static async Task<string> VerifyDriverAsync()
    {
        await using var stream = Open("VBCABLE_Driver_Pack45.zip");
        string hash = Convert.ToHexString(await SHA256.HashDataAsync(stream));
        if (hash != CableInstaller.PackageHash) throw new InvalidDataException("内置 VB-CABLE 原包校验失败。");
        return hash;
    }
    public static string LicenseText()
    {
        var parts = Names.Where(x => x.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) || x.EndsWith(".md", StringComparison.OrdinalIgnoreCase)).Select(name =>
        {
            using var stream = Open(name); using var reader = new StreamReader(stream);
            return name + "\n" + new string('=', 60) + "\n" + reader.ReadToEnd();
        });
        return string.Join("\n\n", parts);
    }
}
