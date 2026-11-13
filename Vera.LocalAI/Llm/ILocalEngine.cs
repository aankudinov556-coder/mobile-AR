namespace Vera.LocalAI.Llm;

/// <summary>Сообщение для шаблона чата модели: system | user | assistant.</summary>
public sealed record ChatTurn(string Role, string Content);

public sealed record GenerateOptions
{
    public int MaxTokens { get; init; } = 1024;
    public float Temperature { get; init; } = 0.7f;
    public float TopP { get; init; } = 0.9f;
    public float RepeatPenalty { get; init; } = 1.1f;
    /// <summary>Остановить генерацию, как только в тексте появится одна из строк (сама строка не выводится).</summary>
    public IReadOnlyList<string> Stops { get; init; } = Array.Empty<string>();
}

/// <summary>Параметры загрузки модели.</summary>
public sealed record EngineLoadOptions(string ModelPath, int ContextSize, int GpuLayers, string Backend, int Threads = 0);

/// <summary>Итог генерации.</summary>
public sealed record GenerateStats(int PromptTokens, int CachedTokens, int OutputTokens, bool HitMaxTokens);

/// <summary>
/// Движок, который крутит модель на этом ПК. Реальный — <see cref="LlamaEngine"/> (llama.cpp через LLamaSharp),
/// в тестах — подделка.
/// </summary>
public interface ILocalEngine : IDisposable
{
    /// <summary>Загруженная модель (null — ничего не загружено).</summary>
    EngineLoadOptions? Loaded { get; }
    /// <summary>Загрузить модель (или ничего не делать, если уже загружена с теми же параметрами).</summary>
    Task EnsureLoadedAsync(EngineLoadOptions options, IProgress<float>? progress, CancellationToken ct);
    /// <summary>Размер окна контекста загруженной модели.</summary>
    int ContextSize { get; }
    /// <summary>Сколько токенов займёт переписка после шаблона чата.</summary>
    int CountTokens(IReadOnlyList<ChatTurn> messages);
    /// <summary>Сгенерировать ответ ассистента на переписку, отдавая текст кусочками.</summary>
    Task<GenerateStats> GenerateAsync(IReadOnlyList<ChatTurn> messages, GenerateOptions options, Action<string> onText, CancellationToken ct);
    /// <summary>Выгрузить модель из памяти.</summary>
    void Unload();
    /// <summary>Название ускорителя, на котором реально работает модель ("процессор", "Vulkan"…).</summary>
    string DeviceName { get; }
}
