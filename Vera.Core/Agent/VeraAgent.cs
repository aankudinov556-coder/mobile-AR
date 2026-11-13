using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Vera.Core.Documents;
using Vera.Core.Providers;
using Vera.Core.Tools;

namespace Vera.Core.Agent;

// ============================================================================ events

public abstract record AgentEvent(string TurnId);

public sealed record TurnStarted(string TurnId, string Source, string UserText, bool HasImage, bool Hidden) : AgentEvent(TurnId);

/// <summary>Кусочек текста ответа (тег эмоции уже вырезан). Step — номер шага модели в ходе.</summary>
public sealed record TextDelta(string TurnId, int Step, string Delta) : AgentEvent(TurnId);

/// <summary>
/// Текст шага целиком заменён (например, модель написала вызов инструмента JSON-ом — его убрали из ответа).
/// UI должен заменить текст этого шага на Text.
/// </summary>
public sealed record TextReplaced(string TurnId, int Step, string Text) : AgentEvent(TurnId);

public sealed record EmotionChanged(string TurnId, string Emotion) : AgentEvent(TurnId);

/// <summary>Вызов инструмента: Running=true — начался, false — закончился (Label/IsError/Kind заполнены).</summary>
public sealed record ToolActivity(string TurnId, string CallId, string Name, bool Running, string Label, bool IsError, string? Kind) : AgentEvent(TurnId);

public sealed record TurnEnded(string TurnId, string Text, string Emotion, string Source) : AgentEvent(TurnId);

public sealed record TurnFailed(string TurnId, string Message, bool Aborted, string PartialText, string Source) : AgentEvent(TurnId);

/// <summary>Вера ещё отвечает на прошлое сообщение.</summary>
public sealed class AgentBusyException : InvalidOperationException
{
    public AgentBusyException() : base("Вера ещё отвечает на прошлое сообщение") { }
}

// ============================================================================ visible history

public sealed record ToolChip(string Id, string Name, string Label, bool IsError);

public sealed class VisibleMessage
{
    /// <summary>"user" | "assistant"</summary>
    public string Role { get; init; } = "user";
    public string Text { get; set; } = "";
    public DateTimeOffset Time { get; init; }
    public bool HasImage { get; init; }
    public List<ToolChip> Tools { get; } = new();
    public string? Emotion { get; init; }
    public string? TurnId { get; init; }
    /// <summary>Имена приложенных документов.</summary>
    public List<string> Documents { get; init; } = new();
}

// ============================================================================ tag filter

/// <summary>Вырезает тег эмоции [smug] в начале ответа при стриминге.</summary>
public sealed partial class TagFilter
{
    private static readonly Regex TagRe = TagRegex();
    private readonly Action<string> _onEmotion;
    private readonly StringBuilder _buf = new();
    private bool _head = true;

    public TagFilter(Action<string> onEmotion) => _onEmotion = onEmotion;

    public static string Strip(string? s) => TagRe.Replace(s ?? "", "").Trim();

    public string Push(string delta)
    {
        if (!_head) return Replace(delta);
        _buf.Append(delta);
        var trimmed = _buf.ToString().TrimStart();
        if (trimmed.Length == 0) return "";
        if (trimmed[0] != '[') { _head = false; return Flush(); }
        var close = trimmed.IndexOf(']');
        if (close == -1)
        {
            if (trimmed.Length > 16) { _head = false; return Flush(); }
            return "";
        }
        _head = false;
        var tag = trimmed[1..close].ToLowerInvariant();
        if (Personas.Emotions.Contains(tag))
        {
            _onEmotion(tag);
            _buf.Clear();
            _buf.Append(trimmed[(close + 1)..].TrimStart());
        }
        return Flush();
    }

    public string Flush()
    {
        var s = Replace(_buf.ToString());
        _buf.Clear();
        return s;
    }

    private string Replace(string s) => TagRe.Replace(s, m =>
    {
        _onEmotion(m.Groups[1].Value.ToLowerInvariant());
        return "";
    });

    [GeneratedRegex(@"\[(neutral|smug|happy|laugh|think|surprised|angry|shy|sad|sleepy)\]\s*", RegexOptions.IgnoreCase)]
    private static partial Regex TagRegex();
}

