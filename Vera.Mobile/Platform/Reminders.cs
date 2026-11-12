using Android.App;
using Android.Content;
using Android.OS;
using AndroidX.Core.App;
using Vera.Core;
using Vera.Mobile.Services;
using AUri = Android.Net.Uri;

namespace Vera.Mobile.Platform;

/// <summary>Что делать, когда ядро говорит «пора напомнить»: облачко Веры (приложение открыто) или уведомление.</summary>
public static class ReminderAlerts
{
    /// <summary>Напоминание сработало, пока приложение на экране (вызывается в UI-потоке): reminder, missed, phrase.</summary>
    public static event Action<Reminder, bool, string>? ForegroundDue;

    public static void OnDue(Reminder r, bool missed)
    {
        try
        {
            // одно и то же срабатывание (id + время) показываем один раз, даже если Tick повторится после перезапуска
            if (!ShownJournal.TryMark(r.Id, r.Due)) return;
            var core = VeraHost.Core;
            var phrase = core.ReminderPhrase(r, missed);
            var s = core.Settings.Current.Reminders;
            var ui = AppState.IsForeground && ForegroundDue is not null;
            if (ui) MainThread.BeginInvokeOnMainThread(() => ForegroundDue?.Invoke(r, missed, phrase));
            if ((!ui || !AppState.ChatVisible) && s.Notification) ReminderNotifier.Show(r, missed, phrase, s.Sound && !ui);
        }
        catch (Exception e)
        {
            Android.Util.Log.Error("Vera", "reminder due: " + e);
        }
    }
}

/// <summary>Журнал показанных срабатываний (id + due) — защита от дублей между BroadcastReceiver и Tick(startup).</summary>
public static class ShownJournal
{
    private static readonly object Gate = new();
    private const string Key = "shown";

    private static ISharedPreferences Prefs => Android.App.Application.Context.GetSharedPreferences("vera_reminders_shown", FileCreationMode.Private)!;

    public static bool TryMark(string id, DateTimeOffset due)
    {
        var token = id + "@" + due.ToUnixTimeSeconds();
        lock (Gate)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var entries = (Prefs.GetString(Key, "") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(e => e.Split('|'))
                .Where(p => p.Length == 2 && long.TryParse(p[1], out var t) && now - t < 4 * 86400)
                .Select(p => (Token: p[0], Time: long.Parse(p[1])))
                .ToList();
            if (entries.Any(e => e.Token == token)) return false;
            entries.Add((token, now));
            if (entries.Count > 300) entries.RemoveRange(0, entries.Count - 300);
            var edit = Prefs.Edit()!;
            edit.PutString(Key, string.Join(";", entries.Select(e => e.Token + "|" + e.Time)));
            edit.Commit(); // синхронно: процесс могут убить сразу после OnReceive
            return true;
        }
    }
}

/// <summary>Канал «Напоминания» и уведомления с действиями «Готово» / «+10 мин».</summary>
public static class ReminderNotifier
{
    public const string Channel = "reminders";
    private static Context Ctx => Android.App.Application.Context;

    public static void EnsureChannel()
    {
        if (Build.VERSION.SdkInt < BuildVersionCodes.O) return;
        var nm = (NotificationManager?)Ctx.GetSystemService(Context.NotificationService);
        if (nm is null || nm.GetNotificationChannel(Channel) is not null) return;
        var ch = new NotificationChannel(Channel, "Напоминания", NotificationImportance.High)
        {
            Description = "Напоминания, таймеры и будильники Веры",
        };
        ch.EnableVibration(true);
        ch.EnableLights(true);
        ch.LightColor = unchecked((int)0xFFE0479A);
        ch.SetShowBadge(true);
        nm.CreateNotificationChannel(ch);
    }

    /// <summary>Стабильный id уведомления (string.GetHashCode в .NET случайный между запусками).</summary>
    public static int NotificationId(string reminderId)
    {
        unchecked
        {
            var h = (int)2166136261;
            foreach (var c in reminderId) h = (h ^ c) * 16777619;
            return (h & 0x7FFFFFFF) | 0x1000;
        }
    }

