using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using VoiceBridge.App.Audio;
using VoiceBridge.App.Platform;

namespace VoiceBridge.App;

public partial class MainWindow
{
    private bool applyingScenario, localOperation;
    private string? localOperationKind, localClipPath, localClipScenario, localClipSignature;
    private CancellationTokenSource? localCancellation;
    private Task? localTask;
    private int routeReadInProgress, routeRefreshRequested;
    private ScenarioAudioProfile CurrentScenario => settings.GetScenario(settings.ScenarioId);

    private void InitializeScenarios()
    {
        settings.Normalize();
        applyingScenario = true;
        try
        {
            ScenarioBox.ItemsSource = Scenarios.All;
            ChannelModeBox.ItemsSource = Scenarios.Channels;
            BufferBox.ItemsSource = Scenarios.Buffers;
            MusicPlayerBox.ItemsSource = MusicPlayers.All;
            ScenarioBox.SelectedItem = Scenarios.Get(settings.ScenarioId);
            ApplyScenarioValues(CurrentScenario);
        }
        finally { applyingScenario = false; }
        UpdateScenarioStatus();
    }

    private void ApplyScenarioValues(ScenarioAudioProfile profile)
    {
        settings.ApplyScenario(profile);
        MicSlider.Value = profile.MicrophoneVolume; MusicSlider.Value = profile.MusicVolume;
        MusicPlayerBox.SelectedItem = MusicPlayers.GetOrDefault(profile.MusicPlayerId);
        ChannelModeBox.SelectedItem = Scenarios.Channels.First(x => x.Mode == profile.ChannelMode);
        BufferBox.SelectedItem = Scenarios.Buffers.First(x => x.Milliseconds == profile.OutputBufferMs);
    }

    private void CaptureScenarioControls()
    {
        if (applyingScenario || !IsInitialized || ScenarioBox.SelectedItem is null) return;
        var profile = CurrentScenario;
        profile.MicrophoneId = (MicrophoneBox.SelectedItem as DeviceChoice)?.Id;
        profile.CableId = (CableBox.SelectedItem as DeviceChoice)?.Id;
        profile.MicrophoneVolume = MicSlider.Value; profile.MusicVolume = MusicSlider.Value;
        profile.MusicPlayerId = SelectedMusicPlayer.Id;
        profile.ChannelMode = (ChannelModeBox.SelectedItem as ChannelChoice)?.Mode ?? VoiceBridge.Core.MusicChannelMode.Stereo;
        profile.OutputBufferMs = (BufferBox.SelectedItem as BufferChoice)?.Milliseconds ?? 20;
        settings.ApplyScenario(profile);
    }

    private void UpdateScenarioStatus()
    {
        if (PeerStatusText is null) return;
        var profile = CurrentScenario;
        PeerStatusText.Text = profile.HasMatchingPeerConfirmation
            ? $"用户记录：{profile.PeerConfirmedAt!.Value.ToLocalTime():g} 对方试听正常；更换平台版本或接收设备后请重测。"
            : profile.PeerConfirmedAt.HasValue ? "配置已改变，之前的试听记录不适用于当前参数。" : "尚未记录对方试听结果；场景名称不代表已关闭平台过滤。";
        LocalRecordButton.IsEnabled = !closing && (!localOperation && engine.Running && !busy || localOperationKind == "record");
        LocalRecordButton.Content = localOperationKind == "record" ? "取消录制" : "录制虚拟输出 10 秒";
        LocalPlayButton.IsEnabled = !closing && (localOperationKind == "play" || !localOperation && !busy && localClipPath is not null
            && localClipScenario == settings.ScenarioId && localClipSignature == profile.Signature() && File.Exists(localClipPath));
        LocalPlayButton.Content = localOperationKind == "play" ? "停止试听" : "试听本地录音";
        ConfirmPeerButton.IsEnabled = SaveScenarioButton.IsEnabled = DefaultInputButton.IsEnabled = RestoreInputButton.IsEnabled = !closing && !busy && !localOperation;
    }

