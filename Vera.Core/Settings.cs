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
    /// <summary>"tease" | "sweet" | "business" | "tsundere"</summary>
    public string Persona { get; set; } = "tease";
    public string CustomPrompt { get; set; } = "";

    public SttSettings Stt { get; set; } = new();
    public TtsSettings Tts { get; set; } = new();
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
    /// <summary>"openai" | "groq" | "device" (средствами ОС) | "whisper" (локальный Whisper, ПК) — два последних делает платформа</summary>
    public string Provider { get; set; } = "openai";
    /// <summary>Модель локального Whisper: "base" | "small" | "medium"</summary>
    public string WhisperModel { get; set; } = "small";
    public string OpenaiModel { get; set; } = "gpt-4o-mini-transcribe";
    public string GroqModel { get; set; } = "whisper-large-v3-turbo";
    /// <summary>"ru", "en", … или "" — автоопределение</summary>
    public string Language { get; set; } = "ru";
}

public sealed class TtsSettings
{
    /// <summary>"openai" | "elevenlabs" | "system" | "piper" (офлайн, ПК) | "off"</summary>
    public string Engine { get; set; } = "openai";
    /// <summary>Голос Piper, например "ru_RU-irina-medium"</summary>
    public string PiperVoice { get; set; } = "ru_RU-irina-medium";
    /// <summary>"voice" — только на голосовые запросы, "always", "never"</summary>
    public string AutoSpeak { get; set; } = "voice";
    public string OpenaiModel { get; set; } = "gpt-4o-mini-tts";
    public string OpenaiVoice { get; set; } = "shimmer";
    public string OpenaiInstructions { get; set; } =
        "Говори как озорная дерзкая девушка-аниме лет двадцати: игриво, с лёгкой насмешкой и улыбкой в голосе, живо и быстро.";
    public string ElevenVoiceId { get; set; } = "";
    public string ElevenModel { get; set; } = "eleven_multilingual_v2";
    public string SystemVoice { get; set; } = "";
    public double Rate { get; set; } = 1.0;
    public double Volume { get; set; } = 0.9;
}

public sealed class VoiceSettings
{
    public bool WakeWord { get; set; }
    public string WakePhrases { get; set; } = "вера, верочка, верунь";
    /// <summary>0..1, больше — чувствительнее</summary>
    public double Sensitivity { get; set; } = 0.5;
    public int SilenceMs { get; set; } = 1100;
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
        _file = new JsonFile<VeraSettings>(storage, "settings.json", () => new VeraSettings());
        // новые провайдеры по умолчанию, если в старом файле их нет
        foreach (var p in new[] { "openrouter", "anthropic", "openai", "custom", "local" })
            if (!_file.Data.Models.ContainsKey(p)) _file.Data.Models[p] = VeraSettings.DefaultModel(p);
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
}
