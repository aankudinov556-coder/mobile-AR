using System;
using NAudio.Wave;

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
