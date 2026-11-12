using System.Text.Json.Nodes;
using Android.Content;
using Android.Content.PM;
using Android.Hardware.Camera2;
using Android.OS;
using Vera.Core;
using AUri = Android.Net.Uri;
using ASettings = Android.Provider.Settings;

namespace Vera.Mobile.Platform;

/// <summary>Инструменты телефона: ссылки, поиск, приложения, звонилка, карты, «поделиться», буфер, фонарик, настройки.</summary>
public sealed class PhoneTools : IPlatformTools
{
    private static Context Ctx => Android.App.Application.Context;
    private static bool _torchOn;

    public IEnumerable<ToolDefinition> GetDefinitions(VeraSettings settings)
    {
        yield return new ToolDefinition
        {
            Name = "get_device_info",
            Description = "Get info about the user's phone: battery level and charging state, manufacturer/model, Android version, network, free storage.",
            Parameters = ToolDefinition.Schema(new JsonObject()),
        };
        if (settings.Capabilities.PcControl)
        {
            yield return new ToolDefinition
            {
                Name = "open_url",
                Description = "Open a link on the phone (http/https in the browser, or app links like tg:, mailto:, whatsapp:, intent-less deep links).",
                Parameters = ToolDefinition.Schema(new JsonObject { ["url"] = ToolDefinition.Str("Full URL, e.g. https://youtube.com") }, "url"),
            };
            yield return new ToolDefinition
            {
                Name = "search_web",
                Description = "Open a search results page in the phone's browser (for the user to look at; you do not see the results).",
                Parameters = ToolDefinition.Schema(new JsonObject
                {
                    ["query"] = ToolDefinition.Str("Search query."),
                    ["engine"] = ToolDefinition.Enum("Search engine. Default google.", "google", "yandex", "youtube", "wikipedia", "bing", "duckduckgo"),
                }, "query"),
            };
            yield return new ToolDefinition
            {
                Name = "open_app",
                Description = "Launch an installed app on the phone by its name as shown in the launcher, e.g. \"Telegram\", \"YouTube\", \"Камера\", \"Календарь\", \"Spotify\". " +
                              "If not found, the result lists installed apps so you can retry with the exact name.",
                Parameters = ToolDefinition.Schema(new JsonObject { ["name"] = ToolDefinition.Str("App name.") }, "name"),
            };
            yield return new ToolDefinition
            {
                Name = "dial_number",
                Description = "Open the phone dialer with a number filled in (the user presses call themselves).",
                Parameters = ToolDefinition.Schema(new JsonObject { ["number"] = ToolDefinition.Str("Phone number, e.g. +7 900 123-45-67") }, "number"),
            };
            yield return new ToolDefinition
            {
                Name = "open_maps",
                Description = "Open a place, address or route search in the maps app.",
                Parameters = ToolDefinition.Schema(new JsonObject
                {
                    ["query"] = ToolDefinition.Str("Place or address, e.g. \"кофейня рядом\" or \"Москва, Тверская 1\"."),
                    ["navigate"] = ToolDefinition.Enum("\"yes\" to start turn-by-turn navigation to the place. Default no.", "yes", "no"),
                }, "query"),
            };
            yield return new ToolDefinition
            {
                Name = "share_text",
                Description = "Open the Android share sheet with a text (to send it to a messenger, notes, email…).",
                Parameters = ToolDefinition.Schema(new JsonObject
                {
                    ["text"] = ToolDefinition.Str("Text to share."),
                    ["title"] = ToolDefinition.Str("Optional subject/title."),
                }, "text"),
            };
            yield return new ToolDefinition
            {
                Name = "flashlight",
                Description = "Turn the phone flashlight (torch) on or off.",
                Parameters = ToolDefinition.Schema(new JsonObject { ["state"] = ToolDefinition.Enum("on, off or toggle", "on", "off", "toggle") }, "state"),
            };
            yield return new ToolDefinition
            {
                Name = "open_settings",
                Description = "Open a system settings screen on the phone.",
                Parameters = ToolDefinition.Schema(new JsonObject
                {
                    ["section"] = ToolDefinition.Enum("Settings section.",
                        "wifi", "bluetooth", "sound", "display", "battery", "location", "mobile_data", "airplane", "nfc",
                        "notifications", "apps", "date", "language", "accessibility", "security", "storage", "app", "main"),
                }, "section"),
            };
        }
        if (settings.Capabilities.Clipboard)
        {
            yield return new ToolDefinition
            {
                Name = "clipboard_read",
                Description = "Read the text currently in the phone's clipboard.",
                Parameters = ToolDefinition.Schema(new JsonObject()),
            };
            yield return new ToolDefinition
            {
                Name = "clipboard_write",
                Description = "Put text into the phone's clipboard (e.g. a translation or code the user asked for).",
                Parameters = ToolDefinition.Schema(new JsonObject { ["text"] = ToolDefinition.Str("Text to copy.") }, "text"),
            };
        }
    }

