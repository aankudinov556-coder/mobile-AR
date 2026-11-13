using System.Text;
using Vera.Core;
using Vera.Core.Agent;
using Vera.Core.Providers;

namespace Vera.LocalAI.Llm;

/// <summary>
/// Провайдер "local": модель крутится прямо на ПК (llama.cpp). Инструменты описываются в системном промпте,
/// модель вызывает их блоками &lt;tool_call&gt;, провайдер превращает их в обычные вызовы для агента.
/// При ошибке может переспросить облачного провайдера (гибридный режим, LocalModelSettings.CloudFallback).
/// </summary>
public sealed class LocalLlmProvider : ILlmProvider, IDisposable
{
    public const string ProviderId = "local";
    /// <summary>Сколько токенов оставить на ответ при подгонке истории под контекст.</summary>
    private const int ReplyReserve = 1024;
    private static readonly string[] StopStrings = { "<tool_response>", "<|im_end|>", "<|im_start|>", "<end_of_turn>", "<|eot_id|>" };

    private readonly ILocalEngine _engine;
    private readonly ModelStore _store;
    private readonly Func<VeraSettings> _settings;
    private readonly Func<string, ILlmProvider?>? _providers;
    private readonly Timer _idle;

    /// <param name="engine">движок (на ПК — <see cref="LlamaEngine"/>)</param>
    /// <param name="store">папка моделей</param>
    /// <param name="settings">текущие настройки</param>
    /// <param name="providers">поиск облачного провайдера для гибридного режима</param>
    public LocalLlmProvider(ILocalEngine engine, ModelStore store, Func<VeraSettings> settings, Func<string, ILlmProvider?>? providers = null)
    {
        _engine = engine;
        _store = store;
        _settings = settings;
        _providers = providers;
        _idle = new Timer(_ => UnloadIfIdle());
    }

    public string Id => ProviderId;
    public string Title => "Встроенная модель";

    /// <summary>Что сейчас происходит: «Загружаю модель… 40%», «Отвечает облако…»; null — ничего.</summary>
    public event Action<string?>? Status;

    /// <summary>Последняя скорость генерации, токенов в секунду.</summary>
    public double LastTokensPerSecond { get; private set; }

    public ILocalEngine Engine => _engine;

    private sealed class Conv : ProviderConversation
    {
        public List<VeraMessage> History { get; } = new();
    }

    public ProviderConversation Begin(IReadOnlyList<VeraMessage> history)
    {
        var c = new Conv();
        c.History.AddRange(history);
        return c;
    }

    public void AppendToolResults(ProviderConversation conv, IReadOnlyList<ToolExecution> results) =>
        ((Conv)conv).History.AddRange(results.Select(r => new VeraMessage
        {
            Role = "tool", ToolCallId = r.CallId, Name = r.Name, Text = r.Result.Text, IsError = r.Result.IsError,
            Images = r.Result.Images?.ToList(),
        }));

    public void AppendUserText(ProviderConversation conv, string text, IReadOnlyList<ImageData>? images = null) =>
        ((Conv)conv).History.Add(new VeraMessage { Role = "user", Text = text, Images = images?.ToList() });

    /// <summary>Путь к GGUF: из выбранной модели, из Local.ModelPath или первая скачанная.</summary>
    public string? ResolveModelPath(string? model)
    {
        if (!string.IsNullOrWhiteSpace(model))
        {
            if (File.Exists(model)) return model;
            if (ModelCatalog.Get(model) is { Kind: AssetKind.Llm } a && _store.IsInstalled(a)) return _store.PathOf(a);
            var inDir = Path.Combine(_store.LlmDir, model);
            if (File.Exists(inDir)) return inDir;
        }
        var s = _settings().Local.ModelPath;
        if (!string.IsNullOrWhiteSpace(s) && File.Exists(s)) return s;
        return _store.InstalledLlms().FirstOrDefault()?.FullName;
    }

