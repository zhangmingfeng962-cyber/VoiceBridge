using VoiceBridge.Core;
using Xunit;
namespace VoiceBridge.Tests;
public class BroadcastGainTests
{
    [Fact]
    public void UnityPreservesSamplesAndMuteDoesNotDestroyInputSource()
    {
        var gain = new BroadcastGain();
        float[] original = Enumerable.Range(0, 960).Select(i => MathF.Sin(i) * .7f).ToArray();
        var values = original.ToArray(); gain.Apply(values); Assert.Equal(original, values);
        gain.Muted = true; gain.Apply(values); values = original.ToArray(); gain.Apply(values); Assert.All(values, x => Assert.Equal(0, x));
        gain.Muted = false; gain.Apply(values); values = original.ToArray(); gain.Apply(values); Assert.Equal(original, values);
    }
    [Fact]
    public void VolumeIsFixedAndIndependentOfSignalAndRampsBothChannelsTogether()
    {
        var gain = new BroadcastGain { Volume = .4f }; float[] values = Enumerable.Repeat(.5f, 960).ToArray(); gain.Apply(values);
        Assert.InRange(values[0], .19f, .5f); Assert.Equal(values[0], values[1]); Assert.InRange(values[^1], .19999f, .20001f);
        values = Enumerable.Repeat(-.8f, 960).ToArray(); gain.Apply(values); Assert.All(values, x => Assert.InRange(x, -.320001f, -.319999f));
    }
}
