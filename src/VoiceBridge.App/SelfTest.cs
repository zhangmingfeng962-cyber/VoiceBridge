using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using System.Windows;
using VoiceBridge.App.Audio;
using VoiceBridge.App.Room;
using VoiceBridge.Core;

namespace VoiceBridge.App;
// Opt-in diagnostics synthesize signals only. They never open a microphone, audio device or process capture.
public static class SelfTest
{
    private static string? Argument(string[] args, string key) => args.SkipWhile(x => x != key).Skip(1).FirstOrDefault();
    public static async Task RunAsync(string[] args)
    {
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown; string? report = Argument(args, "--self-test");
        if (report is null) { Application.Current.Shutdown(2); return; }
        try
        {
            if (args.Contains("--inventory")) { var inventory = await Task.Run(AudioSources.Read); File.WriteAllText(report, JsonSerializer.Serialize(new { Inventory = inventory, MicrophoneOpened = false, ProcessCaptureOpened = false }, RoomCipher.JsonOptions)); }
            else if (Argument(args, "--origin") is { } origin) await StreamAsync(origin, report);
            else
            {
                using var encoder = new RoomEncoder(true); using var decoder = new RoomDecoder(true); float[] pcm = new float[1920]; long frame = 0; double energy = 0;
                for (int n = 0; n < 50; n++) { Signal(pcm, ref frame); var result = decoder.Decode(encoder.Encode(pcm)); energy += result.Sum(x => x * x); }
                File.WriteAllText(report, JsonSerializer.Serialize(new { Codec = "Opus 48 kHz stereo", Frames = 50, DecodedEnergy = energy, MicrophoneOpened = false, ProcessCaptureOpened = false }));
            }
            Application.Current.Shutdown(0);
        }
        catch (Exception ex) { File.WriteAllText(report + ".error", ex.ToString()); Application.Current.Shutdown(1); }
    }
    private static void Signal(float[] stereo, ref long frame, double frequency = 437)
    { for (int i = 0; i < stereo.Length; i += 2, frame++) { stereo[i] = .03f * MathF.Sin((float)(2 * Math.PI * frequency * frame / 48000)); stereo[i + 1] = .02f * MathF.Sin((float)(2 * Math.PI * 997 * frame / 48000)); } }
    private static async Task StreamAsync(string origin, string report)
    {
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(120)); using var client = new HttpClient { BaseAddress = new Uri(origin), Timeout = TimeSpan.FromSeconds(12) };
        string? master;
        if (new Uri(origin).IsLoopback) master = new string('1', 64);
        else { if (origin != RoomService.DefaultOrigin) throw new InvalidOperationException("Diagnostic origin must match the built-in public relay."); master = null; }
        if (master is not null) client.DefaultRequestHeaders.Authorization = new("Bearer", master);
        using var response = await client.PostAsync("/api/rooms", new StringContent("{}"), lifetime.Token); response.EnsureSuccessStatusCode(); using var created = JsonDocument.Parse(await response.Content.ReadAsStringAsync(lifetime.Token));
        string roomId = created.RootElement.GetProperty("id").GetString()!, writer = created.RootElement.GetProperty("writeToken").GetString()!, token = created.RootElement.GetProperty("listenToken").GetString()!;
        var invite = new RoomInvite(origin, roomId, token, RandomNumberGenerator.GetBytes(32)); var cipher = new RoomCipher(invite.Key, roomId);
        using var publisher = new ClientWebSocket(); publisher.Options.SetRequestHeader("Origin", origin); await publisher.ConnectAsync(new Uri(origin.Replace("https:", "wss:").Replace("http:", "ws:") + $"/api/rooms/{roomId}/voice"), lifetime.Token);
        async Task Send(object data) => await publisher.SendAsync(JsonSerializer.SerializeToUtf8Bytes(data, RoomCipher.JsonOptions).AsMemory(), WebSocketMessageType.Text, true, lifetime.Token);
        await Send(new { type = "join", version = 3, role = "desktop", token, name = "合成验证 · 无设备采集", hostToken = writer });
        var welcome = await ReadJsonAsync(publisher, lifetime.Token); string id = welcome.GetProperty("id").GetString()!, control = welcome.GetProperty("controlToken").GetString()!;
        var controlCommands = Channel.CreateUnbounded<JsonElement>(); string? connection = null; long lastControl = -1, stateSeq = 0, appliedRevision = 0; AudioOptions options = new() { Sharing = true, SourceId = "synthetic-a" }; uint[] seq = [0, 0]; long frames = 0, audioBytes = 0;
        void Report(string state) => File.WriteAllText(report, JsonSerializer.Serialize(new { State = state, RoomLink = invite.Link, ControllerLink = invite.ControllerLink(id, control), MusicFrames = frames, AudioBytes = audioBytes, Options = options, MicrophoneOpened = false, ProcessCaptureOpened = false, SyntheticOnly = true, MaxSeconds = 120 }, RoomCipher.JsonOptions));
        var read = Task.Run(async () =>
        {
            try
            {
                while (!lifetime.IsCancellationRequested)
                {
                    var message = await ReadJsonAsync(publisher, lifetime.Token); string? type = message.GetProperty("type").GetString();
                    if (type == "controllerAttached") { connection = message.GetProperty("connectionId").GetString(); stateSeq = 0; lastControl = -1; options = options with { Microphone = false }; }
                    if (type == "controllerDetached") { connection = null; options = options with { Microphone = false }; }
                    if (type == "control" && connection is not null && message.GetProperty("connectionId").GetString() == connection)
                    {
                        long current = message.GetProperty("sequence").GetInt64(); if (current <= lastControl) continue;
                        using var command = cipher.OpenControl(id, connection, message); lastControl = current; controlCommands.Writer.TryWrite(command.RootElement.Clone());
                    }
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException) { }
        });
        using var musicEncoder = new RoomEncoder(true); using var voiceEncoder = new RoomEncoder(false); float[] music = new float[1920], voice = new float[960]; long frame = 0; var clock = Stopwatch.StartNew(); double next = 0; long lastState = -1000;
        try
        {
            await Send(new { type = "flags", microphone = false, music = true, source = "合成音源 A" }); Report("Streaming");
            while (!lifetime.IsCancellationRequested && !File.Exists(report + ".stop"))
            {
                while (controlCommands.Reader.TryRead(out var command))
                {
                    if (command.GetProperty("action").GetString() != "configure") continue;
                    options = command.GetProperty("options").Deserialize<AudioOptions>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!; appliedRevision = command.GetProperty("revision").GetInt64();
                    await Send(new { type = "flags", microphone = options.Microphone, music = options.Sharing, source = options.SourceId == "synthetic-b" ? "合成音源 B" : "合成音源 A" }); Report("Streaming");
                }
                if (options.Sharing)
                {
                    Signal(music, ref frame, options.SourceId == "synthetic-b" ? 660 : 437); for (int i = 0; i < music.Length; i++) music[i] *= options.MusicGain / 100;
                    byte[] packet = cipher.SealAudio(1, id, seq[1]++, musicEncoder.Encode(music)); await publisher.SendAsync(packet.AsMemory(), WebSocketMessageType.Binary, true, lifetime.Token); frames++; audioBytes += packet.Length;
                }
                if (options.Microphone && options.TalkMode == "free")
                {
                    for (int i = 0; i < voice.Length; i++) voice[i] = .02f * MathF.Sin((float)((frame + i) * .07)) * options.MicrophoneGain / 100;
                    byte[] packet = cipher.SealAudio(0, id, seq[0]++, voiceEncoder.Encode(voice)); await publisher.SendAsync(packet.AsMemory(), WebSocketMessageType.Binary, true, lifetime.Token);
                }
                if (connection is { } conn && clock.ElapsedMilliseconds - lastState > 400)
                {
                    await Send(cipher.SealControl(id, conn, stateSeq++, new { options, microphones = new[] { new { id = "test-microphone", name = "合成输入 · 不使用真实麦克风" } }, outputs = new[] { new { id = "test-output", name = "合成验证 · 不播放声音" } }, sources = new[] { new { id = "synthetic-a", name = "合成音源 A", active = true }, new { id = "synthetic-b", name = "合成音源 B", active = true } }, status = "合成验证：未采集麦克风或播放器", microphonePeak = options.Microphone ? .1 : 0, speakerPeak = 0, appliedRevision, streams = Array.Empty<object>(), nativePlayback = true }, true)); lastState = clock.ElapsedMilliseconds;
                }
                next += 20; double wait = next - clock.Elapsed.TotalMilliseconds; if (wait > 0) { try { await Task.Delay(TimeSpan.FromMilliseconds(wait), lifetime.Token); } catch (OperationCanceledException) { break; } }
                if (next < clock.Elapsed.TotalMilliseconds - 40) next = clock.Elapsed.TotalMilliseconds + 20;
            }
        }
        finally
        {
            lifetime.Cancel(); publisher.Abort(); try { await read; } catch { }
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", writer); using var deleted = await client.DeleteAsync($"/api/rooms/{roomId}"); Report("Stopped");
        }
    }
    private static async Task<JsonElement> ReadJsonAsync(ClientWebSocket socket, CancellationToken token)
    {
        byte[] buffer = new byte[96000]; while (true)
        {
            int count = 0; ValueWebSocketReceiveResult result; do { result = await socket.ReceiveAsync(buffer.AsMemory(count), token); if (result.MessageType == WebSocketMessageType.Close) throw new WebSocketException("Closed"); count += result.Count; } while (!result.EndOfMessage);
            if (result.MessageType != WebSocketMessageType.Text) continue; using var document = JsonDocument.Parse(buffer.AsMemory(0, count)); return document.RootElement.Clone();
        }
    }
}
