using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using VoiceBridge.App.Audio;
using VoiceBridge.Core;
using Xunit;

namespace VoiceBridge.Tests;
public class RoomTests
{
    [Fact]
    public void InteropFixtureUsesSyntheticSignalsOnly()
    {
        var cipher = new RoomCipher(Enumerable.Repeat((byte)1, 32).ToArray(), new string('a', 32));
        using var encoder = new RoomEncoder(true); var packets = new List<string>(); long frame = 0;
        for (uint n = 0; n < 50; n++)
        {
            var pcm = new float[1920]; for (int i = 0; i < 960; i++, frame++) { pcm[i * 2] = .1f * MathF.Sin((float)(2 * Math.PI * 437 * frame / 48000)); pcm[i * 2 + 1] = .08f * MathF.Sin((float)(2 * Math.PI * 997 * frame / 48000)); }
            packets.Add(Convert.ToBase64String(cipher.SealAudio(1, new string('b', 32), n, encoder.Encode(pcm))));
        }
        Assert.Equal(50, packets.Count);
        if (Environment.GetEnvironmentVariable("VOICEBRIDGE_INTEROP_FIXTURE") is { } path)
            File.WriteAllText(path, JsonSerializer.Serialize(new { room = new string('a', 32), key = Convert.ToHexString(Enumerable.Repeat((byte)1, 32).ToArray()).ToLowerInvariant(), member = new string('b', 32), packets }));
    }
    [Fact]
    public void BrowserOpusPacketsDecodeInNativeCompanion()
    {
        if (Environment.GetEnvironmentVariable("VOICEBRIDGE_BROWSER_FIXTURE") is not { } path) return;
        using var json = JsonDocument.Parse(File.ReadAllText(path)); using var decoder = new RoomDecoder(true);
        var packets = json.RootElement.GetProperty("browserPackets").EnumerateArray().ToArray(); Assert.InRange(packets.Length, 10, 11); double energy = 0;
        foreach (var packet in packets) { var decoded = decoder.Decode(Convert.FromBase64String(packet.GetString()!)); Assert.Equal(1920, decoded.Length); energy += decoded.Sum(x => x * x); }
        Assert.True(energy > 1);
    }
    [Fact]
    public void InviteNeverCopiesDesktopControlCapability()
    {
        string root = "https://room.example/room/" + new string('a', 32) + "#" + new string('b', 64) + "." + new string('c', 64);
        var invite = RoomInvite.Parse(root + ".desktop." + new string('d', 32) + "." + new string('e', 64));
        Assert.Equal(root, invite.Link); Assert.DoesNotContain("desktop", invite.Link);
        Assert.Throws<ArgumentException>(() => RoomInvite.Parse(root.Replace("https:", "http:")));
        Assert.Throws<ArgumentException>(() => RoomInvite.Parse(root[..^5]));
    }
    [Fact]
    public void AudioAuthenticatesMemberKindSequenceAndCiphertext()
    {
        var key = RandomNumberGenerator.GetBytes(32); var cipher = new RoomCipher(key, new string('a', 32)); string id = new('b', 32); byte[] opus = RandomNumberGenerator.GetBytes(500);
        byte[] packet = cipher.SealAudio(1, id, 8, opus); var decoded = cipher.OpenAudio(id, packet); Assert.Equal(opus, decoded.Opus); Assert.Equal(8u, decoded.Sequence);
        Assert.ThrowsAny<CryptographicException>(() => cipher.OpenAudio(new string('c', 32), packet));
        packet[4] = 0; Assert.ThrowsAny<CryptographicException>(() => cipher.OpenAudio(id, packet)); packet[4] = 1;
        packet[5]++; Assert.ThrowsAny<CryptographicException>(() => cipher.OpenAudio(id, packet)); packet[5]--;
        packet[^1] ^= 1; Assert.ThrowsAny<CryptographicException>(() => cipher.OpenAudio(id, packet));
    }
    [Fact]
    public void EncryptedControlCannotBeReusedAcrossControllerConnections()
    {
        var cipher = new RoomCipher(RandomNumberGenerator.GetBytes(32), new string('a', 32)); string id = new('b', 32), conn = new('c', 32);
        var packet = JsonSerializer.SerializeToElement(cipher.SealControl(id, conn, 1, new { action = "configure", microphoneGain = 250 }, false));
        using var command = cipher.OpenControl(id, conn, packet); Assert.Equal(250, command.RootElement.GetProperty("microphoneGain").GetInt32());
        Assert.ThrowsAny<CryptographicException>(() => cipher.OpenControl(id, new string('d', 32), packet));
    }
    [Fact]
    public void StereoCodecPreservesIndependentTonesAtMuchLowerBandwidth()
    {
        using var encoder = new RoomEncoder(true); using var decoder = new RoomDecoder(true); float[] pcm = new float[1920]; var left = new List<float>(); var right = new List<float>(); long frame = 0, encodedBytes = 0;
        for (int packet = 0; packet < 50; packet++)
        {
            for (int i = 0; i < 960; i++, frame++) { pcm[i * 2] = .2f * MathF.Sin((float)(2 * Math.PI * 437 * frame / 48000)); pcm[i * 2 + 1] = .15f * MathF.Sin((float)(2 * Math.PI * 997 * frame / 48000)); }
            byte[] opus = encoder.Encode(pcm); encodedBytes += opus.Length; var decoded = decoder.Decode(opus); for (int i = 0; i < 960; i++) { left.Add(decoded[i * 2]); right.Add(decoded[i * 2 + 1]); }
        }
        static double Energy(List<float> data, double frequency) { double real = 0, imaginary = 0; for (int i = 1000; i < data.Count; i++) { double angle = 2 * Math.PI * frequency * i / 48000; real += data[i] * Math.Cos(angle); imaginary += data[i] * Math.Sin(angle); } return real * real + imaginary * imaginary; }
        Assert.True(Energy(left, 437) > Energy(left, 997) * 30); Assert.True(Energy(right, 997) > Energy(right, 437) * 30);
        Assert.InRange(encodedBytes, 1000, 60000); // Former raw stereo float PCM was 384000 bytes/second.
        Assert.All(left, value => Assert.True(float.IsFinite(value)));
    }
    [Fact]
    public void BufferStartsAtLowLatencyGrowsDuringJitterAndStaysBounded()
    {
        var stream = new AdaptiveStream(2); float[] packet = Enumerable.Repeat(.2f, 1920).ToArray(), output = new float[1920];
        for (int n = 0; n < 4; n++) stream.Push(packet, 1000 + n * 20); stream.Mix(output, 1); Assert.All(output, value => Assert.Equal(0, value));
        stream.Push(packet, 1080); stream.Mix(output, 1); Assert.Contains(output, value => value > .1f); double initial = stream.TargetMs;
        stream.Push(packet, 1450); Assert.True(stream.TargetMs > initial);
        for (int n = 0; n < 1000; n++) stream.Push(packet, 1500 + n * 20); Assert.InRange(stream.BufferMs, 0, 800); Assert.True(stream.LateTrims > 0);
    }
    [Fact]
    public void IndividualVoiceMuteDoesNotChangeMusicSamples()
    {
        using var first = new RoomMix(); using var second = new RoomMix(); using var music = new RoomEncoder(true); using var voice = new RoomEncoder(false); float[] stereo = Enumerable.Repeat(.1f, 1920).ToArray(), mono = Enumerable.Repeat(.2f, 960).ToArray();
        first.SetGain("speaker", 0, 0);
        for (uint n = 0; n < 5; n++) { var m = music.Encode(stereo); first.Receive("musician", 1, n, m, 1000 + n * 20); second.Receive("musician", 1, n, m, 1000 + n * 20); first.Receive("speaker", 0, n, voice.Encode(mono), 1000 + n * 20); }
        var a = new float[960]; var b = new float[960]; first.Read(a, 0, a.Length); second.Read(b, 0, b.Length); Assert.Equal(b, a);
    }
    [Fact]
    public void BrowserCaptureNeverIncludesDesktopClientDescendantsOrExplorer()
    {
        Dictionary<uint, uint> parents = new() { [10] = 1, [11] = 10, [12] = 10, [20] = 10 };
        Dictionary<uint, string> names = new() { [1] = "explorer.exe", [10] = "chrome.exe", [11] = "chrome.exe", [12] = "chrome.exe", [20] = "VoiceBridge.exe" };
        Assert.Equal(11u, AudioSources.CaptureRoot(11, 20, parents, names)); Assert.Equal(0u, AudioSources.CaptureRoot(10, 20, parents, names));
        Assert.Equal(10u, AudioSources.CaptureRoot(11, 99, parents, names)); Assert.False(AudioSources.SafeProcess("VoiceBridge-v0.8.2")); Assert.False(AudioSources.SafeProcess("explorer"));
    }
    [Theory]
    [InlineData("CABLE Output (VB-Audio)")][InlineData("Voicemeeter Output")][InlineData("立体声混音")]
    public void VirtualInputsCannotReenterRoomVoice(string name) => Assert.False(AudioSources.SafeDevice(name));
}
