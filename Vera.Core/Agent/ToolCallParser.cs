using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Vera.Core.Agent;

/// <summary>Вызов инструмента, найденный в тексте ответа.</summary>
public sealed record TextToolCall(string Name, JsonObject Args);

/// <summary>
/// Разбор вызовов инструментов, которые модель написала обычным текстом вместо настоящего tool_call.
/// Так делают модели без поддержки function calling (часть моделей OpenRouter) и локальные модели.
/// Понимает:
///   &lt;tool_call&gt;{"name": "...", "arguments": {...}}&lt;/tool_call&gt;     (Qwen / Hermes)
///   ```json { "name": "...", "arguments"|"parameters"|"args"|"input": {...} } ```
///   голый JSON-объект или массив таких объектов,
///   {"type":"function","function":{"name":"...","arguments":"{...}"}} и {"tool_calls":[...]}.
/// </summary>
public static partial class ToolCallParser
{
    /// <summary>
    /// Найти вызовы известных инструментов. cleaned — текст без JSON-вызовов.
    /// strict = true: голый JSON засчитывается, только если кроме него в ответе почти нет текста
    /// (чтобы не выполнить пример JSON, который пользователь сам попросил показать).
    /// </summary>
    public static bool TryExtract(string? text, IReadOnlyCollection<string> knownTools, out List<TextToolCall> calls, out string cleaned, bool strict = true)
    {
        calls = new List<TextToolCall>();
        cleaned = text ?? "";
        if (string.IsNullOrWhiteSpace(text) || knownTools.Count == 0) return false;
        var known = new HashSet<string>(knownTools, StringComparer.OrdinalIgnoreCase);
        var removals = new List<(int Start, int Length)>();

        // 1) <tool_call> ... </tool_call> (также незакрытый в конце ответа)
        foreach (Match m in ToolCallTag().Matches(text))
        {
            var found = ParseCalls(m.Groups[1].Value, known);
            if (found.Count == 0) continue;
            calls.AddRange(found);
            removals.Add((m.Index, m.Length));
        }

        // 2) блоки ```json ... ``` / ``` ... ```
        if (calls.Count == 0)
        {
            foreach (Match m in Fence().Matches(text))
            {
                var found = ParseCalls(m.Groups[1].Value, known);
                if (found.Count == 0) continue;
                calls.AddRange(found);
                removals.Add((m.Index, m.Length));
            }
        }

        // 3) голые JSON-объекты / массивы в тексте
        if (calls.Count == 0)
        {
            foreach (var (start, length) in JsonSpans(text))
            {
                var found = ParseCalls(text.Substring(start, length), known);
                if (found.Count == 0) continue;
                calls.AddRange(found);
                removals.Add((start, length));
            }
            if (calls.Count > 0 && strict)
            {
                var rest = Remove(text, removals);
                if (rest.Trim().Length > 220) // много обычного текста — похоже на пример, а не на вызов
                {
                    calls.Clear();
                    return false;
                }
            }
        }

        if (calls.Count == 0) return false;
        cleaned = Tidy(Remove(text, removals));
        return true;
    }

    private static string Remove(string text, List<(int Start, int Length)> spans)
    {
        var sb = new StringBuilder(text);
        foreach (var (start, length) in spans.OrderByDescending(s => s.Start)) sb.Remove(start, length);
        return sb.ToString();
    }

    private static string Tidy(string s) => MultiBlank().Replace(s, "\n\n").Trim();

    /// <summary>Разобрать JSON-фрагмент в список вызовов известных инструментов.</summary>
    public static List<TextToolCall> ParseCalls(string json, IReadOnlySet<string> known)
    {
        var result = new List<TextToolCall>();
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json.Trim(), documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        }
        catch (JsonException)
        {
            return result;
        }
        Collect(node, known, result);
        return result;
    }

    private static void Collect(JsonNode? node, IReadOnlySet<string> known, List<TextToolCall> result)
    {
        switch (node)
        {
            case JsonArray arr:
                foreach (var item in arr) Collect(item, known, result);
                break;
            case JsonObject obj:
                if (obj["tool_calls"] is JsonArray calls) { Collect(calls, known, result); break; }
                if (obj["function"] is JsonObject fn && fn["name"] is not null) { Collect(fn, known, result); break; }
                var name = Str(obj["name"]) ?? Str(obj["tool"]) ?? Str(obj["tool_name"]) ?? Str(obj["function"]);
                if (name is null || !known.Contains(name)) break;
                var argsNode = obj["arguments"] ?? obj["parameters"] ?? obj["args"] ?? obj["input"];
                var args = argsNode switch
                {
                    JsonObject o => (JsonObject)o.DeepClone(),
                    JsonValue v when v.GetValueKind() == JsonValueKind.String => TryParseObject(v.GetValue<string>()),
                    null => ArgsFromRest(obj),
                    _ => new JsonObject(),
                };
                result.Add(new TextToolCall(known.First(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase)), args));
                break;
        }
    }

    private static string? Str(JsonNode? n) =>
        n is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;

    private static JsonObject TryParseObject(string s)
    {
        try { return JsonNode.Parse(s) as JsonObject ?? new JsonObject(); }
        catch (JsonException) { return new JsonObject(); }
    }

    /// <summary>{"name": "x", "text": "...", "in_minutes": 5} — аргументы прямо рядом с именем.</summary>
    private static JsonObject ArgsFromRest(JsonObject obj)
    {
        var o = new JsonObject();
        foreach (var (k, v) in obj)
            if (k is not ("name" or "tool" or "tool_name" or "type" or "id")) o[k] = v?.DeepClone();
        return o;
    }

    /// <summary>Позиции сбалансированных {...} и [...] верхнего уровня (с учётом строк и экранирования).</summary>
    public static IEnumerable<(int Start, int Length)> JsonSpans(string text)
    {
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (c != '{' && c != '[') { i++; continue; }
            var end = MatchBracket(text, i);
            if (end < 0) { i++; continue; }
            yield return (i, end - i + 1);
            i = end + 1;
        }
    }

    private static int MatchBracket(string s, int start)
    {
        var depth = 0;
        var inStr = false;
        for (var i = start; i < s.Length; i++)
        {
            var c = s[i];
            if (inStr)
            {
                if (c == '\\') { i++; continue; }
                if (c == '"') inStr = false;
                continue;
            }
            switch (c)
            {
                case '"': inStr = true; break;
                case '{' or '[': depth++; break;
                case '}' or ']':
                    depth--;
                    if (depth == 0) return i;
                    break;
            }
        }
        return -1;
    }

    /// <summary>Убрать блоки размышлений &lt;think&gt;…&lt;/think&gt; (локальные «думающие» модели).</summary>
    public static string StripThinking(string text) => Think().Replace(text ?? "", "").TrimStart();

    [GeneratedRegex(@"<tool_call>\s*([\s\S]*?)\s*(?:</tool_call>|$)", RegexOptions.IgnoreCase)] private static partial Regex ToolCallTag();
    [GeneratedRegex(@"```(?:json|tool_call|tool)?\s*\n?([\s\S]*?)```", RegexOptions.IgnoreCase)] private static partial Regex Fence();
    [GeneratedRegex(@"\n{3,}")] private static partial Regex MultiBlank();
    [GeneratedRegex(@"<think>[\s\S]*?(?:</think>|$)", RegexOptions.IgnoreCase)] private static partial Regex Think();
}
