using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vera.Core;

public static class VeraJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };
}

/// <summary>JSON-файл в хранилище платформы с отложенной записью.</summary>
public sealed class JsonFile<T> where T : class
{
    private readonly IVeraStorage _storage;
    private readonly string _name;
    private readonly Func<T> _factory;
    private readonly object _gate = new();
    private Timer? _timer;

    public JsonFile(IVeraStorage storage, string name, Func<T> factory)
    {
        _storage = storage;
        _name = name;
        _factory = factory;
        Data = Load();
    }

    public T Data { get; set; }

    private T Load()
    {
        try
        {
            var text = _storage.ReadText(_name);
            if (!string.IsNullOrWhiteSpace(text))
                return JsonSerializer.Deserialize<T>(text, VeraJson.Options) ?? _factory();
        }
        catch (Exception)
        {
            // повреждённый файл — начинаем с чистого листа
        }
        return _factory();
    }

    /// <summary>Сохранить через 300 мс (склеивает частые изменения).</summary>
    public void Save()
    {
        lock (_gate)
        {
            _timer ??= new Timer(_ => SaveNow(), null, Timeout.Infinite, Timeout.Infinite);
            _timer.Change(300, Timeout.Infinite);
        }
    }

    public void SaveNow()
    {
        lock (_gate)
        {
            _timer?.Change(Timeout.Infinite, Timeout.Infinite);
            try { _storage.WriteText(_name, JsonSerializer.Serialize(Data, VeraJson.Options)); }
            catch (Exception) { /* диск недоступен — попробуем при следующем изменении */ }
        }
    }
}

// ============================================================================ settings model

public sealed class VeraSettings
{
    public bool FirstRun { get; set; } = true;
    /// <summary>"openrouter" | "anthropic" | "openai" | "custom" (свой OpenAI-совместимый сервер) | "local" (встроенная модель, ПК)</summary>
    public string Provider { get; set; } = "openrouter";
    public Dictionary<string, string> Models { get; set; } = new()
    {
        ["openrouter"] = "openai/gpt-5-mini",
        ["anthropic"] = "claude-opus-5-5",
        ["openai"] = "gpt-5-mini",
    };
    /// <summary>Адрес своего OpenAI-совместимого сервера (Ollama, LM Studio, vLLM…), например http://localhost:11434/v1</summary>
    public string CustomBaseUrl { get; set; } = "http://localhost:11434/v1";
    /// <summary>Выполнять вызовы инструментов, которые модель написала текстом (JSON / &lt;tool_call&gt;).</summary>
    public bool ToolCallFallback { get; set; } = true;
    /// <summary>"low" | "medium" | "high"</summary>
    public string Effort { get; set; } = "low";
    public bool WebSearch { get; set; }
    public int HistoryLimit { get; set; } = 40;

    public string UserName { get; set; } = "семпай";
    /// <summary>"boss" | "tease" | "sweet" | "business" | "tsundere"</summary>
    public string Persona { get; set; } = "boss";
    /// <summary>Номер миграции характера (v2.4: «дерзкая» по умолчанию заменена на «строгую начальницу»).</summary>
    public int PersonaRev { get; set; }
    /// <summary>Номер миграции голоса (v2.5: пауза конца фразы короче — 1,1 с → 0,6–0,8 с).</summary>
    public int VoiceRev { get; set; }
    public string CustomPrompt { get; set; } = "";

    public SttSettings Stt { get; set; } = new();
    /// <summary>Звук, пока Вера печатает ответ (вместо синтеза речи, v2.5).</summary>
    public ReplySoundSettings Sound { get; set; } = new();
    /// <summary>Голосовой режим: экран для разговора (v2.5).</summary>
    public VoiceModeSettings VoiceMode { get; set; } = new();
    public VoiceSettings Voice { get; set; } = new();
    public HotkeySettings Hotkeys { get; set; } = new();
    public PetSettings Pet { get; set; } = new();
    public CapabilitySettings Capabilities { get; set; } = new();
    public ReminderSettings Reminders { get; set; } = new();
    public List<AppShortcut> AppShortcuts { get; set; } = new();
    /// <summary>Скин маскота: "vera" (пиксельная) | "halo" (рисунок пользователя).</summary>
    public string Skin { get; set; } = "vera";
    public SearchSettings Search { get; set; } = new();
    public LocalModelSettings Local { get; set; } = new();
    public DiagnosticsSettings Diagnostics { get; set; } = new();
    /// <summary>Синхронизация с телефоном.</summary>
    /// <summary>Язык интерфейса и ответов: "ru" | "en".</summary>
    /// <summary>"ru" | "en"; пусто — выбрать при первом запуске (новая установка — по языку системы, старая — русский).</summary>
    public string Language { get; set; } = "";
    /// <summary>Город для погоды на главном экране и в утренней сводке.</summary>
    public string City { get; set; } = "";
    public SyncSettings Sync { get; set; } = new();
    public bool Autostart { get; set; }
    /// <summary>"auto" | "dark" | "light"</summary>
    public string Theme { get; set; } = "auto";

