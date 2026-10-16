using Forms = System.Windows.Forms;

namespace VoiceBridge.App.Platform;

public sealed record TrayState(bool MicrophoneOn, bool MusicOn, int MusicVolume, bool CanChange, bool Running, bool Busy, string MusicPlayerName = "QQ音乐", bool IndependentMusic = false);

public sealed class TrayController : IDisposable
{
    private readonly Forms.NotifyIcon icon;
    private readonly Forms.ContextMenuStrip menu = new();
    private readonly System.Drawing.Icon drawingIcon = AppIcon.CreateTrayIcon();
    private readonly Forms.ToolStripMenuItem microphone = new();
    private readonly Forms.ToolStripMenuItem music = new();
    private readonly Forms.ToolStripMenuItem volumeUp = new("音乐音量 +5%");
    private readonly Forms.ToolStripMenuItem volumeDown = new("音乐音量 -5%");
    private readonly Forms.ToolStripMenuItem stop = new("全部停止");
    private readonly Forms.ToolStripMenuItem independent = new("独立音乐链接：关") { Enabled = false };
    private readonly Func<TrayState> getState;
    private bool disposed;
    public bool Visible => !disposed && icon.Visible;

    public TrayController(Func<TrayState> getState, Action show, Func<Task> toggleMicrophone, Func<Task> toggleMusic, Action<int> adjustMusic, Func<Task> stopAll, Action exit)
    {
        this.getState = getState;
        var showItem = new Forms.ToolStripMenuItem("显示主窗口");
        var exitItem = new Forms.ToolStripMenuItem("退出程序");
        showItem.Click += (_, _) => show();
        microphone.Click += async (_, _) => await RunAsync(toggleMicrophone);
        music.Click += async (_, _) => await RunAsync(toggleMusic);
        volumeUp.Click += (_, _) => adjustMusic(5);
        volumeDown.Click += (_, _) => adjustMusic(-5);
        stop.Click += async (_, _) => await RunAsync(stopAll);
        exitItem.Click += (_, _) => exit();
        menu.Items.AddRange([showItem, new Forms.ToolStripSeparator(), microphone, music, volumeUp, volumeDown, independent, stop, new Forms.ToolStripSeparator(), exitItem]);
        menu.Opening += (_, _) => Update();
        icon = new Forms.NotifyIcon { Icon = drawingIcon, Text = "VoiceBridge", ContextMenuStrip = menu, Visible = true };
        icon.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) show(); };
        Update();
    }

    public void Update()
    {
        if (disposed) return;
        var state = getState();
        microphone.Text = state.MicrophoneOn ? "人声：开" : "人声：关";
        music.Text = state.MusicOn ? $"{state.MusicPlayerName}：开" : $"{state.MusicPlayerName}：关";
        microphone.Checked = state.MicrophoneOn;
        music.Checked = state.MusicOn;
        microphone.Enabled = music.Enabled = state.CanChange;
        volumeUp.Enabled = !state.Busy && state.MusicVolume < 100;
        volumeDown.Enabled = !state.Busy && state.MusicVolume > 0;
        stop.Enabled = !state.Busy && state.Running;
        independent.Text = state.IndependentMusic ? "独立音乐链接：开（收起后继续）" : "独立音乐链接：关";
        icon.Text = $"VoiceBridge · 人声{(state.MicrophoneOn ? "开" : "关")} · 频道音乐{(state.MusicOn ? "开" : "关")} · 链接{(state.IndependentMusic ? "开" : "关")}";
    }

    public void ExplainMinimize()
    {
        if (disposed) return;
        icon.ShowBalloonTip(3000, "VoiceBridge 已收起到托盘", "音频状态保持不变。左键显示窗口，右键控制人声、音乐或退出程序。", Forms.ToolTipIcon.Info);
    }

    private static async Task RunAsync(Func<Task> action)
    {
        try { await action(); } catch (Exception ex) { Log.Write(ex.ToString()); }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        icon.Visible = false;
        icon.Dispose();
        menu.Dispose();
        drawingIcon.Dispose();
    }
}
