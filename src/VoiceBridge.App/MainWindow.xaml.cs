using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Navigation;
using System.Windows.Threading;
using Microsoft.Win32;
using VoiceBridge.App.Audio;
using VoiceBridge.App.Platform;

namespace VoiceBridge.App;
public partial class MainWindow : Window
{
    private readonly AudioEngine engine = new();
    private readonly Settings settings;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private Hotkeys? hotkeys;
    private TrayController? tray;
    private bool busy, closing, canClose;
    private bool exitRequested, exitQueued, systemShutdown, trayNoticeShown;
    private WindowState restoreState = WindowState.Normal;
    private bool sendingMicrophone, sendingMusic;
    private int tick;
    private readonly bool supported = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348);
    private readonly bool persistSettingsOnClose;
    private readonly bool showTrayNotifications;
    private readonly StartupRegistration startup;
    private readonly bool useAudioHardware;
    public bool IsHiddenToTray { get; private set; }
    public bool TrayIconVisible => tray?.Visible == true;
    public MainWindow() : this(true) { }
    public MainWindow(bool persistSettingsOnClose, bool enableTray = true, bool showTrayNotifications = true, StartupRegistration? startupRegistration = null, Settings? initialSettings = null, bool useAudioHardware = true)
    {
        settings = initialSettings ?? Settings.Load();
        this.persistSettingsOnClose = persistSettingsOnClose;
        this.showTrayNotifications = showTrayNotifications;
        this.useAudioHardware = useAudioHardware;
        startup = startupRegistration ?? new StartupRegistration();
        InitializeComponent();
        InitializeScenarios();
        InitializeSharing();
        UpdateStartupControl();
        Activated += (_, _) => UpdateStartupControl();
        MicSlider.Value = settings.MicrophoneVolume; MusicSlider.Value = settings.MusicVolume;
        UpdateVolumes();
        PlatformText.Text = supported ? "人声、音乐分别开关 · 关闭一路不会打断另一路" : "此 Windows 版本不支持进程音频捕获。普通 Win10 19045 无法使用本项目的共享功能，请升级到 Win11。";
        RefreshDevices();
        SourceInitialized += (_, _) =>
        {
            hotkeys = new Hotkeys(new WindowInteropHelper(this).Handle, OnHotkey, out var status);
            HotkeyText.Text = status;
            if (enableTray)
            {
                try
                {
                    tray = new TrayController(GetTrayState, RestoreFromTray,
                        () => SetSourcesAsync(!sendingMicrophone, sendingMusic),
                        () => SetSourcesAsync(sendingMicrophone, !sendingMusic),
                        amount => MusicSlider.Value = Math.Clamp(MusicSlider.Value + amount, 0, 100),
                        StopAllAsync, RequestExit);
                }
                catch (Exception ex) { Log.Write(ex.ToString()); StatusText.Text = "托盘暂不可用，关闭窗口将正常退出。"; }
            }
        };
        timer.Tick += Tick;
        timer.Start();
        Closing += OnClosing;
        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Minimized && TrayIconVisible) HideToTray();
            else if (IsVisible) restoreState = WindowState;
        };
        Closed += (_, _) => { tray?.Dispose(); hotkeys?.Dispose(); };
    }
    private void RefreshDevices()
    {
        try
        {
            string? micId = (MicrophoneBox.SelectedItem as DeviceChoice)?.Id ?? settings.MicrophoneId;
            string? cableId = (CableBox.SelectedItem as DeviceChoice)?.Id ?? settings.CableId;
            var list = useAudioHardware ? AudioDevices.List() : (Microphones: Array.Empty<DeviceChoice>(), Cables: Array.Empty<DeviceChoice>(), DefaultMic: (string?)null);
            MicrophoneBox.ItemsSource = list.Microphones; CableBox.ItemsSource = list.Cables;
            MicrophoneBox.SelectedItem = list.Microphones.FirstOrDefault(x => x.Id == micId) ?? list.Microphones.FirstOrDefault(x => x.Id == list.DefaultMic) ?? list.Microphones.FirstOrDefault();
            CableBox.SelectedItem = list.Cables.FirstOrDefault(x => x.Id == cableId) ?? list.Cables.FirstOrDefault();
            if (list.Cables.Length == 0) StatusText.Text = "未检测到 VB-CABLE。请打开下方官方安装器，完成后重启，再刷新设备。";
            else if (list.Microphones.Length == 0) StatusText.Text = "未检测到可用的真实麦克风。请检查连接和 Windows 麦克风权限。";
            else
            {
                using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
                try { AudioDevices.CheckPlaybackDefaults(enumerator); StatusText.Text = "设备已就绪。点击人声或音乐的开关即可开始，语音软件输入请选择 CABLE Output。"; }
                catch (InvalidOperationException) { StatusText.Text = "无法启动：默认播放或默认通信播放指向了 VB-CABLE。请先点击“修复播放路由”，再启动混音。"; }
            }
        }
        catch (Exception ex) { Report(ex); }
        CaptureScenarioControls(); ReadDefaultInputStatus(); UpdateControls();
    }
    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (engine.Running || BroadcastActive) await StopAllAsync();
        else await SetSourcesAsync(true, false);
    }
    private async Task SetSourcesAsync(bool microphoneOn, bool musicOn)
    {
        if (busy || closing) return;
        busy = true; UpdateControls();
        try
        {
            if (!microphoneOn && !musicOn) { CancelLocalOperation(); await engine.StopAsync(); }
            else if (!engine.Running)
            {
                if (MicrophoneBox.SelectedItem is not DeviceChoice mic || CableBox.SelectedItem is not DeviceChoice cable)
                    throw new InvalidOperationException("请先选择真实麦克风和 CABLE Input。");
                CaptureScenarioControls();
                await engine.StartAsync(mic.Id, cable.Id, (float)MicSlider.Value / 100, (float)MusicSlider.Value / 100, microphoneOn, musicOn, SelectedMusicPlayer.Id, CurrentScenario.ChannelMode, CurrentScenario.OutputBufferMs);
            }
            else if (engine.Mixer is { } mixer) { mixer.MicrophoneEnabled = microphoneOn; mixer.MusicEnabled = musicOn; }
            sendingMicrophone = microphoneOn; sendingMusic = musicOn;
            StatusText.Text = (microphoneOn, musicOn) switch
            {
                (true, true) => $"正在传送人声 + {SelectedMusicPlayer.Name}。语音软件输入选 CABLE Output，输出选耳机。",
                (true, false) => "只传人声，音乐已关闭。你本地的音乐仍可正常播放。",
                (false, true) => $"只传{SelectedMusicPlayer.Name}，人声已静音。",
                _ => "人声和音乐都已关闭，音频采集已停止。"
            };
        }
        catch (Exception ex)
        {
            try { await engine.StopAsync(); } catch (Exception cleanup) { Log.Write(cleanup.ToString()); }
            sendingMicrophone = sendingMusic = false;
            Report(ex);
        }
        finally { busy = false; UpdateControls(); }
    }
    private void UpdateControls()
    {
        StartButton.Content = engine.Running || BroadcastActive ? "全部停止" : "启动人声";
        StartButton.IsEnabled = !busy && supported && (engine.Running || BroadcastActive || (MicrophoneBox.SelectedItem is not null && CableBox.SelectedItem is not null));
        bool canToggle = !busy && supported && MicrophoneBox.SelectedItem is not null && CableBox.SelectedItem is not null && (!engine.Running || engine.Failure is null);
        ShareButton.IsEnabled = MicrophoneButton.IsEnabled = canToggle;
        MicrophoneButton.Content = sendingMicrophone ? "人声：开" : "人声：关";
        ShareButton.Content = sendingMusic ? "音乐：开" : "音乐：关";
        MicrophoneButton.Background = new System.Windows.Media.SolidColorBrush(sendingMicrophone ? System.Windows.Media.Color.FromRgb(42, 108, 160) : System.Windows.Media.Color.FromRgb(48, 68, 92));
        ShareButton.Background = new System.Windows.Media.SolidColorBrush(sendingMusic ? System.Windows.Media.Color.FromRgb(27, 119, 94) : System.Windows.Media.Color.FromRgb(36, 67, 63));
        MicrophoneBox.IsEnabled = CableBox.IsEnabled = MusicPlayerBox.IsEnabled = RefreshButton.IsEnabled = RepairButton.IsEnabled = InstallButton.IsEnabled = !engine.Running && !busy;
        ScenarioBox.IsEnabled = ChannelModeBox.IsEnabled = BufferBox.IsEnabled = !engine.Running && !busy && !localOperation && !closing;
        UpdateScenarioStatus();
        UpdateBroadcastControls();
        tray?.Update();
        QueueExitIfReady();
    }
    private async void Share_Click(object sender, RoutedEventArgs e) => await SetSourcesAsync(sendingMicrophone, !sendingMusic);
    private async void Microphone_Click(object sender, RoutedEventArgs e) => await SetSourcesAsync(!sendingMicrophone, sendingMusic);
    private async void OnHotkey(int id)
    {
        if (id == 1) await SetSourcesAsync(sendingMicrophone, !sendingMusic);
        else if (id == 4) await SetSourcesAsync(!sendingMicrophone, sendingMusic);
        else MusicSlider.Value = Math.Clamp(MusicSlider.Value + (id == 2 ? 5 : -5), 0, 100);
    }
    private void Volume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) { if (IsInitialized) { UpdateVolumes(); CaptureScenarioControls(); UpdateScenarioStatus(); } }
    private void UpdateVolumes()
    {
        MicValue.Text = $"{MicSlider.Value:0}%"; MusicValue.Text = $"{MusicSlider.Value:0}%";
        if (engine.Mixer is { } mix) { mix.MicGain = (float)MicSlider.Value / 100; mix.MusicGain = (float)MusicSlider.Value / 100; }
        tray?.Update();
    }
    private async void Tick(object? sender, EventArgs e)
    {
        if (closing || busy) return;
        if (engine.Mixer is { } mix)
        {
            MicMeter.Value = sendingMicrophone ? mix.MicPeak : 0; MusicMeter.Value = sendingMusic ? mix.MusicPeak : 0;
            TelemetryText.Text = mix.MusicPeak > 1.001f ? $"{SelectedMusicPlayer.Name}源输出过载：请降低其 EQ / 音效增益，避免源音频失真。" : mix.LimiterReductionDb > 3 ? "音乐保持固定音量；人声出现过载削峰，建议降低麦克风音量。" : "48 kHz 混音 · 音乐固定增益 · 接收音质需在语音平台确认";
            ProcessText.Text = engine.MusicStatus;
        }
        if (++tick % 10 == 0 && !engine.Running)
        {
            try { var player = SelectedMusicPlayer; var roots = MusicProcessFinder.Roots(player.Id); ProcessText.Text = roots.Length == 0 ? $"等待{player.Name} · 打开后自动识别" : $"已发现{player.Name} · {roots.Length} 个进程树"; }
            catch (Exception ex) { Log.Write(ex.Message); }
        }
        if (tick % 10 == 0)
        {
            UpdateBroadcastTelemetry();
            var stats = engine.MusicStatistics();
            CaptureTelemetryText.Text = !engine.Running ? "捕获统计：未启动。" : $"音乐捕获：{stats.Sources} 个进程树 · 本次缺样补零 {stats.MissingMilliseconds:F1} ms · 丢弃 {stats.DroppedMilliseconds:F1} ms · 原生不连续 {stats.Discontinuities} 次。启动和暂停也可能影响计数，不能直接当作平台过滤。";
        }
        if (engine.Running && engine.Failure is { } failure)
        {
            busy = true; UpdateControls();
            try { await engine.StopAsync(); sendingMicrophone = sendingMusic = false; Report(failure); }
            finally { busy = false; UpdateControls(); }
        }
    }
    private void Report(Exception ex)
    {
        Log.Write(ex.ToString());
        StatusText.Text = "未就绪：" + ex.GetBaseException().Message;
    }
    private MusicPlayer SelectedMusicPlayer => MusicPlayerBox.SelectedItem as MusicPlayer ?? MusicPlayers.GetOrDefault(settings.MusicPlayerId);
    private void MusicPlayer_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (MusicPlayerBox.SelectedItem is not MusicPlayer player) return;
        settings.MusicPlayerId = player.Id;
        ProcessText.Text = $"自动识别{player.Name} · 等待播放器状态";
        tray?.Update();
        CaptureScenarioControls(); UpdateScenarioStatus();
    }
    private TrayState GetTrayState() => new(sendingMicrophone, sendingMusic, (int)MusicSlider.Value, !closing && MicrophoneButton.IsEnabled, engine.Running || BroadcastActive, busy || closing, SelectedMusicPlayer.Name, BroadcastActive);
    private void SaveSettings()
    {
        if (!persistSettingsOnClose) return;
        try
        {
            CaptureScenarioControls();
            settings.MicrophoneId = (MicrophoneBox.SelectedItem as DeviceChoice)?.Id;
            settings.CableId = (CableBox.SelectedItem as DeviceChoice)?.Id;
            settings.MicrophoneVolume = MicSlider.Value; settings.MusicVolume = MusicSlider.Value;
            settings.MusicPlayerId = SelectedMusicPlayer.Id;
            settings.Save();
        }
        catch (Exception ex) { Log.Write(ex.ToString()); }
    }
    public void HideToTray(bool showNotification = true)
    {
        if (!TrayIconVisible || closing) return;
        SaveSettings();
        if (WindowState != WindowState.Minimized) restoreState = WindowState;
        ShowInTaskbar = false;
        IsHiddenToTray = true;
        Hide();
        if (showNotification && !trayNoticeShown && showTrayNotifications) { trayNoticeShown = true; tray?.ExplainMinimize(); }
    }
    public void RestoreFromTray()
    {
        if (closing || canClose) return;
        IsHiddenToTray = false;
        ShowInTaskbar = true;
        Show();
        WindowState = restoreState == WindowState.Minimized ? WindowState.Normal : restoreState;
        Activate();
    }
    public void RequestExit()
    {
        if (closing || canClose) return;
        CancelLocalOperation();
        broadcastCancellation?.Cancel();
        exitRequested = true;
        if (busy) StatusText.Text = "正在完成当前操作，完成后退出。";
        QueueExitIfReady();
    }
    public void AllowSystemShutdown() { systemShutdown = true; CancelLocalOperation(); }
    private void QueueExitIfReady()
    {
        if (!exitRequested || busy || closing || canClose || exitQueued) return;
        exitQueued = true;
        _ = Dispatcher.BeginInvoke(new Action(() => { exitQueued = false; Close(); }));
    }
    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshDevices();
    private void UpdateStartupControl()
    {
        try
        {
            var state = startup.Read();
            StartupButton.IsEnabled = true;
            StartupButton.Content = state.Enabled ? "开机自启动：开" : "开机自启动：关";
            StartupButton.Background = new System.Windows.Media.SolidColorBrush(state.Enabled
                ? System.Windows.Media.Color.FromRgb(27, 119, 94) : System.Windows.Media.Color.FromRgb(48, 68, 92));
            StartupStatusText.Text = !state.Enabled ? "点击开启；请将 EXE 保留在固定位置。"
                : state.UsesCurrentExecutable ? "登录 Windows 后收起到托盘；人声、音乐保持关闭。"
                : "EXE 位置已改变。关闭后重新开启可更新启动位置。";
        }
        catch (Exception ex)
        {
            Log.Write(ex.ToString());
            StartupButton.Content = "开机自启动：不可用";
            StartupButton.IsEnabled = false;
            StartupStatusText.Text = "无法读取启动设置：" + ex.GetBaseException().Message;
        }
    }
    private void Startup_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            startup.SetEnabled(!startup.Read().Enabled);
            UpdateStartupControl();
        }
        catch (Exception ex)
        {
            UpdateStartupControl();
            Log.Write(ex.ToString());
            StartupStatusText.Text = "设置失败：" + ex.GetBaseException().Message;
        }
    }
    public void ShowForLaunch(bool startInTray)
    {
        if (startInTray)
        {
            // Create the native handle and tray icon without showing or activating the window.
            new WindowInteropHelper(this).EnsureHandle();
            if (TrayIconVisible) { HideToTray(showNotification: false); return; }
        }
        Show();
    }
    private void Repair_Click(object sender, RoutedEventArgs e)
    {
        if (busy || engine.Running) return;
        try
        {
            var result = AudioRouting.RepairCablePlaybackDefaults();
            RefreshDevices();
            StatusText.Text = result.ChangedRoles == 0 ? "默认播放路由正确，可以启动混音。" : $"已修复播放路由：{result.PlaybackName}。已保存备份，可以启动混音。";
        }
        catch (Exception ex) { Report(ex); }
    }
    private static void Open(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    private void SoundSettings_Click(object sender, RoutedEventArgs e) => Open("ms-settings:sound");
    private void StableVolume_Click(object sender, RoutedEventArgs e)
    {
        try { WindowsAudioPreferences.DisableCommunicationDucking(); StatusText.Text = "Windows 已设为通话时不降低其他声音，原设置已备份。语音软件里的自动增益和强降噪仍需分别关闭。"; }
        catch (Exception ex) { Report(ex); }
    }
    private void ShowText(string title, string text)
    {
        new Window
        {
            Owner = this, Title = title, Width = 720, Height = 600, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new System.Windows.Controls.TextBox { Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto, Padding = new Thickness(20), Foreground = System.Windows.Media.Brushes.Black }
        }.ShowDialog();
    }
    private void Licenses_Click(object sender, RoutedEventArgs e) => ShowText("第三方许可与原厂声明", BundledAssets.LicenseText());
    private void QualityHelp_Click(object sender, RoutedEventArgs e) => ShowText("各平台音乐音质设置", "所有平台都使用同一路虚拟麦克风：\n输入 / 麦克风选 CABLE Output，输出 / 扬声器选耳机。\n\n想让对方听到的音乐尽量接近本地，请在平台提供这些选项时：\n1. 开启原声 / 音乐 / 高音质模式；支持立体声时开启立体声。\n2. 关闭自动增益、音量归一化、强降噪、语音活动门限等会反复改变音乐的处理。\n3. 用本软件的音乐滑块调整固定音量；不要让系统或平台再自动调节。\n\nWindows 层可以点击“关闭通话自动压低音量”。\n本软件为两路混音留出固定音量余量，音乐不会因为你说话而自动变小；人声过载时仅对人声削峰。\n\n游戏、QQ、微信、YY、Discord、Steam 等版本提供的选项不同。有些平台强制使用人声编码和处理，无法通过虚拟麦克风强制变成无损音乐。需要在实际频道接收端试听确认；本软件不会伪造所有平台音质一致的保证。\n\n使用耳机可减少真实麦克风拾入音乐或队友声音。人声开关关闭时不会向频道发送麦克风音频；两路都关后释放音频采集。");
    private void Link_Click(object sender, RequestNavigateEventArgs e) { Open(e.Uri.AbsoluteUri); e.Handled = true; }
    private void Diagnostics_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { FileName = "VoiceBridge-diagnostics.json", Filter = "JSON|*.json" };
        if (dialog.ShowDialog(this) != true) return;
        try { CaptureScenarioControls(); Diagnostics.Write(dialog.FileName, SelectedMusicPlayer.Id, settings.ScenarioId, CurrentScenario, engine.MusicStatistics()); StatusText.Text = "设备诊断已导出；文件不包含录音。"; } catch (Exception ex) { Report(ex); }
    }
    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (busy || engine.Running) return;
        busy = true; UpdateControls();
        try
        {
            StatusText.Text = "正在打开 VB-Audio 官方安装器。按原厂提示安装并重启；安装后检查默认播放设备为耳机。";
            await CableInstaller.LaunchAsync();
            StatusText.Text = "官方安装器已退出。若已完成安装，请重启电脑，再打开本软件并刷新设备。";
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { StatusText.Text = "已取消管理员授权。未启动安装器。"; }
        catch (Exception ex) { Report(ex); }
        finally { busy = false; UpdateControls(); }
    }
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (canClose) return;
        if (systemShutdown) { canClose = true; timer.Stop(); SaveSettings(); return; }
        e.Cancel = true;
        if (!exitRequested && TrayIconVisible) { HideToTray(); return; }
        if (closing || busy) { StatusText.Text = "正在完成当前操作，请稍后关闭。"; return; }
        closing = true; busy = true; timer.Stop(); UpdateControls();
        try
        {
            SaveSettings();
        }
        catch (Exception ex) { Log.Write(ex.ToString()); }
        finally
        {
            CancelLocalOperation();
            if (localTask is { } pending) { try { await pending; } catch (Exception ex) { Log.Write(ex.ToString()); } }
            await StopBroadcastAsync();
            try { await engine.StopAsync(); }
            catch (Exception ex) { Log.Write(ex.ToString()); }
            canClose = true;
            // StopAsync can complete synchronously. Wait until WPF finishes this Closing event.
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(Close));
        }
    }
}
