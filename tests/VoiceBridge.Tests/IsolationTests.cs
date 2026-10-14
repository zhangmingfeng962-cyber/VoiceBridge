using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using VoiceBridge.Core;
using VoiceBridge.App.Audio;
using Xunit;
using Xunit.Abstractions;

namespace VoiceBridge.Tests;
[Collection("AudioIntegration")]
public sealed class IsolationTests(ITestOutputHelper log)
{
    [AudioIntegrationFact]
    [Trait("Category", "OptInIntegration")]
    public async Task ProcessCaptureIncludesTargetAndRejectsSiblingOnSameEndpoint()
    {
        // Opt in because this sends synthetic audio into an existing virtual microphone.
        string fixture = Environment.GetEnvironmentVariable("VOICEBRIDGE_FIXTURE") ?? throw new InvalidOperationException("Set VOICEBRIDGE_FIXTURE to the fixture DLL.");
        string dotnet = Environment.GetEnvironmentVariable("VOICEBRIDGE_DOTNET") ?? "dotnet";
        using var enumerator = new MMDeviceEnumerator();
        using var cable = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).First(AudioDevices.IsCableRender);
        // Do not feed synthetic audio into a cable which an active recorder is already using.
        foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)) using (d)
        {
            if (!AudioDevices.IsCable(d)) continue;
            var sessions = d.AudioSessionManager.Sessions;
            for (int i = 0; i < sessions.Count; i++) using (var s = sessions[i])
                Assert.False(s.State == NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateActive, "Virtual microphone is in use. Close voice/recording apps before running this test.");
        }
        AudioDevices.CheckForeignSessions(cable);
        using var target = Start(dotnet, fixture, cable.ID, "437");
        using var sibling = Start(dotnet, fixture, cable.ID, "1301");
        try
        {
            Assert.Equal("READY", await target.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal("READY", await sibling.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));
            using var capture = new ProcessLoopback((uint)target.Id);
            await capture.StartAsync();
            await Task.Delay(150);
            capture.Queue.Clear();
            float[] block = new float[5760]; List<float> mono = [];
            for (int n = 0; n < 60; n++)
            {
                await Task.Delay(5);
                int count = Math.Min(block.Length, capture.Queue.BufferedSamples);
                capture.Queue.Read(block, 0, count);
                for (int i = 0; i < count; i += 2) mono.Add(block[i]);
            }
            Assert.Null(capture.Failure);
            double included = Magnitude(mono, 437), excluded = Magnitude(mono, 1301);
            log.WriteLine($"Frames={mono.Count}; target amplitude={included:F8}; sibling amplitude={excluded:F8}; separation={20 * Math.Log10(included / Math.Max(excluded, 1e-12)):F1} dB");
            Assert.True(mono.Count > 4800, "No usable application-loopback packets were received.");
            Assert.True(included > .01, $"Target amplitude too low: {included}");
            Assert.True(excluded < included / 30, $"Sibling not isolated: target={included}, sibling={excluded}");
        }
        finally
        {
            if (!target.HasExited) target.Kill(true);
            if (!sibling.HasExited) sibling.Kill(true);
            await target.WaitForExitAsync(); await sibling.WaitForExitAsync();
        }
    }
    [AudioIntegrationFact]
    [Trait("Category", "OptInIntegration")]
    public async Task MixedAudioTravelsThroughRealVirtualCable()
    {
        using var e = new MMDeviceEnumerator();
        using var playback = e.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).First(AudioDevices.IsCableRender);
        using var recording = e.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).First(AudioDevices.IsCable);
        AudioDevices.CheckForeignSessions(playback);
        var sessions = recording.AudioSessionManager.Sessions;
        for (int i = 0; i < sessions.Count; i++) using (var s = sessions[i])
            Assert.False(s.State == NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateActive, "Virtual microphone is in use.");
        using var capture = new WasapiCapture(recording, true, 10);
        var input = new CaptureInput(capture.WaveFormat);
        capture.DataAvailable += (_, data) => input.Add(data.Buffer, data.BytesRecorded);
        using var output = new WasapiOut(playback, AudioClientShareMode.Shared, true, 20);
        var mix = new MixBus(new Sine(437, .1f), new Sine(1301, .1f)) { MicGain = 1, MusicGain = 1, MusicEnabled = true };
        output.Init(new SampleAdapter(mix).ToWaveProvider());
        capture.StartRecording(); output.Play();
        try
        {
            await Task.Delay(400);
            float[] samples = new float[4800]; input.Stereo.Read(samples, 0, samples.Length);
            List<float> mono = []; for (int i = 0; i < samples.Length; i += 2) mono.Add(samples[i]);
            double first = Magnitude(mono, 437), second = Magnitude(mono, 1301);
            log.WriteLine($"Virtual cable capture format={capture.WaveFormat}; mixed voice test tone={first:F8}; music test tone={second:F8}");
            Assert.True(first > .02 && second > .02, $"Mixed tones missing: {first}, {second}");
        }
        finally { output.Stop(); capture.StopRecording(); }
    }
    private sealed class Sine(double hz, float amplitude) : IFloatSource
    {
        private long frame;
        public int Read(float[] b, int o, int c)
        {
            for (int i = 0; i < c; i += 2, frame++) b[o + i] = b[o + i + 1] = amplitude * (float)Math.Sin(2 * Math.PI * hz * frame / 48000);
            return c;
        }
    }
    private static Process Start(string dotnet, string dll, string id, string hz)
    {
        var info = new ProcessStartInfo(dotnet) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        info.ArgumentList.Add(dll); info.ArgumentList.Add(id); info.ArgumentList.Add(hz); info.ArgumentList.Add("8");
        return Process.Start(info) ?? throw new InvalidOperationException("Fixture did not start.");
    }
    private static double Magnitude(List<float> signal, double hz)
    {
        double re = 0, im = 0, sum = 0;
        for (int n = 0; n < signal.Count; n++)
        {
            double window = .5 - .5 * Math.Cos(2 * Math.PI * n / (signal.Count - 1));
            double phase = 2 * Math.PI * hz * n / 48000;
            re += signal[n] * window * Math.Cos(phase); im += signal[n] * window * Math.Sin(phase); sum += window;
        }
        return 2 * Math.Sqrt(re * re + im * im) / sum;
    }
}

[CollectionDefinition("AudioIntegration", DisableParallelization = true)]
public sealed class AudioIntegrationCollection { }

public sealed class AudioIntegrationFactAttribute : FactAttribute
{
    public AudioIntegrationFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("VOICEBRIDGE_RUN_AUDIO_INTEGRATION") != "1")
            Skip = "Opt in only with an idle VB-CABLE and closed voice/recording apps; see docs/VALIDATION.zh-CN.md.";
    }
}
