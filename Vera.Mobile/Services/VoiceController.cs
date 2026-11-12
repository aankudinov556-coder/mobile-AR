using Vera.Core;
using Vera.Core.Speech;
using Vera.Mobile.Platform;

namespace Vera.Mobile.Services;

/// <summary>
/// Голосовой ввод: AudioRecord → VoiceRecorder (VAD) → облачное распознавание, либо SpeechRecognizer Android
/// (stt.provider == "device"). Плюс активация по слову «Вера», пока приложение открыто.
/// </summary>
public sealed class VoiceController
{
    private readonly VeraCore _core;
    private readonly SpeechOutput _speech;
    private readonly VoiceRecorder _recorder;
    private readonly MicCapture _mic = new();
    private readonly object _recLock = new();
    private DeviceRecognizer? _device;
    private bool _deviceCommand;
    private bool _deviceWake;
    private bool _wakeBusy;
    private bool _wakeWanted;
    private int _generation;

    public VoiceController(VeraCore core, SpeechOutput speech)
    {
        _core = core;
        _speech = speech;
        _recorder = new VoiceRecorder(() => _core.Settings.Current.Voice.Sensitivity, () => _core.Settings.Current.Voice.SilenceMs);
        _recorder.Segment += OnSegment;
        _recorder.Level += l => Level?.Invoke(l);
        _recorder.StateChanged += OnRecorderState;
        _speech.SpeakingChanged += OnSpeaking;
    }

    /// <summary>Состояние для анимаций и подсказок (в UI-потоке). reason: "timeout", "short", "cancel" или null.</summary>
    public event Action<RecorderState, string?>? StateChanged;
    /// <summary>Уровень микрофона 0..1 (фоновый поток).</summary>
    public event Action<float>? Level;
    /// <summary>Распознанная команда — отправить Вере (UI-поток).</summary>
    public event Action<string>? Heard;
    /// <summary>Сообщение для облачка: (текст, заголовок) (UI-поток).</summary>
    public event Action<string, string?>? Notice;

    public RecorderState State { get; private set; } = RecorderState.Off;

    public bool IsCommandActive => State is RecorderState.Listening or RecorderState.Recording;

    private bool DeviceMode => _core.Settings.Current.Stt.Provider == "device";

    private void Post(Action a) => MainThread.BeginInvokeOnMainThread(a);

    private void SetState(RecorderState s, string? reason)
    {
        Post(() =>
        {
            State = s;
            StateChanged?.Invoke(s, reason);
        });
    }

    public static async Task<bool> HasPermissionAsync()
    {
        try { return await MainThread.InvokeOnMainThreadAsync(() => Permissions.CheckStatusAsync<Permissions.Microphone>()) == PermissionStatus.Granted; }
        catch { return false; }
    }

    public static async Task<bool> EnsurePermissionAsync()
    {
        try
        {
            return await MainThread.InvokeOnMainThreadAsync(async () =>
            {
                var st = await Permissions.CheckStatusAsync<Permissions.Microphone>();
                if (st != PermissionStatus.Granted) st = await Permissions.RequestAsync<Permissions.Microphone>();
                return st == PermissionStatus.Granted;
            });
        }
        catch { return false; }
    }

    // ------------------------------------------------------------------ команда (кнопка / долгий тап)

    /// <summary>Кнопка микрофона: начать, закончить досрочно или заставить Веру замолчать.</summary>
    public void Toggle()
    {
        if (IsCommandActive)
        {
            if (_deviceCommand) Post(() => _device?.Stop());
            else lock (_recLock) _recorder.FinishNow();
            return;
        }
        if (_speech.IsActive)
        {
            _speech.Stop();
            return;
        }
        _ = StartCommandAsync();
    }

    public async Task StartCommandAsync()
    {
        _speech.Stop();
        if (!await EnsurePermissionAsync())
        {
            Post(() => Notice?.Invoke("Нет доступа к микрофону — разреши его Вере в настройках Android.", "🎙️ микрофон"));
            return;
        }
        if (DeviceMode)
        {
            Post(StartDeviceCommand);
            return;
        }
        lock (_recLock) _recorder.StartCommand();
        if (!EnsureMic()) lock (_recLock) _recorder.Cancel("cancel");
    }

    public void Cancel()
    {
        if (_deviceCommand) Post(() => { _device?.Cancel(); _deviceCommand = false; SetState(RecorderState.Off, "cancel"); RestartDeviceWakeSoon(); });
        else if (IsCommandActive) lock (_recLock) _recorder.Cancel("cancel");
    }