// ============================================================================ agent

/// <summary>Цикл агента: история, вызовы модели, инструменты, эмоции.</summary>
public sealed class VeraAgent
{
    private const int MaxSteps = 10;
    private const int MaxSaved = 300;

    private readonly SettingsService _settings;
    private readonly ProviderRegistry _providers;
    private readonly ToolRegistry _tools;
    private readonly MemoryService _memory;
    private readonly JsonFile<List<VeraMessage>> _file;
    private readonly string _platform;
    private readonly object _gate = new();
    private List<VeraMessage> _history;
    private CancellationTokenSource? _cts;

    /// <summary>Документы сессии для read_document (id → полный текст).</summary>
    private readonly ConcurrentDictionary<string, DocumentText> _docs = new();

    /// <summary>Модели, которые отвергли нативные инструменты, — для них инструменты описываются в промпте.</summary>
    private static readonly ConcurrentDictionary<string, bool> TextToolModels = new();

    public VeraAgent(SettingsService settings, ProviderRegistry providers, ToolRegistry tools, MemoryService memory, IVeraStorage storage, string platform)
    {
        _settings = settings;
        _providers = providers;
        _tools = tools;
        _memory = memory;
        _platform = platform;
        _file = new JsonFile<List<VeraMessage>>(storage, "history.json", () => new List<VeraMessage>());
        _history = _file.Data.ToList();
    }

    /// <summary>События хода. Вызываются из фонового потока — UI должен перейти в свой поток.</summary>
    public event Action<AgentEvent>? Event;

    /// <summary>Счётчик расходов (если задан — пополняется после каждого вызова модели).</summary>
    public UsageService? Usage { get; set; }

    /// <summary>Журнал запросов (пишется, если включён в настройках).</summary>
    public RequestLog? Log { get; set; }

    public bool IsBusy { get; private set; }

    private void Emit(AgentEvent e)
    {
        try { Event?.Invoke(e); } catch { /* ошибка подписчика не должна ронять ход */ }
    }

    public void Stop() => _cts?.Cancel();

    public void ClearHistory()
    {
        Stop();
        lock (_gate) _history = new List<VeraMessage>();
        _docs.Clear();
        Persist();
    }

    public IReadOnlyList<VisibleMessage> GetVisibleHistory()
    {
        List<VeraMessage> snapshot;
        lock (_gate) snapshot = _history.ToList();
        var outList = new List<VisibleMessage>();
        foreach (var m in snapshot)
        {
            if (m.Role == "user" && !m.Hidden)
            {
                outList.Add(new VisibleMessage
                {
                    Role = "user", Text = m.Text ?? "", Time = m.Ts, HasImage = m.Images is { Count: > 0 } || m.HadImages,
                    Documents = m.DocNames?.ToList() ?? new List<string>(),
                });
            }
            else if (m.Role == "assistant")
            {
                var last = outList.LastOrDefault();
                var chips = (m.ToolCalls ?? new()).Select(tc => new ToolChip(tc.Id, tc.Name, tc.Label ?? tc.Name, false)).ToList();
                if (last is { Role: "assistant" } && last.TurnId is not null && last.TurnId == m.TurnId)
                {
                    var t = TagFilter.Strip(m.Text);
                    if (t.Length > 0) last.Text = last.Text.Length > 0 ? last.Text + "\n\n" + t : t;
                    last.Tools.AddRange(chips);
                }
                else
                {
                    var vm = new VisibleMessage { Role = "assistant", Text = TagFilter.Strip(m.Text), Time = m.Ts, TurnId = m.TurnId, Emotion = m.Emotion };
                    vm.Tools.AddRange(chips);
                    outList.Add(vm);
                }
            }
            else if (m.Role == "tool")
            {
                for (var i = outList.Count - 1; i >= 0; i--)
                {
                    var idx = outList[i].Tools.FindIndex(c => c.Id == m.ToolCallId);
                    if (idx < 0) continue;
                    var c = outList[i].Tools[idx];
                    outList[i].Tools[idx] = c with { Label = m.Label ?? c.Label, IsError = m.IsError };
                    break;
                }
            }
        }
        return outList;
    }

