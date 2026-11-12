using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Threading;
using Vera.Core;
using Vera.Core.Agent;
using Vera.Core.Speech;

namespace Vera.Desktop.Audio;

/// <summary>
/// Голос Веры: микрофон → VoiceRecorder (рация и активация по слову) → распознавание → агент;
/// ответы агента → SentenceChunker → синтез → Speaker. Все события — в потоке UI.
/// </summary>
public sealed partial class VoiceController : IDisposable
{
    private readonly VeraCore _core;
    private readonly Dispatcher _ui;
    private readonly VoiceRecorder _rec;
    private readonly object _recLock = new();
    private readonly Microphone _mic = new();
    private readonly DispatcherTimer _resumeTimer;

    private SentenceChunker? _chunker;
    private string? _chunkTurn;
    private int _lastStep = -1;
    private bool _wakeBusy;
    private bool _ttsErrorShown;

    public VoiceController(VeraCore core, Dispatcher ui)
    {
        _core = core;
        _ui = ui;
        Speaker = new Speaker(core);
        _rec = new VoiceRecorder(() => _core.Settings.Current.Voice.Sensitivity, () => _core.Settings.Current.Voice.SilenceMs);
        _rec.StateChanged += (st, reason) => _ui.BeginInvoke(() => OnState(st, reason));
        _rec.Level += lvl => _ui.BeginInvoke(() => MicLevel?.Invoke(lvl));
        _rec.Segment += (wav, mode, ms) => _ui.BeginInvoke(() => { var task = OnSegmentAsync(wav, mode); });
        _mic.Frame += f =>
        {
            lock (_recLock) _rec.Feed(f, Microphone.SampleRate);
        };
        _mic.Failed += msg => _ui.BeginInvoke(() =>
        {
            lock (_recLock) _rec.Cancel("cancel");
            _mic.Stop();
            Problem?.Invoke("микрофон", "Микрофон отключился: " + msg);
        });

        _resumeTimer = new DispatcherTimer(DispatcherPriority.Normal, ui) { Interval = TimeSpan.FromMilliseconds(600) };
        _resumeTimer.Tick += (_, _) =>
        {
            _resumeTimer.Stop();
            if (!Speaker.Active) lock (_recLock) _rec.Paused = false;
        };
        Speaker.Started += () =>
        {
            _resumeTimer.Stop();
            lock (_recLock) _rec.Paused = true; // Вера не слушает сама себя
            SpeakingChanged?.Invoke(true);
        };
        Speaker.Ended += () =>
        {
            _resumeTimer.Start();
            SpeakingChanged?.Invoke(false);
        };
        Speaker.Error += msg =>
        {
            if (_ttsErrorShown) return;
            _ttsErrorShown = true;
            Problem?.Invoke("голос", msg);
        };
    }

    public Speaker Speaker { get; }

    /// <summary>Отправить распознанный текст агенту (text, source). Задаёт хост.</summary>
    public Action<string, string>? Send { get; set; }
    /// <summary>Идёт ли сейчас ход агента (во время ответа активация по слову не срабатывает).</summary>
    public Func<bool>? TurnActive { get; set; }

    public RecorderState State { get; private set; } = RecorderState.Off;
    public bool IsCommandMode => _rec.Mode == ListenMode.Command;

    public event Action<RecorderState, string?>? StateChanged;
    public event Action<float>? MicLevel;
    /// <summary>(заголовок, текст) — показать в пузыре.</summary>
    public event Action<string, string>? Problem;
    public event Action? NotHeard;
    /// <summary>Пользователь что-то сказал Вере (для «проснуться»).</summary>
    public event Action? Interaction;
    public event Action<bool>? SpeakingChanged;

    private VeraSettings S => _core.Settings.Current;

    // ================================================================== microphone

    private void EnsureMic() => _mic.Start(S.Voice.MicDeviceId ?? "");

    private void StopMicIfIdle()
    {
        if (_rec.Mode == ListenMode.Off && _mic.Running) _mic.Stop();
    }

    public void StartListening()
    {
        Speaker.Stop();
        try
        {
            EnsureMic();
        }
        catch (Exception e)
        {
            Problem?.Invoke("микрофон", "Нет доступа к микрофону: " + e.Message);
            return;
        }
        lock (_recLock) _rec.StartCommand();
    }

    /// <summary>Горячая клавиша «говорить»: начать, закончить досрочно или перебить речь.</summary>
    public void ListenToggle()
    {
        if (_rec.Mode == ListenMode.Command)
        {
            lock (_recLock) _rec.FinishNow();
        }
        else if (Speaker.Active) Speaker.Stop();
        else StartListening();
    }

