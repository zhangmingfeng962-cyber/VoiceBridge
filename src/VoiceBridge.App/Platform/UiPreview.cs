using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VoiceBridge.App.Platform;
public static class UiPreview
{
    public static void Write(string path)
    {
        // Render the actual WPF visual tree without opening a window or capturing the desktop.
        var window = new MainWindow();
        var content = (System.Windows.Controls.ScrollViewer)window.Content;
        content.Background = window.Background;
        content.Measure(new Size(850, 920));
        content.Arrange(new Rect(0, 0, 850, 920));
        content.UpdateLayout();
        var bitmap = new RenderTargetBitmap(850, 920, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.GetFullPath(path)); encoder.Save(stream);
    }
}
