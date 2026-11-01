using System;
namespace VoiceBridge.UiFixture;
internal static class Program
{
    [STAThread] static void Main() { try { Velopack.VelopackApp.Build().SetAutoApplyOnStartup(false).Run(); var app = new VoiceBridge.App.App { SyntheticFixture = true }; app.DispatcherUnhandledException += (_,e) => { System.IO.File.WriteAllText(System.IO.Path.Combine(AppContext.BaseDirectory,"ui-startup-error.txt"),e.Exception.ToString());e.Handled=true;app.Shutdown(1); }; app.InitializeComponent(); app.Run(); } catch(Exception ex) { System.IO.File.WriteAllText(System.IO.Path.Combine(AppContext.BaseDirectory,"ui-startup-error.txt"),ex.ToString()); } }
}
