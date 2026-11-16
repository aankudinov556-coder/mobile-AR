using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vera.Core;
using Vera.Core.Coding;

namespace Vera.Desktop.Platform;

/// <summary>
/// Вайбкодинг на ПК: команды в папке проекта (PowerShell, с подтверждением), открытие результата,
/// кодинг-агент Claude Code («claude -p», задача — через стандартный ввод, события — stream-json).
/// </summary>
public sealed class CodingHost : ICodingHost
{
    public static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(3);
    public static readonly TimeSpan AgentTimeout = TimeSpan.FromMinutes(30);

    private readonly Func<VeraSettings> _settings;
    private readonly Func<string, string?, Task<bool>> _confirm;

    /// <param name="confirm">(что, зачем) → разрешил ли пользователь</param>
    public CodingHost(Func<VeraSettings> settings, Func<string, string?, Task<bool>> confirm)
    {
        _settings = settings;
        _confirm = confirm;
    }

    /// <summary>Папка проектов: из настроек или Документы\Вера\Проекты.</summary>
    public static string DefaultFolder() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        L10n.English ? "Vera" : "Вера", L10n.English ? "Projects" : "Проекты");

    public static string FolderFor(VeraSettings s) => string.IsNullOrWhiteSpace(s.Coding.Folder) ? DefaultFolder() : s.Coding.Folder;

    public async Task<(int ExitCode, string Output)> RunAsync(string projectDir, string command, string reason, CancellationToken ct)
    {
        if (!await _confirm(command, string.IsNullOrWhiteSpace(reason) ? L10n.F("Команда в проекте «{0}»", Path.GetFileName(projectDir)) : reason).ConfigureAwait(false))
            return (-1, "Пользователь отклонил запуск команды.");
        var script = "Set-Location -LiteralPath '" + projectDir.Replace("'", "''") + "'; " + command;
        var r = await PowerShell.RunAsync(script, CommandTimeout, ct).ConfigureAwait(false);
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(r.StdOut)) sb.Append(r.StdOut.TrimEnd()).Append('\n');
        if (!string.IsNullOrWhiteSpace(r.StdErr)) sb.Append("STDERR:\n").Append(r.StdErr.TrimEnd()).Append('\n');
        if (r.TimedOut) sb.Append("(команда не уложилась в 3 минуты и была остановлена)");
        return (r.ExitCode, sb.Length == 0 ? "(нет вывода)" : sb.ToString());
    }

    public void Open(string path)
    {
        using var _ = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    // ================================================================== Claude Code

    public string? AgentProblem()
    {
        var s = _settings().Coding;
        if (s.Agent != "claude") return L10n.T("кодинг-агент не выбран в настройках");
        return FindClaude(s.AgentPath) is null
            ? L10n.T("Claude Code не найден. Установи его (claude.ai/code) или укажи путь в Настройки → Что ей можно → Вайбкодинг.")
            : null;
    }

    /// <summary>Путь к claude(.exe/.cmd): как указан, в PATH или в обычных местах установки.</summary>
    public static string? FindClaude(string? configured)
    {
        var name = string.IsNullOrWhiteSpace(configured) ? "claude" : configured.Trim().Trim('"');
        if (Path.IsPathRooted(name)) return File.Exists(name) ? name : null;
        var exts = OperatingSystem.IsWindows() ? new[] { ".exe", ".cmd", ".bat", "" } : new[] { "" };
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).ToList();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        dirs.Add(Path.Combine(home, ".local", "bin"));
        dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"));
        dirs.Add(Path.Combine(home, ".claude", "local"));
        foreach (var d in dirs)
            foreach (var e in exts)
            {
                try
                {
                    var p = Path.Combine(d.Trim('"'), name + e);
                    if (File.Exists(p)) return p;
                }
                catch (ArgumentException) { /* кривой элемент PATH */ }
            }
        return null;
    }

    /// <summary>Версия агента — для кнопки «Проверить» в настройках.</summary>
    public async Task<string> AgentVersionAsync(CancellationToken ct = default)
    {
        var exe = FindClaude(_settings().Coding.AgentPath) ?? throw new FileNotFoundException(AgentProblem());
        var psi = Start(exe, new[] { "--version" }, Environment.CurrentDirectory);
        psi.RedirectStandardInput = false;
        using var p = Process.Start(psi) ?? throw new InvalidOperationException(L10n.T("Не удалось запустить агента"));
        var output = await p.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
        await p.WaitForExitAsync(ct).ConfigureAwait(false);
        return output.Trim();
    }

    private static ProcessStartInfo Start(string exe, IEnumerable<string> args, string dir)
    {
        ProcessStartInfo psi;
        if (exe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || exe.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
        {
            // npm-установка — через cmd; аргументы фиксированные, без пользовательского текста
            psi = new ProcessStartInfo("cmd.exe") { ArgumentList = { "/d", "/s", "/c", exe } };
            foreach (var a in args) psi.ArgumentList.Add(a);
        }
        else
        {
            psi = new ProcessStartInfo(exe);
            foreach (var a in args) psi.ArgumentList.Add(a);
        }
        psi.WorkingDirectory = dir;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.RedirectStandardInput = true;
        psi.StandardOutputEncoding = Encoding.UTF8;
        psi.StandardErrorEncoding = Encoding.UTF8;
        return psi;
    }

    public async Task<string> RunAgentAsync(string projectDir, string task, Action<string> progress, CancellationToken ct)
    {
        var s = _settings().Coding;
        var exe = FindClaude(s.AgentPath) ?? throw new FileNotFoundException(AgentProblem());
        var what = L10n.F("Передать задачу агенту Claude Code?\nПапка: {0}", projectDir) +
                   (s.AgentCommands ? L10n.T("\nАгенту разрешено запускать команды.") : L10n.T("\nАгент только правит файлы, команды не запускает."));
        if (!await _confirm(task, what).ConfigureAwait(false)) return "Пользователь отменил задачу агенту.";

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(AgentTimeout);
        var psi = Start(exe, ClaudeCodeAgent.Args(s.AgentCommands), projectDir);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException(L10n.T("Не удалось запустить агента"));
        var stderr = new StringBuilder();
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (stderr) stderr.AppendLine(e.Data); };
        p.BeginErrorReadLine();
        await p.StandardInput.WriteAsync(task).ConfigureAwait(false);
        p.StandardInput.Close();

        string? result = null;
        var isError = false;
        try
        {
            while (await p.StandardOutput.ReadLineAsync(timeout.Token).ConfigureAwait(false) is { } line)
            {
                var (step, res, err) = ClaudeCodeAgent.Parse(line);
                if (step is not null) progress(step);
                if (res is not null) { result = res; isError = err; }
            }
            await p.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch (Exception) { }
            if (ct.IsCancellationRequested) throw;
            return "Агент не уложился в 30 минут и был остановлен. Часть изменений могла сохраниться — их можно откатить.";
        }
        if (result is null)
        {
            string err;
            lock (stderr) err = stderr.ToString().Trim();
            return L10n.F("Агент завершился без итога (код {0}). {1}", p.ExitCode, err.Length > 1500 ? err[^1500..] : err);
        }
        return isError ? "Агент сообщил об ошибке: " + result : result;
    }
}
