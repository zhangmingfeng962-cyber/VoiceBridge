using System.IO;
using System.Text.Json;
using NAudio.CoreAudioApi;
using VoiceBridge.App.Audio;

namespace VoiceBridge.App.Platform;

public static class EngineProbe
{
    // Explicit command-line diagnostic: briefly uses the production microphone/mix engine.
    // Refuse if another app is listening to the cable. Never writes audio samples to disk.
    public static async Task WriteAsync(string path)
    {
        using (var enumerator = new MMDeviceEnumerator())
        foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)) using (d)
        {
            if (!AudioDevices.IsCable(d)) continue;
            var sessions = d.AudioSessionManager.Sessions;
            for (int i = 0; i < sessions.Count; i++) using (var s = sessions[i])
                if (s.State == NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateActive)
                    throw new InvalidOperationException("虚拟麦克风正被其他程序使用，已取消启动诊断。请先退出通话或录音。");
        }
        var devices = AudioDevices.List();
        var settings = Settings.Load();
        var mic = devices.Microphones.FirstOrDefault(x => x.Id == settings.MicrophoneId) ?? devices.Microphones.FirstOrDefault(x => x.Id == devices.DefaultMic) ?? devices.Microphones.FirstOrDefault() ?? throw new InvalidOperationException("没有真实麦克风。");
        var cable = devices.Cables.FirstOrDefault() ?? throw new InvalidOperationException("没有 VB-CABLE。");
        var engine = new AudioEngine();
        object? report = null;
        try
        {
            await engine.StartAsync(mic.Id, cable.Id, 1, .35f);
            await Task.Delay(2200);
            if (engine.Failure is not null) throw engine.Failure;
            report = new
            {
                Timestamp = DateTimeOffset.Now,
                Version = BuildInfo.Version,
                Started = engine.Running,
                Microphone = mic.Name,
                Cable = cable.Name,
                QQMusic = engine.MusicStatus,
                MusicShared = engine.Mixer?.MusicEnabled,
                MicrophoneShared = engine.Mixer?.MicrophoneEnabled,
                MicPeak = engine.Mixer?.MicPeak,
                MicrophoneQueueMs = engine.MicrophoneQueueMs,
                MicDropped = engine.MicDropped,
                MicMissing = engine.MicMissing,
                Note = "生产引擎短时启动/停止验证；音乐共享保持关闭，没有保存录音。不代表接收端或音质验收。"
            };
        }
        finally { await engine.StopAsync(); }
        File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }
}
