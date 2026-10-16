using System.Windows.Threading;
using System.Windows;
using VoiceBridge.App;
using VoiceBridge.App.Platform;
using Xunit;

namespace VoiceBridge.Tests;

[Collection("WpfWindow")]
public sealed class WindowTests
{
    private static MainWindow CreateTrayWindow() => new(persistSettingsOnClose: false, enableTray: true, showTrayNotifications: false)
    {
        WindowStartupLocation = WindowStartupLocation.Manual,
        Left = -32000, Top = -32000, ShowActivated = false, ShowInTaskbar = false
    };

    private static async Task OnSta(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { action(); completion.TrySetResult(); }
            catch (Exception ex) { completion.TrySetException(ex); }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static void ExitAndPump(MainWindow window)
    {
        var frame = new DispatcherFrame();
        bool closed = false;
        window.Closed += (_, _) => { closed = true; frame.Continue = false; };
        window.RequestExit();
        if (!closed) Dispatcher.PushFrame(frame);
        Assert.True(closed);
        Assert.False(window.TrayIconVisible);
    }

    [Fact]
    public async Task IndependentShareRequiresExplicitStartAndDoesNotEnableRecordingOnLaunch() => await OnSta(() =>
    {
        var window = new MainWindow(false, enableTray: false, showTrayNotifications: false, initialSettings: new Settings { MusicPlayerId = "netease" });
        try
        {
            var selector = (System.Windows.Controls.ComboBox)window.FindName("BroadcastPlayerBox");
            Assert.Equal("netease", ((MusicPlayer)selector.SelectedItem).Id);
            Assert.Equal(3, selector.Items.Count);
            Assert.False(((System.Windows.Controls.Button)window.FindName("BroadcastStopButton")).IsEnabled);
            Assert.False(((System.Windows.Controls.Button)window.FindName("BroadcastCopyButton")).IsEnabled);
            Assert.Equal("尚未创建链接", ((System.Windows.Controls.TextBox)window.FindName("BroadcastLinkBox")).Text);
            Assert.Equal("人声：关", ((System.Windows.Controls.Button)window.FindName("MicrophoneButton")).Content);
            Assert.Equal("音乐：关", ((System.Windows.Controls.Button)window.FindName("ShareButton")).Content);
        }
        finally { ExitAndPump(window); }
    });

    [Fact]
    public async Task ScenarioSwitchRestoresIndependentControlsWithoutStartingCapture() => await OnSta(() =>
    {
        var settings = new Settings();
        var window = new MainWindow(false, enableTray: false, showTrayNotifications: false, initialSettings: settings);
        try
        {
            var scenes = (System.Windows.Controls.ComboBox)window.FindName("ScenarioBox");
            var player = (System.Windows.Controls.ComboBox)window.FindName("MusicPlayerBox");
            var music = (System.Windows.Controls.Slider)window.FindName("MusicSlider");
            var buffer = (System.Windows.Controls.ComboBox)window.FindName("BufferBox");
            scenes.SelectedItem = Scenarios.Get("qq"); music.Value = 75; player.SelectedItem = MusicPlayers.KuGou;
            buffer.SelectedItem = Scenarios.Buffers.First(x => x.Milliseconds == 40);
            ((System.Windows.Controls.Button)window.FindName("ConfirmPeerButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            Assert.True(settings.GetScenario("qq").HasMatchingPeerConfirmation);
            scenes.SelectedItem = Scenarios.Get("wechat"); music.Value = 25; player.SelectedItem = MusicPlayers.NetEase;
            buffer.SelectedItem = Scenarios.Buffers.First(x => x.Milliseconds == 60);
            scenes.SelectedItem = Scenarios.Get("qq");
            Assert.Equal(75, music.Value); Assert.Equal("kugou", ((MusicPlayer)player.SelectedItem).Id);
            Assert.Equal(40, ((BufferChoice)buffer.SelectedItem).Milliseconds);
            Assert.Equal(25, settings.GetScenario("wechat").MusicVolume);
            Assert.True(settings.GetScenario("qq").HasMatchingPeerConfirmation);
            music.Value = 70; Assert.False(settings.GetScenario("qq").HasMatchingPeerConfirmation);
            Assert.False(((System.Windows.Controls.Button)window.FindName("LocalRecordButton")).IsEnabled);
            Assert.Equal("音乐：关", ((System.Windows.Controls.Button)window.FindName("ShareButton")).Content);
        }
        finally { ExitAndPump(window); }
    });

    [Fact]
    public async Task PlayerSelectorRestoresSelectionAndSwitchesLabelsWithoutStartingAudio() => await OnSta(() =>
    {
        var settings = new Settings { MusicPlayerId = "kugou" };
        var window = new MainWindow(persistSettingsOnClose: false, enableTray: false,
            showTrayNotifications: false, initialSettings: settings);
        try
        {
            var selector = (System.Windows.Controls.ComboBox)window.FindName("MusicPlayerBox");
            var status = (System.Windows.Controls.TextBlock)window.FindName("ProcessText");
            Assert.Equal(new[] { "QQ音乐", "网易云音乐", "酷狗音乐" }, selector.Items.Cast<MusicPlayer>().Select(p => p.Name));
            Assert.Equal("kugou", ((MusicPlayer)selector.SelectedItem).Id);
            foreach (var player in MusicPlayers.All)
            {
                selector.SelectedItem = player;
                Assert.Equal(player.Id, settings.MusicPlayerId);
                Assert.Contains(player.Name, status.Text);
                Assert.Equal("人声：关", ((System.Windows.Controls.Button)window.FindName("MicrophoneButton")).Content);
                Assert.Equal("音乐：关", ((System.Windows.Controls.Button)window.FindName("ShareButton")).Content);
            }
        }
        finally { ExitAndPump(window); }
    });

    [Fact]
    public async Task StartupLaunchCreatesTrayWithoutShowingWindowAndCanRestore() => await OnSta(() =>
    {
        var window = CreateTrayWindow();
        bool everVisible = false;
        window.IsVisibleChanged += (_, _) => everVisible |= window.IsVisible;
        window.ShowForLaunch(startInTray: true);
        Assert.True(window.TrayIconVisible);
        Assert.True(window.IsHiddenToTray);
        Assert.False(window.ShowInTaskbar);
        Assert.False(everVisible);
        Assert.Equal("人声：关", ((System.Windows.Controls.Button)window.FindName("MicrophoneButton")).Content);
        Assert.Equal("音乐：关", ((System.Windows.Controls.Button)window.FindName("ShareButton")).Content);
        window.RestoreFromTray();
        Assert.True(window.IsVisible);
        ExitAndPump(window);
    });

    [Fact]
    public async Task StartupFallsBackToVisibleWindowWhenTrayIsUnavailable() => await OnSta(() =>
    {
        var window = new MainWindow(persistSettingsOnClose: false, enableTray: false, showTrayNotifications: false);
        window.ShowForLaunch(startInTray: true);
        Assert.True(window.IsVisible);
        Assert.False(window.IsHiddenToTray);
        ExitAndPump(window);
    });

    [Fact]
    public async Task StartupButtonReflectsPersistedSettingAndCanTurnItOff() => await OnSta(() =>
    {
        string keyPath = @"Software\VoiceBridge.Tests\StartupUi-" + Guid.NewGuid().ToString("N");
        var registration = new StartupRegistration(keyPath, () => @"C:\Example Tools\VoiceBridge.exe");
        var window = new MainWindow(persistSettingsOnClose: false, enableTray: false,
            showTrayNotifications: false, startupRegistration: registration);
        try
        {
            var button = (System.Windows.Controls.Button)window.FindName("StartupButton");
            Assert.Equal("开机自启动：关", button.Content);
            button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            Assert.True(registration.Read().Enabled);
            Assert.Equal("开机自启动：开", button.Content);
            button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            Assert.False(registration.Read().Enabled);
            Assert.Equal("开机自启动：关", button.Content);
        }
        finally
        {
            ExitAndPump(window);
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
        }
    });

    [Fact]
    public async Task CloseHidesToTrayAndRestoreKeepsTheWindowAlive() => await OnSta(() =>
    {
        var window = CreateTrayWindow();
        window.Show();
        Assert.True(window.TrayIconVisible);
        bool closed = false;
        window.Closed += (_, _) => closed = true;
        window.Close();
        Assert.True(window.IsHiddenToTray);
        Assert.False(window.IsVisible);
        Assert.False(window.ShowInTaskbar);
        Assert.False(closed);
        window.RestoreFromTray();
        Assert.True(window.IsVisible);
        Assert.False(window.IsHiddenToTray);
        Assert.True(window.ShowInTaskbar);
        ExitAndPump(window);
    });

    [Fact]
    public async Task MinimizeAlsoHidesToTrayAndCanRestoreNormally() => await OnSta(() =>
    {
        var window = CreateTrayWindow();
        window.Show();
        window.WindowState = WindowState.Minimized;
        Assert.True(window.IsHiddenToTray);
        Assert.False(window.IsVisible);
        window.RestoreFromTray();
        Assert.True(window.IsVisible);
        Assert.Equal(WindowState.Normal, window.WindowState);
        ExitAndPump(window);
    });

    [Fact]
    public async Task SystemShutdownClosesInsteadOfHidingToTray() => await OnSta(() =>
    {
        var window = CreateTrayWindow();
        window.Show();
        bool closed = false;
        window.Closed += (_, _) => closed = true;
        window.AllowSystemShutdown();
        window.Close();
        Assert.True(closed);
        Assert.False(window.TrayIconVisible);
    });

    [Fact]
    public async Task CloseAfterSynchronousStopDoesNotReenterClosing()
    {
        var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var window = new MainWindow(persistSettingsOnClose: false, enableTray: false);
                var frame = new DispatcherFrame();
                int count = 0;
                window.Closing += (_, _) => count++;
                window.Closed += (_, _) => frame.Continue = false;
                window.Close();
                Dispatcher.PushFrame(frame);
                Assert.Equal(2, count);
                result.TrySetResult();
            }
            catch (Exception ex) { result.TrySetException(ex); }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await result.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
}

[CollectionDefinition("WpfWindow", DisableParallelization = true)]
public sealed class WpfWindowCollection { }
