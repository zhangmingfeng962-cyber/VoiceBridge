using System.Windows;
namespace VoiceBridge.App;
public partial class App : Application
{
    public bool SyntheticFixture { get; init; }
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Contains("--self-test")) { _ = SelfTest.RunAsync(e.Args); return; }
        bool fixture = SyntheticFixture || e.Args.Contains("--ui-fixture");
        var window = new MainWindow(fixture ? new VoiceBridge.Core.ProfileStore(System.IO.Path.Combine(System.IO.Path.GetTempPath(),"VoiceBridge-ui-fixture-preferences.json")) : null); MainWindow = window; ShutdownMode = ShutdownMode.OnMainWindowClose;
        if(fixture)window.Loaded += async (_,_) => { try { await window.EnterSyntheticFixtureAsync(); } catch(Exception ex) { System.IO.File.WriteAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(),"VoiceBridge-ui-fixture-error.txt"),ex.ToString()); } };
        else if(e.Args.FirstOrDefault(value=>value.StartsWith("voicebridge:",StringComparison.OrdinalIgnoreCase)) is {} link)window.Loaded += async (_,_) => await window.ReceiveLinkAsync(link);
        window.Show();
    }
}
