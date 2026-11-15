namespace Vera.Core;

public sealed class Reminder : Sync.ISyncable
{
    public string Id { get; set; } = "";
    public string Text { get; set; } = "";
    public DateTimeOffset Due { get; set; }
    /// <summary>"none" | "hourly" | "daily" | "weekdays" | "weekly" | "monthly"</summary>
    public string Repeat { get; set; } = "none";
    /// <summary>"user" | "ai" | "snooze"</summary>
    public string Source { get; set; } = "user";
    public DateTimeOffset Created { get; set; }
    public bool Done { get; set; }
    public DateTimeOffset? FiredAt { get; set; }
    /// <summary>Синхронизация: версия (гибридные часы), устройство, локальный номер изменения.</summary>
    public long Ver { get; set; }
    public string? Dev { get; set; }
    public long Seq { get; set; }

    public string RepeatText => ReminderService.RepeatLabel(Repeat);
    public string DueText => RuText.FormatDateTime(Due);
    public string RelativeText => RuText.Relative(Due);

    public Reminder Copy() => (Reminder)MemberwiseClone();
}

/// <summary>Напоминания: хранение, повторы, планировщик.</summary>
public sealed class ReminderService : IDisposable
{
    public static readonly string[] Repeats = { "none", "hourly", "daily", "weekdays", "weekly", "monthly" };
    private static readonly TimeSpan KeepDone = TimeSpan.FromDays(7);

    private readonly JsonFile<List<Reminder>> _file;
    private readonly object _gate = new();
    private Timer? _timer;

    public ReminderService(IVeraStorage storage)
    {
        _file = new JsonFile<List<Reminder>>(storage, "reminders.json", () => new List<Reminder>());
    }

    /// <summary>Пометить локальное изменение для синхронизации (задаёт SyncService). Вызывается под замком.</summary>
    public Action<Sync.ISyncable>? Stamp { get; set; }
    /// <summary>Локальное удаление (kind, id) — для синхронизации.</summary>
    public Action<string, string>? Deleted { get; set; }

    /// <summary>Изменения с номером больше seq, кроме пришедших от устройства exceptDev.</summary>
    internal List<Reminder> ChangedSince(long seq, string? exceptDev)
    {
        lock (_gate) return _file.Data.Where(r => r.Seq > seq && r.Dev != exceptDev).Select(r => r.Copy()).ToList();
    }

    /// <summary>Применить пришедшие напоминания: новее — заменяют, удалённые (по надгробию) — не воскрешают.</summary>
    internal int ApplyRemote(IEnumerable<Reminder> items, Func<long> nextSeq, Func<string, long?> deletedVer)
    {
        var n = 0;
        lock (_gate)
        {
            foreach (var x in items)
            {
                if (deletedVer(x.Id) is { } dv && dv >= x.Ver) continue;
                var local = _file.Data.FirstOrDefault(r => r.Id == x.Id);
                if (local is not null && !Sync.SyncClock.Newer(x.Ver, x.Dev, local.Ver, local.Dev)) continue;
                var copy = x.Copy();
                copy.Seq = nextSeq();
                if (local is not null) _file.Data[_file.Data.IndexOf(local)] = copy;
                else _file.Data.Add(copy);
                n++;
            }
        }
        if (n > 0) { _file.Save(); Changed?.Invoke(); }
        return n;
    }

    /// <summary>Удаление с другого устройства (если наша версия не новее).</summary>
    internal bool ApplyDelete(string id, long ver, string? dev)
    {
        bool removed;
        lock (_gate)
        {
            var local = _file.Data.FirstOrDefault(r => r.Id == id);
            removed = local is not null && !Sync.SyncClock.Newer(local.Ver, local.Dev, ver, dev) && _file.Data.Remove(local);
        }
        if (removed) { _file.Save(); Changed?.Invoke(); }
        return removed;
    }

    /// <summary>Список изменился (добавление, удаление, срабатывание).</summary>
    public event Action? Changed;

    /// <summary>Пора напомнить. bool — «пропущенное» (сработало с опозданием, например после включения ПК).</summary>
    public event Action<Reminder, bool>? Due;

    public static string RepeatLabel(string repeat) => repeat switch
    {
        "hourly" => "каждый час",
        "daily" => "каждый день",
        "weekdays" => "по будням",
        "weekly" => "каждую неделю",
        "monthly" => "каждый месяц",
        _ => "однократно",
    };

    private static DateTimeOffset? StepOnce(DateTimeOffset t, string repeat)
    {
        var d = t.ToLocalTime();
        switch (repeat)
        {
            case "hourly": return d.AddHours(1);
            case "daily": return d.AddDays(1);
            case "weekly": return d.AddDays(7);
            case "monthly": return d.AddMonths(1);
            case "weekdays":
                do { d = d.AddDays(1); } while (d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday);
                return d;
            default: return null;
        }
    }

    /// <summary>Следующее срабатывание строго позже now (null — без повтора).</summary>
    public static DateTimeOffset? NextDue(DateTimeOffset due, string repeat, DateTimeOffset now)
    {
        var t = due;
        var guard = 0;
        while (t <= now && guard++ < 100000)
        {
            var n = StepOnce(t, repeat);
            if (n is null) return null;
            t = n.Value;
        }
        if (repeat == "weekdays")
            while (t.ToLocalTime().DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) t = t.AddDays(1);
        return t;
    }

    /// <summary>Запустить внутренний таймер (тик каждые 5 с). Пропущенные сработают сразу.</summary>
    public void Start()
    {
        Tick(startup: true);
        _timer ??= new Timer(_ => Tick(false), null, 5000, 5000);
    }

