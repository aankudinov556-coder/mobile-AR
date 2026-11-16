using System.Text;
using System.Text.Json;

namespace Vera.Core.Coding;

/// <summary>Один шаг истории проекта: что меняли и прежнее содержимое файлов (для отката).</summary>
public sealed class HistoryStep
{
    public int Id { get; set; }
    public DateTimeOffset At { get; set; } = DateTimeOffset.Now;
    public string Title { get; set; } = "";
    /// <summary>Прежние версии: путь → имя файла копии в .vera/history (null — файла раньше не было).</summary>
    public Dictionary<string, string?> Before { get; set; } = new();
}

/// <summary>
/// Папка «Проекты Веры»: у каждого проекта своя подпапка. Вера читает и меняет файлы ТОЛЬКО внутри неё —
/// пути проверяются (никаких «..», абсолютных путей и ссылок наружу). Каждое изменение — шаг истории с откатом.
/// </summary>
public sealed class ProjectWorkspace
{
    public const long MaxReadBytes = 256 * 1024;
    public const long MaxWriteBytes = 2 * 1024 * 1024;
    private const string Meta = ".vera";
    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".vera", ".git", "node_modules", "bin", "obj", ".venv", "venv", "__pycache__", "dist", "build", ".idea", ".vs",
    };

    public ProjectWorkspace(string root)
    {
        Root = Path.GetFullPath(root);
    }

    public string Root { get; }

    public event Action<string>? Changed;

    // ================================================================== проекты

    public IReadOnlyList<string> Projects()
    {
        if (!Directory.Exists(Root)) return Array.Empty<string>();
        return new DirectoryInfo(Root).EnumerateDirectories()
            .Where(d => !d.Name.StartsWith('.'))
            .OrderByDescending(d => d.LastWriteTimeUtc)
            .Select(d => d.Name).ToList();
    }

    /// <summary>Имя папки проекта из того, как его назвали («Мой таймер!» → «Мой таймер»).</summary>
    public static string SafeName(string? name)
    {
        var bad = Path.GetInvalidFileNameChars().Concat(new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' }).ToHashSet();
        var s = new string((name ?? "").Where(c => !bad.Contains(c) && !char.IsControl(c)).ToArray()).Trim().Trim('.');
        if (s.Length > 60) s = s[..60].Trim();
        return s.Length == 0 ? "Проект" : s;
    }

    /// <summary>Создать проект (или вернуть существующий с тем же именем).</summary>
    public string Create(string name)
    {
        var safe = SafeName(name);
        Directory.CreateDirectory(ProjectDir(safe));
        Changed?.Invoke(safe);
        return safe;
    }

    public bool Exists(string project) => Directory.Exists(ProjectDir(project));

    public string ProjectDir(string project)
    {
        var safe = SafeName(project);
        var dir = Path.GetFullPath(Path.Combine(Root, safe));
        if (!Inside(dir, Root) || dir.Length == Root.Length) throw new UnauthorizedAccessException(L10n.T("Можно работать только внутри папки «Проекты Веры»."));
        return dir;
    }

    /// <summary>Полный путь файла проекта; всё, что ведёт наружу, — ошибка.</summary>
    public string Resolve(string project, string relative)
    {
        var dir = ProjectDir(project);
        var rel = (relative ?? "").Replace('\\', '/').Trim();
        if (rel.Length == 0) throw new ArgumentException(L10n.T("Не указан путь к файлу."));
        if (Path.IsPathRooted(rel) || rel.Contains(':')) throw new UnauthorizedAccessException(L10n.T("Путь должен быть внутри проекта, без диска и «/» в начале."));
        var full = Path.GetFullPath(Path.Combine(dir, rel));
        if (!Inside(full, dir)) throw new UnauthorizedAccessException(L10n.T("Путь выходит за пределы проекта."));
        if (full.Split(Path.DirectorySeparatorChar).Any(p => p.Equals(Meta, StringComparison.OrdinalIgnoreCase)))
            throw new UnauthorizedAccessException(L10n.T("Служебную папку .vera трогать нельзя."));
        // ссылка (symlink/junction) на чужую папку по пути — тоже наружу
        for (var d = Path.GetDirectoryName(full); d is not null && d.Length > dir.Length; d = Path.GetDirectoryName(d))
            if (Directory.Exists(d) && new DirectoryInfo(d).LinkTarget is not null) throw new UnauthorizedAccessException(L10n.T("Путь проходит через ссылку на другую папку."));
        return full;
    }

    private static bool Inside(string path, string dir)
    {
        var d = dir.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return path.StartsWith(d, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            || string.Equals(path, dir.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
    }

    // ================================================================== файлы

    /// <summary>Файлы проекта (без служебных и тяжёлых папок), не больше max.</summary>
    public IReadOnlyList<(string Path, long Size)> Files(string project, int max = 300)
    {
        var dir = ProjectDir(project);
        var list = new List<(string, long)>();
        if (!Directory.Exists(dir)) return list;
        var stack = new Stack<string>();
        stack.Push(dir);
        while (stack.Count > 0 && list.Count < max)
        {
            var d = stack.Pop();
            foreach (var f in Directory.EnumerateFiles(d).OrderBy(x => x))
            {
                if (list.Count >= max) break;
                list.Add((Path.GetRelativePath(dir, f).Replace('\\', '/'), new FileInfo(f).Length));
            }
            foreach (var sub in Directory.EnumerateDirectories(d).OrderByDescending(x => x))
            {
                var info = new DirectoryInfo(sub);
                if (SkipDirs.Contains(info.Name) || info.LinkTarget is not null) continue;
                stack.Push(sub);
            }
        }
        return list;
    }

    public string Read(string project, string relative)
    {
        var full = Resolve(project, relative);
        if (!File.Exists(full)) throw new FileNotFoundException(L10n.F("Файла «{0}» нет в проекте.", relative));
        var info = new FileInfo(full);
        if (info.Length > MaxReadBytes) throw new InvalidOperationException(L10n.F("Файл «{0}» слишком большой ({1} КБ) — прочитать можно до 256 КБ.", relative, info.Length / 1024));
        return File.ReadAllText(full);
    }

    /// <summary>Записать файл целиком (создаёт папки). Прежняя версия — в историю.</summary>
    public void Write(string project, string relative, string content, string title)
    {
        if (Encoding.UTF8.GetByteCount(content ?? "") > MaxWriteBytes) throw new InvalidOperationException(L10n.T("Файл слишком большой — до 2 МБ."));
        var full = Resolve(project, relative);
        var step = Begin(project, title);
        Snapshot(project, step, relative, full);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content ?? "", new UTF8Encoding(false));
        Commit(project, step);
    }

    /// <summary>Заменить кусок текста: old должен встретиться ровно один раз.</summary>
    public void Replace(string project, string relative, string oldText, string newText, string title)
    {
        if (string.IsNullOrEmpty(oldText)) throw new ArgumentException(L10n.T("Пустой фрагмент для замены."));
        var text = Read(project, relative);
        var at = text.IndexOf(oldText, StringComparison.Ordinal);
        if (at < 0) throw new InvalidOperationException(L10n.T("Фрагмент не найден — прочитай файл заново и скопируй кусок точно."));
        if (text.IndexOf(oldText, at + oldText.Length, StringComparison.Ordinal) >= 0)
            throw new InvalidOperationException(L10n.T("Фрагмент встречается несколько раз — возьми кусок побольше, чтобы он был единственным."));
        Write(project, relative, text[..at] + newText + text[(at + oldText.Length)..], title);
    }

    public void Delete(string project, string relative, string title)
    {
        var full = Resolve(project, relative);
        if (!File.Exists(full)) return;
        var step = Begin(project, title);
        Snapshot(project, step, relative, full);
        File.Delete(full);
        Commit(project, step);
    }

    // ================================================================== история

    private string HistoryDir(string project) => Path.Combine(ProjectDir(project), Meta, "history");
    private string JournalPath(string project) => Path.Combine(ProjectDir(project), Meta, "journal.json");

    public List<HistoryStep> History(string project)
    {
        try
        {
            var p = JournalPath(project);
            return File.Exists(p) ? JsonSerializer.Deserialize<List<HistoryStep>>(File.ReadAllText(p), VeraJson.Options) ?? new() : new();
        }
        catch (Exception)
        {
            return new();
        }
    }

    private void SaveHistory(string project, List<HistoryStep> h)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(JournalPath(project))!);
        File.WriteAllText(JournalPath(project), JsonSerializer.Serialize(h, VeraJson.Options));
    }

    private HistoryStep Begin(string project, string title)
    {
        var h = History(project);
        return new HistoryStep { Id = h.Count == 0 ? 1 : h.Max(s => s.Id) + 1, Title = string.IsNullOrWhiteSpace(title) ? L10n.T("Изменение") : title.Trim() };
    }

    private void Snapshot(string project, HistoryStep step, string relative, string full)
    {
        var key = relative.Replace('\\', '/');
        if (step.Before.ContainsKey(key)) return;
        if (!File.Exists(full)) { step.Before[key] = null; return; }
        var dir = Path.Combine(HistoryDir(project), step.Id.ToString());
        Directory.CreateDirectory(dir);
        var copy = (step.Before.Count + 1) + ".bak";
        File.Copy(full, Path.Combine(dir, copy), overwrite: true);
        step.Before[key] = copy;
    }

    private void Commit(string project, HistoryStep step)
    {
        var h = History(project);
        h.Add(step);
        // храним последние 200 шагов
        while (h.Count > 200)
        {
            var old = h[0];
            h.RemoveAt(0);
            try { Directory.Delete(Path.Combine(HistoryDir(project), old.Id.ToString()), true); } catch (Exception) { }
        }
        SaveHistory(project, h);
        Changed?.Invoke(project);
    }

    /// <summary>
    /// Снимок нескольких файлов одним шагом (перед работой кодинг-агента): потом агент меняет что хочет,
    /// а «откатить» вернёт всё как было. Новые файлы, созданные после снимка, откат удалит.
    /// </summary>
    public int SnapshotAll(string project, string title)
    {
        var step = Begin(project, title);
        var files = Files(project, 5001);
        foreach (var (rel, size) in files.Take(5000))
        {
            if (size > MaxWriteBytes) { step.Before[rel] = ""; continue; } // большой файл: не копируем, но и не трогаем при откате
            Snapshot(project, step, rel, Resolve(project, rel));
        }
        // признак полного снимка: файлы, появившиеся после него, откат удалит (если проект не огромный)
        if (files.Count <= 5000) step.Before["*"] = null;
        Commit(project, step);
        return step.Id;
    }

    /// <summary>Откатить последний шаг. Возвращает его название (null — откатывать нечего).</summary>
    public string? Undo(string project)
    {
        var h = History(project);
        if (h.Count == 0) return null;
        var step = h[^1];
        var dir = Path.Combine(HistoryDir(project), step.Id.ToString());
        var full = step.Before.ContainsKey("*");
        foreach (var (rel, copy) in step.Before)
        {
            if (rel == "*" || copy == "") continue;
            var target = Resolve(project, rel);
            if (copy is null)
            {
                if (File.Exists(target)) File.Delete(target);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(Path.Combine(dir, copy), target, overwrite: true);
            }
        }
        if (full)
            foreach (var (rel, _) in Files(project, 5000))
                if (!step.Before.ContainsKey(rel))
                    File.Delete(Resolve(project, rel));
        h.RemoveAt(h.Count - 1);
        SaveHistory(project, h);
        try { Directory.Delete(dir, true); } catch (Exception) { }
        Changed?.Invoke(project);
        return step.Title;
    }
}
