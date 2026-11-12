using Vera.Core;
using Vera.Mobile.Platform;

namespace Vera.Mobile.Views;

/// <summary>Напоминания: добавить вручную (с быстрыми кнопками), список активных и сработавших, удаление.</summary>
public sealed class RemindersPage : ContentPage
{
    private static readonly (string Id, string Label)[] RepeatItems =
        ReminderService.Repeats.Select(r => (r, ReminderService.RepeatLabel(r))).ToArray();

    private VeraCore Core => App.Core;
    private readonly Entry _text;
    private readonly DatePicker _date;
    private readonly TimePicker _time;
    private readonly Picker _repeat;
    private readonly VerticalStackLayout _active = new() { Spacing = 8 };
    private readonly VerticalStackLayout _done = new() { Spacing = 8, IsVisible = false };
    private readonly Label _activeTitle = Ui.Label("Активные", 15, Ui.Text, bold: true);
    private readonly Button _doneToggle;
    private readonly VerticalStackLayout _warnings = new() { Spacing = 8 };
    private IDispatcherTimer? _timer;

    public RemindersPage()
    {
        Ui.StylePage(this, "Напоминания");

        _text = Ui.Entry("О чём напомнить?");
        _text.MaxLength = 300;
        _text.ReturnType = ReturnType.Done;
        _text.Completed += (_, _) => Add();
        _date = new DatePicker { TextColor = Ui.Text, FontSize = 15, MinimumDate = DateTime.Today, Format = "d MMM yyyy", BackgroundColor = Colors.Transparent };
        _time = new TimePicker { TextColor = Ui.Text, FontSize = 15, Format = "HH:mm", BackgroundColor = Colors.Transparent };
        _repeat = Ui.Picker(RepeatItems.Select(r => r.Label), "Повтор");
        _repeat.SelectedIndex = 0;
        ResetWhen();

        var quick = new FlexLayout { Wrap = Microsoft.Maui.Layouts.FlexWrap.Wrap };
        foreach (var (label, act) in new (string, Func<DateTime>)[]
                 {
                     ("+5 мин", () => DateTime.Now.AddMinutes(5)),
                     ("+15 мин", () => DateTime.Now.AddMinutes(15)),
                     ("+30 мин", () => DateTime.Now.AddMinutes(30)),
                     ("+1 ч", () => DateTime.Now.AddHours(1)),
                     ("завтра 9:00", () => DateTime.Today.AddDays(1).AddHours(9)),
                 })
        {
            var b = Ui.Chip(label, (_, _) => SetWhen(act()));
            b.Margin = new Thickness(0, 0, 6, 6);
            quick.Children.Add(b);
        }

        var whenRow = new Grid
        {
            ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(new GridLength(100)) },
            ColumnSpacing = 8,
        };
        whenRow.Add(Ui.Box(_date), 0);
        whenRow.Add(Ui.Box(_time), 1);

        var addCard = Ui.Card(
            Ui.H3("Новое напоминание"),
            Ui.MutedLabel("Или просто скажи Вере: «напомни завтра в 9 позвонить маме» — она поставит сама."),
            Ui.Box(_text),
            whenRow,
            Ui.Box(_repeat),
            quick,
            Ui.Primary("＋ Добавить", (_, _) => Add()));

