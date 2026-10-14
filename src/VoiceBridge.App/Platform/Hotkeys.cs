using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace VoiceBridge.App.Platform;
public sealed class Hotkeys : IDisposable
{
    private readonly HwndSource source;
    private readonly Action<int> pressed;
    private readonly List<int> registered = [];
    public Hotkeys(IntPtr window, Action<int> pressed, out string status)
    {
        this.pressed = pressed;
        source = HwndSource.FromHwnd(window) ?? throw new InvalidOperationException("窗口未创建。");
        source.AddHook(Hook);
        List<string> conflicts = [];
        (int Id, uint Key, string Label)[] keys = [(1, 0x78, "Ctrl+Alt+F9"), (2, 0x26, "Ctrl+Alt+↑"), (3, 0x28, "Ctrl+Alt+↓"), (4, 0x77, "Ctrl+Alt+F8")];
        foreach (var key in keys)
            if (RegisterHotKey(window, key.Id, 0x4003, key.Key)) registered.Add(key.Id); else conflicts.Add(key.Label);
        status = conflicts.Count == 0 ? "快捷键：Ctrl+Alt+F8 人声 · F9 音乐 · ↑/↓ 音乐 ±5%" : "快捷键被占用：" + string.Join("、", conflicts);
    }
    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x0312 && registered.Contains(wParam.ToInt32())) { pressed(wParam.ToInt32()); handled = true; }
        return IntPtr.Zero;
    }
    public void Dispose() { foreach (int id in registered) UnregisterHotKey(source.Handle, id); source.RemoveHook(Hook); }
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
