using Velopack;
using VoiceBridge.App.Room;
namespace VoiceBridge.App.Updates;
public sealed class UpdateService(Func<bool> inRoom, UpdateManager? updateManager = null)
{
    private readonly UpdateManager manager = updateManager ?? new(new SignedWebSource(new Uri(RoomService.DefaultOrigin + "/updates/"), SignedWebSource.ReadPublicKey()), new UpdateOptions { ExplicitChannel = "win-x64" });
    private UpdateInfo? update;
    public string Notes => update?.TargetFullRelease.NotesMarkdown ?? "";
    public bool Available => update is not null;
    public bool Deferred { get; private set; }
    public bool Busy { get; private set; }
    public string Label { get; private set; } = "检查更新";
    public event Action? Changed;
    public async Task CheckAsync()
    {
        if (Busy) return;
        try { if (!manager.IsInstalled) { Label = "安装版支持自动更新"; return; } update = await manager.CheckForUpdatesAsync(); Label = update is null ? "已是最新版本" : "更新到 " + update.TargetFullRelease.Version; }
        catch (Exception) { Label = "更新检查失败，点击重试"; }
        finally { Changed?.Invoke(); }
    }
    public async Task UpdateAsync()
    {
        if (Busy) return;
        if (update is null) { await CheckAsync(); return; }
        if (inRoom()) { Deferred = true; Label = "已预约退房后更新"; Changed?.Invoke(); return; }
        Busy = true; Deferred = false; Changed?.Invoke();
        try
        {
            await manager.DownloadUpdatesAsync(update, value => { Label = "下载更新 " + value + "%"; Changed?.Invoke(); });
            if (inRoom()) { Deferred = true; Label = "更新已准备，退房后安装"; return; }
            Label = "安装更新并重启"; Changed?.Invoke(); manager.ApplyUpdatesAndRestart(update);
        }
        catch (Exception) { Label = "更新未完成，当前版本可继续使用"; }
        finally { Busy = false; Changed?.Invoke(); }
    }
    public Task AfterLeavingAsync() => Deferred ? UpdateAsync() : Task.CompletedTask;
}
