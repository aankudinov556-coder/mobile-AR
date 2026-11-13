using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Vera.Core.Providers;

/// <summary>Сообщение истории в формате, не зависящем от провайдера.</summary>
public sealed class VeraMessage
{
    /// <summary>"user" | "assistant" | "tool"</summary>
    public string Role { get; set; } = "user";
    public string? Text { get; set; }
    /// <summary>Картинки текущей сессии (на диск не сохраняются).</summary>
    [JsonIgnore] public List<ImageData>? Images { get; set; }
    public bool HadImages { get; set; }
    /// <summary>Контекст времени, добавленный программой (часть истории — не меняется задним числом).</summary>
    public string? Ctx { get; set; }
    public DateTimeOffset Ts { get; set; } = DateTimeOffset.Now;
    public string? Source { get; set; }
    public bool Hidden { get; set; }
    public List<ToolCall>? ToolCalls { get; set; }
    public string? ToolCallId { get; set; }
    public string? Name { get; set; }
    public bool IsError { get; set; }
    public string? Label { get; set; }
    public string? TurnId { get; set; }
    public string? Emotion { get; set; }
    /// <summary>Документы текущей сессии (на диск не сохраняются — остаются только имена).</summary>
    [JsonIgnore] public List<DocumentText>? Documents { get; set; }
    public List<string>? DocNames { get; set; }
}

/// <summary>Текст документа, приложенного к сообщению (PDF, DOCX, TXT…).</summary>
public sealed record DocumentText(string Id, string Name, string Text, int Pages = 0);

public sealed class ToolCall
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public JsonObject Args { get; set; } = new();
    public string? Label { get; set; }
}

/// <summary>Результат выполнения инструмента с привязкой к вызову.</summary>
public sealed record ToolExecution(string CallId, string Name, ToolResult Result);

/// <summary>Модель в списке. Цена — в долларах за миллион токенов (если известна).</summary>
public sealed record ModelInfo(string Id, string Name, bool Tools, bool Vision, double? InputPerMTok = null, double? OutputPerMTok = null);

public sealed class StepRequest
{
    public required string Model { get; init; }
    public required string System { get; init; }
    public required IReadOnlyList<ToolDefinition> Tools { get; init; }
    public string Effort { get; init; } = "low";
    public bool WebSearch { get; init; }
}

/// <summary>Итог одного вызова модели.</summary>
public sealed class StepResult
{
    public string Text { get; init; } = "";
    public List<ToolCall> ToolCalls { get; init; } = new();
    /// <summary>"end" | "tool_use" | "pause" | "refusal" | "max_tokens"</summary>
    public string Stop { get; init; } = "end";
    /// <summary>Токены запроса и ответа (если провайдер сообщил).</summary>
    public int? InputTokens { get; init; }
    public int? OutputTokens { get; init; }
    /// <summary>Сырой ответ для журнала запросов (коротко).</summary>
    public string? Raw { get; init; }
}

/// <summary>Переписка в «родном» формате провайдера, только дописывается в рамках хода.</summary>
public abstract class ProviderConversation
{
}

public interface ILlmProvider
{
    string Id { get; }
    string Title { get; }
    ProviderConversation Begin(IReadOnlyList<VeraMessage> history);
    /// <summary>Один вызов модели со стримингом текста. Ответ модели дописывается в conv.</summary>
    Task<StepResult> StepAsync(StepRequest request, ProviderConversation conv, Action<string> onText, CancellationToken ct);
    void AppendToolResults(ProviderConversation conv, IReadOnlyList<ToolExecution> results);
    /// <summary>Дописать сообщение от имени пользователя (например, результаты инструментов, вызванных текстом).</summary>
    void AppendUserText(ProviderConversation conv, string text, IReadOnlyList<ImageData>? images = null);
    Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken ct = default);
    Task<string> TestAsync(CancellationToken ct = default);
}

/// <summary>Понятная пользователю ошибка провайдера.</summary>
public sealed class ProviderException : Exception
{
    public ProviderException(string message, int? status = null, Exception? inner = null) : base(message, inner) => Status = status;
    public int? Status { get; }
    /// <summary>Модель отвергла запрос из-за инструментов (не поддерживает function calling).</summary>
    public bool ToolsUnsupported { get; init; }

    public static ProviderException From(string title, int? status, string? detail, Exception? inner = null)
    {
        var d = (detail ?? "").ToLowerInvariant();
        if ((status is 400 or 404 or 422 or null) && d.Contains("tool") &&
            (d.Contains("support") || d.Contains("not allowed") || d.Contains("no endpoints")))
        {
            return new ProviderException(
                $"{title}: эта модель не умеет вызывать инструменты (напоминания, команды ПК, поиск). " +
                "Выбери модель с пометкой «инструменты» или оставь включённым «Разбор команд из текста» — тогда инструменты отключатся для этой модели.",
                status, inner) { ToolsUnsupported = true };
        }
        var msg = status switch
        {
            401 or 403 => $"{title}: неверный или неактивный API-ключ ({status}).",
            402 => $"{title}: недостаточно средств на балансе (402).",
            404 => $"{title}: модель не найдена — проверь название модели в настройках (404).",
            429 => $"{title}: слишком много запросов / лимит исчерпан (429). Подожди немного.",
            >= 500 => $"{title}: сервер временно недоступен ({status}).",
            400 => $"{title}: запрос отклонён (400): {Short(detail)}",
            _ when inner is HttpRequestException => $"{title}: нет соединения с сервером. Проверь интернет.",
            _ => $"{title}: {Short(detail)}",
        };
        return new ProviderException(msg, status, inner);
    }

    private static string Short(string? s)
    {
        s ??= "ошибка";
        return s.Length > 300 ? s[..300] : s;
    }
}
