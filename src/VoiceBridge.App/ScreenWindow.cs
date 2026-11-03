using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using VoiceBridge.App.Accounts;
using VoiceBridge.App.Room;
namespace VoiceBridge.App;

public sealed class ScreenWindow:Window
{
 private readonly WebView2 view=new();private readonly RoomClient room;private readonly AccountClient account;private readonly string allowedOrigin;private readonly TextBlock status=new(){Margin=new Thickness(20),Text="正在打开共享面板…"};private bool refreshing,closing;private string lastMember="";
 public ScreenWindow(RoomClient session,AccountClient? accountClient=null)
 {
  room=session;lastMember=session.MemberId;room.Changed+=RoomChanged;account=accountClient??AccountClient.Current;allowedOrigin=session.Invite.Origin;if(allowedOrigin!=RoomService.DefaultOrigin&&!new Uri(allowedOrigin).IsLoopback)throw new InvalidOperationException("共享面板服务器无效");Title="屏幕共享 · VoiceBridge";Width=960;Height=760;MinWidth=560;MinHeight=460;WindowStartupLocation=WindowStartupLocation.CenterOwner;
  var panel=new DockPanel();DockPanel.SetDock(status,Dock.Bottom);panel.Children.Add(status);panel.Children.Add(view);Content=panel;WindowAppearance.Apply(this);Loaded+=async(_,_)=>await InitializeAsync();Closed+=(_,_)=>{closing=true;room.Changed-=RoomChanged;view.Dispose();};
 }
 private void RoomChanged(){if(closing)return;_ = Dispatcher.BeginInvoke(()=>{if(closing||view.CoreWebView2 is null)return;if(!room.SessionActive){Close();return;}if(room.MemberId.Length==32&&room.MemberId!=lastMember){lastMember=room.MemberId;view.Source=new Uri(room.ControllerLink+".embedded");status.Text="成员身份已恢复，可重新发起画面共享；原生连麦继续。";}});}
 private async Task InitializeAsync()
 {
  try{
   _=CoreWebView2Environment.GetAvailableBrowserVersionString();
   var environment=await CoreWebView2Environment.CreateAsync(userDataFolder:Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"VoiceBridge","WebView2"));await view.EnsureCoreWebView2Async(environment);if(closing)return;
   view.CoreWebView2.Settings.AreDevToolsEnabled=false;view.CoreWebView2.Settings.AreHostObjectsAllowed=false;view.CoreWebView2.Settings.AreDefaultContextMenusEnabled=true;
   view.CoreWebView2.NavigationStarting+=(_,e)=>{if(!Uri.TryCreate(e.Uri,UriKind.Absolute,out var uri)||uri.GetLeftPart(UriPartial.Authority)!=allowedOrigin){e.Cancel=true;} };
   view.CoreWebView2.NewWindowRequested+=(_,e)=>{e.Handled=true;if(Uri.TryCreate(e.Uri,UriKind.Absolute,out var uri)&&uri.GetLeftPart(UriPartial.Authority)==allowedOrigin)Process.Start(new ProcessStartInfo(e.Uri){UseShellExecute=true});};
   view.CoreWebView2.PermissionRequested+=(_,e)=>{if(e.PermissionKind is CoreWebView2PermissionKind.Microphone or CoreWebView2PermissionKind.Camera)e.State=CoreWebView2PermissionState.Deny;};
   view.CoreWebView2.WebMessageReceived+=async(_,e)=>{if(e.Source.StartsWith(allowedOrigin+"/room/",StringComparison.Ordinal)){try{using var message=JsonDocument.Parse(e.WebMessageAsJson);if(message.RootElement.TryGetProperty("type",out var type)&&type.GetString()=="refreshAttachment")await RefreshAttachmentAsync();}catch(JsonException){}}};
   view.CoreWebView2.ProcessFailed+=async(_,_)=>{status.Text="共享页面正在恢复，原生连麦继续运行。";await RefreshAttachmentAsync();if(!closing)view.Reload();};
   view.CoreWebView2.ContainsFullScreenElementChanged+=(_,_)=>{bool full=view.CoreWebView2.ContainsFullScreenElement;if(Content is DockPanel shell&&shell.Children.Count>0)shell.Children[0].Visibility=full?Visibility.Collapsed:Visibility.Visible;if(full)WindowState=WindowState.Maximized;};
   view.CoreWebView2.NavigationCompleted+=async(_,_)=>await RefreshAttachmentAsync();view.Source=new Uri(room.ControllerLink+".embedded");status.Text="画面由此面板处理，语音和音乐继续由客户端独立运行。";
  }catch(WebView2RuntimeNotFoundException){status.Text="未找到 Microsoft WebView2 Runtime。可安装官方运行时，或使用主窗口的浏览器入口；原生连麦不受影响。";var button=new Button{Content="打开官方 WebView2 下载页",Margin=new Thickness(24)};button.Click+=(_,_)=>Process.Start(new ProcessStartInfo("https://developer.microsoft.com/microsoft-edge/webview2/"){UseShellExecute=true});if(view.Parent is Panel panel){panel.Children.Remove(view);panel.Children.Add(button);}}
   catch(Exception ex){status.Text="共享面板暂时无法打开："+ex.GetBaseException().Message+"。可以使用浏览器入口，连麦继续运行。";}
 }
 private async Task RefreshAttachmentAsync()
 {
  if(refreshing||closing||view.CoreWebView2 is null)return;refreshing=true;try{var ticket=await account.DesktopTicketAsync(room);if(closing)return;view.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new{type="attachment",ticket}));}catch(Exception ex){status.Text="面板身份同步失败："+ex.GetBaseException().Message;}finally{refreshing=false;}
 }
}
