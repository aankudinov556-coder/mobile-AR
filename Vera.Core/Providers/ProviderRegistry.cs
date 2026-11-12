namespace Vera.Core.Providers;

public sealed class ProviderRegistry
{
    private readonly Dictionary<string, ILlmProvider> _providers;

    public ProviderRegistry(Func<string, string> getKey, HttpClient http)
    {
        _providers = new Dictionary<string, ILlmProvider>
        {
            ["openrouter"] = new OpenAICompatProvider("openrouter", getKey, http),
            ["openai"] = new OpenAICompatProvider("openai", getKey, http),
            ["anthropic"] = new AnthropicProvider(getKey),
        };
    }

    public static readonly IReadOnlyList<(string Id, string Title, string Hint)> Known = new[]
    {
        ("openrouter", "OpenRouter", "Любая модель OpenRouter с поддержкой tools, например openai/gpt-5-mini, anthropic/…, google/gemini-…"),
        ("anthropic", "Anthropic", "Например claude-opus-5-5 (умнее всех), claude-sonnet-5-5 (быстрее), claude-haiku-5-5 (дешевле)."),
        ("openai", "OpenAI", "Например gpt-5-mini, gpt-5, gpt-4.1-mini."),
    };

    public ILlmProvider Get(string id) =>
        _providers.TryGetValue(id, out var p) ? p : throw new ArgumentException("Неизвестный провайдер: " + id);

    public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(string provider, CancellationToken ct = default) => Get(provider).ListModelsAsync(ct);
}
