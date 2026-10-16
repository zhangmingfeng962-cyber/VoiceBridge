using NAudio.CoreAudioApi;
using NAudio.Wave;
using VoiceBridge.App.Platform;
using VoiceBridge.Core;

namespace VoiceBridge.App.Audio;
public sealed class AudioEngine : IDisposable
{
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private readonly Dictionary<uint, ProcessLoopback> captures = [];
    private readonly MusicSources music = new();
    private MMDeviceEnumerator? enumerator;
    private MMDevice? micDevice, cableDevice;
    private WasapiCapture? microphone;
    private WasapiOut? output;
    private CaptureInput? micInput;
    private CancellationTokenSource? cancellation;
    private Task? discovery;
    private Exception? failure;
    private string musicStatus = "等待启动";
    private ProcessLoopback[] telemetryCaptures = [];
    public MixBus? Mixer { get; private set; }
    public bool Running { get; private set; }
    public string MusicStatus => Volatile.Read(ref musicStatus);
    public Exception? Failure => Volatile.Read(ref failure);
    public double MicrophoneQueueMs => micInput?.Queue.BufferedMilliseconds ?? 0;
    public long MicDropped => micInput?.Queue.DroppedSamples ?? 0;
    public long MicMissing => micInput?.Queue.MissingSamples ?? 0;
    public CaptureStatistics MusicStatistics()
    {
        var sources = Volatile.Read(ref telemetryCaptures);
        return new(sources.Length, sources.Sum(x => x.Queue.MissingSamples) / 96.0,
            sources.Sum(x => x.Queue.DroppedSamples) / 96.0, sources.Sum(x => Interlocked.Read(ref x.Discontinuities)));
    }