    public static bool Enabled()
    {
        try { return NotificationManagerCompat.From(Ctx).AreNotificationsEnabled(); }
        catch { return true; }
    }

    public static void Show(Reminder r, bool missed, string phrase, bool sound)
    {
        EnsureChannel();
        var ctx = Ctx;
        var nid = NotificationId(r.Id);
        var open = new Intent(ctx, typeof(MainActivity));
        open.SetFlags(ActivityFlags.SingleTop | ActivityFlags.ClearTop);
        open.SetData(AUri.Parse("vera://open/" + r.Id));
        open.PutExtra(MainActivity.ExtraReminderId, r.Id);
        var contentPi = PendingIntent.GetActivity(ctx, nid, open, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

        var icon = ctx.Resources!.GetIdentifier("ic_notify", "drawable", ctx.PackageName);
        if (icon == 0) icon = Android.Resource.Drawable.IcDialogInfo;

        var b = new NotificationCompat.Builder(ctx, Channel)
            .SetSmallIcon(icon)!
            .SetContentTitle((missed ? "⏰ Пропущено: " : "⏰ ") + r.Text)!
            .SetContentText(phrase)!
            .SetStyle(new NotificationCompat.BigTextStyle().BigText(phrase))!
            .SetCategory(NotificationCompat.CategoryReminder)!
            .SetPriority(NotificationCompat.PriorityHigh)!
            .SetColor(unchecked((int)0xFFE0479A))!
            .SetAutoCancel(true)!
            .SetWhen(r.Due.ToUnixTimeMilliseconds())!
            .SetShowWhen(true)!
            .SetSilent(!sound)!
            .SetContentIntent(contentPi)!
            .AddAction(0, "Готово", ReminderReceiver.ActionIntent(ReminderReceiver.ActionDone, r.Id, nid))!
            .AddAction(0, "+10 мин", ReminderReceiver.ActionIntent(ReminderReceiver.ActionSnooze, r.Id, nid))!;
        if (sound) b.SetDefaults(NotificationCompat.DefaultAll);
        try { NotificationManagerCompat.From(ctx).Notify(nid, b.Build()); }
        catch (Java.Lang.SecurityException) { /* нет разрешения POST_NOTIFICATIONS */ }
    }

    public static void Cancel(string reminderId)
    {
        try { NotificationManagerCompat.From(Ctx).Cancel(NotificationId(reminderId)); } catch { }
    }
}

/// <summary>Системные будильники AlarmManager для всех активных напоминаний.</summary>
public static class ReminderScheduler
{
    private static readonly object Gate = new();
    private const string PrefIds = "ids";
    private static Context Ctx => Android.App.Application.Context;

    public static bool CanScheduleExact()
    {
        if (Build.VERSION.SdkInt < BuildVersionCodes.S) return true;
        var am = (AlarmManager?)Ctx.GetSystemService(Context.AlarmService);
        return am?.CanScheduleExactAlarms() ?? false;
    }

    public static void Reschedule(VeraCore core)
    {
        try
        {
            lock (Gate) RescheduleLocked(core);
        }
        catch (Exception e)
        {
            Android.Util.Log.Error("Vera", "reschedule: " + e);
        }
    }