    public string CurrentModel => Models.TryGetValue(Provider, out var m) && !string.IsNullOrWhiteSpace(m) ? m : DefaultModel(Provider);

    public static string DefaultModel(string provider) => provider switch
    {
        "anthropic" => "claude-opus-5-5",
        "openai" => "gpt-5-mini",
        "custom" => "qwen2.5:7b",
        "local" => "",
        _ => "openai/gpt-5-mini",
    };

    public VeraSettings Clone() => JsonSerializer.Deserialize<VeraSettings>(JsonSerializer.Serialize(this, VeraJson.Options), VeraJson.Options)!;
}

public sealed class SttSettings
{
    /// <summary>"openai" | "groq" | "device" (средствами ОС) | "whisper" (локальный Whisper, ПК) | "stream" (потоковое на ПК, sherpa-onnx) — последние три делает платформа</summary>
    public string Provider { get; set; } = "openai";
    /// <summary>Модель локального Whisper: "base" | "small" | "medium"</summary>
    public string WhisperModel { get; set; } = "small";
    public string OpenaiModel { get; set; } = "gpt-4o-mini-transcribe";
    public string GroqModel { get; set; } = "whisper-large-v3-turbo";
    /// <summary>"ru", "en", … или "" — автоопределение</summary>
    public string Language { get; set; } = "ru";
}

public sealed class ReplySoundSettings
{
    /// <summary>"pixel" | "drops" | "soft" | "off"</summary>
    public string Style { get; set; } = "pixel";
    /// <summary>"low" | "mid" | "high"</summary>
    public string Volume { get; set; } = "mid";
}

public sealed class VoiceModeSettings
{
    /// <summary>Открывать голосовой режим, когда зовут «Вера».</summary>
    public bool OpenOnWake { get; set; } = true;
    /// <summary>Анимация перехода: "full" (600 мс) | "fast" (200 мс) | "off"</summary>
    public string Animation { get; set; } = "full";
    /// <summary>Вернуться в чат после стольких секунд тишины; 0 — никогда.</summary>
    public int ExitAfterSeconds { get; set; } = 10;
}

public sealed class VoiceSettings
{
    public bool WakeWord { get; set; }
    public string WakePhrases { get; set; } = "вера, верочка, верунь";
    /// <summary>0..1, больше — чувствительнее</summary>
    public double Sensitivity { get; set; } = 0.5;
    public int SilenceMs { get; set; } = 650;
    public string MicDeviceId { get; set; } = "";
    public bool Chime { get; set; } = true;
    /// <summary>Чем слушать слово-активатор: "stt" (каждая фраза — в распознавание) | "vosk" (локально, без интернета)</summary>
    public string WakeEngine { get; set; } = "stt";
    /// <summary>Определение речи: "energy" (порог громкости) | "silero" (нейросеть Silero VAD)</summary>
    public string Vad { get; set; } = "energy";
}

public sealed class HotkeySettings
{
    public string ToggleChat { get; set; } = "Ctrl+Alt+V";
    public string Talk { get; set; } = "Ctrl+Alt+Space";
    public string Screenshot { get; set; } = "Ctrl+Alt+S";
    /// <summary>Голосовой режим (только пока окно Веры активно).</summary>
    public string VoiceMode { get; set; } = "Ctrl+Space";
}

public sealed class PetSettings
{
    public bool Visible { get; set; } = true;
    public int Scale { get; set; } = 3;
    public bool AlwaysOnTop { get; set; } = true;
    public double? X { get; set; }
    public double? Y { get; set; }
    public bool IdleChatter { get; set; } = true;
    public int BubbleSeconds { get; set; } = 9;
    /// <summary>Садиться на пол, когда скучно (простой без общения).</summary>
    public bool SitWhenBored { get; set; } = true;
    public int SitAfterMinutes { get; set; } = 3;
}

public sealed class SearchSettings
{
    /// <summary>Инструменты web_search / read_page для любой модели.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>"auto" | "duckduckgo" | "wikipedia" | "tavily" | "brave"</summary>
    public string Engine { get; set; } = "auto";
}