    private void Scenario_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (applyingScenario || !IsInitialized || ScenarioBox.SelectedItem is not ScenarioDefinition next) return;
        CaptureScenarioControls();
        settings.ScenarioId = next.Id;
        applyingScenario = true;
        try
        {
            ApplyScenarioValues(CurrentScenario);
            MicrophoneBox.SelectedItem = null; CableBox.SelectedItem = null;
            RefreshDevices();
            ProcessText.Text = $"自动识别{SelectedMusicPlayer.Name} · 等待播放器状态";
        }
        finally { applyingScenario = false; }
        CaptureScenarioControls(); UpdateVolumes(); UpdateControls();
        if (persistSettingsOnClose) { try { settings.Save(); } catch (Exception ex) { Report(ex); } }
    }

    private void ScenarioOption_Changed(object sender, SelectionChangedEventArgs e)
    { if (!applyingScenario && IsInitialized) { CaptureScenarioControls(); UpdateScenarioStatus(); } }

    private void SaveScenario_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            CaptureScenarioControls();
            if (persistSettingsOnClose) settings.Save();
            StatusText.Text = $"已保存“{Scenarios.Get(settings.ScenarioId).Name}”的本机参数；平台内部设置按场景说明检查。";
            UpdateScenarioStatus();
        }
        catch (Exception ex) { Report(ex); }
    }

    private void ScenarioHelp_Click(object sender, RoutedEventArgs e) => ShowText(Scenarios.Get(settings.ScenarioId).Name + " · 设置说明", Scenarios.Get(settings.ScenarioId).Guide);

    private void ReadDefaultInputStatus()
    {
        Interlocked.Exchange(ref routeRefreshRequested, 1);
        if (Interlocked.CompareExchange(ref routeReadInProgress, 1, 0) != 0) return;
        _ = ReadDefaultInputStatusAsync();
    }

    private async Task ReadDefaultInputStatusAsync()
    {
        try
        {
            while (Interlocked.Exchange(ref routeRefreshRequested, 0) != 0 && !Dispatcher.HasShutdownStarted)
            {
                string summary;
                try
                {
                    int virtualRoles = await Task.Run(() => AudioRouting.Snapshot().Count(x => x.Flow == "Capture" && x.Name.StartsWith("CABLE Output", StringComparison.OrdinalIgnoreCase))).ConfigureAwait(false);
                    summary = virtualRoles == 3 ? "Windows 默认输入：三种角色均指向 CABLE Output。通话需保持 VoiceBridge 运行并开启人声；目标软件若自选设备，仍需在其设置中确认。"
                        : $"Windows 默认输入：{virtualRoles}/3 种角色指向 CABLE Output。不同软件可能使用不同默认输入，请确认实际来源。";
                }
                catch (Exception ex) { summary = "读取默认输入失败：" + ex.GetBaseException().Message; }
                if (Dispatcher.HasShutdownStarted) return;
                await Dispatcher.InvokeAsync(() => { if (!closing && !canClose) InputRoutingText.Text = summary; });
            }
        }
        catch (TaskCanceledException) { }
        catch (InvalidOperationException) when (Dispatcher.HasShutdownStarted) { }
        finally
        {
            Interlocked.Exchange(ref routeReadInProgress, 0);
            if (Volatile.Read(ref routeRefreshRequested) != 0 && !Dispatcher.HasShutdownStarted) ReadDefaultInputStatus();
        }
    }

    private void DefaultInput_Click(object sender, RoutedEventArgs e) => ChangeDefaultInput(restore: false);
    private void RestoreInput_Click(object sender, RoutedEventArgs e) => ChangeDefaultInput(restore: true);
    private void ChangeDefaultInput(bool restore)
    {
        try
        {
            CaptureScenarioControls();
            var result = restore ? AudioRouting.RestoreDefaultInput() : AudioRouting.UseVirtualDefaultInput();
            if (result.ChangedRoles > 0) settings.InvalidatePeerConfirmations();
            StatusText.Text = result.ChangedRoles == 0 ? "默认输入无需更改，或已被其他操作修改；请检查目标软件中的实际输入。"
                : $"已{(restore ? "恢复此前默认输入" : "将虚拟麦克风设为 Windows 默认输入")}，共 {result.ChangedRoles} 种角色。当前通话可能需要重新选择输入或重新进入。使用虚拟输入时需保持软件运行并开启人声。";
            ReadDefaultInputStatus(); UpdateScenarioStatus();
            if (persistSettingsOnClose) settings.Save();
        }
        catch (Exception ex) { Report(ex); }
    }

    private void ConfirmPeer_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            CaptureScenarioControls(); CurrentScenario.ConfirmPeerFeedback(DateTimeOffset.Now);
            if (persistSettingsOnClose) settings.Save();
            UpdateScenarioStatus();
        }
        catch (Exception ex) { Report(ex); }
    }

    private async void LocalRecord_Click(object sender, RoutedEventArgs e)
    {
        if (localOperationKind == "record") { CancelLocalOperation(); return; }
        if (localOperation || busy || closing || !engine.Running) return;
        var dialog = new SaveFileDialog { Title = "录制当前虚拟输出（包含已开启的人声和音乐）", Filter = "WAV 音频|*.wav", FileName = $"VoiceBridge-{settings.ScenarioId}-{DateTime.Now:yyyyMMdd-HHmmss}.wav" };
        if (dialog.ShowDialog(this) != true) return;
        CaptureScenarioControls();
        localTask = RecordLocalAsync(dialog.FileName, settings.ScenarioId, CurrentScenario.Signature());
        await localTask;
    }

    private async Task RecordLocalAsync(string path, string scenarioId, string signature)
    {
        localOperation = true; localOperationKind = "record"; localCancellation = new();
        UpdateControls(); LocalCheckText.Text = "正在录制虚拟麦克风，最多 10 秒；再次点击可取消。";
        try
        {
            var result = await LocalOutputCheck.RecordAsync(path, TimeSpan.FromSeconds(10), localCancellation.Token);
            localClipPath = result.FilePath; localClipScenario = scenarioId; localClipSignature = signature;
            var stats = result.Statistics;
            LocalCheckText.Text = $"{(result.Cancelled ? "已取消，保留" : "已录制")} {stats.DurationSeconds:F1} 秒 · 峰值 {stats.Peak * 100:F1}% · 平均电平 {stats.RmsDbFs:F1} dBFS · 近静音窗口 {stats.NearSilentWindowPercent:F1}%（安静段也计入）。\n文件：{result.FilePath}\n此结果只覆盖本地虚拟输出，不能证明目标平台没有过滤音乐。";
            await File.WriteAllTextAsync(path + ".json", JsonSerializer.Serialize(new
            { Scenario = Scenarios.Get(scenarioId).Name, ProfileSignature = signature, LocalOutput = stats, Capture = engine.MusicStatistics(), Scope = "Local virtual microphone only; no receiver verification" }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (OperationCanceledException) { LocalCheckText.Text = "本地检查已取消。"; }
        catch (Exception ex) { Log.Write(ex.ToString()); LocalCheckText.Text = "本地检查失败：" + ex.GetBaseException().Message; }
        finally { localCancellation?.Dispose(); localCancellation = null; localOperation = false; localOperationKind = null; UpdateControls(); }
    }

    private async void LocalPlay_Click(object sender, RoutedEventArgs e)
    {
        if (localOperationKind == "play") { CancelLocalOperation(); return; }
        if (localOperation || localClipPath is null || closing) return;
        localTask = PlayLocalAsync(localClipPath); await localTask;
    }

    private async Task PlayLocalAsync(string path)
    {
        localOperation = true; localOperationKind = "play"; localCancellation = new(); UpdateControls();
        try { await LocalOutputCheck.PlayLocallyAsync(path, localCancellation.Token); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Write(ex.ToString()); LocalCheckText.Text = "试听失败：" + ex.GetBaseException().Message; }
        finally { localCancellation?.Dispose(); localCancellation = null; localOperation = false; localOperationKind = null; UpdateControls(); }
    }

    private void CancelLocalOperation() => localCancellation?.Cancel();
}