    private static VeraMessage Light(VeraMessage m, string? note = null) => new()
    {
        Role = m.Role, Text = note is null ? m.Text : (m.Text ?? "") + note, HadImages = m.HadImages || m.Images is { Count: > 0 }, Ctx = m.Ctx,
        Ts = m.Ts, Source = m.Source, Hidden = m.Hidden, ToolCalls = m.ToolCalls, ToolCallId = m.ToolCallId, Name = m.Name,
        IsError = m.IsError, Label = m.Label, TurnId = m.TurnId, Emotion = m.Emotion, DocNames = m.DocNames,
    };

    private void Persist()
    {
        List<VeraMessage> data;
        lock (_gate)
        {
            if (_history.Count > MaxSaved * 2) _history.RemoveRange(0, _history.Count - MaxSaved);
            data = _history.Skip(Math.Max(0, _history.Count - MaxSaved))
                .Select(m => m.Images is { Count: > 0 } || m.Documents is { Count: > 0 } ? Light(m) : m).ToList();
        }
        _file.Data = data;
        _file.Save();
    }

    /// <summary>Последние N сообщений начиная с user (пары tool_use/tool_result не разрываются).</summary>
    private List<VeraMessage> ContextWindow(int? limitOverride = null)
    {
        var limit = Math.Max(6, limitOverride ?? _settings.Current.HistoryLimit);
        List<VeraMessage> h;
        lock (_gate) h = _history.ToList();
        var start = Math.Max(0, h.Count - limit);
        while (start < h.Count && h[start].Role != "user") start++;
        var slice = h.Skip(start).ToList();
        // картинки и документы оставляем только у двух последних ходов — экономия токенов
        var userIdx = slice.Select((m, i) => (m, i)).Where(x => x.m.Role == "user").Select(x => x.i).ToList();
        var keepFrom = userIdx.Count > 2 ? userIdx[^2] : 0;
        return slice.Select((m, i) =>
        {
            var heavy = m.Images is { Count: > 0 } || m.Documents is { Count: > 0 };
            if (i >= keepFrom && heavy) return m;
            if (!heavy && !m.HadImages && m.DocNames is not { Count: > 0 }) return m;
            var notes = new List<string>();
            if (m.HadImages || m.Images is { Count: > 0 }) notes.Add("[к сообщению было приложено изображение]");
            if (m.DocNames is { Count: > 0 }) notes.Add($"[были приложены документы: {string.Join(", ", m.DocNames)}]");
            var light = Light(m, " " + string.Join(" ", notes));
            light.HadImages = false;
            return light;
        }).ToList();
    }

    private static ToolDefinition ReadDocumentTool() => new()
    {
        Name = "read_document",
        Description = "Read another part of a long document the user attached (when the message says the document is longer).",
        Parameters = ToolDefinition.Schema(new JsonObject
        {
            ["id"] = ToolDefinition.Str("Document id from the <document id=...> tag."),
            ["part"] = ToolDefinition.Num("Part number, starting from 1."),
        }, "id", "part"),
    };

