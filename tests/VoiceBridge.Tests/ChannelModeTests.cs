using VoiceBridge.Core;
using Xunit;

namespace VoiceBridge.Tests;

public sealed class ChannelModeTests
{
    private sealed class Stereo(float left, float right) : IFloatSource
    {
        public int Read(float[] buffer, int offset, int count)
        { for (int i = 0; i < count; i += 2) { buffer[offset + i] = left; buffer[offset + i + 1] = right; } return count; }
    }

    [Theory]
    [InlineData(MusicChannelMode.Stereo, .35f, -.15f)]
    [InlineData(MusicChannelMode.MonoAverage, .15f, .05f)]
    [InlineData(MusicChannelMode.LeftOnly, .35f, .25f)]
    [InlineData(MusicChannelMode.RightOnly, -.05f, -.15f)]
    public void MusicChannelSelectionChangesMusicButKeepsMicrophoneChannels(MusicChannelMode mode, float left, float right)
    {
        var mix = new MixBus(new Stereo(.1f, -.1f), new Stereo(.6f, -.2f)) { MusicEnabled = true, MusicGain = 1, ChannelMode = mode };
        float[] buffer = new float[1920]; mix.Read(buffer, 0, buffer.Length);
        Assert.InRange(buffer[^2] - left, -.000001f, .000001f);
        Assert.InRange(buffer[^1] - right, -.000001f, .000001f);
    }

    [Fact]
    public void OppositePhaseStereoIsPreservedByDefaultAndCanBeDiagnosedWithOneChannel()
    {
        var mix = new MixBus(new Stereo(0, 0), new Stereo(.8f, -.8f)) { MusicEnabled = true, MusicGain = 1 };
        float[] buffer = new float[1920]; mix.Read(buffer, 0, buffer.Length);
        Assert.InRange(buffer[^2], .399999f, .400001f); Assert.InRange(buffer[^1], -.400001f, -.399999f);
        mix.ChannelMode = MusicChannelMode.MonoAverage; mix.Read(buffer, 0, buffer.Length);
        Assert.All(buffer, x => Assert.Equal(0, x));
        mix.ChannelMode = MusicChannelMode.LeftOnly; mix.Read(buffer, 0, buffer.Length);
        Assert.All(buffer, x => Assert.InRange(x, .399999f, .400001f));
    }
}
