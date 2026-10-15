using System.Text.Json;
using VoiceBridge.App.Audio;
using VoiceBridge.App.Platform;
using Xunit;

namespace VoiceBridge.Tests;

public sealed class MusicPlayerTests
{
    [Theory]
    [InlineData("qqmusic", 10u)]
    [InlineData("netease", 20u)]
    [InlineData("kugou", 30u)]
    public void SelectedPlayerIncludesOnlyItsRootsAndDeduplicatesItsChildProcesses(string playerId, uint expected)
    {
        MusicProcessInfo[] processes =
        [
            new(10, 1, "QQMusic.exe"), new(11, 10, "QQMusicExternal.exe"), new(12, 11, "qqmusic.EXE"),
            new(20, 1, "CLOUDMUSIC.EXE"), new(21, 20, "cloudmusic_helper.exe"), new(22, 21, "cloudmusic.exe"),
            new(30, 1, "KuGou.exe"), new(31, 30, "kgservice.exe"), new(32, 31, "kugou.exe"),
            new(40, 1, "VALORANT.exe"), new(41, 1, "Discord.exe"), new(42, 1, "msedge.exe")
        ];
        Assert.Equal(new[] { expected }, MusicProcessFinder.SelectRoots(playerId, processes));
    }

    [Fact]
    public void LookalikeNamesAndUnrelatedHelpersAreNotCaptureRoots()
    {
        MusicProcessInfo[] processes = [new(1, 0, "cloudmusic_helper.exe"), new(2, 0, "notcloudmusic.exe"), new(3, 0, "KuGou.exe.backup")];
        foreach (var player in MusicPlayers.All)
            Assert.Empty(MusicProcessFinder.SelectRoots(player.Id, processes));
    }

    [Fact]
    public void SeparateInstancesAreCapturedOnceEachAndAncestorCyclesTerminate()
    {
        MusicProcessInfo[] processes = [new(20, 50, "cloudmusic.exe"), new(25, 1, "cloudmusic.exe"), new(50, 60, "helper.exe"), new(60, 50, "helper.exe")];
        Assert.Equal(new uint[] { 20, 25 }, MusicProcessFinder.SelectRoots("netease", processes));
    }

    [Fact]
    public async Task UnknownPlayersFailBeforeOpeningAudioDevices()
    {
        Assert.Throws<ArgumentException>(() => MusicProcessFinder.SelectRoots("system", []));
        using var engine = new AudioEngine();
        await Assert.ThrowsAsync<ArgumentException>(() => engine.StartAsync("unused", "unused", 1, 1, musicPlayerId: "system"));
        Assert.False(engine.Running);
    }

    [Fact]
    public void PlayerSelectionRoundTripsAndOldSettingsStillDefaultToQQMusic()
    {
        var old = JsonSerializer.Deserialize<Settings>("{\"MusicVolume\":42}")!;
        Assert.Equal("qqmusic", old.MusicPlayerId);
        var settings = new Settings { MusicPlayerId = "netease", MusicVolume = 42, MicrophoneVolume = 85 };
        var restored = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(settings))!;
        Assert.Equal("netease", restored.MusicPlayerId);
        Assert.Equal(42, restored.MusicVolume);
        Assert.Equal(85, restored.MicrophoneVolume);
        Assert.Equal("qqmusic", MusicPlayers.GetOrDefault("unknown").Id);
        Assert.Equal("kugou", MusicPlayers.GetOrDefault("KUGOU").Id);
    }
}
