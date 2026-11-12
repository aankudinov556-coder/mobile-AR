using System.Text;
using System.Text.RegularExpressions;
using Vera.Core.Providers;
using Vera.Core.Tools;

namespace Vera.Core.Agent;

// ============================================================================ events

public abstract record AgentEvent(string TurnId);

public sealed record TurnStarted(string TurnId, string Source, string UserText, bool HasImage, bool Hidden) : AgentEvent(TurnId);

/// <summary>Кусочек текста ответа (тег эмоции уже вырезан). Step — номер шага модели в ходе.</summary>
public sealed record TextDelta(string TurnId, int Step, string Delta) : AgentEvent(TurnId);

public sealed record EmotionChanged(string TurnId, string Emotion) : AgentEvent(TurnId);

/// <summary>Вызов инструмента: Running=true — начался, false — закончился (Label/IsError/Kind заполнены).</summary>
public sealed record ToolActivity(string TurnId, string CallId, string Name, bool Running, string Label, bool IsError, string? Kind) : AgentEvent(TurnId);

public sealed record TurnEnded(string TurnId, string Text, string Emotion, string Source) : AgentEvent(TurnId);

public sealed record TurnFailed(string TurnId, string Message, bool Aborted, string PartialText, string Source) : AgentEvent(TurnId);

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
                outList.Add(new VisibleMessage { Role = "user", Text = m.Text ?? "", Time = m.Ts, HasImage = m.Images is { Count: > 0 } || m.HadImages });
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

    private void Persist()
    {
        List<VeraMessage> data;
        lock (_gate)
        {
            if (_history.Count > MaxSaved * 2) _history.RemoveRange(0, _history.Count - MaxSaved);
            data = _history.Skip(Math.Max(0, _history.Count - MaxSaved)).Select(m =>
            {
                if (m.Images is not { Count: > 0 }) return m;
                return new VeraMessage
                {
                    Role = m.Role, Text = m.Text, HadImages = true, Ctx = m.Ctx, Ts = m.Ts, Source = m.Source, Hidden = m.Hidden,
                    ToolCalls = m.ToolCalls, ToolCallId = m.ToolCallId, Name = m.Name, IsError = m.IsError, Label = m.Label,
                    TurnId = m.TurnId, Emotion = m.Emotion,
                };
            }).ToList();
        }
        _file.Data = data;
        _file.Save();
    }

    /// <summary>Последние N сообщений начиная с user (пары tool_use/tool_result не разрываются).</summary>
    private List<VeraMessage> ContextWindow()
    {
        var limit = Math.Max(6, _settings.Current.HistoryLimit);
        List<VeraMessage> h;
        lock (_gate) h = _history.ToList();
        var start = Math.Max(0, h.Count - limit);
        while (start < h.Count && h[start].Role != "user") start++;
        var slice = h.Skip(start).ToList();
        // картинки оставляем только у двух последних ходов — экономия токенов
        var userIdx = slice.Select((m, i) => (m, i)).Where(x => x.m.Role == "user").Select(x => x.i).ToList();
        var keepFrom = userIdx.Count > 2 ? userIdx[^2] : 0;
        return slice.Select((m, i) =>
        {
            var hasImg = m.Images is { Count: > 0 };
            if ((i >= keepFrom && hasImg) || (!hasImg && !m.HadImages)) return m;
            return new VeraMessage
            {
                Role = m.Role, Text = (m.Text ?? "") + " [к сообщению было приложено изображение]", Ctx = m.Ctx, Ts = m.Ts,
                ToolCalls = m.ToolCalls, ToolCallId = m.ToolCallId, Name = m.Name, IsError = m.IsError, TurnId = m.TurnId,
            };
        }).ToList();
    }

    /// <summary>
    /// Отправить сообщение. source: "chat" | "voice" | "screen" | "system".
    /// Ошибки приходят событием TurnFailed (и пробрасываются, если это не отмена).
    /// </summary>
    public async Task SendAsync(string text, IReadOnlyList<ImageData>? images = null, string source = "chat", bool hidden = false, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (IsBusy) throw new InvalidOperationException("Вера ещё отвечает на прошлое сообщение");
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
            if (text.Length == 0 && images is not { Count: > 0 }) throw new ArgumentException("Пустое сообщение");
            var provider = _providers.Get(settings.Provider);
            var model = settings.CurrentModel;

            var user = new VeraMessage
            {
                Role = "user",
                Text = text.Length > 0 ? text : "Что на этом изображении?",
                Images = images is { Count: > 0 } ? images.ToList() : null,
                Ctx = RuText.TimeContext(DateTimeOffset.Now),
                Source = source,
                Hidden = hidden,
            };
            lock (_gate) _history.Add(user);
            Emit(new TurnStarted(turnId, source, user.Text!, user.Images is not null, hidden));

            // system и tools фиксируются на весь ход — внутри хода история только дописывается
            var toolDefs = _tools.GetDefinitions(settings);
            var system = Personas.BuildSystemPrompt(settings, _memory.ForPrompt(), toolDefs.Select(t => t.Name).ToList(), _platform);
            var conv = provider.Begin(ContextWindow());
            var request = new StepRequest { Model = model, System = system, Tools = toolDefs, Effort = settings.Effort, WebSearch = settings.WebSearch };

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

                var clean = TagFilter.Strip(res.Text);
                var asst = new VeraMessage { Role = "assistant", Text = clean, TurnId = turnId, Emotion = emotion };
                if (res.ToolCalls.Count > 0) asst.ToolCalls = res.ToolCalls.Select(tc => new ToolCall { Id = tc.Id, Name = tc.Name, Args = tc.Args }).ToList();
                lock (_gate) _history.Add(asst);
                Append(fullText, clean);

                if (res.Stop == "pause") continue;
                if (res.ToolCalls.Count == 0) break;

                var results = new List<ToolExecution>();
                foreach (var tc in res.ToolCalls)
                {
                    if (token.IsCancellationRequested)
                    {
                        results.Add(new ToolExecution(tc.Id, tc.Name, ToolResult.Error("Отменено пользователем.", "⛔ " + tc.Name)));
                        continue;
                    }
                    Emit(new ToolActivity(turnId, tc.Id, tc.Name, true, tc.Name, false, null));
                    var r = await _tools.ExecuteAsync(tc.Name, tc.Args, token).ConfigureAwait(false);
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
                provider.AppendToolResults(conv, results);
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

    private static void Append(StringBuilder sb, string s)
    {
        if (string.IsNullOrEmpty(s)) return;
        if (sb.Length > 0) sb.Append("\n\n");
        sb.Append(s);
    }

    public void Flush() => _file.SaveNow();
}
