using System.IO;
using VoiceBridge.App.Audio;
using VoiceBridge.Core;
using Xunit;
namespace VoiceBridge.Tests;
public class ProfileAndNoiseTests
{
    [Fact]
    public void ProfilePersistsAcrossInstancesAndRecoversFromInvalidFiles()
    {
        string root = Path.Combine(Path.GetTempPath(), "VoiceBridge-profile-test-" + Guid.NewGuid().ToString("N"));
        string file = Path.Combine(root, "preferences.json");
        try
        {
            var store = new ProfileStore(file); Assert.Equal("朋友", store.Read().Nickname);
            Assert.True(store.Save(new() { Nickname = " 新昵称\0 ", NoiseSuppression = false }));
            var restored = new ProfileStore(file).Read(); Assert.Equal("新昵称", restored.Nickname); Assert.False(restored.NoiseSuppression);
            File.WriteAllText(file, "{invalid"); Assert.True(store.Read().NoiseSuppression); Assert.Equal("朋友", store.Read().Nickname);
            File.WriteAllText(file, "{\"version\":100,\"nickname\":\"bad\"}"); Assert.Equal("朋友", store.Read().Nickname);
            Assert.Equal(20, ProfileSettings.CleanName(new string('a', 30)).Length);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Fact]
    public void BundledRnnoiseProcessesFixedFramesAndSuppressesStationaryNoise()
    {
        using var processor = new MicrophoneDenoiser(); var frame = new float[960]; uint random = 12345; double input = 0, output = 0;
        for (int packet = 0; packet < 150; packet++)
        {
            for (int i = 0; i < frame.Length; i++) { random ^= random << 13; random ^= random >> 17; random ^= random << 5; frame[i] = ((random & 65535) / 32768f - 1) * .025f; if (packet >= 50) input += frame[i] * frame[i]; }
            processor.Process(frame);
            Assert.All(frame, sample => Assert.True(float.IsFinite(sample)));
            if (packet >= 50) output += frame.Sum(x => (double)x * x);
        }
        Assert.True(output < input * .5, "Stationary noise should be attenuated after warm-up");
        Assert.Throws<ArgumentException>(() => processor.Process(new float[100]));
    }
}