    private bool EnsureMic()
    {
        if (_mic.Running) return true;
        try
        {
            _mic.Start(Feed);
            return true;
        }
        catch (Exception e)
        {
            Post(() => Notice?.Invoke("Микрофон не включился: " + e.Message, "🎙️ микрофон"));
            return false;
        }
    }

    private void Feed(float[] frame, int rate)
    {
        lock (_recLock) _recorder.Feed(frame, rate);
    }

    private void StopMicIfIdle()
    {
        var gen = Interlocked.Increment(ref _generation);
        _ = Task.Run(async () =>
        {
            await Task.Delay(400).ConfigureAwait(false);
            if (gen != Volatile.Read(ref _generation)) return;
            bool off;
            lock (_recLock) off = _recorder.Mode == ListenMode.Off;
            if (off) _mic.Stop();
        });
    }

    private void OnRecorderState(RecorderState s, string? reason)
    {
        var chime = _core.Settings.Current.Voice.Chime;
        if (s == RecorderState.Listening && chime) Sfx.ListenStart();
        if (s == RecorderState.Processing && chime) Sfx.ListenStop();
        SetState(s, reason);
        if (s is RecorderState.Off or RecorderState.Processing) StopMicIfIdle();
    }

    private void OnSegment(byte[] wav, ListenMode mode, double ms)
    {
        if (mode == ListenMode.Command) _ = Task.Run(() => HandleCommandAsync(wav));
        else _ = Task.Run(() => HandleWakeAsync(wav));
    }

    private RecorderState IdleState()
    {
        lock (_recLock) return _recorder.Mode == ListenMode.Wake ? RecorderState.Wake : RecorderState.Off;
    }

    private async Task HandleCommandAsync(byte[] wav)
    {
        string text;
        try
        {
            text = await _core.Speech.TranscribeAsync(wav, "audio/wav").ConfigureAwait(false);
        }
        catch (Exception e)
        {
            SetState(IdleState(), null);
            Post(() => Notice?.Invoke(e.Message, "🎙️ распознавание"));
            return;
        }
        SetState(IdleState(), null);
        Submit(text);
    }

    private void Submit(string text)
    {
        var meaningful = new string((text ?? "").Where(char.IsLetterOrDigit).ToArray());
        if (meaningful.Length < 2)
        {
            Post(() => Notice?.Invoke(_core.Line("notHeard"), null));
            return;
        }
        Post(() => Heard?.Invoke(text!.Trim()));
    }

    private async Task HandleWakeAsync(byte[] wav)
    {
        if (_wakeBusy || _speech.IsActive || _core.Agent.IsBusy) return;
        _wakeBusy = true;
        try
        {
            var text = await _core.Speech.TranscribeAsync(wav, "audio/wav").ConfigureAwait(false);
            HandleWakeText(text);
        }
        catch (Exception e)
        {
            Android.Util.Log.Warn("Vera", "wake: " + e.Message);
        }
        finally
        {
            _wakeBusy = false;
        }
    }

    private void HandleWakeText(string text)
    {
        var cmd = VoiceRecorder.MatchWake(text, _core.Settings.Current.Voice.WakePhrases);
        if (cmd is null) return;
        var meaningful = new string(cmd.Where(char.IsLetterOrDigit).ToArray());
        if (meaningful.Length >= 3)
        {
            if (_core.Settings.Current.Voice.Chime) Sfx.ListenStart();
            Post(() => Heard?.Invoke(cmd));
        }
        else
        {
            _ = StartCommandAsync();
        }
    }

    // ------------------------------------------------------------------ голос Веры