    /// <summary>Включить/выключить постоянное прослушивание по настройке voice.wakeWord, сменить микрофон.</summary>
    public void SyncSettings()
    {
        var want = S.Voice.WakeWord;
        try
        {
            if (_mic.Running && !string.Equals(_mic.Device, S.Voice.MicDeviceId ?? "", StringComparison.Ordinal))
            {
                _mic.Stop();
                if (_rec.Mode != ListenMode.Off) EnsureMic();
            }
            if (want && !_rec.WakeEnabled)
            {
                EnsureMic();
                lock (_recLock) _rec.StartWake();
            }
            else if (!want && _rec.WakeEnabled)
            {
                lock (_recLock) _rec.StopWake();
                StopMicIfIdle();
            }
        }
        catch (Exception e)
        {
            Problem?.Invoke("микрофон", "Голосовая активация не запустилась: " + e.Message);
        }
    }

    private void OnState(RecorderState st, string? reason)
    {
        State = st;
        if (S.Voice.Chime)
        {
            if (st == RecorderState.Listening) Sfx.ListenStart();
            else if (st == RecorderState.Processing) Sfx.ListenStop();
        }
        StopMicIfIdle();
        StateChanged?.Invoke(st, reason);
    }

    private void BackToIdle()
    {
        State = _rec.WakeEnabled ? RecorderState.Wake : RecorderState.Off;
        StopMicIfIdle();
        StateChanged?.Invoke(State, null);
    }

    [GeneratedRegex(@"[\s.,!?…]")] private static partial Regex Punct();

    private async Task OnSegmentAsync(byte[] wav, ListenMode mode)
    {
        if (mode == ListenMode.Wake)
        {
            await HandleWakeAsync(wav);
            return;
        }
        try
        {
            var text = await Task.Run(() => _core.Speech.TranscribeAsync(wav, "audio/wav"));
            BackToIdle();
            if (Punct().Replace(text ?? "", "").Length < 2)
            {
                NotHeard?.Invoke();
                return;
            }
            Interaction?.Invoke();
            Send?.Invoke(text!.Trim(), "voice");
        }
        catch (Exception e)
        {
            BackToIdle();
            Problem?.Invoke("микрофон", e.Message);
        }
    }

    private async Task HandleWakeAsync(byte[] wav)
    {
        if (_wakeBusy || Speaker.Active || (TurnActive?.Invoke() ?? false)) return;
        _wakeBusy = true;
        try
        {
            var text = await Task.Run(() => _core.Speech.TranscribeAsync(wav, "audio/wav"));
            var rest = VoiceRecorder.MatchWake(text, S.Voice.WakePhrases);
            if (rest is null) return;
            Interaction?.Invoke();
            if (Punct().Replace(rest, "").Length >= 3)
            {
                if (S.Voice.Chime) Sfx.ListenStart();
                Send?.Invoke(rest, "voice");
            }
            else
            {
                StartListening();
            }
        }
        catch (Exception)
        {
            // ошибки распознавания фоновых фраз не показываем — их слишком много
        }
        finally
        {
            _wakeBusy = false;
        }
    }

    // ================================================================== speech

    public bool ShouldSpeak(string source)
    {
        var t = S.Tts;
        if (t.Engine == "off") return false;
        if (t.AutoSpeak == "always") return true;
        if (t.AutoSpeak == "never") return false;
        return source is "voice" or "screen";
    }

    /// <summary>Сказать фразу (напоминание, тест голоса).</summary>
    public void Say(string text, bool resetErrors = false)
    {
        if (resetErrors) _ttsErrorShown = false;
        if (S.Tts.Engine == "off") return;
        Speaker.Stop();
        Speaker.Enqueue(text);
    }

    public void StopSpeaking() => Speaker.Stop();

    /// <summary>События агента (уже в потоке UI): озвучка ответа по предложениям.</summary>
    public void OnAgentEvent(AgentEvent e)
    {
        switch (e)
        {
            case TurnStarted ts:
                Speaker.Stop();
                _chunker = null;
                _chunkTurn = null;
                _lastStep = -1;
                if (ShouldSpeak(ts.Source))
                {
                    _chunkTurn = ts.TurnId;
                    _chunker = new SentenceChunker(c => Speaker.Enqueue(c));
                }
                break;
            case TextDelta d when d.TurnId == _chunkTurn && _chunker is not null:
                // новый шаг модели — новый абзац, не склеиваем предложения
                if (_lastStep >= 0 && d.Step != _lastStep) _chunker.Push("\n");
                _lastStep = d.Step;
                _chunker.Push(d.Delta);
                break;
            case TurnEnded te when te.TurnId == _chunkTurn:
                _chunker?.End();
                _chunker = null;
                _chunkTurn = null;
                break;
            case TurnFailed tf when tf.TurnId == _chunkTurn:
                _chunker = null;
                _chunkTurn = null;
                break;
        }
    }

    public void Dispose()
    {
        Speaker.Stop();
        _mic.Dispose();
    }
}
