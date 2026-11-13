namespace Vera.Core;

/// <summary>Статус ключа для интерфейса (сам ключ наружу не отдаём).</summary>
public sealed record SecretStatus(string Name, string Title, bool IsSet, string? Hint, string Purpose, string Url);

public sealed class SecretsService
{
    public static readonly IReadOnlyList<(string Name, string Title, string Purpose, string Url)> Known = new[]
    {
        ("openrouter", "OpenRouter", "чат — сотни моделей по одному ключу", "https://openrouter.ai/keys"),
        ("anthropic", "Anthropic", "чат — Claude", "https://console.anthropic.com/settings/keys"),
        ("openai", "OpenAI", "чат, голос и распознавание речи", "https://platform.openai.com/api-keys"),
        ("groq", "Groq", "необязательно — быстрое распознавание речи", "https://console.groq.com/keys"),
        ("elevenlabs", "ElevenLabs", "необязательно — голоса ElevenLabs", "https://elevenlabs.io/app/settings/api-keys"),
        ("tavily", "Tavily", "необязательно — веб-поиск получше (есть бесплатный лимит)", "https://app.tavily.com/home"),
        ("brave", "Brave Search", "необязательно — веб-поиск Brave (есть бесплатный лимит)", "https://api-dashboard.search.brave.com/app/keys"),
        ("custom", "Свой сервер", "необязательно — ключ своего OpenAI-совместимого сервера", ""),
    };

    private readonly ISecretStore _store;

    public SecretsService(ISecretStore store) => _store = store;

    public event Action? Changed;

    public string Get(string name) => _store.Get(name) ?? "";

    public bool Has(string name) => !string.IsNullOrWhiteSpace(_store.Get(name));

    public void Set(string name, string? value)
    {
        if (!Known.Any(k => k.Name == name)) throw new ArgumentException("Неизвестный ключ: " + name);
        _store.Set(name, string.IsNullOrWhiteSpace(value) ? null : value.Trim());
        Changed?.Invoke();
    }

    public IReadOnlyList<SecretStatus> Status() => Known.Select(k =>
    {
        var v = _store.Get(k.Name);
        var set = !string.IsNullOrWhiteSpace(v);
        string? hint = set ? (v!.Length > 8 ? v[..4] + "…" + v[^4..] : "••••") : null;
        return new SecretStatus(k.Name, k.Title, set, hint, k.Purpose, k.Url);
    }).ToList();
}
