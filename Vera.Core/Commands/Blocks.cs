namespace Vera.Core.Commands;

public enum BlockCategory { Start, Action, Logic, Vera }

public enum PropKind { Text, Multiline, Number, Choice, Time, Hotkey }

/// <summary>Свойство блока для панели справа.</summary>
public sealed record BlockProp(string Key, string Label, PropKind Kind, string Default = "", IReadOnlyList<(string Value, string Title)>? Options = null, string? Hint = null);

/// <summary>Тип блока: где в меню, иконка, выходы, свойства и короткая подпись на поле.</summary>
public sealed record BlockDef(
    string Type,
    BlockCategory Category,
    string Title,
    string Icon,
    IReadOnlyList<BlockProp> Props,
    Func<CommandNode, string> Summary,
    IReadOnlyList<(string Port, string Label)>? Outputs = null,
    string Keywords = "")
{
    /// <summary>Выходы блока (по умолчанию один — «out»).</summary>
    public IReadOnlyList<(string Port, string Label)> Ports => Outputs ?? new[] { ("out", "") };
    public bool HasInput => Category != BlockCategory.Start;
    public BlockProp? Prop(string key) => Props.FirstOrDefault(p => p.Key == key);
}

/// <summary>Каталог блоков конструктора команд.</summary>
public static class Blocks
{
    private static readonly (string, string)[] Days = { ("daily", "каждый день"), ("weekdays", "по будням"), ("weekends", "по выходным") };

    public static readonly IReadOnlyList<BlockDef> All = new BlockDef[]
    {
        // ---------------------------------------------------------------- запуск
        new("phrase", BlockCategory.Start, "Когда скажу", "mic",
            new[] { new BlockProp("phrases", "Фразы после «Вера, …» (через запятую)", PropKind.Multiline, "рабочий режим", Hint: "Можно и написать в чат.") },
            n => string.Join(" · ", CommandText.SplitPhrases(n.Get("phrases")).Select(p => "«" + p + "»")), Keywords: "фраза голос сказать слово"),
        new("hotkey", BlockCategory.Start, "Горячие клавиши", "keyboard",
            new[] { new BlockProp("keys", "Сочетание", PropKind.Hotkey, "Ctrl+Alt+W") },
            n => n.Get("keys"), Keywords: "клавиши сочетание кнопки"),
        new("schedule", BlockCategory.Start, "По расписанию", "clock",
            new[]
            {
                new BlockProp("time", "Время", PropKind.Time, "09:00"),
                new BlockProp("days", "Дни", PropKind.Choice, "weekdays", Days),
            },
            n => n.Get("time") + " · " + L10n.T(Days.FirstOrDefault(d => d.Item1 == n.Get("days")).Item2 ?? ""), Keywords: "время будильник утро каждый день"),
        new("startup", BlockCategory.Start, "Когда включаю ПК", "power",
            Array.Empty<BlockProp>(), _ => L10n.T("при запуске Веры"), Keywords: "запуск включение старт"),

        // ---------------------------------------------------------------- действия
        new("open_app", BlockCategory.Action, "Открыть программу", "window",
            new[] { new BlockProp("name", "Программа (как в меню «Пуск» или из «Мои программы»)", PropKind.Text, "Блокнот") },
            n => n.Get("name"), Keywords: "запустить приложение игра"),
        new("open_url", BlockCategory.Action, "Открыть сайт", "globe",
            new[] { new BlockProp("url", "Адрес", PropKind.Text, "https://") },
            n => n.Get("url"), Keywords: "ссылка браузер страница"),
        new("open_folder", BlockCategory.Action, "Открыть папку или файл", "folder",
            new[] { new BlockProp("path", "Путь (или «Загрузки», «Документы»…)", PropKind.Text, "Загрузки") },
            n => n.Get("path"), Keywords: "проводник документ"),
        new("volume", BlockCategory.Action, "Громкость", "volume",
            new[] { new BlockProp("level", "Громкость, %", PropKind.Number, "30") },
            n => n.Get("level") + "%", Keywords: "звук тише громче"),
        new("media", BlockCategory.Action, "Музыка: пауза, дальше", "media",
            new[]
            {
                new BlockProp("action", "Что сделать", PropKind.Choice, "play_pause", new[]
                {
                    ("play_pause", "пауза / играть"), ("next", "следующий трек"), ("previous", "предыдущий трек"), ("mute", "выключить звук"),
                }),
            },
            n => L10n.T(n.Get("action") switch { "next" => "следующий трек", "previous" => "предыдущий трек", "mute" => "выключить звук", _ => "пауза / играть" }),
            Keywords: "медиа трек плеер"),
        new("type_text", BlockCategory.Action, "Напечатать текст", "type",
            new[] { new BlockProp("text", "Текст (впишется туда, где курсор)", PropKind.Multiline, "") },
            n => Short(n.Get("text")), Keywords: "ввести набрать клавиатура"),
        new("screenshot", BlockCategory.Action, "Скриншот", "screenshot",
            Array.Empty<BlockProp>(), _ => L10n.T("снимок экрана"), Keywords: "экран снимок"),
        new("system", BlockCategory.Action, "Система", "power",
            new[]
            {
                new BlockProp("action", "Что сделать", PropKind.Choice, "show_desktop", new[]
                {
                    ("show_desktop", "свернуть все окна"), ("lock", "заблокировать ПК"), ("task_manager", "диспетчер задач"), ("settings", "параметры Windows"),
                }),
            },
            n => L10n.T(n.Get("action") switch { "lock" => "заблокировать ПК", "task_manager" => "диспетчер задач", "settings" => "параметры Windows", _ => "свернуть все окна" }),
            Keywords: "блокировка рабочий стол"),
        new("powershell", BlockCategory.Action, "Команда PowerShell", "terminal",
            new[] { new BlockProp("command", "Команда (перед запуском Вера спросит подтверждение)", PropKind.Multiline, "") },
            n => Short(n.Get("command")), Keywords: "консоль терминал скрипт"),

        // ---------------------------------------------------------------- логика
        new("if", BlockCategory.Logic, "Если", "branch",
            new[]
            {
                new BlockProp("cond", "Условие", PropKind.Choice, "weekday", new[]
                {
                    ("weekday", "сегодня будний день"), ("weekend", "сегодня выходной"),
                    ("after", "сейчас позже, чем…"), ("before", "сейчас раньше, чем…"), ("answer", "я ответил «да» на «Спросить меня»"),
                }),
                new BlockProp("time", "Время (для «позже / раньше»)", PropKind.Time, "18:00"),
            },
            n => n.Get("cond") switch
            {
                "weekend" => L10n.T("сегодня выходной"),
                "after" => L10n.T("позже ") + n.Get("time"),
                "before" => L10n.T("раньше ") + n.Get("time"),
                "answer" => L10n.T("ответ «да»"),
                _ => L10n.T("сегодня будний день"),
            },
            new[] { ("yes", "да"), ("no", "нет") }, Keywords: "условие проверка ветка"),
        new("wait", BlockCategory.Logic, "Подождать", "hourglass",
            new[] { new BlockProp("seconds", "Секунд", PropKind.Number, "2") },
            n => L10n.F("{0} с", n.Get("seconds")), Keywords: "пауза задержка"),
        new("repeat", BlockCategory.Logic, "Повторить", "repeat",
            new[] { new BlockProp("times", "Сколько раз", PropKind.Number, "3") },
            n => L10n.F("{0} раза", n.Get("times")),
            new[] { ("body", "что повторять"), ("out", "потом") }, Keywords: "цикл несколько раз"),
        new("ask", BlockCategory.Logic, "Спросить меня", "ask",
            new[] { new BlockProp("question", "Вопрос", PropKind.Text, "Продолжить?") },
            n => n.Get("question"),
            new[] { ("yes", "да"), ("no", "нет") }, Keywords: "подтверждение вопрос да нет"),

        // ---------------------------------------------------------------- Вера
        new("say", BlockCategory.Vera, "Вера ответит", "chat",
            new[] { new BlockProp("text", "Что сказать", PropKind.Multiline, "Готово!") },
            n => "«" + Short(n.Get("text")) + "»", Keywords: "сказать сообщение пузырь"),
        new("ask_ai", BlockCategory.Vera, "Спросить ИИ", "sparkle",
            new[] { new BlockProp("prompt", "Что спросить у Веры (ответ — в чате)", PropKind.Multiline, "Какая сегодня погода?") },
            n => Short(n.Get("prompt")), Keywords: "нейросеть вопрос модель"),
        new("reminder", BlockCategory.Vera, "Поставить напоминание", "bell",
            new[]
            {
                new BlockProp("text", "О чём напомнить", PropKind.Text, "Сделать перерыв"),
                new BlockProp("minutes", "Через сколько минут", PropKind.Number, "50"),
            },
            n => L10n.F("{0} · через {1} мин", Short(n.Get("text")), n.Get("minutes")), Keywords: "таймер будильник"),
    };

