using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shell;
namespace VoiceBridge.App;
public static class WindowAppearance
{
 [DllImport("dwmapi.dll")]private static extern int DwmSetWindowAttribute(IntPtr hwnd,int attribute,ref int value,int size);
 public static void Apply(Window window)
 {
  WindowChrome.SetWindowChrome(window,new WindowChrome{CaptionHeight=40,ResizeBorderThickness=new Thickness(6),GlassFrameThickness=new Thickness(0),CornerRadius=new CornerRadius(0),UseAeroCaptionButtons=false});
  window.SourceInitialized+=(_,_)=>{var handle=new WindowInteropHelper(window).Handle;int dark=1,border=0x111111;_ = DwmSetWindowAttribute(handle,20,ref dark,4);_ = DwmSetWindowAttribute(handle,34,ref border,4);};
  if(window.Content is not UIElement content)return;window.Content=null;var shell=new DockPanel{Background=Brushes.Black,LastChildFill=true};var header=new DockPanel{Height=40,Background=Brushes.Black,LastChildFill=true};DockPanel.SetDock(header,Dock.Top);
  var buttons=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};DockPanel.SetDock(buttons,Dock.Right);
  foreach(var item in new[]{("—","最小化",(Action)(()=>SystemCommands.MinimizeWindow(window))),("□","最大化或还原",(Action)(()=>{if(window.WindowState==WindowState.Maximized)SystemCommands.RestoreWindow(window);else SystemCommands.MaximizeWindow(window);})),("×","关闭窗口",(Action)(()=>window.Close()))}){var button=new Button{Content=item.Item1,Width=46,Height=40,MinHeight=0,Padding=new Thickness(0),Margin=new Thickness(0),Background=Brushes.Transparent,BorderThickness=new Thickness(0),FontSize=17};System.Windows.Automation.AutomationProperties.SetName(button,item.Item2);button.Click+=(_,_)=>item.Item3();WindowChrome.SetIsHitTestVisibleInChrome(button,true);buttons.Children.Add(button);}
  header.Children.Add(buttons);header.Children.Add(new TextBlock{Text="VoiceBridge",Foreground=new SolidColorBrush(Color.FromRgb(184,184,184)),FontSize=12,Margin=new Thickness(16,0,0,0),VerticalAlignment=VerticalAlignment.Center});shell.Children.Add(header);shell.Children.Add(content);window.Content=shell;
  window.Loaded+=(_,_)=>Enter(content);
 }
 public static void Enter(UIElement element){if(!SystemParameters.ClientAreaAnimation)return;element.BeginAnimation(UIElement.OpacityProperty,new DoubleAnimation(0,1,TimeSpan.FromMilliseconds(180)){EasingFunction=new CubicEase{EasingMode=EasingMode.EaseOut}});}
}
