using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using VoiceBridge.App.Room;
namespace VoiceBridge.App;
public sealed class MemberRow(RoomMember member, bool self, bool canKick, Action<string,byte,float> change) : INotifyPropertyChanged
{
    private float voice = 100, music = 100, peak;
    private string status = "";
    public string Id => member.Id;
    public string Initial => string.IsNullOrEmpty(member.Name) ? "友" : System.Globalization.StringInfo.GetNextTextElement(member.Name);
    public string DisplayName => member.Name + (self ? "（你）" : "");
    public Visibility GainVisibility => self ? Visibility.Collapsed : Visibility.Visible;
    public Visibility KickVisibility => canKick && !self ? Visibility.Visible : Visibility.Collapsed;
    public string Status { get => status; private set { status = value; Notify(); } }
    public float Peak { get => peak; private set { peak = value; Notify(); } }
    public float VoiceGain { get => voice; set { if (Math.Abs(voice-value)<.1) return; voice=value; Notify(); change(Id,0,value); } }
    public float MusicGain { get => music; set { if (Math.Abs(music-value)<.1) return; music=value; Notify(); change(Id,1,value); } }
    public void Refresh(bool microphone, bool sharing, bool connected, float level, float voiceGain, float musicGain)
    { Status = !connected ? "正在重连" : (microphone ? level>.003?"正在讲话":"麦克风开启" : "麦克风关闭") + (sharing?" · 音乐共享":""); Peak=Math.Clamp(level*12,0,1);voice=voiceGain;music=musicGain;Notify(nameof(VoiceGain));Notify(nameof(MusicGain)); }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName]string? name=null)=>PropertyChanged?.Invoke(this,new(name));
}
