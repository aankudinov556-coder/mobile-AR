using System.Globalization;
using System.Text.Json.Nodes;

namespace Vera.Core.Tools;

/// <summary>Общие инструменты (напоминания, память, погода) + платформенные.</summary>
public sealed class ToolRegistry
{
    private readonly ReminderService _reminders;
    private readonly MemoryService _memory;
    private readonly HttpClient _http;
    private readonly IPlatformTools? _platform;

    public ToolRegistry(ReminderService reminders, MemoryService memory, HttpClient http, IPlatformTools? platform)
    {
        _reminders = reminders;
        _memory = memory;
        _http = http;
        _platform = platform;
    }

    public IReadOnlyList<ToolDefinition> GetDefinitions(VeraSettings settings)
    {
        var defs = new List<ToolDefinition>(CoreDefinitions());
        if (_platform is not null) defs.AddRange(_platform.GetDefinitions(settings));
        return defs;
    }

    public static IEnumerable<ToolDefinition> CoreDefinitions()
    {
        yield return new ToolDefinition
        {
            Name = "create_reminder",
            Description =
                "Create a reminder / timer / alarm. Vera will notify the user at that time (notification, voice, speech bubble). " +
                "Use it whenever the user asks to remind, set a timer or an alarm. Compute times from the current local time given in <context>. " +
                "Use in_minutes for relative times (\"через 20 минут\"), datetime for absolute ones (\"завтра в 9\").",
            Parameters = ToolDefinition.Schema(new JsonObject
            {
                ["text"] = ToolDefinition.Str("What to remind about, short, in the user's language (e.g. \"Выпить таблетки\")."),
                ["datetime"] = ToolDefinition.Str("Local date-time YYYY-MM-DDTHH:MM (no timezone) for the first trigger."),
                ["in_minutes"] = ToolDefinition.Num("Alternatively: minutes from now (can be fractional)."),
                ["repeat"] = ToolDefinition.Enum("Repeat rule. Default \"none\".", ReminderService.Repeats),
            }, "text"),
        };
        yield return new ToolDefinition
        {
            Name = "list_reminders",
            Description = "List the user's active (and recently fired) reminders with their ids.",
            Parameters = ToolDefinition.Schema(new JsonObject()),
        };
        yield return new ToolDefinition
        {
            Name = "delete_reminder",
            Description = "Delete / cancel a reminder by id (from list_reminders) or by a fragment of its text.",
            Parameters = ToolDefinition.Schema(new JsonObject { ["id"] = ToolDefinition.Str("Reminder id like r_abc123 or a text fragment.") }, "id"),
        };
        yield return new ToolDefinition
        {
            Name = "update_reminder",
            Description = "Change text, time or repeat rule of an existing reminder.",
            Parameters = ToolDefinition.Schema(new JsonObject
            {
                ["id"] = ToolDefinition.Str("Reminder id or text fragment."),
                ["text"] = ToolDefinition.Str("New text."),
                ["datetime"] = ToolDefinition.Str("New local date-time YYYY-MM-DDTHH:MM"),
                ["in_minutes"] = ToolDefinition.Num("Or: minutes from now."),
                ["repeat"] = ToolDefinition.Enum("Repeat rule.", ReminderService.Repeats),
            }, "id"),
        };
        yield return new ToolDefinition
        {
            Name = "remember",
            Description =
                "Save a durable fact about the user to long-term memory (name, birthday, preferences, ongoing projects, important people). " +
                "Do not store trivia or one-off requests. Write the fact as a short standalone sentence in Russian.",
            Parameters = ToolDefinition.Schema(new JsonObject { ["fact"] = ToolDefinition.Str("The fact.") }, "fact"),
        };
        yield return new ToolDefinition
        {
            Name = "forget",
            Description = "Remove a fact from long-term memory by id (like m_abc123) or by text fragment.",
            Parameters = ToolDefinition.Schema(new JsonObject { ["id"] = ToolDefinition.Str("Fact id or text fragment.") }, "id"),
        };
        yield return new ToolDefinition
        {
            Name = "get_weather",
            Description = "Current weather and a 3-day forecast for a city.",
            Parameters = ToolDefinition.Schema(new JsonObject { ["city"] = ToolDefinition.Str("City name, e.g. \"Москва\"") }, "city"),
        };
    }

