using System.Windows;
namespace VoiceBridge.App;
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Contains("--self-test")) { _ = SelfTest.RunAsync(e.Args); return; }
        var window = new MainWindow(); MainWindow = window; ShutdownMode = ShutdownMode.OnMainWindowClose; window.Show();
    }
}
