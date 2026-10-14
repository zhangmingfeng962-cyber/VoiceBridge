namespace VoiceBridge.App.Platform;

public static class BuildInfo
{
    public static string Version => typeof(BuildInfo).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
}