    public async Task<ToolResult> ExecuteAsync(string name, JsonObject args, CancellationToken ct)
    {
        try
        {
            if (args.ContainsKey("__invalid_json")) return ToolResult.Error("Ошибка: аргументы инструмента не являются корректным JSON", name);
            switch (name)
            {
                case "create_reminder":
                {
                    var due = ParseDue(args);
                    var r = _reminders.Add(args.Str("text") ?? "", due, args.Str("repeat") ?? "none", "ai");
                    return ToolResult.Ok(
                        $"Напоминание создано (id {r.Id}): «{r.Text}» — {r.DueText} ({r.RelativeText}), повтор: {r.RepeatText}.",
                        $"⏰ {r.Text} — {RuText.WhenShort(r.Due)}" + (r.Repeat != "none" ? " · " + r.RepeatText : ""),
                        "reminder");
                }
                case "list_reminders":
                {
                    var list = _reminders.List();
                    if (list.Count == 0) return ToolResult.Ok("Напоминаний нет.", "⏰ список напоминаний");
                    var active = list.Where(r => !r.Done).ToList();
                    var done = list.Where(r => r.Done).TakeLast(5).ToList();
                    static string Fmt(Reminder r) => $"- [{r.Id}] «{r.Text}» — {r.DueText} ({r.RelativeText}), {r.RepeatText}";
                    return ToolResult.Ok(
                        $"Активные ({active.Count}):\n{(active.Count > 0 ? string.Join("\n", active.Select(Fmt)) : "—")}\n\n" +
                        $"Недавно сработавшие:\n{(done.Count > 0 ? string.Join("\n", done.Select(Fmt)) : "—")}",
                        $"⏰ напоминаний: {active.Count}");
                }
                case "delete_reminder":
                {
                    var r = _reminders.Remove(args.Str("id") ?? "");
                    return r is null
                        ? ToolResult.Error($"Напоминание «{args.Str("id")}» не найдено. Вызови list_reminders, чтобы узнать id.", "⏰ не найдено")
                        : ToolResult.Ok($"Удалено напоминание «{r.Text}».", $"🗑️ {r.Text}");
                }
                case "update_reminder":
                {
                    DateTimeOffset? due = args.ContainsKey("datetime") || args.ContainsKey("in_minutes") ? ParseDue(args) : null;
                    var r = _reminders.Update(args.Str("id") ?? "", args.Str("text"), due, args.Str("repeat"));
                    return r is null
                        ? ToolResult.Error($"Напоминание «{args.Str("id")}» не найдено.", "⏰ не найдено")
                        : ToolResult.Ok($"Обновлено: «{r.Text}» — {r.DueText} ({r.RelativeText}), {r.RepeatText}.", $"⏰ {r.Text} — {RuText.WhenShort(r.Due)}", "reminder");
                }
                case "remember":
                {
                    var m = _memory.Add(args.Str("fact") ?? "", "ai");
                    return ToolResult.Ok($"Запомнила (id {m.Id}): {m.Text}", $"🧠 {m.Text}", "memory");
                }
                case "forget":
                {
                    var m = _memory.Remove(args.Str("id") ?? "");
                    return m is null ? ToolResult.Error("Такого факта нет в памяти.", "🧠 не найдено") : ToolResult.Ok($"Забыла: {m.Text}", $"🧠 забыто: {m.Text}", "memory");
                }
                case "get_weather":
                    return await WeatherAsync(args.Str("city") ?? "", ct).ConfigureAwait(false);
            }

            if (_platform is not null)
            {
                var r = await _platform.TryExecuteAsync(name, args, ct).ConfigureAwait(false);
                if (r is not null) return r;
            }
            return ToolResult.Error($"Неизвестный инструмент: {name}", name);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e)
        {
            return ToolResult.Error("Ошибка: " + e.Message, "⚠️ " + name);
        }
    }

