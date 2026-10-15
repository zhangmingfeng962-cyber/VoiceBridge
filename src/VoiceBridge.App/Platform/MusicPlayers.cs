namespace VoiceBridge.App.Platform;

public sealed record MusicPlayer(string Id, string Name, string ExecutableName)
{
    public override string ToString() => Name;
}

public static class MusicPlayers
{
    public const string QQMusicId = "qqmusic";
    public static MusicPlayer QQMusic { get; } = new(QQMusicId, "QQ音乐", "QQMusic.exe");
    public static MusicPlayer NetEase { get; } = new("netease", "网易云音乐", "cloudmusic.exe");
    public static MusicPlayer KuGou { get; } = new("kugou", "酷狗音乐", "KuGou.exe");
    public static IReadOnlyList<MusicPlayer> All { get; } = Array.AsReadOnly(new[] { QQMusic, NetEase, KuGou });

    public static MusicPlayer? Find(string? id) => All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
    public static MusicPlayer Get(string id) => Find(id) ?? throw new ArgumentException("不支持的音乐来源，请从播放器列表中选择。", nameof(id));
    public static MusicPlayer GetOrDefault(string? id) => Find(id) ?? QQMusic;
}
