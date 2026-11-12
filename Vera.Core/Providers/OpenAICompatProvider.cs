using System.ClientModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenAI;
using OpenAI.Chat;
using OpenAI.Models;

namespace Vera.Core.Providers;

/// <summary>OpenAI и OpenRouter (OpenAI-совместимый API) через официальный SDK OpenAI.</summary>
public sealed class OpenAICompatProvider : ILlmProvider
{
    public const string OpenRouterBase = "https://openrouter.ai/api/v1";
    private readonly Func<string, string> _getKey;
    private readonly HttpClient _http;

    public OpenAICompatProvider(string id, Func<string, string> getKey, HttpClient http)
    {
        Id = id;
        _getKey = getKey;
        _http = http;
    }

    public string Id { get; }
    public string Title => Id == "openai" ? "OpenAI" : "OpenRouter";

    private sealed class Conv : ProviderConversation
    {
        public List<ChatMessage> Messages { get; } = new();
    }

    private OpenAIClientOptions Options()
    {
        var o = new OpenAIClientOptions();
        if (Id == "openrouter") o.Endpoint = new Uri(OpenRouterBase);
        else if (Environment.GetEnvironmentVariable("VERA_OPENAI_BASE_URL") is { Length: > 0 } custom) o.Endpoint = new Uri(custom);
        return o;
    }

    private ApiKeyCredential Credential()
    {
        var key = _getKey(Id);
        if (string.IsNullOrWhiteSpace(key)) throw new ProviderException($"Не задан API-ключ {Title}. Открой настройки.");
        return new ApiKeyCredential(key);
    }

    // ------------------------------------------------------------------ history
    public ProviderConversation Begin(IReadOnlyList<VeraMessage> history)
    {
        var conv = new Conv();
        Append(conv.Messages, history);
        return conv;
    }

    private static ChatMessageContentPart ImagePart(ImageData im) =>
        ChatMessageContentPart.CreateImagePart(BinaryData.FromBytes(Convert.FromBase64String(im.Base64)), im.Mime);

    internal static void Append(List<ChatMessage> outList, IEnumerable<VeraMessage> history)
    {
        var pendingImages = new List<ImageData>();
        void Flush()
        {
            if (pendingImages.Count == 0) return;
            var parts = new List<ChatMessageContentPart> { ChatMessageContentPart.CreateTextPart("(Изображения, полученные инструментами выше.)") };
            parts.AddRange(pendingImages.Select(ImagePart));
            outList.Add(new UserChatMessage(parts));
            pendingImages.Clear();
        }

        foreach (var m in history)
        {
            if (m.Role != "tool") Flush();
            switch (m.Role)
            {
                case "user":
                {
                    var text = (m.Ctx is null ? "" : $"<context>{m.Ctx}</context>\n") + (m.Text ?? "");
                    if (m.Images is { Count: > 0 })
                    {
                        var parts = new List<ChatMessageContentPart> { ChatMessageContentPart.CreateTextPart(text) };
                        parts.AddRange(m.Images.Select(ImagePart));
                        outList.Add(new UserChatMessage(parts));
                    }
                    else outList.Add(new UserChatMessage(text));
                    break;
                }
                case "assistant":
                {
                    if (m.ToolCalls is { Count: > 0 })
                    {
                        var calls = m.ToolCalls.Select(tc => ChatToolCall.CreateFunctionToolCall(tc.Id, tc.Name,
                            BinaryData.FromString(tc.Args.ToJsonString()))).ToList();
                        var a = new AssistantChatMessage(calls);
                        if (!string.IsNullOrEmpty(m.Text)) a.Content.Add(ChatMessageContentPart.CreateTextPart(m.Text));
                        outList.Add(a);
                    }
                    else outList.Add(new AssistantChatMessage(string.IsNullOrEmpty(m.Text) ? "…" : m.Text));
                    break;
                }
                case "tool":
                    outList.Add(new ToolChatMessage(m.ToolCallId ?? "", string.IsNullOrEmpty(m.Text) ? "(пусто)" : m.Text));
                    if (m.Images is { Count: > 0 }) pendingImages.AddRange(m.Images);
                    break;
            }
        }
        Flush();
    }

    public void AppendToolResults(ProviderConversation conv, IReadOnlyList<ToolExecution> results)
    {
        Append(((Conv)conv).Messages, results.Select(r => new VeraMessage
        {
            Role = "tool", ToolCallId = r.CallId, Name = r.Name, Text = r.Result.Text, Images = r.Result.Images?.ToList(),
        }));
    }

    private static bool IsReasoningModel(string model)
    {
        var m = model.StartsWith("openai/") ? model[7..] : model;
        return m.StartsWith("gpt-5") || (m.Length > 1 && m[0] == 'o' && char.IsDigit(m[1]));
    }

