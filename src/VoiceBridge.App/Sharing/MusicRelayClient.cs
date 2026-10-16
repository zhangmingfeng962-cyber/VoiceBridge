using System.IO;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using VoiceBridge.Core;

namespace VoiceBridge.App.Sharing;

// Only outbound HTTPS music uploads. No reverse proxy or inbound access to the PC.
public sealed class MusicRelayClient : IAsyncDisposable
{
    private readonly HttpClient http;
    private readonly string roomId, writeToken;
    private readonly byte[] encryptionKey = RandomNumberGenerator.GetBytes(32);
    // Four seconds of bounded capture data absorb short WAN stalls without changing music gain.
    private readonly Channel<byte[]> queue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(400) { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly CancellationTokenSource stop = new();
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? uploader;
    private Exception? failure;
    private long dropped, sent;
    private int disposed;
    public string PublicLink { get; }
    public long DroppedPackets => Interlocked.Read(ref dropped);
    public long SentChunks => Interlocked.Read(ref sent);
    public Exception? Failure => Volatile.Read(ref failure);
    private MusicRelayClient(HttpClient http, string id, string writeToken, string listenToken)
    {
        this.http = http; roomId = id; this.writeToken = writeToken;
        PublicLink = http.BaseAddress!.GetLeftPart(UriPartial.Authority) + "/listen/" + id + "#" + listenToken + "." + Convert.ToHexString(encryptionKey).ToLowerInvariant();
    }
    public static async Task<MusicRelayClient> CreateAsync(RelayProfile profile, string playerId, CancellationToken token)
    {
        var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(5) }) { BaseAddress = new Uri(profile.Origin), Timeout = TimeSpan.FromSeconds(8), DefaultRequestVersion = HttpVersion.Version20, DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("VoiceBridge/0.7.0");
        try
        {
            await WarmUpAsync(client, token).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/rooms") { Content = JsonContent.Create(new { player = playerId }) };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", profile.ReadPublishKey());
            using var response = await client.SendAsync(request, token).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.Created) throw new IOException($"中继未接受开始分享（HTTP {(int)response.StatusCode}）。请检查本机发送配置或服务状态。");
            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false), cancellationToken: token).ConfigureAwait(false);
            var root = json.RootElement; string id = root.GetProperty("id").GetString()!, writer = root.GetProperty("writeToken").GetString()!, listener = root.GetProperty("listenToken").GetString()!;
            if (!Hex(id, 32) || !Hex(writer, 64) || !Hex(listener, 64)) throw new InvalidDataException("中继返回了无效房间凭据。");
            var result = new MusicRelayClient(client, id, writer, listener); result.uploader = Task.Run(result.UploadAsync); return result;
        }
        catch { client.Dispose(); throw; }
    }
    private static async Task WarmUpAsync(HttpClient client, CancellationToken token)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(90))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                using var response = await client.GetAsync("/health", token).ConfigureAwait(false);
                if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
                    throw new InvalidOperationException("服务提供商拒绝访问中继（HTTP 403/401）。请由服务提供商解除限制或使用可访问的托管地址。");
                if (response.IsSuccessStatusCode)
                {
                    using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token).ConfigureAwait(false));
                    if (json.RootElement.TryGetProperty("service", out var service) && service.GetString() == "VoiceBridge encrypted music relay")
                    {
                        if (!json.RootElement.GetProperty("ready").GetBoolean()) throw new InvalidOperationException("中继缺少服务器配置，请先完成部署。");
                        return;
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException || (ex is OperationCanceledException && !token.IsCancellationRequested)) { }
            await Task.Delay(1500, token).ConfigureAwait(false);
        }
        throw new TimeoutException("中继未能在 90 秒内就绪。免费服务可能正在冷启动，请稍后重试并检查服务状态。");
    }
    private static bool Hex(string value, int length) => value.Length == length && value.All(char.IsAsciiHexDigit);
    public void Publish(byte[] packet)
    {
        if (!queue.Writer.TryWrite(packet)) Interlocked.Increment(ref dropped);
    }
    public Task WaitUntilReadyAsync(CancellationToken token) => ready.Task.WaitAsync(TimeSpan.FromSeconds(20), token);
    private async Task UploadAsync()
    {
        try
        {
            byte[] plaintext = new byte[RelayChunk.PlainBytes]; int count = 0; uint sequence = 0;
            await foreach (var packet in queue.Reader.ReadAllAsync(stop.Token).ConfigureAwait(false))
            {
                if (packet.Length != MusicPacket.PacketBytes) throw new InvalidDataException("Invalid PCM packet.");
                packet.CopyTo(plaintext, count * MusicPacket.PacketBytes); count++;
                if (count != RelayChunk.Packets) continue;
                byte[] encrypted = RelayChunk.Encrypt(plaintext, encryptionKey, roomId, sequence);
                bool accepted = false;
                for (int attempt = 0; attempt < 3 && !accepted; attempt++)
                {
                    try
                    {
                        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/rooms/{roomId}/chunks/{sequence}") { Content = new ByteArrayContent(encrypted) };
                        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", writeToken);
                        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                        using var response = await http.SendAsync(request, stop.Token).ConfigureAwait(false);
                        if (!response.IsSuccessStatusCode) throw new IOException($"音乐上传失败（HTTP {(int)response.StatusCode}）。");
                        accepted = true;
                    }
                    catch (Exception ex) when (!stop.IsCancellationRequested && attempt < 2 && ex is HttpRequestException or IOException or TaskCanceledException)
                    { await Task.Delay(250, stop.Token).ConfigureAwait(false); }
                }
                Interlocked.Increment(ref sent); ready.TrySetResult(); sequence++; count = 0;
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { ready.TrySetCanceled(stop.Token); }
        catch (Exception ex) { Interlocked.CompareExchange(ref failure, ex, null); ready.TrySetException(ex); }
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        stop.Cancel(); queue.Writer.TryComplete();
        if (uploader is not null) { try { await uploader.ConfigureAwait(false); } catch (OperationCanceledException) { } }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Delete, "/api/rooms/" + roomId);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", writeToken);
            using var response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException) { /* Lease expires after 120 s without uploads. */ }
        CryptographicOperations.ZeroMemory(encryptionKey); http.Dispose(); stop.Dispose();
    }
}
