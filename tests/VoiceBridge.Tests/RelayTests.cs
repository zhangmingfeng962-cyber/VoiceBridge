using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using VoiceBridge.App.Sharing;
using VoiceBridge.Core;
using Xunit;

namespace VoiceBridge.Tests;

public sealed class RelayTests
{
    [Fact]
    public void EncryptionPreservesAllPcmBytesAndUsesFreshNonces()
    {
        byte[] key = RandomNumberGenerator.GetBytes(32), plain = RandomNumberGenerator.GetBytes(RelayChunk.PlainBytes);
        byte[] first = RelayChunk.Encrypt(plain, key, new string('a', 32), 123);
        byte[] second = RelayChunk.Encrypt(plain, key, new string('a', 32), 123);
        Assert.Equal(RelayChunk.EncryptedBytes, first.Length); Assert.Equal(plain, RelayChunk.Decrypt(first, key, new string('a', 32)));
        Assert.NotEqual(first[8..20], second[8..20]);
    }
    [Fact]
    public void TamperedCiphertextRoomAndSequenceAreRejected()
    {
        byte[] key = RandomNumberGenerator.GetBytes(32), plain = new byte[RelayChunk.PlainBytes];
        byte[] chunk = RelayChunk.Encrypt(plain, key, new string('a', 32), 0);
        Assert.ThrowsAny<CryptographicException>(() => RelayChunk.Decrypt(chunk, key, new string('b', 32)));
        chunk[4] ^= 1; Assert.ThrowsAny<CryptographicException>(() => RelayChunk.Decrypt(chunk, key, new string('a', 32)));
        chunk[4] ^= 1; chunk[123] ^= 1; Assert.ThrowsAny<CryptographicException>(() => RelayChunk.Decrypt(chunk, key, new string('a', 32)));
    }
    [Fact]
    public void LocalSenderCredentialIsBoundToUserAndExactOrigin()
    {
        const string origin = "https://test.example", secret = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var profile = new RelayProfile { Origin = origin, ProtectedPublishKey = Convert.ToBase64String(ProtectedData.Protect(Encoding.ASCII.GetBytes(secret), Encoding.UTF8.GetBytes("VoiceBridge.Relay.v1|" + origin), DataProtectionScope.CurrentUser)) };
        Assert.Equal(secret, profile.ReadPublishKey()); profile.Origin = "https://other.example";
        Assert.Throws<CryptographicException>(() => profile.ReadPublishKey());
    }
    [Fact]
    public async Task UploaderSendsEncryptedStereoAndRevokesRoomOnStop()
    {
        var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders(); builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        await using var app = builder.Build();
        string roomId = new('a', 32), writer = new('b', 64), listener = new('c', 64), master = new('d', 64);
        byte[]? encrypted = null; bool deleted = false;
        app.MapGet("/health", () => Results.Json(new { service = "VoiceBridge encrypted music relay", ready = true }));
        app.MapPost("/api/rooms", (HttpContext context) => context.Request.Headers.Authorization == "Bearer " + master ? Results.Json(new { id = roomId, writeToken = writer, listenToken = listener }, statusCode: 201) : Results.StatusCode(403));
        app.MapPut("/api/rooms/{id}/chunks/{sequence}", async (HttpContext context) =>
        {
            Assert.Equal("Bearer " + writer, context.Request.Headers.Authorization.ToString());
            using var memory = new MemoryStream(); await context.Request.Body.CopyToAsync(memory); encrypted = memory.ToArray(); return Results.Json(new { accepted = 0 });
        });
        app.MapDelete("/api/rooms/{id}", (HttpContext context) => { Assert.Equal("Bearer " + writer, context.Request.Headers.Authorization.ToString()); deleted = true; return Results.Json(new { stopped = true }); });
        await app.StartAsync(); string origin = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        var profile = new RelayProfile { Origin = origin, ProtectedPublishKey = Convert.ToBase64String(ProtectedData.Protect(Encoding.ASCII.GetBytes(master), Encoding.UTF8.GetBytes("VoiceBridge.Relay.v1|" + origin), DataProtectionScope.CurrentUser)) };
        var client = await MusicRelayClient.CreateAsync(profile, "test", CancellationToken.None);
        byte[] key = Convert.FromHexString(new Uri(client.PublicLink).Fragment.Split('.')[1]);
        float[] samples = Enumerable.Range(0, MusicPacket.Samples).Select(i => i % 2 == 0 ? .2f : -.3f).ToArray();
        using var original = new MemoryStream();
        for (uint i = 0; i < RelayChunk.Packets; i++) { byte[] packet = MusicPacket.Encode(samples, i); original.Write(packet); client.Publish(packet); }
        try { await client.WaitUntilReadyAsync(CancellationToken.None); Assert.Equal(1, client.SentChunks); Assert.Equal(original.ToArray(), RelayChunk.Decrypt(encrypted!, key, roomId)); }
        finally { await client.DisposeAsync(); await app.StopAsync(); }
        Assert.True(deleted);
    }
}
