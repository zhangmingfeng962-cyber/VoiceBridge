using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace VoiceBridge.App.Platform;
public static class QQMusicFinder
{
    public static uint[] Roots()
    {
        using var snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot.IsInvalid) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>(), Exe = "" };
        Dictionary<uint, uint> parents = [];
        HashSet<uint> qq = [];
        if (Process32First(snapshot, ref entry)) do
        {
            parents[entry.Pid] = entry.ParentPid;
            if (string.Equals(entry.Exe, "QQMusic.exe", StringComparison.OrdinalIgnoreCase)) qq.Add(entry.Pid);
        } while (Process32Next(snapshot, ref entry));
        return qq.Where(pid => !HasQQAncestor(pid, qq, parents)).Order().ToArray();
    }
    private static bool HasQQAncestor(uint pid, HashSet<uint> qq, Dictionary<uint, uint> parents)
    {
        HashSet<uint> seen = [pid];
        while (parents.TryGetValue(pid, out uint parent) && parent != 0 && seen.Add(parent))
        {
            if (qq.Contains(parent)) return true;
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
