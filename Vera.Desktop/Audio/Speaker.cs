using System;
using System.Windows.Threading;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Vera.Core;
using Vera.Core.Speech;

namespace Vera.Desktop.Audio;

/// <summary>
/// «Голосок» Веры вместо синтеза речи (v2.5): пока печатается ответ, звучат короткие тоны в такт буквам.
/// Один постоянный вывод звука с микшером — без задержки на открытие устройства для каждого «пип».
/// Все методы вызывать из потока UI.
/// </summary>
public sealed class Speaker : IDisposable
{
    private readonly VeraCore _core;
    private readonly BlipPacer _pacer = new();
    private readonly DispatcherTimer _timer;
    private readonly Random _rng = new();
    private MixingSampleProvider? _mixer;
    private WaveOutEvent? _out;
    private bool _active;
    private bool _ended = true;
    private DateTime _levelUntil;

    public Speaker(VeraCore core, Dispatcher ui)
    {
        _core = core;
        _timer = new DispatcherTimer(DispatcherPriority.Render, ui) { Interval = TimeSpan.FromMilliseconds(15) };
        _timer.Tick += (_, _) => Tick();
    }

    public event Action? Started;
    public event Action? Ended;

    /// <summary>Звучит сейчас или ещё будет звучать в этом ответе.</summary>
    public bool Active => _active;

    /// <summary>Уровень «голоса» 0..1 — для рта: открыт, пока звучит «пип».</summary>
    public float Level => DateTime.UtcNow < _levelUntil ? 0.8f : 0f;

    private ReplySoundSettings S => _core.Settings.Current.Sound;

    /// <summary>Начался ответ: сбросить очередь.</summary>
    public void Begin()
    {
        _pacer.Reset();
        _ended = false;
    }

    /// <summary>Кусок текста ответа (как пришёл в чат).</summary>
    public void Push(string text)
    {
        if (S.Style == "off") return;
        _pacer.Push(text);
        if (!_pacer.Active) return;
        if (!_active)
        {
            _active = true;
            Started?.Invoke();
        }
        _timer.Start();
    }

    /// <summary>Ответ закончился — доиграть очередь и замолчать.</summary>
    public void End() => _ended = true;

    /// <summary>Короткая фраза целиком (напоминание, тест в настройках).</summary>
    public void Say(string text)
    {
        Stop();
        Begin();
        Push(text);
        End();
    }

    public void Stop()
    {
        _pacer.Reset();
        _ended = true;
        _timer.Stop();
        _levelUntil = default;
        if (_active)
        {
            _active = false;
            Ended?.Invoke();
        }
    }

    private void Tick()
    {
        if (_pacer.Tick(DateTimeOffset.UtcNow)) Play();
        if (_pacer.Active || !_ended) return;
        if (DateTime.UtcNow < _levelUntil) return; // дать прозвучать последнему
        _timer.Stop();
        if (_active)
        {
            _active = false;
            Ended?.Invoke();
        }
    }

    private void Play()
    {
        var s = _core.Settings.Current;
        var blip = ReplySound.Blip(s.Sound.Style, ReplySound.PitchFor(s.Persona), _rng, ReplySound.Gain(s.Sound.Volume));
        if (blip.Length == 0) return;
        _levelUntil = DateTime.UtcNow.AddMilliseconds(blip.Length * 1000.0 / ReplySound.SampleRate * 0.7);
        try
        {
            EnsureOutput();
            _mixer!.AddMixerInput(new ArraySampleProvider(blip, ReplySound.SampleRate, 1));
        }
        catch (Exception)
        {
            // нет устройства вывода — без звука, рот всё равно шевелится
            DisposeOutput();
        }
    }

    private void EnsureOutput()
    {
        if (_out is not null) return;
        _mixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(ReplySound.SampleRate, 1)) { ReadFully = true };
        _out = new WaveOutEvent { DesiredLatency = 90, NumberOfBuffers = 3 };
        _out.Init(new SampleToWaveProvider16(_mixer));
        _out.Play();
    }

    private void DisposeOutput()
    {
        try { _out?.Dispose(); } catch (Exception) { }
        _out = null;
        _mixer = null;
    }

    public void Dispose()
    {
        Stop();
        DisposeOutput();
    }
}
