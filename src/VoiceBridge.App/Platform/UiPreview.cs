using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VoiceBridge.App.Platform;
public static class UiPreview
{
    public static void Write(string path, bool expandScenario = false)
    {
        // Render the actual WPF visual tree without opening a window or capturing the desktop.
        var window = new MainWindow(persistSettingsOnClose: false, enableTray: false,
            showTrayNotifications: false, initialSettings: new Settings { MusicPlayerId = MusicPlayers.NetEase.Id, ScenarioId = "wechat" });
        ((System.Windows.Controls.Expander)window.FindName("ScenarioExpander")).IsExpanded = expandScenario;
        var microphone = (System.Windows.Controls.ComboBox)window.FindName("MicrophoneBox");
        microphone.ItemsSource = new[] { new Audio.DeviceChoice("preview-microphone", "示例：真实麦克风") };
        microphone.SelectedIndex = 0;
        var cable = (System.Windows.Controls.ComboBox)window.FindName("CableBox");
        cable.ItemsSource = new[] { new Audio.DeviceChoice("preview-cable", "CABLE Input (VB-Audio Virtual Cable)") };
        cable.SelectedIndex = 0;
        ((System.Windows.Controls.TextBlock)window.FindName("StatusText")).Text = "界面预览 · 选择播放器后，分别开启需要的人声和音乐。";
        ((System.Windows.Controls.Button)window.FindName("StartupButton")).Content = "开机自启动：关";
        ((System.Windows.Controls.TextBlock)window.FindName("StartupStatusText")).Text = "界面预览 · 不修改开机自启动或音频状态。";
        var content = (System.Windows.Controls.ScrollViewer)window.Content;
        content.Background = window.Background;
        int height = expandScenario ? 2000 : 1650;
        content.Measure(new Size(850, height));
        content.Arrange(new Rect(0, 0, 850, height));
        content.UpdateLayout();
        var bitmap = new RenderTargetBitmap(850, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.GetFullPath(path)); encoder.Save(stream);
    }
}
