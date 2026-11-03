using System.IO;
using Velopack;
using Velopack.Locators;
using Velopack.Logging;
using Velopack.Sources;
using VoiceBridge.App.Updates;
using Xunit;
namespace VoiceBridge.Tests;
public class UpdateRoomGuardTests
{
    [Fact]
    public async Task ScheduledUpdateNeverDownloadsOrExitsAfterSixtySecondsInRoom()
    {
        string path=Path.Combine(Path.GetTempPath(),"VoiceBridge-update-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(path);
        try
        {
            var source=new FakeSource();var locator=new TestVelopackLocator("VoiceBridge.Desktop","0.12.0",path);
            var manager=new UpdateManager(source,new UpdateOptions{ExplicitChannel="win-x64"},locator);
            var service=new UpdateService(()=>true,manager);await service.CheckAsync();Assert.True(service.Available);
            await service.UpdateAsync();Assert.True(service.Deferred);Assert.False(service.Busy);
            await Task.Delay(65000);await service.AfterLeavingAsync();Assert.True(service.Deferred);Assert.Equal(0,source.Downloads);
        }
        finally{Directory.Delete(path,true);}
    }
    private sealed class FakeSource:IUpdateSource
    {
        public int Downloads;
        public Task<VelopackAssetFeed> GetReleaseFeed(IVelopackLogger logger,string? appId,string? channel,Guid? stagingId,VelopackAsset? latestLocalRelease)=>Task.FromResult(VelopackAssetFeed.FromJson("{\"Assets\":[{\"PackageId\":\"VoiceBridge.Desktop\",\"Version\":\"0.12.1\",\"Type\":\"Full\",\"FileName\":\"VoiceBridge-0.12.1-win-x64-full.nupkg\",\"SHA256\":\""+new string('1',64)+"\",\"Size\":100}]}"));
        public Task DownloadReleaseEntry(IVelopackLogger logger,VelopackAsset releaseEntry,string localFile,Action<int> progress,CancellationToken cancelToken){Downloads++;throw new InvalidOperationException("Room must not start downloads");}
    }
}
