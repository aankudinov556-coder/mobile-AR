using Vera.Core.Providers;

namespace Vera.Core;

/// <summary>Расход за день по одной модели.</summary>
public sealed class UsageEntry
{
    public string Day { get; set; } = "";          // yyyy-MM-dd
    public string Provider { get; set; } = "";
    public string Model { get; set; } = "";
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public int Requests { get; set; }
    /// <summary>Примерная стоимость в долларах (если цена модели известна).</summary>
    public double? Cost { get; set; }
}

public sealed record UsageSummary(long InputTokens, long OutputTokens, int Requests, double? Cost, bool CostPartial);

/// <summary>Счётчик токенов и примерной стоимости по провайдерам и дням.</summary>
public sealed class UsageService
{
    private const int KeepDays = 120;
    private readonly JsonFile<List<UsageEntry>> _file;
    private readonly object _gate = new();

    /// <summary>Цены за миллион токенов для моделей с известной ценой (Anthropic, первые лица API).</summary>
    private static readonly Dictionary<string, (double In, double Out)> Builtin = new()
    {
        ["claude-fable-5-2"] = (8, 40), ["claude-fable-5-1"] = (10, 50), ["claude-fable-5"] = (10, 50),
        ["claude-opus-5-5"] = (4, 20), ["claude-opus-5"] = (5, 25), ["claude-opus-4-8"] = (5, 25), ["claude-opus-4-7"] = (5, 25),
        ["claude-opus-4-6"] = (5, 25), ["claude-sonnet-5-5"] = (2, 10), ["claude-sonnet-5"] = (2, 10), ["claude-sonnet-4-6"] = (3, 15),
        ["claude-haiku-5-5"] = (0.10, 0.50), ["claude-haiku-4-5"] = (1, 5),
    };

    public UsageService(IVeraStorage storage)
    {
        _file = new JsonFile<List<UsageEntry>>(storage, "usage.json", () => new List<UsageEntry>());
    }

    public event Action? Changed;

    public static (double In, double Out)? PriceOf(string provider, string model)
    {
        if (provider is "local" or "custom") return (0, 0);
        if (provider == "openrouter" && OpenAICompatProvider.KnownPrices.TryGetValue(model, out var p)) return p;
        var m = model.StartsWith("anthropic/") ? model[10..].Replace('.', '-') : model;
        return Builtin.TryGetValue(m, out var b) ? b : null;
    }

    public void Record(string provider, string model, int? input, int? output)
    {
        if (input is null && output is null) return;
        var day = DateTime.Now.ToString("yyyy-MM-dd");
        lock (_gate)
        {
            var e = _file.Data.FirstOrDefault(x => x.Day == day && x.Provider == provider && x.Model == model);
            if (e is null)
            {
                e = new UsageEntry { Day = day, Provider = provider, Model = model };
                _file.Data.Add(e);
            }
            e.InputTokens += input ?? 0;
            e.OutputTokens += output ?? 0;
            e.Requests++;
            var price = PriceOf(provider, model);
            if (price is { } pr) e.Cost = (e.Cost ?? 0) + ((input ?? 0) * pr.In + (output ?? 0) * pr.Out) / 1_000_000.0;
            var cutoff = DateTime.Now.AddDays(-KeepDays).ToString("yyyy-MM-dd");
            _file.Data.RemoveAll(x => string.CompareOrdinal(x.Day, cutoff) < 0);
        }
        _file.Save();
        Changed?.Invoke();
    }

    public UsageSummary Today() => Sum(e => e.Day == DateTime.Now.ToString("yyyy-MM-dd"));

    public UsageSummary ThisMonth() => Sum(e => e.Day.StartsWith(DateTime.Now.ToString("yyyy-MM")));

    /// <summary>Расход за месяц по провайдерам.</summary>
    public IReadOnlyList<(string Provider, UsageSummary Sum)> MonthByProvider()
    {
        var month = DateTime.Now.ToString("yyyy-MM");
        lock (_gate)
        {
            return _file.Data.Where(e => e.Day.StartsWith(month)).GroupBy(e => e.Provider)
                .Select(g => (g.Key, Summ(g))).OrderByDescending(x => x.Item2.InputTokens + x.Item2.OutputTokens).ToList();
        }
    }

    private UsageSummary Sum(Func<UsageEntry, bool> filter)
    {
        lock (_gate) return Summ(_file.Data.Where(filter));
    }

    private static UsageSummary Summ(IEnumerable<UsageEntry> items)
    {
        var list = items.ToList();
        var priced = list.Where(e => e.Cost is not null).ToList();
        return new UsageSummary(list.Sum(e => e.InputTokens), list.Sum(e => e.OutputTokens), list.Sum(e => e.Requests),
            priced.Count > 0 ? priced.Sum(e => e.Cost!.Value) : null, priced.Count < list.Count);
    }

    public void Reset()
    {
        lock (_gate) _file.Data.Clear();
        _file.Save();
        Changed?.Invoke();
    }

    public void Flush() => _file.SaveNow();
}

/// <summary>Журнал запросов (для диагностики): последние ответы моделей в requests.log.</summary>
public sealed class RequestLog
{
    private const int Keep = 80;
    private readonly IVeraStorage _storage;
    private readonly List<string> _entries = new();
    private readonly object _gate = new();

    public RequestLog(IVeraStorage storage)
    {
        _storage = storage;
        var old = storage.ReadText("requests.log");
        if (!string.IsNullOrEmpty(old))
            _entries.AddRange(old.Split("\n=====\n", StringSplitOptions.RemoveEmptyEntries).TakeLast(Keep));
    }

    public void Add(string provider, string model, int step, string text)
    {
        var entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {provider} · {model} · шаг {step}\n{RuText.Truncate(text, 6000)}";
        string all;
        lock (_gate)
        {
            _entries.Add(entry);
            while (_entries.Count > Keep) _entries.RemoveAt(0);
            all = string.Join("\n=====\n", _entries);
        }
        try { _storage.WriteText("requests.log", all); } catch { /* журнал — не критично */ }
    }

    public string Text()
    {
        lock (_gate) return string.Join("\n=====\n", _entries);
    }

    public void Clear()
    {
        lock (_gate) _entries.Clear();
        try { _storage.WriteText("requests.log", ""); } catch { /* ignore */ }
    }
}
