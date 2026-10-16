using System.IO;
using System.Text.Json;
using VoiceBridge.App.Audio;
using NAudio.CoreAudioApi;

namespace VoiceBridge.App.Platform;
public static class Diagnostics
{
    public static void Write(string path, string? musicPlayerId = null, string? scenarioId = null, ScenarioAudioProfile? profile = null, CaptureStatistics? captureStatistics = null)
    {
        using var e = new MMDeviceEnumerator();
        List<object> endpoints = [];
        foreach (var d in e.EnumerateAudioEndPoints(DataFlow.All, DeviceState.Active)) using (d)
            endpoints.Add(new { d.ID, d.FriendlyName, d.DeviceFriendlyName, Flow = d.DataFlow.ToString(), Format = d.AudioClient.MixFormat.ToString(), Cable = AudioDevices.IsCable(d) });
        List<object> defaults = [];
        foreach (var flow in new[] { DataFlow.Render, DataFlow.Capture }) foreach (var role in new[] { Role.Console, Role.Multimedia, Role.Communications })
        {
            try { using var d = e.GetDefaultAudioEndpoint(flow, role); defaults.Add(new { Flow = flow.ToString(), Role = role.ToString(), d.ID, d.FriendlyName }); }
            catch (System.Runtime.InteropServices.COMException ex) { defaults.Add(new { Flow = flow.ToString(), Role = role.ToString(), Error = ex.Message }); }
        }
        File.WriteAllText(Path.GetFullPath(path), JsonSerializer.Serialize(new
        {
            Timestamp = DateTimeOffset.Now, OS = Environment.OSVersion.VersionString,
            ProcessLoopbackSupported = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348),
            WindowsCommunicationDucking = WindowsAudioPreferences.DuckingPreference,
            Scenario = Scenarios.Get(scenarioId ?? Settings.Load().ScenarioId).Name,
            AudioParameters = profile,
            MusicCaptureStatistics = captureStatistics,
            Endpoints = endpoints, Defaults = defaults, QQMusicRoots = QQMusicFinder.Roots(),
            SelectedMusicPlayer = MusicPlayers.GetOrDefault(musicPlayerId ?? Settings.Load().MusicPlayerId),
            MusicSources = MusicPlayers.All.Select(player => new { player.Id, player.Name, RootProcessIds = MusicProcessFinder.Roots(player.Id) }).ToArray(),
            Note = "仅设备诊断，不采集声音；不代表端到端验证通过。"
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
