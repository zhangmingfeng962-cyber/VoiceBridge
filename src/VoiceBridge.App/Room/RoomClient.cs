using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using VoiceBridge.App.Audio;
using VoiceBridge.Core;

namespace VoiceBridge.App.Room;
public sealed class RoomClient : IAsyncDisposable
{
    private readonly ClientWebSocket socket = new();
    private readonly CancellationTokenSource stop = new();
    private readonly Channel<byte[]> controls = Channel.CreateUnbounded<byte[]>();
    private readonly Channel<(byte Kind, byte[] Data, long Time)> audio = Channel.CreateBounded<(byte, byte[], long)>(new BoundedChannelOptions(12) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly Channel<(string? Connection, JsonElement Command)> commands = Channel.CreateBounded<(string?, JsonElement)>(new BoundedChannelOptions(12) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly Channel<byte> wake = Channel.CreateBounded<byte>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly uint[] sequences = [0, 0];
    private Task? receiver, sender, commandLoop, stateLoop;
    private string? writer, connectionId;
    private RoomCipher? cipher;
    private long stateSequence, controlSequence = -1;
    private long appliedRevision;
    private readonly TaskCompletionSource joined = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private JsonElement members = JsonSerializer.SerializeToElement(Array.Empty<object>());
    private bool disposed, seenRoster, hadController;
    private readonly object disposeGate = new();
    private Task? disposal;
    public RoomInvite Invite { get; private set; } = null!;
    public string MemberId { get; private set; } = "";
    public string ControlToken { get; private set; } = "";
    public string ControllerLink => Invite.ControllerLink(MemberId, ControlToken);
    public DesktopAudio Audio { get; }
    public string Status { get; private set; } = "未连接";
    public bool Connected => socket.State == WebSocketState.Open && MemberId.Length > 0 && !stop.IsCancellationRequested;
    public event Action? Changed;
    public RoomClient() => Audio = new(PublishAudio);
    public async Task CreateAsync(string name)
    {
        string origin = RoomService.DefaultOrigin;
        using var client = new HttpClient { BaseAddress = new Uri(origin), Timeout = TimeSpan.FromSeconds(15) };
        using var health = JsonDocument.Parse(await client.GetStringAsync("/health", stop.Token));
        if (!health.RootElement.TryGetProperty("protocol", out var version) || version.GetInt32() < 3) throw new InvalidOperationException("中继尚未升级到新版音乐房间。");
        client.DefaultRequestHeaders.Add("X-VoiceBridge-Client", "0.10.0");
        using var created = await client.PostAsync("/api/rooms", new StringContent("{\"player\":\"room\"}", Encoding.UTF8, "application/json"), stop.Token); if ((int)created.StatusCode == 429) throw new InvalidOperationException("当前免费房间已满或建房过于频繁，请稍后重试。"); created.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await created.Content.ReadAsStringAsync(stop.Token)); var root = json.RootElement;
        Invite = new(origin, root.GetProperty("id").GetString()!, root.GetProperty("listenToken").GetString()!, RandomNumberGenerator.GetBytes(32)); writer = root.GetProperty("writeToken").GetString();
        await JoinAsync(Invite, name);
    }
    public async Task JoinAsync(RoomInvite invite, string name)
    {
        Invite = invite; cipher = new(invite.Key, invite.RoomId); Status = "正在连接音乐房间…";
        socket.Options.SetRequestHeader("Origin", invite.Origin); socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        await socket.ConnectAsync(new Uri(invite.Origin.Replace("https:", "wss:", StringComparison.Ordinal) + $"/api/rooms/{invite.RoomId}/voice"), stop.Token);
        byte[] join = JsonSerializer.SerializeToUtf8Bytes(new { type = "join", version = 3, role = "desktop", token = invite.Token, name = name[..Math.Min(20, name.Length)], hostToken = writer }, RoomCipher.JsonOptions);
        await socket.SendAsync(join.AsMemory(), WebSocketMessageType.Text, true, stop.Token);
        sender = Task.Run(SendLoopAsync); receiver = Task.Run(ReceiveLoopAsync);
        await joined.Task.WaitAsync(TimeSpan.FromSeconds(15), stop.Token);
        await Audio.StartAsync(); Queue(new { type = "flags", microphone = Audio.Options.Microphone, music = false, source = "" }); commandLoop = Task.Run(CommandLoopAsync); stateLoop = Task.Run(StateLoopAsync); Status = "已连接 · 打开网页控制房间"; Changed?.Invoke();
    }
    private void Queue(object value) { controls.Writer.TryWrite(JsonSerializer.SerializeToUtf8Bytes(value, RoomCipher.JsonOptions)); wake.Writer.TryWrite(0); }
    private void PublishAudio(byte kind, byte[] opus)
    { if (!Connected || cipher is null) return; audio.Writer.TryWrite((kind, cipher.SealAudio(kind, MemberId, sequences[kind]++, opus), Environment.TickCount64)); wake.Writer.TryWrite(0); }
    private async Task SendLoopAsync()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                if (controls.Reader.TryRead(out var control)) { await socket.SendAsync(control.AsMemory(), WebSocketMessageType.Text, true, stop.Token); continue; }
                if (audio.Reader.TryRead(out var frame))
                {
                    if (Environment.TickCount64 - frame.Time > 150 || (frame.Kind == 0 && (!Audio.Options.Microphone || !Audio.Talking)) || (frame.Kind == 1 && !Audio.Options.Sharing)) continue;
                    await socket.SendAsync(frame.Data.AsMemory(), WebSocketMessageType.Binary, true, stop.Token); continue;
                }
                await wake.Reader.ReadAsync(stop.Token);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException) { if (!disposed) Status = "连接已中断，麦克风已关闭"; stop.Cancel(); }
    }
    private async Task ReceiveLoopAsync()
    {
        byte[] bytes = new byte[96000];
        try
        {
            while (!stop.IsCancellationRequested)
            {
                int count = 0; ValueWebSocketReceiveResult result;
                do { result = await socket.ReceiveAsync(bytes.AsMemory(count), stop.Token); if (result.MessageType == WebSocketMessageType.Close) throw new WebSocketException("房间连接已关闭"); count += result.Count; if (count == bytes.Length && !result.EndOfMessage) throw new InvalidDataException("Room message too large."); } while (!result.EndOfMessage);
                if (result.MessageType == WebSocketMessageType.Binary)
                {
                    if (count < 54) continue; string member = Convert.ToHexString(bytes.AsSpan(0, 16)).ToLowerInvariant(); if (member == MemberId) continue;
                    try { var frame = cipher!.OpenAudio(member, bytes.AsSpan(16, count - 16)); Audio.Mix.Receive(member, frame.Kind, frame.Sequence, frame.Opus, Environment.TickCount64); }
                    catch (Exception ex) when (ex is CryptographicException or ArgumentException or InvalidDataException or Concentus.OpusException) { Status = "已忽略无法解码的一路音频；其他成员不受影响"; }
                    continue;
                }
                using var json = JsonDocument.Parse(bytes.AsMemory(0, count)); var root = json.RootElement; string? type = root.GetProperty("type").GetString();
                switch (type)
                {
                    case "welcome": MemberId = root.GetProperty("id").GetString()!; ControlToken = root.GetProperty("controlToken").GetString()!; joined.TrySetResult(); break;
                    case "members":
                        var next = root.GetProperty("members").Clone(); var ids = next.EnumerateArray().Select(m => m.GetProperty("id").GetString()).ToHashSet();
                        foreach (var old in members.EnumerateArray()) if (!ids.Contains(old.GetProperty("id").GetString())) Audio.Mix.Remove(old.GetProperty("id").GetString()!);
                        if (!seenRoster) Audio.Mix.Notify(true);
                        else { var oldIds = members.EnumerateArray().Select(m => m.GetProperty("id").GetString()).ToHashSet(); if (ids.Except(oldIds).Any()) Audio.Mix.Notify(true); if (oldIds.Except(ids).Any()) Audio.Mix.Notify(false); }
                        seenRoster = true; members = next; Changed?.Invoke(); break;
                    case "controllerAttached": if (hadController) Audio.PrivacyMute(); connectionId = root.GetProperty("connectionId").GetString(); controlSequence = -1; stateSequence = 0; appliedRevision = 0; if (hadController) commands.Writer.TryWrite((connectionId, JsonSerializer.SerializeToElement(new { action = "controllerDetached" }))); hadController = true; break;
                    case "controllerDetached": Audio.PrivacyMute(); connectionId = null; commands.Writer.TryWrite((connectionId, JsonSerializer.SerializeToElement(new { action = "controllerDetached" }))); break;
                    case "control":
                        if (connectionId is null || root.GetProperty("connectionId").GetString() != connectionId) break;
                        long sequence = root.GetProperty("sequence").GetInt64(); if (sequence <= controlSequence) break;
                        using (var command = cipher!.OpenControl(MemberId, connectionId, root)) { controlSequence = sequence; commands.Writer.TryWrite((connectionId, command.RootElement.Clone())); } break;
                }
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or InvalidDataException or CryptographicException or JsonException) { if (!disposed) Status = "房间已断开：" + ex.GetBaseException().Message; joined.TrySetException(ex); }
        finally { stop.Cancel(); _ = Audio.DisposeAsync(); Changed?.Invoke(); }
    }
    private async Task CommandLoopAsync()
    {
        try
        {
            await foreach (var queued in commands.Reader.ReadAllAsync(stop.Token))
            {
                if (queued.Connection != connectionId) continue;
                var command = queued.Command;
                string? action = command.TryGetProperty("action", out var property) ? property.GetString() : null;
                if (action == "leave") { _ = Task.Run(async () => await DisposeAsync()); break; }
                if (action == "controllerDetached") await Audio.ApplyAsync(Audio.Options with { Microphone = false });
                else if (action == "configure")
                {
                    var next = command.GetProperty("options").Deserialize<AudioOptions>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); if (next is not null) await Audio.ApplyAsync(next);
                    if (queued.Connection != connectionId) await Audio.ApplyAsync(Audio.Options with { Microphone = false });
                    else if (command.TryGetProperty("revision", out var revision)) appliedRevision = revision.GetInt64();
                }
                Queue(new { type = "flags", microphone = Audio.Options.Microphone, music = Audio.Options.Sharing, source = Audio.Inventory.Sources.FirstOrDefault(s => s.Id == Audio.Options.SourceId)?.Name ?? "" });
                Changed?.Invoke();
            }
        }
        catch (OperationCanceledException) { }
    }
    private async Task StateLoopAsync()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                if (connectionId is { } connection)
                {
                    var inventory = Audio.Inventory;
                    Queue(cipher!.SealControl(MemberId, connection, stateSequence++, new { options = Audio.Options, microphones = inventory.Microphones, outputs = inventory.Outputs, sources = inventory.Sources.Select(s => new { s.Id, s.Name, s.Active }), status = Audio.Status, microphonePeak = Audio.MicrophonePeak, talking = Audio.Talking, streams = Audio.Mix.Statistics(), speakerPeak = Audio.Mix.Peak, appliedRevision, nativePlayback = true }, true));
                }
                await Task.Delay(500, stop.Token);
            }
        }
        catch (OperationCanceledException) { }
    }
    public ValueTask DisposeAsync() { lock (disposeGate) return new(disposal ??= Task.Run(DisposeCoreAsync)); }
    private async Task DisposeCoreAsync()
    {
        if (disposed) return; disposed = true; stop.Cancel(); socket.Abort(); await Audio.DisposeAsync();
        foreach (var task in new[] { receiver, sender, commandLoop, stateLoop }) if (task is not null) { try { await task; } catch { } }
        if (writer is not null)
        {
            try { using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) }; client.DefaultRequestHeaders.Authorization = new("Bearer", writer); using var result = await client.DeleteAsync(Invite.Origin + $"/api/rooms/{Invite.RoomId}"); } catch { }
        }
        socket.Dispose(); stop.Dispose();
    }
}
