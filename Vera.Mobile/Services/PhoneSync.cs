using Android.App;
using Android.Content;
using Vera.Core;
using Vera.Core.Agent;
using Vera.Core.Sync;
using Vera.Mobile.Platform;

namespace Vera.Mobile.Services;

/// <summary>
/// Синхронизация с ПК со стороны телефона: при открытии приложения, через несколько секунд после изменений,
/// раз в ~15 минут (пока открыто) и в фоне — будильником Android (неточный, бережёт батарею).
/// </summary>
public static class PhoneSync
{
    private static int _running;
    private static CancellationTokenSource? _soon;
    private static bool _hooked;

    /// <summary>Последняя ошибка (для экрана синхронизации).</summary>
    public static string? LastError { get; private set; }

    public static event Action? StateChanged;

    public static bool Paired => VeraHost.Core.Sync.Peers.Count > 0;

    /// <summary>Подписаться на изменения ядра (один раз за процесс).</summary>
    public static void Init(VeraCore core)
    {
        if (_hooked) return;
        _hooked = true;
        core.Sync.LastActive = () => AppState.LastActive;
        if (core.Sync.DeviceName == "Телефон")
        {
            try
            {
                var name = DeviceInfo.Current.Name;
                if (!string.IsNullOrWhiteSpace(name)) core.Sync.DeviceName = name;
            }
            catch { /* имя устройства недоступно — остаётся «Телефон» */ }
        }
        core.Reminders.Changed += Soon;
        core.Memory.Changed += Soon;
        core.Agent.Event += e => { if (e is TurnEnded) Soon(); };
    }

    /// <summary>Синхронизироваться через пару секунд (склеивает частые изменения).</summary>
    public static void Soon()
    {
        if (!Paired) return;
        _soon?.Cancel();
        var cts = _soon = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(4000, cts.Token);
                await SyncNowAsync(quiet: true);
            }
            catch (OperationCanceledException) { }
        });
    }

    /// <summary>Обменяться изменениями со всеми привязанными ПК. Возвращает итог (null — нет ПК или уже идёт).</summary>
    public static async Task<SyncResult?> SyncNowAsync(bool quiet)
    {
        var core = VeraHost.Core;
        var peers = core.Sync.Peers;
        if (peers.Count == 0 || Interlocked.Exchange(ref _running, 1) == 1) return null;
        SyncResult? last = null;
        try
        {
            foreach (var pc in peers)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
                last = await core.Sync.SyncAsync(pc, cts.Token);
            }
            LastError = null;
            core.Flush();
        }
        catch (Exception e)
        {
            LastError = e is SyncException ? e.Message : "Не удалось связаться с ПК: " + e.Message;
            if (!quiet) throw;
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
            StateChanged?.Invoke();
        }
        return last;
    }

    /// <summary>Фоновая синхронизация раз в ~15 минут (пересоздаётся при каждом запуске — это безопасно).</summary>
    public static void ScheduleBackground()
    {
        try
        {
            var ctx = Android.App.Application.Context;
            var am = (AlarmManager?)ctx.GetSystemService(Context.AlarmService);
            if (am is null) return;
            var pi = SyncAlarmReceiver.Intent(ctx);
            if (!Paired)
            {
                am.Cancel(pi);
                return;
            }
            var first = Java.Lang.JavaSystem.CurrentTimeMillis() + 15 * 60 * 1000L;
            am.SetInexactRepeating(AlarmType.Rtc, first, AlarmManager.IntervalFifteenMinutes, pi);
        }
        catch (Exception e)
        {
            Android.Util.Log.Warn("Vera", "sync schedule: " + e.Message);
        }
    }
}

/// <summary>Будильник фоновой синхронизации.</summary>
[BroadcastReceiver(Name = "com.vera.assistant.SyncAlarmReceiver", Enabled = true, Exported = false)]
public sealed class SyncAlarmReceiver : BroadcastReceiver
{
    public static PendingIntent Intent(Context ctx)
    {
        var intent = new Intent(ctx, typeof(SyncAlarmReceiver));
        intent.SetAction("com.vera.assistant.SYNC");
        return PendingIntent.GetBroadcast(ctx, 7001, intent, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable)!;
    }

    public override void OnReceive(Context? context, Intent? intent)
    {
        // приложение открыто — синхронизирует само
        if (AppState.IsForeground) return;
        var pending = GoAsync();
        _ = Task.Run(async () =>
        {
            try
            {
                var core = VeraHost.Core;
                PhoneSync.Init(core);
                await PhoneSync.SyncNowAsync(quiet: true);
                ReminderScheduler.Reschedule(core);
            }
            catch (Exception e)
            {
                Android.Util.Log.Warn("Vera", "background sync: " + e.Message);
            }
            finally
            {
                pending?.Finish();
            }
        });
    }
}
