using System.Diagnostics;
using System.IO;
using System.Text.Json;
using VoiceBridge.Core;

namespace VoiceBridge.App.Sharing;

// Developer/browser QA only. Synthesizes quiet test tones; never opens any audio capture/device.
public static class MusicLinkDemo
{
    public static async Task RunAsync(string reportPath, bool internet, bool useRelay = false)
    {
        using var lifetime = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        await using var server = new MusicLinkServer();
        using var tunnel = new QuickMusicTunnel();
        await server.StartAsync("合成测试音 · 未采集麦克风或播放器", lifetime.Token);
        string? publicLink = null;
        MusicRelayClient? relay = null;
        if (useRelay) { relay = await MusicRelayClient.CreateAsync(RelayProfile.Load(), "test", lifetime.Token); server.PacketPublished += relay.Publish; publicLink = relay.PublicLink; }
        void Report(string state) => File.WriteAllText(reportPath, JsonSerializer.Serialize(new { State = state, LocalLink = server.LocalLink, PublicLink = publicLink, CandidateOrigin = tunnel.CandidateOrigin, AudioCaptureOpened = false, MicrophoneOpened = false, DurationLimitSeconds = 300 }, new JsonSerializerOptions { WriteIndented = true }));
        Report(useRelay ? "RelayCreated" : "LocalReady");
        if (internet)
        {
            try
            {
                var origin = await tunnel.StartAsync(server.LocalOrigin!, server.HealthId, new Progress<string>(message => Report(message)), lifetime.Token);
                server.SetPublicOrigin(origin); publicLink = server.Invite(origin); Report("InternetReady");
            }
            catch (Exception ex) { Report("InternetFailed: " + ex.GetBaseException().Message); }
        }
        float[] samples = new float[MusicPacket.Samples]; long frame = 0; uint sequence = 0;
        var clock = Stopwatch.StartNew(); double next = 0;
        bool relayReady = false;
        while (!lifetime.IsCancellationRequested && !File.Exists(reportPath + ".stop"))
        {
            for (int i = 0; i < samples.Length; i += 2, frame++)
            {
                samples[i] = .02f * MathF.Sin((float)(2 * Math.PI * 437 * frame / MusicPacket.SampleRate));
                samples[i + 1] = .02f * MathF.Sin((float)(2 * Math.PI * 997 * frame / MusicPacket.SampleRate));
            }
            server.Publish(MusicPacket.Encode(samples, sequence++)); next += 10;
            if (relay is not null && !relayReady && relay.SentChunks > 0) { relayReady = true; Report("RelayUploading"); }
            double remaining = next - clock.Elapsed.TotalMilliseconds;
            if (remaining > 0) { try { await Task.Delay(TimeSpan.FromMilliseconds(remaining), lifetime.Token); } catch (OperationCanceledException) { break; } }
            if (next < clock.Elapsed.TotalMilliseconds - 30) next = clock.Elapsed.TotalMilliseconds;
        }
        if (relay is not null) { server.PacketPublished -= relay.Publish; await relay.DisposeAsync(); }
        Report("Stopped");
    }
}
