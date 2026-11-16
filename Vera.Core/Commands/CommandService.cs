using System.Globalization;
using System.Text.Json.Nodes;
using Vera.Core.Tools;

namespace Vera.Core.Commands;

/// <summary>То, что команда просит у платформы: спросить человека, показать реплику, спросить ИИ.</summary>
public interface ICommandHost
{
    /// <summary>Вопрос «да / нет» (блок «Спросить меня»).</summary>
    Task<bool> AskAsync(string question, CancellationToken ct);
    /// <summary>Реплика Веры (блок «Вера ответит»): пузырь, «голосок».</summary>
    void Say(string text);
    /// <summary>Отправить вопрос Вере в чат (блок «Спросить ИИ»).</summary>
    Task AskAiAsync(string prompt, CancellationToken ct);
}

public sealed record CommandRunResult(bool Ok, string Message, string? FailedNode = null);

/// <summary>
/// Выполняет схему команды: от блока запуска по линиям, ветки «да / нет», повтор, ожидание.
/// Действия — через инструменты (<see cref="ToolRegistry"/>), те же, что у Веры в чате.
/// </summary>
public sealed class CommandRunner
{
    /// <summary>Защита от зацикленной схемы.</summary>
    public const int MaxSteps = 300;

    private readonly Func<string, JsonObject, CancellationToken, Task<ToolResult>> _tool;
    private readonly ReminderService _reminders;
    private readonly ICommandHost _host;
    private readonly Func<DateTimeOffset> _now;

    public CommandRunner(Func<string, JsonObject, CancellationToken, Task<ToolResult>> tool, ReminderService reminders, ICommandHost host, Func<DateTimeOffset>? now = null)
    {
        _tool = tool;
        _reminders = reminders;
        _host = host;
        _now = now ?? (() => DateTimeOffset.Now);
    }

    /// <summary>Блок начал выполняться (для подсветки в редакторе).</summary>
    public event Action<string>? StepStarted;
    /// <summary>Блок закончил: (id, успех, подпись).</summary>
    public event Action<string, bool, string>? StepFinished;

    private sealed class Run
    {
        public required UserCommand Cmd;
        public int Steps;
        public bool LastAnswer;
        public readonly List<string> Done = new();
    }

    public async Task<CommandRunResult> RunAsync(UserCommand cmd, string? triggerId = null, CancellationToken ct = default)
    {
        var start = (triggerId is null ? null : cmd.Node(triggerId)) ?? cmd.Triggers.FirstOrDefault()
            ?? cmd.Nodes.FirstOrDefault(n => cmd.Links.All(l => l.To != n.Id)) ?? cmd.Nodes.FirstOrDefault();
        if (start is null) return new CommandRunResult(false, L10n.T("В команде нет блоков."));
        var run = new Run { Cmd = cmd };
        try
        {
            var first = Blocks.Get(start.Type)?.Category == BlockCategory.Start ? cmd.Next(start.Id) : start;
            if (first is null) return new CommandRunResult(false, L10n.T("К блоку запуска ничего не подключено."));
            await ChainAsync(run, first, ct).ConfigureAwait(false);
            return new CommandRunResult(true, run.Done.Count > 0 ? string.Join("; ", run.Done) : L10n.T("Готово"));
        }
        catch (OperationCanceledException)
        {
            return new CommandRunResult(false, L10n.T("Остановлено"));
        }
        catch (CommandStepException e)
        {
            return new CommandRunResult(false, e.Message, e.NodeId);
        }
    }

    private async Task ChainAsync(Run run, CommandNode? node, CancellationToken ct)
    {
        while (node is not null)
        {
            ct.ThrowIfCancellationRequested();
            if (++run.Steps > MaxSteps) throw new CommandStepException(node.Id, L10n.T("Слишком много шагов — похоже, схема зациклилась."));
            StepStarted?.Invoke(node.Id);
            string port;
            try
            {
                port = await StepAsync(run, node, ct).ConfigureAwait(false);
            }
            catch (CommandStepException)
            {
                StepFinished?.Invoke(node.Id, false, "");
                throw;
            }
            catch (OperationCanceledException)
            {
                StepFinished?.Invoke(node.Id, false, "");
                throw;
            }
            catch (Exception e)
            {
                StepFinished?.Invoke(node.Id, false, e.Message);
                throw new CommandStepException(node.Id, Title(node) + ": " + e.Message);
            }
            StepFinished?.Invoke(node.Id, true, "");
            node = port.Length == 0 ? null : run.Cmd.Next(node.Id, port);
        }
    }