public sealed class LocalModelSettings
{
    /// <summary>Путь к GGUF-файлу выбранной модели.</summary>
    public string ModelPath { get; set; } = "";
    /// <summary>Размер контекста в токенах.</summary>
    public int ContextSize { get; set; } = 8192;
    /// <summary>Слои на видеокарте: -1 — авто (все, если есть ускоритель), 0 — только процессор.</summary>
    public int GpuLayers { get; set; } = -1;
    /// <summary>Ускорение: "auto" | "cpu" | "vulkan" | "cuda12"</summary>
    public string Backend { get; set; } = "auto";
    /// <summary>Выгружать модель из памяти после N минут простоя (0 — не выгружать).</summary>
    public int UnloadAfterMinutes { get; set; } = 10;
    /// <summary>Если локальная модель не справилась (ошибка) — повторить у облачного провайдера.</summary>
    public bool CloudFallback { get; set; }
    /// <summary>Облачный провайдер для гибридного режима.</summary>
    public string FallbackProvider { get; set; } = "openrouter";
}

public sealed class SyncSettings
{
    /// <summary>ПК: принимать подключения телефона в домашней сети.</summary>
    public bool Enabled { get; set; }
    /// <summary>Синхронизировать историю чата.</summary>
    public bool Chat { get; set; } = true;
    /// <summary>Передавать API-ключи (заполняются только недостающие).</summary>
    public bool Keys { get; set; }
    /// <summary>Где звенят напоминания: "both" | "pc" | "phone" | "active" (там, где я сейчас).</summary>
    public string RingOn { get; set; } = "both";
    /// <summary>TCP-порт сервера синхронизации на ПК.</summary>
    public int Port { get; set; } = 47320;
}

public sealed class DiagnosticsSettings
{
    /// <summary>Журнал запросов: сырые ответы моделей пишутся в requests.log.</summary>
    public bool LogRequests { get; set; }
}

public sealed class CapabilitySettings
{
    public bool PcControl { get; set; } = true;
    public bool Screenshots { get; set; } = true;
    public bool Clipboard { get; set; } = true;
    public bool RunCommands { get; set; }
    public bool ConfirmCommands { get; set; } = true;
}

public sealed class ReminderSettings
{
    public bool Speak { get; set; } = true;
    public bool Sound { get; set; } = true;
    public bool Notification { get; set; } = true;
}

public sealed class AppShortcut
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
}

/// <summary>Настройки: загрузка, изменение, событие Changed.</summary>
public sealed class SettingsService
{
    private readonly JsonFile<VeraSettings> _file;

    public SettingsService(IVeraStorage storage)
    {
        var existed = !string.IsNullOrWhiteSpace(storage.ReadText("settings.json"));
        _file = new JsonFile<VeraSettings>(storage, "settings.json", () => new VeraSettings());
        // v2.4.1: язык интерфейса. У тех, кто уже пользовался Верой, остаётся русский
        if (_file.Data.Language is not ("ru" or "en"))
        {
            _file.Data.Language = existed ? "ru" : L10n.SystemDefault();
            if (!existed && _file.Data.Language == "en") _file.Data.Stt.Language = "en";
            if (existed) _file.Save();
        }
        // новые провайдеры по умолчанию, если в старом файле их нет
        foreach (var p in new[] { "openrouter", "anthropic", "openai", "custom", "local" })
            if (!_file.Data.Models.ContainsKey(p)) _file.Data.Models[p] = VeraSettings.DefaultModel(p);
        // v2.4: характер по умолчанию заменён — переводим тех, у кого стоял прежний
        // v2.5: прежняя пауза 1,1 с делала ответ заметно медленнее — укорачиваем, если её не меняли
        if (_file.Data.VoiceRev < 1)
        {
            if (existed && _file.Data.Voice.SilenceMs == 1100) _file.Data.Voice.SilenceMs = _file.Data.Voice.Vad == "silero" ? 600 : 800;
            _file.Data.VoiceRev = 1;
            if (existed) _file.Save();
        }
        if (_file.Data.PersonaRev < 1)
        {
            if (_file.Data.Persona == "tease") _file.Data.Persona = Personas.Default;
            _file.Data.PersonaRev = 1;
            _file.Save();
        }
    }

    /// <summary>Текущие настройки (живой объект — не меняйте его напрямую, используйте Update).</summary>
    public VeraSettings Current => _file.Data;

