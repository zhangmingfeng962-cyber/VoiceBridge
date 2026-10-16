using System.Text.Json;
using VoiceBridge.App.Audio;
using VoiceBridge.App.Platform;
using VoiceBridge.Core;
using Xunit;

namespace VoiceBridge.Tests;

public sealed class ScenarioTests
{
    [Fact]
    public void LegacySettingsMigrateWithoutChangingSoundLevelsOrStartingSound()
    {
        var settings = JsonSerializer.Deserialize<Settings>("{\"MicrophoneVolume\":170,\"MusicVolume\":78,\"MusicPlayerId\":\"netease\",\"MicrophoneId\":\"example-mic\"}")!;
        settings.Normalize();
        var profile = settings.GetScenario("general");
        Assert.Equal(170, profile.MicrophoneVolume); Assert.Equal(78, profile.MusicVolume);
        Assert.Equal("netease", profile.MusicPlayerId); Assert.Equal("example-mic", profile.MicrophoneId);
        Assert.Equal(MusicChannelMode.Stereo, profile.ChannelMode); Assert.Equal(20, profile.OutputBufferMs);
        Assert.False(profile.HasMatchingPeerConfirmation);
    }

    [Fact]
    public void SeparateSoftwareProfilesRoundTripWithoutSharingParameterObjects()
    {
        var settings = new Settings(); settings.Normalize();
        var qq = settings.GetScenario("qq");
        qq.MusicVolume = 75; qq.MicrophoneVolume = 110; qq.MusicPlayerId = "kugou";
        qq.OutputBufferMs = 40; qq.ChannelMode = MusicChannelMode.LeftOnly;
        qq.ConfirmPeerFeedback(DateTimeOffset.Parse("2026-10-16T10:00:00+08:00"));
        var wechat = settings.GetScenario("wechat"); wechat.MusicVolume = 25; wechat.OutputBufferMs = 60;
        settings.ScenarioId = "qq"; settings.Normalize();
        var restored = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(settings))!; restored.Normalize();
        Assert.Equal(75, restored.MusicVolume); Assert.Equal("kugou", restored.MusicPlayerId);
        Assert.Equal(25, restored.GetScenario("wechat").MusicVolume); Assert.Equal(60, restored.GetScenario("wechat").OutputBufferMs);
        Assert.True(restored.GetScenario("qq").HasMatchingPeerConfirmation);
        Assert.False(restored.GetScenario("wechat").HasMatchingPeerConfirmation);
        restored.GetScenario("wechat").MusicVolume = 90;
        Assert.Equal(75, restored.GetScenario("qq").MusicVolume);
    }

    [Fact]
    public void ChangingAnyRecordedAudioParameterRequiresNewPeerFeedback()
    {
        Action<ScenarioAudioProfile>[] changes =
        [p => p.MicrophoneVolume = 90, p => p.MusicVolume = 70, p => p.MusicPlayerId = "netease",
         p => p.ChannelMode = MusicChannelMode.RightOnly, p => p.OutputBufferMs = 40,
         p => p.MicrophoneId = "different-mic", p => p.CableId = "different-cable"];
        foreach (var change in changes)
        {
            var profile = new ScenarioAudioProfile(); profile.ConfirmPeerFeedback(DateTimeOffset.Now);
            Assert.True(profile.HasMatchingPeerConfirmation);
            change(profile); Assert.False(profile.HasMatchingPeerConfirmation);
        }
    }

    [Fact]
    public void WindowsWideRoutingChangesInvalidateFeedbackForAllSavedScenarios()
    {
        var settings = new Settings(); settings.Normalize();
        settings.GetScenario("qq").ConfirmPeerFeedback(DateTimeOffset.Now);
        settings.GetScenario("wechat").ConfirmPeerFeedback(DateTimeOffset.Now);
        settings.InvalidatePeerConfirmations();
        Assert.False(settings.GetScenario("qq").HasMatchingPeerConfirmation);
        Assert.False(settings.GetScenario("wechat").HasMatchingPeerConfirmation);
    }

    [Fact]
    public void MalformedProfilesAreNormalizedAndDoNotInventPeerConfirmation()
    {
        var settings = new Settings { ScenarioId = "WECHAT", ScenarioProfiles = new()
        {
            ["WECHAT"] = new() { MusicVolume = double.NaN, MicrophoneVolume = double.PositiveInfinity,
                MusicPlayerId = "system", ChannelMode = (MusicChannelMode)999, OutputBufferMs = 1 },
            ["qq"] = null!, ["unknown"] = new()
        } };
        settings.Normalize();
        var profile = settings.GetScenario("wechat");
        Assert.Equal("wechat", settings.ScenarioId); Assert.Equal(100, profile.MicrophoneVolume);
        Assert.Equal(35, profile.MusicVolume); Assert.Equal("qqmusic", profile.MusicPlayerId);
        Assert.Equal(20, profile.OutputBufferMs); Assert.Equal(MusicChannelMode.Stereo, profile.ChannelMode);
        Assert.False(profile.HasMatchingPeerConfirmation); Assert.False(settings.ScenarioProfiles.ContainsKey("unknown"));
    }

    [Fact]
    public async Task InvalidEngineParametersFailBeforeOpeningMicrophones()
    {
        using var engine = new AudioEngine();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => engine.StartAsync("unused", "unused", 1, 1, outputBufferMs: 1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => engine.StartAsync("unused", "unused", 1, 1, channelMode: (MusicChannelMode)999));
        Assert.False(engine.Running);
    }
}
