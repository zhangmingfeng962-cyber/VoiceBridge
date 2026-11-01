using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;
using Velopack;
namespace VoiceBridge.App;
public static class Program
{
    private static Mutex? instance;
    private static readonly string PipeName="VoiceBridge-"+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(WindowsIdentity.GetCurrent().User?.Value??Environment.UserName)))[..16];
    [STAThread] public static void Main(string[] args)
    {
        VelopackApp.Build().SetAutoApplyOnStartup(false).OnFirstRun(_=>RegisterProtocol()).OnBeforeUninstallFastCallback(_=>UnregisterProtocol()).Run();
        bool diagnostic=args.Contains("--self-test")||args.Contains("--ui-fixture");
        if(!diagnostic){instance=new(false,"Local\\"+PipeName);bool owned;try{owned=instance.WaitOne(0);}catch(AbandonedMutexException){owned=true;}if(!owned){try{using var pipe=new NamedPipeClientStream(".",PipeName,PipeDirection.Out);pipe.Connect(1500);string text=args.FirstOrDefault(x=>x.StartsWith("voicebridge:",StringComparison.OrdinalIgnoreCase))??"show";pipe.Write(Encoding.UTF8.GetBytes(text));}catch(IOException){}catch(TimeoutException){}return;}}
        var app=new App();app.InitializeComponent();if(!diagnostic)_=ListenAsync(app);app.Run();instance?.ReleaseMutex();instance?.Dispose();
    }
    private static async Task ListenAsync(App app)
    {while(!app.Dispatcher.HasShutdownStarted){try{await using var pipe=new NamedPipeServerStream(PipeName,PipeDirection.In,1,PipeTransmissionMode.Message,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);await pipe.WaitForConnectionAsync();byte[] buffer=new byte[4096];int size=await pipe.ReadAsync(buffer);if(!pipe.IsMessageComplete)continue;string text=Encoding.UTF8.GetString(buffer,0,size);_ = app.Dispatcher.BeginInvoke(async()=>{if(app.MainWindow is MainWindow window){if(text=="show")window.RestoreWindow();else await window.ReceiveLinkAsync(text);}});}catch(IOException){}catch(ObjectDisposedException){break;}}}
    public static void RegisterProtocol(){using var key=Registry.CurrentUser.CreateSubKey("Software\\Classes\\voicebridge");key.SetValue("","URL:VoiceBridge");key.SetValue("URL Protocol","");using var command=key.CreateSubKey("shell\\open\\command");command.SetValue("","\""+Environment.ProcessPath+"\" \"%1\"");}
    private static void UnregisterProtocol(){using var command=Registry.CurrentUser.OpenSubKey("Software\\Classes\\voicebridge\\shell\\open\\command");if(command?.GetValue("") is string value&&value.Contains("VoiceBridge",StringComparison.OrdinalIgnoreCase))Registry.CurrentUser.DeleteSubKeyTree("Software\\Classes\\voicebridge",false);}
}
