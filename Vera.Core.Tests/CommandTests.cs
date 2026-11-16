using System.Text.Json.Nodes;
using Vera.Core.Commands;
using Xunit;

namespace Vera.Core.Tests;

[Collection("L10n")]
public class CommandTests
{
    public CommandTests() => L10n.Lang = "ru"; // сообщения проверяются по-русски; другие тесты могут переключить язык
    private sealed class FakeHost : ICommandHost
    {
        public bool Answer = true;
        public readonly List<string> Said = new();
        public readonly List<string> Asked = new();
        public Task<bool> AskAsync(string question, CancellationToken ct) { Asked.Add(question); return Task.FromResult(Answer); }
        public void Say(string text) => Said.Add(text);
        public Task AskAiAsync(string prompt, CancellationToken ct) { Asked.Add("AI:" + prompt); return Task.CompletedTask; }
    }

    private readonly List<string> _calls = new();
    private readonly FakeHost _host = new();
    private readonly ReminderService _reminders = new(new MemStorage());
    private DateTimeOffset _now = new(2026, 11, 16, 10, 0, 0, TimeSpan.FromHours(3)); // понедельник

    private CommandRunner Runner(Func<string, JsonObject, ToolResult>? tool = null) => new(
        (name, args, _) =>
        {
            _calls.Add(name + " " + args.ToJsonString());
            return Task.FromResult(tool?.Invoke(name, args) ?? ToolResult.Ok("ok", "✓ " + name));
        }, _reminders, _host, () => _now);

    /// <summary>Схема: блоки по порядку, соединённые «out».</summary>
    private static UserCommand Chain(params CommandNode[] nodes)
    {
        var c = new UserCommand { Name = "Тест" };
        c.Nodes.AddRange(nodes);
        for (var i = 0; i + 1 < nodes.Length; i++) c.Links.Add(new CommandLink { From = nodes[i].Id, To = nodes[i + 1].Id });
        return c;
    }

    private static CommandNode N(string type, params (string K, string V)[] props)
    {
        var n = Blocks.Create(type, 0, 0);
        foreach (var (k, v) in props) n.Props[k] = v;
        return n;
    }

    [Fact]
    public async Task RunsActionsInOrderFromTrigger()
    {
        var c = Chain(N("phrase", ("phrases", "работаем")), N("volume", ("level", "30")), N("open_app", ("name", "VS Code")), N("say", ("text", "Работаем")));
        var steps = new List<string>();
        var r = Runner();
        r.StepStarted += id => steps.Add(c.Node(id)!.Type);
        var res = await r.RunAsync(c);
        Assert.True(res.Ok, res.Message);
        Assert.Equal(new[] { "volume", "open_app", "say" }, steps);
        Assert.Equal("media_control {\"action\":\"set_volume\",\"level\":30}", _calls[0]);
        Assert.Contains("VS Code", _calls[1]);
        Assert.Equal(new[] { "Работаем" }, _host.Said);
    }

    [Fact]
    public async Task IfBranchesByWeekday()
    {
        var trig = N("phrase");
        var cond = N("if", ("cond", "weekday"));
        var yes = N("say", ("text", "работа"));
        var no = N("say", ("text", "отдых"));
        var c = new UserCommand { Nodes = { trig, cond, yes, no } };
        c.Links.Add(new CommandLink { From = trig.Id, To = cond.Id });
        c.Links.Add(new CommandLink { From = cond.Id, Port = "yes", To = yes.Id });
        c.Links.Add(new CommandLink { From = cond.Id, Port = "no", To = no.Id });
        await Runner().RunAsync(c);
        _now = _now.AddDays(5); // суббота
        await Runner().RunAsync(c);
        Assert.Equal(new[] { "работа", "отдых" }, _host.Said);
    }

    [Fact]
    public async Task AskAndRepeat()
    {
        var trig = N("phrase");
        var ask = N("ask", ("question", "Точно?"));
        var rep = N("repeat", ("times", "3"));
        var body = N("media", ("action", "next"));
        var after = N("say", ("text", "всё"));
        var c = new UserCommand { Nodes = { trig, ask, rep, body, after } };
        c.Links.Add(new CommandLink { From = trig.Id, To = ask.Id });
        c.Links.Add(new CommandLink { From = ask.Id, Port = "yes", To = rep.Id });
        c.Links.Add(new CommandLink { From = rep.Id, Port = "body", To = body.Id });
        c.Links.Add(new CommandLink { From = rep.Id, Port = "out", To = after.Id });
        await Runner().RunAsync(c);
        Assert.Equal(3, _calls.Count(x => x.Contains("next")));
        Assert.Equal(new[] { "всё" }, _host.Said);
        _host.Answer = false;
        _calls.Clear();
        await Runner().RunAsync(c);
        Assert.Empty(_calls); // ответил «нет» — ветка «нет» пустая
    }

    [Fact]
    public async Task ToolErrorStopsAndPointsToBlock()
    {
        var bad = N("open_app", ("name", "нет такой"));
        var c = Chain(N("phrase"), bad, N("say", ("text", "не дойдёт")));
        var res = await Runner((name, _) => name == "open_app" ? ToolResult.Error("не нашла", "🚀 не найдено") : ToolResult.Ok("ok")).RunAsync(c);
        Assert.False(res.Ok);
        Assert.Equal(bad.Id, res.FailedNode);
        Assert.Contains("не найдено", res.Message);
        Assert.Empty(_host.Said);
    }