    public async Task<ToolResult?> TryExecuteAsync(string name, JsonObject args, CancellationToken ct)
    {
        switch (name)
        {
            case "get_device_info": return DeviceInfoText();
            case "open_url": return OpenUrl(args.Str("url") ?? "");
            case "search_web": return SearchWeb(args.Str("query") ?? "", args.Str("engine"));
            case "open_app": return OpenApp(args.Str("name") ?? "");
            case "dial_number": return Dial(args.Str("number") ?? "");
            case "open_maps": return OpenMaps(args.Str("query") ?? "", args.Str("navigate") == "yes");
            case "share_text": return Share(args.Str("text") ?? "", args.Str("title"));
            case "flashlight": return Flashlight(args.Str("state") ?? "toggle");
            case "open_settings": return OpenSettings(args.Str("section") ?? "main");
            case "clipboard_read":
            {
                var text = await MainThread.InvokeOnMainThreadAsync(async () => await Clipboard.Default.GetTextAsync());
                if (string.IsNullOrEmpty(text)) return ToolResult.Ok("Буфер обмена пуст (или Android не дал его прочитать).", "📋 буфер пуст");
                return ToolResult.Ok("Текст в буфере обмена:\n" + RuText.Truncate(text, 8000), "📋 прочитала буфер");
            }
            case "clipboard_write":
            {
                var text = args.Str("text") ?? "";
                await MainThread.InvokeOnMainThreadAsync(async () => await Clipboard.Default.SetTextAsync(text));
                return ToolResult.Ok($"Скопировано в буфер обмена ({text.Length} симв.).", "📋 скопировала");
            }
        }
        return null;
    }

    // ------------------------------------------------------------------ helpers

    private static void Start(Intent intent, string? chooserTitle = null)
    {
        var activity = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity;
        var i = chooserTitle is null ? intent : Intent.CreateChooser(intent, chooserTitle)!;
        if (activity is null) i.AddFlags(ActivityFlags.NewTask);
        MainThread.BeginInvokeOnMainThread(() =>
        {
            try
            {
                if (activity is not null) activity.StartActivity(i);
                else Ctx.StartActivity(i);
            }
            catch (Exception e)
            {
                Views.Ui.Toast("Не получилось открыть: " + e.Message);
            }
        });
    }

    private static bool CanHandle(Intent intent)
    {
        try { return intent.ResolveActivity(Ctx.PackageManager!) is not null; }
        catch { return true; }
    }

