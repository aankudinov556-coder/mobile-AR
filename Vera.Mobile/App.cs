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
        // шрифт Onest по умолчанию для всего текста
        foreach (var (type, prop) in new (Type, BindableProperty)[]
                 {
                     (typeof(Label), Label.FontFamilyProperty), (typeof(Button), Button.FontFamilyProperty),
                     (typeof(Entry), Entry.FontFamilyProperty), (typeof(Editor), Editor.FontFamilyProperty),
                     (typeof(Picker), Picker.FontFamilyProperty),
                 })
            Resources.Add(new Style(type) { Setters = { new Setter { Property = prop, Value = Ui.Font } } });
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
        var window = new Window(nav) { Title = L10n.T("Вера") };
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
        // синхронизация с ПК: сразу при открытии и раз в 15 минут, пока открыто
        PhoneSync.ScheduleBackground();
        PhoneSync.Soon();
        if (_syncTicker is null)
        {
            _syncTicker = Dispatcher.CreateTimer();
            _syncTicker.Interval = TimeSpan.FromMinutes(15);
            _syncTicker.Tick += (_, _) => _ = PhoneSync.SyncNowAsync(quiet: true);
        }
        _syncTicker.Start();
        if (PendingPairing is { } qr)
        {
            PendingPairing = null;
            OpenPairing(qr);
        }
    }

    private IDispatcherTimer? _syncTicker;

    /// <summary>Ссылка привязки из QR (vera-sync://pair?...), пришедшая до появления окна.</summary>
    public static string? PendingPairing { get; set; }

    /// <summary>Открыть экран синхронизации и привязать ПК по ссылке из QR-кода.</summary>
    public static void OpenPairing(string qr)
    {
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            try
            {
                if (Current?.Windows.FirstOrDefault()?.Page is NavigationPage nav)
                {
                    var page = new SyncPage();
                    await nav.PushAsync(page);
                    page.Pair(qr);
                }
                else PendingPairing = qr;
            }
            catch (Exception e)
            {
                Ui.Toast(L10n.T("Не получилось открыть привязку: ") + e.Message, true);
            }
        });
    }

    private void OnBackground()
    {
        AppState.SetForeground(false);
        _ticker?.Stop();
        _syncTicker?.Stop();
        Voice.Suspend();
        VeraHost.Flush();
    }
}
