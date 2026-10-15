namespace VoiceBridge.App.Platform;

// Preserve the QQMusic-specific entry point for older diagnostics.
public static class QQMusicFinder
{
    public static uint[] Roots() => MusicProcessFinder.Roots(MusicPlayers.QQMusicId);
}
