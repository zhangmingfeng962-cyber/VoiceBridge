using System.Buffers.Binary;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using VoiceBridge.App.Sharing;
using VoiceBridge.Core;
using Xunit;

namespace VoiceBridge.Tests;

public sealed class MusicLinkTests
{
    [Fact]
    public void PcmKeepsStereoFloatBitsAndSanitizesInvalidSamples()
    {
        float[] samples = Enumerable.Range(0, MusicPacket.Samples).Select(i => i % 2 == 0 ? i / 1000f : -i / 2000f).ToArray();
        samples[0] = -0f; samples[3] = float.NaN; samples[4] = float.PositiveInfinity;
        byte[] packet = MusicPacket.Encode(samples, uint.MaxValue);
        Assert.Equal("VBM1", Encoding.ASCII.GetString(packet, 0, 4));
        Assert.Equal(uint.MaxValue, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(4)));
        Assert.Equal(48000u, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(8)));
        Assert.Equal((ushort)2, BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(12)));
        for (int i = 0; i < samples.Length; i++)
            Assert.Equal(BitConverter.SingleToInt32Bits(float.IsFinite(samples[i]) ? samples[i] : 0), BitConverter.SingleToInt32Bits(BinaryPrimitives.ReadSingleLittleEndian(packet.AsSpan(16 + i * 4))));
        Assert.Throws<ArgumentException>(() => MusicPacket.Encode(new float[4], 0));
    }
    [Fact]
    public async Task AuthenticatedListenersReceiveIdenticalPcmAndShutdownRevokesLink()
    {
        var server = new MusicLinkServer();
        try
        {
            await server.StartAsync("QQ音乐");
            using var first = await ConnectAsync(server);
            using var second = await ConnectAsync(server);
            float[] samples = Enumerable.Range(0, MusicPacket.Samples).Select(i => MathF.Sin(i) * .4f).ToArray();
            byte[] packet = MusicPacket.Encode(samples, 902);
            server.Publish(packet);
            Assert.Equal(packet, await ReceiveAsync(first)); Assert.Equal(packet, await ReceiveAsync(second));
            Assert.Equal(2, server.ListenerCount);
            await server.DisposeAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { var result = await first.ReceiveAsync(new byte[10].AsMemory(), timeout.Token); Assert.Equal(WebSocketMessageType.Close, result.MessageType); }
            catch (WebSocketException) { }
            Assert.Equal(0, server.ListenerCount);
        }
        finally { await server.DisposeAsync(); }
    }
    [Fact]
    public async Task WrongInvitationCannotReceiveAudio()
    {
        await using var server = new MusicLinkServer(); await server.StartAsync("网易云音乐");
        using var socket = new ClientWebSocket(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await socket.ConnectAsync(SocketUri(server), timeout.Token);
        await socket.SendAsync(Encoding.ASCII.GetBytes(new string('0', 64)).AsMemory(), WebSocketMessageType.Text, true, timeout.Token);
        var result = await socket.ReceiveAsync(new byte[256].AsMemory(), timeout.Token);
        Assert.Equal(WebSocketMessageType.Close, result.MessageType); Assert.Equal(WebSocketCloseStatus.PolicyViolation, socket.CloseStatus);
        Assert.Equal(0, server.ListenerCount);
    }
    [Fact]
    public async Task ForeignBrowserOriginIsRejectedBeforeAuthentication()
    {
        await using var server = new MusicLinkServer(); await server.StartAsync("酷狗音乐");
        using var socket = new ClientWebSocket(); socket.Options.SetRequestHeader("Origin", "https://unrelated.example");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<WebSocketException>(() => socket.ConnectAsync(SocketUri(server), timeout.Token));
    }
    [Fact]
    public async Task ListenerLimitDoesNotDisconnectExistingFriends()
    {
        await using var server = new MusicLinkServer(); await server.StartAsync("QQ音乐");
        var clients = new List<ClientWebSocket>();
        try
        {
            for (int i = 0; i < MusicLinkServer.MaximumListeners; i++) clients.Add(await ConnectAsync(server));
            using var extra = new ClientWebSocket(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await extra.ConnectAsync(SocketUri(server), timeout.Token);
            await extra.SendAsync(Encoding.ASCII.GetBytes(new Uri(server.LocalLink).Fragment[1..]).AsMemory(), WebSocketMessageType.Text, true, timeout.Token);
            var result = await extra.ReceiveAsync(new byte[256].AsMemory(), timeout.Token);
            Assert.Equal(WebSocketMessageType.Close, result.MessageType); Assert.Equal(8, server.ListenerCount);
            var packet = MusicPacket.Encode(new float[MusicPacket.Samples], 0); server.Publish(packet);
            foreach (var client in clients) Assert.Equal(packet, await ReceiveAsync(client));
        }
        finally { foreach (var client in clients) client.Dispose(); }
    }
    [Fact]
    public async Task ReceiverAssetsHaveNoMicrophoneCaptureAndNoDirectoryAccess()
    {
        await using var server = new MusicLinkServer(); await server.StartAsync("QQ音乐");
        using var client = new HttpClient { BaseAddress = server.LocalOrigin };
        using var page = await client.GetAsync("/");
        Assert.Equal("no-referrer", page.Headers.GetValues("Referrer-Policy").Single());
        Assert.Contains("microphone=()", page.Headers.GetValues("Permissions-Policy").Single());
        string script = await client.GetStringAsync("/receiver.js");
        Assert.DoesNotContain("getUserMedia", script); Assert.DoesNotContain("MediaRecorder", script);
        Assert.Contains("audioWorklet", script); Assert.Contains("WebSocket", script);
        using var response = await client.GetAsync("/settings.json"); Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain(new Uri(server.LocalLink).Fragment[1..], await page.Content.ReadAsStringAsync());
    }
    [Fact]
    public async Task NewSessionChangesSecretAndHttpsRootMustBeExplicit()
    {
        await using var first = new MusicLinkServer(); await first.StartAsync("QQ音乐");
        await using var second = new MusicLinkServer(); await second.StartAsync("QQ音乐");
        Assert.NotEqual(new Uri(first.LocalLink).Fragment, new Uri(second.LocalLink).Fragment);
        first.SetPublicOrigin(new Uri("https://music.example/"));
        Assert.StartsWith("https://music.example/#", first.Invite(new Uri("https://music.example/")));
        Assert.Throws<ArgumentException>(() => first.SetPublicOrigin(new Uri("http://music.example/")));
        Assert.Throws<ArgumentException>(() => first.SetPublicOrigin(new Uri("https://music.example/control")));
        Assert.Throws<ArgumentException>(() => first.SetPublicOrigin(new Uri("https://music.example/?secret=1")));
    }
    [Theory]
    [InlineData("INF https://dawn-warm-sound.trycloudflare.com |", true)]
    [InlineData("https://example.com/", false)]
    [InlineData("unrelated connection text", false)]
    public void OnlyOfficialTemporaryHostnamesAreAccepted(string line, bool expected)
    {
        Assert.Equal(expected, QuickMusicTunnel.TryReadOrigin(line, out var result));
        if (expected) Assert.Equal("https", result!.Scheme);
    }
    private static Uri SocketUri(MusicLinkServer server) => new UriBuilder(server.LocalOrigin!) { Scheme = "ws", Path = "/audio" }.Uri;
    private static async Task<ClientWebSocket> ConnectAsync(MusicLinkServer server)
    {
        var socket = new ClientWebSocket();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await socket.ConnectAsync(SocketUri(server), timeout.Token);
            await socket.SendAsync(Encoding.ASCII.GetBytes(new Uri(server.LocalLink).Fragment[1..]).AsMemory(), WebSocketMessageType.Text, true, timeout.Token);
            using var hello = JsonDocument.Parse(await ReceiveAsync(socket));
            Assert.Equal("float32le", hello.RootElement.GetProperty("format").GetString()); return socket;
        }
        catch { socket.Dispose(); throw; }
    }
    private static async Task<byte[]> ReceiveAsync(ClientWebSocket socket)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        byte[] buffer = new byte[MusicPacket.PacketBytes]; int count = 0;
        ValueWebSocketReceiveResult result;
        do { result = await socket.ReceiveAsync(buffer.AsMemory(count), timeout.Token); count += result.Count; } while (!result.EndOfMessage);
        return buffer[..count];
    }
}
