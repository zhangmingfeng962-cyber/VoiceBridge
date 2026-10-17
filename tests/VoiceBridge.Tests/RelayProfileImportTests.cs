using System.Security.Cryptography;
using System.IO;
using System.Text;
using System.Text.Json;
using VoiceBridge.App.Sharing;
using Xunit;

namespace VoiceBridge.Tests;
public class RelayProfileImportTests
{
    private static RelayProfile Profile(string origin = "https://relay.example") => new()
    {
        Origin = origin,
        ProtectedPublishKey = Convert.ToBase64String(ProtectedData.Protect(Encoding.ASCII.GetBytes(new string('a', 64)), Encoding.UTF8.GetBytes("VoiceBridge.Relay.v1|" + origin), DataProtectionScope.CurrentUser))
    };
    [Fact]
    public void MissingDesktopUserConfigurationCanUsePortableFileAndImportWithoutChangingSecret()
    {
        string folder = Path.Combine(Path.GetTempPath(), "VoiceBridge-profile-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        string portable = Path.Combine(folder, RelayProfile.PortableFileName), local = Path.Combine(folder, "user.json");
        try
        {
            File.WriteAllText(portable, JsonSerializer.Serialize(Profile()));
            Assert.Equal(portable, RelayProfile.ResolvePath(local, portable));
            Assert.True(RelayProfile.RestorePortableIfMissing(local, portable));
            Assert.False(RelayProfile.RestorePortableIfMissing(local, portable));
            Assert.Equal(new string('a', 64), RelayProfile.LoadFile(local).ReadPublishKey());
            Assert.Equal(local, RelayProfile.ResolvePath(local, portable));
            Assert.Equal("https://relay.example", RelayProfile.LoadFile(local).Origin);
            RelayProfile.Import(portable, local); Assert.Equal(File.ReadAllText(local), File.ReadAllText(local + ".previous"));
        }
        finally { foreach (string path in Directory.GetFiles(folder)) File.Delete(path); Directory.Delete(folder); }
    }
    [Fact]
    public void WrongUserOrOriginCannotReplacePreviouslyWorkingCredential()
    {
        string folder = Path.Combine(Path.GetTempPath(), "VoiceBridge-invalid-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        string portable = Path.Combine(folder, "invalid.vbrelay"), local = Path.Combine(folder, "user.json");
        try
        {
            const string original = "preserve original"; File.WriteAllText(local, original);
            var invalid = Profile(); invalid.Origin = "https://other.example"; File.WriteAllText(portable, JsonSerializer.Serialize(invalid));
            Assert.ThrowsAny<CryptographicException>(() => RelayProfile.Import(portable, local)); Assert.Equal(original, File.ReadAllText(local));
            Assert.False(File.Exists(local + ".tmp"));
        }
        finally { foreach (string path in Directory.GetFiles(folder)) File.Delete(path); Directory.Delete(folder); }
    }
}
