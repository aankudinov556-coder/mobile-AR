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
    private readonly VerticalStackLayout _root = new() { Spacing = 14, Padding = new Thickness(16, 12, 16, 28) };
    private bool _busy;
    private string? _message;
    private bool _messageError;

    private static VeraCore Core => App.Core;

    public SyncPage()
    {
        Ui.StylePage(this, "Синхронизация");
        Content = new ScrollView { Content = _root };
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
            _message = $"✓ Связана с ПК «{pc.Name}». Память, напоминания и чат синхронизированы.";
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
            _message = r is null ? "Синхронизация уже идёт" : $"✓ Готово: получено {r.Received}, отправлено {r.Sent}.";
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

    private static Image Art(string path, double height) => new()
    {
        Source = ImageSource.FromStream(() => FileSystem.OpenAppPackageFileAsync(path).GetAwaiter().GetResult()),
        HeightRequest = height,
        Aspect = Aspect.AspectFit,
    };

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
            var info = new VerticalStackLayout
            {
                Spacing = 4,
                VerticalOptions = LayoutOptions.End,
                Children =
                {
                    new Label { Text = "СВЯЗАНА С ПК", FontSize = 12, FontFamily = Ui.FontBold, TextColor = Ui.OnAccent, CharacterSpacing = 1.2 },
                    new Label { Text = "«" + pc.Name + "»", FontSize = 22, FontFamily = Ui.Display, TextColor = Ui.OnAccent, LineBreakMode = LineBreakMode.TailTruncation },
                    new Label
                    {
                        Text = pc.LastSync is { } t ? "Синхронизировано " + RuText.Relative(t) : "Ещё не синхронизировалась",
                        FontSize = 13.5, TextColor = Ui.OnAccent,
                    },
                },
            };
            var g = new Grid { ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) }, MinimumHeightRequest = 130 };
            g.Add(info, 0);
            g.Add(Art("skins/chibi/laugh.png", 120), 1);
            _root.Children.Add(new Border
            {
                BackgroundColor = Ui.Accent,
                Stroke = Ui.Ink,
                StrokeThickness = 2.5,
                StrokeShape = Ui.Round(22),
                Shadow = Ui.HardShadow(4),
                Padding = new Thickness(18, 14, 8, 0),
                Margin = new Thickness(0, 0, 4, 4),
                Content = g,
            });
            var now = Ui.Primary(_busy ? "Синхронизирую…" : "Синхронизировать сейчас", (_, _) => SyncNow());
            now.IsEnabled = !_busy;
            _root.Children.Add(now);
        }

        if (_message is not null)
            _root.Children.Add(Ui.Note(_message, _messageError ? Ui.Danger : Ui.Ok));
        if (peers.Count > 0 && PhoneSync.LastError is { } err && _message is null)
            _root.Children.Add(Ui.Note(err, Ui.Warn));

        if (peers.Count == 0) return;

        _root.Children.Add(Ui.Card(
            Ui.H3("Что общее"),
            Ui.Toggle("Память и напоминания", true, _ => { }, "Всегда"),
            Ui.Toggle("История чата", s.Sync.Chat, v => Core.Settings.Update(x => x.Sync.Chat = v)),
            Ui.Toggle("API-ключи", s.Sync.Keys, v => Core.Settings.Update(x => x.Sync.Keys = v),
                "Только недостающие, по шифрованному каналу; галочка нужна и на ПК")));

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
                FontSize = 13.5,
                TextColor = on ? Ui.OnAccent : Ui.Text,
                BackgroundColor = on ? Ui.Accent : Colors.Transparent,
                BorderColor = on ? Ui.Ink : Ui.Stroke,
                BorderWidth = on ? 2.5 : 2,
                CornerRadius = 14,
                MinimumHeightRequest = 46,
            };
            var value = id;
            b.Clicked += (_, _) => { Core.Settings.Update(x => x.Sync.RingOn = value); Render(); };
            ring.Add(b, i % 2, i / 2);
            i++;
        }
        _root.Children.Add(Ui.Card(Ui.H3("Где звенят напоминания"), ring,
            Ui.MutedLabel("«Где я сейчас» — на ПК, если его трогали последние 5 минут, иначе на телефоне.", 12.5)));

        var name = Ui.Entry("Имя телефона");
        name.Text = Core.Sync.DeviceName;
        name.Unfocused += (_, _) => { if (!string.IsNullOrWhiteSpace(name.Text)) Core.Sync.DeviceName = name.Text; };
        _root.Children.Add(Ui.Card(Ui.Field("Как ПК видит этот телефон", Ui.Box(name))));

        var unpair = Ui.Ghost($"Отвязать ПК «{peers[0].Name}»", async (_, _) =>
        {
            if (!await DisplayAlertAsync("Отвязать ПК?", "Синхронизация остановится. Данные на обоих устройствах останутся.", "Отвязать", "Отмена")) return;
            foreach (var p in Core.Sync.Peers) Core.Sync.Unpair(p.Id);
            PhoneSync.ScheduleBackground();
            _message = null;
            Render();
        }, Ui.Danger);
        _root.Children.Add(unpair);
    }

    private void RenderPairing()
    {
        var steps = new VerticalStackLayout
        {
            Spacing = 8,
            Children =
            {
                Ui.Heading("Связать с ПК", 20),
                Ui.Label("1. На ПК открой Веру → Настройки → Телефон → «Привязать телефон».", 14.5),
                Ui.Label("2. Наведи обычную камеру телефона на QR-код — Вера откроется и привяжется сама.", 14.5),
                Ui.Label("3. Телефон и ПК должны быть в одной домашней сети Wi-Fi.", 14.5),
            },
        };
        var g = new Grid { ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) }, ColumnSpacing = 8 };
        g.Add(steps, 0);
        g.Add(Art("skins/chibi/wave.png", 110), 1);
        _root.Children.Add(new Border
        {
            BackgroundColor = Ui.Surface,
            Stroke = Ui.Ink,
            StrokeThickness = 2.5,
            StrokeShape = Ui.Round(22),
            Shadow = Ui.HardShadow(4),
            Padding = new Thickness(16),
            Margin = new Thickness(0, 0, 4, 4),
            Content = g,
        });

        var code = Ui.Editor("Или вставь сюда ссылку из QR (vera-sync://…)", 80);
        var pair = Ui.Primary(_busy ? "Связываюсь…" : "Привязать", (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(code.Text)) Pair(code.Text);
        });
        pair.IsEnabled = !_busy;
        var paste = Ui.Ghost("Вставить из буфера", async (_, _) =>
        {
            try { code.Text = await Clipboard.Default.GetTextAsync(); } catch { }
        });
        _root.Children.Add(Ui.Card(Ui.Field("Код привязки", Ui.Box(code, 80)), paste, pair));
    }
}
