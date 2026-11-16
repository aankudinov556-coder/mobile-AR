namespace Vera.Core.Commands;

/// <summary>Своя команда: схема из блоков, соединённых линиями (конструктор команд, ПК).</summary>
public sealed class UserCommand
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];
    public string Name { get; set; } = "Новая команда";
    public bool Enabled { get; set; } = true;
    /// <summary>Вера может запустить команду сама, если поймёт просьбу своими словами.</summary>
    public bool AllowAi { get; set; } = true;
    public List<CommandNode> Nodes { get; set; } = new();
    public List<CommandLink> Links { get; set; } = new();
    public DateTimeOffset Updated { get; set; } = DateTimeOffset.Now;
    /// <summary>Когда последний раз сработало расписание (чтобы не запускать дважды за минуту).</summary>
    public DateTimeOffset? LastScheduled { get; set; }

    public CommandNode? Node(string id) => Nodes.FirstOrDefault(n => n.Id == id);

    public IEnumerable<CommandNode> Triggers => Nodes.Where(n => Blocks.Get(n.Type)?.Category == BlockCategory.Start);

    /// <summary>Куда ведёт выход port блока from (null — никуда).</summary>
    public CommandNode? Next(string from, string port = "out")
    {
        var l = Links.FirstOrDefault(x => x.From == from && x.Port == port);
        return l is null ? null : Node(l.To);
    }

    /// <summary>Фразы всех триггеров «Когда скажу».</summary>
    public IEnumerable<string> Phrases() => Triggers.Where(t => t.Type == "phrase")
        .SelectMany(t => CommandText.SplitPhrases(t.Get("phrases")));

    /// <summary>Глубокая копия (для «отменить» в редакторе).</summary>
    public UserCommand Clone() => System.Text.Json.JsonSerializer.Deserialize<UserCommand>(
        System.Text.Json.JsonSerializer.Serialize(this, VeraJson.Options), VeraJson.Options)!;
}

public sealed class CommandNode
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    /// <summary>Тип блока из <see cref="Blocks"/>.</summary>
    public string Type { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    public Dictionary<string, string> Props { get; set; } = new();

    public string Get(string key) => Props.TryGetValue(key, out var v) ? v : Blocks.Get(Type)?.Prop(key)?.Default ?? "";
}

/// <summary>Линия: выход Port блока From → вход блока To.</summary>
public sealed class CommandLink
{
    public string From { get; set; } = "";
    public string Port { get; set; } = "out";
    public string To { get; set; } = "";
}

public sealed class CommandsFile
{
    public List<UserCommand> Commands { get; set; } = new();
}

public static class CommandText
{
    /// <summary>«рабочий режим, работаем» → ["рабочий режим", "работаем"].</summary>
    public static IEnumerable<string> SplitPhrases(string? s) =>
        (s ?? "").Split(new[] { ',', ';', '\n' }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(Normalize).Where(p => p.Length > 0);

    /// <summary>Строчные, без пунктуации и лишних пробелов, ё → е.</summary>
    public static string Normalize(string? s)
    {
        var chars = (s ?? "").ToLowerInvariant().Replace('ё', 'е')
            .Select(c => char.IsLetterOrDigit(c) ? c : ' ').ToArray();
        return string.Join(' ', new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
