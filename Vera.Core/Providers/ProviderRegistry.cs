namespace Vera.Core.Providers;

public sealed class ProviderRegistry
{
    private readonly Dictionary<string, ILlmProvider> _providers;

    public ProviderRegistry(Func<string, string> getKey, HttpClient http, Func<string>? customBaseUrl = null)
    {
        _providers = new Dictionary<string, ILlmProvider>
        {
            ["openrouter"] = new OpenAICompatProvider("openrouter", getKey, http),
            ["openai"] = new OpenAICompatProvider("openai", getKey, http),
            ["anthropic"] = new AnthropicProvider(getKey),
            ["custom"] = new OpenAICompatProvider("custom", getKey, http, customBaseUrl),
        };
    }

    public static readonly IReadOnlyList<(string Id, string Title, string Hint)> Known = new[]
    {
        ("openrouter", "OpenRouter", "Любая модель OpenRouter с поддержкой tools, например openai/gpt-5-mini, anthropic/…, google/gemini-…"),
        ("anthropic", "Anthropic", "Например claude-opus-5-5 (умнее всех), claude-sonnet-5-5 (быстрее), claude-haiku-5-5 (дешевле)."),
        ("openai", "OpenAI", "Например gpt-5-mini, gpt-5, gpt-4.1-mini."),
        ("custom", "Свой сервер", "Любой OpenAI-совместимый сервер: Ollama (http://localhost:11434/v1), LM Studio (http://localhost:1234/v1)…"),
        ("local", "Локальная модель", "Встроенный движок: модель GGUF работает прямо на этом ПК, без интернета и оплаты."),
    };

    /// <summary>Добавить провайдера платформы (например, встроенную локальную модель на ПК).</summary>
    public void Register(ILlmProvider provider) => _providers[provider.Id] = provider;

    public bool IsAvailable(string id) => _providers.ContainsKey(id);

    /// <summary>Провайдеры, доступные на этой платформе.</summary>
    public IReadOnlyList<(string Id, string Title, string Hint)> Available => Known.Where(k => _providers.ContainsKey(k.Id)).ToList();

    /// <summary>Нужен ли провайдеру ключ API, чтобы работать.</summary>
    public static bool NeedsKey(string id) => id is "openrouter" or "anthropic" or "openai";

    public ILlmProvider Get(string id) =>
        _providers.TryGetValue(id, out var p) ? p : throw new ArgumentException("Неизвестный провайдер: " + id);

    public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(string provider, CancellationToken ct = default) => Get(provider).ListModelsAsync(ct);
}
