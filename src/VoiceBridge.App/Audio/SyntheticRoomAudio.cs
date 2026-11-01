using System.Diagnostics;
using VoiceBridge.Core;
namespace VoiceBridge.App.Audio;
// Explicit diagnostics only. Never opens WASAPI, a microphone or a process audio capture.
public sealed class SyntheticRoomAudio(Action<byte,byte[]> publish) : IRoomAudio
{
    private readonly CancellationTokenSource stop = new();
    private AudioOptions options = new() { Microphone = true, Sharing = true, NoiseSuppression = false, SourceId = "synthetic-music" };
    private volatile bool connected, available = true;
    private Task? pump;
    public AudioOptions Options => Volatile.Read(ref options);
    public AudioInventory Inventory { get; } = new([new("synthetic","合成麦克风 · 无设备采集")], [new("synthetic","合成输出 · 不播放声音")], [new("synthetic-music","合成立体声测试音",true,[])]);
    public RoomMix Mix { get; } = new();
    public string Status => "合成音频验证：没有设备采集";
    public float MicrophonePeak => Talking ? .015f : 0;
    public bool Talking => connected && available && Options.Microphone && Options.TalkMode == "free";
    public bool MicrophoneReady => available;
    public bool OutputReady => available;
    public long SentFrames { get; private set; }
    public double ReceivedEnergy { get; private set; }
    public Task StartAsync() { pump = Task.Run(PumpAsync); return Task.CompletedTask; }
    public Task ApplyAsync(AudioOptions value) { Volatile.Write(ref options, value); return Task.CompletedTask; }
    public void PrivacyMute() => Volatile.Write(ref options, Options with { Microphone = false });
    public void SetNetworkConnected(bool value) => connected = value;
    public async Task InterruptDeviceAsync() { available = false; await Task.Delay(700, stop.Token); available = true; }
    private async Task PumpAsync()
    {
        using var voice = new RoomEncoder(false); using var music = new RoomEncoder(true); using var scheduling = new AudioThread();
        var clock = Stopwatch.StartNew(); long sample = 0; double next = 20; float[] mono = new float[960], stereo = new float[1920], output = new float[1920];
        try { while (!stop.IsCancellationRequested)
        {
            var current = Options;
            for (int i = 0; i < 960; i++, sample++) { mono[i] = .015f * MathF.Sin((float)(sample * 2 * Math.PI * 220 / 48000)); stereo[i*2] = .02f * MathF.Sin((float)(sample * 2 * Math.PI * 437 / 48000)); stereo[i*2+1] = .012f * MathF.Sin((float)(sample * 2 * Math.PI * 997 / 48000)); }
            if (Talking) { publish(0, voice.Encode(mono)); SentFrames++; }
            if (connected && available && current.Sharing) { publish(1, music.Encode(stereo)); SentFrames++; }
            Mix.Read(output, 0, output.Length); ReceivedEnergy += output.Sum(value => (double)value * value);
            double delay = next - clock.Elapsed.TotalMilliseconds; if (delay > 0) await Task.Delay(TimeSpan.FromMilliseconds(delay), stop.Token);
            next += 20; if (next < clock.Elapsed.TotalMilliseconds - 60) next = clock.Elapsed.TotalMilliseconds + 20;
        } } catch (OperationCanceledException) { }
    }
    public async ValueTask DisposeAsync() { stop.Cancel(); if (pump is not null) await pump; Mix.Dispose(); stop.Dispose(); }
}
