using System.Windows.Threading;
using System.Windows;
using VoiceBridge.App;
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
