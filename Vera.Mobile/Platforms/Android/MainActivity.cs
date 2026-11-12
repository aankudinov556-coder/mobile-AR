using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using AndroidX.Core.View;

namespace Vera.Mobile;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, Exported = true,
    WindowSoftInputMode = Android.Views.SoftInput.AdjustResize,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout |
                           ConfigChanges.SmallestScreenSize | ConfigChanges.Density | ConfigChanges.KeyboardHidden)]
public class MainActivity : MauiAppCompatActivity
{
    public const string ExtraReminderId = "vera_reminder_id";

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        try
        {
            if (Window is { } w)
            {
                var c = WindowCompat.GetInsetsController(w, w.DecorView);
                c.AppearanceLightStatusBars = false;
                c.AppearanceLightNavigationBars = false;
                w.DecorView.SetBackgroundColor(Android.Graphics.Color.ParseColor("#15131c"));
            }
        }
        catch { }
        HandleIntent(Intent);
        _ = RequestNotificationsLaterAsync();
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        HandleIntent(intent);
    }

    private static void HandleIntent(Intent? intent)
    {
        var id = intent?.GetStringExtra(ExtraReminderId);
        if (string.IsNullOrEmpty(id)) return;
        intent!.RemoveExtra(ExtraReminderId);
        App.OpenReminder(id);
    }

    /// <summary>Android 13+: разрешение на уведомления спрашиваем один раз, после старта.</summary>
    private static async Task RequestNotificationsLaterAsync()
    {
        if (Build.VERSION.SdkInt < BuildVersionCodes.Tiramisu) return;
        await Task.Delay(2500);
        try
        {
            if (Preferences.Default.Get("asked_notifications", false)) return;
            Preferences.Default.Set("asked_notifications", true);
            await MainThread.InvokeOnMainThreadAsync(async () =>
            {
                if (await Permissions.CheckStatusAsync<Permissions.PostNotifications>() != PermissionStatus.Granted)
                    await Permissions.RequestAsync<Permissions.PostNotifications>();
            });
        }
        catch { }
    }
}
