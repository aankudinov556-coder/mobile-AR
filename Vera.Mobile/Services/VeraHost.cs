using Vera.Core;
using Vera.Mobile.Platform;

namespace Vera.Mobile.Services;

/// <summary>
/// Один экземпляр ядра на процесс: его используют и интерфейс, и BroadcastReceiver напоминаний
/// (Android может поднять процесс только ради будильника, без активити).
/// </summary>
public static class VeraHost
{
    private static readonly object Gate = new();
    private static VeraCore? _core;
    private static AndroidSecretStore? _secrets;

    public static VeraCore Core
    {
        get
        {
            var c = Volatile.Read(ref _core);
            return c ?? Create();
        }
    }

    public static bool IsCreated => Volatile.Read(ref _core) is not null;

    private static VeraCore Create()
    {
        lock (Gate)
        {
            if (_core is not null) return _core;
            _secrets = new AndroidSecretStore();
            var core = new VeraCore(new AndroidStorage(), _secrets, new PhoneTools(), platform: "mobile");
            // Будильники Android пересчитываем при каждом изменении списка
            core.Reminders.Changed += () => ReminderScheduler.Reschedule(core);
            core.Reminders.Due += ReminderAlerts.OnDue;
            Volatile.Write(ref _core, core);
            try { ReminderNotifier.EnsureChannel(); } catch { }
            try { ReminderScheduler.Reschedule(core); } catch { }
            return core;
        }
    }

    /// <summary>Сохранить всё немедленно (приложение уходит в фон / процесс могут убить).</summary>
    public static void Flush()
    {
        var c = Volatile.Read(ref _core);
        if (c is null) return;
        try { c.Flush(); } catch { }
        _secrets?.Flush();
    }
}

/// <summary>Состояние приложения для решений «показать уведомление или облачко».</summary>
public static class AppState
{
    private static int _resumed;

    /// <summary>Окно приложения на экране.</summary>
    public static bool IsForeground => Volatile.Read(ref _resumed) > 0;

    /// <summary>Открыт экран чата (Веру видно).</summary>
    public static bool ChatVisible { get; set; }

    public static event Action<bool>? ForegroundChanged;

    public static void SetForeground(bool value)
    {
        var old = Interlocked.Exchange(ref _resumed, value ? 1 : 0) > 0;
        if (old != value) ForegroundChanged?.Invoke(value);
    }
}
