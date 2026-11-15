using Vera.Core;
using Vera.Core.Sync;
using Vera.Mobile.Services;

namespace Vera.Mobile.Views;

/// <summary>
/// Синхронизация с ПК: привязка по QR (камера телефона открывает Веру ссылкой vera-sync://) или вставкой кода,
/// «Синхронизировать сейчас», что общее, где звенят напоминания, отвязка.
/// </summary>
public sealed class SyncPage : ContentPage
{
    private readonly VerticalStackLayout _root = new() { Spacing = 16, Padding = new Thickness(16, 6, 16, 24) };
    private bool _busy;
    /// <summary>Сколько изменений пришло при последней синхронизации (для золотой карточки).</summary>
    private static int? _lastReceived;
    private static bool _syncHooked;
    private string? _message;
    private bool _messageError;

    private static VeraCore Core => App.Core;

    public SyncPage()
    {
        Ui.StylePage(this, "Синхронизация");
        NavigationPage.SetHasNavigationBar(this, false);
        if (!_syncHooked)
        {
            _syncHooked = true;
            Core.Sync.Synced += r => _lastReceived = r.Received;
        }
        var root = new Grid
        {
            RowDefinitions = { new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Star) },
            BackgroundColor = Ui.Bg,
        };
        root.Add(Ui.PageHeader(this, "Синхронизация"), 0, 0);
        root.Add(new ScrollView { Content = _root }, 0, 1);
        Content = root;
        Appearing += (_, _) => { PhoneSync.StateChanged += OnState; Render(); };
        Disappearing += (_, _) => PhoneSync.StateChanged -= OnState;
    }

    private void OnState() => MainThread.BeginInvokeOnMainThread(Render);

    /// <summary>Привязать ПК по содержимому QR-кода.</summary>
    public async void Pair(string qr)
    {
        if (_busy) return;
        _busy = true;
        _message = "Связываюсь с ПК…";
        _messageError = false;
        Render();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            var pc = await Task.Run(() => Core.Sync.PairAsync(qr.Trim(), cts.Token));
            _message = $"Связана с ПК «{pc.Name}». Память, напоминания и чат синхронизированы.";
            PhoneSync.ScheduleBackground();
            try { HapticFeedback.Default.Perform(HapticFeedbackType.Click); } catch { }
        }
        catch (Exception e)
        {
            _message = e is SyncException ? e.Message : "Не получилось: " + e.Message;
            _messageError = true;
        }
        finally
        {
            _busy = false;
            Render();
        }
    }

    private async void SyncNow()
    {
        if (_busy) return;
        _busy = true;
        _message = "Синхронизирую…";
        _messageError = false;
        Render();
        try
        {
            var r = await PhoneSync.SyncNowAsync(quiet: false);
            if (r is not null) _lastReceived = r.Received;
            _message = r is null ? "Синхронизация уже идёт" : $"Готово: получено {r.Received}, отправлено {r.Sent}.";
        }
        catch (Exception e)
        {
            _message = e.Message;
            _messageError = true;
        }
        finally
        {
            _busy = false;
            Render();
        }
    }

    private static Image Art(string path, double height) => Ui.Art(path, height);

    /// <summary>Кнопка-стикер: тёмная, с чёрной обводкой и жёсткой тенью (не золотая — золото для главного).</summary>
    private static Button DarkSticker(string text, EventHandler onClick)
    {
        var b = new Button
        {
            Text = text,
            TextColor = Ui.Text,
            BackgroundColor = Ui.Surface,
            BorderColor = Ui.Ink,
            BorderWidth = 2.5,
            CornerRadius = 16,
            FontFamily = Ui.FontBold,
            FontSize = 15,
            Padding = new Thickness(16, 12),
            MinimumHeightRequest = 52,
            Shadow = Ui.HardShadow(3),
            Margin = new Thickness(0, 0, 3, 3),
        };
        b.Clicked += onClick;
        return b;
    }

    /// <summary>Строка «Что общее»: текст слева, галочка справа, снизу — разделитель.</summary>
    private static View ShareRow(string text, string? hint, bool value, Action<bool>? changed, bool last)
    {
        var box = new CheckBox { IsChecked = value, Color = Ui.Accent, VerticalOptions = LayoutOptions.Center };
        // без обработчика — всегда включено (галочку не снять)
        box.CheckedChanged += (_, e) =>
        {
            if (changed is null) { if (!e.Value) box.IsChecked = true; }
            else changed(e.Value);
        };
        var texts = new VerticalStackLayout { Spacing = 2, VerticalOptions = LayoutOptions.Center };
        texts.Children.Add(new Label { Text = text, FontSize = 15, TextColor = Ui.Text });
        if (!string.IsNullOrEmpty(hint)) texts.Children.Add(new Label { Text = hint, FontSize = 12, TextColor = Ui.Muted });
        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) => { if (changed is not null) box.IsChecked = !box.IsChecked; };
        texts.GestureRecognizers.Add(tap);
        var g = new Grid
        {
            ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) },
            ColumnSpacing = 12,
            Padding = new Thickness(16, 8, 8, 8),
            MinimumHeightRequest = 48,
        };
        g.Add(texts, 0);
        g.Add(box, 1);
        if (last) return g;
        return new VerticalStackLayout
        {
            Spacing = 0,
            Children = { g, new BoxView { HeightRequest = 2, Color = Ui.Surface2 } },
        };
    }

    private void Render()
    {
        _root.Children.Clear();
        var peers = Core.Sync.Peers;
        var s = Core.Settings.Current;

        if (peers.Count == 0) RenderPairing();
        else
        {
            var pc = peers[0];
            // золотая карточка «Связана с ПК»
            var synced = pc.LastSync is { } t
                ? "Синхронизировано " + (DateTimeOffset.Now - t < TimeSpan.FromMinutes(1) ? "только что" : RuText.Relative(t))
                : "Ещё не синхронизировалась";
            if (pc.LastSync is not null && _lastReceived is { } n)
                synced += $" · получено {n} {RuText.Plural(n, "изменение", "изменения", "изменений")}";
            var info = new VerticalStackLayout
            {
                Spacing = 6,
                VerticalOptions = LayoutOptions.End,
                Padding = new Thickness(0, 0, 0, 18),
                Children =
                {
                    Ui.Caps("Связана с ПК", Ui.OnAccent),
                    new Label { Text = "«" + pc.Name + "»", FontSize = 22, FontFamily = Ui.Display, TextColor = Ui.OnAccent, LineBreakMode = LineBreakMode.TailTruncation },
                    new Label { Text = synced, FontSize = 13, TextColor = Ui.OnAccent, LineBreakMode = LineBreakMode.WordWrap },
                },
            };
            var g = new Grid
            {
                ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) },
                ColumnSpacing = 12,
                MinimumHeightRequest = 150,
            };
            g.Add(info, 0);
            var art = Art("skins/chibi/laugh.png", 124);
            art.VerticalOptions = LayoutOptions.End;
            g.Add(art, 1);
            _root.Children.Add(new Border
            {
                BackgroundColor = Ui.Accent,
                Stroke = Ui.Ink,
                StrokeThickness = 2.5,
                StrokeShape = Ui.Round(22),
                Shadow = Ui.HardShadow(4),
                Padding = new Thickness(18, 18, 6, 0),
                Margin = new Thickness(0, 0, 4, 4),
                Content = g,
            });
            var now = DarkSticker(_busy ? "Синхронизирую…" : "Синхронизировать сейчас", (_, _) => SyncNow());
            now.IsEnabled = !_busy;
            _root.Children.Add(now);
        }

        if (_message is not null)
            _root.Children.Add(Ui.Note(_message, _messageError ? Ui.Danger : Ui.Ok));
        if (peers.Count > 0 && PhoneSync.LastError is { } err && _message is null)
            _root.Children.Add(Ui.Note(err, Ui.Warn));

        if (peers.Count == 0) return;

        // «Что общее» — карточка со строками-разделителями
        var shared = new VerticalStackLayout
        {
            Spacing = 0,
            Children =
            {
                new ContentView { Padding = new Thickness(16, 12, 16, 4), Content = Ui.Caps("Что общее") },
                ShareRow("Память и напоминания", null, true, null, false),
                ShareRow("История чата", null, s.Sync.Chat, v => Core.Settings.Update(x => x.Sync.Chat = v), false),
                ShareRow("API-ключи", "Только недостающие, по шифрованному каналу; галочка нужна и на ПК", s.Sync.Keys,
                    v => Core.Settings.Update(x => x.Sync.Keys = v), true),
            },
        };
        _root.Children.Add(new Border
        {
            BackgroundColor = Ui.Surface,
            Stroke = Ui.Surface3,
            StrokeThickness = 2,
            StrokeShape = Ui.Round(18),
            Padding = new Thickness(0, 0, 0, 4),
            Content = shared,
        });

        // «Где звенят напоминания» — сетка 2×2
        var ring = new Grid
        {
            ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Star) },
            RowDefinitions = { new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Auto) },
            ColumnSpacing = 8,
            RowSpacing = 8,
        };
        var i = 0;
        foreach (var (id, text) in new[] { ("both", "На обоих"), ("active", "Где я сейчас"), ("pc", "Только ПК"), ("phone", "Только телефон") })
        {
            var on = s.Sync.RingOn == id;
            var b = new Button
            {
                Text = text,
                FontFamily = Ui.FontBold,
                FontSize = 13,
                TextColor = on ? Ui.OnAccent : Ui.Soft,
                BackgroundColor = on ? Ui.Accent : Colors.Transparent,
                BorderColor = on ? Ui.Ink : Ui.Stroke,
                BorderWidth = on ? 2.5 : 2,
                CornerRadius = 14,
                Padding = new Thickness(6, 10),
                MinimumHeightRequest = 44,
            };
            var value = id;
            b.Clicked += (_, _) => { Core.Settings.Update(x => x.Sync.RingOn = value); Render(); };
            ring.Add(b, i % 2, i / 2);
            i++;
        }
        _root.Children.Add(new VerticalStackLayout
        {
            Spacing = 8,
            Children =
            {
                Ui.Caps("Где звенят напоминания"),
                ring,
                new Label { Text = "«Где я сейчас» — на ПК, если его трогали последние 5 минут, иначе на телефоне.", FontSize = 12, TextColor = Ui.Faint },
            },
        });

        var name = Ui.Entry("Имя телефона");
        name.Text = Core.Sync.DeviceName;
        name.Unfocused += (_, _) => { if (!string.IsNullOrWhiteSpace(name.Text)) Core.Sync.DeviceName = name.Text; };
        _root.Children.Add(new VerticalStackLayout { Spacing = 8, Children = { Ui.Caps("Как ПК видит этот телефон"), Ui.Box(name) } });

        // «Отвязать» — красная обводка внизу
        var unpair = new Button
        {
            Text = $"Отвязать ПК «{peers[0].Name}»",
            FontFamily = Ui.FontBold,
            FontSize = 14,
            TextColor = Ui.Danger,
            BackgroundColor = Colors.Transparent,
            BorderColor = Color.FromArgb("#4a2a2e"),
            BorderWidth = 2,
            CornerRadius = 14,
            Padding = new Thickness(12),
            MinimumHeightRequest = 46,
            Margin = new Thickness(0, 10, 0, 0),
        };
        unpair.Clicked += async (_, _) =>
        {
            if (!await DisplayAlertAsync("Отвязать ПК?", "Синхронизация остановится. Данные на обоих устройствах останутся.", "Отвязать", "Отмена")) return;
            foreach (var p in Core.Sync.Peers) Core.Sync.Unpair(p.Id);
            PhoneSync.ScheduleBackground();
            _message = null;
            _lastReceived = null;
            Render();
        };
        _root.Children.Add(unpair);
    }

    private void RenderPairing()
    {
        var steps = new VerticalStackLayout
        {
            Spacing = 8,
            Children =
            {
                Ui.Caps("Не связана"),
                Ui.Heading("Связать с ПК", 20),
                Ui.Label("1. На ПК открой Веру → Настройки → Телефон → «Привязать телефон».", 14),
                Ui.Label("2. Наведи обычную камеру телефона на QR-код — Вера откроется и привяжется сама.", 14),
                Ui.Label("3. Телефон и ПК должны быть в одной домашней сети Wi-Fi.", 14),
            },
        };
        var g = new Grid { ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) }, ColumnSpacing = 8 };
        g.Add(steps, 0);
        var art = Art("skins/chibi/wave.png", 110);
        art.VerticalOptions = LayoutOptions.End;
        g.Add(art, 1);
        _root.Children.Add(new Border
        {
            BackgroundColor = Ui.Surface,
            Stroke = Ui.Ink,
            StrokeThickness = 2.5,
            StrokeShape = Ui.Round(22),
            Shadow = Ui.HardShadow(4),
            Padding = new Thickness(18),
            Margin = new Thickness(0, 0, 4, 4),
            Content = g,
        });

        var code = Ui.Editor("Или вставь сюда ссылку из QR (vera-sync://…)", 80);
        var pair = Ui.Primary(_busy ? "Связываюсь…" : "Привязать", (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(code.Text)) Pair(code.Text);
        });
        pair.IsEnabled = !_busy;
        pair.CornerRadius = 16;
        pair.MinimumHeightRequest = 50;
        pair.Margin = new Thickness(0, 0, 3, 3);
        var paste = Ui.Ghost("Вставить из буфера", async (_, _) =>
        {
            try { code.Text = await Clipboard.Default.GetTextAsync(); } catch { }
        });
        _root.Children.Add(new Border
        {
            BackgroundColor = Ui.Surface,
            Stroke = Ui.Surface3,
            StrokeThickness = 2,
            StrokeShape = Ui.Round(18),
            Padding = new Thickness(16, 14),
            Content = new VerticalStackLayout { Spacing = 10, Children = { Ui.Caps("Код привязки"), Ui.Box(code, 80), paste, pair } },
        });
    }
}
