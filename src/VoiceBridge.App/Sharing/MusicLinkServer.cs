using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using VoiceBridge.Core;

namespace VoiceBridge.App.Sharing;

public sealed class MusicLinkServer : IAsyncDisposable
{
    public const int MaximumListeners = 8;
    private readonly ConcurrentDictionary<long, Listener> listeners = new();
    private readonly CancellationTokenSource stop = new();
    private readonly string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    private readonly string healthId = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
    private WebApplication? app;
    private long nextId, overflows;
    private int pending, disposed;
    private string? publicOrigin;
    public Uri? LocalOrigin { get; private set; }
    public string LocalLink => LocalOrigin is null ? "" : Invite(LocalOrigin);
    public string HealthId => healthId;
    public int ListenerCount => listeners.Count;
    public long SlowConnections => Interlocked.Read(ref overflows);
    public string Invite(Uri origin) => origin.GetLeftPart(UriPartial.Authority) + "/#" + token;

    public void SetPublicOrigin(Uri origin)
    {
        if (origin.Scheme != "https" || !string.IsNullOrEmpty(origin.UserInfo) || origin.AbsolutePath != "/" || origin.Query.Length != 0 || origin.Fragment.Length != 0)
            throw new ArgumentException("请使用根路径的 HTTPS 地址，例如 https://music.example.com/。");
        Volatile.Write(ref publicOrigin, origin.GetLeftPart(UriPartial.Authority));
    }