    /// <summary>
    /// Отправить сообщение. source: "chat" | "voice" | "screen" | "system".
    /// Ошибки приходят событием TurnFailed (и пробрасываются, если это не отмена).
    /// </summary>
    public async Task SendAsync(string text, IReadOnlyList<ImageData>? images = null, string source = "chat", bool hidden = false,
        CancellationToken ct = default, IReadOnlyList<DocumentText>? documents = null)
    {
        lock (_gate)
        {
            if (IsBusy) throw new AgentBusyException();
            IsBusy = true;
        }
        var settings = _settings.Current;
        var turnId = "t_" + Guid.NewGuid().ToString("N")[..10];
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _cts.Token;
        var fullText = new StringBuilder();
        string? emotion = null;

        try
        {
            text = (text ?? "").Trim();
            var docs = documents?.Where(d => d is not null).ToList() ?? new List<DocumentText>();
            if (text.Length == 0 && images is not { Count: > 0 } && docs.Count == 0) throw new ArgumentException("Пустое сообщение");
            var providerId = settings.Provider;
            var provider = _providers.Get(providerId);
            var model = settings.CurrentModel;

            foreach (var d in docs) _docs[d.Id] = d;
            var user = new VeraMessage
            {
                Role = "user",
                Text = text.Length > 0 ? text : docs.Count > 0 ? "Посмотри документ." : "Что на этом изображении?",
                Images = images is { Count: > 0 } ? images.ToList() : null,
                Documents = docs.Count > 0 ? docs.Select(DocumentReader.ForMessage).ToList() : null,
                DocNames = docs.Count > 0 ? docs.Select(d => d.Name).ToList() : null,
                Ctx = RuText.TimeContext(DateTimeOffset.Now),
                Source = source,
                Hidden = hidden,
            };
            lock (_gate) _history.Add(user);
            Emit(new TurnStarted(turnId, source, user.Text!, user.Images is not null, hidden));

            // system и tools фиксируются на весь ход — внутри хода история только дописывается
            var toolDefs = _tools.GetDefinitions(settings).ToList();
            if (!_docs.IsEmpty) toolDefs.Add(ReadDocumentTool());
            var toolNames = toolDefs.Select(t => t.Name).ToList();
            var system = Personas.BuildSystemPrompt(settings, _memory.ForPrompt(), toolNames, _platform);
            var textTools = TextToolModels.ContainsKey(providerId + "|" + model);
            if (textTools) system += "\n\n" + ToolPrompt.Describe(toolDefs, compact: true);
            var conv = provider.Begin(ContextWindow());
            StepRequest MakeRequest() => new()
            {
                Model = model, System = system, Tools = textTools ? Array.Empty<ToolDefinition>() : toolDefs,
                Effort = settings.Effort, WebSearch = settings.WebSearch,
            };
            var request = MakeRequest();

            void OnEmotion(string e)
            {
                emotion = e;
                Emit(new EmotionChanged(turnId, e));
            }

            for (var step = 0; step < MaxSteps; step++)
            {
                var filter = new TagFilter(OnEmotion);
                var stepText = new StringBuilder();
                var stepNo = step;
                StepResult res;
                try
                {
                    res = await provider.StepAsync(request, conv, d =>
                    {
                        var clean = filter.Push(d);
                        if (clean.Length == 0) return;
                        stepText.Append(clean);
                        Emit(new TextDelta(turnId, stepNo, clean));
                    }, token).ConfigureAwait(false);
                }
                catch (ProviderException pe) when (pe.ToolsUnsupported && !textTools && settings.ToolCallFallback && stepText.Length == 0)
                {
                    // модель не умеет нативные инструменты — описываем их в промпте и повторяем шаг
                    TextToolModels[providerId + "|" + model] = true;
                    textTools = true;
                    system += "\n\n" + ToolPrompt.Describe(toolDefs, compact: true);
                    request = MakeRequest();
                    step--;
                    continue;
                }
                catch (OperationCanceledException)
                {
                    if (stepText.Length > 0)
                    {
                        lock (_gate) _history.Add(new VeraMessage { Role = "assistant", Text = stepText + " …", TurnId = turnId, Emotion = emotion });
                        Append(fullText, stepText.ToString());
                    }
                    throw;
                }
                var tail = filter.Flush();
                if (tail.Length > 0) Emit(new TextDelta(turnId, step, tail));

                Usage?.Record(providerId, model, res.InputTokens, res.OutputTokens);
                if (settings.Diagnostics.LogRequests) Log?.Add(providerId, model, step, res.Raw ?? res.Text);

                var calls = res.ToolCalls;
                var clean = TagFilter.Strip(ToolCallParser.StripThinking(res.Text));
                var fromText = false;
                if (calls.Count == 0 && res.Stop != "refusal" && settings.ToolCallFallback &&
                    ToolCallParser.TryExtract(clean, toolNames, out var found, out var cleaned, strict: !textTools))
                {
                    // модель написала вызов инструмента текстом — выполняем его и убираем JSON из ответа
                    fromText = true;
                    var prefix = "txt_" + Guid.NewGuid().ToString("N")[..8] + "_";
                    calls = found.Select((c, i) => new ToolCall { Id = prefix + i, Name = c.Name, Args = c.Args }).ToList();
                    clean = TagFilter.Strip(cleaned);
                    Emit(new TextReplaced(turnId, step, clean));
                }

                var asst = new VeraMessage { Role = "assistant", Text = clean, TurnId = turnId, Emotion = emotion };
                if (calls.Count > 0) asst.ToolCalls = calls.Select(tc => new ToolCall { Id = tc.Id, Name = tc.Name, Args = tc.Args }).ToList();
                lock (_gate) _history.Add(asst);
                Append(fullText, clean);

                if (res.Stop == "pause") continue;
                if (calls.Count == 0) break;

                var results = new List<ToolExecution>();
                foreach (var tc in calls)
                {
                    if (token.IsCancellationRequested)
                    {
                        results.Add(new ToolExecution(tc.Id, tc.Name, ToolResult.Error("Отменено пользователем.", "⛔ " + tc.Name)));
                        continue;
                    }
                    Emit(new ToolActivity(turnId, tc.Id, tc.Name, true, tc.Name, false, null));
                    var r = tc.Name == "read_document"
                        ? ReadDocument(tc.Args)
                        : await _tools.ExecuteAsync(tc.Name, tc.Args, token).ConfigureAwait(false);
                    results.Add(new ToolExecution(tc.Id, tc.Name, r));
                    var call = asst.ToolCalls!.First(x => x.Id == tc.Id);
                    call.Label = r.Label;
                    Emit(new ToolActivity(turnId, tc.Id, tc.Name, false, r.Label ?? tc.Name, r.IsError, r.Kind));
                }
                lock (_gate)
                {
                    foreach (var r in results)
                    {
                        _history.Add(new VeraMessage
                        {
                            Role = "tool", ToolCallId = r.CallId, Name = r.Name, Text = r.Result.Text, Images = r.Result.Images?.ToList(),
                            IsError = r.Result.IsError, Label = r.Result.Label, TurnId = turnId,
                        });
                    }
                }
                if (fromText)
                {
                    // в «родной» переписке вызова не было — результаты передаём сообщением пользователя
                    var imgs = results.SelectMany(r => r.Result.Images ?? Array.Empty<ImageData>()).ToList();
                    provider.AppendUserText(conv, ToolPrompt.Results(results.Select(r => (r.Name, r.Result))), imgs.Count > 0 ? imgs : null);
                }
                else provider.AppendToolResults(conv, results);
                token.ThrowIfCancellationRequested();
                if (step == MaxSteps - 1) Append(fullText, "(Слишком много шагов подряд — остановилась.)");
            }

            Emit(new TurnEnded(turnId, fullText.ToString(), emotion ?? "neutral", source));
        }
        catch (OperationCanceledException)
        {
            Emit(new TurnFailed(turnId, "Прервано", true, fullText.ToString(), source));
        }
        catch (Exception e)
        {
            Emit(new TurnFailed(turnId, e.Message, false, fullText.ToString(), source));
            throw;
        }
        finally
        {
            lock (_gate) IsBusy = false;
            _cts?.Dispose();
            _cts = null;
            Persist();
        }
    }

    private ToolResult ReadDocument(JsonObject args)
    {
        var id = args.Str("id") ?? "";
        if (!_docs.TryGetValue(id, out var doc))
        {
            doc = _docs.Values.FirstOrDefault(d => d.Name.Contains(id, StringComparison.OrdinalIgnoreCase));
            if (doc is null) return ToolResult.Error($"Документ «{id}» не найден (документы живут только до перезапуска).", "📄 не найден");
        }
        var (text, part, parts) = DocumentReader.GetPart(doc.Text, (int)(args.Num("part") ?? 1));
        return ToolResult.Ok($"Документ «{doc.Name}», часть {part} из {parts}:\n\n{text}", $"📄 {doc.Name} · {part}/{parts}");
    }

    private static void Append(StringBuilder sb, string s)
    {
        if (string.IsNullOrEmpty(s)) return;
        if (sb.Length > 0) sb.Append("\n\n");
        sb.Append(s);
    }

    public void Flush() => _file.SaveNow();
}
