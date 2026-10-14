using System.Diagnostics;
using System.IO;
using System.IO.Compression;

namespace VoiceBridge.App.Platform;
public static class CableInstaller
{
    public const string PackageHash = "B950E39F01AF1D04EA623C8F6D8EB9B6EA5C477C637295FABF20631C85116BFB";
    public static async Task LaunchAsync()
    {
        await BundledAssets.VerifyDriverAsync();
        // Fresh extraction from the pinned original ZIP, never execute previously extracted files.
        string folder = Path.Combine(Settings.Folder, "installers", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        using (var stream = BundledAssets.Open("VBCABLE_Driver_Pack45.zip"))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Read)) archive.ExtractToDirectory(folder);
        string exe = Path.Combine(folder, "VBCABLE_Setup_x64.exe");
        using var process = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas", WorkingDirectory = folder });
        if (process is not null) await process.WaitForExitAsync();
        Log.Write("Official VB-CABLE setup exited. Reboot and refresh devices to verify installation.");
    }
}
