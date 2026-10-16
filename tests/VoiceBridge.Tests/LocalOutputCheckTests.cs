using System.IO;
using NAudio.Wave;
using VoiceBridge.App.Audio;
using Xunit;

namespace VoiceBridge.Tests;

public sealed class LocalOutputCheckTests
{
    private static string Clip(float[] samples, WaveFormat format)
    {
        string path = Path.Combine(Path.GetTempPath(), "VoiceBridge-test-" + Guid.NewGuid().ToString("N") + ".wav");
        using var writer = new WaveFileWriter(path, format);
        writer.WriteSamples(samples, 0, samples.Length);
        return path;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FloatAndPcmFilesReportKnownLevelsWithoutUsingDevices(bool floatingPoint)
    {
        float[] samples = new float[48000 * 2];
        for (int frame = 0; frame < 48000; frame++) samples[frame * 2] = samples[frame * 2 + 1] = .25f * MathF.Sin((float)(2 * Math.PI * 1000 * frame / 48000));
        string path = Clip(samples, floatingPoint ? WaveFormat.CreateIeeeFloatWaveFormat(48000, 2) : new WaveFormat(48000, 16, 2));
        try
        {
            var result = LocalOutputCheck.Analyze(path);
            Assert.Equal(1, result.DurationSeconds, 6); Assert.Equal(48000, result.SampleRate); Assert.Equal(2, result.Channels);
            Assert.InRange(result.Peak, .2499, .2501); Assert.InRange(result.RmsDbFs, -15.06, -15.04);
            Assert.Equal(0, result.NearSilentWindowPercent); Assert.Equal(0, result.NearFullScaleSamples);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void SilentWindowsAreMeasuredWithoutInferringPlatformFiltering()
    {
        float[] samples = new float[3840]; Array.Fill(samples, .2f, 1920, 1920);
        string path = Clip(samples, WaveFormat.CreateIeeeFloatWaveFormat(48000, 1));
        try
        {
            var result = LocalOutputCheck.Analyze(path);
            Assert.Equal(.08, result.DurationSeconds, 6); Assert.Equal(50, result.NearSilentWindowPercent);
            Assert.InRange(result.Peak, .19999, .20001);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void EmptyFileReturnsFiniteStatistics()
    {
        string path = Clip([], WaveFormat.CreateIeeeFloatWaveFormat(48000, 2));
        try
        {
            var result = LocalOutputCheck.Analyze(path);
            Assert.Equal(0, result.DurationSeconds); Assert.Equal(-120, result.RmsDbFs);
            Assert.Equal(0, result.Peak); Assert.Equal(0, result.NearSilentWindowPercent);
        }
        finally { File.Delete(path); }
    }
}
