using Vera.Core;
using Vera.Mobile.Platform;
using Vera.Mobile.Services;
using Vera.Mobile.Views;

namespace Vera.Mobile;

public partial class App : Application
{
    private static SpeechOutput? _speech;
    private static VoiceController? _voice;
    private IDispatcherTimer? _ticker;

    /// <summary>Ядро — один экземпляр на процесс (нужно и интерфейсу, и BroadcastReceiver).</summary>
    public static VeraCore Core => VeraHost.Core;

    public static SpeechOutput Speech => _speech ??= new SpeechOutput(Core);

    public static VoiceController Voice => _voice ??= new VoiceController(Core, Speech);

    /// <summary>Приложение открыли из уведомления о напоминании.</summary>
    public static string? PendingReminderId { get; set; }

    public static event Action<string>? ReminderOpened;

    public static void OpenReminder(string id)
    {
        if (ReminderOpened is null) PendingReminderId = id;
        else ReminderOpened.Invoke(id);
    }

    public App()
    {
        UserAppTheme = AppTheme.Dark;
        Resources.Add(new Style(typeof(NavigationPage))
        {
            Setters =
            {
                new Setter { Property = NavigationPage.BarBackgroundColorProperty, Value = Ui.Bg },
                new Setter { Property = NavigationPage.BarTextColorProperty, Value = Ui.Text },
                new Setter { Property = VisualElement.BackgroundColorProperty, Value = Ui.Bg },
            },
        });
        Core.Settings.Changed += s => MainThread.BeginInvokeOnMainThread(() => { _ = Voice.SyncWakeAsync(); });
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var nav = new NavigationPage(new ChatPage())
        {
            BarBackgroundColor = Ui.Bg,
            BarTextColor = Ui.Text,
            BackgroundColor = Ui.Bg,
        };
        var window = new Window(nav) { Title = "Вера" };
        window.Created += (_, _) => OnForeground();
        window.Resumed += (_, _) => OnForeground();
        window.Activated += (_, _) => { if (!AppState.IsForeground) OnForeground(); };
        window.Stopped += (_, _) => OnBackground();
        window.Destroying += (_, _) => OnBackground();
        return window;
    }

    private void OnForeground()
    {
        AppState.SetForeground(true);
        // пропущенные (сработавшие, пока приложение было закрыто) — повторно не покажутся благодаря журналу
        try { Core.Reminders.Tick(startup: true); } catch { }
        try { ReminderScheduler.Reschedule(Core); } catch { }
        if (_ticker is null)
        {
            // пока приложение на экране — проверяем точно по времени (будильник Android может опаздывать)
            _ticker = Dispatcher.CreateTimer();
            _ticker.Interval = TimeSpan.FromSeconds(5);
            _ticker.Tick += (_, _) => { try { Core.Reminders.Tick(); } catch { } };
        }
        _ticker.Start();
        _ = Voice.SyncWakeAsync();
    }

    private void OnBackground()
    {
        AppState.SetForeground(false);
        _ticker?.Stop();
        Voice.Suspend();
        VeraHost.Flush();
    }
}
