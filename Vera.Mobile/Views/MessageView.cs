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

/// <summary>Пузырь сообщения: пользователь справа (градиент), Вера слева (тёмный), чипы инструментов, Markdown.</summary>
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
        _tools = new FlexLayout { Wrap = Microsoft.Maui.Layouts.FlexWrap.Wrap, IsVisible = false };
        _body = new ContentView();
        _bubble = new Border
        {
            Padding = new Thickness(13, 9, 13, 10),
            StrokeThickness = 0,
            StrokeShape = user ? Ui.Round(18, 18, 18, 5) : Ui.Round(18, 18, 5, 18),
            HorizontalOptions = user ? LayoutOptions.End : LayoutOptions.Start,
            Content = new VerticalStackLayout { Spacing = 6, Children = { _tools, _body } },
        };
        if (user) _bubble.Background = Ui.AccentGradient;
        else _bubble.BackgroundColor = Ui.Surface2;

        Content = new VerticalStackLayout
        {
            Spacing = 0,
            HorizontalOptions = user ? LayoutOptions.End : LayoutOptions.Start,
            Children = { _bubble, meta },
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
                Ui.Toast("Сообщение скопировано");
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
            stack.Children.Add(new Label { Text = "📎 изображение", FontSize = 12, TextColor = Colors.White.WithAlpha(0.8f) });
        }
        if (!string.IsNullOrWhiteSpace(text))
            stack.Children.Add(new Label { Text = text, TextColor = Colors.White, FontSize = 15.5, LineBreakMode = LineBreakMode.WordWrap });
        _body.Content = stack;
    }

    public void SetVera(string text, IReadOnlyList<ChipState> tools, bool typing, bool error = false)
    {
        _text = text;
        _tools.Children.Clear();
        foreach (var t in tools)
        {
            var label = t.Running ? "⚙️ " + Human(t.Name) + "…" : (string.IsNullOrEmpty(t.Label) ? t.Name : t.Label);
            _tools.Children.Add(new Border
            {
                BackgroundColor = t.IsError ? Ui.DangerSoft : t.Running ? Ui.Surface3 : Ui.AccentSoft,
                StrokeThickness = 0,
                StrokeShape = Ui.Round(10),
                Padding = new Thickness(9, 4),
                Margin = new Thickness(0, 0, 6, 6),
                Content = new Label
                {
                    Text = label,
                    FontSize = 12.5,
                    TextColor = t.IsError ? Ui.Danger : t.Running ? Ui.Muted : Color.FromArgb("#f6c4e0"),
                    LineBreakMode = LineBreakMode.TailTruncation,
                    MaxLines = 2,
                },
            });
        }
        _tools.IsVisible = tools.Count > 0;

        if (!string.IsNullOrWhiteSpace(text)) _body.Content = Markdown.Render(text, Ui.Text, 15.5);
        else if (typing) _body.Content = new TypingDots();
        else _body.Content = null;
        _body.IsVisible = _body.Content is not null;

        if (error)
        {
            _bubble.BackgroundColor = Ui.DangerSoft;
            _bubble.Stroke = Ui.Danger.WithAlpha(0.4f);
            _bubble.StrokeThickness = 1;
        }
    }

    public static string Human(string name) => name switch
    {
        "create_reminder" => "ставлю напоминание",
        "list_reminders" => "смотрю напоминания",
        "delete_reminder" => "удаляю напоминание",
        "update_reminder" => "меняю напоминание",
        "remember" => "запоминаю",
        "forget" => "забываю",
        "get_weather" => "смотрю погоду",
        "get_device_info" => "смотрю телефон",
        "open_url" => "открываю ссылку",
        "search_web" => "ищу",
        "open_app" => "запускаю приложение",
        "dial_number" => "открываю звонилку",
        "open_maps" => "открываю карты",
        "share_text" => "делюсь",
        "flashlight" => "щёлкаю фонариком",
        "open_settings" => "открываю настройки",
        "clipboard_read" => "читаю буфер",
        "clipboard_write" => "копирую",
        "web_search" => "ищу в интернете",
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
            _dots[i] = new BoxView { WidthRequest = 7, HeightRequest = 7, CornerRadius = 3.5, Color = Ui.Muted, Opacity = 0.35 };
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
