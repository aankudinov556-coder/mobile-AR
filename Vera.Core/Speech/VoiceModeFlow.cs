namespace Vera.Core.Speech;

/// <summary>Что сейчас происходит в голосовом режиме — для надписи и анимации.</summary>
public enum VoicePhase
{
    /// <summary>Микрофон открыт, ждём речь.</summary>
    Listening,
    /// <summary>Человек говорит (идёт распознавание).</summary>
    Hearing,
    /// <summary>Фраза отправлена, Вера думает.</summary>
    Thinking,
    /// <summary>Вера печатает ответ («голосок» звучит).</summary>
    Answering,
    /// <summary>Пауза между ответом и следующим прослушиванием.</summary>
    Waiting,
}

/// <summary>Что платформе сделать после события или тика.</summary>
public enum VoiceModeAction { None, StartListening, Exit }

/// <summary>
/// Разговор «без рук» в голосовом режиме: после ответа Вера снова слушает, после долгой тишины режим закрывается.
/// Платформа сообщает события (распознавание, ход агента, звук) и раз в ~200 мс вызывает <see cref="Tick"/>.
/// Не потокобезопасен — вызывать из UI-потока.
/// </summary>
public sealed class VoiceModeFlow
{
    /// <summary>Пауза после ответа перед новым прослушиванием (чтобы не ловить хвост звука).</summary>
    public static readonly TimeSpan ListenAgainDelay = TimeSpan.FromMilliseconds(450);

    private readonly Func<int> _exitAfterSeconds;
    private DateTimeOffset _lastSpeech;
    private DateTimeOffset? _listenAt;
    private bool _turn;
    private bool _sound;

    /// <param name="exitAfterSeconds">через сколько секунд тишины выйти (0 — никогда); читается каждый раз</param>
    public VoiceModeFlow(Func<int> exitAfterSeconds) => _exitAfterSeconds = exitAfterSeconds;

    public bool Active { get; private set; }
    public VoicePhase Phase { get; private set; } = VoicePhase.Listening;
    /// <summary>Распознанный текст текущей/последней фразы.</summary>
    public string Heard { get; private set; } = "";
    /// <summary>Промежуточный текст, пока человек говорит.</summary>
    public string Partial { get; private set; } = "";

    public event Action? Changed;

    public VoiceModeAction Enter(DateTimeOffset now)
    {
        Active = true;
        _lastSpeech = now;
        _turn = _sound = false;
        _listenAt = null;
        Heard = Partial = "";
        Set(VoicePhase.Listening);
        return VoiceModeAction.StartListening;
    }

    public void Exit()
    {
        Active = false;
        _listenAt = null;
        Changed?.Invoke();
    }

    /// <summary>Микрофон начал слушать.</summary>
    public void OnListening()
    {
        if (!Active) return;
        _listenAt = null;
        Partial = "";
        Set(VoicePhase.Listening);
    }

    /// <summary>Человек заговорил или пришёл промежуточный текст.</summary>
    public void OnSpeech(DateTimeOffset now, string? partial = null)
    {
        if (!Active) return;
        _lastSpeech = now;
        if (partial is not null) Partial = partial;
        Set(VoicePhase.Hearing);
    }

    /// <summary>Фраза распознана и уходит Вере.</summary>
    public void OnHeard(DateTimeOffset now, string text)
    {
        if (!Active) return;
        _lastSpeech = now;
        Heard = text;
        Partial = "";
        Set(VoicePhase.Thinking);
    }

    /// <summary>Ничего не сказали (тишина / слишком коротко): слушать дальше или выйти.</summary>
    public VoiceModeAction OnNothingHeard(DateTimeOffset now)
    {
        if (!Active) return VoiceModeAction.None;
        var limit = _exitAfterSeconds();
        if (limit > 0 && now - _lastSpeech >= TimeSpan.FromSeconds(limit)) return VoiceModeAction.Exit;
        Set(VoicePhase.Waiting);
        _listenAt = now + ListenAgainDelay;
        return VoiceModeAction.None;
    }

    public void OnTurnStarted()
    {
        if (!Active) return;
        _turn = true;
        _listenAt = null;
        Set(VoicePhase.Thinking);
    }

    public void OnAnswerText()
    {
        if (!Active || Phase == VoicePhase.Answering) return;
        Set(VoicePhase.Answering);
    }

    public void OnTurnEnded(DateTimeOffset now)
    {
        if (!Active) return;
        _turn = false;
        _lastSpeech = now; // тишина считается от конца ответа
        ScheduleIfIdle(now);
    }

    /// <summary>«Голосок» зазвучал / затих.</summary>
    public void OnSound(bool playing, DateTimeOffset now)
    {
        if (!Active) return;
        _sound = playing;
        if (!playing)
        {
            _lastSpeech = now;
            ScheduleIfIdle(now);
        }
    }

    /// <summary>Пора ли снова слушать.</summary>
    public VoiceModeAction Tick(DateTimeOffset now)
    {
        if (!Active || _listenAt is not { } at || now < at) return VoiceModeAction.None;
        _listenAt = null;
        return VoiceModeAction.StartListening;
    }

    private void ScheduleIfIdle(DateTimeOffset now)
    {
        if (_turn || _sound) return;
        Set(VoicePhase.Waiting);
        _listenAt = now + ListenAgainDelay;
    }

    private void Set(VoicePhase p)
    {
        Phase = p;
        Changed?.Invoke();
    }

    /// <summary>Длительность перехода в режим, мс: "full" 600, "fast" 200, "off" (или без анимаций в системе) 0.</summary>
    public static int EnterMs(string? animation, bool systemAnimationsOff) =>
        systemAnimationsOff ? 0 : animation switch { "off" => 0, "fast" => 200, _ => 600 };

    /// <summary>Выход — вдвое быстрее (полная — 300 мс).</summary>
    public static int ExitMs(string? animation, bool systemAnimationsOff) => EnterMs(animation, systemAnimationsOff) / 2;
}
