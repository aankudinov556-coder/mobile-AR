using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta.Messages;

namespace Vera.Core.Providers;

/// <summary>
/// Anthropic (Claude) через официальный SDK. Переписка хранится как JSON сообщений Messages API
/// и превращается в типизированные параметры SDK перед каждым вызовом.
/// Thinking-блоки прошлых ходов не воспроизводятся (снятие «ведущей» серии допустимо);
/// внутри хода ответы модели дописываются как есть — история только растёт.
/// </summary>
public sealed partial class AnthropicProvider : ILlmProvider
{
    private static readonly Regex Modern = ModernRegex();
    private static readonly Regex FallbackModels = FallbackRegex();
    private static readonly Regex WebSearchNew = WebSearchRegex();

    private readonly Func<string, string> _getKey;

    public AnthropicProvider(Func<string, string> getKey) => _getKey = getKey;

    public string Id => "anthropic";
    public string Title => "Anthropic";

    private sealed class Conv : ProviderConversation
    {
        public List<JsonObject> Messages { get; } = new();
    }

    private AnthropicClient Client()
    {
        var key = _getKey("anthropic");
        if (string.IsNullOrWhiteSpace(key)) throw new ProviderException("Не задан API-ключ Anthropic. Открой настройки.");
        // VERA_ANTHROPIC_BASE_URL — для тестов с мок-сервером
        if (Environment.GetEnvironmentVariable("VERA_ANTHROPIC_BASE_URL") is { Length: > 0 } baseUrl)
            return new AnthropicClient { ApiKey = key, MaxRetries = 2, BaseUrl = baseUrl };
        return new AnthropicClient { ApiKey = key, MaxRetries = 2 };
    }

    // ------------------------------------------------------------------ history → JSON
    public ProviderConversation Begin(IReadOnlyList<VeraMessage> history)
    {
        var conv = new Conv();
        conv.Messages.AddRange(ToJson(history));
        return conv;
    }

