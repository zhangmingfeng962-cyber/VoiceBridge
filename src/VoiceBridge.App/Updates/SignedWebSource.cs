using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Velopack;
using Velopack.Logging;
using Velopack.Sources;
namespace VoiceBridge.App.Updates;
public sealed class SignedWebSource(Uri root, string publicKey) : IUpdateSource
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(15) };
    private readonly Dictionary<string, string> hashes = new(StringComparer.Ordinal);
    public static string ReadPublicKey()
    { using var resource = typeof(SignedWebSource).Assembly.GetManifestResourceStream("VoiceBridge.UpdatePublicKey") ?? throw new InvalidDataException("缺少更新验证公钥"); using var reader = new StreamReader(resource); return reader.ReadToEnd(); }
    public static VelopackAssetFeed VerifyFeed(byte[] bytes, string signature, string key)
    {
        if (bytes.Length > 524288) throw new InvalidDataException("更新清单过大");
        using var verifier = ECDsa.Create(); verifier.ImportFromPem(key);
        if (!verifier.VerifyData(bytes, Convert.FromBase64String(signature.Trim()), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)) throw new CryptographicException("更新清单签名不匹配，保留当前版本");
        var feed = VelopackAssetFeed.FromJson(Encoding.UTF8.GetString(bytes));
        foreach (var asset in feed.Assets)
            if (asset.PackageId != "VoiceBridge" || string.IsNullOrEmpty(asset.FileName) || asset.FileName.Contains("..", StringComparison.Ordinal) || asset.FileName.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-' or '_')) || asset.Size <= 0 || asset.Size > 314572800 || asset.SHA256?.Length != 64 || !asset.SHA256.All(Uri.IsHexDigit)) throw new InvalidDataException("更新文件信息无效");
        return feed;
    }
    public async Task<VelopackAssetFeed> GetReleaseFeed(IVelopackLogger logger, string? appId, string? channel, Guid? stagingId, VelopackAsset? latestLocalRelease)
    {
        if (channel is not (null or "win-x64")) throw new InvalidDataException("更新频道无效");
        byte[] bytes = await MetadataAsync("releases.win-x64.json", 524288);
        string signature = Encoding.ASCII.GetString(await MetadataAsync("releases.win-x64.json.sig", 2048));
        var feed = VerifyFeed(bytes, signature, publicKey); hashes.Clear(); foreach (var item in feed.Assets) hashes[item.FileName] = item.SHA256;
        return feed;
    }
    private async Task<byte[]> MetadataAsync(string name, int limit)
    {
        using var response = await Http.GetAsync(new Uri(root,name),HttpCompletionOption.ResponseHeadersRead); response.EnsureSuccessStatusCode();
        if(response.RequestMessage?.RequestUri?.GetLeftPart(UriPartial.Authority)!=root.GetLeftPart(UriPartial.Authority))throw new InvalidDataException("更新清单地址发生变化");
        await using var stream=await response.Content.ReadAsStreamAsync();using var result=new MemoryStream();byte[] buffer=new byte[8192];int count;
        while((count=await stream.ReadAsync(buffer))>0){if(result.Length+count>limit)throw new InvalidDataException("更新清单过大");result.Write(buffer,0,count);}return result.ToArray();
    }
    public async Task DownloadReleaseEntry(IVelopackLogger logger, VelopackAsset releaseEntry, string localFile, Action<int> progress, CancellationToken cancelToken)
    {
        if (!hashes.TryGetValue(releaseEntry.FileName, out string? expected) || expected != releaseEntry.SHA256) throw new CryptographicException("文件不在已验证清单中");
        long existing = File.Exists(localFile) ? new FileInfo(localFile).Length : 0;
        if (existing > releaseEntry.Size) { File.Delete(localFile); existing = 0; }
        if (existing != releaseEntry.Size)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(root, Uri.EscapeDataString(releaseEntry.FileName)));
            if (existing > 0) request.Headers.Range = new RangeHeaderValue(existing, null);
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancelToken); response.EnsureSuccessStatusCode();
            if (response.RequestMessage?.RequestUri?.GetLeftPart(UriPartial.Authority) != root.GetLeftPart(UriPartial.Authority)) throw new InvalidDataException("更新下载地址发生变化");
            bool append = existing > 0 && response.StatusCode == HttpStatusCode.PartialContent;
            if (response.StatusCode == HttpStatusCode.PartialContent)
            { var range = response.Content.Headers.ContentRange; if (range?.From != existing || range.To != releaseEntry.Size - 1 || range.Length != releaseEntry.Size) throw new InvalidDataException("更新续传范围不匹配"); }
            if (!append) existing = 0;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(localFile))!);
            await using var file = new FileStream(localFile, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 65536, true);
            await using var stream = await response.Content.ReadAsStreamAsync(cancelToken); byte[] buffer = new byte[65536]; int count;
            while ((count = await stream.ReadAsync(buffer, cancelToken)) > 0)
            { existing += count; if (existing > releaseEntry.Size) throw new InvalidDataException("更新包大小不匹配"); await file.WriteAsync(buffer.AsMemory(0, count), cancelToken); progress((int)(existing * 100 / releaseEntry.Size)); }
        }
        bool valid;
        await using(var input=File.OpenRead(localFile)) valid=input.Length==releaseEntry.Size&&Convert.ToHexString(await SHA256.HashDataAsync(input,cancelToken)).Equals(expected,StringComparison.OrdinalIgnoreCase);
        if(!valid){File.Delete(localFile);throw new CryptographicException("更新包校验失败，保留当前版本");}
    }
}
