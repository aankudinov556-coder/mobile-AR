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
    /// <summary>"openrouter" | "anthropic" | "openai"</summary>
    public string Provider { get; set; } = "openrouter";
    public Dictionary<string, string> Models { get; set; } = new()
    {
        ["openrouter"] = "openai/gpt-5-mini",
        ["anthropic"] = "claude-opus-5-5",
        ["openai"] = "gpt-5-mini",
    };
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
    public bool Autostart { get; set; }
    /// <summary>"auto" | "dark" | "light"</summary>
    public string Theme { get; set; } = "auto";

    public string CurrentModel => Models.TryGetValue(Provider, out var m) && !string.IsNullOrWhiteSpace(m) ? m : DefaultModel(Provider);

    public static string DefaultModel(string provider) => provider switch
    {
        "anthropic" => "claude-opus-5-5",
        "openai" => "gpt-5-mini",
        _ => "openai/gpt-5-mini",
    };

    public VeraSettings Clone() => JsonSerializer.Deserialize<VeraSettings>(JsonSerializer.Serialize(this, VeraJson.Options), VeraJson.Options)!;
}

public sealed class SttSettings
{
    /// <summary>"openai" | "groq" | "device" (распознавание средствами ОС — делает платформа)</summary>
    public string Provider { get; set; } = "openai";
    public string OpenaiModel { get; set; } = "gpt-4o-mini-transcribe";
    public string GroqModel { get; set; } = "whisper-large-v3-turbo";
    /// <summary>"ru", "en", … или "" — автоопределение</summary>
    public string Language { get; set; } = "ru";
}

public sealed class TtsSettings
{
    /// <summary>"openai" | "elevenlabs" | "system" | "off"</summary>
    public string Engine { get; set; } = "openai";
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
        foreach (var p in new[] { "openrouter", "anthropic", "openai" })
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
