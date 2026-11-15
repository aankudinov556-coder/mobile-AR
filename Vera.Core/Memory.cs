namespace Vera.Core;

public sealed class MemoryFact : Sync.ISyncable
{
    public string Id { get; set; } = "";
    public string Text { get; set; } = "";
    /// <summary>"user" | "ai"</summary>
    public string Source { get; set; } = "user";
    public DateTimeOffset Created { get; set; }
    public long Ver { get; set; }
    public string? Dev { get; set; }
    public long Seq { get; set; }

    public MemoryFact Copy() => (MemoryFact)MemberwiseClone();
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

    /// <summary>Пометить локальное изменение для синхронизации (задаёт SyncService).</summary>
    public Action<Sync.ISyncable>? Stamp { get; set; }
    public Action<string, string>? Deleted { get; set; }

    internal List<MemoryFact> ChangedSince(long seq, string? exceptDev)
    {
        lock (_gate) return _file.Data.Where(m => m.Seq > seq && m.Dev != exceptDev).Select(m => m.Copy()).ToList();
    }

    internal int ApplyRemote(IEnumerable<MemoryFact> items, Func<long> nextSeq, Func<string, long?> deletedVer)
    {
        var n = 0;
        lock (_gate)
        {
            foreach (var x in items)
            {
                if (deletedVer(x.Id) is { } dv && dv >= x.Ver) continue;
                var local = _file.Data.FirstOrDefault(m => m.Id == x.Id);
                if (local is not null && !Sync.SyncClock.Newer(x.Ver, x.Dev, local.Ver, local.Dev)) continue;
                var copy = x.Copy();
                copy.Seq = nextSeq();
                if (local is not null) _file.Data[_file.Data.IndexOf(local)] = copy;
                else _file.Data.Add(copy);
                n++;
            }
            while (_file.Data.Count > MaxFacts) _file.Data.RemoveAt(0);
        }
        if (n > 0) { _file.Save(); Changed?.Invoke(); }
        return n;
    }

    internal bool ApplyDelete(string id, long ver, string? dev)
    {
        bool removed;
        lock (_gate)
        {
            var local = _file.Data.FirstOrDefault(m => m.Id == id);
            removed = local is not null && !Sync.SyncClock.Newer(local.Ver, local.Dev, ver, dev) && _file.Data.Remove(local);
        }
        if (removed) { _file.Save(); Changed?.Invoke(); }
        return removed;
    }

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
            Stamp?.Invoke(m);
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
            Stamp?.Invoke(m);
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
            Deleted?.Invoke("memory", m.Id);
        }
        _file.Save();
        Changed?.Invoke();
        return m;
    }

    public void Clear()
    {
        lock (_gate)
        {
            foreach (var m in _file.Data) Deleted?.Invoke("memory", m.Id);
            _file.Data.Clear();
        }
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