    private static JsonObject ImageBlock(ImageData im) => new()
    {
        ["type"] = "image",
        ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = im.Mime, ["data"] = im.Base64 },
    };

    private static JsonObject TextBlock(string text) => new() { ["type"] = "text", ["text"] = text };

    internal static List<JsonObject> ToJson(IEnumerable<VeraMessage> history)
    {
        var outList = new List<JsonObject>();
        var toolBuf = new JsonArray();
        void FlushTools()
        {
            if (toolBuf.Count == 0) return;
            outList.Add(new JsonObject { ["role"] = "user", ["content"] = toolBuf });
            toolBuf = new JsonArray();
        }

        foreach (var m in history)
        {
            if (m.Role != "tool") FlushTools();
            switch (m.Role)
            {
                case "user":
                {
                    var content = new JsonArray();
                    foreach (var im in m.Images ?? new()) content.Add(ImageBlock(im));
                    if (m.Ctx is not null) content.Add(TextBlock($"<context>{m.Ctx}</context>"));
                    foreach (var d in m.Documents ?? new())
                        content.Add(TextBlock($"<document id=\"{d.Id}\" name=\"{d.Name}\">\n{d.Text}\n</document>"));
                    content.Add(TextBlock(string.IsNullOrEmpty(m.Text) ? "…" : m.Text));
                    outList.Add(new JsonObject { ["role"] = "user", ["content"] = content });
                    break;
                }
                case "assistant":
                {
                    var content = new JsonArray();
                    if (!string.IsNullOrEmpty(m.Text)) content.Add(TextBlock(m.Text));
                    foreach (var tc in m.ToolCalls ?? new())
                        content.Add(new JsonObject { ["type"] = "tool_use", ["id"] = tc.Id, ["name"] = tc.Name, ["input"] = tc.Args.DeepClone() });
                    if (content.Count == 0) content.Add(TextBlock("…"));
                    outList.Add(new JsonObject { ["role"] = "assistant", ["content"] = content });
                    break;
                }
                case "tool":
                {
                    var content = new JsonArray { TextBlock(string.IsNullOrEmpty(m.Text) ? "(пусто)" : m.Text) };
                    foreach (var im in m.Images ?? new()) content.Add(ImageBlock(im));
                    var block = new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = m.ToolCallId ?? "", ["content"] = content };
                    if (m.IsError) block["is_error"] = true;
                    toolBuf.Add(block);
                    break;
                }
            }
        }
        FlushTools();
        return outList;
    }

    public void AppendUserText(ProviderConversation conv, string text, IReadOnlyList<ImageData>? images = null) =>
        ((Conv)conv).Messages.AddRange(ToJson(new[] { new VeraMessage { Role = "user", Text = text, Images = images?.ToList() } }));

    public void AppendToolResults(ProviderConversation conv, IReadOnlyList<ToolExecution> results)
    {
        ((Conv)conv).Messages.AddRange(ToJson(results.Select(r => new VeraMessage
        {
            Role = "tool", ToolCallId = r.CallId, Name = r.Name, Text = r.Result.Text,
            Images = r.Result.Images?.ToList(), IsError = r.Result.IsError,
        })));
    }

    // ------------------------------------------------------------------ step
    public async Task<StepResult> StepAsync(StepRequest req, ProviderConversation conv, Action<string> onText, CancellationToken ct)
    {
        var c = (Conv)conv;
        var client = Client();
        var modern = Modern.IsMatch(req.Model);

        var toolsJson = new JsonArray();
        foreach (var t in req.Tools)
        {
            toolsJson.Add(new JsonObject
            {
                ["name"] = t.Name,
                ["description"] = t.Description,
                ["input_schema"] = t.Parameters.DeepClone(),
                ["eager_input_streaming"] = true,
            });
        }
        if (req.WebSearch)
        {
            toolsJson.Add(new JsonObject
            {
                ["type"] = WebSearchNew.IsMatch(req.Model) ? "web_search_20260209" : "web_search_20250305",
                ["name"] = "web_search",
                ["max_uses"] = 5,
            });
        }

        var messagesArray = new JsonArray(c.Messages.Select(m => (JsonNode)m.DeepClone()).ToArray());
        var p = new MessageCreateParams
        {
            Model = req.Model,
            MaxTokens = modern || req.Model.Contains("-4-") || req.Model.Contains("-4.") ? 32000 : 8192,
            System = req.System,
            Messages = JsonSerializer.Deserialize<List<BetaMessageParam>>(messagesArray.ToJsonString())!,
        };
        if (toolsJson.Count > 0) p = p with { Tools = JsonSerializer.Deserialize<List<BetaToolUnion>>(toolsJson.ToJsonString())! };
        if (modern)
        {
            p = p with
            {
                Thinking = new BetaThinkingConfigAdaptive(),
                OutputConfig = new BetaOutputConfig
                {
                    Effort = req.Effort switch { "high" => Effort.High, "medium" => Effort.Medium, _ => Effort.Low },
                },
            };
        }
        if (FallbackModels.IsMatch(req.Model))
        {
            // при отказе классификатора API само повторит ход на запасной модели
            p = p with { Betas = ["server-side-fallback-2026-07-01"], Fallbacks = new Default() };
        }

        for (var attempt = 0; ; attempt++)
        {
            var acc = new StreamAccumulator(onText);
            try
            {
                await foreach (var ev in client.Beta.Messages.CreateStreaming(p, ct).WithCancellation(ct))
                    acc.OnEvent(JsonSerializer.SerializeToNode(ev) as JsonObject);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) when (IsApiError(e))
            {
                throw ProviderException.From(Title, StatusOf(e), ApiMessage(e), e);
            }
            catch (HttpRequestException e)
            {
                throw ProviderException.From(Title, null, e.Message, e);
            }

            if (acc.BrokenToolJson && attempt < 2)
            {
                // tool input не распарсился (eager input streaming) — переспрашиваем ход
                if (acc.Text.Length > 0) onText("\n");
                continue;
            }

            var content = acc.ParamContent();
            var toolUses = acc.Blocks.Where(b => b["type"]?.GetValue<string>() == "tool_use").ToList();
            var stop = acc.StopReason;

            if (stop == "max_tokens" && toolUses.Count > 0)
                throw new ProviderException("Ответ модели обрезан (max_tokens) посреди вызова инструмента. Попробуй ещё раз.");

            c.Messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = content });

            if (stop == "refusal")
                return new StepResult { Text = acc.Text.Length > 0 ? acc.Text.ToString() : "Не могу с этим помочь.", Stop = "refusal", InputTokens = acc.InputTokens, OutputTokens = acc.OutputTokens };
            if (stop == "pause_turn")
                return new StepResult { Text = acc.Text.ToString(), Stop = "pause", InputTokens = acc.InputTokens, OutputTokens = acc.OutputTokens };

            var calls = toolUses.Select(b => new ToolCall
            {
                Id = b["id"]?.GetValue<string>() ?? "",
                Name = b["name"]?.GetValue<string>() ?? "",
                Args = b["input"] as JsonObject ?? new JsonObject(),
            }).ToList();
            return new StepResult
            {
                Text = acc.Text.ToString(),
                ToolCalls = calls,
                Stop = calls.Count > 0 ? "tool_use" : stop == "max_tokens" ? "max_tokens" : "end",
                InputTokens = acc.InputTokens,
                OutputTokens = acc.OutputTokens,
                Raw = $"stop={stop}; blocks={string.Join(",", acc.Blocks.Select(b => b["type"]?.GetValue<string>()))}; text={acc.Text}",
            };
        }
    }

    private static bool IsApiError(Exception e) => e is AnthropicException;

    private static int? StatusOf(Exception e)
    {
        switch (e)
        {
            case AnthropicUnauthorizedException: return 401;
            case AnthropicRateLimitException: return 429;
            case AnthropicNotFoundException: return 404;
            case AnthropicBadRequestException: return 400;
            case AnthropicServiceUnavailableException: return 503;
            case Anthropic5xxException: return 500;
        }
        var prop = e.GetType().GetProperty("StatusCode");
        if (prop?.GetValue(e) is { } v)
        {
            try { return Convert.ToInt32(v); } catch { /* ignore */ }
        }
        return null;
    }

    private static string ApiMessage(Exception e) =>
        e is AnthropicServiceException se && !string.IsNullOrEmpty(se.ApiMessage) ? se.ApiMessage : e.Message;

    // ------------------------------------------------------------------ models / test
    public async Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken ct = default)
    {
        try
        {
            var page = await Client().Models.List(cancellationToken: ct);
            var list = new List<ModelInfo>();
            foreach (var m in page.Items) list.Add(new ModelInfo(m.ID, m.DisplayName ?? m.ID, true, true));
            return list;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) when (IsApiError(e))
        {
            throw ProviderException.From(Title, StatusOf(e), ApiMessage(e), e);
        }
    }

    public async Task<string> TestAsync(CancellationToken ct = default)
    {
        var models = await ListModelsAsync(ct);
        return $"Ключ работает ({models.Count} моделей)";
    }

    [GeneratedRegex(@"claude-(fable|mythos)-5|claude-(opus|sonnet|haiku)-5|claude-opus-4-[6-9]|claude-sonnet-4-6")]
    private static partial Regex ModernRegex();

    [GeneratedRegex(@"^claude-(fable-5-2|fable-5-1|opus-5-5|opus-5|sonnet-5-5)$")]
    private static partial Regex FallbackRegex();

    [GeneratedRegex(@"claude-(fable|mythos)-5|claude-(opus|sonnet)-5|claude-opus-4-[6-9]|claude-sonnet-4-6")]
    private static partial Regex WebSearchRegex();
}

