using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;
using VoiceBridge.App.Room;
using VoiceBridge.Core;
using Xunit;
namespace VoiceBridge.Tests;
public class ReconnectionTests
{
    [Fact]
    public async Task NativeClientResumesSameMemberWithoutOpeningAudioDevicesAndExitLeavesFriendsConnected()
    {
        string? root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "VoiceBridge.sln"))) root = Path.GetDirectoryName(root);
        Assert.NotNull(root);
        var info = new ProcessStartInfo("node") { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add("relay/tests/native-fixture.mjs");
        using var server = Process.Start(info)!;
        string directory = Path.Combine(Path.GetTempPath(), "VoiceBridge-reconnect-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var details = JsonDocument.Parse((await server.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)))!);
            string origin = details.RootElement.GetProperty("origin").GetString()!, control = details.RootElement.GetProperty("control").GetString()!;
            using var http = new HttpClient { BaseAddress = new Uri(origin), Timeout = TimeSpan.FromSeconds(5) }; http.DefaultRequestHeaders.Authorization = new("Bearer", new string('1', 64));
            using var response = await http.PostAsync("/api/rooms", new StringContent("{}")); response.EnsureSuccessStatusCode();
            using var created = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            string id = created.RootElement.GetProperty("id").GetString()!, token = created.RootElement.GetProperty("listenToken").GetString()!;
            using var friend = new ClientWebSocket(); friend.Options.SetRequestHeader("Origin", origin);
            await friend.ConnectAsync(new Uri(origin.Replace("http:", "ws:") + $"/api/rooms/{id}/voice"), CancellationToken.None);
            await friend.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new { type = "join", version = 3, role = "browser", token, name = "朋友" }).AsMemory(), WebSocketMessageType.Text, true, CancellationToken.None);
            byte[] bytes = new byte[96000]; await friend.ReceiveAsync(bytes.AsMemory(), CancellationToken.None);
            int starts = 0;
            await using var client = new RoomClient(new ProfileStore(Path.Combine(directory, "preferences.json")), _ => { starts++; return Task.CompletedTask; });
            await client.JoinAsync(new RoomInvite(origin, id, token, RandomNumberGenerator.GetBytes(32)), "记住我");
            string member = client.MemberId; Assert.True(client.Connected); Assert.Equal(1, starts);
            using var dropped = await http.PostAsync(control + "/drop", new StringContent("{}")); dropped.EnsureSuccessStatusCode();
            await WaitFor(() => !client.Connected);
            Assert.True(client.SessionActive);
            await WaitFor(() => client.Connected);
            Assert.Equal(member, client.MemberId); Assert.Equal(1, starts); Assert.Equal("记住我", new ProfileStore(Path.Combine(directory, "preferences.json")).Read().Nickname);
            await client.DisposeAsync();
            await WaitFor(() => !client.SessionActive);
            using var stats = JsonDocument.Parse(await http.GetStringAsync(control + "/stats"));
            Assert.Equal(1, stats.RootElement.GetProperty("rooms").GetInt32()); Assert.Equal(1, stats.RootElement.GetProperty("peers").GetInt32()); Assert.Equal(WebSocketState.Open, friend.State);
            friend.Abort();
        }
        finally
        {
            if (!server.HasExited) { await server.StandardInput.WriteLineAsync("stop"); try { await server.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); } catch (TimeoutException) { server.Kill(true); } }
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
    private static async Task WaitFor(Func<bool> condition)
    { var watch = Stopwatch.StartNew(); while (!condition()) { Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), "Connection did not reach expected state"); await Task.Delay(20); } }
}