    public async Task StartAsync(string micId, string cableId, float micGain, float musicGain, bool microphoneEnabled = true, bool musicEnabled = false, string musicPlayerId = MusicPlayers.QQMusicId, MusicChannelMode channelMode = MusicChannelMode.Stereo, int outputBufferMs = 20)
    {
        if (!Enum.IsDefined(channelMode)) throw new ArgumentOutOfRangeException(nameof(channelMode));
        if (outputBufferMs is not (20 or 40 or 60)) throw new ArgumentOutOfRangeException(nameof(outputBufferMs));
        var player = MusicPlayers.Get(musicPlayerId);
        await lifecycle.WaitAsync();
        try
        {
            if (Running) return;
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348))
                throw new PlatformNotSupportedException("进程音频捕获需要 Windows Build 20348 或更新版本。普通 Win10 19045 不支持；请在 Win11 使用。");
            failure = null;
            enumerator = new MMDeviceEnumerator();
            AudioDevices.CheckPlaybackDefaults(enumerator);
            micDevice = enumerator.GetDevice(micId);
            cableDevice = enumerator.GetDevice(cableId);
            if (!AudioDevices.IsSafeMicrophone(micDevice)) throw new InvalidOperationException("请选择真实麦克风；禁止使用立体声混音、虚拟线缆或监听输入。");
            if (!AudioDevices.IsCableRender(cableDevice)) throw new InvalidOperationException("输出必须为官方 VB-CABLE 的 CABLE Input。");
            if (cableDevice.AudioClient.MixFormat.SampleRate < 44100) throw new InvalidOperationException("请将 CABLE Input 的共享格式设为 44.1 kHz 或 48 kHz。");
            AudioDevices.CheckForeignSessions(cableDevice);
            microphone = new WasapiCapture(micDevice, true, 10);
            micInput = new CaptureInput(microphone.WaveFormat);
            Mixer = new MixBus(micInput.Stereo, music) { MicGain = micGain, MusicGain = musicGain, MicrophoneEnabled = microphoneEnabled, MusicEnabled = musicEnabled, ChannelMode = channelMode };
            microphone.DataAvailable += OnMicrophoneData;
            microphone.RecordingStopped += (_, args) => { if (args.Exception is not null) Fail(args.Exception); };
            output = new WasapiOut(cableDevice, AudioClientShareMode.Shared, true, outputBufferMs);
            output.PlaybackStopped += (_, args) => { if (args.Exception is not null) Fail(args.Exception); };
            output.Init(new SampleAdapter(Mixer).ToWaveProvider());
            microphone.StartRecording();
            output.Play();
            Running = true;
            cancellation = new CancellationTokenSource();
            var token = cancellation.Token;
            discovery = Task.Run(() => WatchAsync(micId, cableId, player, token));
            Log.Write($"Audio engine started, 48000 Hz stereo float mix; player={player.Id}; voice={microphoneEnabled}; music={musicEnabled}; channel={channelMode}; renderBuffer={outputBufferMs}ms; fixed headroom, no music ducking.");
        }
        catch { Cleanup(); throw; }
        finally { lifecycle.Release(); }
    }
    private void OnMicrophoneData(object? sender, WaveInEventArgs args)
    {
        try { micInput?.Add(args.Buffer, args.BytesRecorded); } catch (Exception ex) { Fail(ex); }
    }
    private void Fail(Exception ex)
    {
        Interlocked.CompareExchange(ref failure, ex, null);
        if (Mixer is not null) Mixer.MusicEnabled = false;
        Log.Write(ex.ToString());
    }
    private async Task WatchAsync(string micId, string cableId, MusicPlayer player, CancellationToken token)
    {
        try
        {
            // IMMDevice interfaces created in WPF's STA cannot be reused on the MTA monitor.
            // Open all monitor COM objects in this apartment and pass endpoint IDs across threads.
            using var monitorEnumerator = new MMDeviceEnumerator();
            using var monitorMic = monitorEnumerator.GetDevice(micId);
            using var monitorCable = monitorEnumerator.GetDevice(cableId);
            while (!token.IsCancellationRequested && Failure is null)
            {
                AudioDevices.CheckPlaybackDefaults(monitorEnumerator);
                // Use a fresh endpoint wrapper so its session collection includes newly created streams.
                using (var currentCable = monitorEnumerator.GetDevice(cableId)) AudioDevices.CheckForeignSessions(currentCable);
                if (monitorMic.State != DeviceState.Active || monitorCable.State != DeviceState.Active)
                    throw new InvalidOperationException("音频设备已断开或禁用。请重新选择设备并启动。");
                var roots = MusicProcessFinder.Roots(player.Id);
                foreach (var pid in captures.Keys.Where(pid => !roots.Contains(pid)).ToArray())
                {
                    var removed = captures[pid]; captures.Remove(pid);
                    PublishCaptures();
                    removed.Dispose();
                }
                foreach (uint pid in roots.Where(pid => !captures.ContainsKey(pid)))
                {
                    token.ThrowIfCancellationRequested();
                    var capture = new ProcessLoopback(pid);
                    try { await capture.StartAsync(); captures.Add(pid, capture); }
                    catch { capture.Dispose(); throw; }
                    PublishCaptures();
                }
                foreach (var capture in captures.Values)
                    if (capture.Failure is not null) throw new InvalidOperationException($"{player.Name}捕获中断，请重新启动混音。", capture.Failure);
                Volatile.Write(ref musicStatus, roots.Length == 0 ? $"等待{player.Name} · 麦克风可正常传输" : $"已连接{player.Name} · {roots.Length} 个进程树");
                await Task.Delay(1000, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { Fail(ex); }
    }
    public async Task StopAsync()
    {
        await lifecycle.WaitAsync();
        try
        {
            if (Mixer is not null) Mixer.MusicEnabled = false;
            cancellation?.Cancel();
            if (discovery is not null) await discovery.ConfigureAwait(false);
            Cleanup();
        }
        finally { lifecycle.Release(); }
    }
    private void PublishCaptures()
    {
        var snapshot = captures.Values.ToArray();
        music.Replace(snapshot.Select(c => c.Queue));
        Volatile.Write(ref telemetryCaptures, snapshot);
    }
    private void Cleanup()
    {
        Running = false;
        if (microphone is not null) microphone.DataAvailable -= OnMicrophoneData;
        try { output?.Stop(); } catch (Exception ex) { Log.Write(ex.Message); }
        try { microphone?.StopRecording(); } catch (Exception ex) { Log.Write(ex.Message); }
        output?.Dispose(); output = null;
        microphone?.Dispose(); microphone = null;
        music.Replace([]);
        Volatile.Write(ref telemetryCaptures, []);
        foreach (var c in captures.Values) c.Dispose();
        captures.Clear();
        micInput = null; Mixer = null;
        micDevice?.Dispose(); micDevice = null; cableDevice?.Dispose(); cableDevice = null;
        enumerator?.Dispose(); enumerator = null;
        cancellation?.Dispose(); cancellation = null; discovery = null;
        Volatile.Write(ref musicStatus, "已停止");
    }
    public void Dispose() => StopAsync().GetAwaiter().GetResult();
}

public sealed record CaptureStatistics(int Sources, double MissingMilliseconds, double DroppedMilliseconds, long Discontinuities);
