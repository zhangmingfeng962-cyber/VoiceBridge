using System.Diagnostics;
using VoiceBridge.App.Audio;
using VoiceBridge.App.Platform;
using VoiceBridge.Core;

namespace VoiceBridge.App.Sharing;

// Independent process capture: neither the microphone nor a system/cable loopback is opened.
public sealed class MusicBroadcastSession : IAsyncDisposable
{
    private readonly CancellationTokenSource stop = new();
    private readonly MusicSources music = new();
    private readonly Dictionary<uint, ProcessLoopback> captures = [];
    private ProcessLoopback[] snapshot = [];
    private Task? discovery;
    private Thread? pump;
    private QuickMusicTunnel? tunnel;
    private Exception? failure;
    private long packets, invalidSamples, overloadedSamples;
    private int disposed;
    private string status = "未开始";
    public MusicLinkServer Server { get; } = new();
    public string? PublicLink { get; private set; }
    public Exception? Failure => Volatile.Read(ref failure);
    public bool TunnelExited => tunnel?.HasExited == true;
    public string Status => Volatile.Read(ref status);
    public long Packets => Interlocked.Read(ref packets);
    public long InvalidSamples => Interlocked.Read(ref invalidSamples);
    public long OverloadedSamples => Interlocked.Read(ref overloadedSamples);
    public CaptureStatistics Statistics()
    {
        var current = Volatile.Read(ref snapshot);
        return new(current.Length, current.Sum(x => x.Queue.MissingSamples) / 96.0, current.Sum(x => x.Queue.DroppedSamples) / 96.0, current.Sum(x => Interlocked.Read(ref x.Discontinuities)));
    }
    public async Task StartAsync(string playerId, CancellationToken token)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348)) throw new PlatformNotSupportedException("音乐进程捕获需要 Windows 11。");
        var player = MusicPlayers.Get(playerId);
        await Server.StartAsync(player.Name, token).ConfigureAwait(false);
        discovery = Task.Run(() => DiscoverAsync(player, stop.Token));
        pump = new Thread(Pump) { IsBackground = true, Name = "Independent music PCM" };
        pump.Start();
    }
    private async Task DiscoverAsync(MusicPlayer player, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var roots = MusicProcessFinder.Roots(player.Id);
                foreach (uint pid in captures.Keys.Where(id => !roots.Contains(id)).ToArray())
                {
                    var old = captures[pid]; captures.Remove(pid); Replace(); old.Dispose();
                }
                foreach (uint pid in roots.Where(id => !captures.ContainsKey(id)))
                {
                    token.ThrowIfCancellationRequested();
                    var capture = new ProcessLoopback(pid);
                    try { await capture.StartAsync().ConfigureAwait(false); captures.Add(pid, capture); }
                    catch { capture.Dispose(); throw; }
                    Replace();
                }
                foreach (var source in captures.Values) if (source.Failure is { } error) throw new InvalidOperationException("音乐捕获已中断，请停止分享后重试。", error);
                Volatile.Write(ref status, roots.Length == 0 ? $"等待{player.Name}播放" : $"已连接{player.Name} · {roots.Length} 个进程树");
                await Task.Delay(1000, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { Interlocked.CompareExchange(ref failure, ex, null); stop.Cancel(); }
    }
    private void Replace()
    {
        var current = captures.Values.ToArray(); music.Replace(current.Select(x => x.Queue)); Volatile.Write(ref snapshot, current);
    }
    private void Pump()
    {
        try
        {
            float[] samples = new float[MusicPacket.Samples]; uint sequence = 0;
            var clock = Stopwatch.StartNew(); double next = 30; // Initial capture jitter allowance, independent of local speaker clocks.
            while (!stop.IsCancellationRequested)
            {
                double remaining = next - clock.Elapsed.TotalMilliseconds;
                if (remaining > 0 && stop.Token.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(remaining))) return;
                if (stop.IsCancellationRequested) return;
                music.Read(samples, 0, samples.Length);
                long invalid = 0, overloaded = 0;
                foreach (float value in samples) { if (!float.IsFinite(value)) invalid++; else if (Math.Abs(value) > 1) overloaded++; }
                Interlocked.Add(ref invalidSamples, invalid); Interlocked.Add(ref overloadedSamples, overloaded);
                Server.Publish(MusicPacket.Encode(samples, sequence++)); Interlocked.Increment(ref packets);
                next += 10;
                // After a suspended/overloaded machine, resume live rather than replaying a burst of old audio.
                if (next < clock.Elapsed.TotalMilliseconds - 30) next = clock.Elapsed.TotalMilliseconds + 10;
            }
        }
        catch (Exception ex) { Interlocked.CompareExchange(ref failure, ex, null); stop.Cancel(); }
    }
    public async Task CreateInternetLinkAsync(IProgress<string>? progress, CancellationToken token)
    {
        if (tunnel is not null) throw new InvalidOperationException("临时链接已经创建，请停止分享后重新开始。");
        if (Server.LocalOrigin is null) throw new InvalidOperationException("请先开始独立分享。");
        var candidate = new QuickMusicTunnel();
        try
        {
            Uri origin = await candidate.StartAsync(Server.LocalOrigin, Server.HealthId, progress, token).ConfigureAwait(false);
            Server.SetPublicOrigin(origin); PublicLink = Server.Invite(origin); tunnel = candidate;
        }
        catch { candidate.Dispose(); throw; }
    }
    public void UseHttpsOrigin(Uri origin)
    {
        if (tunnel is not null) throw new InvalidOperationException("请先停止独立分享，取消临时链接并重新开始，再接入固定 HTTPS 地址。");
        Server.SetPublicOrigin(origin); PublicLink = Server.Invite(origin);
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        stop.Cancel(); tunnel?.Dispose();
        if (discovery is not null) { try { await discovery.ConfigureAwait(false); } catch (OperationCanceledException) { } }
        if (pump is not null) await Task.Run(pump.Join).ConfigureAwait(false);
        music.Replace([]); Volatile.Write(ref snapshot, []);
        foreach (var capture in captures.Values) capture.Dispose(); captures.Clear();
        await Server.DisposeAsync().ConfigureAwait(false); stop.Dispose();
    }
}
