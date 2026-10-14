using System.IO;

namespace VoiceBridge.App.Platform;

public static class AppIcon
{
    public static System.Drawing.Icon CreateTrayIcon()
    {
        using var stream = typeof(AppIcon).Assembly.GetManifestResourceStream("VoiceBridge.Icons.App.ico")
            ?? throw new FileNotFoundException("应用图标资源缺失。");
        using var original = new System.Drawing.Icon(stream, 32, 32);
        return (System.Drawing.Icon)original.Clone();
    }
}
