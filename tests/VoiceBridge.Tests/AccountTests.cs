using System.Net;
using System.Net.Http;
using System.IO;
using System.Text;
using VoiceBridge.App.Accounts;
using VoiceBridge.Core;
using Xunit;
namespace VoiceBridge.Tests;
public sealed class AccountTests
{
 private sealed class Handler:HttpMessageHandler
 {
  public bool Fail;
  public TaskCompletionSource<HttpResponseMessage>? Refresh,Logout;
  public TaskCompletionSource<bool> RefreshStarted=new(TaskCreationOptions.RunContinuationsAsynchronously);
  protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
  {if(Fail)throw new HttpRequestException("synthetic offline");if(request.RequestUri?.AbsolutePath=="/api/auth/refresh"&&Refresh is not null){RefreshStarted.TrySetResult(true);return Refresh.Task;}if(request.RequestUri?.AbsolutePath=="/api/auth/logout"&&Logout is not null)return Logout.Task;return Task.FromResult(Identity());}
  public static HttpResponseMessage Identity()=>new(HttpStatusCode.OK){Content=new StringContent("{\"token\":\""+new string('a',64)+"\",\"expires\":9999999999999,\"user\":{\"id\":\""+new string('b',32)+"\",\"nickname\":\"合成账号\",\"avatarUrl\":null,\"shortId\":\"bbbbbb\"}}",Encoding.UTF8,"application/json")};
 }
 private static string FileName()=>Path.Combine(Path.GetTempPath(),"VoiceBridge-auth-tests-"+Guid.NewGuid().ToString("N"),"account.bin");
 [Fact]public async Task NativeSessionUsesUserDpapiAndCanBeRestoredWithoutSavingPassword()
 {var file=FileName();var handler=new Handler();using var http=new HttpClient(handler){BaseAddress=new Uri("https://voicebridge.example")};var client=new AccountClient(file,http);await client.SignInAsync("fixture@example.com","合成测试密码 不属于真实用户 password");byte[] bytes=File.ReadAllBytes(file);Assert.DoesNotContain(new string('a',64),Encoding.UTF8.GetString(bytes));Assert.DoesNotContain("password",Encoding.UTF8.GetString(bytes));using var nextHttp=new HttpClient(new Handler()){BaseAddress=http.BaseAddress};var restored=new AccountClient(file,nextHttp);Assert.Equal(client.Bearer,restored.Bearer);Assert.Equal("合成账号",restored.User?.Nickname);File.Delete(file);Directory.Delete(Path.GetDirectoryName(file)!);}
 [Fact]public void CorruptNativeCredentialDoesNotPreventStartup()
 {var file=FileName();Directory.CreateDirectory(Path.GetDirectoryName(file)!);File.WriteAllBytes(file,[0,1,2,3]);using var http=new HttpClient(new Handler()){BaseAddress=new Uri("https://voicebridge.example")};Assert.Null(new AccountClient(file,http).Session);File.Delete(file);Directory.Delete(Path.GetDirectoryName(file)!);}
 [Fact]public async Task ExplicitSignOutRemovesLocalCredentialEvenWhenServerIsOffline()
 {var file=FileName();var handler=new Handler();using var http=new HttpClient(handler){BaseAddress=new Uri("https://voicebridge.example")};var client=new AccountClient(file,http);await client.SignInAsync("fixture@example.com","合成密码不发往真实服务器");handler.Fail=true;await client.LogoutAsync();Assert.Null(client.Session);Assert.False(File.Exists(file));using var nextHttp=new HttpClient(new Handler()){BaseAddress=http.BaseAddress};Assert.Null(new AccountClient(file,nextHttp).Session);Directory.Delete(Path.GetDirectoryName(file)!);}
 [Fact]public async Task LateRefreshCannotReviveAnExplicitlySignedOutSession()
 {var file=FileName();var handler=new Handler{Refresh=new(TaskCreationOptions.RunContinuationsAsynchronously),Logout=new(TaskCreationOptions.RunContinuationsAsynchronously)};using var http=new HttpClient(handler){BaseAddress=new Uri("https://voicebridge.example")};var client=new AccountClient(file,http);await client.SignInAsync("fixture@example.com","合成密码不发往真实服务器");var refresh=client.RestoreAsync();await handler.RefreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));var logout=client.LogoutAsync();Assert.Null(client.Session);Assert.False(File.Exists(file));handler.Refresh.SetResult(Handler.Identity());Assert.False(await refresh);Assert.Null(client.Session);handler.Logout.SetResult(new(HttpStatusCode.OK));await logout;Assert.False(File.Exists(file));Directory.Delete(Path.GetDirectoryName(file)!);}
 [Fact]public async Task MalformedRefreshResponsePreservesTheCacheWithoutCrashingStartup()
 {var file=FileName();var handler=new Handler{Refresh=new(TaskCreationOptions.RunContinuationsAsynchronously)};using var http=new HttpClient(handler){BaseAddress=new Uri("https://voicebridge.example")};var client=new AccountClient(file,http);await client.SignInAsync("fixture@example.com","合成密码不发往真实服务器");handler.Refresh.SetResult(new(HttpStatusCode.OK){Content=new StringContent("<html>unexpected upstream response</html>")});Assert.False(await client.RestoreAsync());Assert.NotNull(client.Session);File.Delete(file);Directory.Delete(Path.GetDirectoryName(file)!);}
 [Fact]public void CloseBehaviorIsRetainedAlongsideAudioPreferences()
 {var file=FileName()+".json";var store=new ProfileStore(file);Assert.True(store.Read().CloseToTray);Assert.True(store.Save(store.Read() with{CloseToTray=false,Nickname="我的昵称",MicrophoneGain=175}));var saved=new ProfileStore(file).Read();Assert.False(saved.CloseToTray);Assert.Equal("我的昵称",saved.Nickname);Assert.Equal(175,saved.MicrophoneGain);File.Delete(file);Directory.Delete(Path.GetDirectoryName(file)!);}
}
