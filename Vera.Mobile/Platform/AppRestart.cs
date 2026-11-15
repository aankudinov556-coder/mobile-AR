using Android.App;
using Android.Content;
using Vera.Mobile.Services;

namespace Vera.Mobile.Platform;

/// <summary>Перезапуск приложения (после смены языка): сохранить всё, запланировать запуск и завершить процесс.</summary>
public static class AppRestart
{
    public static void Run()
    {
        VeraHost.Flush();
        var ctx = Android.App.Application.Context;
        var intent = ctx.PackageManager?.GetLaunchIntentForPackage(ctx.PackageName!);
        if (intent is not null)
        {
            intent.AddFlags(ActivityFlags.ClearTask | ActivityFlags.NewTask);
            var pending = PendingIntent.GetActivity(ctx, 4711, intent, PendingIntentFlags.CancelCurrent | PendingIntentFlags.Immutable);
            var alarms = (AlarmManager?)ctx.GetSystemService(Context.AlarmService);
            alarms?.Set(AlarmType.Rtc, Java.Lang.JavaSystem.CurrentTimeMillis() + 400, pending);
        }
        Microsoft.Maui.ApplicationModel.Platform.CurrentActivity?.FinishAffinity();
        Java.Lang.JavaSystem.Exit(0);
    }
}
