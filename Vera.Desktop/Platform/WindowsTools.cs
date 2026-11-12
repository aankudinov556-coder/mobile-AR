using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using Vera.Core;

namespace Vera.Desktop.Platform;

/// <summary>То, что инструментам нужно от интерфейса (окна, буфер обмена, подтверждения).</summary>
public interface IToolHost
{
    VeraSettings Settings { get; }
    /// <summary>Снимок экрана под курсором; окна Веры на время снимка прячутся.</summary>
    Task<ScreenShot> CaptureScreenAsync();
    /// <summary>Спросить пользователя, можно ли выполнить команду.</summary>
    Task<bool> ConfirmCommandAsync(string command, string? reason);
    Task<string?> ReadClipboardAsync();
    Task WriteClipboardAsync(string text);
}

/// <summary>Инструменты управления ПК (перенос src/main/tools.js).</summary>
public sealed partial class WindowsTools : IPlatformTools
{
    private readonly IToolHost _host;

    public WindowsTools(IToolHost host) => _host = host;

    // ================================================================== definitions

    private static ToolDefinition Def(string name, string description, JsonObject parameters) =>
        new() { Name = name, Description = description, Parameters = parameters };

    public IEnumerable<ToolDefinition> GetDefinitions(VeraSettings settings)
    {
        var caps = settings.Capabilities;
        yield return Def("get_system_info", "Get info about the user's PC: OS, CPU load, RAM usage, uptime, battery.",
            ToolDefinition.Schema(new JsonObject()));

        if (caps.PcControl)
        {
            yield return Def("open_url", "Open a URL in the default browser (http/https), or a special link (mailto:, tg:, steam:, ms-settings:).",
                ToolDefinition.Schema(new JsonObject { ["url"] = ToolDefinition.Str("The URL to open.") }, "url"));
            yield return Def("search_web", "Open a search results page in the user's browser (for the user to look at).",
                ToolDefinition.Schema(new JsonObject
                {
                    ["query"] = ToolDefinition.Str("Search query."),
                    ["engine"] = ToolDefinition.Enum("Search engine (default google).", "google", "yandex", "youtube", "wikipedia", "bing", "duckduckgo"),
                }, "query"));
            yield return Def("open_app",
                "Launch an application on the user's PC by name. Prefer the name as shown in the Windows Start menu, e.g. \"Telegram\", " +
                "\"Google Chrome\", \"Steam\", \"Visual Studio Code\", \"Блокнот\", \"Калькулятор\".",
                ToolDefinition.Schema(new JsonObject { ["name"] = ToolDefinition.Str("Application name.") }, "name"));
            yield return Def("open_folder",
                "Open a folder or file in Explorer. Accepts an absolute path or an alias: desktop, downloads, documents, pictures, music, videos, home.",
                ToolDefinition.Schema(new JsonObject { ["path"] = ToolDefinition.Str("Absolute path or alias.") }, "path"));
            yield return Def("media_control", "Control media playback and system volume.",
                ToolDefinition.Schema(new JsonObject
                {
                    ["action"] = ToolDefinition.Enum("What to do.", "play_pause", "next", "previous", "volume_up", "volume_down", "mute", "set_volume"),
                    ["level"] = ToolDefinition.Num("Volume 0-100 for set_volume; step count for volume_up/down (default 5 steps = 10%)."),
                }, "action"));
            yield return Def("system_action", "System actions: lock the PC, minimize all windows (show desktop), open task manager, open Windows settings.",
                ToolDefinition.Schema(new JsonObject
                {
                    ["action"] = ToolDefinition.Enum("Action.", "lock", "show_desktop", "task_manager", "settings"),
                }, "action"));
        }
        if (caps.Clipboard)
        {
            yield return Def("clipboard_read", "Read the text currently in the user's clipboard.", ToolDefinition.Schema(new JsonObject()));
            yield return Def("clipboard_write", "Put text into the user's clipboard (e.g. a translated text or code the user asked for).",
                ToolDefinition.Schema(new JsonObject { ["text"] = ToolDefinition.Str("Text to copy.") }, "text"));
        }
        if (caps.Screenshots)
        {
            yield return Def("take_screenshot",
                "Take a screenshot of the user's screen to see what they are looking at. Use when the user asks about something on the screen " +
                "(\"что у меня на экране\", \"посмотри\", \"помоги с этой ошибкой\").",
                ToolDefinition.Schema(new JsonObject()));
        }
        if (caps.RunCommands)
        {
            yield return Def("run_command",
                "Run a PowerShell command on the user's PC and return its output. The user must confirm every command. " +
                "Use only when really needed; never destructive commands.",
                ToolDefinition.Schema(new JsonObject
                {
                    ["command"] = ToolDefinition.Str("PowerShell command."),
                    ["reason"] = ToolDefinition.Str("Short explanation in Russian shown to the user in the confirmation dialog."),
                }, "command"));
        }
    }

