using Vera.Core;

namespace Vera.Mobile.Views;

/// <summary>Состояние чипа инструмента в сообщении.</summary>
public sealed class ChipState
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Label { get; set; } = "";
    public bool Running { get; set; }
    public bool IsError { get; set; }
}

/// <summary>Пузырь сообщения: пользователь справа (золотой стикер), Вера слева (белая бумага), над ответом — плашки действий, Markdown.</summary>
public sealed class MessageView : ContentView
{
    private readonly bool _user;
    private readonly Border _bubble;
    private readonly FlexLayout _tools;
    private readonly ContentView _body;
    private string _text = "";

    public MessageView(bool user, DateTimeOffset time)
    {
        _user = user;
        var meta = new Label
        {
            Text = time.ToLocalTime().ToString("HH:mm"),
            FontSize = 11,
            TextColor = Ui.Faint,
            HorizontalOptions = user ? LayoutOptions.End : LayoutOptions.Start,
            Margin = new Thickness(8, 2, 8, 0),
        };
        // плашки действий («Запущено: …») — над пузырём, на тёмном фоне
        _tools = new FlexLayout { Wrap = Microsoft.Maui.Layouts.FlexWrap.Wrap, IsVisible = false, Margin = new Thickness(0, 0, 0, 2) };
        _body = new ContentView();
        // комикс-пузыри: Вера — белая бумага с чёрной обводкой, пользователь — золотой стикер
        _bubble = new Border
        {
            Padding = new Thickness(14, 10, 14, 11),
            Stroke = Ui.Ink,
            StrokeThickness = 2.5,
            Shadow = Ui.HardShadow(3),
            Margin = new Thickness(0, 0, 3, 3),
            StrokeShape = user ? Ui.Round(18, 18, 18, 6) : Ui.Round(18, 18, 6, 18),
            HorizontalOptions = user ? LayoutOptions.End : LayoutOptions.Start,
            Content = _body,
        };
        _bubble.BackgroundColor = user ? Ui.Accent : Ui.Paper;

        Content = new VerticalStackLayout
        {
            Spacing = 0,
            HorizontalOptions = user ? LayoutOptions.End : LayoutOptions.Start,
            Children = { _tools, _bubble, meta },
        };
        Padding = user ? new Thickness(52, 0, 0, 0) : new Thickness(0, 0, 40, 0);

        // двойной тап — скопировать текст сообщения
        var dbl = new TapGestureRecognizer { NumberOfTapsRequired = 2 };
        dbl.Tapped += async (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(_text)) return;
            try
            {
                await Clipboard.Default.SetTextAsync(_text);
                Ui.Toast(L10n.T("Сообщение скопировано"));
            }
            catch { }
        };
        _bubble.GestureRecognizers.Add(dbl);
    }

    public void SetUser(string text, ImageSource? image, bool hadImage)
    {
        _text = text;
        var stack = new VerticalStackLayout { Spacing = 6 };
        if (image is not null)
        {
            stack.Children.Add(new Border
            {
                StrokeThickness = 0,
                StrokeShape = Ui.Round(12),
                Content = new Image { Source = image, Aspect = Aspect.AspectFill, WidthRequest = 200, HeightRequest = 200 },
            });
        }
        else if (hadImage)
        {
            stack.Children.Add(new Label { Text = L10n.T("📎 изображение"), FontSize = 12, TextColor = Ui.OnAccent.WithAlpha(0.8f) });
        }
        if (!string.IsNullOrWhiteSpace(text))
            stack.Children.Add(new Label { Text = text, TextColor = Ui.OnAccent, FontSize = 15, FontFamily = Ui.Font, LineBreakMode = LineBreakMode.WordWrap });
        _body.Content = stack;
    }

    public void SetVera(string text, IReadOnlyList<ChipState> tools, bool typing, bool error = false)
    {
        _text = text;
        _tools.Children.Clear();
        foreach (var t in tools)
        {
            var label = t.Running ? Human(t.Name) + "…" : (string.IsNullOrEmpty(t.Label) ? t.Name : t.Label);
            // готово — золотая пилюля с чёрной обводкой; в работе — тёмная; ошибка — красная
            _tools.Children.Add(new Border
            {
                BackgroundColor = t.IsError ? Ui.DangerSoft : t.Running ? Ui.Surface2 : Ui.Accent,
                Stroke = t.IsError ? Ui.Danger.WithAlpha(0.5f) : t.Running ? Ui.Stroke : Ui.Ink,
                StrokeThickness = 2,
                StrokeShape = Ui.Round(13),
                Padding = new Thickness(10, 4),
                Margin = new Thickness(0, 0, 6, 6),
                Content = new Label
                {
                    Text = label,
                    FontSize = 12,
                    TextColor = t.IsError ? Ui.Danger : t.Running ? Ui.Soft : Ui.InkText,
                    FontFamily = Ui.FontBold,
                    LineBreakMode = LineBreakMode.TailTruncation,
                    MaxLines = 2,
                },
            });
        }
        _tools.IsVisible = tools.Count > 0;

        if (!string.IsNullOrWhiteSpace(text)) _body.Content = Markdown.Render(text, Ui.InkText, 15);
        else if (typing) _body.Content = new TypingDots();
        else _body.Content = null;
        _body.IsVisible = _body.Content is not null;
        // ответ только из действий — без пустого пузыря
        _bubble.IsVisible = _body.IsVisible;

        if (error)
        {
            _bubble.BackgroundColor = Color.FromArgb("#ffe9e6");
            _bubble.Stroke = Ui.Ink;
        }
    }

    public static string Human(string name) => name switch
    {
        "create_reminder" => L10n.T("ставлю напоминание"),
        "list_reminders" => L10n.T("смотрю напоминания"),
        "delete_reminder" => L10n.T("удаляю напоминание"),
        "update_reminder" => L10n.T("меняю напоминание"),
        "remember" => L10n.T("запоминаю"),
        "forget" => L10n.T("забываю"),
        "get_weather" => L10n.T("смотрю погоду"),
        "get_device_info" => L10n.T("смотрю телефон"),
        "open_url" => L10n.T("открываю ссылку"),
        "search_web" => L10n.T("ищу"),
        "open_app" => L10n.T("запускаю приложение"),
        "dial_number" => L10n.T("открываю звонилку"),
        "open_maps" => L10n.T("открываю карты"),
        "share_text" => L10n.T("делюсь"),
        "flashlight" => L10n.T("щёлкаю фонариком"),
        "open_settings" => L10n.T("открываю настройки"),
        "clipboard_read" => L10n.T("читаю буфер"),
        "clipboard_write" => L10n.T("копирую"),
        "web_search" => L10n.T("ищу в интернете"),
        _ => name,
    };
}

/// <summary>Индикатор «печатает…» — три точки.</summary>
public sealed class TypingDots : HorizontalStackLayout
{
    private readonly BoxView[] _dots = new BoxView[3];
    private IDispatcherTimer? _timer;
    private int _step;

    public TypingDots()
    {
        Spacing = 5;
        Padding = new Thickness(2, 6);
        for (var i = 0; i < 3; i++)
        {
            _dots[i] = new BoxView { WidthRequest = 7, HeightRequest = 7, CornerRadius = 3.5, Color = Ui.InkText, Opacity = 0.35 };
            Children.Add(_dots[i]);
        }
        Loaded += (_, _) =>
        {
            _timer = Dispatcher.CreateTimer();
            _timer.Interval = TimeSpan.FromMilliseconds(260);
            _timer.Tick += (_, _) =>
            {
                _step = (_step + 1) % 3;
                for (var i = 0; i < 3; i++)
                {
                    _dots[i].Opacity = i == _step ? 1 : 0.35;
                    _dots[i].TranslationY = i == _step ? -2 : 0;
                }
            };
            _timer.Start();
        };
        Unloaded += (_, _) => { _timer?.Stop(); _timer = null; };
    }
}
