using System.Text.Json.Nodes;
using Vera.Core.Coding;
using Xunit;

namespace Vera.Core.Tests;

[Collection("L10n")]
public sealed class CodingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vera-code-" + Guid.NewGuid().ToString("N"));
    private readonly ProjectWorkspace _ws;

    public CodingTests()
    {
        L10n.Lang = "ru";
        _ws = new ProjectWorkspace(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (Exception) { }
    }

    private sealed class FakeHost : ICodingHost
    {
        public readonly List<string> Ran = new();
        public string? Opened;
        public string? Problem = "не выбран";
        public string? AgentTask;
        public Task<(int, string)> RunAsync(string dir, string command, string reason, CancellationToken ct)
        {
            Ran.Add(command);
            return Task.FromResult((0, "hello from " + Path.GetFileName(dir)));
        }
        public void Open(string path) => Opened = path;
        public string? AgentProblem() => Problem;
        public Task<string> RunAgentAsync(string dir, string task, Action<string> progress, CancellationToken ct)
        {
            AgentTask = task;
            progress("правит app.js");
            File.WriteAllText(Path.Combine(dir, "app.js"), "// агент");
            File.WriteAllText(Path.Combine(dir, "new.txt"), "создан агентом");
            return Task.FromResult("Сделано: таймер");
        }
    }

    [Fact]
    public void PathsCannotEscapeTheProject()
    {
        var p = _ws.Create("Таймер");
        Assert.Equal("Таймер", p);
        Assert.Throws<UnauthorizedAccessException>(() => _ws.Resolve(p, "../x.txt"));
        Assert.Throws<UnauthorizedAccessException>(() => _ws.Resolve(p, "a/../../x.txt"));
        Assert.Throws<UnauthorizedAccessException>(() => _ws.Resolve(p, "/etc/passwd"));
        Assert.Throws<UnauthorizedAccessException>(() => _ws.Resolve(p, "C:/Windows/win.ini"));
        Assert.Throws<UnauthorizedAccessException>(() => _ws.Resolve(p, ".vera/journal.json"));
        Assert.Equal("evil", ProjectWorkspace.SafeName("../evil"));
        Assert.True(_ws.Resolve(p, "src/app.js").StartsWith(_ws.ProjectDir(p)));
        Assert.Equal("Мой таймер", ProjectWorkspace.SafeName("Мой таймер?*"));
        Assert.Equal("Проект", ProjectWorkspace.SafeName("..."));
    }

    [Fact]
    public void WriteReplaceAndUndo()
    {
        var p = _ws.Create("Страница");
        _ws.Write(p, "index.html", "<h1>Привет</h1>", "Первая версия");
        _ws.Replace(p, "index.html", "Привет", "Пока", "Заменила текст");
        Assert.Equal("<h1>Пока</h1>", _ws.Read(p, "index.html"));
        Assert.Throws<InvalidOperationException>(() => _ws.Replace(p, "index.html", "нет такого", "x", ""));
        _ws.Write(p, "a.txt", "aa aa", "двойной");
        Assert.Throws<InvalidOperationException>(() => _ws.Replace(p, "a.txt", "aa", "b", ""));
        Assert.Equal(3, _ws.History(p).Count);

        Assert.Equal("двойной", _ws.Undo(p));
        Assert.False(File.Exists(_ws.Resolve(p, "a.txt"))); // файла раньше не было — откат удаляет
        Assert.Equal("Заменила текст", _ws.Undo(p));
        Assert.Equal("<h1>Привет</h1>", _ws.Read(p, "index.html"));
        _ws.Undo(p);
        Assert.False(File.Exists(_ws.Resolve(p, "index.html")));
        Assert.Null(_ws.Undo(p));
        Assert.DoesNotContain(_ws.Files(p), f => f.Path.StartsWith(".vera"));
    }

    [Fact]
    public async Task ToolsWorkThroughRegistry()
    {
        var core = new VeraCore(new MemStorage(), new MemSecrets(), null, "desktop");
        var host = new FakeHost();
        core.Tools.Coding = new CodingTools(_ws, host, () => core.Settings.Current);
        var names = core.Tools.GetDefinitions(core.Settings.Current).Select(d => d.Name).ToList();
        Assert.Contains("code_write_file", names);
        Assert.DoesNotContain("code_delegate", names); // агент не выбран

        async Task<ToolResult> Call(string name, JsonObject args) => await core.Tools.ExecuteAsync(name, args, default);
        var created = await Call("code_create_project", new JsonObject { ["name"] = "Таймер" });
        Assert.False(created.IsError, created.Text);
        Assert.False((await Call("code_write_file", new JsonObject { ["project"] = "Таймер", ["path"] = "index.html", ["content"] = "<p>25:00</p>" })).IsError);
        Assert.Contains("index.html", (await Call("code_list_files", new JsonObject { ["project"] = "Таймер" })).Text);
        Assert.Equal("<p>25:00</p>", (await Call("code_read_file", new JsonObject { ["project"] = "Таймер", ["path"] = "index.html" })).Text);
        var run = await Call("code_run", new JsonObject { ["project"] = "Таймер", ["command"] = "python main.py" });
        Assert.Contains("hello from Таймер", run.Text);
        await Call("code_open", new JsonObject { ["project"] = "Таймер", ["path"] = "index.html" });
        Assert.EndsWith("index.html", host.Opened);
        Assert.True((await Call("code_read_file", new JsonObject { ["project"] = "Таймер", ["path"] = "../../secret" })).IsError);
        Assert.True((await Call("code_list_files", new JsonObject { ["project"] = "Нет такого" })).IsError);

        core.Settings.Update(s => s.Coding.Enabled = false);
        Assert.DoesNotContain(core.Tools.GetDefinitions(core.Settings.Current), d => d.Name.StartsWith("code_"));
    }

    [Fact]
    public async Task DelegateSnapshotsAndCanBeUndone()
    {
        var core = new VeraCore(new MemStorage(), new MemSecrets(), null, "desktop");
        var host = new FakeHost { Problem = null };
        core.Settings.Update(s => s.Coding.Agent = "claude");
        var tools = new CodingTools(_ws, host, () => core.Settings.Current);
        core.Tools.Coding = tools;
        Assert.Contains(core.Tools.GetDefinitions(core.Settings.Current), d => d.Name == "code_delegate");
        var p = _ws.Create("Приложение");
        _ws.Write(p, "app.js", "// было", "начало");
        var steps = new List<string>();
        tools.Activity += (_, s) => steps.Add(s);
        var r = await core.Tools.ExecuteAsync("code_delegate", new JsonObject { ["project"] = p, ["task"] = "Сделай таймер" }, default);
        Assert.False(r.IsError, r.Text);
        Assert.Contains("Сделано: таймер", r.Text);
        Assert.Contains("правит app.js", steps);
        Assert.Equal("// агент", _ws.Read(p, "app.js"));
        _ws.Undo(p); // откат всей работы агента одним шагом
        Assert.Equal("// было", _ws.Read(p, "app.js"));
        Assert.False(File.Exists(_ws.Resolve(p, "new.txt")));
    }

    [Fact]
    public void ClaudeStreamIsParsed()
    {
        Assert.DoesNotContain(ClaudeCodeAgent.Args(false), a => a == "--allowedTools");
        Assert.Contains("Bash", ClaudeCodeAgent.Args(true));
        Assert.Contains("stream-json", ClaudeCodeAgent.Args(false));
        var edit = ClaudeCodeAgent.Parse("{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"name\":\"Edit\",\"input\":{\"file_path\":\"C:/p/src/app.js\"}}]}}");
        Assert.Equal("правит app.js", edit.Step);
        var bash = ClaudeCodeAgent.Parse("{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"name\":\"Bash\",\"input\":{\"command\":\"npm test\"}}]}}");
        Assert.Equal("запускает npm test", bash.Step);
        var text = ClaudeCodeAgent.Parse("{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"думаю\"}]}}");
        Assert.Null(text.Step);
        var res = ClaudeCodeAgent.Parse("{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"result\":\"Готово\"}");
        Assert.Equal("Готово", res.Result);
        Assert.False(res.IsError);
        Assert.Null(ClaudeCodeAgent.Parse("не json").Step);
        Assert.Null(ClaudeCodeAgent.Parse("").Result);
    }

    [Fact]
    public void CodingPromptAppearsOnlyWithTools()
    {
        var s = new VeraSettings();
        Assert.DoesNotContain("Вайбкодинг", Personas.BuildSystemPrompt(s, "", new[] { "open_app" }, "desktop"));
        var withCode = Personas.BuildSystemPrompt(s, "", new[] { "code_write_file", "code_delegate" }, "desktop");
        Assert.Contains("Вайбкодинг", withCode);
        Assert.Contains("code_delegate", withCode);
    }
}
