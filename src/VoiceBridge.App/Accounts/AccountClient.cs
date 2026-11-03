using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VoiceBridge.App.Room;
using VoiceBridge.Core;
namespace VoiceBridge.App.Accounts;

public sealed record AccountProfile(string Id,string Nickname,string? AvatarUrl,string ShortId);
public sealed record AccountSession(string Token,long Expires,AccountProfile User);
public sealed class AccountClient
{
 public static AccountClient Current {get;}=new();
 private readonly HttpClient http;
 private readonly string file;
 private readonly SemaphoreSlim gate=new(1);
 public AccountSession? Session {get;private set;}
 public AccountProfile? User=>Session?.User;
 public string? Bearer=>Session?.Token;
 public string Origin=>http.BaseAddress!.GetLeftPart(UriPartial.Authority);
 public event Action? Changed;
 public AccountClient():this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"VoiceBridge","account.bin"),new HttpClient{BaseAddress=new(RoomService.DefaultOrigin),Timeout=TimeSpan.FromSeconds(25)}){}
 internal AccountClient(string storageFile,HttpClient client){file=storageFile;http=client;http.DefaultRequestHeaders.Add("X-VoiceBridge-Client","desktop");try{if(File.Exists(file)&&new FileInfo(file).Length<32768)Session=JsonSerializer.Deserialize<AccountSession>(UserSecret.Unprotect(File.ReadAllBytes(file)),RoomCipher.JsonOptions);}catch{Session=null;}}
 private void Save(){try{Directory.CreateDirectory(Path.GetDirectoryName(file)!);var temp=file+".tmp";File.WriteAllBytes(temp,UserSecret.Protect(JsonSerializer.SerializeToUtf8Bytes(Session,RoomCipher.JsonOptions)));File.Move(temp,file,true);}catch(IOException){}catch(UnauthorizedAccessException){}Changed?.Invoke();}
 public async Task<JsonElement> RequestAsync(string route,object? body=null,HttpMethod? method=null,bool authenticated=true)
 {
  using var request=new HttpRequestMessage(method??HttpMethod.Post,route);
  if(authenticated&&Bearer is {} token)request.Headers.Authorization=new("Bearer",token);
  if(method!=HttpMethod.Get)request.Content=JsonContent.Create(body??new{},options:RoomCipher.JsonOptions);
  using var response=await http.SendAsync(request);using var json=JsonDocument.Parse(await response.Content.ReadAsStringAsync());
  if(!response.IsSuccessStatusCode)throw new InvalidOperationException(json.RootElement.TryGetProperty("error",out var error)?error.GetString():"账号服务暂时不可用");
  return json.RootElement.Clone();
 }
 public async Task<bool> RestoreAsync()
 {
  if(Session is null)return false;await gate.WaitAsync();try{using var request=new HttpRequestMessage(HttpMethod.Post,"/api/auth/refresh"){Content=JsonContent.Create(new{})};request.Headers.Authorization=new("Bearer",Session.Token);using var response=await http.SendAsync(request);if(response.StatusCode==System.Net.HttpStatusCode.Unauthorized){Session=null;Save();return false;}response.EnsureSuccessStatusCode();Session=await response.Content.ReadFromJsonAsync<AccountSession>(RoomCipher.JsonOptions);Save();return Session is not null;}catch(HttpRequestException){return false;}catch(TaskCanceledException){return false;}finally{gate.Release();}
 }
 public async Task SignInAsync(string address,string password){var data=await RequestAsync("/api/auth/login",new{address,password},authenticated:false);Session=data.Deserialize<AccountSession>(RoomCipher.JsonOptions);Save();}
 public async Task<string> VerifyAsync(string address,string code,string purpose){var data=await RequestAsync("/api/auth/verify",new{address,code,purpose},authenticated:false);return data.GetProperty("verificationToken").GetString()!;}
 public async Task RegisterAsync(string verificationToken,string password,string nickname){var data=await RequestAsync("/api/auth/register",new{verificationToken,password,nickname},authenticated:false);Session=data.Deserialize<AccountSession>(RoomCipher.JsonOptions);Save();}
 public async Task UpdateProfileAsync(string nickname){var data=await RequestAsync("/api/me",new{nickname},HttpMethod.Patch);if(Session is {} current){Session=current with{User=data.GetProperty("user").Deserialize<AccountProfile>(RoomCipher.JsonOptions)!};Save();}}
 public async Task UploadAvatarAsync(byte[] png){var data=await RequestAsync("/api/me/avatar",new{png=Convert.ToBase64String(png)});if(Session is {} current){Session=current with{User=data.GetProperty("user").Deserialize<AccountProfile>(RoomCipher.JsonOptions)!};Save();}}
 public async Task LogoutAsync(){try{await RequestAsync("/api/auth/logout");}finally{Session=null;Save();}}
 public async Task<string> DesktopTicketAsync(RoomClient room){var result=await RequestAsync("/api/auth/desktop-ticket",new{roomId=room.Invite.RoomId,memberId=room.MemberId});return result.GetProperty("ticket").GetString()!;}
}
// Current-user Windows DPAPI; no plaintext password or reusable credential in preferences.json.
internal static class UserSecret
{
 [StructLayout(LayoutKind.Sequential)]private struct Blob{public int Length;public IntPtr Data;}
 [DllImport("crypt32.dll",SetLastError=true,CharSet=CharSet.Unicode)]private static extern bool CryptProtectData(ref Blob input,string? description,IntPtr entropy,IntPtr reserved,IntPtr prompt,int flags,out Blob output);
 [DllImport("crypt32.dll",SetLastError=true,CharSet=CharSet.Unicode)]private static extern bool CryptUnprotectData(ref Blob input,IntPtr description,IntPtr entropy,IntPtr reserved,IntPtr prompt,int flags,out Blob output);
 [DllImport("kernel32.dll")]private static extern IntPtr LocalFree(IntPtr pointer);
 public static byte[] Protect(byte[] bytes)=>Transform(bytes,true);
 public static byte[] Unprotect(byte[] bytes)=>Transform(bytes,false);
 private static byte[] Transform(byte[] bytes,bool protect){var input=new Blob{Length=bytes.Length,Data=Marshal.AllocHGlobal(bytes.Length)};try{Marshal.Copy(bytes,0,input.Data,bytes.Length);Blob output;bool ok=protect?CryptProtectData(ref input,"VoiceBridge session",IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,1,out output):CryptUnprotectData(ref input,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,1,out output);if(!ok)throw new CryptographicException(Marshal.GetLastWin32Error());try{var result=new byte[output.Length];Marshal.Copy(output.Data,result,0,result.Length);return result;}finally{LocalFree(output.Data);}}finally{Marshal.FreeHGlobal(input.Data);}}
}