    // ------------------------------------------------------------------ step
    public async Task<StepResult> StepAsync(StepRequest req, ProviderConversation conv, Action<string> onText, CancellationToken ct)
    {
        var c = (Conv)conv;
        var model = req.Model;
        if (Id == "openrouter" && req.WebSearch && !model.EndsWith(":online")) model += ":online";
        var client = new ChatClient(model, Credential(), Options());

        var options = new ChatCompletionOptions();
        if (Id == "openai") options.MaxOutputTokenCount = 16000;
        if (Id == "openai" && IsReasoningModel(model))
        {
            options.ReasoningEffortLevel = req.Effort switch
            {
                "high" => ChatReasoningEffortLevel.High,
                "medium" => ChatReasoningEffortLevel.Medium,
                _ => ChatReasoningEffortLevel.Low,
            };
        }
        foreach (var t in req.Tools)
            options.Tools.Add(ChatTool.CreateFunctionTool(t.Name, t.Description, BinaryData.FromString(t.Parameters.ToJsonString())));

        var messages = new List<ChatMessage> { new SystemChatMessage(req.System) };
        messages.AddRange(c.Messages);

        var text = new StringBuilder();
        var calls = new SortedDictionary<int, (string Id, string Name, StringBuilder Args)>();
        ChatFinishReason? finish = null;
        try
        {
            await foreach (var u in client.CompleteChatStreamingAsync(messages, options, ct).WithCancellation(ct))
            {
                foreach (var part in u.ContentUpdate)
                {
                    if (string.IsNullOrEmpty(part.Text)) continue;
                    text.Append(part.Text);
                    onText(part.Text);
                }
                foreach (var tc in u.ToolCallUpdates)
                {
                    if (!calls.TryGetValue(tc.Index, out var acc)) acc = ("", "", new StringBuilder());
                    if (!string.IsNullOrEmpty(tc.ToolCallId)) acc.Id = tc.ToolCallId;
                    if (!string.IsNullOrEmpty(tc.FunctionName) && acc.Name.Length == 0) acc.Name = tc.FunctionName;
                    if (tc.FunctionArgumentsUpdate is { } upd && !upd.ToMemory().IsEmpty) acc.Args.Append(upd.ToString());
                    calls[tc.Index] = acc;
                }
                if (u.FinishReason is { } fr) finish = fr;
            }
        }
        catch (ClientResultException e)
        {
            throw ProviderException.From(Title, e.Status, e.Message, e);
        }
        catch (OperationCanceledException) { throw; }
        catch (HttpRequestException e)
        {
            throw ProviderException.From(Title, null, e.Message, e);
        }

        var toolCalls = calls.Values.Where(a => a.Name.Length > 0).Select((a, i) => new ToolCall
        {
            Id = a.Id.Length > 0 ? a.Id : $"call_{DateTime.UtcNow.Ticks:x}_{i}",
            Name = a.Name,
            Args = ParseArgs(a.Args.ToString()),
        }).ToList();

        // ответ модели — в переписку хода
        Append(c.Messages, new[] { new VeraMessage { Role = "assistant", Text = text.ToString(), ToolCalls = toolCalls.Count > 0 ? toolCalls : null } });

        return new StepResult
        {
            Text = text.ToString(),
            ToolCalls = toolCalls,
            Stop = finish == ChatFinishReason.Length ? "max_tokens" : toolCalls.Count > 0 ? "tool_use" : "end",
        };
    }

    internal static JsonObject ParseArgs(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return new JsonObject();
        try
        {
            return JsonNode.Parse(s) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            return new JsonObject { ["__invalid_json"] = s.Length > 500 ? s[..500] : s };
        }
    }

    // ------------------------------------------------------------------ models / test
    public async Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken ct = default)
    {
        if (Id == "openrouter")
        {
            using var res = await _http.GetAsync(OpenRouterBase + "/models", ct);
            if (!res.IsSuccessStatusCode) throw ProviderException.From(Title, (int)res.StatusCode, await res.Content.ReadAsStringAsync(ct));
            var json = JsonNode.Parse(await res.Content.ReadAsStringAsync(ct));
            var list = new List<ModelInfo>();
            foreach (var m in json?["data"]?.AsArray() ?? new JsonArray())
            {
                if (m is null) continue;
                var id = m["id"]?.GetValue<string>() ?? "";
                var supported = m["supported_parameters"]?.AsArray().Select(x => x?.GetValue<string>()).ToList() ?? new();
                var inputs = m["architecture"]?["input_modalities"]?.AsArray().Select(x => x?.GetValue<string>()).ToList() ?? new();
                list.Add(new ModelInfo(id, m["name"]?.GetValue<string>() ?? id, supported.Contains("tools"), inputs.Contains("image")));
            }
            return list.OrderBy(m => m.Id).ToList();
        }
        try
        {
            var client = new OpenAIModelClient(Credential(), Options());
            var models = await client.GetModelsAsync(ct);
            string[] skip = { "audio", "realtime", "transcribe", "tts", "image", "search", "embedding", "instruct", "moderation", "dall-e", "whisper" };
            return models.Value.Select(m => m.Id)
                .Where(id => (id.StartsWith("gpt") || id.StartsWith("chatgpt") || (id.Length > 1 && id[0] == 'o' && char.IsDigit(id[1])))
                             && !skip.Any(id.Contains))
                .OrderBy(id => id)
                .Select(id => new ModelInfo(id, id, true, true))
                .ToList();
        }
        catch (ClientResultException e)
        {
            throw ProviderException.From(Title, e.Status, e.Message, e);
        }
    }

    public async Task<string> TestAsync(CancellationToken ct = default)
    {
        if (Id == "openrouter")
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, OpenRouterBase + "/key");
            var key = _getKey(Id);
            if (string.IsNullOrWhiteSpace(key)) throw new ProviderException("Ключ не задан");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
            using var res = await _http.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode) throw ProviderException.From(Title, (int)res.StatusCode, await res.Content.ReadAsStringAsync(ct));
            var j = JsonNode.Parse(await res.Content.ReadAsStringAsync(ct));
            var left = j?["data"]?["limit_remaining"];
            return "Ключ работает" + (left is not null ? $", остаток лимита: {left}" : "");
        }
        var models = await ListModelsAsync(ct);
        return $"Ключ работает ({models.Count} моделей)";
    }
}
