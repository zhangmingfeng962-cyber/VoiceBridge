using System.Windows;
using VoiceBridge.App.Platform;

namespace VoiceBridge.App;
public partial class App : Application
{
    private Mutex? instance;
    private InstanceSignals? signals;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Contains("--probe-tray"))
        {
            var path = e.Args.SkipWhile(x => x != "--probe-tray").Skip(1).FirstOrDefault();
            if (path is null) { Shutdown(2); return; }
            _ = ProbeTrayAsync(path);
            return;
        }
        if (e.Args.Contains("--exit")) { Shutdown(InstanceSignals.ExitExisting() ? 0 : 2); return; }
        if (e.Args.Contains("--probe-capture-api"))
        {
            var path = e.Args.SkipWhile(x => x != "--probe-capture-api").Skip(1).FirstOrDefault();
            if (path is null) { Shutdown(2); return; }
            _ = ProbeCaptureApiAsync(path);
            return;
        }
        if (e.Args.Contains("--verify-bundle"))
        {
            var path = e.Args.SkipWhile(x => x != "--verify-bundle").Skip(1).FirstOrDefault();
            if (path is null) { Shutdown(2); return; }
            _ = VerifyBundleAsync(path);
            return;
        }
        if (e.Args.Contains("--stabilize-windows"))
        {
            var path = e.Args.SkipWhile(x => x != "--stabilize-windows").Skip(1).FirstOrDefault();
            if (path is null) { Shutdown(2); return; }
            try
            {
                var backup = WindowsAudioPreferences.DisableCommunicationDucking();
                System.IO.File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new { BackupPath = backup, UserDuckingPreference = WindowsAudioPreferences.DuckingPreference }));
                Shutdown(0);
            }
            catch (Exception ex) { Log.Write(ex.ToString()); Shutdown(1); }
            return;
        }
        if (e.Args.Contains("--repair-playback"))
        {
            var path = e.Args.SkipWhile(x => x != "--repair-playback").Skip(1).FirstOrDefault();
            if (path is null) { Shutdown(2); return; }
            try
            {
                var result = AudioRouting.RepairCablePlaybackDefaults();
                System.IO.File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(result, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                Shutdown(0);
            }
            catch (Exception ex) { Log.Write(ex.ToString()); Shutdown(1); }
            return;
        }
        if (e.Args.Contains("--probe-engine"))
        {
            var path = e.Args.SkipWhile(x => x != "--probe-engine").Skip(1).FirstOrDefault();
            if (path is null) { Shutdown(2); return; }
            _ = RunProbeAsync(path);
            return;
        }
        if (e.Args.Contains("--render-preview"))
        {
            var path = e.Args.SkipWhile(x => x != "--render-preview").Skip(1).FirstOrDefault();
            if (path is null) { Shutdown(2); return; }
            try { UiPreview.Write(path); Shutdown(0); } catch (Exception ex) { Log.Write(ex.ToString()); Shutdown(1); }
            return;
        }
        if (e.Args.Contains("--diagnose"))
        {
            var path = e.Args.SkipWhile(x => x != "--diagnose").Skip(1).FirstOrDefault();
            if (path is null) { Shutdown(2); return; }
            try { Diagnostics.Write(path); Shutdown(0); } catch (Exception ex) { Log.Write(ex.ToString()); Shutdown(1); }
            return;
        }
        instance = new Mutex(true, "Local\\VoiceBridge.Desktop", out bool first);
        if (!first)
        {
            if (!InstanceSignals.ShowExisting()) MessageBox.Show("VoiceBridge 正在启动，请稍后再次打开。", "VoiceBridge");
            Shutdown(); return;
        }
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Write(args.Exception.ToString());
            MessageBox.Show(args.Exception.Message + "\n详情见本地日志。", "VoiceBridge");
            args.Handled = true;
        };
        var window = new MainWindow();
        MainWindow = window;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        signals = new InstanceSignals(
            () => { if (!Dispatcher.HasShutdownStarted) _ = Dispatcher.BeginInvoke(new Action(window.RestoreFromTray)); },
            () => { if (!Dispatcher.HasShutdownStarted) _ = Dispatcher.BeginInvoke(new Action(window.RequestExit)); });
        SessionEnding += (_, _) => window.AllowSystemShutdown();
        window.Show();
    }
    private async Task RunProbeAsync(string path)
    {
        try { await EngineProbe.WriteAsync(path); Shutdown(0); }
        catch (Exception ex) { Log.Write(ex.ToString()); Shutdown(1); }
    }
    private async Task ProbeTrayAsync(string path)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        MainWindow? window = null;
        try
        {
            window = new MainWindow(false, enableTray: true, showTrayNotifications: false)
            {
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000,
                ShowActivated = false, ShowInTaskbar = false
            };
            window.Show();
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            bool trayCreated = window.TrayIconVisible;
            window.Close();
            bool closeHidWindow = window.IsHiddenToTray && !window.IsVisible && !window.ShowInTaskbar;
            window.RestoreFromTray();
            bool restored = window.IsVisible && !window.IsHiddenToTray && window.WindowState == WindowState.Normal;
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            window.Closed += (_, _) => closed.TrySetResult();
            window.RequestExit();
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            bool trayRemoved = !window.TrayIconVisible;
            if (!trayCreated || !closeHidWindow || !restored || !trayRemoved) throw new InvalidOperationException("Tray lifecycle probe failed.");
            System.IO.File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new
            {
                Version = BuildInfo.Version, TrayCreated = trayCreated, CloseHidWindow = closeHidWindow,
                RestoreSucceeded = restored, ExplicitExitClosedWindow = closed.Task.IsCompleted,
                TrayRemovedOnExit = trayRemoved, AudioStarted = false
            }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            Shutdown(0);
        }
        catch (Exception ex)
        {
            Log.Write(ex.ToString());
            if (window is not null) { window.AllowSystemShutdown(); window.Close(); }
            Shutdown(1);
        }
    }
    private async Task VerifyBundleAsync(string path)
    {
        try
        {
            string hash = await BundledAssets.VerifyDriverAsync();
            System.IO.File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new { Version = BuildInfo.Version, DriverSHA256 = hash, Assets = BundledAssets.Names, LicenseCharacters = BundledAssets.LicenseText().Length }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            Shutdown(0);
        }
        catch (Exception ex) { Log.Write(ex.ToString()); Shutdown(1); }
    }
    private async Task ProbeCaptureApiAsync(string path)
    {
        try
        {
            using var capture = new Audio.ProcessLoopback((uint)Environment.ProcessId);
            await capture.StartAsync();
            await Task.Delay(350);
            if (capture.Failure is not null) throw capture.Failure;
            System.IO.File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new { Version = BuildInfo.Version, NativeProcessCaptureAvailable = true, Target = "This silent diagnostic process only", MicrophoneAccessed = false, AudioSentToVirtualDevice = false }));
            Shutdown(0);
        }
        catch (Exception ex) { Log.Write(ex.ToString()); Shutdown(1); }
    }
    protected override void OnExit(ExitEventArgs e) { signals?.Dispose(); instance?.Dispose(); base.OnExit(e); }
}
