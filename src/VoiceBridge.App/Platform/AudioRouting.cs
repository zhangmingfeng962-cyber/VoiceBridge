using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using NAudio.CoreAudioApi;
using VoiceBridge.App.Audio;

namespace VoiceBridge.App.Platform;

public sealed record AudioRoute(string Flow, string Role, string Id, string Name);
public sealed record RouteRepair(string PlaybackName, string? BackupPath, int ChangedRoles);

public static class AudioRouting
{
    public static AudioRoute[] Snapshot()
    {
        using var e = new MMDeviceEnumerator();
        List<AudioRoute> routes = [];
        foreach (var flow in new[] { DataFlow.Render, DataFlow.Capture })
        foreach (var role in new[] { Role.Console, Role.Multimedia, Role.Communications })
        {
            using var d = e.GetDefaultAudioEndpoint(flow, role);
            routes.Add(new(flow.ToString(), role.ToString(), d.ID, d.FriendlyName));
        }
        return routes.ToArray();
    }

    // An explicit repair action uses the user's existing normal playback endpoint.
    // Never changes recording defaults or guesses between multiple physical outputs.
    public static RouteRepair RepairCablePlaybackDefaults()
    {
        using var e = new MMDeviceEnumerator();
        var before = Snapshot();
        List<Role> broken = [];
        string? playbackId = null, playbackName = null;
        foreach (var role in new[] { Role.Console, Role.Multimedia, Role.Communications })
        {
            using var endpoint = e.GetDefaultAudioEndpoint(DataFlow.Render, role);
            if (AudioDevices.IsCable(endpoint)) broken.Add(role);
            else if (playbackId is null && role != Role.Communications)
            {
                playbackId = endpoint.ID; playbackName = endpoint.FriendlyName;
            }
        }
        if (broken.Count == 0) return new(playbackName ?? "当前播放设备", null, 0);
        if (playbackId is null)
            throw new InvalidOperationException("当前没有可参照的正常默认播放设备。请在 Windows 声音设置中先选择耳机，再点击修复。");

        Directory.CreateDirectory(Settings.Folder);
        string backup = Path.Combine(Settings.Folder, $"audio-routes-before-{DateTimeOffset.Now:yyyyMMdd-HHmmss-fff}.json");
        File.WriteAllText(backup, JsonSerializer.Serialize(before, new JsonSerializerOptions { WriteIndented = true }));
        object native = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9"), true)!)!;
        var policy = (IPolicyConfig)native;
        List<Role> changed = [];
        try
        {
            foreach (var role in broken)
            {
                Marshal.ThrowExceptionForHR(policy.SetDefaultEndpoint(playbackId, role));
                changed.Add(role);
            }
            AudioDevices.CheckPlaybackDefaults(e);
            Log.Write($"Playback route repaired to {playbackName}; roles={string.Join(',', changed)}; backup={backup}");
            return new(playbackName!, backup, changed.Count);
        }
        catch
        {
            foreach (var role in changed)
            {
                var original = before.Single(x => x.Flow == "Render" && x.Role == role.ToString());
                int hr = policy.SetDefaultEndpoint(original.Id, role);
                if (hr < 0) Log.Write($"Playback rollback failed ({hr:X8}); original route saved in {backup}");
            }
            throw;
        }
        finally { Marshal.ReleaseComObject(native); }
    }

    // Windows PolicyConfig COM ABI, also used by AudioDeviceCmdlets. Only SetDefaultEndpoint is called.
    [ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr format);
        [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, int useDefault, IntPtr format);
        [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr endpointFormat, IntPtr mixFormat);
        [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, int useDefault, IntPtr defaultPeriod, IntPtr minimumPeriod);
        [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr period);
        [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);
        [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);
        [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, int fxStore, IntPtr key, IntPtr value);
        [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, int fxStore, IntPtr key, IntPtr value);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, Role role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string id, int visible);
    }
}