        _doneToggle = Ui.Ghost("Сработавшие", (_, _) =>
        {
            _done.IsVisible = !_done.IsVisible;
            Render();
        });
        _doneToggle.HorizontalOptions = LayoutOptions.Start;

        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Spacing = 14,
                Padding = new Thickness(14, 12, 14, 28),
                Children = { _warnings, addCard, _activeTitle, _active, _doneToggle, _done },
            },
        };

    }

    private void OnChanged() => MainThread.BeginInvokeOnMainThread(Render);

    protected override void OnAppearing()
    {
        base.OnAppearing();
        Core.Reminders.Changed += OnChanged;
        Render();
        RenderWarnings();
        _timer = Dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(30);
        _timer.Tick += (_, _) => Render();
        _timer.Start();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        Core.Reminders.Changed -= OnChanged;
        _timer?.Stop();
        _timer = null;
    }

    private void ResetWhen()
    {
        var d = DateTime.Now.AddHours(1);
        d = d.AddMinutes((5 - d.Minute % 5) % 5);
        SetWhen(new DateTime(d.Year, d.Month, d.Day, d.Hour, d.Minute, 0));
    }

    private void SetWhen(DateTime t)
    {
        _date.MinimumDate = DateTime.Today;
        _date.Date = t.Date;
        _time.Time = new TimeSpan(t.Hour, t.Minute, 0);
    }

    private DateTime SelectedWhen()
    {
        DateTime? d = _date.Date;
        TimeSpan? t = _time.Time;
        return (d ?? DateTime.Today).Date + (t ?? TimeSpan.Zero);
    }

    private void Add()
    {
        var text = (_text.Text ?? "").Trim();
        if (text.Length == 0) { Ui.Toast("Напиши, о чём напомнить"); _text.Focus(); return; }
        var when = SelectedWhen();
        var repeat = RepeatItems[Math.Max(0, _repeat.SelectedIndex)].Id;
        try
        {
            var r = Core.Reminders.Add(text, new DateTimeOffset(when), repeat, "user");
            _text.Text = "";
            _text.Unfocus();
            ResetWhen();
            _repeat.SelectedIndex = 0;
            Ui.Toast($"Готово: {r.DueText}");
            _ = EnsureNotificationPermissionAsync();
        }
        catch (Exception e)
        {
            Ui.Toast(e.Message, true);
        }
    }

    private static async Task EnsureNotificationPermissionAsync()
    {
        if (Android.OS.Build.VERSION.SdkInt < Android.OS.BuildVersionCodes.Tiramisu) return;
        try
        {
            if (await Permissions.CheckStatusAsync<Permissions.PostNotifications>() != PermissionStatus.Granted)
                await Permissions.RequestAsync<Permissions.PostNotifications>();
        }
        catch { }
    }

    private void RenderWarnings()
    {
        _warnings.Children.Clear();
        if (!ReminderNotifier.Enabled())
        {
            _warnings.Children.Add(WarningCard("🔕 Уведомления выключены — когда приложение закрыто, Вера не сможет напомнить.",
                "Разрешить", () => PermissionsUi.OpenNotificationSettings()));
        }
        if (!ReminderScheduler.CanScheduleExact())
        {
            _warnings.Children.Add(WarningCard("⏱️ Нет разрешения на точные будильники — напоминания могут приходить с опозданием.",
                "Разрешить", () => PermissionsUi.OpenExactAlarmSettings()));
        }
    }

    private static Border WarningCard(string text, string button, Action action)
    {
        var b = Ui.Ghost(button, (_, _) => action(), Ui.Warn);
        b.HorizontalOptions = LayoutOptions.Start;
        return new Border
        {
            BackgroundColor = Ui.Warn.WithAlpha(0.10f),
            Stroke = Ui.Warn.WithAlpha(0.35f),
            StrokeThickness = 1,
            StrokeShape = Ui.Round(14),
            Padding = new Thickness(14, 10),
            Content = new VerticalStackLayout { Spacing = 8, Children = { Ui.Label(text, 14, Ui.Text), b } },
        };
    }

    private void Render()
    {
        var all = Core.Reminders.List();
        var active = all.Where(r => !r.Done).ToList();
        var done = all.Where(r => r.Done).OrderByDescending(r => r.FiredAt ?? r.Due).ToList();
        _activeTitle.Text = active.Count > 0 ? $"Активные ({active.Count})" : "Активные";
        _doneToggle.Text = (_done.IsVisible ? "▾ " : "▸ ") + "Сработавшие" + (done.Count > 0 ? $" ({done.Count})" : "");

        _active.Children.Clear();
        if (active.Count == 0) _active.Children.Add(Ui.MutedLabel("Пусто. Скажи Вере «напомни через 10 минут…» 😉", 14));
        foreach (var r in active) _active.Children.Add(Item(r));

        _done.Children.Clear();
        if (done.Count == 0) _done.Children.Add(Ui.MutedLabel("Ничего не срабатывало", 14));
        foreach (var r in done) _done.Children.Add(Item(r));
    }

    private View Item(Reminder r)
    {
        var soon = !r.Done && r.Due - DateTimeOffset.Now < TimeSpan.FromMinutes(15);
        var tags = new FlexLayout { Wrap = Microsoft.Maui.Layouts.FlexWrap.Wrap };
        void Tag(string t, Color? bg = null, Color? fg = null)
        {
            var tag = Ui.Tag(t, bg, fg);
            tag.Margin = new Thickness(0, 0, 6, 4);
            tags.Children.Add(tag);
        }
        Tag(RuText.WhenShort(r.Due), soon ? Ui.AccentSoft : null, soon ? Color.FromArgb("#f6c4e0") : Ui.Text);
        Tag(r.Done ? "сработало" : RuText.Relative(r.Due));
        if (r.Repeat != "none") Tag("🔁 " + r.RepeatText);
        if (r.Source == "ai") Tag("от Веры");

        var actions = new HorizontalStackLayout { Spacing = 0, VerticalOptions = LayoutOptions.Center };
        if (r.Done)
        {
            actions.Children.Add(Ui.Icon("ic_clock.png", (_, _) =>
            {
                Core.Reminders.Snooze(r.Id, 10);
                Ui.Toast("Напомню через 10 минут");
            }, 40, 9));
        }
        actions.Children.Add(Ui.Icon("ic_trash.png", async (_, _) =>
        {
            if (!await DisplayAlertAsync("Удалить напоминание?", r.Text, "Удалить", "Отмена")) return;
            Core.Reminders.Remove(r.Id);
            ReminderNotifier.Cancel(r.Id);
        }, 40, 9));

        var g = new Grid
        {
            ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) },
            ColumnSpacing = 6,
        };
        g.Add(new VerticalStackLayout
        {
            Spacing = 6,
            Children =
            {
                new Label { Text = r.Text, FontSize = 15.5, TextColor = r.Done ? Ui.Muted : Ui.Text, TextDecorations = r.Done ? TextDecorations.Strikethrough : TextDecorations.None },
                tags,
            },
        }, 0);
        g.Add(actions, 1);
        return new Border
        {
            BackgroundColor = Ui.Surface,
            Stroke = soon ? Ui.Accent.WithAlpha(0.5f) : Ui.Stroke,
            StrokeThickness = 1,
            StrokeShape = Ui.Round(14),
            Padding = new Thickness(14, 10, 6, 10),
            Content = g,
        };
    }
}

