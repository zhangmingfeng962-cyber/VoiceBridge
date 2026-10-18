using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using VoiceBridge.Core;

namespace VoiceBridge.App.Audio;
public sealed record AudioOptions
{
    public bool Microphone { get; init; }
    public string MicrophoneId { get; init; } = "";
    public string OutputId { get; init; } = "";
    public float MicrophoneGain { get; init; } = 100;
    public float SpeakerGain { get; init; } = 100;
    public string TalkMode { get; init; } = "free";
    public int TalkKey { get; init; } = 86;
    public bool Sharing { get; init; }
    public string SourceId { get; init; } = "";
    public float MusicGain { get; init; } = 100;
    public bool AutoBuffer { get; init; } = true;
    public Dictionary<string, float> MemberVoice { get; init; } = new();
    public Dictionary<string, float> MemberMusic { get; init; } = new();
}
public sealed class DesktopAudio : IAsyncDisposable
{
    private readonly SemaphoreSlim lifecycle = new(1);
    private readonly CancellationTokenSource stop = new();
    private readonly MusicSources music = new();
    private readonly Dictionary<uint, (long Started, ProcessLoopback Capture)> captures = [];
    private readonly Action<byte, byte[]> publish;
    private WasapiCapture? microphone;
    private MMDevice? microphoneDevice, outputDevice;
    private MMDeviceEnumerator? enumerator;
    private CaptureInput? microphoneInput;
    private WasapiOut? output;
    private AudioOptions options = new();
    private AudioInventory inventory = new([], [], []);
    private Task? monitor;
    private Thread? pump;
    private bool wasTalking;
    private long privacyEpoch, configuredEpoch;
    private string selectedOutput = "", selectedMicrophone = "";
    private bool disposed;
    private readonly object disposeGate = new();
    private Task? disposal;
    public readonly RoomMix Mix = new();
    public AudioOptions Options => Volatile.Read(ref options);
    public AudioInventory Inventory => Volatile.Read(ref inventory);
    public string Status { get; private set; } = "未开启麦克风或音乐共享";
    public float MicrophonePeak { get; private set; }
    public bool Talking { get; private set; }
    public DesktopAudio(Action<byte, byte[]> publish) => this.publish = publish;
    public async Task StartAsync()
    {
        var initial = await Task.Run(AudioSources.Read); Volatile.Write(ref inventory, initial);
        monitor = Task.Run(MonitorAsync);
        pump = new Thread(Pump) { IsBackground = true, Name = "Room Opus transmit" }; pump.Start();
        // Listening starts with the room, but microphone and application capture remain off.
        await ApplyAsync(new());
    }
    public Task ApplyAsync(AudioOptions next) { long approval = Interlocked.Read(ref privacyEpoch); return Task.Run(() => ApplyCoreAsync(next, approval)); }
    private async Task ApplyCoreAsync(AudioOptions next, long approval)
    {
        if (disposed) return;
        next = next with { MicrophoneGain = Math.Clamp(float.IsFinite(next.MicrophoneGain) ? next.MicrophoneGain : 100, 0, 400), SpeakerGain = Math.Clamp(float.IsFinite(next.SpeakerGain) ? next.SpeakerGain : 100, 0, 200), MusicGain = Math.Clamp(float.IsFinite(next.MusicGain) ? next.MusicGain : 100, 0, 100), TalkMode = next.TalkMode == "ptt" ? "ptt" : "free", TalkKey = Math.Clamp(next.TalkKey, 8, 254), OutputId = next.OutputId ?? "", MicrophoneId = next.MicrophoneId ?? "", SourceId = next.SourceId ?? "", MemberVoice = next.MemberVoice ?? new(), MemberMusic = next.MemberMusic ?? new() };
        await lifecycle.WaitAsync();
        try
        {
            Mix.SpeakerGain = next.SpeakerGain / 100; Mix.AutoBuffer = next.AutoBuffer;
            foreach (var entry in next.MemberVoice.Take(8)) Mix.SetGain(entry.Key, 0, entry.Value);
            foreach (var entry in next.MemberMusic.Take(8)) Mix.SetGain(entry.Key, 1, entry.Value);
            enumerator ??= new MMDeviceEnumerator();
            string outputId = next.OutputId.Length > 0 ? next.OutputId : Inventory.Outputs.FirstOrDefault()?.Id ?? "";
            if (output is null || outputId != selectedOutput)
            {
                output?.Stop(); output?.Dispose(); outputDevice?.Dispose(); output = null;
                if (outputId.Length == 0) throw new InvalidOperationException("未找到耳机或扬声器，请连接正常听音设备。");
                outputDevice = enumerator.GetDevice(outputId);
                if (!AudioSources.SafeDevice(outputDevice.FriendlyName)) throw new InvalidOperationException("请选真实耳机或扬声器，不能将房间播放送到虚拟线缆。");
                output = new WasapiOut(outputDevice, AudioClientShareMode.Shared, true, 20); output.Init(new SampleAdapter(Mix).ToWaveProvider()); output.Play(); selectedOutput = outputId;
            }
            if (approval != Interlocked.Read(ref privacyEpoch)) next = next with { Microphone = false };
            string micId = next.MicrophoneId.Length > 0 ? next.MicrophoneId : Inventory.Microphones.FirstOrDefault()?.Id ?? "";
            string? microphoneError = null;
            try
            {
                if (!next.Microphone || micId != selectedMicrophone)
                {
                    StopMicrophone();
                    if (next.Microphone)
                    {
                        if (micId.Length == 0) throw new InvalidOperationException("未找到真实麦克风。");
                        microphoneDevice = enumerator.GetDevice(micId);
                        if (!AudioSources.SafeDevice(microphoneDevice.FriendlyName)) throw new InvalidOperationException("请选择真实麦克风，不能使用虚拟线缆或立体声混音。");
                        microphone = new WasapiCapture(microphoneDevice, true, 10); var input = new CaptureInput(microphone.WaveFormat); Volatile.Write(ref microphoneInput, input);
                        microphone.DataAvailable += (_, data) => input.Add(data.Buffer, data.BytesRecorded);
                        microphone.RecordingStopped += (_, data) => { if (data.Exception is not null) Status = "麦克风中断：" + data.Exception.Message; };
                        microphone.StartRecording(); selectedMicrophone = micId;
                    }
                }
            }
            catch (Exception ex) { StopMicrophone(); next = next with { Microphone = false }; microphoneError = "麦克风未开启：" + ex.GetBaseException().Message; }
            Interlocked.Exchange(ref configuredEpoch, approval);
            if (approval != Interlocked.Read(ref privacyEpoch)) { StopMicrophone(); next = next with { Microphone = false }; }
            Volatile.Write(ref options, next with { OutputId = outputId, MicrophoneId = micId });
            try
            {
                if (next.Sharing && !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348)) throw new PlatformNotSupportedException("程序音源共享需要 Windows 11；也可在浏览器网页模式共享标签页音频。");
                await SyncSourcesAsync(next);
            }
            catch (Exception ex) { await StopSourcesAsync(); Volatile.Write(ref options, Options with { Sharing = false }); Status = "音乐未共享：" + ex.GetBaseException().Message; return; }
            Status = microphoneError ?? (next.Sharing ? "正在共享所选程序 · 房间声音由客户端独立播放" : "已连接房间 · 音乐共享关闭");
        }
        catch (Exception ex)
        {
            StopMicrophone(); Volatile.Write(ref options, next with { Microphone = false, Sharing = false }); await StopSourcesAsync(); Status = ex.GetBaseException().Message;
        }
        finally { lifecycle.Release(); }
    }
    private void StopMicrophone()
    {
        Volatile.Write(ref microphoneInput, null); Talking = false; wasTalking = false;
        var capture = Interlocked.Exchange(ref microphone, null); var device = Interlocked.Exchange(ref microphoneDevice, null);
        try { capture?.StopRecording(); } catch { }
        capture?.Dispose(); device?.Dispose(); selectedMicrophone = "";
    }
    public void PrivacyMute() { Interlocked.Increment(ref privacyEpoch); Volatile.Write(ref options, Options with { Microphone = false }); StopMicrophone(); }
    private async Task SyncSourcesAsync(AudioOptions value)
    {
        var source = value.Sharing ? Inventory.Sources.FirstOrDefault(x => x.Id == value.SourceId) : null;
        var targets = source?.Targets ?? [];
        foreach (var id in captures.Keys.ToArray())
        {
            if (!targets.Any(t => t.Pid == id && t.Started == captures[id].Started))
            { var old = captures[id]; captures.Remove(id); music.Replace(captures.Values.Select(c => c.Capture.Queue)); old.Capture.Dispose(); }
        }
        foreach (var target in targets.Where(t => !captures.ContainsKey(t.Pid)))
        {
            if (target.Pid == Environment.ProcessId) continue;
            var capture = new ProcessLoopback(target.Pid);
            try { await capture.StartAsync(); captures[target.Pid] = (target.Started, capture); music.Replace(captures.Values.Select(c => c.Capture.Queue)); }
            catch { capture.Dispose(); throw; }
        }
        if (value.Sharing && source is null) Status = "等待所选程序恢复音频；也可在网页切换来源";
    }
    private Task StopSourcesAsync()
    { music.Replace([]); foreach (var capture in captures.Values) capture.Capture.Dispose(); captures.Clear(); return Task.CompletedTask; }
    private async Task MonitorAsync()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                await Task.Delay(2000, stop.Token); var current = await Task.Run(AudioSources.Read, stop.Token); Volatile.Write(ref inventory, current);
                await lifecycle.WaitAsync(stop.Token);
                try { await SyncSourcesAsync(Options); } catch (Exception ex) { Status = "来源暂不可用：" + ex.GetBaseException().Message; }
                finally { lifecycle.Release(); }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Status = "音源检测中断：" + ex.GetBaseException().Message; }
    }
    private void Pump()
    {
        using var voiceEncoder = new RoomEncoder(false); using var musicEncoder = new RoomEncoder(true);
        var clock = Stopwatch.StartNew(); double next = 20; float[] stereo = new float[1920], mono = new float[960];
        while (!stop.IsCancellationRequested)
        {
            double remaining = next - clock.Elapsed.TotalMilliseconds;
            if (remaining > 0 && stop.Token.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(remaining))) break;
            var current = Options; var input = Volatile.Read(ref microphoneInput);
            bool talking = Interlocked.Read(ref configuredEpoch) == Interlocked.Read(ref privacyEpoch) && current.Microphone && input is not null && (current.TalkMode == "free" || (GetAsyncKeyState(current.TalkKey) & 0x8000) != 0);
            Talking = talking;
            try
            {
                if (talking)
                {
                    if (!wasTalking) input!.Queue.Clear(); input!.Stereo.Read(stereo, 0, stereo.Length); float peak = 0;
                    for (int i = 0; i < mono.Length; i++) { float sample = (stereo[i * 2] + stereo[i * 2 + 1]) * .5f * current.MicrophoneGain / 100; peak = Math.Max(peak, Math.Abs(sample)); mono[i] = Math.Clamp(float.IsFinite(sample) ? sample : 0, -.95f, .95f); }
                    MicrophonePeak = peak; publish(0, voiceEncoder.Encode(mono));
                }
                else { MicrophonePeak = 0; if (wasTalking) input?.Queue.Clear(); }
                wasTalking = talking;
                if (current.Sharing)
                {
                    music.Read(stereo, 0, stereo.Length); for (int i = 0; i < stereo.Length; i++) stereo[i] = float.IsFinite(stereo[i]) ? Math.Clamp(stereo[i] * current.MusicGain / 100, -1, 1) : 0;
                    publish(1, musicEncoder.Encode(stereo));
                }
            }
            catch (Exception ex) { Status = "音频发送中断：" + ex.GetBaseException().Message; }
            next += 20; if (next < clock.Elapsed.TotalMilliseconds - 40) next = clock.Elapsed.TotalMilliseconds + 20;
        }
    }
    public ValueTask DisposeAsync() { lock (disposeGate) return new(disposal ??= Task.Run(DisposeCoreAsync)); }
    private async Task DisposeCoreAsync()
    {
        if (disposed) return; disposed = true; stop.Cancel(); if (monitor is not null) await monitor; pump?.Join(TimeSpan.FromSeconds(3));
        await lifecycle.WaitAsync(); try { StopMicrophone(); await StopSourcesAsync(); output?.Stop(); output?.Dispose(); outputDevice?.Dispose(); enumerator?.Dispose(); Mix.Dispose(); } finally { lifecycle.Release(); stop.Dispose(); }
    }
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
}
