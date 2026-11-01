using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using VoiceBridge.App.Audio;
using VoiceBridge.App.Room;
using VoiceBridge.App.Updates;
using VoiceBridge.Core;

namespace VoiceBridge.App;
public partial class MainWindow : Window
{
    private RoomClient? room;
    private bool busy, exiting, syncing, bindingKey;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly ProfileStore profile;
    private readonly UpdateService updates;
    private readonly ObservableCollection<MemberRow> memberRows = [];
    private LobbyRoom[] lobby = [];
    private string rosterSignature = "", sourceSignature = "", inputSignature = "", outputSignature = "";
    private DateTime nextLobby = DateTime.MinValue, nextUpdate = DateTime.MinValue;
    private NativeTray? tray;
    public MainWindow(ProfileStore? settings = null)
    {
        profile = settings ?? new(); InitializeComponent(); NicknameBox.Text = profile.Read().Nickname; MemberList.ItemsSource = memberRows;
        updates = new(() => room?.SessionActive == true || busy);
        updates.Changed += () => Dispatcher.BeginInvoke(() => { UpdateButton.Content = updates.Label; UpdateNotes.Text = updates.Notes; UpdateButton.IsEnabled = !updates.Busy; });
        Closing += OnClosing; PreviewKeyDown += BindKeyDown;
        Loaded += (_,_) => { tray = new NativeTray(this, () => _ = ToggleMicrophoneAsync(), () => _ = RequestExitAsync()); _ = RefreshLobbyAsync(); _ = updates.CheckAsync(); };
        timer.Tick += async (_,_) =>
        {
            if (exiting) return;
            if (room is not null && !room.SessionActive && !busy) { string reason = room.Status; await LeaveAsync(false); StatusText.Text = reason; }
            RefreshRoom();
            if (IsVisible && WindowState != WindowState.Minimized && LobbyView.Visibility == Visibility.Visible && DateTime.UtcNow >= nextLobby) { nextLobby = DateTime.UtcNow.AddSeconds(10); _ = RefreshLobbyAsync(); }
            if (DateTime.UtcNow >= nextUpdate) { nextUpdate = DateTime.UtcNow.AddHours(6); _ = updates.CheckAsync(); }
        };
        timer.Start(); RefreshRoom();
    }
    public async Task EnterSyntheticFixtureAsync()
    {
        using var http = new System.Net.Http.HttpClient { BaseAddress = new Uri("http://127.0.0.1:4096") };
        http.DefaultRequestHeaders.Authorization = new("Bearer", new string('1',64));
        using var response = await http.PostAsync("/api/rooms",new System.Net.Http.StringContent("{\"name\":\"合成连麦验证\"}")); response.EnsureSuccessStatusCode();
        using var json = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync()); var data = json.RootElement;
        var invite = new RoomInvite("http://127.0.0.1:4096",data.GetProperty("id").GetString()!,data.GetProperty("listenToken").GetString()!,System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var candidate = new RoomClient(profile, audioFactory: publish => new SyntheticRoomAudio(publish));
        await candidate.JoinAsync(invite,"合成麦克风 · 无设备采集",data.GetProperty("writeToken").GetString());room=candidate;ShowRoom();StatusText.Text="合成验证：没有读取麦克风、屏幕或播放器";
    }
    public async Task ReceiveLinkAsync(string link)
    { RestoreWindow(); if (busy || updates.Busy) { InviteBox.Text = link; StatusText.Text = "先完成当前操作，再加入邀请的房间。"; return; } await JoinTargetAsync(link); }
    public void RestoreWindow() { Show(); if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal; Activate(); }
    private async Task<bool> PrepareSwitchAsync()
    {
        if (room is null) return true;
        if (MessageBox.Show(this,"进入其他房间将离开当前房间。现在切换吗？","切换房间",MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes) return false;
        await LeaveAsync(false); return true;
    }
    private async Task ConnectAsync(Func<RoomClient,string,Task> connect)
    {
        if (busy || updates.Busy || !await PrepareSwitchAsync()) return;
        SaveNickname(); busy=true; SubmitCreateButton.IsEnabled=false; var candidate=new RoomClient();
        try { StatusText.Text="正在进入房间…";await connect(candidate,profile.Read().Nickname);room=candidate;rosterSignature="";ShowRoom();CloseDrawer(); }
        catch(Exception ex){await candidate.DisposeAsync();StatusText.Text="没有进入房间："+ex.GetBaseException().Message;}
        finally{busy=false;SubmitCreateButton.IsEnabled=true;RefreshRoom();}
    }
    private void SaveNickname()
    { string name=ProfileSettings.CleanName(NicknameBox.Text);if(name.Length==0)name="朋友";NicknameBox.Text=name;profile.Save(profile.Read() with{Nickname=name});if(room?.Connected==true&&room.Name!=name)room.Rename(name); }
    private void Nickname_LostFocus(object sender,RoutedEventArgs e)=>SaveNickname();
    private async Task RefreshLobbyAsync()
    {
        try{lobby=await LobbyClient.ListAsync();FilterRooms();if(room is null&&!busy)StatusText.Text="公开房间 "+lobby.Length+" 个。也可以使用朋友的邀请加入。";}
        catch(Exception){if(room is null&&!busy)StatusText.Text="大厅暂时无法刷新，可使用邀请加入。"; }
    }
    private void FilterRooms()
    {if(RoomList is null)return;string query=SearchBox.Text.Trim();var visible=lobby.Where(r=>r.Name.Contains(query,StringComparison.OrdinalIgnoreCase)||r.OwnerName.Contains(query,StringComparison.OrdinalIgnoreCase)).ToArray();RoomList.ItemsSource=visible;EmptyLobbyText.Visibility=visible.Length==0?Visibility.Visible:Visibility.Collapsed;}
    private void Search_Changed(object sender,TextChangedEventArgs e)=>FilterRooms();
    private async void Refresh_Click(object sender,RoutedEventArgs e)=>await RefreshLobbyAsync();
    private void Lobby_Click(object sender,RoutedEventArgs e){LobbyView.Visibility=Visibility.Visible;RoomView.Visibility=Visibility.Collapsed;ViewTitle.Text="房间大厅";ViewHint.Text="找个房间，和朋友开黑。";_ = RefreshLobbyAsync();}
    private void ShowRoom(){LobbyView.Visibility=Visibility.Collapsed;RoomView.Visibility=Visibility.Visible;RefreshRoom();}
    private void Current_Click(object sender,RoutedEventArgs e){if(room is not null)ShowRoom();}
    private void Create_Click(object sender,RoutedEventArgs e){RoomNameBox.Text=profile.Read().Nickname+"的房间";PublicRoomCheck.IsChecked=false;OpenDrawer(CreatePanel);}
    private async void SubmitCreate_Click(object sender,RoutedEventArgs e)
    {string name=RoomNameBox.Text.Trim();if(name.Length==0){StatusText.Text="请给房间起一个名字。";return;}bool publicly=PublicRoomCheck.IsChecked==true;await ConnectAsync((client,nick)=>client.CreateAsync(nick,name,publicly));}
    private async void EnterPublic_Click(object sender,RoutedEventArgs e){if(sender is Button {Tag:string id})await JoinTargetAsync(RoomService.DefaultOrigin+"/rooms/"+id);}
    private async void Join_Click(object sender,RoutedEventArgs e)=>await JoinTargetAsync(InviteBox.Text);
    private async Task JoinTargetAsync(string target)
    {
        try{target=LobbyClient.Normalize(target);var id=LobbyClient.PublicId(target);RoomInvite invite=id is not null?await LobbyClient.ResolveAsync(id):RoomInvite.Parse(target);await ConnectAsync((client,name)=>client.JoinAsync(invite,name));}
        catch(Exception ex){StatusText.Text=ex.GetBaseException().Message;}
    }
    private async Task LeaveAsync(bool applyUpdate=true)
    {if(room is not {} current)return;room=null;await current.DisposeAsync();memberRows.Clear();rosterSignature="";RefreshRoom();Lobby_Click(this,new());if(applyUpdate)await updates.AfterLeavingAsync();}
    private async void Leave_Click(object sender,RoutedEventArgs e)=>await LeaveAsync();
    private async Task ApplyAsync(Func<AudioOptions,AudioOptions> change)
    {if(syncing||room is null)return;try{await room.UpdateOptionsAsync(change(room.Audio.Options));}catch(OperationCanceledException){}catch(Exception ex){StatusText.Text=ex.GetBaseException().Message;}}
    private Task ToggleMicrophoneAsync()=>ApplyAsync(options=>options with{Microphone=!options.Microphone});
    private async void Mic_Click(object sender,RoutedEventArgs e)=>await ToggleMicrophoneAsync();
    private async void Music_Click(object sender,RoutedEventArgs e)
    {if(room is null)return;if(!room.Audio.Options.Sharing&&string.IsNullOrEmpty(room.Audio.Options.SourceId)){StatusText.Text="先播放目标程序，再选择音乐来源。";return;}await ApplyAsync(options=>options with{Sharing=!options.Sharing});}
    private async void Noise_Click(object sender,RoutedEventArgs e)=>await ApplyAsync(options=>options with{NoiseSuppression=!options.NoiseSuppression});
    private async void TalkMode_Changed(object sender,SelectionChangedEventArgs e){if(!syncing&&room is not null)await ApplyAsync(options=>options with{TalkMode=TalkModeBox.SelectedIndex==1?"ptt":"free"});}
    private async void Source_Changed(object sender,SelectionChangedEventArgs e){if(!syncing&&SourceBox.SelectedValue is string id)await ApplyAsync(options=>options with{SourceId=id});}
    private async void Device_Changed(object sender,SelectionChangedEventArgs e)
    {if(!syncing&&room is not null)await ApplyAsync(options=>options with{MicrophoneId=MicrophoneBox.SelectedValue as string??options.MicrophoneId,OutputId=OutputBox.SelectedValue as string??options.OutputId});}
    private async void Gain_Changed(object sender,RoutedPropertyChangedEventArgs<double> e)
    {if(!syncing&&room is not null)await ApplyAsync(options=>options with{MicrophoneGain=(float)MicGainSlider.Value,SpeakerGain=(float)SpeakerGainSlider.Value,MusicGain=(float)MusicGainSlider.Value});}
    private void BindKey_Click(object sender,RoutedEventArgs e){bindingKey=true;KeyButton.Content="按下新的说话键";}
    private async void BindKeyDown(object sender,KeyEventArgs e)
    {if(!bindingKey||room is null)return;e.Handled=true;bindingKey=false;int key=KeyInterop.VirtualKeyFromKey(e.Key==Key.System?e.SystemKey:e.Key);if(key>=8&&key<=254)await ApplyAsync(options=>options with{TalkKey=key});}
    private async void ChangeMemberGain(string id,byte kind,float value)
    {await ApplyAsync(options=>{var voice=new Dictionary<string,float>(options.MemberVoice);var music=new Dictionary<string,float>(options.MemberMusic);if(kind==0)voice[id]=value;else music[id]=value;return options with{MemberVoice=voice,MemberMusic=music};});}
    private void Copy_Click(object sender,RoutedEventArgs e){if(room is not null){Clipboard.SetText(room.Invite.Link);StatusText.Text="房间邀请已复制。";}}
    private void Open_Click(object sender,RoutedEventArgs e){if(room?.Connected==true)Process.Start(new ProcessStartInfo(room.ControllerLink){UseShellExecute=true});}
    private void Settings_Click(object sender,RoutedEventArgs e)=>OpenDrawer(SettingsPanel);
    private void OpenDrawer(FrameworkElement panel){Drawer.Visibility=Visibility.Visible;CreatePanel.Visibility=panel==CreatePanel?Visibility.Visible:Visibility.Collapsed;ManagePanel.Visibility=panel==ManagePanel?Visibility.Visible:Visibility.Collapsed;SettingsPanel.Visibility=panel==SettingsPanel?Visibility.Visible:Visibility.Collapsed;}
    private void CloseDrawer()=>Drawer.Visibility=Visibility.Collapsed;
    private void CloseDrawer_Click(object sender,RoutedEventArgs e)=>CloseDrawer();
    private void Manage_Click(object sender,RoutedEventArgs e){if(room?.Room is {} data){ManageNameBox.Text=data.Name;ManagePublicCheck.IsChecked=data.Public;OpenDrawer(ManagePanel);}}
    private void SaveManage_Click(object sender,RoutedEventArgs e){if(room is not null){room.Manage(ManageNameBox.Text,ManagePublicCheck.IsChecked==true);CloseDrawer();}}
    private void Kick_Click(object sender,RoutedEventArgs e){if(sender is Button{Tag:string id}&&room is not null){var member=room.Members.FirstOrDefault(m=>m.Id==id);if(MessageBox.Show(this,"将 "+member?.Name+" 移出房间？","移出成员",MessageBoxButton.YesNo)==MessageBoxResult.Yes)room.Kick(id);}}
    private async void Update_Click(object sender,RoutedEventArgs e)=>await updates.UpdateAsync();
    private async void CheckUpdate_Click(object sender,RoutedEventArgs e)=>await updates.CheckAsync();
    private async void Exit_Click(object sender,RoutedEventArgs e)=>await RequestExitAsync();
    public async Task RequestExitAsync()
    {if(exiting)return;if(busy||updates.Busy){StatusText.Text="当前操作完成后即可退出。";RestoreWindow();return;}exiting=true;timer.Stop();SaveNickname();await LeaveAsync(false);tray?.Dispose();Close();Application.Current.Shutdown();}
    private void OnClosing(object? sender,CancelEventArgs e){if(exiting)return;e.Cancel=true;SaveNickname();Hide();tray?.ShowBackgroundNotice();}
    private void RefreshRoom()
    {
        bool active=room?.SessionActive==true;MicButton.IsEnabled=LeaveButton.IsEnabled=CopyButton.IsEnabled=CurrentRoomButton.IsEnabled=OpenButton.IsEnabled=active;
        if(room is null){ConnectionText.Text="还没有加入房间";MicButton.Content="开启麦克风";ManageButton.Visibility=Visibility.Collapsed;return;}
        if(!NicknameBox.IsKeyboardFocusWithin&&room.Name!=NicknameBox.Text)NicknameBox.Text=room.Name;
        var audio=room.Audio;var options=audio.Options;
        ConnectionText.Text=room.Connected?((!options.Microphone||audio.MicrophoneReady)?"已连接":"已连接，麦克风设备待恢复"):"正在恢复连接";
        if(RoomView.Visibility==Visibility.Visible){ViewTitle.Text=room.Room?.Name??"音乐房间";ViewHint.Text=(room.Room?.Public==true?"公开房间":"邀请房间")+"，"+room.Members.Length+" / 8 人";}
        StatusText.Text=room.Connected?audio.Status:room.Status;tray?.SetTip(ConnectionText.Text+"，麦克风"+(options.Microphone?"开":"关"));
        syncing=true;
        try{
            MicButton.Content=options.Microphone?"关闭麦克风":"开启麦克风";NoiseButton.Content="麦克风降噪："+(options.NoiseSuppression?"开":"关");
            TalkModeBox.SelectedIndex=options.TalkMode=="ptt"?1:0;KeyButton.Content=bindingKey?"按下新的说话键":"说话键："+(options.TalkKey is >=65 and <=90?((char)options.TalkKey).ToString():options.TalkKey.ToString());
            if(!MicGainSlider.IsMouseCaptureWithin)MicGainSlider.Value=options.MicrophoneGain;if(!SpeakerGainSlider.IsMouseCaptureWithin)SpeakerGainSlider.Value=options.SpeakerGain;if(!MusicGainSlider.IsMouseCaptureWithin)MusicGainSlider.Value=options.MusicGain;
            MicGainText.Text="麦克风 "+options.MicrophoneGain.ToString("0")+"%";SpeakerGainText.Text="房间收听 "+options.SpeakerGain.ToString("0")+"%";MusicGainText.Text="音乐 "+options.MusicGain.ToString("0")+"%";MusicButton.Content=options.Sharing?"停止共享音乐":"开始共享音乐";
            string source=string.Join('|',audio.Inventory.Sources.Select(x=>x.Id+":"+x.Name));if(source!=sourceSignature){SourceBox.ItemsSource=audio.Inventory.Sources;sourceSignature=source;}SourceBox.SelectedValue=options.SourceId;SourceCountText.Text="检测到 "+audio.Inventory.Sources.Length+" 个音源";
            string inputs=string.Join('|',audio.Inventory.Microphones.Select(x=>x.Id));if(inputs!=inputSignature){MicrophoneBox.ItemsSource=audio.Inventory.Microphones;inputSignature=inputs;}MicrophoneBox.SelectedValue=options.MicrophoneId;
            string outputs=string.Join('|',audio.Inventory.Outputs.Select(x=>x.Id));if(outputs!=outputSignature){OutputBox.ItemsSource=audio.Inventory.Outputs;outputSignature=outputs;}OutputBox.SelectedValue=options.OutputId;
        }finally{syncing=false;}
        bool owner=room.Room?.OwnerId==room.MemberId;ManageButton.Visibility=owner?Visibility.Visible:Visibility.Collapsed;
        var roster=room.Members;string signature=string.Join('|',roster.Select(m=>m.Id+":"+m.Name))+":"+owner;
        if(signature!=rosterSignature){memberRows.Clear();foreach(var member in roster)memberRows.Add(new(member,member.Id==room.MemberId,owner,ChangeMemberGain));rosterSignature=signature;}
        foreach(var row in memberRows){var member=roster.FirstOrDefault(m=>m.Id==row.Id);if(member is not null)row.Refresh(member.Microphone,member.Music,member.Connected,row.Id==room.MemberId?audio.MicrophonePeak:audio.Mix.PeakFor(row.Id),options.MemberVoice.GetValueOrDefault(row.Id,100),options.MemberMusic.GetValueOrDefault(row.Id,100));}
    }
}

