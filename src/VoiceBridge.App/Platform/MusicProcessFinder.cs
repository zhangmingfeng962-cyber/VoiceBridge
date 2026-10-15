using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace VoiceBridge.App.Platform;

public readonly record struct MusicProcessInfo(uint ProcessId, uint ParentProcessId, string ExecutableName);

public static class MusicProcessFinder
{
    public static uint[] Roots(string playerId = MusicPlayers.QQMusicId)
    {
        _ = MusicPlayers.Get(playerId);
        using var snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot.IsInvalid) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>(), Exe = "" };
        List<MusicProcessInfo> processes = [];
        if (Process32First(snapshot, ref entry)) do
        {
            processes.Add(new(entry.Pid, entry.ParentPid, entry.Exe));
        } while (Process32Next(snapshot, ref entry));
        return SelectRoots(playerId, processes);
    }

    public static uint[] SelectRoots(string playerId, IEnumerable<MusicProcessInfo> processes)
    {
        var player = MusicPlayers.Get(playerId);
        Dictionary<uint, uint> parents = [];
        HashSet<uint> matching = [];
        foreach (var process in processes)
        {
            parents[process.ProcessId] = process.ParentProcessId;
            if (process.ProcessId != 0 && string.Equals(process.ExecutableName, player.ExecutableName, StringComparison.OrdinalIgnoreCase))
                matching.Add(process.ProcessId);
        }
        return matching.Where(pid => !HasMatchingAncestor(pid, matching, parents)).Order().ToArray();
    }

    private static bool HasMatchingAncestor(uint pid, HashSet<uint> matching, Dictionary<uint, uint> parents)
    {
        HashSet<uint> seen = [pid];
        while (parents.TryGetValue(pid, out uint parent) && parent != 0 && seen.Add(parent))
        {
            if (matching.Contains(parent)) return true;
            pid = parent;
        }
        return false;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage, Pid;
        public UIntPtr Heap;
        public uint Module, Threads, ParentPid;
        public int Priority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Exe;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32First(SafeFileHandle snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32Next(SafeFileHandle snapshot, ref ProcessEntry entry);
}
