using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace VoiceBridge.App.Audio;

public sealed record LocalClipStatistics(double DurationSeconds, int SampleRate, int Channels, double Peak,
    double RmsDbFs, double NearSilentWindowPercent, long NearFullScaleSamples);
public sealed record LocalClipResult(string FilePath, bool Cancelled, LocalClipStatistics Statistics);

public static class LocalOutputCheck
{
    public static Task<LocalClipResult> RecordAsync(string path, TimeSpan duration, CancellationToken token) => Task.Run(async () =>
    {
        if (duration <= TimeSpan.Zero || duration > TimeSpan.FromSeconds(30)) throw new ArgumentOutOfRangeException(nameof(duration));
        using var enumerator = new MMDeviceEnumerator();
        string? endpointId = null;
        foreach (var endpoint in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
        {
            using (endpoint)
            {
                if (!AudioDevices.IsCable(endpoint) || !endpoint.FriendlyName.StartsWith("CABLE Output", StringComparison.OrdinalIgnoreCase)) continue;
                if (endpointId is not null) throw new InvalidOperationException("发现多个 CABLE Output，请先在 Windows 声音设置中确认使用的虚拟设备。");
                endpointId = endpoint.ID;
            }
        }
        if (endpointId is null) throw new InvalidOperationException("未找到 CABLE Output，无法录制虚拟麦克风。");
        using var device = enumerator.GetDevice(endpointId);
        using var capture = new WasapiCapture(device, true, 20);
        var stopped = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var writeFailed = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        {
            using var writer = new WaveFileWriter(Path.GetFullPath(path), capture.WaveFormat);
            capture.DataAvailable += (_, data) =>
            {
                try { writer.Write(data.Buffer, 0, data.BytesRecorded); }
                catch (Exception ex) { writeFailed.TrySetResult(ex); }
            };
            capture.RecordingStopped += (_, e) => stopped.TrySetResult(e.Exception);
            capture.StartRecording();
            try { await Task.WhenAny(Task.Delay(duration, token), stopped.Task, writeFailed.Task).ConfigureAwait(false); }
            finally
            {
                capture.StopRecording();
                await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            if (writeFailed.Task.IsCompletedSuccessfully) throw writeFailed.Task.Result;
            if (stopped.Task.Result is { } error) throw error;
        }
        return new LocalClipResult(Path.GetFullPath(path), token.IsCancellationRequested, Analyze(path));
    }, token);

    public static LocalClipStatistics Analyze(string path)
    {
        using var reader = new WaveFileReader(path);
        var source = reader.ToSampleProvider();
        int channels = source.WaveFormat.Channels, rate = source.WaveFormat.SampleRate;
        float[] buffer = new float[channels * 2048];
        long sampleCount = 0, nearFull = 0, silentFrames = 0, windowFrames = 0;
        int windowSize = Math.Max(1, rate / 50), inWindow = 0;
        double squares = 0, peak = 0, windowPeak = 0;
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (read % channels != 0) throw new InvalidDataException("录音数据未按完整帧对齐。");
            for (int offset = 0; offset < read; offset += channels)
            {
                for (int channel = 0; channel < channels; channel++)
                {
                    float sample = buffer[offset + channel];
                    if (!float.IsFinite(sample)) throw new InvalidDataException("录音中存在无效数值。");
                    double value = Math.Abs((double)sample);
                    peak = Math.Max(peak, value); windowPeak = Math.Max(windowPeak, value);
                    squares += sample * (double)sample; sampleCount++;
                    if (value >= .9999) nearFull++;
                }
                if (++inWindow >= windowSize)
                {
                    windowFrames += inWindow;
                    if (windowPeak < .00001) silentFrames += inWindow;
                    inWindow = 0; windowPeak = 0;
                }
            }
        }
        if (inWindow > 0) { windowFrames += inWindow; if (windowPeak < .00001) silentFrames += inWindow; }
        return new(sampleCount / (double)channels / rate, rate, channels, peak,
            sampleCount == 0 || squares == 0 ? -120 : Math.Max(-120, 10 * Math.Log10(squares / sampleCount)),
            windowFrames == 0 ? 0 : silentFrames * 100.0 / windowFrames, nearFull);
    }

    public static Task PlayLocallyAsync(string path, CancellationToken token) => Task.Run(async () =>
    {
        using var enumerator = new MMDeviceEnumerator();
        AudioDevices.CheckPlaybackDefaults(enumerator);
        using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        if (AudioDevices.IsCable(device)) throw new InvalidOperationException("试听输出必须为耳机，不能为虚拟线缆。");
        using var reader = new WaveFileReader(path);
        using var player = new WasapiOut(device, AudioClientShareMode.Shared, true, 60);
        var stopped = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        player.PlaybackStopped += (_, e) => stopped.TrySetResult(e.Exception);
        player.Init(reader);
        using var cancellation = token.Register(player.Stop);
        token.ThrowIfCancellationRequested();
        player.Play();
        try { await stopped.Task.WaitAsync(TimeSpan.FromSeconds(40), token).ConfigureAwait(false); }
        finally { player.Stop(); }
        if (stopped.Task.IsCompletedSuccessfully && stopped.Task.Result is { } error) throw error;
    }, token);
}
