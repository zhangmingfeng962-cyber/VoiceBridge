using System;using System.IO;using System.Net.Http;using System.Text.Json;using System.Windows;using VoiceBridge.App;using VoiceBridge.App.Accounts;using VoiceBridge.Core;
namespace VoiceBridge.UiFixture;
internal static class Program{
[STAThread]static void Main(){try{
 Velopack.VelopackApp.Build().SetAutoApplyOnStartup(false).Run();var app=new VoiceBridge.App.App{ExternalWindow=true};app.InitializeComponent();app.ShutdownMode=ShutdownMode.OnMainWindowClose;
 var temporary=Path.Combine(Path.GetTempPath(),"VoiceBridge-013-fixture-"+Guid.NewGuid().ToString("N"));
 using var http=new HttpClient{BaseAddress=new Uri("http://localhost:4102")};var account=new AccountClient(Path.Combine(temporary,"session.bin"),http);account.SignInAsync("player.one@example.com","VoiceBridge synthetic test 2026!").GetAwaiter().GetResult();
 var list=account.RequestAsync("/api/lobby/rooms",method:HttpMethod.Get).GetAwaiter().GetResult();string id=list.GetProperty("rooms")[0].GetProperty("id").GetString()!;var entry=account.RequestAsync("/api/lobby/rooms/"+id+"/join").GetAwaiter().GetResult();var invite=new RoomInvite("http://localhost:4102",id,entry.GetProperty("token").GetString()!,Convert.FromHexString(entry.GetProperty("key").GetString()!),true);
 var window=new MainWindow(new ProfileStore(Path.Combine(temporary,"preferences.json")),account);app.MainWindow=window;window.Loaded+=async(_,_)=>{try{await window.EnterAccountFixtureAsync(invite);}catch(Exception ex){File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"ui-startup-error.txt"),ex.ToString());}};app.DispatcherUnhandledException+=(_,e)=>{File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"ui-startup-error.txt"),e.Exception.ToString());e.Handled=true;app.Shutdown(1);};app.Run(window);
}catch(Exception ex){File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"ui-startup-error.txt"),ex.ToString());}}
}
