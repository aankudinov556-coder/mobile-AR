using System.Text.Json.Nodes;

namespace Vera.Core;

/// <summary>Хранилище текстовых файлов в папке данных приложения (реализует платформа).</summary>
public interface IVeraStorage
{
    /// <summary>Содержимое файла или null, если файла нет.</summary>
    string? ReadText(string name);

    /// <summary>Атомарная запись (через временный файл).</summary>
    void WriteText(string name, string content);
}

/// <summary>Защищённое хранилище ключей API (DPAPI на Windows, Keystore на Android).</summary>
public interface ISecretStore
{
    string? Get(string name);

    /// <summary>null или пустая строка — удалить.</summary>
    void Set(string name, string? value);
}

/// <summary>
/// Платформенные инструменты (управление ПК / действия на телефоне).
/// Ядро добавляет их к общим инструментам (напоминания, память, погода).
/// </summary>
public interface IPlatformTools
{
    IEnumerable<ToolDefinition> GetDefinitions(VeraSettings settings);

    /// <summary>Выполнить инструмент. Вернуть null, если имя не принадлежит этой платформе.</summary>
    Task<ToolResult?> TryExecuteAsync(string name, JsonObject args, CancellationToken ct);
}

/// <summary>Картинка в base64 (скриншот, фото).</summary>
public sealed record ImageData(string Mime, string Base64);

/// <summary>Описание инструмента для модели. Parameters — JSON Schema объекта.</summary>
public sealed class ToolDefinition
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required JsonObject Parameters { get; init; }

    public static JsonObject Schema(JsonObject properties, params string[] required) => new()
    {
        ["type"] = "object",
        ["properties"] = properties,
        ["required"] = new JsonArray(required.Select(r => (JsonNode)r).ToArray()),
    };

    public static JsonObject Str(string description) => new() { ["type"] = "string", ["description"] = description };
    public static JsonObject Num(string description) => new() { ["type"] = "number", ["description"] = description };
    public static JsonObject Enum(string description, params string[] values) => new()
    {
        ["type"] = "string",
        ["description"] = description,
        ["enum"] = new JsonArray(values.Select(v => (JsonNode)v).ToArray()),
    };
}

/// <summary>Результат инструмента: текст для модели + подпись-чип для интерфейса.</summary>
public sealed class ToolResult
{
    public required string Text { get; init; }
    public IReadOnlyList<ImageData>? Images { get; init; }
    public bool IsError { get; init; }
    /// <summary>Короткая подпись для чипа в чате, например «⏰ Выпить воды — 15:00».</summary>
    public string? Label { get; init; }
    /// <summary>Тип события для анимаций: "reminder", "memory", …</summary>
    public string? Kind { get; init; }

    public static ToolResult Ok(string text, string? label = null, string? kind = null) => new() { Text = text, Label = label, Kind = kind };
    public static ToolResult Error(string text, string? label = null) => new() { Text = text, Label = label, IsError = true };
}

public static class JsonArgs
{
    public static string? Str(this JsonObject o, string key)
    {
        if (!o.TryGetPropertyValue(key, out var n) || n is null) return null;
        return n.GetValueKind() == System.Text.Json.JsonValueKind.String ? n.GetValue<string>() : n.ToJsonString();
    }

    public static double? Num(this JsonObject o, string key)
    {
        if (!o.TryGetPropertyValue(key, out var n) || n is null) return null;
        return n.GetValueKind() switch
        {
            System.Text.Json.JsonValueKind.Number => double.Parse(n.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture),
            System.Text.Json.JsonValueKind.String when double.TryParse(n.GetValue<string>(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var d) => d,
            _ => null,
        };
    }
}
