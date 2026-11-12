using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Vera.Core;
using Vera.Desktop.UI;

namespace Vera.Desktop.Chat;

/// <summary>Вкладка «Напоминания»: добавление вручную, активные и сработавшие.</summary>
public sealed class RemindersPanel : Grid
{
    private static readonly CultureInfo Ru = new("ru-RU");
    private static readonly string[] Formats =
    {
        "dd.MM.yyyy HH:mm", "d.M.yyyy H:mm", "dd.MM.yy HH:mm", "d.M.yy H:mm", "dd.MM HH:mm", "d.M H:mm",
        "yyyy-MM-dd HH:mm", "yyyy-MM-ddTHH:mm",
    };

    private readonly AppHost _host;
    private readonly ChatWindow _window;
    private readonly TextBox _text;
    private readonly TextBox _when;
    private readonly ComboBox _repeat;
    private readonly StackPanel _active = new();
    private readonly StackPanel _done = new();
    private readonly TextBlock _activeCount;
    private readonly TextBlock _doneTitle;
    private readonly DispatcherTimer _tick;
    private bool _doneOpen;

    public RemindersPanel(AppHost host, ChatWindow window)
    {
        _host = host;
        _window = window;

        _text = new TextBox { Tag = "О чём напомнить?", MaxLength = 300 };
        _text.KeyDown += (_, e) => { if (e.Key == Key.Enter) Add(); };
        _when = new TextBox { Tag = "дд.мм.гггг чч:мм", ToolTip = "Например: 13.11.2026 09:30, или просто 18:00" };
        _when.KeyDown += (_, e) => { if (e.Key == Key.Enter) Add(); };
        _repeat = Ui.Combo(ReminderService.Repeats.Select(r => (r, ReminderService.RepeatLabel(r))));
        _repeat.SelectedIndex = 0;
        _repeat.MinWidth = 150;

        var chips = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        foreach (var (label, minutes) in new[] { ("+5 мин", 5), ("+15 мин", 15), ("+30 мин", 30), ("+1 ч", 60), ("завтра 9:00", -1) })
        {
            var b = new Button { Content = label, Style = Ui.Style("Btn.Chip"), Margin = new Thickness(0, 0, 6, 6) };
            var m = minutes;
            b.Click += (_, _) =>
            {
                var t = m > 0 ? DateTime.Now.AddMinutes(m) : DateTime.Today.AddDays(1).AddHours(9);
                _when.Text = t.ToString("dd.MM.yyyy HH:mm", Ru);
            };
            chips.Children.Add(b);
        }
        var add = Ui.Button("Добавить", "Btn.Primary", "plus", Add);
        add.Margin = new Thickness(0, 0, 0, 6);
        chips.Children.Add(add);

        var hint = Ui.Muted("Или просто скажи Вере: «напомни завтра в 9 позвонить маме» — она поставит сама.");
        hint.Margin = new Thickness(0, 4, 0, 10);
        var form = new StackPanel();
        form.Children.Add(_text);
        var row = Ui.Row(new UIElement[] { _when, _repeat }, 8, 0);
        row.Margin = new Thickness(0, 8, 0, 0);
        form.Children.Add(row);
        form.Children.Add(chips);
        var card = Ui.Card(Ui.Title("Новое напоминание"), hint, form);

        _activeCount = Ui.Muted("", 14);
        var activeTitle = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 6, 2, 8) };
        activeTitle.Children.Add(Ui.Text("Активные ", 14, "Fg", FontWeights.Bold));
        activeTitle.Children.Add(_activeCount);

        _doneTitle = Ui.Text("▸ Сработавшие", 14, "Muted", FontWeights.Bold);
        var doneHeader = new Button { Content = _doneTitle, Style = Ui.Style("Btn.Base"), HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(2, 4, 2, 4), Margin = new Thickness(0, 14, 0, 0) };
        doneHeader.Click += (_, _) => { _doneOpen = !_doneOpen; Render(); };
        _done.Margin = new Thickness(0, 8, 0, 0);

        var content = new StackPanel();
        content.Children.Add(card);
        content.Children.Add(activeTitle);
        content.Children.Add(_active);
        content.Children.Add(doneHeader);
        content.Children.Add(_done);
        Children.Add(Ui.Scroll(content));

        ResetWhen();
        _tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _tick.Tick += (_, _) => { if (IsVisible) Render(); };
        _tick.Start();
        Render();
    }

    private void ResetWhen()
    {
        var d = DateTime.Now.AddHours(1);
        d = d.AddMinutes((5 - d.Minute % 5) % 5).AddSeconds(-d.Second);
        _when.Text = d.ToString("dd.MM.yyyy HH:mm", Ru);
    }

    /// <summary>Разобрать «дд.мм.гггг чч:мм», «дд.мм чч:мм» или просто «чч:мм» (сегодня/завтра).</summary>
    private static DateTimeOffset? ParseWhen(string s)
    {
        s = (s ?? "").Trim().Replace(',', ' ');
        while (s.Contains("  ")) s = s.Replace("  ", " ");
        if (DateTime.TryParseExact(s, Formats, Ru, DateTimeStyles.AssumeLocal, out var dt)) return new DateTimeOffset(dt);
        if (TimeSpan.TryParseExact(s, new[] { @"h\:mm", @"hh\:mm" }, Ru, out var ts) && ts < TimeSpan.FromDays(1))
        {
            var t = DateTime.Today + ts;
            if (t <= DateTime.Now) t = t.AddDays(1);
            return new DateTimeOffset(t);
        }
        if (DateTime.TryParse(s, Ru, DateTimeStyles.AssumeLocal, out dt)) return new DateTimeOffset(dt);
        return null;
    }

    private void Add()
    {
        var text = _text.Text.Trim();
        if (text.Length == 0)
        {
            _window.Toast("Напиши, о чём напомнить", true);
            _text.Focus();
            return;
        }
        var due = ParseWhen(_when.Text);
        if (due is null)
        {
            _window.Toast("Не понимаю время. Формат: 13.11.2026 09:30", true);
            _when.Focus();
            return;
        }
        try
        {
            var r = _host.Core.Reminders.Add(text, due.Value, Ui.Selected(_repeat) ?? "none", "user");
            _text.Clear();
            ResetWhen();
            _window.Toast($"Готово: {r.DueText}");
            Render();
        }
        catch (Exception e)
        {
            _window.Toast(e.Message, true);
        }
    }

    public void Refresh() => Render();

    private void Render()
    {
        var list = _host.Core.Reminders.List();
        var active = list.Where(r => !r.Done).ToList();
        var done = list.Where(r => r.Done).OrderByDescending(r => r.FiredAt ?? r.Due).ToList();
        _activeCount.Text = active.Count > 0 ? $"({active.Count})" : "";
        _doneTitle.Text = (_doneOpen ? "▾ " : "▸ ") + "Сработавшие" + (done.Count > 0 ? $" ({done.Count})" : "");
        _window.SetReminderBadge(active.Count);

        _active.Children.Clear();
        foreach (var r in active) _active.Children.Add(Item(r));
        if (active.Count == 0) _active.Children.Add(EmptyNote("Пусто. Скажи Вере «напомни через 10 минут…» 😉"));

        _done.Children.Clear();
        _done.Visibility = _doneOpen ? Visibility.Visible : Visibility.Collapsed;
        foreach (var r in done) _done.Children.Add(Item(r));
        if (done.Count == 0) _done.Children.Add(EmptyNote("Ничего не срабатывало"));
    }

    public static Border EmptyNote(string text)
    {
        var t = Ui.Muted(text, 13.5);
        t.TextAlignment = TextAlignment.Center;
        var b = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(8, 18, 8, 18),
            Child = t,
        };
        b.SetResourceReference(Border.BorderBrushProperty, "Line");
        return b;
    }

    public static Border MakeTag(string text, string? icon = null)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        if (icon is not null) sp.Children.Add(Icons.Make(icon, 11));
        var t = Ui.Muted(text, 10.5);
        if (icon is not null) t.Margin = new Thickness(4, 0, 0, 0);
        sp.Children.Add(t);
        var b = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(7, 1, 7, 1), Child = sp, VerticalAlignment = VerticalAlignment.Center };
        b.SetResourceReference(Border.BackgroundProperty, "Bg3");
        b.SetResourceReference(TextElementForeground, "Muted");
        return b;
    }

    private static readonly DependencyProperty TextElementForeground = System.Windows.Documents.TextElement.ForegroundProperty;

    public static Border ItemFrame(UIElement content, bool accent = false)
    {
        var b = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(12, 10, 8, 10),
            Margin = new Thickness(0, 0, 0, 8),
            Child = content,
        };
        b.SetResourceReference(Border.BackgroundProperty, "Bg2");
        b.SetResourceReference(Border.BorderBrushProperty, accent ? "Accent" : "Line");
        return b;
    }

    private UIElement Item(Reminder r)
    {
        var title = Ui.Text(r.Text, 14, "Fg", FontWeights.SemiBold);
        var sub = new WrapPanel { Margin = new Thickness(0, 2, 0, 0) };
        var when = Ui.Text(RuText.WhenShort(r.Due), 12, "Accent", FontWeights.Bold, wrap: false);
        when.Margin = new Thickness(0, 0, 8, 0);
        sub.Children.Add(when);
        var rel = Ui.Muted(r.Done ? "сработало" : RuText.Relative(r.Due));
        rel.Margin = new Thickness(0, 0, 8, 0);
        sub.Children.Add(rel);
        if (r.Repeat != "none")
        {
            var tg = MakeTag(r.RepeatText, "repeat");
            tg.Margin = new Thickness(0, 0, 6, 0);
            sub.Children.Add(tg);
        }
        if (r.Source == "ai") sub.Children.Add(MakeTag("от Веры"));
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(title);
        text.Children.Add(sub);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var id = r.Id;
        if (r.Done)
            actions.Children.Add(Ui.IconButton("clock", "Повторить через 10 минут", () => Act(() => _host.Core.Reminders.Snooze(id, 10)), 30, 16));
        actions.Children.Add(Ui.IconButton("trash", "Удалить", () => Act(() => _host.Core.Reminders.Remove(id)), 30, 16));

        var soon = !r.Done && r.Due - DateTimeOffset.Now < TimeSpan.FromMinutes(15);
        var frame = ItemFrame(Ui.Row(new UIElement[] { text, actions }, 10, 0), soon);
        if (r.Done) frame.Opacity = 0.6;
        return frame;
    }

    private void Act(Action a)
    {
        try { a(); }
        catch (Exception e) { _window.Toast(e.Message, true); }
        Render();
    }
}
