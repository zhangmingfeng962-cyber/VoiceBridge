using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace VoiceBridge.App.Audio;
public sealed record DeviceOption(string Id, string Name);
public sealed record ProcessTarget(uint Pid, long Started);
public sealed record AudioSource(string Id, string Name, bool Active, ProcessTarget[] Targets);
public sealed record AudioInventory(DeviceOption[] Microphones, DeviceOption[] Outputs, AudioSource[] Sources);
public static class AudioSources
{
    public static uint CaptureRoot(uint session, uint self, IReadOnlyDictionary<uint, uint> parents, IReadOnlyDictionary<uint, string> names)
    {
        static bool Ancestor(uint root, uint child, IReadOnlyDictionary<uint, uint> map) { HashSet<uint> seen = []; while (child != 0 && seen.Add(child)) { if (child == root) return true; if (!map.TryGetValue(child, out child)) break; } return false; }
        uint selected = session; HashSet<uint> visited = [selected];
        while (parents.TryGetValue(selected, out uint parent) && visited.Add(parent) && names.TryGetValue(parent, out var name) && string.Equals(name, names.GetValueOrDefault(selected), StringComparison.OrdinalIgnoreCase)) selected = parent;
        if (Ancestor(selected, self, parents)) selected = session;
        return Ancestor(selected, self, parents) ? 0 : selected;
    }
    public static bool SafeDevice(string name) => !new[] { "CABLE", "Voicemeeter", "Stereo Mix", "立体声混音", "Loopback", "Monitor", "监听", "虚拟" }.Any(x => name.Contains(x, StringComparison.OrdinalIgnoreCase));
    public static bool SafeProcess(string name) => !name.StartsWith("VoiceBridge", StringComparison.OrdinalIgnoreCase) && !new[] { "audiodg", "System", "svchost", "explorer" }.Contains(name, StringComparer.OrdinalIgnoreCase);
    public static AudioInventory Read()
    {
        using var enumerator = new MMDeviceEnumerator(); List<DeviceOption> microphones = [], outputs = []; var sessions = new Dictionary<uint, bool>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)) using (device) if (SafeDevice(device.FriendlyName)) microphones.Add(new(device.ID, device.FriendlyName));
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)) using (device)
        {
            if (SafeDevice(device.FriendlyName)) outputs.Add(new(device.ID, device.FriendlyName));
            try { var list = device.AudioSessionManager.Sessions; for (int n = 0; n < list.Count; n++) using (var session = list[n]) if (session.GetProcessID > 0) sessions[session.GetProcessID] = sessions.GetValueOrDefault(session.GetProcessID) || session.State == AudioSessionState.AudioSessionStateActive; } catch (COMException) { }
        }
        var processList = Snapshot(); var parentMap = processList.ToDictionary(p => p.Id, p => p.Parent);
        var names = processList.ToDictionary(p => p.Id, p => p.Name);
        var groups = new Dictionary<string, (string Name, bool Active, Dictionary<uint, long> Targets)>();
        foreach (var item in sessions)
        {
            try
            {
                using var process = Process.GetProcessById((int)item.Key); if (process.Id == Environment.ProcessId || !SafeProcess(process.ProcessName)) continue;
                string executable = process.MainModule?.FileName ?? process.ProcessName; string display;
                try { display = FileVersionInfo.GetVersionInfo(executable).FileDescription ?? process.ProcessName; } catch { display = process.ProcessName; }
                uint root = CaptureRoot(item.Key, (uint)Environment.ProcessId, parentMap, names); if (root == 0) continue;
                using var rootProcess = Process.GetProcessById((int)root);
                string id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(executable.ToLowerInvariant())))[..24].ToLowerInvariant();
                var current = groups.GetValueOrDefault(id, (Name: display, Active: false, Targets: new Dictionary<uint, long>())); current.Active |= item.Value; current.Targets[root] = rootProcess.StartTime.ToUniversalTime().Ticks; groups[id] = current;
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or ArgumentException or NotSupportedException) { }
        }
        var sources = groups.Select(pair => new AudioSource(pair.Key, pair.Value.Name, pair.Value.Active, pair.Value.Targets.Select(p => new ProcessTarget(p.Key, p.Value)).ToArray())).OrderByDescending(x => x.Active).ThenBy(x => x.Name).Take(64).ToArray();
        string? defaultMic = null, defaultOutput = null;
        try { using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia); defaultMic = device.ID; } catch (COMException) { }
        try { using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia); defaultOutput = device.ID; } catch (COMException) { }
        return new(microphones.OrderByDescending(d => d.Id == defaultMic).ToArray(), outputs.OrderByDescending(d => d.Id == defaultOutput).ToArray(), sources);
    }
    private sealed record Entry(uint Id, uint Parent, string Name);
    private static Entry[] Snapshot()
    {
        using var snapshot = CreateToolhelp32Snapshot(2, 0); List<Entry> result = []; var item = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>(), Name = "" };
        if (!snapshot.IsInvalid && Process32First(snapshot, ref item)) do { result.Add(new(item.Pid, item.Parent, item.Name)); } while (Process32Next(snapshot, ref item)); return result.ToArray();
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct ProcessEntry { public uint Size, Usage, Pid; public UIntPtr Heap; public uint Module, Threads, Parent; public int Priority; public uint Flags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Name; }
    private sealed class SnapshotHandle : SafeHandleZeroOrMinusOneIsInvalid { private SnapshotHandle() : base(true) { } protected override bool ReleaseHandle() => CloseHandle(handle); }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SnapshotHandle CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32First(SnapshotHandle snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32Next(SnapshotHandle snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr value);
}
