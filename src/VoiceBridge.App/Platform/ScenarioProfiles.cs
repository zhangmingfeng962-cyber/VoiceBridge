using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VoiceBridge.Core;

namespace VoiceBridge.App.Platform;

public sealed record ScenarioDefinition(string Id, string Name, string Guide);
public sealed record ChannelChoice(MusicChannelMode Mode, string Name);
public sealed record BufferChoice(int Milliseconds, string Name);

public static class Scenarios
{
    private const string Common = "\n\nVoiceBridge 只调整送入平台之前的声音，不会自动修改目标软件内部的降噪或编码。\n先录制虚拟输出，再与对方实际接收的同一段音乐比较。\n默认保留立体声；改为单声道会改变立体声效果。左右相加可能使反相内容抵消，必要时用仅左 / 仅右声道对比。\n增大缓冲只能帮助本机调度断音，会增加延迟，不能关闭平台过滤。";
    public static IReadOnlyList<ScenarioDefinition> All { get; } = Array.AsReadOnly(new[]
    {
        new ScenarioDefinition("general", "通用 / 原声基准", "将目标软件输入选为 CABLE Output，输出选耳机。\n若平台提供原声 / 音乐 / 系统音频共享入口，按其官方说明选择适合音乐的通道。" + Common),
        new ScenarioDefinition("qq", "QQ 语音", "QQ 私聊、群语音的设置可能不同。\n在实际使用的通话界面确认输入为 CABLE Output。\n若当前版本提供降噪、自动增益或音乐模式选项，分别对比后记录结果；没有对应选项时不要假定已关闭。" + Common),
        new ScenarioDefinition("wechat", "微信通话", "在 Windows 微信的实际通话中确认麦克风来源。\n若版本没有开放原声或降噪开关，VoiceBridge 无法替它关闭处理。\n用同一段音乐比较本地虚拟输出与对方接收端，普通麦克风测试不能代替微信通话验收。" + Common),
        new ScenarioDefinition("valorant", "无畏契约", "在游戏语音设置中确认输入为 CABLE Output。\n对比持续按住语音键时的只放音乐、边说话边放音乐。按键通话只控制传输时机，不保证取消降噪。\n游戏运行时也检查本机缺样计数，排除负载造成的断音。" + Common),
        new ScenarioDefinition("pubg-emulator", "和平精英（模拟器）", "先确认模拟器 Windows 侧麦克风来源为 CABLE Output，再检查游戏侧语音设置。\n若模拟器提供麦克风增强或降噪选项，单独关闭后对比；游戏侧若也有相关选项，应分别检查。\n模拟器转接与游戏处理是不同环节，本机输出正常不能证明这两层都保留了音乐。" + Common)
    });
    public static IReadOnlyList<ChannelChoice> Channels { get; } = Array.AsReadOnly(new[]
    {
        new ChannelChoice(MusicChannelMode.Stereo, "保留立体声"), new ChannelChoice(MusicChannelMode.MonoAverage, "单声道：左右平均"),
        new ChannelChoice(MusicChannelMode.LeftOnly, "单声道：仅左声道"), new ChannelChoice(MusicChannelMode.RightOnly, "单声道：仅右声道")
    });
    public static IReadOnlyList<BufferChoice> Buffers { get; } = Array.AsReadOnly(new[]
    { new BufferChoice(20, "20 ms · 较低延迟"), new BufferChoice(40, "40 ms · 较大缓冲"), new BufferChoice(60, "60 ms · 更大缓冲") });
    public static ScenarioDefinition Get(string? id) => All.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase)) ?? All[0];
}

public sealed class ScenarioAudioProfile
{
    public string? MicrophoneId { get; set; }
    public string? CableId { get; set; }
    public double MicrophoneVolume { get; set; } = 100;
    public double MusicVolume { get; set; } = 35;
    public string MusicPlayerId { get; set; } = MusicPlayers.QQMusicId;
    public MusicChannelMode ChannelMode { get; set; }
    public int OutputBufferMs { get; set; } = 20;
    public string? PeerConfirmedSignature { get; set; }
    public DateTimeOffset? PeerConfirmedAt { get; set; }

    public void Normalize()
    {
        MicrophoneVolume = double.IsFinite(MicrophoneVolume) ? Math.Clamp(MicrophoneVolume, 0, 200) : 100;
        MusicVolume = double.IsFinite(MusicVolume) ? Math.Clamp(MusicVolume, 0, 100) : 35;
        MusicPlayerId = MusicPlayers.GetOrDefault(MusicPlayerId).Id;
        if (!Enum.IsDefined(ChannelMode)) ChannelMode = MusicChannelMode.Stereo;
        if (OutputBufferMs is not (20 or 40 or 60)) OutputBufferMs = 20;
    }
    public string Signature() => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
    { MicrophoneId, CableId, MicrophoneVolume, MusicVolume, MusicPlayerId, ChannelMode, OutputBufferMs }))));
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasMatchingPeerConfirmation => PeerConfirmedAt.HasValue && PeerConfirmedSignature == Signature();
    public void ConfirmPeerFeedback(DateTimeOffset timestamp) { PeerConfirmedAt = timestamp; PeerConfirmedSignature = Signature(); }
}
