using System.Buffers.Binary;
using System.IO;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using VoiceBridge.Core;

namespace VoiceBridge.App.Audio;

public sealed class SampleAdapter(IFloatSource source, int sampleRate = 48000, int channels = 2) : ISampleProvider
{
    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
    public int Read(float[] buffer, int offset, int count) => source.Read(buffer, offset, count);
}

public sealed class NaudioSource(ISampleProvider source) : IFloatSource
{
    public int Read(float[] buffer, int offset, int count) => source.Read(buffer, offset, count);
}

public sealed class CaptureInput
{
    private readonly WaveFormat format;
    private float[] converted = new float[8192];
    public LiveSampleQueue Queue { get; }
    public IFloatSource Stereo { get; }
    public CaptureInput(WaveFormat format)
    {
        if (format is WaveFormatExtensible extensible) format = extensible.ToStandardWaveFormat();
        this.format = format;
        if (format.Channels is < 1 or > 2) throw new NotSupportedException("请在 Windows 中将麦克风设置为单声道或双声道。");
        if (format.Encoding != WaveFormatEncoding.IeeeFloat && format.Encoding != WaveFormatEncoding.Pcm)
            throw new NotSupportedException("麦克风必须使用 PCM 或 IEEE float 格式。");
        if (format.BitsPerSample is not (16 or 24 or 32)) throw new NotSupportedException("麦克风需使用 16/24/32 位格式。");
        Queue = new LiveSampleQueue(format.SampleRate, format.Channels);
        ISampleProvider provider = new SampleAdapter(Queue, format.SampleRate, format.Channels);
        if (format.SampleRate != 48000) provider = new WdlResamplingSampleProvider(provider, 48000);
        if (format.Channels == 1) provider = new MonoToStereoSampleProvider(provider);
        Stereo = new NaudioSource(provider);
    }
    public void Add(byte[] buffer, int length)
    {
        int bytes = format.BitsPerSample / 8, count = length / bytes;
        if (count % format.Channels != 0) throw new InvalidDataException("麦克风数据未对齐。");
        if (converted.Length < count) Array.Resize(ref converted, count);
        for (int i = 0; i < count; i++)
        {
            var s = buffer.AsSpan(i * bytes, bytes);
            converted[i] = format.Encoding == WaveFormatEncoding.IeeeFloat ? BitConverter.ToSingle(s) : bytes switch
            {
                2 => BinaryPrimitives.ReadInt16LittleEndian(s) / 32768f,
                3 => ((s[0] | s[1] << 8 | s[2] << 16) << 8 >> 8) / 8388608f,
                4 => BinaryPrimitives.ReadInt32LittleEndian(s) / 2147483648f,
                _ => 0
            };
        }
        Queue.Write(converted.AsSpan(0, count));
    }
}

public sealed class MusicSources : IFloatSource
{
    private IFloatSource[] sources = [];
    private float[] scratch = new float[4096];
    public void Replace(IEnumerable<IFloatSource> value) => Volatile.Write(ref sources, value.ToArray());
    public int Read(float[] buffer, int offset, int count)
    {
        Array.Clear(buffer, offset, count);
        if (scratch.Length < count) Array.Resize(ref scratch, count);
        foreach (var source in Volatile.Read(ref sources))
        {
            int read = source.Read(scratch, 0, count);
            for (int i = 0; i < read; i++) buffer[offset + i] += scratch[i];
        }
        return count;
    }
}
