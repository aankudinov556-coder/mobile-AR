using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Vera.Core;
using Windows.Media.SpeechSynthesis;

namespace Vera.Desktop.Audio;

/// <summary>Системный голос Windows (OneCore, Windows.Media.SpeechSynthesis).</summary>
public static class SystemVoice
{
    public static IReadOnlyList<(string Name, string Language)> Voices()
    {
        try
        {
            return SpeechSynthesizer.AllVoices.Select(v => (v.DisplayName, v.Language)).ToList();
        }
        catch (Exception)
        {
            return Array.Empty<(string, string)>();
        }
    }

    /// <summary>Синтез в WAV. Голос — по имени из настроек, иначе первый русский.</summary>
    public static async Task<byte[]> SynthesizeAsync(string text, string voiceName, double rate)
    {
        using var synth = new SpeechSynthesizer();
        var voices = SpeechSynthesizer.AllVoices;
        var voice = voices.FirstOrDefault(v => !string.IsNullOrEmpty(voiceName) && v.DisplayName == voiceName)
                    ?? voices.FirstOrDefault(v => v.Language.StartsWith("ru", StringComparison.OrdinalIgnoreCase));
        if (voice is not null) synth.Voice = voice;
        try
        {
            synth.Options.SpeakingRate = Math.Clamp(rate, 0.5, 3.0);
            synth.Options.AudioPitch = 1.15;
        }
        catch (Exception)
        {
            // старые сборки Windows 10 — без этих параметров
        }
        using var stream = await synth.SynthesizeTextToStreamAsync(text);
        using var s = stream.AsStreamForRead();
        using var ms = new MemoryStream();
        await s.CopyToAsync(ms).ConfigureAwait(false);
        return ms.ToArray();
    }
}

/// <summary>Одно проигрывание с огибающей громкости для липсинка.</summary>
internal sealed class Playback
{
    private const int EnvRate = 50;
    private readonly WaveOutEvent _out;
    private readonly float[] _env;
    private readonly double _bytesPerSecond;
    private bool _finished;

    public event Action? Finished;

    public Playback(Pcm pcm, float volume)
    {
        _env = AudioTools.Envelope(pcm, EnvRate);
        ISampleProvider sp = new ArraySampleProvider(pcm);
        sp = new VolumeSampleProvider(sp) { Volume = Math.Clamp(volume, 0f, 1f) };
        var wave16 = new SampleToWaveProvider16(sp);
        _bytesPerSecond = wave16.WaveFormat.AverageBytesPerSecond;
        _out = new WaveOutEvent { DesiredLatency = 200, NumberOfBuffers = 3 };
        _out.PlaybackStopped += (_, _) => Finish();
        _out.Init(wave16);
    }

    public void Start() => _out.Play();

    /// <summary>Уровень звука «сейчас» по позиции воспроизведения (0..1).</summary>
    public float Level
    {
        get
        {
            if (_finished) return 0;
            try
            {
                var sec = _out.GetPosition() / _bytesPerSecond;
                var idx = (int)(sec * EnvRate);
                return idx >= 0 && idx < _env.Length ? _env[idx] : 0;
            }
            catch (Exception)
            {
                return 0;
            }
        }
    }

    public void Stop()
    {
        try { _out.Stop(); } catch (Exception) { Finish(); }
    }

    private void Finish()
    {
        if (_finished) return;
        _finished = true;
        try { _out.Dispose(); } catch (Exception) { }
        Finished?.Invoke();
    }
}

/// <summary>
/// Очередь фраз: синтез заранее (параллельно), проигрывание по очереди, уровень для липсинка.
/// Все методы вызывать из потока UI.
/// </summary>
public sealed class Speaker
{
    private sealed record Item(string Text, int Gen, Task<Pcm?> Audio);

    private readonly VeraCore _core;
    private readonly Queue<Item> _queue = new();
    private int _gen;
    private bool _playing;
    private Playback? _current;

    public Speaker(VeraCore core) => _core = core;

    public event Action? Started;
    public event Action? Ended;
    public event Action<string>? Error;

    public bool Active => _playing || _queue.Count > 0;

    /// <summary>Текущий уровень звука 0..1 (для рта).</summary>
    public float Level => _current?.Level ?? 0f;

    public void Enqueue(string text)
    {
        var t = (text ?? "").Trim();
        if (t.Length == 0) return;
        var item = new Item(t, _gen, Task.Run(() => PrepareAsync(t)));
        _queue.Enqueue(item);
        _ = PumpAsync();
    }

    private async Task<Pcm?> PrepareAsync(string text)
    {
        var tts = _core.Settings.Current.Tts;
        var r = await _core.Speech.SynthesizeAsync(text).ConfigureAwait(false);
        if (r.Skip) return null;
        if (r.UseSystemVoice)
        {
            var wav = await SystemVoice.SynthesizeAsync(r.Text, tts.SystemVoice, tts.Rate).ConfigureAwait(false);
            return AudioTools.DecodeWav(wav);
        }
        if (r.Audio is not { Length: > 0 }) return null;
        var pcm = AudioTools.DecodeMp3(r.Audio);
        return AudioTools.ChangeTempo(pcm, tts.Rate);
    }

    public void Stop()
    {
        _gen++;
        _queue.Clear();
        var cur = _current;
        _current = null;
        cur?.Stop();
        if (_playing)
        {
            _playing = false;
            Ended?.Invoke();
        }
    }

    private async Task PumpAsync()
    {
        if (_playing) return;
        if (!_queue.TryDequeue(out var item)) return;
        _playing = true;
        Started?.Invoke();
        try
        {
            var pcm = await item.Audio;
            if (item.Gen != _gen) return;
            if (pcm is { Samples.Length: > 0 }) await PlayAsync(pcm);
        }
        catch (Exception e)
        {
            if (item.Gen == _gen) Error?.Invoke(e.Message);
        }
        finally
        {
            if (item.Gen == _gen)
            {
                _playing = false;
                if (_queue.Count == 0) Ended?.Invoke();
                _ = PumpAsync();
            }
        }
    }

    private Task PlayAsync(Pcm pcm)
    {
        var tcs = new TaskCompletionSource();
        var vol = (float)_core.Settings.Current.Tts.Volume;
        var pb = new Playback(pcm, vol);
        _current = pb;
        pb.Finished += () =>
        {
            if (ReferenceEquals(_current, pb)) _current = null;
            tcs.TrySetResult();
        };
        pb.Start();
        return tcs.Task;
    }
}
