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
  protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
  {if(Fail)throw new HttpRequestException("synthetic offline");return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("{\"token\":\""+new string('a',64)+"\",\"expires\":9999999999999,\"user\":{\"id\":\""+new string('b',32)+"\",\"nickname\":\"合成账号\",\"avatarUrl\":null,\"shortId\":\"bbbbbb\"}}",Encoding.UTF8,"application/json")});}
 }
 private static string FileName()=>Path.Combine(Path.GetTempPath(),"VoiceBridge-auth-tests-"+Guid.NewGuid().ToString("N"),"account.bin");
 [Fact]public async Task NativeSessionUsesUserDpapiAndCanBeRestoredWithoutSavingPassword()
 {var file=FileName();var handler=new Handler();using var http=new HttpClient(handler){BaseAddress=new Uri("https://voicebridge.example")};var client=new AccountClient(file,http);await client.SignInAsync("fixture@example.com","合成测试密码 不属于真实用户 password");byte[] bytes=File.ReadAllBytes(file);Assert.DoesNotContain(new string('a',64),Encoding.UTF8.GetString(bytes));Assert.DoesNotContain("password",Encoding.UTF8.GetString(bytes));using var nextHttp=new HttpClient(new Handler()){BaseAddress=http.BaseAddress};var restored=new AccountClient(file,nextHttp);Assert.Equal(client.Bearer,restored.Bearer);Assert.Equal("合成账号",restored.User?.Nickname);File.Delete(file);Directory.Delete(Path.GetDirectoryName(file)!);}
 [Fact]public void CorruptNativeCredentialDoesNotPreventStartup()
 {var file=FileName();Directory.CreateDirectory(Path.GetDirectoryName(file)!);File.WriteAllBytes(file,[0,1,2,3]);using var http=new HttpClient(new Handler()){BaseAddress=new Uri("https://voicebridge.example")};Assert.Null(new AccountClient(file,http).Session);File.Delete(file);Directory.Delete(Path.GetDirectoryName(file)!);}
 [Fact]public async Task ExplicitSignOutRemovesLocalCredentialEvenWhenServerIsOffline()
 {var file=FileName();var handler=new Handler();using var http=new HttpClient(handler){BaseAddress=new Uri("https://voicebridge.example")};var client=new AccountClient(file,http);await client.SignInAsync("fixture@example.com","合成密码不发往真实服务器");handler.Fail=true;await Assert.ThrowsAsync<HttpRequestException>(()=>client.LogoutAsync());Assert.Null(client.Session);using var nextHttp=new HttpClient(new Handler()){BaseAddress=http.BaseAddress};Assert.Null(new AccountClient(file,nextHttp).Session);File.Delete(file);Directory.Delete(Path.GetDirectoryName(file)!);}
 [Fact]public void CloseBehaviorIsRetainedAlongsideAudioPreferences()
 {var file=FileName()+".json";var store=new ProfileStore(file);Assert.True(store.Read().CloseToTray);Assert.True(store.Save(store.Read() with{CloseToTray=false,Nickname="我的昵称",MicrophoneGain=175}));var saved=new ProfileStore(file).Read();Assert.False(saved.CloseToTray);Assert.Equal("我的昵称",saved.Nickname);Assert.Equal(175,saved.MicrophoneGain);File.Delete(file);Directory.Delete(Path.GetDirectoryName(file)!);}
}
