using System.Text;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using VoiceBridge.Core;
namespace VoiceBridge.App.Accounts;

public sealed class AccountWindow:Window
{
 private readonly AccountClient account;private readonly StackPanel fields=new(){Margin=new Thickness(32,20,32,28)};
 private readonly TextBlock heading=new(){FontSize=24,FontWeight=FontWeights.SemiBold},notice=new(){Foreground=new SolidColorBrush(Color.FromRgb(221,185,117)),Margin=new Thickness(0,16,0,12)};
 private readonly TextBox email=new(){MaxLength=254},code=new(){MaxLength=6},nickname=new(){MaxLength=20};
 private readonly PasswordField password=new(),again=new(),oldPassword=new();
 private Image? preview;
 private string mode="login",step="email",ticket="";private byte[]? avatar;private bool busy;private DateTime sendAfter;
 public AccountWindow(bool profile=false,AccountClient? accountClient=null)
 {
  account=accountClient??AccountClient.Current;Title=profile?"个人资料 · VoiceBridge":"登录 · VoiceBridge";Width=470;Height=740;MinWidth=400;MinHeight=540;WindowStartupLocation=WindowStartupLocation.CenterOwner;ResizeMode=ResizeMode.CanResize;Content=new ScrollViewer{Content=fields,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};WindowAppearance.Apply(this);
  if(profile&&account.User is not null)mode="profile";nickname.Text=account.User?.Nickname??new ProfileStore().Read().Nickname;Build();
 }
 private void Label(string text)=>fields.Children.Add(new TextBlock{Text=text,Margin=new Thickness(0,16,0,6),FontSize=14});
 private Button Button(string text,Func<Task> action,bool primary=false){var button=new Button{Content=text,Margin=new Thickness(0,12,0,0),HorizontalAlignment=HorizontalAlignment.Stretch};if(primary)button.Style=(Style)Application.Current.FindResource("Primary");button.Click+=async(_,_)=>{if(busy)return;busy=true;button.IsEnabled=false;notice.Text="";try{await action();}catch(Exception ex){notice.Text=ex.GetBaseException().Message;}finally{busy=false;button.IsEnabled=true;}};fields.Children.Add(button);return button;}
 private void Change(string next){mode=next;step="email";ticket="";password.Value=again.Value="";notice.Text="";Build();}
 private void Build()
 {
  fields.Children.Clear();heading.Text=mode=="profile"?"个人资料":mode=="register"?"创建你的账号":mode=="reset"?"找回密码":"欢迎回来";fields.Children.Add(heading);
  fields.Children.Add(new TextBlock{Text=mode=="login"?"使用邮箱和密码登录，自动记住登录状态。":mode=="profile"?"头像和昵称随账号同步。":"先验证邮箱，再设置 15–128 字符的密码。",Foreground=new SolidColorBrush(Color.FromRgb(184,184,184)),FontSize=12,Margin=new Thickness(0,10,0,12)});
  if(mode=="profile"){
   AddAvatarPreview();Label("昵称");fields.Children.Add(nickname);Button("选择头像",ChooseAvatarAsync);Button("保存资料",async()=>{await account.UpdateProfileAsync(nickname.Text);if(avatar is not null)await account.UploadAvatarAsync(avatar);notice.Text="资料已保存。";},true);
   Label("修改密码：当前密码");fields.Children.Add(oldPassword);Label("新密码");fields.Children.Add(password);Label("再次输入新密码");fields.Children.Add(again);Button("保存新密码",async()=>{ValidatePassword();await account.RequestAsync("/api/auth/password",new{currentPassword=oldPassword.Value,newPassword=password.Value});oldPassword.Value=password.Value=again.Value="";notice.Text="密码已更新，其他设备需要重新登录。";});
   Button("退出登录",async()=>{await account.LogoutAsync();DialogResult=false;Close();});
  }else if(step=="email"){
   Label("邮箱");fields.Children.Add(email);
   if(mode=="login"){Label("密码");fields.Children.Add(password);Button("登录",async()=>{await account.SignInAsync(email.Text,password.Value);DialogResult=true;Close();},true);Button("首次使用，注册账号",()=>{Change("register");return Task.CompletedTask;});Button("忘记密码",()=>{Change("reset");return Task.CompletedTask;});}
   else{Label("验证码");fields.Children.Add(code);Button("获取验证码",async()=>{if(DateTime.UtcNow<sendAfter)throw new InvalidOperationException("请等待一分钟后重发");await account.RequestAsync("/api/auth/code",new{address=email.Text,purpose=mode},authenticated:false);sendAfter=DateTime.UtcNow.AddMinutes(1);notice.Text="验证码已发送，五分钟内有效。";});Button("验证邮箱",async()=>{ticket=await account.VerifyAsync(email.Text,code.Text,mode);step="password";Build();},true);Button("返回密码登录",()=>{Change("login");return Task.CompletedTask;});}
  }else{
   Label("设置密码");fields.Children.Add(password);Label("再次输入密码");fields.Children.Add(again);if(mode=="register"){Label("昵称");fields.Children.Add(nickname);AddAvatarPreview();Button("选择头像（可稍后设置）",ChooseAvatarAsync);}
   Button(mode=="register"?"注册并登录":"保存新密码",async()=>{ValidatePassword();if(mode=="register"){await account.RegisterAsync(ticket,password.Value,nickname.Text);if(avatar is not null)try{await account.UploadAvatarAsync(avatar);}catch{MessageBox.Show(this,"账号已创建，头像可在个人资料中重新上传。");}DialogResult=true;Close();}else{await account.RequestAsync("/api/auth/reset",new{verificationToken=ticket,password=password.Value},authenticated:false);Change("login");notice.Text="密码已更新，请使用新密码登录。";}},true);Button("重新验证邮箱",()=>{Change(mode);return Task.CompletedTask;});
  }
  fields.Children.Add(notice);
 }
 private void AddAvatarPreview(){preview=new Image{Width=72,Height=72,Stretch=Stretch.UniformToFill,HorizontalAlignment=HorizontalAlignment.Left,Margin=new Thickness(0,16,0,0),Clip=new EllipseGeometry(new Point(36,36),36,36)};if(avatar is not null){using var memory=new MemoryStream(avatar);var image=new BitmapImage();image.BeginInit();image.CacheOption=BitmapCacheOption.OnLoad;image.StreamSource=memory;image.EndInit();preview.Source=image;}else if(account.User?.AvatarUrl is {} url)preview.Source=new BitmapImage(new Uri(account.Origin+url));else preview.Visibility=Visibility.Collapsed;fields.Children.Add(preview);}
 private void ValidatePassword(){if(password.Value.Normalize().EnumerateRunes().Count()<15||password.Value.EnumerateRunes().Count()>128)throw new InvalidOperationException("密码需要 15–128 个字符，可以是一段短语。");if(password.Value!=again.Value)throw new InvalidOperationException("两次输入的密码不一致。");}
 private Task ChooseAvatarAsync(){var dialog=new OpenFileDialog{Title="选择头像",Filter="图片|*.png;*.jpg;*.jpeg;*.webp;*.bmp"};if(dialog.ShowDialog(this)!=true)return Task.CompletedTask;if(new FileInfo(dialog.FileName).Length>8*1024*1024)throw new InvalidOperationException("请选择小于 8MB 的图片");var image=new BitmapImage();image.BeginInit();image.CacheOption=BitmapCacheOption.OnLoad;image.DecodePixelWidth=1024;image.UriSource=new Uri(dialog.FileName);image.EndInit();image.Freeze();int side=Math.Min(image.PixelWidth,image.PixelHeight);var cropped=new CroppedBitmap(image,new Int32Rect((image.PixelWidth-side)/2,(image.PixelHeight-side)/2,side,side));var scaled=new TransformedBitmap(cropped,new ScaleTransform(256d/side,256d/side));var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(scaled));using var output=new MemoryStream();encoder.Save(output);avatar=output.ToArray();if(preview is not null){using var memory=new MemoryStream(avatar);var loaded=new BitmapImage();loaded.BeginInit();loaded.CacheOption=BitmapCacheOption.OnLoad;loaded.StreamSource=memory;loaded.EndInit();preview.Source=loaded;preview.Visibility=Visibility.Visible;}notice.Text="头像已选择，将在保存时上传。";return Task.CompletedTask;}
}
public sealed class PasswordField:Grid
{
 private readonly PasswordBox hidden=new(){MaxLength=128,Padding=new Thickness(10,8,10,8),MinHeight=38,Background=new SolidColorBrush(Color.FromRgb(32,32,32)),Foreground=Brushes.White};private readonly TextBox shown=new(){MaxLength=128,Visibility=Visibility.Collapsed,Margin=new Thickness(0)};private bool visible;
 public string Value{get=>visible?shown.Text:hidden.Password;set{shown.Text=value;hidden.Password=value;}}
 public PasswordField(){ColumnDefinitions.Add(new());ColumnDefinitions.Add(new(){Width=GridLength.Auto});Children.Add(hidden);Children.Add(shown);var toggle=new Button{Content="显示",MinWidth=52,Margin=new Thickness(8,0,0,0),Padding=new Thickness(8,4,8,4)};SetColumn(toggle,1);toggle.Click+=(_,_)=>{var current=Value;visible=!visible;Value=current;hidden.Visibility=visible?Visibility.Collapsed:Visibility.Visible;shown.Visibility=visible?Visibility.Visible:Visibility.Collapsed;toggle.Content=visible?"隐藏":"显示";};Children.Add(toggle);}
}
