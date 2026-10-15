using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;
using VoiceBridge.Core;

namespace VoiceBridge.App.Audio;

// Public Windows application-loopback API. No system endpoint loopback, DLL injection, or hooks.
public sealed class ProcessLoopback : IDisposable
{
    private readonly ManualResetEvent stop = new(false);
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread thread;
    private int disposed;
    public LiveSampleQueue Queue { get; } = new(48000, 2);
    public Exception? Failure { get; private set; }
    public long Discontinuities;
    public uint ProcessId { get; }

    public ProcessLoopback(uint processId)
    {
        ProcessId = processId;
        thread = new Thread(Run) { Name = $"Music capture {processId}", IsBackground = true };
        thread.SetApartmentState(ApartmentState.MTA);
    }
    public async Task StartAsync()
    {
        thread.Start();
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
    }

    private void Run()
    {
        AudioClient? client = null;
        IActivateOperation? operation = null;
        IntPtr mmcss = IntPtr.Zero;
        using var sampleReady = new AutoResetEvent(false);
        bool started = false;
        try
        {
            var completion = new ActivationCompletion(ProcessId);
            Guid iid = typeof(IAudioClient).GUID;
            int hr = ActivateAudioInterfaceAsync("VAD\\Process_Loopback", ref iid, completion.Variant,
                completion, out operation);
            if (hr < 0) { completion.FreeParameters(); Marshal.ThrowExceptionForHR(hr); }
            if (!completion.Result.Task.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("Windows 进程音频接口激活超时。");
            client = new AudioClient(completion.Result.Task.GetAwaiter().GetResult());
            GC.KeepAlive(completion);
            client.Initialize(AudioClientShareMode.Shared,
                AudioClientStreamFlags.Loopback | AudioClientStreamFlags.EventCallback |
                AudioClientStreamFlags.AutoConvertPcm | AudioClientStreamFlags.SrcDefaultQuality,
                0, 0, WaveFormat.CreateIeeeFloatWaveFormat(48000, 2), Guid.Empty);
            client.SetEventHandle(sampleReady.SafeWaitHandle.DangerousGetHandle());
            var capture = client.AudioCaptureClient;
            uint taskIndex = 0;
            mmcss = AvSetMmThreadCharacteristics("Audio", ref taskIndex);
            float[] samples = new float[Math.Max(client.BufferSize * 2, 4096)];
            client.Start(); started = true; ready.TrySetResult();
            WaitHandle[] handles = [stop, sampleReady];
            while (WaitHandle.WaitAny(handles, 100) != 0)
            {
                while (!stop.WaitOne(0) && capture.GetNextPacketSize() > 0)
                {
                    IntPtr ptr = capture.GetBuffer(out int frames, out AudioClientBufferFlags flags);
                    try
                    {
                        int count = checked(frames * 2);
                        if (samples.Length < count) Array.Resize(ref samples, count);
                        if ((flags & AudioClientBufferFlags.Silent) != 0) Array.Clear(samples, 0, count);
                        else Marshal.Copy(ptr, samples, 0, count);
                        if ((flags & AudioClientBufferFlags.DataDiscontinuity) != 0) Interlocked.Increment(ref Discontinuities);
                        Queue.Write(samples.AsSpan(0, count));
                    }
                    finally { capture.ReleaseBuffer(frames); }
                }
            }
        }
        catch (Exception ex) { Failure = ex; ready.TrySetException(ex); }
        finally
        {
            try { if (started) client?.Stop(); } catch (COMException) { }
            client?.Dispose();
            if (operation is not null) Marshal.ReleaseComObject(operation);
            if (mmcss != IntPtr.Zero) AvRevertMmThreadCharacteristics(mmcss);
            stop.Dispose();
        }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        if (thread.ThreadState.HasFlag(ThreadState.Unstarted)) { stop.Dispose(); return; }
        try { stop.Set(); } catch (ObjectDisposedException) { }
        thread.Join(TimeSpan.FromSeconds(12)); // worker owns and releases its COM objects and wait handles
        Queue.Clear();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ActivationParameters { public uint Type, ProcessId, Mode; }
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct BlobVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public uint Size;
        [FieldOffset(16)] public IntPtr Data;
    }
    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IActivateOperation { void GetActivateResult(out int result, [MarshalAs(UnmanagedType.IUnknown)] out object activated); }
    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IActivationCompletion { void ActivateCompleted(IActivateOperation operation); }
    [ComImport, Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAgileObject { }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public sealed class ActivationCompletion : IActivationCompletion, IAgileObject
    {
        public TaskCompletionSource<IAudioClient> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IntPtr Variant { get; private set; }
        private IntPtr data;
        public ActivationCompletion(uint pid)
        {
            data = Marshal.AllocHGlobal(12);
            Marshal.StructureToPtr(new ActivationParameters { Type = 1, ProcessId = pid, Mode = 0 }, data, false);
            Variant = Marshal.AllocHGlobal(24);
            Marshal.StructureToPtr(new BlobVariant { Type = 65, Size = 12, Data = data }, Variant, false);
        }
        public void ActivateCompleted(IActivateOperation operation)
        {
            try
            {
                operation.GetActivateResult(out int hr, out object value);
                Marshal.ThrowExceptionForHR(hr);
                Result.TrySetResult((IAudioClient)value);
            }
            catch (Exception ex) { Result.TrySetException(ex); }
            finally { FreeParameters(); }
        }
        public void FreeParameters()
        {
            if (Variant != IntPtr.Zero) { Marshal.FreeHGlobal(Variant); Variant = IntPtr.Zero; }
            if (data != IntPtr.Zero) { Marshal.FreeHGlobal(data); data = IntPtr.Zero; }
        }
    }
    [DllImport("Mmdevapi.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int ActivateAudioInterfaceAsync(string device, ref Guid iid, IntPtr activation,
        IActivationCompletion completion, out IActivateOperation operation);
    [DllImport("avrt.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr AvSetMmThreadCharacteristics(string name, ref uint index);
    [DllImport("avrt.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AvRevertMmThreadCharacteristics(IntPtr handle);
}
