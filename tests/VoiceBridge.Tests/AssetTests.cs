using VoiceBridge.App.Platform;
using Xunit;

namespace VoiceBridge.Tests;
public sealed class AssetTests
{
    [Fact]
    public async Task EmbeddedDriverMatchesVendorPackageWithoutExternalFiles()
    {
        Assert.Equal(CableInstaller.PackageHash, await BundledAssets.VerifyDriverAsync());
        Assert.Contains("DOTNET-RUNTIME-LICENSE.txt", BundledAssets.Names);
        Assert.Contains("VB-CABLE", BundledAssets.LicenseText());
    }
}
