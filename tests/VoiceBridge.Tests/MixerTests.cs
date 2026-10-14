using VoiceBridge.Core;
using Xunit;

namespace VoiceBridge.Tests;
public sealed class MixerTests
{
    private sealed class Constant(float value) : IFloatSource
    {
        public long Consumed;
        public int Read(float[] b, int o, int c) { b.AsSpan(o, c).Fill(value); Consumed += c; return c; }
    }
    [Fact]
    public void OverflowKeepsNewestCompleteFrames()
    {
        var queue = new LiveSampleQueue(1000, 2, 10);
        queue.Write(Enumerable.Range(0, 16).Select(x => (float)x).ToArray());
        queue.Write(Enumerable.Range(16, 16).Select(x => (float)x).ToArray());
        float[] result = new float[20]; queue.Read(result, 0, 20);
        Assert.Equal(Enumerable.Range(12, 20).Select(x => (float)x), result);
        Assert.Equal(12, queue.DroppedSamples);
    }
    [Fact]
    public void UnderrunOutputsSilenceAndDoesNotReplayOldSamples()
    {
        var queue = new LiveSampleQueue(48000, 2);
        queue.Write([.2f, -.2f]);
        float[] result = [1, 1, 1, 1]; queue.Read(result, 0, 4);
        Assert.Equal(new float[] { .2f, -.2f, 0, 0 }, result);
        queue.Read(result, 0, 4); Assert.All(result, x => Assert.Equal(0, x));
        Assert.Equal(6, queue.MissingSamples);
    }
    [Fact]
    public void OversizedWriteIsBoundedAndRetainsLastFrames()
    {
        var q = new LiveSampleQueue(1000, 2, 10);
        q.Write(Enumerable.Range(0, 100).Select(x => (float)x).ToArray());
        Assert.Equal(10, q.BufferedMilliseconds);
        float[] b = new float[20]; q.Read(b, 0, 20);
        Assert.Equal(80, b[0]); Assert.Equal(99, b[^1]); Assert.Equal(80, q.DroppedSamples);
    }
    [Fact]
    public void MutedMusicIsConsumedWhileVoiceContinues()
    {
        var voice = new Constant(.2f); var music = new Constant(.5f);
        var mix = new MixBus(voice, music) { MicGain = 1, MusicGain = 1, MusicEnabled = false };
        float[] b = new float[960]; mix.Read(b, 0, b.Length);
        Assert.InRange(b[^1], .0999f, .1001f); Assert.Equal(960, music.Consumed);
        mix.MusicEnabled = true; mix.Read(b, 0, b.Length);
        Assert.InRange(b[^1], .3499f, .3501f);
        mix.MusicEnabled = false; mix.Read(b, 0, b.Length);
        Assert.InRange(b[^1], .0999f, .1001f); Assert.Equal(2880, music.Consumed);
    }
    [Fact]
    public void GainsAreIndependentAndRamped()
    {
        var mix = new MixBus(new Constant(.4f), new Constant(.2f)) { MicGain = .5f, MusicGain = .5f, MusicEnabled = true };
        float[] b = new float[960]; mix.Read(b, 0, b.Length);
        Assert.InRange(b[^1], .1499f, .1501f);
        mix.MicGain = 0; mix.Read(b, 0, b.Length);
        Assert.InRange(b[^1], .0499f, .0501f);
        for (int i = 2; i < b.Length; i += 2) Assert.True(Math.Abs(b[i] - b[i - 2]) <= .002f);
    }
    [Fact]
    public void OverloadedMixIsFiniteAndBounded()
    {
        var mix = new MixBus(new Constant(.99f), new Constant(.99f)) { MicGain = 2, MusicGain = 1, MusicEnabled = true };
        float[] b = new float[960];
        for (int n = 0; n < 500; n++) { mix.Read(b, 0, b.Length); Assert.All(b, v => Assert.InRange(v, -.95f, .95f)); }
        Assert.True(mix.LimiterReductionDb > 6);
        for (int i = 0; i < b.Length; i += 2) Assert.Equal(b[i], b[i + 1]);
    }
    [Fact]
    public void InvalidSamplesCannotPoisonTheMix()
    {
        var mix = new MixBus(new Constant(float.NaN), new Constant(float.PositiveInfinity)) { MusicEnabled = true };
        mix.MicGain = float.NaN; mix.MusicGain = float.PositiveInfinity;
        float[] b = new float[960]; mix.Read(b, 0, b.Length);
        Assert.All(b, x => Assert.Equal(0, x));
    }
    [Fact]
    public void ExtremeFiniteSamplesCannotOverflowIntoNoise()
    {
        var mix = new MixBus(new Constant(float.MaxValue), new Constant(float.MaxValue)) { MicGain = 2, MusicGain = 1, MusicEnabled = true };
        float[] b = new float[960]; mix.Read(b, 0, b.Length);
        Assert.All(b, x => Assert.InRange(x, -.95f, .95f));
    }
    [Fact]
    public void MicrophoneMuteKeepsMusicAndConsumesVoice()
    {
        var voice = new Constant(.4f); var music = new Constant(.2f);
        var mix = new MixBus(voice, music) { MicGain = 1, MusicGain = 1, MicrophoneEnabled = false, MusicEnabled = true };
        float[] b = new float[960]; mix.Read(b, 0, b.Length);
        Assert.InRange(b[^1], .0999f, .1001f);
        Assert.Equal(960, voice.Consumed);
        mix.MicrophoneEnabled = true; mix.Read(b, 0, b.Length);
        Assert.InRange(b[^1], .2999f, .3001f);
        mix.MicrophoneEnabled = false; mix.Read(b, 0, b.Length);
        Assert.InRange(b[^1], .0999f, .1001f);
    }
    private sealed class StereoReference : IFloatSource
    {
        public int Read(float[] b, int o, int c) { for (int i = 0; i < c; i += 2) { b[o + i] = .8f; b[o + i + 1] = -.8f; } return c; }
    }
    [Fact]
    public void LoudVoiceCannotDuckTheMusic()
    {
        var mix = new MixBus(new Constant(1), new StereoReference()) { MicGain = 2, MusicGain = 1, MusicEnabled = true };
        float[] b = new float[1920]; mix.Read(b, 0, b.Length);
        for (int i = 960; i < b.Length; i += 2)
            Assert.InRange((b[i] - b[i + 1]) / 2, .399999f, .400001f);
        Assert.True(mix.LimiterReductionDb > 6);
        Assert.All(b, sample => Assert.InRange(sample, -.95f, .95f));
    }
    private sealed class ReferenceTone : IFloatSource
    {
        public long Frame;
        public static float At(long frame) => .55f * MathF.Sin((float)(2 * Math.PI * 997 * frame / 48000)) + .25f * MathF.Sin((float)(2 * Math.PI * 7001 * frame / 48000));
        public int Read(float[] b, int o, int c) { for (int i = 0; i < c; i += 2, Frame++) b[o + i] = b[o + i + 1] = At(Frame); return c; }
    }
    [Fact]
    public void MusicPathPreservesWaveformWithConstantGain()
    {
        var source = new ReferenceTone();
        var mix = new MixBus(new Constant(0), source) { MusicGain = 1, MusicEnabled = true };
        float[] b = new float[960]; mix.Read(b, 0, b.Length);
        for (int packet = 0; packet < 500; packet++)
        {
            long first = source.Frame; mix.Read(b, 0, b.Length);
            for (int i = 0; i < b.Length; i += 2)
            {
                float expected = ReferenceTone.At(first + i / 2) * .5f;
                Assert.InRange(b[i] - expected, -.000001f, .000001f);
                Assert.Equal(b[i], b[i + 1]);
            }
        }
    }
    [Fact]
    public void BothSwitchesOffFadeToSilenceAndKeepSourcesCurrent()
    {
        var voice = new Constant(.3f); var music = new Constant(.3f);
        var mix = new MixBus(voice, music) { MusicEnabled = true };
        float[] b = new float[960]; mix.Read(b, 0, b.Length);
        mix.MicrophoneEnabled = mix.MusicEnabled = false;
        mix.Read(b, 0, b.Length);
        Assert.Equal(0, b[^1]);
        mix.Read(b, 0, b.Length); Assert.All(b, x => Assert.Equal(0, x));
        Assert.Equal(2880, voice.Consumed); Assert.Equal(2880, music.Consumed);
    }
    [Fact]
    public async Task ConcurrentCaptureAndRenderStayBounded()
    {
        var q = new LiveSampleQueue(48000, 2, 60);
        var writer = Task.Run(() => { float[] b = new float[960]; Array.Fill(b, .5f); for (int i = 0; i < 1000; i++) q.Write(b); });
        var reader = Task.Run(() => { float[] b = new float[960]; for (int i = 0; i < 1000; i++) { q.Read(b, 0, b.Length); Assert.All(b, x => Assert.InRange(x, 0, .5f)); } });
        await Task.WhenAll(writer, reader); Assert.InRange(q.BufferedMilliseconds, 0, 60);
    }
}
