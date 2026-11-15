using System.Globalization;
using Vera.Core;
using Vera.Mobile.Platform;

namespace Vera.Mobile.Views;

/// <summary>
/// Напоминания («Ореол»): группы «Сегодня», «Завтра», дальше по датам; карточка со временем слева
/// (ближайшее — золотая колонка), тап по карточке — действия; внизу Вера с репликой и «Новое напоминание».
/// </summary>
public sealed class RemindersPage : ContentPage
{
    private static readonly CultureInfo Ru = new("ru-RU");

    private VeraCore Core => App.Core;
    private readonly VerticalStackLayout _list = new() { Spacing = 12, Padding = new Thickness(16, 6, 16, 12) };
    private readonly VerticalStackLayout _warnings = new() { Spacing = 8 };
    private readonly Image _veraArt;
    private readonly Label _veraSays;
    private string _veraPose = "";
    private bool _showDone;
    private IDispatcherTimer? _timer;

    public RemindersPage()
    {
        Ui.StylePage(this, "Напоминания");
        NavigationPage.SetHasNavigationBar(this, false);

        // Вера внизу: чиби сердится, если что-то пропущено, иначе машет; рядом — белый комикс-пузырь
        _veraArt = new Image { WidthRequest = 110, HeightRequest = 110, Aspect = Aspect.AspectFit, VerticalOptions = LayoutOptions.End };
        _veraSays = new Label { FontSize = 14, TextColor = Ui.InkText, LineBreakMode = LineBreakMode.WordWrap };
        var bubble = new Border
        {
            BackgroundColor = Ui.Paper,
            Stroke = Ui.Ink,
            StrokeThickness = 2.5,
            StrokeShape = Ui.Round(18, 18, 6, 18),
            Padding = new Thickness(14, 11),
            Shadow = Ui.HardShadow(3),
            Margin = new Thickness(0, 0, 3, 30),
            VerticalOptions = LayoutOptions.End,
            Content = _veraSays,
        };
        var veraRow = new Grid
        {
            ColumnDefinitions = { new ColumnDefinition(new GridLength(110)), new ColumnDefinition(GridLength.Star) },
            ColumnSpacing = 10,
        };
        veraRow.Add(_veraArt, 0);
        veraRow.Add(bubble, 1);

        var add = Ui.Primary("Новое напоминание", async (_, _) => await EditAsync(null));
        add.CornerRadius = 16;
        add.MinimumHeightRequest = 52;
        add.Shadow = Ui.HardShadow(4);
        add.Margin = new Thickness(0, 0, 4, 4);

        var root = new Grid
        {
            RowDefinitions = { new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Star), new RowDefinition(GridLength.Auto) },
            BackgroundColor = Ui.Bg,
        };
        root.Add(Ui.PageHeader(this, "Напоминания"), 0, 0);
        root.Add(new ScrollView { Content = _list }, 0, 1);
        root.Add(new VerticalStackLayout { Spacing = 12, Padding = new Thickness(16, 4, 16, 16), Children = { veraRow, add } }, 0, 2);
        Content = root;
    }

    private void OnChanged() => MainThread.BeginInvokeOnMainThread(Render);

    protected override void OnAppearing()
    {
        base.OnAppearing();
        Core.Reminders.Changed += OnChanged;
        RenderWarnings();
        Render();
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

    private async Task EditAsync(Reminder? r)
    {
        try { await Navigation.PushAsync(new ReminderEditPage(r)); } catch { }
    }

    internal static async Task EnsureNotificationPermissionAsync()
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
            _warnings.Children.Add(WarningCard("Уведомления выключены — когда приложение закрыто, Вера не сможет напомнить.",
                "Разрешить", () => PermissionsUi.OpenNotificationSettings()));
        }
        if (!ReminderScheduler.CanScheduleExact())
        {
            _warnings.Children.Add(WarningCard("Нет разрешения на точные будильники — напоминания могут приходить с опозданием.",
                "Разрешить", () => PermissionsUi.OpenExactAlarmSettings()));
        }
        _warnings.IsVisible = _warnings.Children.Count > 0;
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

    private static string Short(string s, int n) => s.Length > n ? s[..(n - 1)].TrimEnd() + "…" : s;

    /// <summary>Сработало с опозданием (приложение было закрыто) в последние сутки — или висит просроченным.</summary>
    private static Reminder? Missed(IReadOnlyList<Reminder> all, DateTimeOffset now) =>
        all.Where(r => r.Done && r.FiredAt is { } f && f - r.Due > TimeSpan.FromMinutes(2) && now - f < TimeSpan.FromHours(24))
            .OrderByDescending(r => r.FiredAt)
            .FirstOrDefault()
        ?? all.FirstOrDefault(r => !r.Done && now - r.Due > TimeSpan.FromMinutes(2));

    private static string GroupName(DateTimeOffset due, DateTimeOffset now)
    {
        var d = due.LocalDateTime.Date;
        var today = now.LocalDateTime.Date;
        if (d < today) return "Просрочено";
        if (d == today) return "Сегодня";
        if (d == today.AddDays(1)) return "Завтра";
        return d.ToString(d.Year == today.Year ? "dddd, d MMMM" : "d MMMM yyyy", Ru);
    }

    private void Render()
    {
        var now = DateTimeOffset.Now;
        var all = Core.Reminders.List();
        var active = all.Where(r => !r.Done).OrderBy(r => r.Due).ToList();
        var done = all.Where(r => r.Done).OrderByDescending(r => r.FiredAt ?? r.Due).ToList();
        var next = active.FirstOrDefault(r => r.Due >= now) ?? active.FirstOrDefault();
        var me = Core.Sync.DeviceId;
        var peers = Core.Sync.Peers;
        string? ring = null;
        if (peers.Count > 0)
        {
            ring = Core.Settings.Current.Sync.RingOn switch
            {
                "active" => "звенит там, где ты",
                "pc" => "звенит на ПК",
                "phone" => "звенит на телефоне",
                _ => "звенит на обоих",
            };
        }

        _list.Children.Clear();
        if (_warnings.IsVisible) _list.Children.Add(_warnings);
        if (active.Count == 0)
            _list.Children.Add(new Label { Text = "Активных напоминаний нет.", FontSize = 14, TextColor = Ui.Muted, Margin = new Thickness(0, 8, 0, 0) });

        string? group = null;
        foreach (var r in active)
        {
            var g = GroupName(r.Due, now);
            if (g != group)
            {
                var caps = Ui.Caps(g);
                if (group is not null) caps.Margin = new Thickness(0, 8, 0, 0);
                _list.Children.Add(caps);
                group = g;
            }
            _list.Children.Add(Row(r, next is not null && r.Id == next.Id, now, me, peers, ring));
        }

        if (done.Count > 0)
        {
            var toggle = new Label
            {
                Text = (_showDone ? "▾ " : "▸ ") + $"Сработавшие ({done.Count})".ToUpperInvariant(),
                FontSize = 12,
                FontFamily = Ui.FontBold,
                CharacterSpacing = 1,
                TextColor = Ui.Faint,
                Padding = new Thickness(0, 10, 0, 4),
            };
            var tap = new TapGestureRecognizer();
            tap.Tapped += (_, _) => { _showDone = !_showDone; Render(); };
            toggle.GestureRecognizers.Add(tap);
            _list.Children.Add(toggle);
            if (_showDone)
                foreach (var r in done) _list.Children.Add(Row(r, false, now, me, peers, null));
        }

        // реплика Веры
        var missed = Missed(all, now);
        string pose, say;
        if (missed is not null)
        {
            pose = "angry";
            var yesterday = missed.Due.LocalDateTime.Date == now.LocalDateTime.Date.AddDays(-1);
            say = (yesterday ? "Вчера ты проспал" : "Ты проспал") + $" «{Short(missed.Text, 40)}». " +
                  (peers.Count > 0 ? "Ещё раз — и я начну звенеть на всех устройствах сразу!" : "Ещё раз — и я начну звенеть громче!");
        }
        else if (next is not null)
        {
            pose = "wave";
            say = $"Следующее — «{Short(next.Text, 40)}» {RuText.Relative(next.Due)}. Я прослежу~";
        }
        else
        {
            pose = "wave";
            say = "Пока пусто. Скажи мне в чате «напомни через 10 минут…» — поставлю сама.";
        }
        _veraSays.Text = say;
        if (pose != _veraPose)
        {
            _veraPose = pose;
            _veraArt.Source = Ui.Asset($"skins/chibi/{pose}.png");
        }
    }

    /// <summary>Карточка: слева время и подпись, справа текст и откуда напоминание.</summary>
    private View Row(Reminder r, bool next, DateTimeOffset now, string me, IReadOnlyList<Vera.Core.Sync.SyncPeer> peers, string? ring)
    {
        var local = r.Due.LocalDateTime;
        var overdue = !r.Done && r.Due < now.AddMinutes(-1);
        string sub;
        if (r.Done) sub = "сработало";
        else if (overdue) sub = "просрочено";
        else if (r.Repeat != "none" && !next) sub = r.RepeatText;
        else if (next || local.Date == now.LocalDateTime.Date) sub = RuText.Relative(r.Due);
        else sub = local.ToString("ddd", Ru);

        var time = new Label
        {
            Text = local.ToString("HH:mm", CultureInfo.InvariantCulture),
            FontSize = 18,
            FontFamily = Ui.Display,
            TextColor = next ? Ui.OnAccent : Ui.Text,
            HorizontalTextAlignment = TextAlignment.Center,
        };
        var subLabel = new Label
        {
            Text = sub,
            FontSize = 11,
            FontFamily = Ui.FontBold,
            TextColor = next ? Ui.OnAccent : overdue ? Ui.Danger : Ui.Muted,
            HorizontalTextAlignment = TextAlignment.Center,
            LineBreakMode = LineBreakMode.WordWrap,
            MaxLines = 2,
        };
        var left = new Border
        {
            BackgroundColor = next ? Ui.Accent : Ui.Surface2,
            StrokeThickness = 0,
            StrokeShape = Ui.Round(16, 0, 16, 0),
            Padding = new Thickness(4, 10),
            Content = new VerticalStackLayout { Spacing = 1, VerticalOptions = LayoutOptions.Center, Children = { time, subLabel } },
        };
        var divider = new BoxView { WidthRequest = next ? 2.5 : 2, Color = next ? Ui.Ink : Ui.Surface3 };

        // подпись справа: повтор (если не показан слева), кто поставил, где создано, где звенит
        var parts = new List<string>();
        if (r.Repeat != "none" && (next || r.Done || overdue)) parts.Add(r.RepeatText);
        if (r.Source == "ai") parts.Add("поставила Вера");
        else if (r.Source == "snooze") parts.Add("отложено");
        if (!string.IsNullOrEmpty(r.Dev) && r.Dev != me)
            parts.Add(peers.FirstOrDefault(p => p.Id == r.Dev)?.Platform == "mobile" ? "с другого телефона" : "создано на ПК");
        if (next && ring is not null) parts.Add(ring);
        if (r.Done && r.FiredAt is { } f) parts.Add(RuText.WhenShort(f));
        if (parts.Count == 0) parts.Add(r.Repeat == "none" ? "однократно" : "повторяется");

        var right = new VerticalStackLayout
        {
            Spacing = 4,
            Padding = new Thickness(14, 12),
            VerticalOptions = LayoutOptions.Center,
            Children =
            {
                new Label
                {
                    Text = r.Text,
                    FontSize = 15,
                    FontFamily = Ui.FontBold,
                    TextColor = r.Done ? Ui.Muted : Ui.Text,
                    TextDecorations = r.Done ? TextDecorations.Strikethrough : TextDecorations.None,
                    LineBreakMode = LineBreakMode.WordWrap,
                },
                new Label { Text = string.Join(" · ", parts), FontSize = 12, TextColor = Ui.Muted, LineBreakMode = LineBreakMode.WordWrap },
            },
        };

        var g = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(new GridLength(78)), new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star),
            },
        };
        g.Add(left, 0);
        g.Add(divider, 1);
        g.Add(right, 2);

        // ближайшее — чёрная обводка и жёсткая тень, остальные — тихая рамка
        var card = new Border
        {
            BackgroundColor = Ui.Surface,
            Stroke = next ? Ui.Ink : Ui.Surface3,
            StrokeThickness = next ? 2.5 : 2,
            StrokeShape = Ui.Round(18),
            Padding = new Thickness(0),
            Margin = next ? new Thickness(0, 0, 3, 3) : new Thickness(0),
            Opacity = r.Done ? 0.65 : 1,
            Content = g,
        };
        if (next) card.Shadow = Ui.HardShadow(3);
        var tap = new TapGestureRecognizer();
        tap.Tapped += async (_, _) => await ActionsAsync(r);
        card.GestureRecognizers.Add(tap);
        return card;
    }

    /// <summary>Действия по тапу: выполнено, отложить, изменить, удалить.</summary>
    private async Task ActionsAsync(Reminder r)
    {
        const string done = "Выполнено", later10 = "Отложить на 10 мин", later60 = "Отложить на час",
            again = "Напомнить через 10 мин", edit = "Изменить", delete = "Удалить";
        var opts = new List<string>();
        if (r.Done) opts.Add(again);
        else
        {
            if (r.Repeat == "none") opts.AddRange(new[] { done, later10, later60 });
        }
        opts.Add(edit);
        var choice = await DisplayActionSheetAsync(Short(r.Text, 60), "Отмена", delete, opts.ToArray());
        try
        {
            switch (choice)
            {
                case done:
                    Core.Reminders.Complete(r.Id);
                    ReminderNotifier.Cancel(r.Id);
                    Ui.Toast("Отмечено как выполненное");
                    break;
                case later10:
                case later60:
                {
                    // отложить — сдвинуть позже от назначенного времени (или от «сейчас», если уже прошло)
                    var from = r.Due > DateTimeOffset.Now ? r.Due : DateTimeOffset.Now;
                    var due = from.AddMinutes(choice == later10 ? 10 : 60);
                    Core.Reminders.Update(r.Id, due: due);
                    ReminderNotifier.Cancel(r.Id);
                    Ui.Toast("Перенесено на " + RuText.WhenShort(due));
                    break;
                }
                case again:
                    Core.Reminders.Snooze(r.Id, 10);
                    ReminderNotifier.Cancel(r.Id);
                    Ui.Toast("Напомню через 10 минут");
                    break;
                case edit:
                    await EditAsync(r);
                    break;
                case delete:
                    if (!await DisplayAlertAsync("Удалить напоминание?", r.Text, "Удалить", "Отмена")) return;
                    Core.Reminders.Remove(r.Id);
                    ReminderNotifier.Cancel(r.Id);
                    break;
            }
        }
        catch (Exception e)
        {
            Ui.Toast(e.Message, true);
        }
    }
}

