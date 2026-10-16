using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace VoiceBridge.App.Sharing;

// Close the tunnel even if VoiceBridge crashes. Never install a persistent background service.
internal sealed class ChildProcessJob : IDisposable
{
    private readonly SafeFileHandle handle;
    private ChildProcessJob(SafeFileHandle handle) => this.handle = handle;
    public static ChildProcessJob Attach(Process process)
    {
        var handle = CreateJobObject(IntPtr.Zero, null);
        if (handle.IsInvalid) { handle.Dispose(); throw new Win32Exception(); }
        try
        {
            var limits = new ExtendedLimits { Basic = new BasicLimits { LimitFlags = 0x2000 } };
            if (!SetInformationJobObject(handle, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()) || !AssignProcessToJobObject(handle, process.Handle)) throw new Win32Exception();
            return new ChildProcessJob(handle);
        }
        catch { handle.Dispose(); throw; }
    }
    public void Dispose() => handle.Dispose();
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits { public long PerProcessUserTimeLimit, PerJobUserTimeLimit; public uint LimitFlags; public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize; public uint ActiveProcessLimit; public UIntPtr Affinity; public uint PriorityClass, SchedulingClass; }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits { public BasicLimits Basic; public IoCounters Io; public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, ref ExtendedLimits info, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
}