    /// <summary>Срабатывает после каждого Update (в потоке вызывающего).</summary>
    public event Action<VeraSettings>? Changed;

    public VeraSettings Update(Action<VeraSettings> change)
    {
        lock (_file)
        {
            change(_file.Data);
        }
        _file.Save();
        Changed?.Invoke(_file.Data);
        return _file.Data;
    }

    public void Flush() => _file.SaveNow();

    /// <summary>Настройки в JSON для переноса на другой ПК (ключей API здесь нет — они в защищённом хранилище).</summary>
    public string Export()
    {
        lock (_file) return JsonSerializer.Serialize(_file.Data, VeraJson.Options);
    }

    /// <summary>Загрузить настройки из JSON. Позиция Веры на экране и путь к локальной модели остаются свои.</summary>
    public void Import(string json)
    {
        VeraSettings next;
        try
        {
            next = JsonSerializer.Deserialize<VeraSettings>(json, VeraJson.Options) ?? throw new FormatException();
        }
        catch (Exception e) when (e is JsonException or FormatException or NotSupportedException)
        {
            throw new FormatException("Это не файл настроек Веры.", e);
        }
        Replace(next, keepLocal: true);
    }

    /// <summary>Сбросить всё к начальным значениям (ключи, напоминания и память не трогаются).</summary>
    public void Reset() => Replace(new VeraSettings { FirstRun = false }, keepLocal: true);

    private void Replace(VeraSettings next, bool keepLocal)
    {
        lock (_file)
        {
            var cur = _file.Data;
            if (keepLocal)
            {
                next.Pet.X = cur.Pet.X;
                next.Pet.Y = cur.Pet.Y;
                next.Local.ModelPath = cur.Local.ModelPath;
                if (cur.Models.TryGetValue("local", out var lm)) next.Models["local"] = lm;
                next.AppShortcuts = next.AppShortcuts.Where(a => File.Exists(a.Path) || Directory.Exists(a.Path)).ToList();
                next.FirstRun = false;
            }
            foreach (var p in new[] { "openrouter", "anthropic", "openai", "custom", "local" })
                if (!next.Models.ContainsKey(p)) next.Models[p] = VeraSettings.DefaultModel(p);
            _file.Data = next;
        }
        _file.Save();
        Changed?.Invoke(_file.Data);
    }
}

/// <summary>Готовые наборы настроек: одной кнопкой переключить провайдера и распознавание.</summary>
public static class SettingsPresets
{
    public sealed record Preset(string Id, string Title, string About, Action<VeraSettings> Apply);

    public static readonly IReadOnlyList<Preset> All = new[]
    {
        new Preset("offline", "🌙 Бесплатно и офлайн",
            "Всё на этом ПК: встроенная модель, быстрое распознавание речи, имя слушает Vosk. Без ключей и интернета (кроме поиска). Нужно скачать модели.",
            s =>
            {
                s.Provider = "local";
                s.Stt.Provider = "stream";
                s.Voice.SilenceMs = 600;
                s.Voice.WakeEngine = "vosk";
                s.Voice.Vad = "silero";
                s.Search.Engine = "auto";
                s.WebSearch = false;
            }),
        new Preset("quality", "✨ Максимум качества",
            "Claude от Anthropic и распознавание речи OpenAI. Нужны ключи Anthropic и OpenAI.",
            s =>
            {
                s.Provider = "anthropic";
                s.Models["anthropic"] = VeraSettings.DefaultModel("anthropic");
                s.Effort = "medium";
                s.Stt.Provider = "openai";
                s.Voice.WakeEngine = "vosk";
                s.Voice.Vad = "silero";
            }),
        new Preset("cheap", "🪙 Экономно",
            "Недорогая модель через OpenRouter и распознавание Groq (почти бесплатно).",
            s =>
            {
                s.Provider = "openrouter";
                s.Models["openrouter"] = VeraSettings.DefaultModel("openrouter");
                s.Effort = "low";
                s.Stt.Provider = "groq";
                s.Voice.WakeEngine = "vosk";
                s.Voice.Vad = "silero";
            }),
    };

    /// <summary>Каких ключей не хватает для набора (имена секретов).</summary>
    public static IReadOnlyList<string> MissingKeys(string presetId, Func<string, bool> has) => presetId switch
    {
        "quality" => new[] { "anthropic", "openai" }.Where(k => !has(k)).ToList(),
        "cheap" => new[] { "openrouter", "groq" }.Where(k => !has(k)).ToList(),
        _ => Array.Empty<string>(),
    };
}
