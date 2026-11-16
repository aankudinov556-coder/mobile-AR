using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Vera.Core.Coding;

/// <summary>Что вайбкодингу нужно от платформы (ПК): запуск команд, открытие файлов, кодинг-агент.</summary>
public interface ICodingHost
{
    /// <summary>Запустить команду в папке проекта (после подтверждения пользователя). Вернуть код выхода и вывод.</summary>
    Task<(int ExitCode, string Output)> RunAsync(string projectDir, string command, string reason, CancellationToken ct);
    /// <summary>Открыть файл (страницу — в браузере) или папку.</summary>
    void Open(string path);
    /// <summary>Есть ли кодинг-агент (null — есть; иначе — почему нет).</summary>
    string? AgentProblem();
    /// <summary>Передать задачу кодинг-агенту в папке проекта; progress — короткие шаги по ходу. Вернуть итог агента.</summary>
    Task<string> RunAgentAsync(string projectDir, string task, Action<string> progress, CancellationToken ct);
}

/// <summary>
/// Вайбкодинг (п. 13.5): инструменты Веры для работы с проектами в папке «Проекты Веры».
/// Вариант A — сама пишет файлы и запускает; вариант B — code_delegate, задача кодинг-агенту.
/// </summary>
public sealed class CodingTools
{
    public const int OutputLimit = 6000;
    private readonly Func<VeraSettings> _settings;

    public CodingTools(ProjectWorkspace workspace, ICodingHost host, Func<VeraSettings> settings)
    {
        Workspace = workspace;
        Host = host;
        _settings = settings;
    }

    public ProjectWorkspace Workspace { get; }
    public ICodingHost Host { get; }

    /// <summary>Шаг работы (для журнала на экране «Проекты» и пузыря Веры).</summary>
    public event Action<string, string>? Activity;

    public static bool IsCodingTool(string name) => name.StartsWith("code_", StringComparison.Ordinal);

    public IEnumerable<ToolDefinition> Definitions()
    {
        var s = _settings().Coding;
        if (!s.Enabled) yield break;
        static ToolDefinition Def(string name, string desc, JsonObject props, params string[] required) =>
            new() { Name = name, Description = desc, Parameters = ToolDefinition.Schema(props, required) };
        // узел JSON нельзя вставить в две схемы — каждый раз новый
        static JsonNode project() => ToolDefinition.Str("Project folder name (from code_projects or code_create_project).");
        static JsonNode path() => ToolDefinition.Str("File path inside the project, e.g. \"index.html\" or \"src/app.js\".");

        yield return Def("code_projects", "List the user's coding projects in Vera's projects folder.", new JsonObject());
        yield return Def("code_create_project", "Create a new project folder for code the user asked to write. Returns the folder name to use.",
            new JsonObject { ["name"] = ToolDefinition.Str("Short human name, e.g. \"Таймер\".") }, "name");
        yield return Def("code_list_files", "List files of a project.", new JsonObject { ["project"] = project() }, "project");
        yield return Def("code_read_file", "Read a text file of a project (up to 256 KB).", new JsonObject { ["project"] = project(), ["path"] = path() }, "project", "path");
        yield return Def("code_write_file", "Create or overwrite a file in a project with full content. Every change is saved in the project history and can be undone.",
            new JsonObject
            {
                ["project"] = project(),
                ["path"] = path(),
                ["content"] = ToolDefinition.Str("Full file content."),
                ["summary"] = ToolDefinition.Str("What this change does, in Russian, a few words."),
            }, "project", "path", "content");
        yield return Def("code_replace", "Replace one exact fragment in a project file (the fragment must occur exactly once). Prefer this for small edits.",
            new JsonObject
            {
                ["project"] = project(),
                ["path"] = path(),
                ["old"] = ToolDefinition.Str("Exact text to replace, copied from code_read_file."),
                ["new"] = ToolDefinition.Str("Replacement text."),
                ["summary"] = ToolDefinition.Str("What this change does, in Russian, a few words."),
            }, "project", "path", "old", "new");
        yield return Def("code_run", "Run a command in the project folder (PowerShell on Windows), e.g. \"python main.py\" or \"npm test\". The user confirms every command. Returns exit code and output.",
            new JsonObject
            {
                ["project"] = project(),
                ["command"] = ToolDefinition.Str("Command to run."),
                ["reason"] = ToolDefinition.Str("Why, in Russian, shown in the confirmation dialog."),
            }, "project", "command");
        yield return Def("code_open", "Open the project result for the user: a file (an .html page opens in the browser) or the project folder.",
            new JsonObject { ["project"] = project(), ["path"] = ToolDefinition.Str("Optional file path; empty — open the folder.") }, "project");
        yield return Def("code_undo", "Undo the last change in a project (restores files as they were).", new JsonObject { ["project"] = project() }, "project");
        if (s.Agent != "none" && Host.AgentProblem() is null)
            yield return Def("code_delegate",
                "Hand a bigger coding task (a real app, many files, tests, refactoring) to the coding agent installed on this PC. " +
                "It works in the project folder and returns a summary. Use for anything beyond a small script or page.",
                new JsonObject
                {
                    ["project"] = project(),
                    ["task"] = ToolDefinition.Str("Detailed task for the agent, in the user's language, with all requirements."),
                }, "project", "task");
    }

