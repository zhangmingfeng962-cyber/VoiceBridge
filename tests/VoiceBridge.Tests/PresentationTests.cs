using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VoiceBridge.App;
using VoiceBridge.Core;
using Xunit;
namespace VoiceBridge.Tests;
public class PresentationTests
{
    [Fact]
    public void MemberCuesAreDistinctBoundedAndRespectListeningMute()
    {
        var join = RoomNotifications.Create(true); var leave = RoomNotifications.Create(false);
        Assert.Equal(17280, join.Length); Assert.NotEqual(join, leave); Assert.All(join, n => Assert.InRange(n, -.056f, .056f));
        Assert.Equal(0, join[0]); Assert.Equal(0, leave[0]); using var mix = new RoomMix(); mix.SpeakerGain = 0; mix.Notify(true); var buffer = new float[19200]; mix.Read(buffer, 0, buffer.Length); Assert.All(buffer, n => Assert.Equal(0, n));
        mix.SpeakerGain = 1; mix.Notify(false); mix.Read(buffer, 0, buffer.Length); Assert.Contains(buffer, n => Math.Abs(n) > .01);
    }
    [Fact]
    public void DisabledButtonsStayDarkAndReadableUnderWindowsTheme()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var application = new VoiceBridge.App.App(); application.InitializeComponent(); var style = (Style)application.Resources[typeof(Button)];
                var panel = new StackPanel { Background = new SolidColorBrush(Color.FromRgb(16, 25, 39)), Width = 620, Height = 180, Margin = new Thickness(0) };
                foreach (bool enabled in new[] { true, false })
                {
                    var button = new Button { Style = style, Content = enabled ? "创建房间 · 可用" : "创建房间 · 已入房，暂不可用", IsEnabled = enabled, Margin = new Thickness(18, 12, 18, 0) }; panel.Children.Add(button);
                    button.ApplyTemplate(); var border = (Border)button.Template.FindName("Chrome", button);
                    if (!enabled) { Assert.Equal(Color.FromRgb(28, 41, 58), ((SolidColorBrush)border.Background).Color); var label = (ContentPresenter)button.Template.FindName("Label", button); Assert.Equal(Color.FromRgb(135, 152, 174), ((SolidColorBrush)System.Windows.Documents.TextElement.GetForeground(label)).Color); }
                }
                panel.Measure(new Size(620, 180)); panel.Arrange(new Rect(0, 0, 620, 180)); panel.UpdateLayout();
                if (Environment.GetEnvironmentVariable("VOICEBRIDGE_STYLE_PREVIEW") is { } file)
                { var bitmap = new RenderTargetBitmap(620, 180, 96, 96, PixelFormats.Pbgra32); bitmap.Render(panel); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var output = File.Create(file); encoder.Save(output); }
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(20))); if (failure is not null) throw failure;
    }
}
