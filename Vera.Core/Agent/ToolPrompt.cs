using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Vera.Core.Agent;

/// <summary>
/// Описание инструментов прямо в системном промпте — для моделей без нативного function calling
/// (часть моделей OpenRouter, встроенные локальные модели). Модель отвечает блоками &lt;tool_call&gt;,
/// которые разбирает <see cref="ToolCallParser"/>.
/// </summary>
public static class ToolPrompt
{
    public static string Describe(IReadOnlyList<ToolDefinition> tools, bool compact = false)
    {
        if (tools.Count == 0) return "";
        var sb = new StringBuilder();
        sb.AppendLine("# Инструменты");
        sb.AppendLine("Ты можешь вызывать инструменты. Чтобы вызвать инструмент, напиши ТОЛЬКО блок:");
        sb.AppendLine("<tool_call>");
        sb.AppendLine("{\"name\": \"имя_инструмента\", \"arguments\": {\"параметр\": \"значение\"}}");
        sb.AppendLine("</tool_call>");
        sb.AppendLine("Можно несколько блоков подряд. После вызова придёт сообщение с <tool_response> — тогда коротко ответь пользователю.");
        sb.AppendLine("Не придумывай результаты инструментов и не показывай пользователю JSON. Если инструмент не нужен — просто отвечай.");
        sb.AppendLine();
        sb.AppendLine("Доступные инструменты:");
        foreach (var t in tools)
        {
            var desc = compact ? FirstSentence(t.Description) : t.Description;
            sb.Append("- ").Append(t.Name).Append(": ").AppendLine(desc);
            var props = t.Parameters["properties"] as JsonObject;
            var required = (t.Parameters["required"] as JsonArray)?.Select(x => x?.GetValue<string>()).ToHashSet() ?? new HashSet<string?>();
            if (props is null || props.Count == 0) continue;
            foreach (var (name, schema) in props)
            {
                var type = schema?["type"]?.GetValue<string>() ?? "string";
                var en = schema?["enum"] is JsonArray e ? " (" + string.Join("|", e.Select(x => x?.GetValue<string>())) + ")" : "";
                var pd = schema?["description"]?.GetValue<string>();
                sb.Append("    • ").Append(name).Append(": ").Append(type).Append(en)
                  .Append(required.Contains(name) ? ", обязательно" : "")
                  .AppendLine(string.IsNullOrEmpty(pd) || compact ? "" : " — " + pd);
            }
        }
        return sb.ToString();
    }

    /// <summary>Результаты инструментов в виде сообщения пользователя (для «текстовых» вызовов).</summary>
    public static string Results(IEnumerable<(string Name, ToolResult Result)> results)
    {
        var sb = new StringBuilder();
        foreach (var (name, r) in results)
        {
            sb.Append("<tool_response name=\"").Append(name).Append("\"").Append(r.IsError ? " error=\"true\"" : "").AppendLine(">");
            sb.AppendLine(r.Text);
            sb.AppendLine("</tool_response>");
        }
        sb.Append("Продолжи ответ пользователю обычным текстом (без JSON и без тегов).");
        return sb.ToString();
    }

    private static string FirstSentence(string s)
    {
        var i = s.IndexOf(". ", StringComparison.Ordinal);
        return i > 20 ? s[..(i + 1)] : s;
    }

    public static string Json(JsonObject o) => o.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
}