/// <summary>Собирает ответ из событий стрима Messages API (в виде JSON).</summary>
internal sealed class StreamAccumulator
{
    private readonly Action<string> _onText;
    private readonly Dictionary<int, JsonObject> _byIndex = new();
    private readonly Dictionary<int, StringBuilder> _partialJson = new();

    public StreamAccumulator(Action<string> onText) => _onText = onText;

    public List<JsonObject> Blocks { get; } = new();
    public StringBuilder Text { get; } = new();
    public string? StopReason { get; private set; }
    public int? InputTokens { get; private set; }
    public int? OutputTokens { get; private set; }

    private static int? Int(JsonNode? n) => n is JsonValue v && v.GetValueKind() == JsonValueKind.Number ? v.GetValue<int>() : null;
    public bool BrokenToolJson { get; private set; }

    public void OnEvent(JsonObject? ev)
    {
        if (ev is null) return;
        switch (ev["type"]?.GetValue<string>())
        {
            case "message_start":
            {
                var u = ev["message"]?["usage"];
                var input = Int(u?["input_tokens"]);
                if (input is not null) InputTokens = input + (Int(u?["cache_creation_input_tokens"]) ?? 0) + (Int(u?["cache_read_input_tokens"]) ?? 0);
                OutputTokens = Int(u?["output_tokens"]) ?? OutputTokens;
                break;
            }
            case "content_block_start":
            {
                var idx = ev["index"]?.GetValue<int>() ?? Blocks.Count;
                var block = (ev["content_block"]?.DeepClone() as JsonObject) ?? new JsonObject();
                _byIndex[idx] = block;
                Blocks.Add(block);
                break;
            }
            case "content_block_delta":
            {
                var idx = ev["index"]?.GetValue<int>() ?? 0;
                if (!_byIndex.TryGetValue(idx, out var block)) break;
                var delta = ev["delta"] as JsonObject;
                switch (delta?["type"]?.GetValue<string>())
                {
                    case "text_delta":
                    {
                        var t = delta["text"]?.GetValue<string>() ?? "";
                        block["text"] = (block["text"]?.GetValue<string>() ?? "") + t;
                        Text.Append(t);
                        _onText(t);
                        break;
                    }
                    case "input_json_delta":
                        if (!_partialJson.TryGetValue(idx, out var sb)) _partialJson[idx] = sb = new StringBuilder();
                        sb.Append(delta["partial_json"]?.GetValue<string>());
                        break;
                    case "thinking_delta":
                        block["thinking"] = (block["thinking"]?.GetValue<string>() ?? "") + (delta["thinking"]?.GetValue<string>() ?? "");
                        break;
                    case "signature_delta":
                        block["signature"] = delta["signature"]?.GetValue<string>();
                        break;
                    case "citations_delta":
                        if (block["citations"] is not JsonArray arr) block["citations"] = arr = new JsonArray();
                        arr.Add(delta["citation"]?.DeepClone());
                        break;
                }
                break;
            }
            case "content_block_stop":
            {
                var idx = ev["index"]?.GetValue<int>() ?? 0;
                if (_byIndex.TryGetValue(idx, out var block) && _partialJson.TryGetValue(idx, out var sb))
                {
                    var s = sb.ToString();
                    if (string.IsNullOrWhiteSpace(s)) block["input"] = new JsonObject();
                    else
                    {
                        try { block["input"] = JsonNode.Parse(s) as JsonObject ?? new JsonObject(); }
                        catch (JsonException) { BrokenToolJson = true; }
                    }
                }
                break;
            }
            case "message_delta":
                StopReason = ev["delta"]?["stop_reason"]?.GetValue<string>() ?? StopReason;
                OutputTokens = Int(ev["usage"]?["output_tokens"]) ?? OutputTokens;
                break;
        }
    }

