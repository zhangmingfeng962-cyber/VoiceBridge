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
    private bool BroadcastActive => broadcast is not null || broadcastBusy || broadcastStopping;

    private void InitializeSharing()
    {
        BroadcastPlayerBox.ItemsSource = MusicPlayers.All;
        BroadcastPlayerBox.SelectedItem = MusicPlayers.GetOrDefault(settings.MusicPlayerId);
        BroadcastQuickCheck.Content = RelayProfile.IsConfigured ? "启用跨网收听（使用本机已配置的 HTTPS 音乐中继）" : "启用跨网收听（未配置中继时尝试临时链接）";
        UpdateBroadcastControls();
    }
    private void UpdateBroadcastControls()
    {
        BroadcastStartButton.IsEnabled = !BroadcastActive && !busy && !closing && supported;
        BroadcastStopButton.IsEnabled = BroadcastActive && !broadcastStopping && !closing;
        BroadcastPlayerBox.IsEnabled = BroadcastSeparateCheck.IsEnabled = BroadcastQuickCheck.IsEnabled = !BroadcastActive && !closing;
        BroadcastCopyButton.IsEnabled = broadcast?.PublicLink is not null && !broadcastBusy && !closing;
        BroadcastPreviewButton.IsEnabled = broadcast?.Server.LocalOrigin is not null && !closing;
        BroadcastHttpsButton.IsEnabled = broadcast?.Server.LocalOrigin is not null && !broadcastBusy && !closing;
    }
    private async void BroadcastStart_Click(object sender, RoutedEventArgs e)
    {
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
        var session = new MusicBroadcastSession(); broadcast = session;
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
                BroadcastLinkBox.Text = session.PublicLink!;
                BroadcastStatusText.Text = session.UsesRelay ? "已通过 HTTPS 中继确认首段加密音乐上传。复制完整链接给朋友，对方用浏览器点击“开始收听”；人声继续走原通话。" : "临时收听链接已建立。复制完整链接给朋友，对方点“开始收听”；实际跨网音质仍需对方确认。";
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
        if (broadcast?.PublicLink is not { } link) return;
        try { System.Windows.Clipboard.SetText(link); BroadcastStatusText.Text = "完整收听链接已复制，含解密 / 收听密钥。请只发给指定朋友；停止时会撤销，断网时最迟 2 分钟过期。"; }
        catch (Exception ex) { BroadcastStatusText.Text = "无法访问剪贴板：" + ex.GetBaseException().Message; }
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
        if (session.UsesRelay) BroadcastStatsText.Text = $"{session.Status} · 云端上传 {session.UploadedChunks} 段 · 网络队列丢包 {session.UploadDroppedPackets} 包\n48 kHz 立体声 · AES-GCM 端到端加密 · 每段 400 ms · 接收端默认缓冲 1.2 秒\n本机捕获缺样 {statistics.MissingMilliseconds:F1} ms · 丢弃 {statistics.DroppedMilliseconds:F1} ms。公网收听人数以实际接收为准，本机不统计朋友身份。";
        if (session.Failure is { } error) BroadcastStatusText.Text = "音乐采集已停止：" + error.GetBaseException().Message + " 请停止分享后重试。";
        else if (session.TunnelExited) BroadcastStatusText.Text = "临时互联网连接已断开。请停止分享后重新创建；本机服务仍在运行。";
        else if (session.RelayFailure is { } networkError) BroadcastStatusText.Text = "公网音乐上传已停止：" + networkError.GetBaseException().Message + " 请停止分享后检查网络并重试。";
        tray?.Update();
    }
    private void BroadcastHelp_Click(object sender, RoutedEventArgs e) => ShowText("独立音乐分享", "1. 在独立分享区选择 QQ音乐、网易云音乐或酷狗音乐，并开始播放。\n2. 点击“开始独立分享”。默认关闭送进语音频道的音乐，保持正常人声交流。独立通道不采集麦克风。\n3. 等临时链接建立后，复制完整链接给朋友。朋友用新版 Edge / Chrome 打开并点击“开始收听”，无需麦克风权限。建议戴耳机。\n4. 接收端可选 120 / 240 / 480 ms 缓冲并调节收听音量。更大缓冲增加延迟，不能弥补持续带宽不足。\n5. 停止独立分享、全部停止或从托盘退出会断开收听连接，旧链接失效；重新开始会生成新密钥。收起到托盘会继续分享，开机启动不会自动分享。\n\n未压缩 48 kHz 双声道 float32，每位收听者约需 3.1 Mbps 上行，另有协议开销。浏览器有少量时钟漂移校正和断流淡入淡出，不添加人声降噪或自动增益。不能保证网络零中断、端到端低于 100 ms 或所有设备听感完全相同。\n\n临时互联网链接使用官方 cloudflared 2026.10.1，首次下载约 53 MB 并验证 SHA-256。无需管理员、Cloudflare 账号或端口转发，不安装系统服务。音频经 Cloudflare 转发，不是端到端加密；不要分享私密内容。临时服务用于测试，无持续可用保证。链接持有者可收听，请勿公开转发。\n\n固定地址：取消临时链接、开始分享，在高级区查看本机端口。将自己的 HTTPS 反向代理转发到该地址，支持 /audio WebSocket、关闭缓存和响应缓冲，然后填根地址。软件不会自动配置外部服务器，也不开放本机文件、麦克风或控制接口。");
}
