using NAudio.Wave;
using VoiceBridge.App.Audio;
using VoiceBridge.App.Platform;
using Xunit;

namespace VoiceBridge.Tests;
public sealed class PlatformTests
{
    [Fact]
    public void RealProcessSnapshotHasUniqueRoots()
    {
        var roots = QQMusicFinder.Roots();
        Assert.Equal(roots.Length, roots.Distinct().Count());
    }

    [Fact]
    public void Signed24BitPcmConvertsCorrectly()
    {
        var input = new CaptureInput(new WaveFormat(48000, 24, 2));
        input.Add([0, 0, 0x80, 0xff, 0xff, 0x7f], 6);
        float[] b = new float[2]; input.Stereo.Read(b, 0, 2);
        Assert.Equal(-1, b[0]); Assert.InRange(b[1], .999999f, 1);
    }
    [Fact]
    public void MonoPcm16IsCopiedToBothStereoChannels()
    {
        var input = new CaptureInput(new WaveFormat(48000, 16, 1));
        input.Add([0, 0x40], 2); float[] b = new float[2]; input.Stereo.Read(b, 0, 2);
        Assert.Equal(.5f, b[0]); Assert.Equal(b[0], b[1]);
    }
    [WindowsAudioFact]
    [Trait("Category", "WindowsAudio")]
    public async Task NativeProcessLoopbackActivatesCapturesAndStops()
    {
        using var capture = new ProcessLoopback((uint)Environment.ProcessId);
        await capture.StartAsync();
        await Task.Delay(350);
        Assert.Null(capture.Failure);
        float[] b = new float[960]; capture.Queue.Read(b, 0, b.Length);
        Assert.All(b, x => Assert.True(float.IsFinite(x)));
    }
}

public sealed class WindowsAudioFactAttribute : FactAttribute
{
    public WindowsAudioFactAttribute()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348)) Skip = "Application loopback requires Windows Build 20348 or later.";
    }
}