/// <summary>Создание и правка напоминания: текст, дата, время, повтор, быстрые кнопки.</summary>
public sealed class ReminderEditPage : ContentPage
{
    private static readonly (string Id, string Label)[] RepeatItems =
        ReminderService.Repeats.Select(r => (r, ReminderService.RepeatLabel(r))).ToArray();

    private VeraCore Core => App.Core;
    private readonly Reminder? _orig;
    private readonly Entry _text;
    private readonly DatePicker _date;
    private readonly TimePicker _time;
    private readonly Picker _repeat;

    public ReminderEditPage(Reminder? r)
    {
        _orig = r;
        var title = r is null ? "Новое напоминание" : "Изменить";
        Ui.StylePage(this, title);
        NavigationPage.SetHasNavigationBar(this, false);

        _text = Ui.Entry("О чём напомнить?");
        _text.MaxLength = 300;
        _text.ReturnType = ReturnType.Done;
        _text.Completed += async (_, _) => await SaveAsync();
        _date = new DatePicker { TextColor = Ui.Text, FontSize = 15, MinimumDate = DateTime.Today, Format = "d MMM yyyy", BackgroundColor = Colors.Transparent };
        _time = new TimePicker { TextColor = Ui.Text, FontSize = 15, Format = "HH:mm", BackgroundColor = Colors.Transparent };
        _repeat = Ui.Picker(RepeatItems.Select(x => x.Label), "Повтор");
        _repeat.SelectedIndex = 0;
        if (r is null) ResetWhen();
        else
        {
            _text.Text = r.Text;
            var due = r.Due.LocalDateTime;
            SetWhen(due < DateTime.Now ? DateTime.Now.AddMinutes(10) : due);
            _repeat.SelectedIndex = Math.Max(0, Array.FindIndex(RepeatItems, x => x.Id == r.Repeat));
        }

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

        var fields = new List<IView>();
        if (r is null) fields.Add(Ui.MutedLabel("Или просто скажи Вере: «напомни завтра в 9 позвонить маме» — она поставит сама."));
        fields.Add(Ui.Field("О чём напомнить", Ui.Box(_text)));
        fields.Add(Ui.Field("Когда", whenRow));
        fields.Add(quick);
        fields.Add(Ui.Field("Повтор", Ui.Box(_repeat)));
        var card = Ui.Card(fields.ToArray());

        var save = Ui.Primary(r is null ? "Добавить" : "Сохранить", async (_, _) => await SaveAsync());
        save.CornerRadius = 16;
        save.MinimumHeightRequest = 52;
        save.Shadow = Ui.HardShadow(4);
        save.Margin = new Thickness(0, 0, 4, 4);

        var root = new Grid
        {
            RowDefinitions = { new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Star) },
            BackgroundColor = Ui.Bg,
        };
        root.Add(Ui.PageHeader(this, title), 0, 0);
        root.Add(new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Spacing = 14,
                Padding = new Thickness(16, 6, 16, 28),
                Children = { card, save },
            },
        }, 0, 1);
        Content = root;
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

    private bool _saving;

    /// <summary>Повторяющееся с прошедшим временем — на ближайший будущий повтор.</summary>
    private static DateTimeOffset NextFuture(DateTimeOffset t, string repeat)
    {
        var now = DateTimeOffset.Now;
        for (var i = 0; i < 2000 && t <= now; i++)
        {
            t = repeat switch
            {
                "hourly" => t.AddHours(1),
                "weekly" => t.AddDays(7),
                "monthly" => t.AddMonths(1),
                _ => t.AddDays(1),
            };
            if (repeat == "weekdays")
                while (t.LocalDateTime.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) t = t.AddDays(1);
        }
        return t;
    }

    private async Task SaveAsync()
    {
        if (_saving) return;
        var text = (_text.Text ?? "").Trim();
        if (text.Length == 0) { Ui.Toast("Напиши, о чём напомнить"); _text.Focus(); return; }
        var when = new DateTimeOffset(SelectedWhen());
        var repeat = RepeatItems[Math.Max(0, _repeat.SelectedIndex)].Id;
        try
        {
            if (_orig is null)
            {
                var r = Core.Reminders.Add(text, when, repeat, "user");
                Ui.Toast($"Готово: {r.DueText}");
            }
            else
            {
                if (repeat != "none") when = NextFuture(when, repeat);
                else if (when <= DateTimeOffset.Now) { Ui.Toast("Это время уже прошло", true); return; }
                var r = Core.Reminders.Update(_orig.Id, text, when, repeat);
                if (r is null) { Ui.Toast("Напоминание не найдено — возможно, его удалили", true); return; }
                ReminderNotifier.Cancel(_orig.Id);
                Ui.Toast($"Сохранено: {r.DueText}");
            }
        }
        catch (Exception e)
        {
            Ui.Toast(e.Message, true);
            return;
        }
        _saving = true;
        _text.Unfocus();
        _ = RemindersPage.EnsureNotificationPermissionAsync();
        try { await Navigation.PopAsync(); } catch { }
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