    private static ToolResult OpenUrl(string url)
    {
        url = url.Trim();
        if (url.Length == 0) return ToolResult.Error("Не указана ссылка", "🌐 нет ссылки");
        if (!url.Contains(':')) url = "https://" + url;
        if (url.StartsWith("file:", StringComparison.OrdinalIgnoreCase) || url.StartsWith("content:", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) || url.StartsWith("intent:", StringComparison.OrdinalIgnoreCase))
            return ToolResult.Error("Такие ссылки открывать нельзя.", "🌐 запрещено");
        var intent = new Intent(Intent.ActionView, AUri.Parse(url));
        intent.AddCategory(Intent.CategoryBrowsable);
        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase) && !CanHandle(intent))
            return ToolResult.Error($"На телефоне нет приложения для ссылки {url}", "🌐 нечем открыть");
        Start(intent);
        var shown = url.Replace("https://", "").Replace("http://", "");
        return ToolResult.Ok($"Открыто: {url}", "🌐 " + (shown.Length > 50 ? shown[..50] + "…" : shown));
    }

    private static ToolResult SearchWeb(string query, string? engine)
    {
        if (string.IsNullOrWhiteSpace(query)) return ToolResult.Error("Пустой запрос", "🔎 пусто");
        var q = Uri.EscapeDataString(query);
        var url = engine switch
        {
            "yandex" => $"https://yandex.ru/search/?text={q}",
            "youtube" => $"https://www.youtube.com/results?search_query={q}",
            "wikipedia" => $"https://ru.wikipedia.org/w/index.php?search={q}",
            "bing" => $"https://www.bing.com/search?q={q}",
            "duckduckgo" => $"https://duckduckgo.com/?q={q}",
            _ => $"https://www.google.com/search?q={q}",
        };
        Start(new Intent(Intent.ActionView, AUri.Parse(url)));
        return ToolResult.Ok($"Открыла поиск «{query}» ({engine ?? "google"}). Результатов я не вижу — только открыла страницу.", "🔎 " + query);
    }

    private static string Norm(string s) => new string((s ?? "").ToLowerInvariant().Replace('ё', 'е')
        .Where(c => char.IsLetterOrDigit(c) || c == ' ').ToArray()).Trim();

    /// <summary>Русские названия → то, что обычно написано в лаунчере (или кусок имени пакета).</summary>
    private static readonly Dictionary<string, string[]> Aliases = new()
    {
        ["ютуб"] = new[] { "youtube" }, ["ютуб музыка"] = new[] { "youtube music" },
        ["телеграм"] = new[] { "telegram", "org.telegram" }, ["телега"] = new[] { "telegram" },
        ["ватсап"] = new[] { "whatsapp" }, ["вотсап"] = new[] { "whatsapp" }, ["вацап"] = new[] { "whatsapp" },
        ["вк"] = new[] { "vk", "вконтакте" }, ["вконтакте"] = new[] { "vk", "вконтакте" },
        ["инстаграм"] = new[] { "instagram" }, ["тикток"] = new[] { "tiktok" }, ["спотифай"] = new[] { "spotify" },
        ["хром"] = new[] { "chrome" }, ["браузер"] = new[] { "chrome", "browser", "браузер", "firefox", "яндекс" },
        ["яндекс"] = new[] { "яндекс", "yandex" }, ["карты"] = new[] { "maps", "карты", "навигатор" },
        ["гугл карты"] = new[] { "maps" }, ["навигатор"] = new[] { "навигатор", "navigator" },
        ["камера"] = new[] { "camera", "камера" }, ["галерея"] = new[] { "gallery", "photos", "галерея", "фото" },
        ["фото"] = new[] { "photos", "gallery", "фото" }, ["калькулятор"] = new[] { "calculator", "калькулятор" },
        ["часы"] = new[] { "clock", "часы", "будильник" }, ["будильник"] = new[] { "clock", "будильник", "часы" },
        ["календарь"] = new[] { "calendar", "календарь" }, ["почта"] = new[] { "gmail", "mail", "почта" },
        ["гмейл"] = new[] { "gmail" }, ["настройки"] = new[] { "settings", "настройки" }, ["телефон"] = new[] { "phone", "телефон", "dialer" },
        ["сообщения"] = new[] { "messages", "сообщения", "sms" }, ["контакты"] = new[] { "contacts", "контакты" },
        ["плей маркет"] = new[] { "play store", "play маркет", "google play" }, ["плеймаркет"] = new[] { "play store", "google play" },
        ["музыка"] = new[] { "music", "музыка" }, ["заметки"] = new[] { "keep", "notes", "заметки" },
        ["дискорд"] = new[] { "discord" }, ["стим"] = new[] { "steam" }, ["нетфликс"] = new[] { "netflix" },
        ["сбер"] = new[] { "сбер", "sber" }, ["сбербанк"] = new[] { "сбер", "sber" }, ["тинькофф"] = new[] { "т-банк", "tinkoff", "тинькофф" },
        ["т банк"] = new[] { "т-банк", "tinkoff" }, ["озон"] = new[] { "ozon", "озон" }, ["вайлдберриз"] = new[] { "wildberries" },
        ["госуслуги"] = new[] { "госуслуги", "gosuslugi" }, ["файлы"] = new[] { "files", "файлы", "менеджер файлов" },
        ["диктофон"] = new[] { "recorder", "диктофон" }, ["погода"] = new[] { "weather", "погода" },
    };

    private static ToolResult OpenApp(string raw)
    {
        raw = raw.Trim();
        if (raw.Length == 0) return ToolResult.Error("Не указано имя приложения", "📱 нет имени");
        var pm = Ctx.PackageManager!;
        var main = new Intent(Intent.ActionMain);
        main.AddCategory(Intent.CategoryLauncher);
        var apps = pm.QueryIntentActivities(main, PackageInfoFlags.MatchAll)
            .Select(ri => (Label: ri.LoadLabel(pm)?.ToString() ?? "", Pkg: ri.ActivityInfo?.PackageName ?? "", Act: ri.ActivityInfo?.Name ?? ""))
            .Where(a => a.Pkg.Length > 0 && a.Pkg != Ctx.PackageName)
            .ToList();

        var n = Norm(raw);
        var wanted = new List<string> { n };
        if (Aliases.TryGetValue(n, out var al)) wanted.AddRange(al.Select(Norm));

        int Score((string Label, string Pkg, string Act) a)
        {
            var label = Norm(a.Label);
            var pkg = a.Pkg.ToLowerInvariant();
            var best = 0;
            foreach (var w in wanted)
            {
                if (w.Length == 0) continue;
                if (label == w) best = Math.Max(best, 100);
                else if (label.StartsWith(w)) best = Math.Max(best, 85);
                else if (label.Contains(w)) best = Math.Max(best, 70);
                else if (w.Contains(label) && label.Length >= 3) best = Math.Max(best, 55);
                else if (pkg.Contains(w.Replace(" ", ""))) best = Math.Max(best, 50);
            }
            return best;
        }

        var match = apps.Select(a => (App: a, Score: Score(a))).Where(x => x.Score > 0).OrderByDescending(x => x.Score).ThenBy(x => x.App.Label.Length).FirstOrDefault();
        if (match.Score == 0)
        {
            var list = string.Join(", ", apps.Select(a => a.Label).Distinct().OrderBy(x => x).Take(80));
            return ToolResult.Error($"Не нашла приложение «{raw}». Установленные приложения: {list}", "📱 не нашла «" + raw + "»");
        }
        var launch = pm.GetLaunchIntentForPackage(match.App.Pkg);
        if (launch is null)
        {
            launch = new Intent(Intent.ActionMain);
            launch.AddCategory(Intent.CategoryLauncher);
            launch.SetClassName(match.App.Pkg, match.App.Act);
        }
        launch.AddFlags(ActivityFlags.NewTask);
        Start(launch);
        return ToolResult.Ok($"Запустила «{match.App.Label}».", "📱 " + match.App.Label);
    }

    private static ToolResult Dial(string number)
    {
        var clean = new string(number.Where(c => char.IsDigit(c) || c is '+' or '*' or '#' or ',').ToArray());
        if (clean.Length == 0) return ToolResult.Error("Не указан номер", "📞 нет номера");
        Start(new Intent(Intent.ActionDial, AUri.Parse("tel:" + Uri.EscapeDataString(clean))));
        return ToolResult.Ok($"Открыла звонилку с номером {number}. Позвонить пользователь должен сам.", "📞 " + number);
    }

    private static ToolResult OpenMaps(string query, bool navigate)
    {
        if (string.IsNullOrWhiteSpace(query)) return ToolResult.Error("Не указано место", "🗺️ нет места");
        var q = Uri.EscapeDataString(query);
        var intent = navigate
            ? new Intent(Intent.ActionView, AUri.Parse("google.navigation:q=" + q))
            : new Intent(Intent.ActionView, AUri.Parse("geo:0,0?q=" + q));
        if (!CanHandle(intent)) intent = new Intent(Intent.ActionView, AUri.Parse("geo:0,0?q=" + q));
        if (!CanHandle(intent)) intent = new Intent(Intent.ActionView, AUri.Parse("https://www.google.com/maps/search/?api=1&query=" + q));
        Start(intent);
        return ToolResult.Ok($"Открыла карты: {query}" + (navigate ? " (маршрут)" : ""), "🗺️ " + query);
    }

    private static ToolResult Share(string text, string? title)
    {
        if (string.IsNullOrEmpty(text)) return ToolResult.Error("Нечего отправлять", "📤 пусто");
        var intent = new Intent(Intent.ActionSend);
        intent.SetType("text/plain");
        intent.PutExtra(Intent.ExtraText, text);
        if (!string.IsNullOrWhiteSpace(title)) intent.PutExtra(Intent.ExtraSubject, title);
        Start(intent, string.IsNullOrWhiteSpace(title) ? "Поделиться" : title);
        return ToolResult.Ok("Открыла меню «Поделиться». Пользователь сам выберет, куда отправить.", "📤 поделиться");
    }

    private static ToolResult Flashlight(string state)
    {
        var cm = (CameraManager?)Ctx.GetSystemService(Context.CameraService);
        if (cm is null) return ToolResult.Error("Камера недоступна", "🔦 нет фонарика");
        string? id = null;
        foreach (var cid in cm.GetCameraIdList())
        {
            var ch = cm.GetCameraCharacteristics(cid);
            var flash = ch.Get(CameraCharacteristics.FlashInfoAvailable);
            if (flash is Java.Lang.Boolean b && b.BooleanValue()) { id = cid; break; }
        }
        if (id is null) return ToolResult.Error("На этом телефоне нет вспышки", "🔦 нет фонарика");
        var on = state switch { "on" => true, "off" => false, _ => !_torchOn };
        try
        {
            cm.SetTorchMode(id, on);
            _torchOn = on;
        }
        catch (Exception e)
        {
            return ToolResult.Error("Не удалось переключить фонарик: " + e.Message, "🔦 ошибка");
        }
        return ToolResult.Ok(on ? "Фонарик включён." : "Фонарик выключен.", on ? "🔦 фонарик включён" : "🔦 фонарик выключен");
    }

    private static ToolResult OpenSettings(string section)
    {
        var (action, title) = section switch
        {
            "wifi" => (ASettings.ActionWifiSettings, "Wi-Fi"),
            "bluetooth" => (ASettings.ActionBluetoothSettings, "Bluetooth"),
            "sound" => (ASettings.ActionSoundSettings, "звук"),
            "display" => (ASettings.ActionDisplaySettings, "экран"),
            "battery" => (Intent.ActionPowerUsageSummary, "батарея"),
            "location" => (ASettings.ActionLocationSourceSettings, "геолокация"),
            "mobile_data" => (ASettings.ActionDataRoamingSettings, "мобильная сеть"),
            "airplane" => (ASettings.ActionAirplaneModeSettings, "режим полёта"),
            "nfc" => (ASettings.ActionNfcSettings, "NFC"),
            "notifications" => ("android.settings.ALL_APPS_NOTIFICATION_SETTINGS", "уведомления"),
            "apps" => (ASettings.ActionApplicationSettings, "приложения"),
            "date" => (ASettings.ActionDateSettings, "дата и время"),
            "language" => (ASettings.ActionLocaleSettings, "язык"),
            "accessibility" => (ASettings.ActionAccessibilitySettings, "спец. возможности"),
            "security" => (ASettings.ActionSecuritySettings, "безопасность"),
            "storage" => (ASettings.ActionInternalStorageSettings, "память"),
            "app" => (ASettings.ActionApplicationDetailsSettings, "настройки Веры"),
            _ => (ASettings.ActionSettings, "настройки"),
        };
        var intent = new Intent(action);
        if (section == "app") intent.SetData(AUri.Parse("package:" + Ctx.PackageName));
        if (!CanHandle(intent)) intent = new Intent(ASettings.ActionSettings);
        Start(intent);
        return ToolResult.Ok($"Открыла настройки: {title}.", "⚙️ " + title);
    }

    private static ToolResult DeviceInfoText()
    {
        var lines = new List<string>();
        string label;
        try
        {
            var level = Battery.Default.ChargeLevel;
            var st = Battery.Default.State switch
            {
                BatteryState.Charging => "заряжается",
                BatteryState.Full => "полностью заряжен",
                BatteryState.Discharging or BatteryState.NotCharging => "не заряжается",
                _ => "состояние неизвестно",
            };
            var src = Battery.Default.PowerSource switch
            {
                BatteryPowerSource.AC => " (от сети)",
                BatteryPowerSource.Usb => " (по USB)",
                BatteryPowerSource.Wireless => " (беспроводная зарядка)",
                _ => "",
            };
            var pct = level >= 0 ? (int)Math.Round(level * 100) : -1;
            lines.Add(pct >= 0 ? $"Батарея: {pct}%, {st}{src}" : "Батарея: нет данных");
            if (Battery.Default.EnergySaverStatus == EnergySaverStatus.On) lines.Add("Режим энергосбережения включён");
            label = pct >= 0 ? $"🔋 {pct}%" : "📱 телефон";
        }
        catch
        {
            label = "📱 телефон";
        }
        lines.Add($"Телефон: {Build.Manufacturer} {Build.Model}");
        lines.Add($"Android {Build.VERSION.Release} (API {(int)Build.VERSION.SdkInt})");
        try
        {
            var net = Connectivity.Current;
            var kinds = string.Join(", ", net.ConnectionProfiles.Select(p => p switch
            {
                ConnectionProfile.WiFi => "Wi-Fi",
                ConnectionProfile.Cellular => "мобильная сеть",
                ConnectionProfile.Bluetooth => "Bluetooth",
                ConnectionProfile.Ethernet => "Ethernet",
                _ => "другое",
            }));
            lines.Add($"Интернет: {(net.NetworkAccess == NetworkAccess.Internet ? "есть" : "нет")}" + (kinds.Length > 0 ? $" ({kinds})" : ""));
        }
        catch { }
        try
        {
            var stat = new StatFs(Android.OS.Environment.DataDirectory!.AbsolutePath);
            var free = stat.AvailableBytes / 1024.0 / 1024 / 1024;
            var total = stat.TotalBytes / 1024.0 / 1024 / 1024;
            lines.Add($"Память: свободно {free:0.#} из {total:0.#} ГБ");
        }
        catch { }
        lines.Add($"Время: {RuText.FormatDateTime(DateTimeOffset.Now)}");
        return ToolResult.Ok(string.Join("\n", lines), label);
    }
}
