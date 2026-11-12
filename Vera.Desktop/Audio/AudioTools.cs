using System;
using System.IO;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Vera.Desktop.Audio;

/// <summary>Звук, целиком раскодированный в память (float, перемежённые каналы).</summary>
public sealed record Pcm(float[] Samples, int SampleRate, int Channels)
{
    public double Seconds => Samples.Length / (double)(SampleRate * Channels);
}

/// <summary>Отдаёт массив float как ISampleProvider.</summary>
public sealed class ArraySampleProvider : ISampleProvider
{
    private readonly float[] _data;
    private int _pos;

    public ArraySampleProvider(float[] data, int sampleRate, int channels)
    {
        _data = data;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
    }

    public ArraySampleProvider(Pcm pcm) : this(pcm.Samples, pcm.SampleRate, pcm.Channels) { }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        var n = Math.Min(count, _data.Length - _pos);
        if (n <= 0) return 0;
        Array.Copy(_data, _pos, buffer, offset, n);
        _pos += n;
        return n;
    }
}

/// <summary>Тот же поток, но с другой заявленной частотой (для ускорения через ресемплинг).</summary>
internal sealed class RelabelledSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _src;

    public RelabelledSampleProvider(ISampleProvider src, int sampleRate)
    {
        _src = src;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, src.WaveFormat.Channels);
    }

    public WaveFormat WaveFormat { get; }
    public int Read(float[] buffer, int offset, int count) => _src.Read(buffer, offset, count);
}

public static class AudioTools
{
    public static Pcm ReadAll(ISampleProvider sp)
    {
        var fmt = sp.WaveFormat;
        var chunk = new float[Math.Max(4096, fmt.SampleRate * fmt.Channels / 4)];
        var data = new float[chunk.Length * 4];
        var len = 0;
        int n;
        while ((n = sp.Read(chunk, 0, chunk.Length)) > 0)
        {
            if (len + n > data.Length) Array.Resize(ref data, Math.Max(data.Length * 2, len + n));
            Array.Copy(chunk, 0, data, len, n);
            len += n;
        }
        Array.Resize(ref data, len);
        return new Pcm(data, fmt.SampleRate, fmt.Channels);
    }

    /// <summary>MP3 → PCM. Сначала ACM-декодер Windows, при неудаче — Media Foundation.</summary>
    public static Pcm DecodeMp3(byte[] mp3)
    {
        try
        {
            using var ms = new MemoryStream(mp3);
            using var reader = new Mp3FileReader(ms);
            return ReadAll(reader.ToSampleProvider());
        }
        catch (Exception)
        {
            using var ms = new MemoryStream(mp3);
            using var reader = new StreamMediaFoundationReader(ms);
            return ReadAll(reader.ToSampleProvider());
        }
    }

    public static Pcm DecodeWav(byte[] wav)
    {
        using var ms = new MemoryStream(wav);
        using var reader = new WaveFileReader(ms);
        return ReadAll(reader.ToSampleProvider());
    }

    /// <summary>
    /// Изменить скорость речи без изменения высоты голоса:
    /// ресемплинг (быстрее и выше) + обратный сдвиг высоты фазовым вокодером.
    /// </summary>
    public static Pcm ChangeTempo(Pcm pcm, double rate)
    {
        if (Math.Abs(rate - 1) < 0.03 || pcm.Samples.Length == 0) return pcm;
        rate = Math.Clamp(rate, 0.5, 2.0);
        const int fft = 2048;
        const int osamp = 4;
        try
        {
            // добавляем тишину в конец, чтобы вокодер «дожевал» хвост
            var padded = new float[pcm.Samples.Length + fft * 2 * pcm.Channels];
            Array.Copy(pcm.Samples, padded, pcm.Samples.Length);
            var src = new ArraySampleProvider(padded, pcm.SampleRate, pcm.Channels);
            var fast = new WdlResamplingSampleProvider(new RelabelledSampleProvider(src, (int)Math.Round(pcm.SampleRate * rate)), pcm.SampleRate);
            var shifted = new SmbPitchShiftingSampleProvider(fast, fft, osamp, (float)(1.0 / rate));
            var outPcm = ReadAll(shifted);
            // задержка вокодера: fft - fft/osamp кадров
            var skip = Math.Min(outPcm.Samples.Length, (fft - fft / osamp) * pcm.Channels);
            var expected = (int)(pcm.Samples.Length / rate);
            expected -= expected % pcm.Channels;
            var len = Math.Max(0, Math.Min(outPcm.Samples.Length - skip, expected));
            var result = new float[len];
            Array.Copy(outPcm.Samples, skip, result, 0, len);
            return new Pcm(result, pcm.SampleRate, pcm.Channels);
        }
        catch (Exception)
        {
            return pcm;
        }
    }

    /// <summary>Огибающая громкости (по 20 мс) для липсинка: 0..1.</summary>
    public static float[] Envelope(Pcm pcm, int blocksPerSecond = 50)
    {
        var frame = Math.Max(1, pcm.SampleRate / blocksPerSecond) * pcm.Channels;
        var blocks = (pcm.Samples.Length + frame - 1) / frame;
        var env = new float[blocks];
        for (var b = 0; b < blocks; b++)
        {
            double acc = 0;
            var start = b * frame;
            var end = Math.Min(pcm.Samples.Length, start + frame);
            for (var i = start; i < end; i++) acc += pcm.Samples[i] * pcm.Samples[i];
            var rms = Math.Sqrt(acc / Math.Max(1, end - start));
            env[b] = (float)Math.Min(1, rms / 0.12);
        }
        return env;
    }
}