    public async Task<ToolResult?> TryExecuteAsync(string name, JsonObject args, CancellationToken ct)
    {
        if (!IsCodingTool(name)) return null;
        if (!_settings().Coding.Enabled) return ToolResult.Error("Вайбкодинг выключен в настройках.", "💻 выключено");
        var project = args.Str("project") ?? "";
        switch (name)
        {
            case "code_projects":
            {
                var list = Workspace.Projects();
                return ToolResult.Ok(list.Count == 0 ? "Проектов пока нет." : "Проекты: " + string.Join(", ", list), "💻 проекты");
            }
            case "code_create_project":
            {
                var p = Workspace.Create(args.Str("name") ?? "");
                Activity?.Invoke(p, L10n.T("Создала проект"));
                return ToolResult.Ok($"Проект создан: «{p}» (папка {Workspace.ProjectDir(p)}).", "💻 " + p, "code");
            }
            case "code_list_files":
            {
                Need(project);
                var files = Workspace.Files(project);
                return ToolResult.Ok(files.Count == 0 ? "В проекте пока нет файлов." : string.Join("\n", files.Select(f => $"{f.Path} ({f.Size} B)")), "💻 файлы");
            }
            case "code_read_file":
            {
                Need(project);
                var path = args.Str("path") ?? "";
                return ToolResult.Ok(Workspace.Read(project, path), "📄 " + path);
            }
            case "code_write_file":
            {
                Need(project);
                var path = args.Str("path") ?? "";
                var summary = args.Str("summary") ?? L10n.F("Записала {0}", path);
                Workspace.Write(project, path, args.Str("content") ?? "", summary);
                Activity?.Invoke(project, summary);
                return ToolResult.Ok($"Записан файл {path}.", "✏️ " + path, "code");
            }
            case "code_replace":
            {
                Need(project);
                var path = args.Str("path") ?? "";
                var summary = args.Str("summary") ?? L10n.F("Поправила {0}", path);
                Workspace.Replace(project, path, args.Str("old") ?? "", args.Str("new") ?? "", summary);
                Activity?.Invoke(project, summary);
                return ToolResult.Ok($"Исправлен файл {path}.", "✏️ " + path, "code");
            }
            case "code_run":
            {
                Need(project);
                var cmd = args.Str("command") ?? "";
                if (cmd.Trim().Length == 0) return ToolResult.Error("Пустая команда.");
                Activity?.Invoke(project, L10n.T("Запускаю: ") + cmd);
                var (code, output) = await Host.RunAsync(Workspace.ProjectDir(project), cmd, args.Str("reason") ?? "", ct).ConfigureAwait(false);
                var tail = Tail(output);
                Activity?.Invoke(project, code == 0 ? L10n.T("Команда выполнена") : L10n.F("Команда завершилась с ошибкой ({0})", code));
                return new ToolResult { Text = $"Код выхода: {code}\n{tail}", Label = "▶ " + cmd, IsError = false, Kind = "code" };
            }
            case "code_open":
            {
                Need(project);
                var path = args.Str("path");
                Host.Open(string.IsNullOrWhiteSpace(path) ? Workspace.ProjectDir(project) : Workspace.Resolve(project, path));
                return ToolResult.Ok("Открыто.", "📂 " + (string.IsNullOrWhiteSpace(path) ? project : path));
            }
            case "code_undo":
            {
                Need(project);
                var t = Workspace.Undo(project);
                if (t is null) return ToolResult.Ok("Откатывать нечего — история пуста.", "↩️ нечего");
                Activity?.Invoke(project, L10n.T("Откатила: ") + t);
                return ToolResult.Ok($"Откачен шаг: «{t}».", "↩️ " + t, "code");
            }
            case "code_delegate":
            {
                Need(project);
                if (Host.AgentProblem() is { } why) return ToolResult.Error("Кодинг-агент недоступен: " + why);
                var task = args.Str("task") ?? "";
                Workspace.SnapshotAll(project, L10n.T("Перед задачей агента: ") + Short(task));
                Activity?.Invoke(project, L10n.T("Агент взялся за задачу"));
                var result = await Host.RunAgentAsync(Workspace.ProjectDir(project), task, step => Activity?.Invoke(project, step), ct).ConfigureAwait(false);
                Activity?.Invoke(project, L10n.T("Агент закончил"));
                return ToolResult.Ok("Итог работы агента:\n" + Tail(result), "🤖 " + Short(task), "code");
            }
            default:
                return ToolResult.Error("Неизвестный инструмент " + name);
        }
    }

