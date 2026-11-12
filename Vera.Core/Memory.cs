namespace Vera.Core;

public sealed class MemoryFact
{
    public string Id { get; set; } = "";
    public string Text { get; set; } = "";
    /// <summary>"user" | "ai"</summary>
    public string Source { get; set; } = "user";
    public DateTimeOffset Created { get; set; }
}

/// <summary>Долгая память: факты о пользователе между сессиями.</summary>
public sealed class MemoryService
{
    private const int MaxFacts = 150;
    private readonly JsonFile<List<MemoryFact>> _file;
    private readonly object _gate = new();

    public MemoryService(IVeraStorage storage)
    {
        _file = new JsonFile<List<MemoryFact>>(storage, "memory.json", () => new List<MemoryFact>());
    }

    public event Action? Changed;

    public IReadOnlyList<MemoryFact> List()
    {
        lock (_gate) return _file.Data.ToList();
    }

    public MemoryFact Add(string text, string source = "user")
    {
        text = (text ?? "").Trim();
        if (text.Length == 0) throw new ArgumentException("Пустой факт");
        if (text.Length > 400) text = text[..400];
        MemoryFact m;
        lock (_gate)
        {
            var dup = _file.Data.FirstOrDefault(x => string.Equals(x.Text, text, StringComparison.OrdinalIgnoreCase));
            if (dup is not null) return dup;
            m = new MemoryFact { Id = "m_" + Guid.NewGuid().ToString("N")[..10], Text = text, Source = source, Created = DateTimeOffset.Now };
            _file.Data.Add(m);
            while (_file.Data.Count > MaxFacts) _file.Data.RemoveAt(0);
        }
        _file.Save();
        Changed?.Invoke();
        return m;
    }

    public MemoryFact? Update(string id, string text)
    {
        MemoryFact? m;
        lock (_gate)
        {
            m = _file.Data.FirstOrDefault(x => x.Id == id);
            if (m is null) return null;
            if (!string.IsNullOrWhiteSpace(text)) m.Text = text.Trim();
        }
        _file.Save();
        Changed?.Invoke();
        return m;
    }

    public MemoryFact? Remove(string idOrText)
    {
        MemoryFact? m;
        lock (_gate)
        {
            var q = (idOrText ?? "").ToLowerInvariant();
            m = _file.Data.FirstOrDefault(x => x.Id == idOrText)
                ?? (q.Length > 0 ? _file.Data.FirstOrDefault(x => x.Text.ToLowerInvariant().Contains(q)) : null);
            if (m is null) return null;
            _file.Data.Remove(m);
        }
        _file.Save();
        Changed?.Invoke();
        return m;
    }

    public void Clear()
    {
        lock (_gate) _file.Data.Clear();
        _file.Save();
        Changed?.Invoke();
    }

    public string ForPrompt()
    {
        lock (_gate)
            return _file.Data.Count == 0 ? "(пока ничего)" : string.Join("\n", _file.Data.Select(m => $"- [{m.Id}] {m.Text}"));
    }

    public void Flush() => _file.SaveNow();
}