    public async Task StartAsync(string playerName, CancellationToken cancellationToken = default)
    {
        if (app is not null) throw new InvalidOperationException("Already started.");
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory, EnvironmentName = "Production" });
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders(); // Never log invitation tokens, request URLs, or device identifiers.
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, 0);
            options.Limits.MaxRequestBodySize = 0;
            options.Limits.MaxRequestHeadersTotalSize = 8192;
            options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
        });
        var application = builder.Build();
        application.Use(async (context, next) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["X-Frame-Options"] = "DENY";
            context.Response.Headers["Permissions-Policy"] = "microphone=(), camera=(), display-capture=()";
            context.Response.Headers["Content-Security-Policy"] = "default-src 'none'; script-src 'self'; style-src 'unsafe-inline'; connect-src 'self'; worker-src 'self'; base-uri 'none'; frame-ancestors 'none'; form-action 'none'";
            await next(context);
        });
        application.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(15) });
        application.MapGet("/", () => Results.Content(Resource("receiver.html"), "text/html; charset=utf-8"));
        application.MapGet("/receiver.js", () => Results.Content(Resource("receiver.js"), "text/javascript; charset=utf-8"));
        application.MapGet("/pcm-worklet.js", () => Results.Content(Resource("pcm-worklet.js"), "text/javascript; charset=utf-8"));
        application.MapGet("/health", () => Results.Json(new { service = "VoiceBridge Music", id = healthId }));
        application.Map("/audio", context => AcceptAsync(context, playerName));
        try
        {
            await application.StartAsync(cancellationToken).ConfigureAwait(false);
            string address = application.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            LocalOrigin = new Uri(address);
            app = application;
        }
        catch { await application.DisposeAsync().ConfigureAwait(false); throw; }
    }

    private async Task AcceptAsync(HttpContext context, string playerName)
    {
        string origin = context.Request.Headers.Origin.ToString();
        if (!context.WebSockets.IsWebSocketRequest ||
            (origin.Length != 0 && origin != LocalOrigin?.GetLeftPart(UriPartial.Authority) && origin != Volatile.Read(ref publicOrigin)))
        { context.Response.StatusCode = StatusCodes.Status403Forbidden; return; }
        if (Interlocked.Increment(ref pending) > 16)
        { Interlocked.Decrement(ref pending); context.Response.StatusCode = StatusCodes.Status429TooManyRequests; return; }
        long id = 0;
        WebSocket? socket = null;
        Listener? listener = null;
        try
        {
            socket = await context.WebSockets.AcceptWebSocketAsync();
            using var authTimeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token, context.RequestAborted);
            authTimeout.CancelAfter(TimeSpan.FromSeconds(8));
            byte[] authentication = new byte[128];
            var auth = await socket.ReceiveAsync(authentication.AsMemory(), authTimeout.Token);
            if (auth.MessageType != WebSocketMessageType.Text || !auth.EndOfMessage || auth.Count != token.Length ||
                !CryptographicOperations.FixedTimeEquals(authentication.AsSpan(0, auth.Count), Encoding.ASCII.GetBytes(token)))
            { await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Invitation invalid", authTimeout.Token); return; }
            // Atomic admission, including simultaneous authenticated connections.
            lock (listeners)
            {
                if (listeners.Count < MaximumListeners)
                {
                    id = Interlocked.Increment(ref nextId);
                    listener = new Listener(socket);
                    listeners[id] = listener;
                }
            }
            if (listener is null)
            { await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Listener limit reached", authTimeout.Token); return; }
            byte[] hello = JsonSerializer.SerializeToUtf8Bytes(new { type = "format", sampleRate = MusicPacket.SampleRate, channels = MusicPacket.Channels, frames = MusicPacket.Frames, format = "float32le", player = playerName });
            await socket.SendAsync(hello.AsMemory(), WebSocketMessageType.Text, true, authTimeout.Token);
            using var active = CancellationTokenSource.CreateLinkedTokenSource(stop.Token, context.RequestAborted);
            Task sending = SendAsync(listener, active.Token), receiving = ReceiveHeartbeatAsync(socket, active.Token);
            await Task.WhenAny(sending, receiving).ConfigureAwait(false);
            active.Cancel(); Abort(socket);
            try { await Task.WhenAll(sending, receiving).ConfigureAwait(false); } catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ChannelClosedException or ObjectDisposedException) { }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or IOException or ObjectDisposedException) { }
        finally
        {
            if (id != 0) listeners.TryRemove(id, out _);
            listener?.Packets.Writer.TryComplete(); if (socket is not null) { if (socket.State != WebSocketState.Closed) Abort(socket); socket.Dispose(); }
            Interlocked.Decrement(ref pending);
        }
    }
    private static async Task SendAsync(Listener listener, CancellationToken token)
    {
        await foreach (byte[] packet in listener.Packets.Reader.ReadAllAsync(token).ConfigureAwait(false))
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            await listener.Socket.SendAsync(packet.AsMemory(), WebSocketMessageType.Binary, true, timeout.Token).ConfigureAwait(false);
        }
    }
    private static async Task ReceiveHeartbeatAsync(WebSocket socket, CancellationToken token)
    {
        byte[] buffer = new byte[32];
        while (!token.IsCancellationRequested)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            var message = await socket.ReceiveAsync(buffer.AsMemory(), timeout.Token).ConfigureAwait(false);
            if (message.MessageType != WebSocketMessageType.Text || !message.EndOfMessage || message.Count != 4 || !buffer.AsSpan(0, 4).SequenceEqual("ping"u8)) return;
        }
    }
    public void Publish(byte[] packet)
    {
        if (packet.Length != MusicPacket.PacketBytes || !packet.AsSpan(0, 4).SequenceEqual("VBM1"u8)) throw new ArgumentException("Invalid PCM packet.");
        foreach (var item in listeners)
            if (!item.Value.Packets.Writer.TryWrite(packet))
            {
                // A slow receiver cannot block capture or build an unlimited delayed playlist.
                Interlocked.Increment(ref overflows);
                Abort(item.Value.Socket); listeners.TryRemove(item.Key, out _);
            }
    }
    private static string Resource(string name)
    {
        using var stream = typeof(MusicLinkServer).Assembly.GetManifestResourceStream("VoiceBridge.Sharing.Web." + name) ?? throw new InvalidOperationException("Missing receiver resource.");
        using var reader = new StreamReader(stream, Encoding.UTF8); return reader.ReadToEnd();
    }
    private static void Abort(WebSocket socket) { try { socket.Abort(); } catch (ObjectDisposedException) { } }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        stop.Cancel();
        foreach (var item in listeners.Values) { item.Packets.Writer.TryComplete(); Abort(item.Socket); }
        if (app is not null)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await app.StopAsync(timeout.Token).ConfigureAwait(false); } catch (OperationCanceledException) { }
            await app.DisposeAsync().ConfigureAwait(false);
        }
        listeners.Clear(); stop.Dispose();
    }
    private sealed class Listener(WebSocket socket)
    {
        public WebSocket Socket { get; } = socket;
        public Channel<byte[]> Packets { get; } = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(32) { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
    }
}
