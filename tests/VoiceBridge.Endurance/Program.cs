using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;
using VoiceBridge.App.Audio;
using VoiceBridge.App.Room;
using VoiceBridge.Core;
int seconds = args.Length > 0 ? int.Parse(args[0]) : 7200;
string report = args.Length > 1 ? Path.GetFullPath(args[1]) : Path.GetFullPath("endurance-report.json");
using var http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:4094"), Timeout = TimeSpan.FromSeconds(10) };
http.DefaultRequestHeaders.Authorization = new("Bearer", new string('1',64));
using var created = await http.PostAsync("/api/rooms", new StringContent("{}")); created.EnsureSuccessStatusCode();using var json=JsonDocument.Parse(await created.Content.ReadAsStringAsync());
var root=json.RootElement;string id=root.GetProperty("id").GetString()!,token=root.GetProperty("listenToken").GetString()!;
var invite=new RoomInvite("http://127.0.0.1:4094",id,token,RandomNumberGenerator.GetBytes(32));
var engines=new List<SyntheticRoomAudio>();var clients=new List<RoomClient>();
string temporary=Path.Combine(Path.GetTempPath(),"VoiceBridge-endurance-"+Guid.NewGuid().ToString("N"));
var clock=Stopwatch.StartNew();int cycles=0;bool passed=false;string? error=null;
try{
 for(int i=0;i<2;i++){var client=new RoomClient(new ProfileStore(Path.Combine(temporary,"profile-"+i+".json")),audioFactory:publish=>{var engine=new SyntheticRoomAudio(publish);engines.Add(engine);return engine;});clients.Add(client);await client.JoinAsync(invite,"合成连麦 "+i);}
 var original=clients.Select(x=>x.MemberId).ToArray();double[] energy=[0,0];
 while(clock.Elapsed.TotalSeconds<seconds){
  if(cycles%12==0){using var page=new ClientWebSocket();page.Options.SetRequestHeader("Origin",invite.Origin);await page.ConnectAsync(new Uri("ws://127.0.0.1:4094/api/rooms/"+id+"/voice"),CancellationToken.None);await page.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new{type="join",version=3,role="controller",memberId=clients[0].MemberId,controlToken=clients[0].ControlToken}).AsMemory(),WebSocketMessageType.Text,true,CancellationToken.None);byte[] buffer=new byte[96000];await page.ReceiveAsync(buffer.AsMemory(),CancellationToken.None);page.Abort();}
  if(cycles>0&&cycles%18==0){using var result=await http.PostAsync("http://127.0.0.1:4095/drop",new StringContent("{}"));await Task.Delay(1600);}
  if(cycles>0&&cycles%24==0)await engines[0].InterruptDeviceAsync();
  if(cycles>0&&cycles%30==0){await clients[0].UpdateOptionsAsync(engines[0].Options with{Microphone=false});await Task.Delay(1100);if(engines[0].Options.Microphone)throw new Exception("Explicit mute was lost");await clients[0].UpdateOptionsAsync(engines[0].Options with{Microphone=true});}
  for(int i=0;i<2;i++){
   if(!clients[i].SessionActive||!engines[i].Options.Microphone||clients[i].MemberId!=original[i])throw new Exception("Native session state changed unexpectedly");
   if(cycles>1&&engines[i].ReceivedEnergy<=energy[i])throw new Exception("Decoded audio stopped progressing");energy[i]=engines[i].ReceivedEnergy;
  }
  cycles++;File.WriteAllText(report,JsonSerializer.Serialize(new{running=true,seconds=Math.Round(clock.Elapsed.TotalSeconds),cycles,syntheticOnly=true,realMicrophone=false,realScreen=false,frames=engines.Select(x=>x.SentFrames),decodedEnergy=energy}));
  if(cycles%6==0){await Task.Run(()=>{var busy=Stopwatch.StartNew();while(busy.ElapsedMilliseconds<150)Thread.SpinWait(10000);});}
  await Task.Delay(10000);
 }
 passed=true;
}catch(Exception ex){error=ex.ToString();}
finally{foreach(var client in clients)await client.DisposeAsync();if(Directory.Exists(temporary))Directory.Delete(temporary,true);}
File.WriteAllText(report,JsonSerializer.Serialize(new{passed,seconds=Math.Round(clock.Elapsed.TotalSeconds),cycles,error,syntheticOnly=true,realMicrophone=false,realScreen=false,frames=engines.Select(x=>x.SentFrames),decodedEnergy=engines.Select(x=>x.ReceivedEnergy)}));
Console.WriteLine(passed?"ENDURANCE_PASSED":"ENDURANCE_FAILED "+error);return passed?0:1;
