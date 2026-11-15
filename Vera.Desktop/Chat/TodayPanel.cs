using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Vera.Core;
using Vera.Desktop.UI;

namespace Vera.Desktop.Chat;

/// <summary>Правая панель «Сегодня»: погода, ближайшие напоминания, что Вера помнит, телефон.</summary>
public sealed class TodayPanel : Border
{
    private readonly AppHost _host;
    private readonly StackPanel _weather = new();
    private readonly StackPanel _reminders = new();
    private readonly WrapPanel _memory = new();
    private readonly TextBlock _phone;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMinutes(1) };
    private DateTime _weatherAt = DateTime.MinValue;
    private string _weatherCity = "";

    public TodayPanel(AppHost host)
    {
        _host = host;
        Width = 300;
        BorderThickness = new Thickness(2, 0, 0, 0);
        this.Res(BackgroundProperty, "Side");
        this.Res(BorderBrushProperty, "Bg3");
        Padding = new Thickness(18);

        _phone = Ui.Muted("", 12);
        _phone.TextWrapping = TextWrapping.Wrap;

        var hint = new Border { BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(14), Padding = new Thickness(10, 8, 10, 8) };
        hint.SetResourceReference(Border.BorderBrushProperty, "Line");
        var lie = new Image { Source = Vera.Desktop.Pet.SpriteSheet.LoadImage("/assets/skins/halo/lie.png"), Width = 58, Height = 38, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 10, 0) };
        RenderOptions.SetBitmapScalingMode(lie, BitmapScalingMode.HighQuality);
        var hintText = Ui.Muted("Станет скучно — лягу на панели задач и подожду", 12);
        hint.Child = Ui.Row(new UIElement[] { lie, hintText }, 0, 1);

        var root = new DockPanel { LastChildFill = false };
        void Top(UIElement e) { DockPanel.SetDock(e, Dock.Top); root.Children.Add(e); }
        Top(Ui.Heading("Сегодня", 17, FontWeights.Bold));
        Top(Spacer(14));
        Top(_weather);
        Top(Spacer(16));
        Top(Label("Напоминания"));
        Top(_reminders);
        Top(Spacer(14));
        Top(Label("Помнит о тебе"));
        Top(_memory);
        Top(Spacer(14));
        Top(_phone);
        DockPanel.SetDock(hint, Dock.Bottom);
        root.Children.Add(hint);
        Child = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden };

        host.Core.Reminders.Changed += () => Dispatcher.BeginInvoke(RenderReminders);
        host.Core.Memory.Changed += () => Dispatcher.BeginInvoke(RenderMemory);
        host.Core.Sync.Changed += () => Dispatcher.BeginInvoke(RenderPhone);
        _timer.Tick += (_, _) => Refresh();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) { Refresh(); _timer.Start(); }
            else _timer.Stop();
        };
    }

    private static FrameworkElement Spacer(double h) => new Border { Height = h };

    private static TextBlock Label(string text)
    {
        var t = Ui.Text(text.ToUpperInvariant(), 11.5, "Subtle", FontWeights.Bold, wrap: false);
        t.Margin = new Thickness(0, 0, 0, 8);
        return t;
    }

    public void Refresh()
    {
        RenderReminders();
        RenderMemory();
        RenderPhone();
        var city = _host.Core.Settings.Current.City ?? "";
        if (city != _weatherCity || DateTime.Now - _weatherAt > TimeSpan.FromMinutes(20)) _ = LoadWeather(city);
    }

    private async Task LoadWeather(string city)
    {
        _weatherCity = city;
        _weatherAt = DateTime.Now;
        _weather.Children.Clear();
        if (city.Trim().Length == 0)
        {
            var box = new TextBox { Tag = L10n.T("Твой город — для погоды") };
            box.KeyDown += (_, e) =>
            {
                if (e.Key != Key.Enter || box.Text.Trim().Length == 0) return;
                var c = box.Text.Trim();
                _host.Core.Settings.Update(s => s.City = c);
                _ = LoadWeather(c);
            };
            _weather.Children.Add(Card(new StackPanel { Children = { Ui.Muted("Погода появится здесь", 12), Spacer(6), box } }, false));
            return;
        }
        try
        {
            var w = await Task.Run(() => _host.Core.Tools.WeatherNowAsync(city));
            _weather.Children.Clear();
            if (w is null)
            {
                _weather.Children.Add(Card(Ui.Muted(L10n.F("Город «{0}» не нашёлся — поправь в Настройки → Характер и вид.", city), 12), false));
                return;
            }
            var temp = Ui.Heading((w.Temp > 0 ? "+" : "") + w.Temp + "°", 34);
            temp.SetResourceReference(TextBlock.ForegroundProperty, "AccentText");
            var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
            info.Children.Add(Ui.Text(Capital(w.Description), 14, "Fg", FontWeights.SemiBold));
            info.Children.Add(Ui.Muted(L10n.F("{0} · {1}…{2}° · ветер {3} м/с", w.Place, w.Min, w.Max, w.Wind) + (w.Precip is { } p ? L10n.F(" · осадки {0}%", p) : ""), 12));
            var card = Card(Ui.Row(new UIElement[] { temp, info }, 0, 1), true);
            _weather.Children.Add(card);
            Anim.Pop(card, 300, 0.92);
        }
        catch (Exception)
        {
            _weather.Children.Clear();
            _weather.Children.Add(Card(Ui.Muted("Погода недоступна — нет интернета?", 12), false));
            _weatherAt = DateTime.Now - TimeSpan.FromMinutes(15); // повторим через 5 минут
        }
    }

    private static string Capital(string s) => s.Length == 0 ? s : char.ToUpper(s[0]) + s[1..];

    private static Border Card(UIElement child, bool sticker)
    {
        var b = new Border { Child = child, CornerRadius = new CornerRadius(16), Padding = new Thickness(14), BorderThickness = new Thickness(sticker ? 2.5 : 2) };
        b.SetResourceReference(BackgroundProperty, "Bg2");
        if (sticker)
        {
            b.BorderBrush = Brushes.Black;
            b.Effect = Anim.HardShadow(3);
            b.Margin = new Thickness(0, 0, 3, 3);
        }
        else b.SetResourceReference(Border.BorderBrushProperty, "Line");
        return b;
    }

    private void RenderReminders()
    {
        _reminders.Children.Clear();
        var now = DateTimeOffset.Now;
        var list = _host.Core.Reminders.Upcoming().Where(r => r.Due - now < TimeSpan.FromHours(24)).Take(4).ToList();
        if (list.Count == 0)
        {
            _reminders.Children.Add(Ui.Muted("На ближайшие сутки — ничего. Свобода~", 12.5));
            return;
        }
        for (var i = 0; i < list.Count; i++)
        {
            var r = list[i];
            var time = new Border { CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(2), Padding = new Thickness(8, 3, 8, 3), VerticalAlignment = VerticalAlignment.Center };
            var tt = Ui.Text(r.Due.ToLocalTime().ToString("HH:mm"), 13, i == 0 ? "OnAccent" : "Fg", FontWeights.Bold, wrap: false);
            tt.FontFamily = Ui.Display;
            time.Child = tt;
            if (i == 0) { time.SetResourceReference(BackgroundProperty, "Accent"); time.BorderBrush = Brushes.Black; }
            else { time.SetResourceReference(BackgroundProperty, "Bg3"); time.SetResourceReference(Border.BorderBrushProperty, "Line"); }
            var text = Ui.Text(r.Text + (r.Repeat != "none" ? " · " + r.RepeatText : ""), 13.5, "Fg", wrap: false);
            text.Margin = new Thickness(10, 0, 0, 0);
            text.VerticalAlignment = VerticalAlignment.Center;
            var row = new Border { CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(2), Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(0, 0, 0, 8), Child = Ui.Row(new UIElement[] { time, text }, 0, 1) };
            row.SetResourceReference(BackgroundProperty, "Bg2");
            row.SetResourceReference(Border.BorderBrushProperty, "Bg4");
            _reminders.Children.Add(row);
        }
    }

    private void RenderMemory()
    {
        _memory.Children.Clear();
        var facts = _host.Core.Memory.List().TakeLast(6).ToList();
        if (facts.Count == 0)
        {
            _memory.Children.Add(Ui.Muted("Пока ничего. Расскажи о себе — запомню.", 12.5));
            return;
        }
        foreach (var f in facts)
        {
            var t = Ui.Text(f.Text.Length > 40 ? f.Text[..40] + "…" : f.Text, 12, "Muted", wrap: false);
            var chip = new Border { Child = t, CornerRadius = new CornerRadius(999), Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 0, 6, 6), ToolTip = f.Text };
            chip.SetResourceReference(BackgroundProperty, "Bg3");
            _memory.Children.Add(chip);
        }
    }

    private void RenderPhone()
    {
        var p = _host.Core.Sync.Peers.OrderByDescending(x => x.LastSync).FirstOrDefault();
        _phone.Text = p is null
            ? "Телефон не привязан — Настройки → Телефон."
            : $"📱 {p.Name}: " + (p.LastSync is { } t ? L10n.T("синхронизирован ") + RuText.Relative(t) : L10n.T("ещё не синхронизировался"));
    }
}
