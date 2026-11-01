using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
namespace VoiceBridge.App;
public sealed class NativeTray : IDisposable
{
    private NotifyData data; private readonly HwndSource source; private readonly MainWindow window; private readonly Action mute,exit; private bool notice,disposed;
    public NativeTray(MainWindow owner,Action muteAction,Action exitAction){window=owner;mute=muteAction;exit=exitAction;var hwnd=new WindowInteropHelper(owner).Handle;source=HwndSource.FromHwnd(hwnd)!;source.AddHook(Messages);data=new(){Size=(uint)Marshal.SizeOf<NotifyData>(),Window=hwnd,Id=1,Flags=7,Callback=0x8001,Icon=LoadIcon(),Tip="VoiceBridge",Info="",InfoTitle=""};ShellNotify(0,ref data);}
    private IntPtr Messages(IntPtr hwnd,int message,IntPtr wParam,IntPtr lParam,ref bool handled)
    {if(message!=0x8001)return IntPtr.Zero;if(lParam.ToInt32() is 0x202 or 0x203)window.RestoreWindow();if(lParam.ToInt32()==0x205){var menu=new ContextMenu();var restore=new MenuItem{Header="恢复 VoiceBridge"};restore.Click+=(_,_)=>window.RestoreWindow();var microphone=new MenuItem{Header="切换麦克风"};microphone.Click+=(_,_)=>mute();var quit=new MenuItem{Header="退出程序"};quit.Click+=(_,_)=>exit();menu.Items.Add(restore);menu.Items.Add(microphone);menu.Items.Add(new Separator());menu.Items.Add(quit);menu.Placement=PlacementMode.MousePoint;menu.IsOpen=true;}handled=true;return IntPtr.Zero;}
    public void SetTip(string value){if(disposed)return;data.Tip="VoiceBridge · "+value;if(data.Tip.Length>120)data.Tip=data.Tip[..120];ShellNotify(1,ref data);}
    public void ShowBackgroundNotice(){if(notice||disposed)return;notice=true;data.Flags|=16;data.InfoTitle="VoiceBridge 已收起";data.Info="连麦继续运行。右下角托盘可关麦、恢复窗口或退出程序。";data.InfoFlags=1;ShellNotify(1,ref data);data.Flags&=~16u;}
    public void Dispose(){if(disposed)return;disposed=true;ShellNotify(2,ref data);source.RemoveHook(Messages);if(data.Icon!=IntPtr.Zero)DestroyIcon(data.Icon);}
    private static IntPtr LoadIcon(){using var resource=Application.GetResourceStream(new Uri("pack://application:,,,/VoiceBridge;component/Assets/voicebridge.ico"))!.Stream;using var memory=new MemoryStream();resource.CopyTo(memory);var bytes=memory.ToArray();int count=BitConverter.ToUInt16(bytes,4),entry=6;for(int i=0;i<count;i++)if(bytes[6+i*16]==32){entry=6+i*16;break;}int size=BitConverter.ToInt32(bytes,entry+8),offset=BitConverter.ToInt32(bytes,entry+12);return CreateIcon(bytes.AsSpan(offset,size).ToArray(),(uint)size,true,0x30000,32,32,0);}
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]private struct NotifyData{public uint Size;public IntPtr Window;public uint Id,Flags,Callback;public IntPtr Icon;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)]public string Tip;public uint State,StateMask;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=256)]public string Info;public uint Timeout;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=64)]public string InfoTitle;public uint InfoFlags;public Guid ItemGuid;public IntPtr BalloonIcon;}
    [DllImport("shell32.dll",EntryPoint="Shell_NotifyIconW",CharSet=CharSet.Unicode)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool ShellNotify(uint action,ref NotifyData data);
    [DllImport("user32.dll",EntryPoint="CreateIconFromResourceEx")]private static extern IntPtr CreateIcon(byte[] bits,uint size,bool icon,uint version,int width,int height,uint flags);
    [DllImport("user32.dll")][return:MarshalAs(UnmanagedType.Bool)]private static extern bool DestroyIcon(IntPtr icon);
}