    private void Need(string project)
    {
        if (string.IsNullOrWhiteSpace(project)) throw new ArgumentException("Не указан project. Вызови code_projects или code_create_project.");
        if (!Workspace.Exists(project)) throw new ArgumentException($"Проекта «{project}» нет. Создай его через code_create_project.");
    }

    /// <summary>Хвост длинного вывода (модели нужен конец — там ошибки).</summary>
    public static string Tail(string? s)
    {
        s ??= "";
        return s.Length <= OutputLimit ? s : "…(начало обрезано)\n" + s[^OutputLimit..];
    }

    private static string Short(string s)
    {
        s = (s ?? "").Replace('\n', ' ').Trim();
        return s.Length > 60 ? s[..60] + "…" : s;
    }
}

/// <summary>Кодинг-агент Claude Code: аргументы запуска и разбор потока событий (--output-format stream-json).</summary>
public static class ClaudeCodeAgent
{
    /// <summary>
    /// Аргументы для «claude -p»: правки файлов — без вопросов; команды — только если разрешено в настройках.
    /// Сама задача идёт через стандартный ввод — так её текст не ломается кавычками и спецсимволами командной строки.
    /// </summary>
    public static IReadOnlyList<string> Args(bool allowCommands)
    {
        var a = new List<string> { "-p", "--output-format", "stream-json", "--verbose", "--permission-mode", "acceptEdits" };
        if (allowCommands) a.AddRange(new[] { "--allowedTools", "Bash" });
        return a;
    }

    /// <summary>Одна строка потока → короткое описание шага (null — неинтересно) или итог (IsResult).</summary>
    public static (string? Step, string? Result, bool IsError) Parse(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return (null, null, false);
        JsonNode? j;
        try { j = JsonNode.Parse(line); } catch (JsonException) { return (null, null, false); }
        var type = j?["type"]?.GetValue<string>();
        if (type == "result")
        {
            var isError = j?["is_error"]?.GetValue<bool>() ?? false;
            return (null, j?["result"]?.GetValue<string>() ?? "", isError);
        }
        if (type != "assistant") return (null, null, false);
        var content = j?["message"]?["content"]?.AsArray();
        if (content is null) return (null, null, false);
        foreach (var c in content)
        {
            if (c?["type"]?.GetValue<string>() != "tool_use") continue;
            var name = c["name"]?.GetValue<string>() ?? "";
            var input = c["input"];
            string? Arg(string k) => input?[k]?.GetValueKind() == JsonValueKind.String ? input[k]!.GetValue<string>() : null;
            var file = Arg("file_path") ?? Arg("path") ?? Arg("notebook_path");
            var shortFile = file is null ? "" : Path.GetFileName(file);
            return (name switch
            {
                "Write" => L10n.T("пишет ") + shortFile,
                "Edit" or "MultiEdit" => L10n.T("правит ") + shortFile,
                "Read" => L10n.T("читает ") + shortFile,
                "Bash" => L10n.T("запускает ") + Trim(Arg("command")),
                "Glob" or "Grep" or "LS" => L10n.T("ищет по проекту"),
                "TodoWrite" => L10n.T("составляет план"),
                "WebFetch" or "WebSearch" => L10n.T("ищет в интернете"),
                _ => name,
            }, null, false);
        }
        return (null, null, false);
    }

    private static string Trim(string? s)
    {
        s = (s ?? "").Replace('\n', ' ').Trim();
        return s.Length > 50 ? s[..50] + "…" : s;
    }
}