    /// <summary>Выполнить блок, вернуть выход, по которому идти дальше ("" — конец).</summary>
    private async Task<string> StepAsync(Run run, CommandNode n, CancellationToken ct)
    {
        switch (n.Type)
        {
            case "phrase" or "hotkey" or "schedule" or "startup":
                return "out"; // блок запуска посреди схемы — просто проходим
            case "open_app": await Tool(run, n, "open_app", new JsonObject { ["name"] = n.Get("name") }, ct); return "out";
            case "open_url": await Tool(run, n, "open_url", new JsonObject { ["url"] = n.Get("url") }, ct); return "out";
            case "open_folder": await Tool(run, n, "open_folder", new JsonObject { ["path"] = n.Get("path") }, ct); return "out";
            case "volume":
                await Tool(run, n, "media_control", new JsonObject { ["action"] = "set_volume", ["level"] = Math.Clamp(Int(n.Get("level"), 30), 0, 100) }, ct);
                return "out";
            case "media": await Tool(run, n, "media_control", new JsonObject { ["action"] = n.Get("action") }, ct); return "out";
            case "type_text": await Tool(run, n, "type_text", new JsonObject { ["text"] = n.Get("text") }, ct); return "out";
            case "screenshot": await Tool(run, n, "take_screenshot", new JsonObject(), ct); return "out";
            case "system": await Tool(run, n, "system_action", new JsonObject { ["action"] = n.Get("action") }, ct); return "out";
            case "powershell":
                await Tool(run, n, "run_command", new JsonObject
                {
                    ["command"] = n.Get("command"),
                    ["reason"] = L10n.F("Шаг команды «{0}»", run.Cmd.Name),
                }, ct);
                return "out";
            case "if":
                return Condition(run, n) ? "yes" : "no";
            case "wait":
                await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(Dbl(n.Get("seconds"), 1), 0, 3600)), ct).ConfigureAwait(false);
                return "out";
            case "repeat":
            {
                var times = Math.Clamp(Int(n.Get("times"), 1), 0, 100);
                var body = run.Cmd.Next(n.Id, "body");
                for (var i = 0; i < times && body is not null; i++) await ChainAsync(run, body, ct).ConfigureAwait(false);
                return "out";
            }
            case "ask":
                run.LastAnswer = await _host.AskAsync(n.Get("question"), ct).ConfigureAwait(false);
                return run.LastAnswer ? "yes" : "no";
            case "say":
                _host.Say(n.Get("text"));
                return "out";
            case "ask_ai":
                await _host.AskAiAsync(n.Get("prompt"), ct).ConfigureAwait(false);
                return "out";
            case "code_task":
                await _host.AskAiAsync(L10n.F("В проекте «{0}» сделай: {1}", n.Get("project"), n.Get("task")), ct).ConfigureAwait(false);
                return "out";
            case "reminder":
            {
                var r = _reminders.Add(n.Get("text"), _now().AddMinutes(Math.Clamp(Dbl(n.Get("minutes"), 10), 0.1, 60 * 24 * 365)), "none", "command");
                run.Done.Add("⏰ " + r.Text);
                return "out";
            }
            default:
                throw new CommandStepException(n.Id, L10n.T("Неизвестный блок: ") + n.Type);
        }
    }

    private async Task Tool(Run run, CommandNode n, string name, JsonObject args, CancellationToken ct)
    {
        var r = await _tool(name, args, ct).ConfigureAwait(false);
        if (r.IsError) throw new CommandStepException(n.Id, Title(n) + ": " + (r.Label ?? r.Text));
        if (!string.IsNullOrEmpty(r.Label)) run.Done.Add(r.Label!);
    }

    private bool Condition(Run run, CommandNode n)
    {
        var now = _now();
        var weekend = now.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
        return n.Get("cond") switch
        {
            "weekend" => weekend,
            "after" => now.TimeOfDay >= Time(n.Get("time")),
            "before" => now.TimeOfDay < Time(n.Get("time")),
            "answer" => run.LastAnswer,
            _ => !weekend,
        };
    }

    private static string Title(CommandNode n) => L10n.T(Blocks.Get(n.Type)?.Title ?? n.Type);

    internal static int Int(string s, int fallback) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    internal static double Dbl(string s, double fallback) =>
        double.TryParse((s ?? "").Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    /// <summary>«18:30» → 18:30; ошибка — полночь.</summary>
    public static TimeSpan Time(string s) =>
        TimeSpan.TryParseExact((s ?? "").Trim(), new[] { @"h\:mm", @"hh\:mm" }, CultureInfo.InvariantCulture, out var t) ? t : TimeSpan.Zero;
}