    private EngineLoadOptions LoadOptions(string path)
    {
        var l = _settings().Local;
        return new EngineLoadOptions(path, l.ContextSize, l.GpuLayers, l.Backend);
    }

    private async Task EnsureLoaded(string? model, CancellationToken ct)
    {
        var path = ResolveModelPath(model) ?? throw new ProviderException(
            "Встроенная модель не выбрана: открой «Настройки → Модель» и скачай одну из рекомендованных (или укажи свой .gguf-файл).");
        var opts = LoadOptions(path);
        if (_engine.Loaded is { } cur && cur with { Backend = opts.Backend } == opts) return; // ускоритель меняется только после перезапуска
        var name = Path.GetFileNameWithoutExtension(path);
        Status?.Invoke($"Загружаю модель {name}…");
        var progress = new Progress<float>(f => Status?.Invoke($"Загружаю модель {name}… {f * 100:0}%"));
        try
        {
            await _engine.EnsureLoadedAsync(opts, progress, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException and not ProviderException)
        {
            throw new ProviderException(e.Message, null, e);
        }
        finally { Status?.Invoke(null); }
    }

    public async Task<StepResult> StepAsync(StepRequest request, ProviderConversation conv, Action<string> onText, CancellationToken ct)
    {
        var c = (Conv)conv;
        _idle.Change(Timeout.Infinite, Timeout.Infinite);
        try
        {
            return await LocalStep(request, c, onText, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException && CanFallback(out var fb, out var model))
        {
            Status?.Invoke("Встроенная модель не справилась — отвечает " + fb.Title + "…");
            try
            {
                var res = await fb.StepAsync(new StepRequest
                {
                    Model = model, System = request.System, Tools = request.Tools, Effort = request.Effort, WebSearch = request.WebSearch,
                }, fb.Begin(c.History), onText, ct).ConfigureAwait(false);
                c.History.Add(AssistantMessage(res.Text, res.ToolCalls));
                return res;
            }
            catch (ProviderException pe) when (e is ProviderException)
            {
                throw new ProviderException(e.Message + " Запасной провайдер тоже не ответил: " + pe.Message, null, e);
            }
            finally { Status?.Invoke(null); }
        }
        finally { ScheduleUnload(); }
    }

    private bool CanFallback(out ILlmProvider provider, out string model)
    {
        provider = null!;
        model = "";
        var s = _settings();
        if (!s.Local.CloudFallback || _providers is null) return false;
        var id = s.Local.FallbackProvider;
        if (id == ProviderId || _providers(id) is not { } p) return false;
        provider = p;
        model = s.Models.TryGetValue(id, out var m) && !string.IsNullOrWhiteSpace(m) ? m : VeraSettings.DefaultModel(id);
        return true;
    }

    private async Task<StepResult> LocalStep(StepRequest request, Conv c, Action<string> onText, CancellationToken ct)
    {
        await EnsureLoaded(request.Model, ct).ConfigureAwait(false);
        var system = request.System;
        if (request.Tools.Count > 0) system += "\n\n" + ToolPrompt.Describe(request.Tools, compact: true);
        var turns = Fit(system, ToTurns(c.History));

        var raw = new StringBuilder();
        var filter = new LocalStreamFilter();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        GenerateStats stats;
        try
        {
            stats = await _engine.GenerateAsync(turns, new GenerateOptions { MaxTokens = 1024, Stops = StopStrings }, piece =>
            {
                raw.Append(piece);
                var visible = filter.Push(piece);
                if (visible.Length > 0) onText(visible);
            }, ct).ConfigureAwait(false);
        }
        catch (PromptTooLongException e)
        {
            throw new ProviderException($"Сообщение слишком длинное для встроенной модели ({e.Tokens} токенов при контексте {e.Context}). " +
                                        "Увеличь размер контекста в настройках или начни новый разговор.", null, e);
        }
        catch (InvalidOperationException e)
        {
            throw new ProviderException(e.Message, null, e);
        }
        var tail = filter.Flush();
        if (tail.Length > 0) onText(tail);
        LastTokensPerSecond = stats.OutputTokens / Math.Max(0.05, sw.Elapsed.TotalSeconds);

        var text = ToolCallParser.StripThinking(raw.ToString()).Trim();
        var calls = new List<ToolCall>();
        var visibleText = text;
        if (text.Contains("<tool_call>", StringComparison.Ordinal) &&
            ToolCallParser.TryExtract(text, request.Tools.Select(t => t.Name).ToList(), out var found, out var cleaned, strict: false))
        {
            var prefix = "local_" + Guid.NewGuid().ToString("N")[..8] + "_";
            calls = found.Select((f, i) => new ToolCall { Id = prefix + i, Name = f.Name, Args = f.Args }).ToList();
            visibleText = cleaned.Trim();
        }
        else if (text.Contains("<tool_call>", StringComparison.Ordinal))
        {
            // вызов не разобрался — показываем текст без служебного блока
            visibleText = ToolCallParser.StripThinking(raw.ToString().Split("<tool_call>")[0]).Trim();
        }
        c.History.Add(AssistantMessage(visibleText, calls));
        return new StepResult
        {
            Text = visibleText,
            ToolCalls = calls,
            Stop = calls.Count > 0 ? "tool_use" : stats.HitMaxTokens ? "max_tokens" : "end",
            InputTokens = stats.PromptTokens,
            OutputTokens = stats.OutputTokens,
            Raw = $"[{_engine.DeviceName}, {LastTokensPerSecond:0.0} ток/с, из кэша {stats.CachedTokens}/{stats.PromptTokens}] " + raw,
        };
    }

    private static VeraMessage AssistantMessage(string text, IReadOnlyList<ToolCall> calls) => new()
    {
        Role = "assistant",
        Text = text,
        ToolCalls = calls.Count > 0 ? calls.Select(x => new ToolCall { Id = x.Id, Name = x.Name, Args = x.Args }).ToList() : null,
    };

    /// <summary>VeraMessage → сообщения шаблона чата. Результаты инструментов идут сообщением пользователя.</summary>
    internal static List<ChatTurn> ToTurns(IEnumerable<VeraMessage> history)
    {
        var list = new List<ChatTurn>();
        var tools = new List<(string Name, ToolResult Result)>();
        var toolImages = false;
        void FlushTools()
        {
            if (tools.Count == 0) return;
            var t = ToolPrompt.Results(tools);
            if (toolImages) t = "(Инструмент вернул картинку, но встроенная модель не видит изображения.)\n" + t;
            Add("user", t);
            tools.Clear();
            toolImages = false;
        }
        void Add(string role, string content)
        {
            // шаблоны многих моделей требуют чередования ролей — склеиваем подряд идущие
            if (list.Count > 0 && list[^1].Role == role) list[^1] = list[^1] with { Content = list[^1].Content + "\n\n" + content };
            else list.Add(new ChatTurn(role, content));
        }

        foreach (var m in history)
        {
            if (m.Role != "tool") FlushTools();
            switch (m.Role)
            {
                case "user":
                {
                    var sb = new StringBuilder();
                    if (m.Ctx is not null) sb.Append("<context>").Append(m.Ctx).Append("</context>\n");
                    foreach (var d in m.Documents ?? new())
                        sb.Append("<document id=\"").Append(d.Id).Append("\" name=\"").Append(d.Name).Append("\">\n").Append(d.Text).Append("\n</document>\n");
                    if (m.Images is { Count: > 0 } || (m.HadImages && m.Images is null))
                        sb.Append("(Пользователь приложил изображение, но встроенная модель не видит картинки — скажи об этом, если оно важно.)\n");
                    sb.Append(m.Text);
                    Add("user", sb.ToString());
                    break;
                }
                case "assistant":
                {
                    var sb = new StringBuilder(m.Text ?? "");
                    foreach (var tc in m.ToolCalls ?? new())
                    {
                        sb.Append(sb.Length > 0 ? "\n" : "").Append("<tool_call>\n{\"name\": \"").Append(tc.Name)
                          .Append("\", \"arguments\": ").Append(ToolPrompt.Json(tc.Args)).Append("}\n</tool_call>");
                    }
                    Add("assistant", sb.Length > 0 ? sb.ToString() : "…");
                    break;
                }
                case "tool":
                    tools.Add((m.Name ?? "tool", m.IsError ? ToolResult.Error(m.Text ?? "") : ToolResult.Ok(m.Text ?? "")));
                    if (m.Images is { Count: > 0 }) toolImages = true;
                    break;
            }
        }
        FlushTools();
        // ответ ассистента должен идти после пользователя
        while (list.Count > 0 && list[0].Role == "assistant") list.RemoveAt(0);
        return list;
    }

    /// <summary>Подогнать переписку под окно контекста: сначала выкидываем старые сообщения, потом режем длинные.</summary>
    private List<ChatTurn> Fit(string system, List<ChatTurn> turns)
    {
        List<ChatTurn> With(List<ChatTurn> t) => new[] { new ChatTurn("system", system) }.Concat(t).ToList();
        var limit = Math.Max(512, _engine.ContextSize - ReplyReserve);
        var all = With(turns);
        if (_engine.CountTokens(all) <= limit) return all;

        // выкидываем старые пары, но последнее сообщение пользователя оставляем всегда
        while (turns.Count > 1)
        {
            turns.RemoveAt(0);
            while (turns.Count > 1 && turns[0].Role != "user") turns.RemoveAt(0);
            all = With(turns);
            if (_engine.CountTokens(all) <= limit) return all;
        }
        // одно огромное сообщение (документ) — режем по символам, грубо 3 символа на токен
        var last = turns[^1];
        var over = _engine.CountTokens(all) - limit;
        var cut = Math.Max(0, last.Content.Length - over * 3 - 200);
        turns[^1] = last with { Content = last.Content[..cut] + "\n…(текст обрезан: не поместился в контекст встроенной модели)" };
        return With(turns);
    }

    public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken ct = default)
    {
        var list = _store.InstalledLlms().Select(f =>
        {
            var cat = ModelCatalog.Llms.FirstOrDefault(a => a.Files[0].FileName.Equals(f.Name, StringComparison.OrdinalIgnoreCase));
            return new ModelInfo(f.FullName, cat?.Title ?? Path.GetFileNameWithoutExtension(f.Name), Tools: true, Vision: false, 0, 0);
        }).ToList();
        return Task.FromResult<IReadOnlyList<ModelInfo>>(list);
    }

    public async Task<string> TestAsync(CancellationToken ct = default)
    {
        var s = _settings();
        var model = s.Models.TryGetValue(ProviderId, out var m) ? m : "";
        await EnsureLoaded(model, ct).ConfigureAwait(false);
        var sb = new StringBuilder();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var stats = await _engine.GenerateAsync(new[]
        {
            new ChatTurn("system", "Отвечай одним коротким предложением по-русски."),
            new ChatTurn("user", "Скажи привет."),
        }, new GenerateOptions { MaxTokens = 24, Stops = StopStrings }, t => sb.Append(t), ct).ConfigureAwait(false);
        var speed = stats.OutputTokens / Math.Max(0.05, sw.Elapsed.TotalSeconds);
        ScheduleUnload();
        var answer = ToolCallParser.StripThinking(sb.ToString()).Trim();
        if (answer.Length > 60) answer = answer[..60] + "…";
        return $"Модель работает на устройстве «{_engine.DeviceName}», ~{speed:0.#} токенов/с. Ответ: «{answer}»";
    }

    private void ScheduleUnload()
    {
        var min = _settings().Local.UnloadAfterMinutes;
        _idle.Change(min > 0 ? TimeSpan.FromMinutes(min) : Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    private void UnloadIfIdle()
    {
        try { _engine.Unload(); } catch { /* выгрузка не критична */ }
    }

    public void Dispose()
    {
        _idle.Dispose();
        _engine.Dispose();
    }
}
