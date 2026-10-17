using System.Windows;
using VoiceBridge.App.Platform;
using VoiceBridge.App.Sharing;

namespace VoiceBridge.App;

public partial class MainWindow
{
    private MusicBroadcastSession? broadcast;
    private CancellationTokenSource? broadcastCancellation;
    private Task? broadcastTask, broadcastStopTask;
    private bool broadcastBusy, broadcastStopping;
    private bool broadcastMusicEnabled;
    private bool BroadcastActive => broadcast is not null || broadcastBusy || broadcastStopping;

    private void InitializeSharing()
    {
        BroadcastPlayerBox.ItemsSource = MusicPlayers.All;
        BroadcastPlayerBox.SelectedItem = MusicPlayers.GetOrDefault(settings.MusicPlayerId);
        BroadcastVolumeSlider.Value = settings.BroadcastMusicVolume;
        UpdateBroadcastVolume();
        BroadcastQuickCheck.Content = RelayProfile.IsConfigured ? "启用跨网收听（使用本机已配置的 HTTPS 音乐中继）" : "启用跨网收听（未配置中继时尝试临时链接）";
        UpdateBroadcastControls();
    }
    private void UpdateBroadcastControls()
    {
        BroadcastStartButton.IsEnabled = !broadcastStopping && !busy && !closing && supported;
        BroadcastStartButton.Content = broadcastStopping ? "正在关闭房间…" : BroadcastActive ? "关闭房间" : "创建音乐房间";
        BroadcastMusicButton.IsEnabled = broadcast is not null && !broadcastStopping && !closing;
        BroadcastMusicButton.Content = broadcastMusicEnabled && BroadcastActive ? "伴奏：开" : "伴奏：关";
        BroadcastRoomButton.IsEnabled = broadcast?.UsesRelay == true && broadcast.PublicLink is not null && !broadcastBusy && !closing;
        BroadcastStopButton.IsEnabled = BroadcastActive && !broadcastStopping && !closing;
        BroadcastPlayerBox.IsEnabled = BroadcastSeparateCheck.IsEnabled = BroadcastQuickCheck.IsEnabled = !BroadcastActive && !closing;
        BroadcastCopyButton.IsEnabled = broadcast?.PublicLink is not null && !broadcastBusy && !closing;
        BroadcastPreviewButton.IsEnabled = broadcast?.Server.LocalOrigin is not null && !closing;
        BroadcastHttpsButton.IsEnabled = broadcast?.Server.LocalOrigin is not null && !broadcastBusy && !closing;
    }
    private async void BroadcastStart_Click(object sender, RoutedEventArgs e)
    {
        if (BroadcastActive) { await StopBroadcastAsync(); return; }
        if (BroadcastActive || busy || closing || !supported) return;
        var player = BroadcastPlayerBox.SelectedItem as MusicPlayer ?? MusicPlayers.QQMusic;
        bool internet = BroadcastQuickCheck.IsChecked == true, separate = BroadcastSeparateCheck.IsChecked == true;
        broadcastBusy = true; broadcastCancellation = new CancellationTokenSource();
        UpdateControls();
        broadcastTask = StartBroadcastAsync(player.Id, internet, separate, broadcastCancellation.Token);
        try { await broadcastTask; }
        finally { broadcastBusy = false; UpdateControls(); }
    }
    private async Task StartBroadcastAsync(string playerId, bool internet, bool separate, CancellationToken token)
    {
        var session = new MusicBroadcastSession(); broadcast = session; broadcastMusicEnabled = true;
        UpdateBroadcastVolume();
        try
        {
            if (separate && sendingMusic) await SetSourcesAsync(sendingMicrophone, false);
            token.ThrowIfCancellationRequested();
            BroadcastStatusText.Text = "正在开启独立音乐捕获，不会采集麦克风…";
            await session.StartAsync(playerId, token);
            BroadcastLocalText.Text = "本机监听：" + session.Server.LocalOrigin + "（只接受本机连接）";
            BroadcastLinkBox.Text = session.Server.LocalLink;
            UpdateBroadcastControls();
            if (internet)
            {
                var progress = new Progress<string>(message => { if (ReferenceEquals(broadcast, session)) BroadcastStatusText.Text = message; });
                if (RelayProfile.IsConfigured) await session.CreateRelayLinkAsync(playerId, progress, token);
                else await session.CreateInternetLinkAsync(progress, token);
                BroadcastLinkBox.Text = RoomLink(session);
                BroadcastStatusText.Text = session.UsesRelay ? "音乐房间已就绪。复制邀请给朋友，你点击“进入语音房间”；双方进房间后可单独开启麦克风，伴奏不会经过人声降噪。" : "临时音乐链接已建立；此旧连接仅支持收听，语音房间需要配置新版 HTTPS 中继。";
            }
            else BroadcastStatusText.Text = "本机分享已开始。可本机试听，或在高级选项接入自己的 HTTPS 反向代理。";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { BroadcastStatusText.Text = "已取消创建链接。"; }
        catch (Exception ex)
        {
            // Keep a successfully started local session for preview/custom HTTPS after a tunnel failure.
            BroadcastStatusText.Text = "互联网链接未就绪：" + ex.GetBaseException().Message + (session.Server.LocalOrigin is not null ? " 本机服务仍在运行，可停止后重试或配置固定 HTTPS 地址。" : " 请停止后重试。");
        }
    }
    private async void BroadcastStop_Click(object sender, RoutedEventArgs e) => await StopBroadcastAsync();
    private Task StopBroadcastAsync()
    {
        if (broadcastStopTask is { IsCompleted: false }) return broadcastStopTask;
        return broadcastStopTask = StopBroadcastCoreAsync();
    }
    private async Task StopBroadcastCoreAsync()
    {
        broadcastStopping = true; UpdateBroadcastControls();
        broadcastCancellation?.Cancel();
        if (broadcastTask is { } pending) { try { await pending; } catch (Exception ex) { Log.Write(ex.GetType().Name); } }
        var session = broadcast; bool wasRelay = session?.UsesRelay == true; broadcast = null;
        broadcastMusicEnabled = false;
        if (session is not null)
        {
            try { await session.DisposeAsync(); }
            catch (Exception ex) { Log.Write(ex.GetType().Name); BroadcastStatusText.Text = "停止分享时出现错误，请退出程序后重试。"; }
        }
        broadcastCancellation?.Dispose(); broadcastCancellation = null; broadcastTask = null; broadcastBusy = broadcastStopping = false;
        BroadcastLinkBox.Text = "分享已停止，原链接已失效";
        BroadcastStatusText.Text = wasRelay ? "已停止上传并尝试撤销收听；若网络中断，旧房间最迟在停止上传 2 分钟后过期。" : "独立音乐分享已停止；所有收听连接已断开。";
        BroadcastLocalText.Text = "本机监听端口：未启动";
        BroadcastStatsText.Text = "未连接收听端。";
        UpdateControls();
    }
    private async Task StopAllAsync()
    {
        await StopBroadcastAsync();
        await SetSourcesAsync(false, false);
    }
    private void BroadcastCopy_Click(object sender, RoutedEventArgs e)
    {
        if (broadcast?.PublicLink is null) return;
        string link = RoomLink(broadcast);
        try { System.Windows.Clipboard.SetText(link); BroadcastStatusText.Text = "完整收听链接已复制，含解密 / 收听密钥。请只发给指定朋友；停止时会撤销，断网时最迟 2 分钟过期。"; }
        catch (Exception ex) { BroadcastStatusText.Text = "无法访问剪贴板：" + ex.GetBaseException().Message; }
    }
    private static string RoomLink(MusicBroadcastSession session) => session.UsesRelay ? session.PublicLink!.Replace("/listen/", "/room/", StringComparison.Ordinal) : session.PublicLink!;
    private void BroadcastRoom_Click(object sender, RoutedEventArgs e)
    {
        if (broadcast?.UsesRelay != true || broadcast.PublicLink is null) return;
        try { Open(RoomLink(broadcast) + ".host"); }
        catch (Exception ex) { BroadcastStatusText.Text = ex.GetBaseException().Message; }
    }
    private void BroadcastMusic_Click(object sender, RoutedEventArgs e)
    {
        if (broadcast is null) return;
        broadcastMusicEnabled = !broadcastMusicEnabled; UpdateBroadcastVolume(); UpdateBroadcastControls();
    }
    private void BroadcastVolume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    { if (IsInitialized) UpdateBroadcastVolume(); }
    private void UpdateBroadcastVolume()
    {
        settings.BroadcastMusicVolume = BroadcastVolumeSlider.Value;
        BroadcastVolumeValue.Text = $"{BroadcastVolumeSlider.Value:0}%";
        if (broadcast is { } session) { session.Gain.Volume = (float)BroadcastVolumeSlider.Value / 100; session.Gain.Muted = !broadcastMusicEnabled; }
    }
    private void BroadcastPreview_Click(object sender, RoutedEventArgs e)
    {
        if (broadcast?.Server.LocalOrigin is null) return;
        try { Open(broadcast.Server.LocalLink); BroadcastStatusText.Text = "已打开本机接收页。可先将页面音量调至 0% 观察连接；试听会与原播放器叠加，请使用较低音量。"; }
        catch (Exception ex) { BroadcastStatusText.Text = ex.GetBaseException().Message; }
    }
    private void BroadcastHttps_Click(object sender, RoutedEventArgs e)
    {
        if (broadcast is null || broadcastBusy) return;
        try
        {
            broadcast.UseHttpsOrigin(new Uri(BroadcastHttpsBox.Text.Trim(), UriKind.Absolute));
            BroadcastLinkBox.Text = broadcast.PublicLink!;
            BroadcastStatusText.Text = "已使用填写的 HTTPS 地址生成邀请。尚未验证代理配置，请先用另一个网络打开测试。";
            UpdateBroadcastControls();
        }
        catch (Exception ex) { BroadcastStatusText.Text = "地址未设置：" + ex.GetBaseException().Message; }
    }
    private void UpdateBroadcastTelemetry()
    {
        if (broadcast is not { } session || broadcastBusy) return;
        var statistics = session.Statistics();
        BroadcastStatsText.Text = $"{session.Status} · 收听 {session.Server.ListenerCount}/{MusicLinkServer.MaximumListeners} 人 · 发出 {session.Packets} 包\n捕获缺样 {statistics.MissingMilliseconds:F1} ms · 丢弃 {statistics.DroppedMilliseconds:F1} ms · 原生不连续 {statistics.Discontinuities} 次 · 慢连接断开 {session.Server.SlowConnections} 次\n源过载样本 {session.OverloadedSamples} · 非有限样本 {session.InvalidSamples}。过载时请降低播放器 EQ / 音效；缺样统计也可能包含启动、暂停。";
        if (session.UsesRelay) BroadcastStatsText.Text = $"{session.Status} · 云端上传 {session.UploadedChunks} 段 · 网络队列丢包 {session.UploadDroppedPackets} 包\n48 kHz 立体声 · AES-GCM 端到端加密 · 每段 400 ms · 接收端默认缓冲 2.4 秒\n本机捕获缺样 {statistics.MissingMilliseconds:F1} ms · 丢弃 {statistics.DroppedMilliseconds:F1} ms。公网收听人数以实际接收为准，本机不统计朋友身份。";
        if (session.Failure is { } error) BroadcastStatusText.Text = "音乐采集已停止：" + error.GetBaseException().Message + " 请停止分享后重试。";
        else if (session.TunnelExited) BroadcastStatusText.Text = "临时互联网连接已断开。请停止分享后重新创建；本机服务仍在运行。";
        else if (session.RelayFailure is { } networkError) BroadcastStatusText.Text = "公网音乐上传已停止：" + networkError.GetBaseException().Message + " 请停止分享后检查网络并重试。";
        tray?.Update();
    }
    private void BroadcastHelp_Click(object sender, RoutedEventArgs e) => ShowText("音乐房间", "选择播放器并播放音乐，点击“创建音乐房间”。上传确认后点击“邀请朋友”，你自己点“进入语音房间”。双方在浏览器里进入房间，主动点击开启麦克风后才能讲话；默认不开麦。\n\n房间最多 8 人。桌面伴奏开关和音量只控制传给房间的音乐，讲话不会自动压低伴奏。房间人声与麦克风在浏览器页面控制。主持人页面默认音乐 0%，避免与本机播放器重复。\n\n音乐为 48 kHz 立体声独立加密传输，默认 2.4 秒缓冲；人声使用另一路加密连接。两者延迟不同，没有精确同步或 100 ms 保证。使用耳机；朋友可分别调整音乐与人声音量。\n\n点击关闭房间会停止上传并撤销链接，断网未撤销时最多 120 秒过期。最小化到托盘会继续，启动软件不会自动开房或开麦。\n\n需要配置新版 HTTPS 中继。免费托管受休眠、带宽与性能限制。没有发送配置时，兼容的临时链接只支持音乐收听，不能用于房间语音。邀请链接含房间密钥，只发给指定朋友。");
}
