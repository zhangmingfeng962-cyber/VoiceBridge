using System.Runtime.InteropServices;

namespace VoiceBridge.App.Audio;

// RNNoise consumes 48 kHz mono, 480 samples per call, in signed 16-bit float units.
// No VAD gate, automatic gain control or music processing is added here.
public sealed class MicrophoneDenoiser : IDisposable
{
    private IntPtr state;
    private readonly float[] frame = new float[480];
    public MicrophoneDenoiser()
    {
        if (Native.FrameSize() != 480) throw new InvalidOperationException("降噪组件帧长不兼容");
        state = Native.Create(IntPtr.Zero);
        if (state == IntPtr.Zero) throw new InvalidOperationException("无法启动麦克风降噪");
    }
    public void Process(Span<float> mono)
    {
        if (mono.Length % 480 != 0 || state == IntPtr.Zero) throw new ArgumentException("Expected 10 ms mono frames");
        for (int offset = 0; offset < mono.Length; offset += 480)
        {
            for (int i = 0; i < 480; i++) frame[i] = Math.Clamp(float.IsFinite(mono[offset + i]) ? mono[offset + i] : 0, -1, 1) * 32768f;
            Native.Process(state, frame, frame);
            for (int i = 0; i < 480; i++) mono[offset + i] = float.IsFinite(frame[i]) ? frame[i] / 32768f : 0;
        }
    }
    public void Dispose() { if (state != IntPtr.Zero) { Native.Destroy(state); state = IntPtr.Zero; } }
    private static class Native
    {
        private const string Library = "voicebridge-rnnoise";
        [DllImport(Library, EntryPoint = "rnnoise_get_frame_size", CallingConvention = CallingConvention.Cdecl)] internal static extern int FrameSize();
        [DllImport(Library, EntryPoint = "rnnoise_create", CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr Create(IntPtr model);
        [DllImport(Library, EntryPoint = "rnnoise_destroy", CallingConvention = CallingConvention.Cdecl)] internal static extern void Destroy(IntPtr state);
        [DllImport(Library, EntryPoint = "rnnoise_process_frame", CallingConvention = CallingConvention.Cdecl)] internal static extern float Process(IntPtr state, [In, Out] float[] output, [In] float[] input);
    }
}
