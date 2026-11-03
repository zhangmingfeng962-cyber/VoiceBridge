using System.IO;using System.IO.Compression;
using System.Security.Cryptography;
using Velopack;using Velopack.Locators;using Velopack.Logging;using Velopack.Sources;
string root=Path.GetFullPath(args[0]);var feed=VelopackAssetFeed.FromJson(File.ReadAllText(Path.Combine(root,"feed/releases.win-x64.json")));var baseline=feed.Assets.Single(a=>a.Version.ToString()=="0.11.99"&&a.Type==VelopackAssetType.Full);var target=feed.Assets.Single(a=>a.Version.ToString()=="0.12.0"&&a.Type==VelopackAssetType.Full);
foreach(bool failDelta in new[]{false,true}){
 string cache=Path.Combine(root,(failDelta?"fallback-cache-":"delta-cache-")+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(cache);File.Copy(Path.Combine(root,"feed",baseline.FileName),Path.Combine(cache,baseline.FileName),true);
 var locator=new TestVelopackLocator("VoiceBridge.Desktop","0.11.99",cache,Path.Combine(root,"baseline-app/current"),Path.Combine(root,"baseline-app"),Path.Combine(root,"baseline-app/Update.exe"),"win-x64",null,baseline,Path.Combine(root,"baseline-app/current/VoiceBridge.exe"));
 var source=new TrackingSource(new DirectoryInfo(Path.Combine(root,"feed")),failDelta);var manager=new UpdateManager(source,new UpdateOptions{ExplicitChannel="win-x64"},locator);var update=await manager.CheckForUpdatesAsync()??throw new Exception("Update missing");if(!update.DeltasToTarget.Any())throw new Exception("Delta not selected");await manager.DownloadUpdatesAsync(update);
 string prepared=Path.Combine(cache,target.FileName);using var actual=ZipFile.OpenRead(prepared);using var expected=ZipFile.OpenRead(Path.Combine(root,"feed",target.FileName));if(actual.Entries.Count!=expected.Entries.Count)throw new Exception("Prepared package entries differ");foreach(var item in expected.Entries){var copy=actual.GetEntry(item.FullName)??throw new Exception("Prepared entry missing");using var x=item.Open();using var y=copy.Open();if(!SHA256.HashData(x).SequenceEqual(SHA256.HashData(y)))throw new Exception("Prepared payload differs: "+item.FullName);}Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new{passed=true,failDelta,deltaCount=update.DeltasToTarget.Count(),source.Types,source.Bytes,target.Size}));
}
sealed class TrackingSource(DirectoryInfo root,bool fail):IUpdateSource{
 readonly SimpleFileSource inner=new(root);public List<string> Types=[];public long Bytes;
 public Task<VelopackAssetFeed> GetReleaseFeed(IVelopackLogger logger,string? appId,string? channel,Guid? stagingId,VelopackAsset? latestLocalRelease)=>inner.GetReleaseFeed(logger,appId,channel??"win-x64",stagingId,latestLocalRelease);
 public Task DownloadReleaseEntry(IVelopackLogger logger,VelopackAsset entry,string file,Action<int> progress,CancellationToken cancel){Types.Add(entry.Type.ToString());if(fail&&entry.Type==VelopackAssetType.Delta)throw new IOException("Synthetic delta failure");Bytes+=entry.Size;return inner.DownloadReleaseEntry(logger,entry,file,progress,cancel);}
}