    /// <summary>Время из аргументов create_reminder: in_minutes или datetime (локальное YYYY-MM-DDTHH:MM).</summary>
    public static DateTimeOffset ParseDue(JsonObject args, DateTimeOffset? nowOverride = null)
    {
        var now = nowOverride ?? DateTimeOffset.Now;
        var mins = args.Num("in_minutes");
        if (mins is not null)
        {
            if (mins <= 0 || double.IsNaN(mins.Value)) throw new ArgumentException("in_minutes должно быть положительным числом");
            return now.AddMinutes(mins.Value);
        }
        var s = args.Str("datetime")?.Trim().Replace(' ', 'T');
        if (string.IsNullOrEmpty(s)) throw new ArgumentException("Нужно указать datetime или in_minutes");
        // со смещением (…+03:00 / Z) — как есть, без смещения — локальное время
        if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var t)) return t;
        throw new ArgumentException($"Не понимаю дату «{s}». Используй формат YYYY-MM-DDTHH:MM (локальное время).");
    }

    private static readonly Dictionary<int, string> Wmo = new()
    {
        [0] = "ясно", [1] = "преимущественно ясно", [2] = "переменная облачность", [3] = "пасмурно", [45] = "туман", [48] = "изморозь",
        [51] = "лёгкая морось", [53] = "морось", [55] = "сильная морось", [56] = "ледяная морось", [57] = "ледяная морось",
        [61] = "небольшой дождь", [63] = "дождь", [65] = "сильный дождь", [66] = "ледяной дождь", [67] = "ледяной дождь",
        [71] = "небольшой снег", [73] = "снег", [75] = "сильный снег", [77] = "снежная крупа", [80] = "ливень", [81] = "ливни",
        [82] = "сильный ливень", [85] = "снегопад", [86] = "сильный снегопад", [95] = "гроза", [96] = "гроза с градом", [99] = "гроза с градом",
    };

    private async Task<ToolResult> WeatherAsync(string city, CancellationToken ct)
    {
        city = city.Trim();
        if (city.Length == 0) return ToolResult.Error("Не указан город", "🌦️ погода");
        var geo = JsonNode.Parse(await _http.GetStringAsync(
            $"https://geocoding-api.open-meteo.com/v1/search?count=1&language=ru&name={Uri.EscapeDataString(city)}", ct).ConfigureAwait(false));
        var place = geo?["results"]?[0];
        if (place is null) return ToolResult.Error($"Город «{city}» не найден.", "🌦️ город не найден");
        var lat = place["latitude"]!.GetValue<double>().ToString(CultureInfo.InvariantCulture);
        var lon = place["longitude"]!.GetValue<double>().ToString(CultureInfo.InvariantCulture);
        var w = JsonNode.Parse(await _http.GetStringAsync(
            $"https://api.open-meteo.com/v1/forecast?latitude={lat}&longitude={lon}" +
            "&current=temperature_2m,apparent_temperature,relative_humidity_2m,weather_code,wind_speed_10m" +
            "&daily=weather_code,temperature_2m_max,temperature_2m_min,precipitation_probability_max&timezone=auto&forecast_days=3&wind_speed_unit=ms",
            ct).ConfigureAwait(false))!;
        var c = w["current"]!;
        string Code(JsonNode? n) => n is not null && Wmo.TryGetValue(n.GetValue<int>(), out var s) ? s : "";
        int R(JsonNode? n) => (int)Math.Round(n?.GetValue<double>() ?? 0);
        var name = place["name"]?.GetValue<string>() ?? city;
        var country = place["country"]?.GetValue<string>();
        var lines = new List<string>
        {
            $"{name}{(country is null ? "" : ", " + country)}: сейчас {R(c["temperature_2m"])}°C (ощущается {R(c["apparent_temperature"])}°C), " +
            $"{Code(c["weather_code"])}, ветер {R(c["wind_speed_10m"])} м/с, влажность {R(c["relative_humidity_2m"])}%.",
        };
        string[] days = { "Сегодня", "Завтра", "Послезавтра" };
        var daily = w["daily"];
        var times = daily?["time"]?.AsArray();
        for (var i = 0; i < (times?.Count ?? 0); i++)
        {
            lines.Add($"{(i < days.Length ? days[i] : times![i]!.GetValue<string>())}: {R(daily!["temperature_2m_min"]![i])}…{R(daily["temperature_2m_max"]![i])}°C, " +
                      $"{Code(daily["weather_code"]![i])}, осадки {daily["precipitation_probability_max"]?[i]?.ToString() ?? "?"}%");
        }
        return ToolResult.Ok(string.Join("\n", lines), $"🌦️ {name}: {R(c["temperature_2m"])}°C");
    }
}