    // ================================================================== execute

    private static ToolResult Disabled(string name) =>
        ToolResult.Error("Эта возможность выключена в настройках Веры (Настройки → Что Вере можно делать).", "⛔ " + name);

    public async Task<ToolResult?> TryExecuteAsync(string name, JsonObject args, CancellationToken ct)
    {
        var caps = _host.Settings.Capabilities;
        switch (name)
        {
            case "get_system_info": return await SystemInfoAsync(ct).ConfigureAwait(false);
            case "open_url": return caps.PcControl ? OpenUrl(args) : Disabled(name);
            case "search_web": return caps.PcControl ? SearchWeb(args) : Disabled(name);
            case "open_app": return caps.PcControl ? await OpenAppAsync(args, ct).ConfigureAwait(false) : Disabled(name);
            case "open_folder": return caps.PcControl ? OpenFolder(args) : Disabled(name);
            case "media_control": return caps.PcControl ? await MediaControlAsync(args, ct).ConfigureAwait(false) : Disabled(name);
            case "system_action": return caps.PcControl ? SystemAction(args) : Disabled(name);
            case "clipboard_read":
                if (!caps.Clipboard) return Disabled(name);
                {
                    var t = await _host.ReadClipboardAsync().ConfigureAwait(false);
                    return ToolResult.Ok(string.IsNullOrEmpty(t) ? "(буфер обмена пуст или содержит не текст)" : RuText.Truncate(t, 8000), "📋 прочитан буфер");
                }
            case "clipboard_write":
                if (!caps.Clipboard) return Disabled(name);
                await _host.WriteClipboardAsync(args.Str("text") ?? "").ConfigureAwait(false);
                return ToolResult.Ok("Текст скопирован в буфер обмена.", "📋 скопировано");
            case "take_screenshot":
            {
                if (!caps.Screenshots) return Disabled(name);
                var shot = await _host.CaptureScreenAsync().ConfigureAwait(false);
                return new ToolResult
                {
                    Text = $"Скриншот экрана {shot.Width}×{shot.Height} ({RuText.LocalIso(DateTimeOffset.Now)}).",
                    Images = new[] { new ImageData(ScreenShot.Mime, shot.Base64) },
                    Label = "📸 скриншот экрана",
                };
            }
            case "run_command": return caps.RunCommands ? await RunCommandAsync(args, ct).ConfigureAwait(false) : Disabled(name);
            default: return null;
        }
    }

    // ================================================================== helpers

    private static void ShellOpen(string target, string? arguments = null)
    {
        var psi = new ProcessStartInfo(target) { UseShellExecute = true };
        if (!string.IsNullOrEmpty(arguments)) psi.Arguments = arguments;
        using var _ = Process.Start(psi);
    }

    [GeneratedRegex(@"[^a-zа-я0-9+#]+")] private static partial Regex NonWord();
    [GeneratedRegex(@"\.(lnk|url|exe|appref-ms)$", RegexOptions.IgnoreCase)] private static partial Regex LaunchExt();
    [GeneratedRegex(@"uninstall|удален|деинстал", RegexOptions.IgnoreCase)] private static partial Regex UninstallRe();
    [GeneratedRegex(@"^[\w .\-а-яё]+$", RegexOptions.IgnoreCase)] private static partial Regex SafeName();
    [GeneratedRegex(@"^(https?:|mailto:|tg:|steam:|ms-settings:|spotify:|discord:)", RegexOptions.IgnoreCase)] private static partial Regex AllowedUrl();

    public static string Normalize(string? s) =>
        NonWord().Replace((s ?? "").ToLowerInvariant().Replace('ё', 'е'), " ").Trim();

