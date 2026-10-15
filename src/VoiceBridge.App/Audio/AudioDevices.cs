using NAudio.CoreAudioApi;

namespace VoiceBridge.App.Audio;
public sealed record DeviceChoice(string Id, string Name) { public override string ToString() => Name; }
public static class AudioDevices
{
    public static bool IsCable(MMDevice d) => d.DeviceFriendlyName.Contains("VB-Audio Virtual Cable", StringComparison.OrdinalIgnoreCase);
    public static bool IsCableRender(MMDevice d) => IsCable(d) && d.FriendlyName.StartsWith("CABLE Input", StringComparison.OrdinalIgnoreCase);
    public static bool IsSafeMicrophone(MMDevice d)
    {
        string name = d.FriendlyName + " " + d.DeviceFriendlyName;
        return !IsCable(d) && !new[] { "Stereo Mix", "立体声混音", "What U Hear", "Voicemeeter", "Virtual", "虚拟", "Loopback", "Monitor", "监听" }
            .Any(x => name.Contains(x, StringComparison.OrdinalIgnoreCase));
    }
    public static (DeviceChoice[] Microphones, DeviceChoice[] Cables, string? DefaultMic) List()
    {
        using var e = new MMDeviceEnumerator();
        List<DeviceChoice> microphones = [], cables = [];
        foreach (var d in e.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            using (d) if (IsSafeMicrophone(d)) microphones.Add(new(d.ID, d.FriendlyName));
        foreach (var d in e.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            using (d) if (IsCableRender(d)) cables.Add(new(d.ID, d.FriendlyName));
        string? defaultId = null;
        try { using var d = e.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications); defaultId = d.ID; }
        catch (System.Runtime.InteropServices.COMException) { }
        return (microphones.ToArray(), cables.ToArray(), defaultId);
    }
    public static void CheckPlaybackDefaults(MMDeviceEnumerator enumerator)
    {
        foreach (var role in new[] { Role.Console, Role.Multimedia, Role.Communications })
        {
            using var endpoint = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, role);
            if (IsCable(endpoint)) throw new InvalidOperationException("Windows 的默认播放设备或默认通信播放设备当前是 VB-CABLE。请先改回耳机，并将语音软件的输出设为耳机，再启动混音。");
        }
    }
    public static void CheckForeignSessions(MMDevice cable)
    {
        // Per-app output overrides can also feed the cable. Fail closed on any active foreign stream.
        var sessions = cable.AudioSessionManager.Sessions;
        for (int i = 0; i < sessions.Count; i++)
        {
            using var session = sessions[i];
            if (session.State == NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateActive &&
                session.GetProcessID != (uint)Environment.ProcessId)
                throw new InvalidOperationException("检测到其他程序正在向 VB-CABLE 输出。请将音乐播放器、游戏和语音软件的播放输出都设置为耳机，再重新启动混音。");
        }
    }
}
