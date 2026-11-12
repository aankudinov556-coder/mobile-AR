using Vera.Core.Agent;
using Vera.Core.Providers;
using Vera.Core.Speech;
using Vera.Core.Tools;

namespace Vera.Core;

/// <summary>
/// Корень ядра Веры: создаётся один раз платформой (ПК или Android) и раздаёт сервисы.
/// </summary>
public sealed class VeraCore : IDisposable
{
    /// <param name="storage">файлы данных приложения</param>
    /// <param name="secrets">защищённое хранилище ключей</param>
    /// <param name="platformTools">инструменты платформы (может быть null)</param>
    /// <param name="platform">"desktop" | "mobile" — влияет на системный промпт</param>
    public VeraCore(IVeraStorage storage, ISecretStore secrets, IPlatformTools? platformTools, string platform)
    {
        Platform = platform;
        Http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("VeraAssistant/2.0");
        Settings = new SettingsService(storage);
        Secrets = new SecretsService(secrets);
        Reminders = new ReminderService(storage);
        Memory = new MemoryService(storage);
        Providers = new ProviderRegistry(Secrets.Get, Http);
        Tools = new ToolRegistry(Reminders, Memory, Http, platformTools);
        Speech = new SpeechService(Settings, Secrets, Http);
        Agent = new VeraAgent(Settings, Providers, Tools, Memory, storage, platform);
    }

    public string Platform { get; }
    public HttpClient Http { get; }
    public SettingsService Settings { get; }
    public SecretsService Secrets { get; }
    public ReminderService Reminders { get; }
    public MemoryService Memory { get; }
    public ProviderRegistry Providers { get; }
    public ToolRegistry Tools { get; }
    public SpeechService Speech { get; }
    public VeraAgent Agent { get; }

    private string UserName => string.IsNullOrWhiteSpace(Settings.Current.UserName) ? "семпай" : Settings.Current.UserName;

    /// <summary>Готовая реплика характера: "poke", "drag", "idle", "listening", "notHeard", "reminder", "missed", …</summary>
    public string Line(string kind, string? text = null) => Personas.Line(Settings.Current.Persona, kind, UserName, text);

    /// <summary>Приветствие при запуске; NoKey — нет ключа текущего провайдера.</summary>
    public (string Text, bool NoKey) Greeting()
    {
        var noKey = !Secrets.Has(Settings.Current.Provider);
        var kind = noKey ? "noKey" : Personas.GreetingKind(DateTime.Now);
        return (Line(kind), noKey);
    }

    /// <summary>Фраза, которую Вера говорит при срабатывании напоминания.</summary>
    public string ReminderPhrase(Reminder r, bool missed) => Line(missed ? "missed" : "reminder", r.Text);

    /// <summary>Проверить ключ: openrouter/anthropic/openai/groq/elevenlabs.</summary>
    public async Task<string> TestKeyAsync(string name, CancellationToken ct = default)
    {
        switch (name)
        {
            case "groq": return await Speech.TestGroqAsync(ct);
            case "elevenlabs":
                var v = await Speech.ElevenVoicesAsync(ct);
                return $"Ключ работает ({v.Count} голосов)";
            default: return await Providers.Get(name).TestAsync(ct);
        }
    }

    /// <summary>Сохранить всё на диск немедленно (при выходе).</summary>
    public void Flush()
    {
        Settings.Flush();
        Reminders.Flush();
        Memory.Flush();
        Agent.Flush();
    }

    public void Dispose()
    {
        Flush();
        Reminders.Dispose();
        Http.Dispose();
    }
}