public sealed class CommandStepException : Exception
{
    public CommandStepException(string nodeId, string message) : base(message) => NodeId = nodeId;
    public string NodeId { get; }
}

/// <summary>Свои команды: хранение (commands.json), поиск по фразе, расписание. Не синхронизируются с телефоном.</summary>
public sealed class CommandService
{
    private readonly JsonFile<CommandsFile> _file;

    public CommandService(IVeraStorage storage) => _file = new JsonFile<CommandsFile>(storage, "commands.json", () => new CommandsFile());

    public event Action? Changed;

    public IReadOnlyList<UserCommand> List() => _file.Data.Commands;

    public UserCommand? Get(string id) => _file.Data.Commands.FirstOrDefault(c => c.Id == id);

    /// <summary>Добавить или заменить (по Id).</summary>
    public void Save(UserCommand cmd)
    {
        cmd.Updated = DateTimeOffset.Now;
        var list = _file.Data.Commands;
        var i = list.FindIndex(c => c.Id == cmd.Id);
        if (i >= 0) list[i] = cmd; else list.Add(cmd);
        _file.Save();
        Changed?.Invoke();
    }

    public void Remove(string id)
    {
        if (_file.Data.Commands.RemoveAll(c => c.Id == id) > 0)
        {
            _file.Save();
            Changed?.Invoke();
        }
    }

    public void Flush() => _file.SaveNow();

    /// <summary>
    /// Команда, чья фраза совпадает с текстом целиком (с «Вера, …» в начале или без).
    /// Только точное совпадение — обычные просьбы идут Вере как раньше.
    /// </summary>
    public (UserCommand Cmd, CommandNode Trigger)? MatchPhrase(string? text, string wakePhrases = "вера")
    {
        var t = CommandText.Normalize(text);
        if (t.Length == 0) return null;
        var variants = new List<string> { t };
        foreach (var w in CommandText.SplitPhrases(wakePhrases).Append("вера"))
            if (t.StartsWith(w + " ", StringComparison.Ordinal)) variants.Add(t[(w.Length + 1)..]);
        foreach (var c in List().Where(c => c.Enabled))
            foreach (var trig in c.Triggers.Where(n => n.Type == "phrase"))
                if (CommandText.SplitPhrases(trig.Get("phrases")).Any(p => variants.Contains(p)))
                    return (c, trig);
        return null;
    }

    /// <summary>Команды, чьё расписание наступило в (after, now]. Отмечает запуск, чтобы не повторять.</summary>
    public List<(UserCommand Cmd, CommandNode Trigger)> DueSchedules(DateTimeOffset now)
    {
        var due = new List<(UserCommand, CommandNode)>();
        foreach (var c in List().Where(c => c.Enabled))
            foreach (var t in c.Triggers.Where(n => n.Type == "schedule"))
            {
                var days = t.Get("days");
                var weekend = now.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
                if ((days == "weekdays" && weekend) || (days == "weekends" && !weekend)) continue;
                var at = new DateTimeOffset(now.Date + CommandRunner.Time(t.Get("time")), now.Offset);
                // окно 5 минут: если ПК был занят или спал — всё равно запустим, но один раз за день
                if (now < at || now - at > TimeSpan.FromMinutes(5)) continue;
                if (c.LastScheduled is { } last && last >= at) continue;
                c.LastScheduled = now;
                due.Add((c, t));
            }
        if (due.Count > 0) _file.Save();
        return due;
    }

    public IEnumerable<(UserCommand Cmd, CommandNode Trigger)> StartupCommands() =>
        List().Where(c => c.Enabled).SelectMany(c => c.Triggers.Where(t => t.Type == "startup").Select(t => (c, t)));

    public IEnumerable<(UserCommand Cmd, CommandNode Trigger, string Keys)> Hotkeys() =>
        List().Where(c => c.Enabled).SelectMany(c => c.Triggers.Where(t => t.Type == "hotkey" && t.Get("keys").Length > 0)
            .Select(t => (c, t, t.Get("keys"))));

    /// <summary>Найти команду по названию или фразе (для инструмента Веры).</summary>
    public UserCommand? FindForAi(string? nameOrPhrase)
    {
        var q = CommandText.Normalize(nameOrPhrase);
        var ai = List().Where(c => c.Enabled && c.AllowAi).ToList();
        return ai.FirstOrDefault(c => CommandText.Normalize(c.Name) == q)
            ?? ai.FirstOrDefault(c => c.Phrases().Contains(q))
            ?? ai.FirstOrDefault(c => q.Length > 2 && CommandText.Normalize(c.Name).Contains(q));
    }
}