    /// <summary>Блоки ответа в форме параметров запроса (для дописывания в историю хода).</summary>
    public JsonArray ParamContent()
    {
        var arr = new JsonArray();
        foreach (var b in Blocks)
        {
            var type = b["type"]?.GetValue<string>();
            JsonObject? p = type switch
            {
                "text" => WithCitations(new JsonObject { ["type"] = "text", ["text"] = b["text"]?.GetValue<string>() ?? "" }, b),
                "thinking" => new JsonObject { ["type"] = "thinking", ["thinking"] = b["thinking"]?.GetValue<string>() ?? "", ["signature"] = b["signature"]?.GetValue<string>() ?? "" },
                "redacted_thinking" => new JsonObject { ["type"] = "redacted_thinking", ["data"] = b["data"]?.DeepClone() },
                "tool_use" or "server_tool_use" => new JsonObject { ["type"] = type, ["id"] = b["id"]?.DeepClone(), ["name"] = b["name"]?.DeepClone(), ["input"] = b["input"]?.DeepClone() ?? new JsonObject() },
                _ => b.DeepClone() as JsonObject, // результаты серверных инструментов, fallback-блоки — как есть
            };
            if (p is null) continue;
            if (type == "text" && string.IsNullOrEmpty(p["text"]?.GetValue<string>())) continue;
            arr.Add(p);
        }
        if (arr.Count == 0) arr.Add(new JsonObject { ["type"] = "text", ["text"] = "…" });
        return arr;
    }

    private static JsonObject WithCitations(JsonObject p, JsonObject src)
    {
        if (src["citations"] is JsonArray { Count: > 0 } c) p["citations"] = c.DeepClone();
        return p;
    }
}
