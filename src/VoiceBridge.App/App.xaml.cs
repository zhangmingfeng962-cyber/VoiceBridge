using System.Windows;
namespace VoiceBridge.App;
public partial class App : Application
{
    public bool ExternalWindow { get; init; }
    public bool SyntheticFixture { get; init; }
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);if(ExternalWindow)return;
        if (e.Args.Contains("--self-test")) { _ = SelfTest.RunAsync(e.Args); return; }
        bool fixture = SyntheticFixture || e.Args.Contains("--ui-fixture");
        var window = new MainWindow(fixture ? new VoiceBridge.Core.ProfileStore(System.IO.Path.Combine(System.IO.Path.GetTempPath(),"VoiceBridge-ui-fixture-preferences.json")) : null); MainWindow = window; ShutdownMode = ShutdownMode.OnMainWindowClose;
        if(fixture)window.Loaded += async (_,_) => { try { await window.EnterSyntheticFixtureAsync(); } catch(Exception ex) { System.IO.File.WriteAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(),"VoiceBridge-ui-fixture-error.txt"),ex.ToString()); } };
        else if(e.Args.FirstOrDefault(value=>value.StartsWith("voicebridge:",StringComparison.OrdinalIgnoreCase)) is {} link)window.Loaded += async (_,_) => await window.ReceiveLinkAsync(link);
        window.Show();
    }
    private void ButtonPress(object sender,System.Windows.Input.MouseButtonEventArgs e){if(sender is not System.Windows.Controls.Button b||!SystemParameters.ClientAreaAnimation||b.Name is "MicButton" or "MusicButton" or "NoiseButton" or "SpeakerButton")return;if(b.RenderTransform is not System.Windows.Media.ScaleTransform)b.RenderTransform=new System.Windows.Media.ScaleTransform(1,1);b.RenderTransformOrigin=new Point(.5,.5);var scale=(System.Windows.Media.ScaleTransform)b.RenderTransform;scale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty,new System.Windows.Media.Animation.DoubleAnimation(.98,TimeSpan.FromMilliseconds(80)));scale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty,new System.Windows.Media.Animation.DoubleAnimation(.98,TimeSpan.FromMilliseconds(80)));}
    private void ButtonRelease(object sender,System.Windows.Input.MouseEventArgs e){if(sender is not System.Windows.Controls.Button b||b.RenderTransform is not System.Windows.Media.ScaleTransform scale)return;scale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty,new System.Windows.Media.Animation.DoubleAnimation(1,TimeSpan.FromMilliseconds(120)));scale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty,new System.Windows.Media.Animation.DoubleAnimation(1,TimeSpan.FromMilliseconds(120)));}
}
