using System.Security.Cryptography;
using System.Text;
using System.IO;
using System.Diagnostics;
using VoiceBridge.App.Updates;
using Xunit;
namespace VoiceBridge.Tests;
public class UpdateTrustTests
{
    [Fact] public async Task UpdateDownloadResumesVerifiesAndAllowsRetryAfterCorruption()
    {
        string? root=AppContext.BaseDirectory;while(root is not null&&!File.Exists(Path.Combine(root,"VoiceBridge.sln")))root=Path.GetDirectoryName(root);Assert.NotNull(root);
        string directory=Path.Combine(Path.GetTempPath(),"VoiceBridge-package-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        using var key=ECDsa.Create(ECCurve.NamedCurves.nistP256);byte[] package=RandomNumberGenerator.GetBytes(262144);string sha=Convert.ToHexString(SHA256.HashData(package));
        byte[] feed=Encoding.UTF8.GetBytes("{\"Assets\":[{\"PackageId\":\"VoiceBridge.Desktop\",\"Version\":\"0.12.0\",\"Type\":\"Full\",\"FileName\":\"sample.nupkg\",\"SHA256\":\""+sha+"\",\"Size\":262144}]}");
        await File.WriteAllBytesAsync(Path.Combine(directory,"releases.win-x64.json"),feed);await File.WriteAllTextAsync(Path.Combine(directory,"releases.win-x64.json.sig"),Convert.ToBase64String(key.SignData(feed,HashAlgorithmName.SHA256,DSASignatureFormat.Rfc3279DerSequence)));await File.WriteAllBytesAsync(Path.Combine(directory,"sample.nupkg"),package);
        var start=new ProcessStartInfo("node"){WorkingDirectory=root,UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true};start.ArgumentList.Add("relay/tests/update-http-fixture.mjs");start.ArgumentList.Add(directory);using var process=Process.Start(start)!;
        try{
            string origin=(await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)))!;var source=new SignedWebSource(new Uri(origin),key.ExportSubjectPublicKeyInfoPem());var entries=await source.GetReleaseFeed(null!,null,"win-x64",null,null);var entry=Assert.Single(entries.Assets);string target=Path.Combine(directory,"download.partial");
            await File.WriteAllBytesAsync(target,package[..32768]);await source.DownloadReleaseEntry(null!,entry,target,_=>{},CancellationToken.None);Assert.Equal("32768",await File.ReadAllTextAsync(Path.Combine(directory,"range.txt")));Assert.Equal(package,await File.ReadAllBytesAsync(target));
            await File.WriteAllTextAsync(Path.Combine(directory,"mode.txt"),"bad-range");await File.WriteAllBytesAsync(target,package[..100]);await Assert.ThrowsAsync<InvalidDataException>(()=>source.DownloadReleaseEntry(null!,entry,target,_=>{},CancellationToken.None));
            await File.WriteAllTextAsync(Path.Combine(directory,"mode.txt"),"corrupt");File.Delete(target);await Assert.ThrowsAsync<CryptographicException>(()=>source.DownloadReleaseEntry(null!,entry,target,_=>{},CancellationToken.None));Assert.False(File.Exists(target));
            await File.WriteAllTextAsync(Path.Combine(directory,"mode.txt"),"");await source.DownloadReleaseEntry(null!,entry,target,_=>{},CancellationToken.None);Assert.Equal(package,await File.ReadAllBytesAsync(target));
        }finally{await process.StandardInput.WriteLineAsync("stop");try{await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));}catch(TimeoutException){process.Kill(true);}Directory.Delete(directory,true);}
    }
    [Fact] public void UpdateFeedRequiresThePinnedSignatureAndSha256()
    {
        using var key=ECDsa.Create(ECCurve.NamedCurves.nistP256);using var other=ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] bytes=Encoding.UTF8.GetBytes("{\"Assets\":[]}");string signature=Convert.ToBase64String(key.SignData(bytes,HashAlgorithmName.SHA256,DSASignatureFormat.Rfc3279DerSequence));
        Assert.Empty(SignedWebSource.VerifyFeed(bytes,signature,key.ExportSubjectPublicKeyInfoPem()).Assets);
        Assert.Throws<CryptographicException>(()=>SignedWebSource.VerifyFeed(bytes,signature,other.ExportSubjectPublicKeyInfoPem()));
        bytes[0]=(byte)' ';Assert.Throws<CryptographicException>(()=>SignedWebSource.VerifyFeed(bytes,signature,key.ExportSubjectPublicKeyInfoPem()));
    }
}
