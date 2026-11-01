using VoiceBridge.Core;
namespace VoiceBridge.App.Audio;
public interface IRoomAudio : IAsyncDisposable
{
    AudioOptions Options { get; }
    AudioInventory Inventory { get; }
    RoomMix Mix { get; }
    string Status { get; }
    float MicrophonePeak { get; }
    bool Talking { get; }
    bool MicrophoneReady { get; }
    bool OutputReady { get; }
    Task StartAsync();
    Task ApplyAsync(AudioOptions next);
    void PrivacyMute();
    void SetNetworkConnected(bool connected);
}