    private static void RescheduleLocked(VeraCore core)
    {
        var ctx = Ctx;
        var am = (AlarmManager?)ctx.GetSystemService(Context.AlarmService);
        if (am is null) return;
        var prefs = ctx.GetSharedPreferences("vera_alarms", FileCreationMode.Private)!;
        var old = (prefs.GetString(PrefIds, "") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var exact = CanScheduleExact();
        var nowMs = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        var ids = new HashSet<string>();
        foreach (var r in core.Reminders.Upcoming().Take(100))
        {
            var pi = Pending(ctx, r.Id, PendingIntentFlags.UpdateCurrent);
            if (pi is null) continue;
            var at = Math.Max(r.Due.ToUnixTimeMilliseconds(), nowMs + 1500);
            try
            {
                if (exact) am.SetExactAndAllowWhileIdle(AlarmType.RtcWakeup, at, pi);
                else am.SetAndAllowWhileIdle(AlarmType.RtcWakeup, at, pi);
            }
            catch (Java.Lang.SecurityException)
            {
                am.SetAndAllowWhileIdle(AlarmType.RtcWakeup, at, pi);
            }
            ids.Add(r.Id);
        }
        foreach (var id in old.Except(ids))
        {
            var pi = Pending(ctx, id, PendingIntentFlags.NoCreate);
            if (pi is null) continue;
            am.Cancel(pi);
            pi.Cancel();
        }
        var edit = prefs.Edit()!;
        edit.PutString(PrefIds, string.Join(";", ids));
        edit.Apply();
    }

    private static PendingIntent? Pending(Context ctx, string id, PendingIntentFlags flags)
    {
        var intent = new Intent(ctx, typeof(ReminderReceiver));
        intent.SetAction(ReminderReceiver.ActionFire);
        intent.SetData(AUri.Parse("vera://reminder/" + Uri.EscapeDataString(id)));
        intent.PutExtra(ReminderReceiver.ExtraId, id);
        return PendingIntent.GetBroadcast(ctx, 0, intent, flags | PendingIntentFlags.Immutable);
    }
}

/// <summary>Срабатывание будильника и кнопки уведомления.</summary>
[BroadcastReceiver(Name = "com.vera.assistant.ReminderReceiver", Enabled = true, Exported = false)]
public sealed class ReminderReceiver : BroadcastReceiver
{
    public const string ActionFire = "com.vera.assistant.REMINDER_FIRE";
    public const string ActionDone = "com.vera.assistant.REMINDER_DONE";
    public const string ActionSnooze = "com.vera.assistant.REMINDER_SNOOZE";
    public const string ExtraId = "reminder_id";

    public static PendingIntent ActionIntent(string action, string id, int requestCode)
    {
        var ctx = Android.App.Application.Context;
        var intent = new Intent(ctx, typeof(ReminderReceiver));
        intent.SetAction(action);
        intent.SetData(AUri.Parse($"vera://{(action == ActionDone ? "done" : "snooze")}/{Uri.EscapeDataString(id)}"));
        intent.PutExtra(ExtraId, id);
        return PendingIntent.GetBroadcast(ctx, requestCode, intent, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable)!;
    }

    public override void OnReceive(Context? context, Intent? intent)
    {
        try
        {
            var core = VeraHost.Core;
            var id = intent?.GetStringExtra(ExtraId) ?? "";
            switch (intent?.Action)
            {
                case ActionFire:
                    core.Reminders.Tick(startup: false);
                    // если сработало чуть раньше времени — будильник переставится на точное время
                    ReminderScheduler.Reschedule(core);
                    break;
                case ActionDone:
                    ReminderNotifier.Cancel(id);
                    core.Reminders.Complete(id);
                    break;
                case ActionSnooze:
                    ReminderNotifier.Cancel(id);
                    var r = core.Reminders.Snooze(id, 10);
                    if (r is not null) Views.Ui.Toast("Напомню в " + r.Due.ToLocalTime().ToString("HH:mm"));
                    break;
            }
            core.Reminders.Flush();
        }
        catch (Exception e)
        {
            Android.Util.Log.Error("Vera", "receiver: " + e);
        }
    }
}

/// <summary>После перезагрузки, обновления приложения и смены времени — пропущенные напоминания и новые будильники.</summary>
[BroadcastReceiver(Name = "com.vera.assistant.BootReceiver", Enabled = true, Exported = true)]
[IntentFilter(new[]
{
    Intent.ActionBootCompleted,
    "android.intent.action.QUICKBOOT_POWERON",
    Intent.ActionMyPackageReplaced,
    Intent.ActionTimeChanged,
    Intent.ActionTimezoneChanged,
    "android.app.action.SCHEDULE_EXACT_ALARM_PERMISSION_STATE_CHANGED",
})]
public sealed class BootReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        try
        {
            var core = VeraHost.Core;
            var boot = intent?.Action is Intent.ActionBootCompleted or "android.intent.action.QUICKBOOT_POWERON";
            core.Reminders.Tick(startup: boot);
            ReminderScheduler.Reschedule(core);
            core.Reminders.Flush();
        }
        catch (Exception e)
        {
            Android.Util.Log.Error("Vera", "boot: " + e);
        }
    }
}