/// <summary>Переходы в системные настройки разрешений.</summary>
public static class PermissionsUi
{
    private static void Start(Android.Content.Intent i)
    {
        try
        {
            var a = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity;
            if (a is not null) a.StartActivity(i);
            else
            {
                i.AddFlags(Android.Content.ActivityFlags.NewTask);
                Android.App.Application.Context.StartActivity(i);
            }
        }
        catch
        {
            AppInfo.Current.ShowSettingsUI();
        }
    }

    public static void OpenNotificationSettings()
    {
        var ctx = Android.App.Application.Context;
        var i = new Android.Content.Intent(Android.Provider.Settings.ActionAppNotificationSettings);
        i.PutExtra(Android.Provider.Settings.ExtraAppPackage, ctx.PackageName);
        Start(i);
    }

    public static void OpenExactAlarmSettings()
    {
        if (Android.OS.Build.VERSION.SdkInt < Android.OS.BuildVersionCodes.S) return;
        var ctx = Android.App.Application.Context;
        var i = new Android.Content.Intent(Android.Provider.Settings.ActionRequestScheduleExactAlarm, Android.Net.Uri.Parse("package:" + ctx.PackageName));
        Start(i);
    }

    public static void OpenTtsSettings()
    {
        Start(new Android.Content.Intent("com.android.settings.TTS_SETTINGS"));
    }
}
