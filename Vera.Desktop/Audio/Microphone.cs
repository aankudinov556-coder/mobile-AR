using System;
using System.Collections.Generic;
using NAudio.Wave;

namespace Vera.Desktop.Audio;

/// <summary>Микрофон: NAudio WaveInEvent, 16 кГц моно, кадры по 30 мс (float −1..1).</summary>
public sealed class Microphone : IDisposable
{
    public const int SampleRate = 16000;
    private WaveInEvent? _wave;
    private string _device = "";

    /// <summary>Кадр с микрофона. Вызывается в аудиопотоке!</summary>
    public event Action<float[]>? Frame;
    /// <summary>Запись прервалась с ошибкой (устройство отключили и т.п.).</summary>
    public event Action<string>? Failed;

    public bool Running => _wave is not null;
    public string Device => _device;

    /// <summary>Названия устройств записи (WinMM обрезает их до 31 символа).</summary>
    public static IReadOnlyList<string> Devices()
    {
        var list = new List<string>();
        try
        {
            for (var i = 0; i < WaveInEvent.DeviceCount; i++) list.Add(WaveInEvent.GetCapabilities(i).ProductName);
        }
        catch (Exception) { }
        return list;
    }

    private static int DeviceIndex(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return -1; // WAVE_MAPPER — устройство по умолчанию
        var devs = Devices();
        for (var i = 0; i < devs.Count; i++)
            if (string.Equals(devs[i], name, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    public void Start(string deviceName)
    {
        if (_wave is not null && string.Equals(_device, deviceName ?? "", StringComparison.Ordinal)) return;
        Stop();
        var w = new WaveInEvent
        {
            DeviceNumber = DeviceIndex(deviceName ?? ""),
            WaveFormat = new WaveFormat(SampleRate, 16, 1),
            BufferMilliseconds = 30,
            NumberOfBuffers = 4,
        };
        w.DataAvailable += OnData;
        w.RecordingStopped += (_, e) =>
        {
            if (e.Exception is not null && ReferenceEquals(_wave, w)) Failed?.Invoke(e.Exception.Message);
        };
        try
        {
            w.StartRecording();
        }
        catch (Exception)
        {
            w.Dispose();
            throw;
        }
        _wave = w;
        _device = deviceName ?? "";
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        var n = e.BytesRecorded / 2;
        if (n <= 0) return;
        var f = new float[n];
        var buf = e.Buffer;
        for (var i = 0; i < n; i++) f[i] = (short)(buf[2 * i] | (buf[2 * i + 1] << 8)) / 32768f;
        Frame?.Invoke(f);
    }

    public void Stop()
    {
        var w = _wave;
        _wave = null;
        if (w is null) return;
        w.DataAvailable -= OnData;
        try { w.StopRecording(); } catch (Exception) { }
        try { w.Dispose(); } catch (Exception) { }
    }

    public void Dispose() => Stop();
}
