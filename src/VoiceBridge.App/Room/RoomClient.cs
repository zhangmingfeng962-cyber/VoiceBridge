using System.IO;
using System.Net.Http;
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
    private ClientWebSocket? socket;
    private readonly CancellationTokenSource stop = new();
    private readonly SemaphoreSlim sendGate = new(1);
    private readonly Channel<byte[]> controls = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(16) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly Channel<(byte[] Data, long Time)>[] audio = [
        Channel.CreateBounded<(byte[], long)>(new BoundedChannelOptions(6) { FullMode = BoundedChannelFullMode.DropOldest }),
        Channel.CreateBounded<(byte[], long)>(new BoundedChannelOptions(6) { FullMode = BoundedChannelFullMode.DropOldest })];
    private readonly Channel<(string? Connection, JsonElement Command)> commands = Channel.CreateBounded<(string?, JsonElement)>(new BoundedChannelOptions(16) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly Channel<byte> wake = Channel.CreateBounded<byte>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly uint[] sequences = [0, 0];
    private readonly ProfileStore profile = new();
    private Task? sessionLoop, commandLoop, stateLoop;
    private string? writer, connectionId, resumeToken;
    private RoomCipher? cipher;
    private long stateSequence, controlSequence = -1, appliedRevision;
    private readonly TaskCompletionSource joined = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private JsonElement members = JsonSerializer.SerializeToElement(Array.Empty<object>());
    private bool disposed, seenRoster, hadController;
    private volatile bool networkReady, running;
    private readonly object disposeGate = new();
    private Task? disposal;
    public RoomInvite Invite { get; private set; } = null!;
    public string MemberId { get; private set; } = "";
    public string Name { get; private set; } = "朋友";
    public string ControlToken { get; private set; } = "";
    public string ControllerLink => Invite.ControllerLink(MemberId, ControlToken);
    public DesktopAudio Audio { get; }
    public string Status { get; private set; } = "未连接";
    public bool Connected => networkReady && socket?.State == WebSocketState.Open && !stop.IsCancellationRequested;
    public bool SessionActive => running && !disposed && !stop.IsCancellationRequested;
    public event Action? Changed;
    public RoomClient() => Audio = new(PublishAudio);
    public async Task CreateAsync(string name)
    {
        string origin = RoomService.DefaultOrigin;
        using var client = new HttpClient { BaseAddress = new Uri(origin), Timeout = TimeSpan.FromSeconds(15) };
        using var health = JsonDocument.Parse(await client.GetStringAsync("/health", stop.Token));
        if (!health.RootElement.TryGetProperty("protocol", out var version) || version.GetInt32() < 3) throw new InvalidOperationException("中继尚未升级到新版音乐房间。");
        client.DefaultRequestHeaders.Add("X-VoiceBridge-Client", "0.11.0");
        using var created = await client.PostAsync("/api/rooms", new StringContent("{\"player\":\"room\"}", Encoding.UTF8, "application/json"), stop.Token);
        if ((int)created.StatusCode == 429) throw new InvalidOperationException("当前房间已满或建房过于频繁，请稍后重试。");
        created.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await created.Content.ReadAsStringAsync(stop.Token)); var root = json.RootElement;
        Invite = new(origin, root.GetProperty("id").GetString()!, root.GetProperty("listenToken").GetString()!, RandomNumberGenerator.GetBytes(32)); writer = root.GetProperty("writeToken").GetString();
        await JoinAsync(Invite, name);
    }
    public async Task JoinAsync(RoomInvite invite, string name)
    {
        Invite = invite; Name = ProfileSettings.CleanName(name); if (Name.Length == 0) Name = "朋友";
        profile.Save(profile.Read() with { Nickname = Name });
        cipher = new(invite.Key, invite.RoomId); Status = "正在连接音乐房间…"; running = true;
        commandLoop = Task.Run(CommandLoopAsync); sessionLoop = Task.Run(SessionLoopAsync);
        await joined.Task.WaitAsync(TimeSpan.FromSeconds(20), stop.Token);
        await Audio.StartAsync(); Audio.SetNetworkConnected(Connected); SendFlags();
        stateLoop = Task.Run(StateLoopAsync); Status = "已连接 · 打开网页控制房间"; Changed?.Invoke();
    }
    private void Queue(object value)
    {
        if (!Connected) return;
        controls.Writer.TryWrite(JsonSerializer.SerializeToUtf8Bytes(value, RoomCipher.JsonOptions)); wake.Writer.TryWrite(0);
    }
    private void SendFlags() => Queue(new { type = "flags", microphone = Audio.Options.Microphone, music = Audio.Options.Sharing, source = Audio.Inventory.Sources.FirstOrDefault(s => s.Id == Audio.Options.SourceId)?.Name ?? "" });
    private void PublishAudio(byte kind, byte[] opus)
    {
        if (!Connected || cipher is null) return;
        audio[kind].Writer.TryWrite((cipher.SealAudio(kind, MemberId, sequences[kind]++, opus), Environment.TickCount64)); wake.Writer.TryWrite(0);
    }
    private void ClearQueues()
    {
        while (controls.Reader.TryRead(out _)) { }
        foreach (var lane in audio) while (lane.Reader.TryRead(out _)) { }
        Audio.SetNetworkConnected(false); networkReady = false;
    }
    private async Task SessionLoopAsync()
    {
        long recoveryStarted = 0; int attempt = 0;
        try
        {
            while (!stop.IsCancellationRequested)
            {
                using var transport = new ClientWebSocket(); using var sessionStop = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                socket = transport; transport.Options.SetRequestHeader("Origin", Invite.Origin); transport.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
                try
                {
                    await transport.ConnectAsync(new Uri(Invite.Origin.Replace("https:", "wss:", StringComparison.Ordinal) + $"/api/rooms/{Invite.RoomId}/voice"), sessionStop.Token).WaitAsync(TimeSpan.FromSeconds(12), stop.Token);
                    object request = MemberId.Length > 0 && resumeToken is not null
                        ? new { type = "join", version = 3, role = "desktop", token = Invite.Token, resume = new { memberId = MemberId, token = resumeToken } }
                        : new { type = "join", version = 3, role = "desktop", token = Invite.Token, name = Name, hostToken = writer, canResume = true };
                    await SendAsync(transport, JsonSerializer.SerializeToUtf8Bytes(request, RoomCipher.JsonOptions), false, sessionStop.Token);
                    var welcomed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var receive = ReceiveLoopAsync(transport, welcomed, sessionStop.Token); var send = SendLoopAsync(transport, sessionStop.Token);
                    try
                    {
                        var first = await Task.WhenAny(welcomed.Task, receive, Task.Delay(15000, sessionStop.Token));
                        if (first != welcomed.Task) { if (receive.IsCompleted) await receive; throw new TimeoutException("入房响应超时"); }
                        await welcomed.Task; recoveryStarted = 0; attempt = 0;
                        networkReady = true; Audio.SetNetworkConnected(true); SendFlags(); Status = "已连接 · 打开网页控制房间"; joined.TrySetResult(); Changed?.Invoke();
                        await await Task.WhenAny(receive, send);
                    }
                    finally { sessionStop.Cancel(); transport.Abort(); try { await Task.WhenAll(receive, send); } catch (Exception) { } }
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
                catch (Exception ex) when (ex is WebSocketException or IOException or TimeoutException or OperationCanceledException or JsonException or CryptographicException)
                {
                    ClearQueues();
                    if (ex is RoomDisconnected terminal && terminal.Code is 1000 or 4001 or 4003 or 4004 or 4009 or 4010)
                    { Status = terminal.Code == 4009 ? "房间已满，最多 8 人" : terminal.Code == 4010 ? "重连保留时间已结束，请重新加入房间" : "房间已结束或连接权限失效，请重新加入"; joined.TrySetException(ex); break; }
                    if (MemberId.Length == 0) { Status = "无法连接房间：" + ex.Message; joined.TrySetException(ex); break; }
                    if (resumeToken is null) { Status = "旧版服务不支持恢复，请重新加入"; break; }
                    recoveryStarted = recoveryStarted == 0 ? Environment.TickCount64 : recoveryStarted;
                    if (Environment.TickCount64 - recoveryStarted >= 90000) { Status = "网络中断超过 90 秒，请重新加入房间"; break; }
                    Status = "网络波动，正在自动重连… 音频上行已暂停"; Changed?.Invoke();
                    double delay = Math.Min(5000, 500 * Math.Pow(2, Math.Min(attempt++, 4))) * (.8 + Random.Shared.NextDouble() * .2);
                    await Task.Delay(TimeSpan.FromMilliseconds(delay), stop.Token);
                }
                finally { if (ReferenceEquals(socket, transport)) socket = null; }
            }
        }
        catch (OperationCanceledException) { }
        finally { running = false; networkReady = false; Audio.SetNetworkConnected(false); Audio.PrivacyMute(); Changed?.Invoke(); }
    }
    private async Task SendAsync(ClientWebSocket transport, byte[] data, bool binary, CancellationToken token)
    {
        await sendGate.WaitAsync(token);
        try { await transport.SendAsync(data.AsMemory(), binary ? WebSocketMessageType.Binary : WebSocketMessageType.Text, true, token); }
        finally { sendGate.Release(); }
    }
    private async Task SendLoopAsync(ClientWebSocket transport, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            if (controls.Reader.TryRead(out var control)) { await SendAsync(transport, control, false, token); continue; }
            bool sent = false;
            for (byte kind = 0; kind < 2; kind++) if (audio[kind].Reader.TryRead(out var frame))
            {
                if (Environment.TickCount64 - frame.Time > 120 || kind == 0 && (!Audio.Options.Microphone || !Audio.Talking) || kind == 1 && !Audio.Options.Sharing) continue;
                await SendAsync(transport, frame.Data, true, token); sent = true; break;
            }
            if (!sent) await wake.Reader.ReadAsync(token);
        }
    }
    private async Task ReceiveLoopAsync(ClientWebSocket transport, TaskCompletionSource welcomed, CancellationToken token)
    {
        byte[] bytes = new byte[96000];
        while (!token.IsCancellationRequested)
        {
            int count = 0; ValueWebSocketReceiveResult result;
            do
            {
                result = await transport.ReceiveAsync(bytes.AsMemory(count), token);
                if (result.MessageType == WebSocketMessageType.Close) throw new RoomDisconnected((int?)transport.CloseStatus ?? 1006);
                count += result.Count; if (count == bytes.Length && !result.EndOfMessage) throw new InvalidDataException("Room message too large.");
            } while (!result.EndOfMessage);
            if (result.MessageType == WebSocketMessageType.Binary)
            {
                if (count < 54) continue; string member = Convert.ToHexString(bytes.AsSpan(0, 16)).ToLowerInvariant(); if (member == MemberId) continue;
                try { var frame = cipher!.OpenAudio(member, bytes.AsSpan(16, count - 16)); Audio.Mix.Receive(member, frame.Kind, frame.Sequence, frame.Opus, Environment.TickCount64); }
                catch (Exception ex) when (ex is CryptographicException or ArgumentException or InvalidDataException or Concentus.OpusException) { Status = "已忽略无法解码的一路音频"; }
                continue;
            }
            using var json = JsonDocument.Parse(bytes.AsMemory(0, count)); var root = json.RootElement; string? type = root.GetProperty("type").GetString();
            switch (type)
            {
                case "welcome":
                    MemberId = root.GetProperty("id").GetString()!; ControlToken = root.GetProperty("controlToken").GetString()!;
                    if (root.TryGetProperty("resumeToken", out var resume) && resume.ValueKind == JsonValueKind.String) resumeToken = resume.GetString();
                    if (root.TryGetProperty("resumed", out var resumed) && resumed.GetBoolean())
                    {
                        if (root.TryGetProperty("pendingControl", out var pending) && pending.ValueKind == JsonValueKind.Object) await HandleControlAsync(pending);
                        if (!root.TryGetProperty("controllerPresent", out var present) || !present.GetBoolean()) Audio.PrivacyMute();
                    }
                    welcomed.TrySetResult(); break;
                case "members":
                    var next = root.GetProperty("members").Clone(); var ids = next.EnumerateArray().Select(m => m.GetProperty("id").GetString()).ToHashSet();
                    foreach (var old in members.EnumerateArray()) if (!ids.Contains(old.GetProperty("id").GetString())) Audio.Mix.Remove(old.GetProperty("id").GetString()!);
                    if (!seenRoster) Audio.Mix.Notify(true);
                    else { var oldIds = members.EnumerateArray().Select(m => m.GetProperty("id").GetString()).ToHashSet(); if (ids.Except(oldIds).Any()) Audio.Mix.Notify(true); if (oldIds.Except(ids).Any()) Audio.Mix.Notify(false); }
                    foreach (var member in next.EnumerateArray()) if (member.GetProperty("id").GetString() == MemberId)
                    {
                        string name = ProfileSettings.CleanName(member.GetProperty("name").GetString());
                        if (name.Length > 0 && name != Name) { Name = name; profile.Save(profile.Read() with { Nickname = name }); }
                    }
                    seenRoster = true; members = next; Changed?.Invoke(); break;
                case "controllerAttached":
                    if (hadController) Audio.PrivacyMute();
                    connectionId = root.GetProperty("connectionId").GetString(); controlSequence = -1; stateSequence = 0; appliedRevision = 0; hadController = true; break;
                case "controllerDetached": Audio.PrivacyMute(); connectionId = null; SendFlags(); break;
                case "control": await HandleControlAsync(root); break;
            }
        }
    }
    private async Task HandleControlAsync(JsonElement root)
    {
        if (connectionId is null || root.GetProperty("connectionId").GetString() != connectionId) return;
        long sequence = root.GetProperty("sequence").GetInt64(); if (sequence <= controlSequence) return;
        using var command = cipher!.OpenControl(MemberId, connectionId, root); controlSequence = sequence;
        await ApplyCommandAsync(connectionId, command.RootElement.Clone());
    }
    private async Task ApplyCommandAsync(string? connection, JsonElement command)
    {
        if (connection != connectionId) return;
        string? action = command.TryGetProperty("action", out var property) ? property.GetString() : null;
        if (action == "leave") { _ = Task.Run(async () => await DisposeAsync()); return; }
        if (action == "configure")
        {
            var next = command.GetProperty("options").Deserialize<AudioOptions>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (next is not null)
            {
                await Audio.ApplyAsync(next);
                profile.Save(profile.Read() with { NoiseSuppression = Audio.Options.NoiseSuppression });
            }
            if (connection != connectionId) Audio.PrivacyMute();
            else if (command.TryGetProperty("revision", out var revision)) appliedRevision = revision.GetInt64();
        }
        SendFlags(); Changed?.Invoke();
    }
    private async Task CommandLoopAsync()
    {
        try { await foreach (var command in commands.Reader.ReadAllAsync(stop.Token)) await ApplyCommandAsync(command.Connection, command.Command); }
        catch (OperationCanceledException) { }
    }
    private async Task StateLoopAsync()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                if (Connected && connectionId is { } connection)
                {
                    var inventory = Audio.Inventory;
                    Queue(cipher!.SealControl(MemberId, connection, stateSequence++, new { options = Audio.Options, noiseSuppressionAvailable = true, clientVersion = "0.11.0", microphones = inventory.Microphones, outputs = inventory.Outputs, sources = inventory.Sources.Select(s => new { s.Id, s.Name, s.Active }), status = Audio.Status, microphonePeak = Audio.MicrophonePeak, talking = Audio.Talking, streams = Audio.Mix.Statistics(), speakerPeak = Audio.Mix.Peak, appliedRevision, nativePlayback = true }, true));
                }
                await Task.Delay(500, stop.Token);
            }
        }
        catch (OperationCanceledException) { }
    }
    public ValueTask DisposeAsync() { lock (disposeGate) return new(disposal ??= Task.Run(DisposeCoreAsync)); }
    private async Task DisposeCoreAsync()
    {
        if (disposed) return; disposed = true; Audio.SetNetworkConnected(false); Audio.PrivacyMute();
        try
        {
            using var deadline = new CancellationTokenSource(1000);
            if (socket is { State: WebSocketState.Open } current) await SendAsync(current, JsonSerializer.SerializeToUtf8Bytes(new { type = "leave" }), false, deadline.Token);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException) { }
        stop.Cancel(); socket?.Abort();
        foreach (var task in new[] { sessionLoop, commandLoop, stateLoop }) if (task is not null) { try { await task; } catch (Exception) { } }
        await Audio.DisposeAsync(); stop.Dispose(); sendGate.Dispose();
    }
    private sealed class RoomDisconnected(int code) : IOException("Room connection closed (" + code + ")") { public int Code { get; } = code; }
}