    [Fact]
    public async Task LoopIsStoppedAndWaitIsCancellable()
    {
        var a = N("media", ("action", "next"));
        var b = N("media", ("action", "previous"));
        var c = new UserCommand { Nodes = { a, b } };
        c.Links.Add(new CommandLink { From = a.Id, To = b.Id });
        c.Links.Add(new CommandLink { From = b.Id, To = a.Id });
        var res = await Runner().RunAsync(c);
        Assert.False(res.Ok);
        Assert.Contains("зациклилась", res.Message);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var waitCmd = Chain(N("phrase"), N("wait", ("seconds", "30")));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var w = await Runner().RunAsync(waitCmd, ct: cts.Token);
        Assert.False(w.Ok);
        Assert.True(sw.ElapsedMilliseconds < 5000);
    }

    [Fact]
    public async Task ReminderBlockAddsReminder()
    {
        _now = DateTimeOffset.Now;
        await Runner().RunAsync(Chain(N("phrase"), N("reminder", ("text", "Перерыв"), ("minutes", "50"))));
        var r = Assert.Single(_reminders.List());
        Assert.Equal("Перерыв", r.Text);
        Assert.Equal(_now.AddMinutes(50), r.Due);
    }

    [Fact]
    public void PhraseMatchingIsExact()
    {
        var svc = new CommandService(new MemStorage());
        var c = Chain(N("phrase", ("phrases", "Рабочий режим, работаем")), N("say"));
        c.Name = "Работа";
        svc.Save(c);
        Assert.Equal(c.Id, svc.MatchPhrase("Вера, рабочий режим!")?.Cmd.Id);
        Assert.Equal(c.Id, svc.MatchPhrase("работаем")?.Cmd.Id);
        Assert.Null(svc.MatchPhrase("включи рабочий режим и музыку"));
        c.Enabled = false;
        svc.Save(c);
        Assert.Null(svc.MatchPhrase("работаем"));
    }

    [Fact]
    public void ScheduleFiresOncePerDayOnRightDays()
    {
        var svc = new CommandService(new MemStorage());
        svc.Save(Chain(N("schedule", ("time", "09:30"), ("days", "weekdays")), N("say")));
        var mon = new DateTimeOffset(2026, 11, 16, 9, 30, 20, TimeSpan.FromHours(3));
        Assert.Empty(svc.DueSchedules(mon.AddMinutes(-1)));
        Assert.Single(svc.DueSchedules(mon));
        Assert.Empty(svc.DueSchedules(mon.AddMinutes(1))); // уже запускали
        Assert.Single(svc.DueSchedules(mon.AddDays(1)));
        Assert.Empty(svc.DueSchedules(mon.AddDays(5))); // суббота
        Assert.Empty(svc.DueSchedules(mon.AddDays(2).AddMinutes(20))); // проспали больше 5 минут
    }

    [Fact]
    public async Task VeraCanRunAllowedCommandsViaTool()
    {
        var core = new VeraCore(new MemStorage(), new MemSecrets(), null, "desktop");
        var cmd = Chain(N("phrase", ("phrases", "кофе")), N("say", ("text", "Варю")));
        cmd.Name = "Кофе-брейк";
        core.Commands.Save(cmd);
        Assert.DoesNotContain(core.Tools.GetDefinitions(core.Settings.Current), d => d.Name == "run_saved_command");
        var host = new FakeHost();
        core.Tools.RunCommand = (c, ct) => new CommandRunner(core.Tools.ExecuteAsync, core.Reminders, host).RunAsync(c, null, ct);
        var def = Assert.Single(core.Tools.GetDefinitions(core.Settings.Current), d => d.Name == "run_saved_command");
        Assert.Contains("Кофе-брейк", def.Description);
        var ok = await core.Tools.ExecuteAsync("run_saved_command", new JsonObject { ["name"] = "кофе-брейк" }, default);
        Assert.False(ok.IsError, ok.Text);
        Assert.Equal(new[] { "Варю" }, host.Said);
        cmd.AllowAi = false;
        core.Commands.Save(cmd);
        Assert.True((await core.Tools.ExecuteAsync("run_saved_command", new JsonObject { ["name"] = "Кофе-брейк" }, default)).IsError);
    }

    [Fact]
    public void CatalogIsConsistent()
    {
        Assert.Equal(Blocks.All.Count, Blocks.All.Select(b => b.Type).Distinct().Count());
        foreach (var b in Blocks.All)
        {
            var n = Blocks.Create(b.Type, 0, 0);
            Assert.NotNull(b.Summary(n));
            Assert.All(b.Props, p => Assert.True(n.Props.ContainsKey(p.Key)));
        }
        Assert.Contains(Blocks.Search("звук"), b => b.Type == "volume");
        Assert.Contains(Blocks.Search("ГРОМК"), b => b.Type == "volume");
        Assert.Equal(Blocks.All.Count, Blocks.Search("").Count());
        var s = Blocks.Sample();
        Assert.Single(s.Triggers);
        Assert.Equal("say", s.Next(s.Triggers.First().Id)!.Type);
    }
}
