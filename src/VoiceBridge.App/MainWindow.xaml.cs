using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using VoiceBridge.App.Room;
using VoiceBridge.Core;

namespace VoiceBridge.App;
public partial class MainWindow : Window
{
    private RoomClient? room;
    private bool busy, closing, canClose;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    public MainWindow()
    {
        InitializeComponent();
        UpdateControls(); Closing += OnClosing;
        timer.Tick += async (_, _) => { if (room is not null && !busy) { if (!room.Connected) await LeaveAsync(); else { StatusText.Text = room.Status + "\n" + (room.Audio.Options.Microphone ? "麦克风：开启" : "麦克风：关闭") + "\n" + room.Audio.Status; UpdateControls(); } } }; timer.Start();
    }
    private void UpdateControls()
    {
        bool active = room is not null; CreateButton.IsEnabled = !active && !busy; JoinButton.IsEnabled = !active && !busy; NicknameBox.IsEnabled = InviteBox.IsEnabled = !active && !busy;
        OpenButton.IsEnabled = CopyButton.IsEnabled = room?.Connected == true && !busy; LeaveButton.IsEnabled = active && !busy;
    }
    private async void Create_Click(object sender, RoutedEventArgs e) => await ConnectAsync(true);
    private async void Join_Click(object sender, RoutedEventArgs e) => await ConnectAsync(false);
    private async Task ConnectAsync(bool create)
    {
        if (busy || room is not null) return; busy = true; UpdateControls(); var candidate = new RoomClient();
        try
        {
            StatusText.Text = "正在连接房间…";
            if (create) await candidate.CreateAsync(string.IsNullOrWhiteSpace(NicknameBox.Text) ? "主持人" : NicknameBox.Text);
            else await candidate.JoinAsync(RoomInvite.Parse(InviteBox.Text), string.IsNullOrWhiteSpace(NicknameBox.Text) ? "朋友" : NicknameBox.Text);
            room = candidate; OpenRoom(); StatusText.Text = "已进房。麦克风会自动开启；网页可改昵称、关麦和共享本机音源。";
        }
        catch (Exception ex) { await candidate.DisposeAsync(); StatusText.Text = "未进房：" + ex.GetBaseException().Message; }
        finally { busy = false; UpdateControls(); }
    }
    private void Paste_Click(object sender, RoutedEventArgs e) { try { if (Clipboard.ContainsText()) InviteBox.Text = Clipboard.GetText(); } catch { StatusText.Text = "无法读取剪贴板，请手动粘贴邀请。"; } }
    private void OpenRoom() { if (room?.Connected == true) Process.Start(new ProcessStartInfo(room.ControllerLink) { UseShellExecute = true }); }
    private void Open_Click(object sender, RoutedEventArgs e) { try { OpenRoom(); } catch (Exception ex) { StatusText.Text = ex.Message; } }
    private void Copy_Click(object sender, RoutedEventArgs e) { if (room is not null) { Clipboard.SetText(room.Invite.Link); StatusText.Text = "朋友邀请已复制。只包含入房权限，不含你的本机控制权限。"; } }
    private async void Leave_Click(object sender, RoutedEventArgs e) => await LeaveAsync();
    private async Task LeaveAsync() { if (room is not { } current) return; room = null; busy = true; UpdateControls(); await current.DisposeAsync(); busy = false; StatusText.Text = "已退出房间，音频采集和播放已停止。"; UpdateControls(); }
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (canClose) return; e.Cancel = true; if (closing) return; if (busy) { StatusText.Text = "正在完成连接操作，请稍后关闭。"; return; }
        closing = true; timer.Stop(); await LeaveAsync(); canClose = true; Close();
    }
}
