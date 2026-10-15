using Microsoft.Win32;
using VoiceBridge.App.Platform;
using Xunit;

namespace VoiceBridge.Tests;

public sealed class StartupTests : IDisposable
{
    private readonly string keyPath = @"Software\VoiceBridge.Tests\Startup-" + Guid.NewGuid().ToString("N");
    private const string Executable = @"C:\音乐工具 & 语音\Voice Bridge.exe";
    private StartupRegistration Create(string path = Executable) => new(keyPath, () => path);

    [Fact]
    public void ReadingDisabledStartupDoesNotCreateRegistryKeys()
    {
        Assert.False(Create().Read().Enabled);
        using var key = Registry.CurrentUser.OpenSubKey(keyPath);
        Assert.Null(key);
    }

    [Fact]
    public void TogglePersistsQuotedExecutableAndOnlyRemovesItsOwnValue()
    {
        using var key = Registry.CurrentUser.CreateSubKey(keyPath, writable: true);
        key.SetValue("OtherProgram", "unchanged");
        Create().SetEnabled(true);
        Assert.Equal(new StartupState(true, true), Create().Read());
        Assert.Equal("\"" + Executable + "\" --startup", key.GetValue(StartupRegistration.ValueName));
        Assert.Equal(RegistryValueKind.String, key.GetValueKind(StartupRegistration.ValueName));
        Create().SetEnabled(false);
        Create().SetEnabled(false);
        Assert.False(Create().Read().Enabled);
        Assert.Null(key.GetValue(StartupRegistration.ValueName));
        Assert.Equal("unchanged", key.GetValue("OtherProgram"));
    }

    [Fact]
    public void MovingTheExecutableIsDetectedAndReenablingUpdatesThePath()
    {
        Create().SetEnabled(true);
        var moved = Create(@"C:\Users\Example\Desktop\VoiceBridge.exe");
        Assert.Equal(new StartupState(true, false), moved.Read());
        moved.SetEnabled(false);
        moved.SetEnabled(true);
        Assert.Equal(new StartupState(true, true), moved.Read());
    }

    [Fact]
    public void InvalidOrTooLongPathsDoNotChangeExistingRegistration()
    {
        Create().SetEnabled(true);
        foreach (string path in new[] { "VoiceBridge.exe", @"C:\Tools\VoiceBridge.dll", "C:\\bad\"path.exe", "C:\\bad\npath.exe", @"C:\" + new string('a', 250) + ".exe" })
            Assert.Throws<InvalidOperationException>(() => Create(path).SetEnabled(true));
        Assert.Equal(new StartupState(true, true), Create().Read());
    }

    public void Dispose() => Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
}