    public void Stop() => _timer?.Change(Timeout.Infinite, Timeout.Infinite);

    public void Dispose() => _timer?.Dispose();

    /// <summary>Все напоминания: сначала активные по времени, потом сработавшие.</summary>
    public IReadOnlyList<Reminder> List()
    {
        lock (_gate)
        {
            Cleanup();
            return _file.Data.OrderBy(r => r.Done).ThenBy(r => r.Due).Select(r => r.Copy()).ToList();
        }
    }

    /// <summary>Активные напоминания по возрастанию времени (для планирования системных будильников).</summary>
    public IReadOnlyList<Reminder> Upcoming()
    {
        lock (_gate) return _file.Data.Where(r => !r.Done).OrderBy(r => r.Due).Select(r => r.Copy()).ToList();
    }

    public Reminder? Get(string id)
    {
        lock (_gate) return _file.Data.FirstOrDefault(r => r.Id == id)?.Copy();
    }

    public Reminder Add(string text, DateTimeOffset due, string repeat = "none", string source = "user")
    {
        text = (text ?? "").Trim();
        if (text.Length == 0) throw new ArgumentException("Пустой текст напоминания");
        if (!Repeats.Contains(repeat)) repeat = "none";
        var now = DateTimeOffset.Now;
        if (due <= now)
        {
            if (repeat == "none")
            {
                if (now - due > TimeSpan.FromMinutes(1)) throw new ArgumentException("Это время уже прошло: " + RuText.FormatDateTime(due));
                due = now.AddSeconds(1);
            }
            else due = NextDue(due, repeat, now) ?? now.AddSeconds(1);
        }
        var r = new Reminder
        {
            Id = "r_" + Guid.NewGuid().ToString("N")[..12],
            Text = text.Length > 500 ? text[..500] : text,
            Due = due,
            Repeat = repeat,
            Source = source,
            Created = now,
        };
        lock (_gate) { Stamp?.Invoke(r); _file.Data.Add(r); }
        _file.Save();
        Changed?.Invoke();
        return r.Copy();
    }

    private Reminder? Find(string idOrText)
    {
        if (string.IsNullOrWhiteSpace(idOrText)) return null;
        var q = idOrText.ToLowerInvariant();
        return _file.Data.FirstOrDefault(r => r.Id == idOrText)
            ?? _file.Data.FirstOrDefault(r => !r.Done && r.Text.ToLowerInvariant().Contains(q));
    }

    public Reminder? Remove(string idOrText)
    {
        Reminder? r;
        lock (_gate)
        {
            r = Find(idOrText);
            if (r is null) return null;
            _file.Data.Remove(r);
            Deleted?.Invoke("reminder", r.Id);
        }
        _file.Save();
        Changed?.Invoke();
        return r;
    }

    public Reminder? Update(string idOrText, string? text = null, DateTimeOffset? due = null, string? repeat = null)
    {
        Reminder? r;
        lock (_gate)
        {
            r = Find(idOrText);
            if (r is null) return null;
            if (!string.IsNullOrWhiteSpace(text)) r.Text = text.Trim();
            if (repeat is not null && Repeats.Contains(repeat)) r.Repeat = repeat;
            if (due is not null) { r.Due = due.Value; r.Done = false; r.FiredAt = null; }
            Stamp?.Invoke(r);
        }
        _file.Save();
        Changed?.Invoke();
        return r.Copy();
    }

    /// <summary>Отложить. Для повторяющихся создаётся отдельное разовое напоминание.</summary>
    public Reminder? Snooze(string id, int minutes = 10)
    {
        Reminder? r;
        lock (_gate)
        {
            r = Find(id);
            if (r is null) return null;
            if (r.Repeat != "none" && !r.Done)
            {
                var text = r.Text;
                r = null;
                return Add(text, DateTimeOffset.Now.AddMinutes(minutes), "none", "snooze");
            }
            r.Due = DateTimeOffset.Now.AddMinutes(minutes);
            r.Done = false;
            r.FiredAt = null;
            Stamp?.Invoke(r);
        }
        _file.Save();
        Changed?.Invoke();
        return r.Copy();
    }

    public Reminder? Complete(string id)
    {
        Reminder? r;
        lock (_gate)
        {
            r = Find(id);
            if (r is null) return null;
            if (r.Repeat == "none") { r.Done = true; r.FiredAt ??= DateTimeOffset.Now; }
            Stamp?.Invoke(r);
        }
        _file.Save();
        Changed?.Invoke();
        return r.Copy();
    }

    private void Cleanup()
    {
        var now = DateTimeOffset.Now;
        var removed = _file.Data.RemoveAll(r => r.Done && r.FiredAt is { } f && now - f > KeepDone);
        if (removed > 0) _file.Save();
    }

    /// <summary>Проверить, что пора напомнить. Десктоп зовёт через Start(); мобильный — при открытии приложения.</summary>
    public void Tick(bool startup = false)
    {
        var fired = new List<(Reminder R, bool Missed)>();
        var now = DateTimeOffset.Now;
        lock (_gate)
        {
            foreach (var r in _file.Data)
            {
                if (r.Done || r.Due > now) continue;
                var missed = startup || now - r.Due > TimeSpan.FromMinutes(2);
                fired.Add((r.Copy(), missed));
                r.FiredAt = now;
                if (r.Repeat == "none") r.Done = true;
                else r.Due = NextDue(r.Due, r.Repeat, now) ?? now.AddYears(100);
                Stamp?.Invoke(r);
            }
        }
        if (fired.Count == 0) return;
        _file.Save();
        foreach (var (r, missed) in fired) Due?.Invoke(r, missed);
        Changed?.Invoke();
    }

    public void Flush() => _file.SaveNow();
}