    public static double Score(string query, string name)
    {
        var q = Normalize(query);
        var n = Normalize(name);
        if (q.Length == 0 || n.Length == 0) return 0;
        if (n == q) return 100;
        if (n.StartsWith(q, StringComparison.Ordinal)) return 80;
        if (n.Contains(q, StringComparison.Ordinal)) return 60;
        var qw = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var hits = qw.Count(w => w.Length > 1 && n.Contains(w, StringComparison.Ordinal));
        return hits > 0 ? 20 + 30 * ((double)hits / qw.Length) : 0;
    }

    // ================================================================== info

    private static string CpuName()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return (k?.GetValue("ProcessorNameString") as string)?.Trim() ?? "?";
        }
        catch (Exception) { return "?"; }
    }

    private static string OsName()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var product = k?.GetValue("ProductName") as string ?? "Windows";
            var display = k?.GetValue("DisplayVersion") as string ?? k?.GetValue("ReleaseId") as string;
            var build = Environment.OSVersion.Version.Build;
            // в реестре Windows 11 до сих пор называется «Windows 10»
            if (build >= 22000) product = product.Replace("Windows 10", "Windows 11");
            return $"{product}{(string.IsNullOrEmpty(display) ? "" : " " + display)} (сборка {build}, {RuntimeInformation.OSArchitecture})";
        }
        catch (Exception)
        {
            return RuntimeInformation.OSDescription;
        }
    }

    private static async Task<ToolResult> SystemInfoAsync(CancellationToken ct)
    {
        Native.GetSystemTimes(out var idle1, out var kernel1, out var user1);
        await Task.Delay(400, ct).ConfigureAwait(false);
        Native.GetSystemTimes(out var idle2, out var kernel2, out var user2);
        var total = (kernel2 - kernel1) + (user2 - user1); // kernel включает idle
        var cpu = total > 0 ? (int)Math.Round(100.0 * (1 - (double)(idle2 - idle1) / total)) : 0;
        cpu = Math.Clamp(cpu, 0, 100);

        var mem = new Native.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<Native.MEMORYSTATUSEX>() };
        Native.GlobalMemoryStatusEx(ref mem);
        var totalMem = (double)mem.ullTotalPhys;
        var used = totalMem - mem.ullAvailPhys;
        static string Gb(double x) => (x / (1024.0 * 1024 * 1024)).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
        var memPct = totalMem > 0 ? (int)Math.Round(used / totalMem * 100) : 0;
        var up = TimeSpan.FromMilliseconds(Environment.TickCount64);

        var lines = new List<string>
        {
            $"ОС: {OsName()}, имя ПК: {Environment.MachineName}",
            $"CPU: {CpuName()} ×{Environment.ProcessorCount}, загрузка {cpu}%",
            $"ОЗУ: занято {Gb(used)} из {Gb(totalMem)} ГБ ({memPct}%)",
            $"Аптайм: {(int)up.TotalHours} ч {up.Minutes} мин",
            $"Время: {RuText.FormatDateTime(DateTimeOffset.Now)}",
        };
        if (Native.GetSystemPowerStatus(out var ps) && ps.BatteryFlag != 128 && ps.BatteryFlag != 255 && ps.BatteryLifePercent <= 100)
        {
            var state = (ps.BatteryFlag & 8) != 0 ? ", заряжается" : ps.ACLineStatus == 1 ? ", от сети" : "";
            lines.Add($"Батарея: {ps.BatteryLifePercent}%{state}");
        }
        return ToolResult.Ok(string.Join("\n", lines), $"🖥️ CPU {cpu}% · RAM {memPct}%");
    }

    // ================================================================== browser

    private static ToolResult OpenUrl(JsonObject a)
    {
        var url = (a.Str("url") ?? "").Trim();
        if (!AllowedUrl().IsMatch(url))
            throw new ArgumentException("Разрешены только ссылки http(s), mailto:, tg:, steam:, ms-settings:, spotify:, discord:");
        ShellOpen(url);
        var shown = Regex.Replace(url, "^https?://", "", RegexOptions.IgnoreCase);
        if (shown.Length > 60) shown = shown[..60];
        return ToolResult.Ok($"Открыто: {url}", $"🌐 {shown}");
    }

    private static ToolResult SearchWeb(JsonObject a)
    {
        var query = a.Str("query") ?? "";
        var q = Uri.EscapeDataString(query);
        var engine = (a.Str("engine") ?? "google").ToLowerInvariant();
        var url = engine switch
        {
            "yandex" => $"https://yandex.ru/search/?text={q}",
            "youtube" => $"https://www.youtube.com/results?search_query={q}",
            "wikipedia" => $"https://ru.wikipedia.org/w/index.php?search={q}",
            "bing" => $"https://www.bing.com/search?q={q}",
            "duckduckgo" => $"https://duckduckgo.com/?q={q}",
            _ => $"https://www.google.com/search?q={q}",
        };
        if (engine is not ("yandex" or "youtube" or "wikipedia" or "bing" or "duckduckgo")) engine = "google";
        ShellOpen(url);
        return ToolResult.Ok($"Открыла поиск «{query}» ({engine}). Результатов я не вижу — только открыла страницу.", $"🔎 {query}");
    }

    // ================================================================== apps

    private static readonly Dictionary<string, string> AppAliases = new()
    {
        ["блокнот"] = "notepad", ["калькулятор"] = "calc", ["проводник"] = "explorer", ["пейнт"] = "mspaint", ["paint"] = "mspaint",
        ["диспетчер задач"] = "taskmgr", ["task manager"] = "taskmgr", ["командная строка"] = "cmd", ["терминал"] = "wt",
        ["terminal"] = "wt", ["параметры"] = "ms-settings:", ["настройки windows"] = "ms-settings:", ["settings"] = "ms-settings:",
        ["панель управления"] = "control", ["control panel"] = "control",
        ["телеграм"] = "telegram", ["телеграмм"] = "telegram", ["дискорд"] = "discord", ["стим"] = "steam", ["спотифай"] = "spotify",
        ["хром"] = "chrome", ["гугл хром"] = "chrome", ["файрфокс"] = "firefox", ["фаерфокс"] = "firefox", ["эдж"] = "edge",
        ["яндекс браузер"] = "yandex", ["яндекс"] = "yandex", ["ворд"] = "word", ["эксель"] = "excel", ["повер поинт"] = "powerpoint",
        ["вс код"] = "visual studio code", ["vscode"] = "visual studio code", ["vs code"] = "visual studio code", ["обс"] = "obs",
        ["вотсап"] = "whatsapp", ["ватсап"] = "whatsapp", ["зум"] = "zoom", ["скайп"] = "skype", ["опера"] = "opera",
    };

    private static readonly Dictionary<string, string> BuiltinExe = new()
    {
        ["notepad"] = "notepad.exe", ["calc"] = "calc.exe", ["explorer"] = "explorer.exe", ["mspaint"] = "mspaint.exe",
        ["taskmgr"] = "taskmgr.exe", ["cmd"] = "cmd.exe", ["powershell"] = "powershell.exe", ["wt"] = "wt.exe",
        ["control"] = "control.exe", ["ms-settings:"] = "ms-settings:",
    };

    private sealed record Launchable(string Name, string Path, bool IsStoreApp = false);

    private static readonly object CacheGate = new();
    private static List<Launchable>? _shortcuts;
    private static DateTime _shortcutsTime;
    private static List<Launchable>? _startApps;
    private static DateTime _startAppsTime;

    private static void Walk(string dir, int depth, List<Launchable> outList)
    {
        if (depth < 0 || string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
        try
        {
            foreach (var d in Directory.EnumerateDirectories(dir)) Walk(d, depth - 1, outList);
            foreach (var f in Directory.EnumerateFiles(dir))
            {
                var file = System.IO.Path.GetFileName(f);
                if (!LaunchExt().IsMatch(file) || UninstallRe().IsMatch(file)) continue;
                outList.Add(new Launchable(LaunchExt().Replace(file, ""), f));
            }
        }
        catch (Exception)
        {
            // нет доступа к папке — пропускаем
        }
    }

    private static List<Launchable> StartMenuShortcuts()
    {
        lock (CacheGate)
        {
            if (_shortcuts is not null && DateTime.UtcNow - _shortcutsTime < TimeSpan.FromMinutes(10)) return _shortcuts;
            var list = new List<Launchable>();
            foreach (var d in new[]
                     {
                         Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
                         Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                         Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                         Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
                     })
                Walk(d, 4, list);
            _shortcuts = list;
            _shortcutsTime = DateTime.UtcNow;
            return list;
        }
    }

    /// <summary>Приложения из меню «Пуск», включая приложения Microsoft Store (через Get-StartApps).</summary>
    private static async Task<List<Launchable>> StartAppsAsync(CancellationToken ct)
    {
        lock (CacheGate)
        {
            if (_startApps is not null && DateTime.UtcNow - _startAppsTime < TimeSpan.FromMinutes(10)) return _startApps;
        }
        var list = new List<Launchable>();
        try
        {
            var r = await PowerShell.RunAsync("Get-StartApps | ForEach-Object { $_.Name + '|' + $_.AppID }", TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
            foreach (var line in r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var i = line.LastIndexOf('|');
                if (i <= 0 || i == line.Length - 1) continue;
                var name = line[..i];
                if (UninstallRe().IsMatch(name)) continue;
                list.Add(new Launchable(name, line[(i + 1)..], true));
            }
        }
        catch (Exception)
        {
            // PowerShell недоступен — обойдёмся ярлыками
        }
        lock (CacheGate)
        {
            _startApps = list;
            _startAppsTime = DateTime.UtcNow;
        }
        return list;
    }

    private static bool TryLaunch(Launchable l)
    {
        try
        {
            if (l.IsStoreApp) ShellOpen("explorer.exe", "shell:AppsFolder\\" + l.Path);
            else ShellOpen(l.Path);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private async Task<ToolResult> OpenAppAsync(JsonObject a, CancellationToken ct)
    {
        var raw = (a.Str("name") ?? "").Trim();
        if (raw.Length == 0) throw new ArgumentException("Не указано имя приложения");
        var n = Normalize(raw);
        var alias = AppAliases.TryGetValue(n, out var al) ? al : n;
        double Best(string name) => Math.Max(Score(raw, name), Score(alias, name));

        // 1) пользовательские ярлыки из настроек
        var custom = _host.Settings.AppShortcuts
            .Where(s => !string.IsNullOrWhiteSpace(s.Path))
            .Select(s => (s, score: Best(s.Name)))
            .OrderByDescending(x => x.score)
            .FirstOrDefault();
        if (custom.s is not null && custom.score >= 60 && TryLaunch(new Launchable(custom.s.Name, custom.s.Path)))
            return ToolResult.Ok($"Запустила «{custom.s.Name}».", $"🚀 {custom.s.Name}");

        // 2) встроенные программы Windows
        if (BuiltinExe.TryGetValue(alias, out var exe))
        {
            ShellOpen(exe);
            return ToolResult.Ok($"Запустила {raw}.", $"🚀 {raw}");
        }

        // 3) ярлыки меню «Пуск» и рабочего стола
        var ranked = StartMenuShortcuts()
            .Select(s => (s, score: Best(s.Name)))
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score).ThenBy(x => x.s.Name.Length)
            .ToList();
        if (ranked.Count > 0 && ranked[0].score >= 50 && TryLaunch(ranked[0].s))
            return ToolResult.Ok($"Запустила «{ranked[0].s.Name}».", $"🚀 {ranked[0].s.Name}");

        // 4) приложения «Пуска», у которых нет ярлыка-файла (Microsoft Store и т.п.)
        var apps = (await StartAppsAsync(ct).ConfigureAwait(false))
            .Select(s => (s, score: Best(s.Name)))
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score).ThenBy(x => x.s.Name.Length)
            .ToList();
        if (apps.Count > 0 && apps[0].score >= 50 && TryLaunch(apps[0].s))
            return ToolResult.Ok($"Запустила «{apps[0].s.Name}».", $"🚀 {apps[0].s.Name}");

        // 5) последняя попытка — по имени через оболочку (App Paths, PATH)
        if (SafeName().IsMatch(raw))
        {
            try
            {
                ShellOpen(raw);
                return ToolResult.Ok($"Попробовала запустить «{raw}» через оболочку Windows.", $"🚀 {raw}");
            }
            catch (Win32Exception)
            {
                // не нашлось
            }
        }

        var sugg = ranked.Select(x => x.s.Name).Concat(apps.Select(x => x.s.Name)).Distinct().Take(5).ToList();
        return ToolResult.Error(
            $"Не нашла приложение «{raw}»." + (sugg.Count > 0 ? $" Похожие: {string.Join(", ", sugg)}." : "") +
            " Пользователь может добавить ярлык в Настройки → Мои программы.",
            $"🚀 не найдено: {raw}");
    }

    // ================================================================== folders

    private static string? FolderAlias(string p)
    {
        var key = Normalize(p) switch
        {
            "desktop" or "рабочий стол" or "рабочий" => "desktop",
            "downloads" or "загрузки" or "закачки" => "downloads",
            "documents" or "документы" or "мои документы" => "documents",
            "pictures" or "изображения" or "картинки" or "фото" => "pictures",
            "music" or "музыка" => "music",
            "videos" or "видео" => "videos",
            "home" or "домашняя" or "домашняя папка" => "home",
            _ => null,
        };
        return key switch
        {
            "desktop" => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            "downloads" => Native.KnownFolder(Native.FolderDownloads)
                           ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
            "documents" => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "pictures" => Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            "music" => Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            "videos" => Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            "home" => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            _ => null,
        };
    }

    private static ToolResult OpenFolder(JsonObject a)
    {
        var p = (a.Str("path") ?? "").Trim().Trim('"');
        if (p.Length == 0) throw new ArgumentException("Не указан путь");
        var target = FolderAlias(p) ?? Environment.ExpandEnvironmentVariables(p);
        if (!Directory.Exists(target) && !File.Exists(target)) throw new ArgumentException($"Путь не существует: {target}");
        ShellOpen(target);
        var name = System.IO.Path.GetFileName(target.TrimEnd('\\', '/'));
        return ToolResult.Ok($"Открыла: {target}", $"📁 {(string.IsNullOrEmpty(name) ? target : name)}");
    }

    // ================================================================== media

    private static AudioEndpointVolume? EndpointVolume(out MMDeviceEnumerator? enumerator, out MMDevice? device)
    {
        enumerator = null;
        device = null;
        try
        {
            enumerator = new MMDeviceEnumerator();
            device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return device.AudioEndpointVolume;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static int? CurrentVolume()
    {
        var vol = EndpointVolume(out var en, out var dev);
        try { return vol is null ? null : (int)Math.Round(vol.MasterVolumeLevelScalar * 100); }
        catch (Exception) { return null; }
        finally { dev?.Dispose(); en?.Dispose(); }
    }

    private static async Task<ToolResult> MediaControlAsync(JsonObject a, CancellationToken ct)
    {
        var action = a.Str("action") ?? "";
        if (action == "set_volume")
        {
            var level = (int)Math.Clamp(Math.Round(a.Num("level") ?? 50), 0, 100);
            var vol = EndpointVolume(out var en, out var dev);
            try
            {
                if (vol is not null)
                {
                    vol.MasterVolumeLevelScalar = level / 100f;
                    if (level > 0 && vol.Mute) vol.Mute = false;
                }
                else
                {
                    // запасной путь — клавишами громкости (шаг 2%)
                    for (var i = 0; i < 50; i++) Native.PressKey(0xAE);
                    for (var i = 0; i < level / 2; i++) Native.PressKey(0xAF);
                }
            }
            finally
            {
                dev?.Dispose();
                en?.Dispose();
            }
            return ToolResult.Ok($"Громкость установлена на {level}%.", $"🔊 громкость {level}%");
        }

        (byte vk, string label) = action switch
        {
            "play_pause" => ((byte)0xB3, "⏯️ пауза/play"),
            "next" => ((byte)0xB0, "⏭️ следующий трек"),
            "previous" => ((byte)0xB1, "⏮️ предыдущий трек"),
            "volume_up" => ((byte)0xAF, "🔊 громче"),
            "volume_down" => ((byte)0xAE, "🔉 тише"),
            "mute" => ((byte)0xAD, "🔇 звук"),
            _ => throw new ArgumentException("Неизвестное действие: " + action),
        };
        var times = action is "volume_up" or "volume_down" ? (int)Math.Clamp(Math.Round(a.Num("level") ?? 5), 1, 50) : 1;
        for (var i = 0; i < times; i++) Native.PressKey(vk);

        var text = $"Готово: {action}{(times > 1 ? " ×" + times : "")}.";
        if (action is "volume_up" or "volume_down" or "mute")
        {
            await Task.Delay(150, ct).ConfigureAwait(false);
            if (CurrentVolume() is { } now) text += $" Громкость сейчас ~{now}%.";
        }
        return ToolResult.Ok(text, label);
    }

    // ================================================================== system

    private static ToolResult SystemAction(JsonObject a)
    {
        switch (a.Str("action"))
        {
            case "lock":
                Native.LockWorkStation();
                return ToolResult.Ok("Компьютер заблокирован.", "🔒 блокировка");
            case "show_desktop":
                try
                {
                    var type = Type.GetTypeFromProgID("Shell.Application") ?? throw new InvalidOperationException();
                    var shell = Activator.CreateInstance(type)!;
                    try { type.InvokeMember("MinimizeAll", BindingFlags.InvokeMethod, null, shell, null); }
                    finally { Marshal.ReleaseComObject(shell); }
                }
                catch (Exception)
                {
                    // Win+D
                    Native.keybd_event(0x5B, 0, 0, UIntPtr.Zero);
                    Native.keybd_event(0x44, 0, 0, UIntPtr.Zero);
                    Native.keybd_event(0x44, 0, Native.KEYEVENTF_KEYUP, UIntPtr.Zero);
                    Native.keybd_event(0x5B, 0, Native.KEYEVENTF_KEYUP, UIntPtr.Zero);
                }
                return ToolResult.Ok("Все окна свёрнуты.", "🗔 рабочий стол");
            case "task_manager":
                ShellOpen("taskmgr.exe");
                return ToolResult.Ok("Открыт диспетчер задач.", "📊 диспетчер задач");
            case "settings":
                ShellOpen("ms-settings:");
                return ToolResult.Ok("Открыты параметры Windows.", "⚙️ параметры");
            default:
                throw new ArgumentException("Неизвестное действие");
        }
    }

    // ================================================================== commands

    private async Task<ToolResult> RunCommandAsync(JsonObject a, CancellationToken ct)
    {
        var settings = _host.Settings;
        if (!settings.Capabilities.RunCommands) throw new InvalidOperationException("Выполнение команд отключено в настройках");
        var command = (a.Str("command") ?? "").Trim();
        if (command.Length == 0) throw new ArgumentException("Пустая команда");
        if (settings.Capabilities.ConfirmCommands)
        {
            var ok = await _host.ConfirmCommandAsync(command, a.Str("reason")).ConfigureAwait(false);
            if (!ok) return ToolResult.Error("Пользователь отклонил выполнение команды.", "⛔ команда отклонена");
        }
        var r = await PowerShell.RunAsync(command, TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(r.StdOut)) parts.Add("STDOUT:\n" + RuText.Truncate(r.StdOut, 6000));
        if (!string.IsNullOrWhiteSpace(r.StdErr)) parts.Add("STDERR:\n" + RuText.Truncate(r.StdErr, 3000));
        if (r.TimedOut) parts.Add("(команда не уложилась в 60 секунд и была остановлена)");
        var label = command.Length > 50 ? command[..50] : command;
        return new ToolResult
        {
            Text = $"Код выхода: {r.ExitCode}\n{(parts.Count > 0 ? string.Join("\n\n", parts) : "(нет вывода)")}",
            IsError = r.ExitCode != 0,
            Label = $"💻 {label}",
        };
    }
}

/// <summary>Запуск PowerShell через -EncodedCommand (UTF-16LE base64) — без проблем с кавычками.</summary>
public static class PowerShell
{
    public sealed record Result(int ExitCode, string StdOut, string StdErr, bool TimedOut);

    public static async Task<Result> RunAsync(string script, TimeSpan timeout, CancellationToken ct)
    {
        var full = "[Console]::OutputEncoding=[System.Text.Encoding]::UTF8; $ProgressPreference='SilentlyContinue'; " + script;
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(full));
        var psi = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", encoded })
            psi.ArgumentList.Add(arg);

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Не удалось запустить PowerShell");
        p.StandardInput.Close();
        var outTask = p.StandardOutput.ReadToEndAsync(ct);
        var errTask = p.StandardError.ReadToEndAsync(ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var timedOut = false;
        try
        {
            await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timedOut = !ct.IsCancellationRequested;
            try { p.Kill(entireProcessTree: true); } catch (Exception) { /* уже завершился */ }
            if (!timedOut) throw;
        }
        string stdout = "", stderr = "";
        try { stdout = await outTask.ConfigureAwait(false); } catch (Exception) { }
        try { stderr = await errTask.ConfigureAwait(false); } catch (Exception) { }
        return new Result(timedOut ? -1 : p.ExitCode, stdout, stderr, timedOut);
    }
}
