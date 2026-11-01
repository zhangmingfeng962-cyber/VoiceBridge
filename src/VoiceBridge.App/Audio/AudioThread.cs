using System.Runtime.InteropServices;
namespace VoiceBridge.App.Audio;
internal sealed class AudioThread : IDisposable
{
    private readonly IntPtr handle;
    public AudioThread() { uint index = 0; handle = AvSetMmThreadCharacteristics("Audio", ref index); }
    public void Dispose() { if (handle != IntPtr.Zero) AvRevertMmThreadCharacteristics(handle); }
    [DllImport("avrt.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr AvSetMmThreadCharacteristics(string task, ref uint index);
    [DllImport("avrt.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AvRevertMmThreadCharacteristics(IntPtr handle);
}
