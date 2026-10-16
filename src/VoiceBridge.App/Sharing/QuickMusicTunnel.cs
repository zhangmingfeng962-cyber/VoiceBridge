using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VoiceBridge.App.Sharing;

// Optional, unmodified official cloudflared helper. No service, administrator rights, or account.
public sealed class QuickMusicTunnel : IDisposable
{
    public const string Version = "2026.10.1";
    public const string DownloadUrl = "https://github.com/cloudflare/cloudflared/releases/download/2026.10.1/cloudflared-windows-amd64.exe";
    public const string ExpectedSha256 = "4d2490e74082c5d48dbfedaf92576fa5bd1702379ab88eb7faa0cfbe8dc891b6";
    private static readonly SemaphoreSlim downloadGate = new(1, 1);
    private static readonly HttpClient http = new() { Timeout = TimeSpan.FromMinutes(3) };
    private Process? process;
    private ChildProcessJob? job;
    public Uri? CandidateOrigin { get; private set; }
    public bool HasExited => process?.HasExited == true;

    public static bool TryReadOrigin(string line, out Uri? origin)
    {
        origin = null;
        var match = Regex.Match(line, @"https://[a-z0-9]+(?:-[a-z0-9]+)*\.trycloudflare\.com\b", RegexOptions.CultureInvariant);
        return match.Success && Uri.TryCreate(match.Value, UriKind.Absolute, out origin);
    }
    public async Task<Uri> StartAsync(Uri localOrigin, string expectedHealthId, IProgress<string>? progress, CancellationToken token)
    {
        string path = await EnsureHelperAsync(progress, token).ConfigureAwait(false);
        progress?.Report("正在建立临时互联网连接…");
        var found = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        var info = new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardError = true, RedirectStandardOutput = true, WorkingDirectory = Path.GetDirectoryName(path)! };
        foreach (string arg in new[] { "tunnel", "--no-autoupdate", "--protocol", "http2", "--metrics", "127.0.0.1:0", "--url", localOrigin.GetLeftPart(UriPartial.Authority) }) info.ArgumentList.Add(arg);
        process = new Process { StartInfo = info, EnableRaisingEvents = true };
        void ReadLine(object sender, DataReceivedEventArgs args) { if (args.Data is { } line && TryReadOrigin(line, out var origin)) found.TrySetResult(origin!); }
        process.OutputDataReceived += ReadLine; process.ErrorDataReceived += ReadLine;
        process.Exited += (_, _) => found.TrySetException(new IOException("临时连接组件已退出。请检查网络后重试，或配置固定 HTTPS 地址。"));
        token.ThrowIfCancellationRequested();
        if (!process.Start()) throw new IOException("无法启动临时连接组件。");
        try { job = ChildProcessJob.Attach(process); }
        catch { process.Kill(entireProcessTree: true); throw; }
        process.BeginOutputReadLine(); process.BeginErrorReadLine();
        Uri url = CandidateOrigin = await found.Task.WaitAsync(TimeSpan.FromSeconds(45), token).ConfigureAwait(false);
        progress?.Report("正在检查临时链接能否访问本机音乐服务…");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(45));
        while (true)
        {
            if (deadline.IsCancellationRequested && !token.IsCancellationRequested) throw new TimeoutException("临时域名已分配，但当前网络无法在 45 秒内访问音乐服务。可停止后重试，或接入自己的 HTTPS 地址。");
            token.ThrowIfCancellationRequested();
            if (process.HasExited) throw new IOException("临时连接组件已退出。");
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token); attempt.CancelAfter(TimeSpan.FromSeconds(3));
            try
            {
                using var response = await http.GetAsync(new Uri(url, "/health"), attempt.Token).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(attempt.Token).ConfigureAwait(false));
                    if (json.RootElement.TryGetProperty("id", out var id) && id.GetString() == expectedHealthId) return url;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException || (ex is OperationCanceledException && !token.IsCancellationRequested))
            {
                if (deadline.IsCancellationRequested) throw new TimeoutException("临时域名已分配，但当前网络无法访问音乐服务。请停止后重试或配置固定 HTTPS 地址。");
            }
            try { await Task.Delay(1000, deadline.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new TimeoutException("临时域名已分配，但当前网络无法访问音乐服务。请停止后重试或配置固定 HTTPS 地址。"); }
        }
    }
    public static async Task<string> EnsureHelperAsync(IProgress<string>? progress, CancellationToken token)
    {
        await downloadGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VoiceBridge", "tools", "cloudflared-" + Version);
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "cloudflared.exe");
            if (File.Exists(path) && await HashMatchesAsync(path, token).ConfigureAwait(false)) return path;
            progress?.Report("首次使用：正在下载官方连接组件（约 53 MB），完成后校验 SHA-256…");
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".download";
            try
            {
                using var response = await http.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                await using (var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
                await using (var destination = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                {
                    byte[] buffer = new byte[65536]; long total = 0; int count;
                    while ((count = await source.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
                    {
                        total += count; if (total > 60_000_000) throw new InvalidDataException("连接组件下载超出预期大小。");
                        await destination.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                    }
                }
                if (!await HashMatchesAsync(temporary, token).ConfigureAwait(false)) throw new InvalidDataException("连接组件完整性校验失败，未执行下载的文件。");
                File.Move(temporary, path, overwrite: true); return path;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        finally { downloadGate.Release(); }
    }
    private static async Task<bool> HashMatchesAsync(string path, CancellationToken token)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false)).Equals(ExpectedSha256, StringComparison.OrdinalIgnoreCase);
    }
    public void Dispose()
    {
        job?.Dispose(); job = null;
        if (process is not null)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            process.Dispose(); process = null;
        }
    }
}