    public static BlockDef? Get(string? type) => All.FirstOrDefault(b => b.Type == type);

    /// <summary>Поиск для меню блоков: по названию и ключевым словам.</summary>
    public static IEnumerable<BlockDef> Search(string? query)
    {
        var q = CommandText.Normalize(query);
        if (q.Length == 0) return All;
        return All.Where(b => CommandText.Normalize(L10n.T(b.Title) + " " + b.Title + " " + b.Keywords + " " + L10n.T(b.Keywords)).Contains(q));
    }

    public static string CategoryTitle(BlockCategory c) => c switch
    {
        BlockCategory.Start => "Запуск",
        BlockCategory.Action => "Действия",
        BlockCategory.Logic => "Логика",
        _ => "Вера",
    };

    private static string Short(string s)
    {
        s = (s ?? "").Replace('\n', ' ').Trim();
        return s.Length > 40 ? s[..40] + "…" : s;
    }

    /// <summary>Новый блок с настройками по умолчанию.</summary>
    public static CommandNode Create(string type, double x, double y)
    {
        var def = Get(type) ?? throw new ArgumentException("Неизвестный блок: " + type);
        var n = new CommandNode { Type = type, X = x, Y = y };
        foreach (var p in def.Props) n.Props[p.Key] = L10n.T(p.Default);
        return n;
    }

    /// <summary>Пример для новой команды: «Когда скажу» → «Вера ответит».</summary>
    public static UserCommand Sample()
    {
        var c = new UserCommand();
        var t = Create("phrase", 300, 40);
        var s = Create("say", 300, 180);
        c.Nodes.Add(t);
        c.Nodes.Add(s);
        c.Links.Add(new CommandLink { From = t.Id, To = s.Id });
        return c;
    }
}