    private void OnSpeaking(bool speaking)
    {
        if (speaking)
        {
            lock (_recLock) _recorder.Paused = true;
            if (_deviceWake && !_deviceCommand) Post(() => _device?.Cancel());
        }
        else
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(600).ConfigureAwait(false);
                if (_speech.IsSpeaking) return;
                lock (_recLock) _recorder.Paused = false;
                RestartDeviceWakeSoon();
            });
        }
    }

    // ------------------------------------------------------------------ активация по слову

    /// <summary>Включить/выключить прослушивание «Вера» по настройке и видимости приложения.</summary>
    public async Task SyncWakeAsync()
    {
        var want = _core.Settings.Current.Voice.WakeWord && AppState.IsForeground;
        if (want && !await HasPermissionAsync()) want = false;
        _wakeWanted = want;
        var device = DeviceMode;

        if (!want || device)
        {
            lock (_recLock) if (_recorder.WakeEnabled) _recorder.StopWake();
            StopMicIfIdle();
        }
        if (!want || !device)
        {
            if (_deviceWake)
            {
                _deviceWake = false;
                Post(() => { if (!_deviceCommand) _device?.Cancel(); });
                if (!_deviceCommand) SetState(RecorderState.Off, null);
            }
        }
        if (!want) return;

        if (device)
        {
            if (!_deviceWake)
            {
                _deviceWake = true;
                Post(StartDeviceWake);
            }
        }
        else
        {
            lock (_recLock) if (!_recorder.WakeEnabled) _recorder.StartWake();
            EnsureMic();
        }
    }

    /// <summary>Приложение ушло в фон — микрофон отпускаем.</summary>
    public void Suspend()
    {
        _wakeWanted = false;
        lock (_recLock)
        {
            if (_recorder.Mode == ListenMode.Command) _recorder.Cancel("cancel");
            if (_recorder.WakeEnabled) _recorder.StopWake();
        }
        _mic.Stop();
        _deviceWake = false;
        _deviceCommand = false;
        Post(() => _device?.Cancel());
    }

    // ------------------------------------------------------------------ SpeechRecognizer (UI-поток)

    private DeviceRecognizer Device()
    {
        if (_device is not null) return _device;
        var d = new DeviceRecognizer();
        d.Level += l => Level?.Invoke(l);
        d.SpeechStarted += () => { if (_deviceCommand) SetState(RecorderState.Recording, null); };
        d.SpeechEnded += () => { if (_deviceCommand) SetState(RecorderState.Processing, null); };
        d.Result += OnDeviceResult;
        d.Failed += OnDeviceFailed;
        _device = d;
        return d;
    }

    private void StartDeviceCommand()
    {
        var d = Device();
        d.Cancel();
        _deviceCommand = true;
        if (!d.Start(_core.Settings.Current.Stt.Language))
        {
            _deviceCommand = false;
            Notice?.Invoke("На телефоне нет службы распознавания речи. Выбери OpenAI или Groq в настройках.", "🎙️ распознавание");
            return;
        }
        if (_core.Settings.Current.Voice.Chime) Sfx.ListenStart();
        SetState(RecorderState.Listening, null);
    }

    private void StartDeviceWake()
    {
        if (!_deviceWake || _deviceCommand || _speech.IsSpeaking || !_wakeWanted) return;
        var d = Device();
        if (d.Active) return;
        if (!d.Start(_core.Settings.Current.Stt.Language))
        {
            _deviceWake = false;
            Notice?.Invoke("Служба распознавания Android недоступна — активация по слову выключена.", "🎙️ распознавание");
            return;
        }
        SetState(RecorderState.Wake, null);
    }

    private void RestartDeviceWakeSoon(int delayMs = 300)
    {
        if (!_deviceWake) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(delayMs).ConfigureAwait(false);
            Post(StartDeviceWake);
        });
    }

    private void OnDeviceResult(string text)
    {
        if (_deviceCommand)
        {
            _deviceCommand = false;
            if (_core.Settings.Current.Voice.Chime) Sfx.ListenStop();
            SetState(_deviceWake ? RecorderState.Wake : RecorderState.Off, null);
            Submit(text);
            RestartDeviceWakeSoon(1500);
            return;
        }
        if (!_speech.IsActive && !_core.Agent.IsBusy)
        {
            var cmd = VoiceRecorder.MatchWake(text, _core.Settings.Current.Voice.WakePhrases);
            if (cmd is not null)
            {
                var meaningful = new string(cmd.Where(char.IsLetterOrDigit).ToArray());
                if (meaningful.Length >= 3)
                {
                    if (_core.Settings.Current.Voice.Chime) Sfx.ListenStart();
                    Heard?.Invoke(cmd);
                }
                else
                {
                    StartDeviceCommand();
                    return;
                }
            }
        }
        RestartDeviceWakeSoon();
    }

    private void OnDeviceFailed(string reason)
    {
        if (_deviceCommand)
        {
            _deviceCommand = false;
            var soft = reason is "short" or "timeout" or "cancel" or "busy";
            SetState(_deviceWake ? RecorderState.Wake : RecorderState.Off, soft ? reason : null);
            if (!soft) Notice?.Invoke(reason, "🎙️ распознавание");
            RestartDeviceWakeSoon(1200);
            return;
        }
        if (!_deviceWake) return;
        if (reason is "short" or "timeout" or "cancel") RestartDeviceWakeSoon(250);
        else if (reason == "busy") RestartDeviceWakeSoon(1500);
        else
        {
            _deviceWake = false;
            SetState(RecorderState.Off, null);
            Notice?.Invoke("Активация по слову остановлена: " + reason, "🎙️ распознавание");
        }
    }
}
