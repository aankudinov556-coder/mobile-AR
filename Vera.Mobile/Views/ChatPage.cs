using System.Text;
using Vera.Core;
using Vera.Core.Agent;
using Vera.Core.Documents;
using Vera.Core.Providers;
using Vera.Core.Speech;
using Vera.Core.Sprites;
using Vera.Core.Tools;
using Vera.Mobile.Platform;
using Vera.Mobile.Services;

namespace Vera.Mobile.Views;

/// <summary>
/// Главный экран: чат-мессенджер. Вера сидит на верхнем краю панели ввода, ноги свисают поверх панели;
/// над ней — зарезервированная «сцена» с облачком, сообщения идут выше и Верой не перекрываются.
/// В начале переписки за день — карточка «утро · сводка» (погода и напоминания на сегодня).
/// </summary>
public sealed class ChatPage : ContentPage
{
    private sealed class LiveTurn
    {
        public required MessageView View { get; init; }
        public StringBuilder Text { get; } = new();
        public List<ChipState> Tools { get; } = new();
        public int? LastStep { get; set; }
        /// <summary>Где в тексте начинается каждый шаг модели (для TextReplaced).</summary>
        public Dictionary<int, int> StepStart { get; } = new();
        public SentenceChunker? Chunker { get; set; }
        public bool Speak { get; set; }
        public string Source { get; init; } = "chat";
    }

    private const int HistoryShown = 80;
    private static bool _greeted;

    private VeraCore Core => App.Core;
    private readonly SpeechOutput _speech;
    private readonly VoiceController _voice;
    private readonly double _unit;

    // UI
    private readonly Label _status;
    private readonly BoxView _statusDot;
    private readonly ScrollView _scroll;
    private readonly VerticalStackLayout _list;
    private readonly View _empty;
    private readonly VeraView _vera;
    private readonly Border _bubble;
    private readonly Label _bubbleHead;
    private readonly Label _bubbleText;
    private readonly HorizontalStackLayout _bubbleActions;
    private readonly HorizontalStackLayout _levelBars;
    private readonly AbsoluteLayout _fx;
    private readonly Editor _input;
    /// <summary>Золотая кнопка справа: микрофон / отправить / стоп.</summary>
    private readonly ImageButton _sendBtn;
    private readonly Border _attachBox;
    private readonly Image _attachImage;
    private readonly Border _remBadge;
    private readonly Label _remBadgeText;

    // сводка дня
    private readonly VerticalStackLayout _summary;
    private Label _sumCaption = null!;
    private Grid _sumWeather = null!;
    private Label _sumTemp = null!;
    private Label _sumDesc = null!;
    private FlexLayout _sumPills = null!;
    private Label _sumNoPills = null!;
    private static WeatherNow? _weather;
    private static string? _weatherCity;
    private static long _weatherAt;
    private static bool _weatherLoading;

    // состояние
    private readonly Dictionary<string, LiveTurn> _live = new();
    private readonly HashSet<string> _dirty = new();
    private bool _renderScheduled;
    private bool _busy;
    private bool _stickToBottom = true;
    private readonly List<ImageData> _attachments = new();
    private readonly List<DocumentText> _docs = new();
    private Border _docBox = null!;
    private Label _docLabel = null!;
    private List<string>? _pendingDocs;
    private byte[]? _attachPreview;
    private byte[]? _pendingPreview;
    private bool _bubbleSticky;
    private string _bubbleKind = "";
    private int _bubbleGen;
    private long _lastInteraction = System.Environment.TickCount64;
    private long _nextIdleChat = System.Environment.TickCount64 + 100_000 + Random.Shared.Next(60_000);
    private int _pokes;
    private volatile float _micLevel;
    private IDispatcherTimer? _lifeTimer;
    private IDispatcherTimer? _barsTimer;

    public ChatPage()
    {
        _speech = App.Speech;
        _voice = App.Voice;
        Ui.StylePage(this, "Вера");
        NavigationPage.SetHasNavigationBar(this, false);

        _unit = VeraView.PickUnit(DeviceDisplay.MainDisplayInfo.Density);
        var stageHeight = VeraSprite.SeatLine * _unit;
        const double veraLeft = 4;

        // ------------------------------------------------ шапка
        // «Вера» шрифтом Unbounded, под ней — зелёная точка и состояние связи с ПК
        _status = new Label { FontSize = 12, TextColor = Ui.Muted, LineBreakMode = LineBreakMode.TailTruncation, MaxLines = 1, VerticalOptions = LayoutOptions.Center };
        _statusDot = new BoxView
        {
            WidthRequest = 8,
            HeightRequest = 8,
            CornerRadius = 4,
            Color = Ui.Ok,
            Margin = new Thickness(0, 1, 6, 0),
            VerticalOptions = LayoutOptions.Center,
            IsVisible = false,
        };
        var statusRow = new Grid { ColumnDefinitions = { new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star) } };
        statusRow.Add(_statusDot, 0);
        statusRow.Add(_status, 1);
        var title = new VerticalStackLayout
        {
            Spacing = 0,
            VerticalOptions = LayoutOptions.Center,
            Children = { Ui.Heading("Вера", 22), statusRow },
        };
        _remBadgeText = new Label
        {
            FontSize = 11,
            TextColor = Ui.OnAccent,
            FontFamily = Ui.FontBold,
            HorizontalTextAlignment = TextAlignment.Center,
            VerticalTextAlignment = TextAlignment.Center,
        };
        _remBadge = new Border
        {
            BackgroundColor = Ui.Accent,
            Stroke = Ui.Ink,
            StrokeThickness = 1.5,
            StrokeShape = Ui.Round(9),
            Padding = new Thickness(4, 0),
            MinimumWidthRequest = 18,
            HeightRequest = 18,
            HorizontalOptions = LayoutOptions.End,
            VerticalOptions = LayoutOptions.Start,
            InputTransparent = true,
            IsVisible = false,
            Content = _remBadgeText,
        };
        // квадратные кнопки 44×44; отступ сверху-справа — место под бейдж
        static ImageButton HeadBtn(ImageButton b)
        {
            b.Margin = new Thickness(0, 5, 5, 0);
            return b;
        }
        var remBtn = new Grid
        {
            Children =
            {
                HeadBtn(Ui.SquareIcon("ic_bell.png", async (_, _) => await OpenPage(new RemindersPage()), "Напоминания")),
                _remBadge,
            },
        };
        var icons = new HorizontalStackLayout
        {
            Spacing = 3,
            VerticalOptions = LayoutOptions.Center,
            Children =
            {
                HeadBtn(Ui.SquareIcon("ic_more.png", async (_, _) => await MoreMenuAsync(), "Ещё")),
                remBtn,
                HeadBtn(Ui.SquareIcon("ic_gear.png", async (_, _) => await OpenPage(new SettingsPage()), "Настройки")),
            },
        };
        var header = new Grid
        {
            Padding = new Thickness(16, 10, 11, 10),
            ColumnSpacing = 10,
            ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) },
        };
        header.Add(title, 0);
        header.Add(icons, 1);
        var headerLine = new BoxView { Color = Ui.Surface2, HeightRequest = 2, VerticalOptions = LayoutOptions.End };

        // ------------------------------------------------ сообщения
        _list = new VerticalStackLayout { Spacing = 12, Padding = new Thickness(14, 16, 14, 8) };
        _empty = BuildEmpty();
        _summary = BuildSummary();
        _scroll = new ScrollView { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Never };
        _scroll.Scrolled += (_, e) =>
        {
            _stickToBottom = e.ScrollY >= _scroll.ContentSize.Height - _scroll.Height - 80;
            Touch();
        };

        // ------------------------------------------------ Вера + облачко + эффекты
        _vera = new VeraView(_unit) { Margin = new Thickness(veraLeft, 0, 0, 0) };
        _vera.Tapped += Poke;
        _vera.LongPressed += () => { Touch(); _voice.Toggle(); };

        _bubbleHead = new Label { FontSize = 12, TextColor = Ui.LinkOnPaper, FontFamily = Ui.FontBold, IsVisible = false };
        _bubbleText = new Label { FontSize = 14.5, TextColor = Ui.InkText, LineBreakMode = LineBreakMode.WordWrap, MaxLines = 7 };
        _bubbleActions = new HorizontalStackLayout { Spacing = 6, IsVisible = false, Margin = new Thickness(0, 4, 0, 0) };
        _levelBars = new HorizontalStackLayout { Spacing = 3, IsVisible = false, HeightRequest = 18, Margin = new Thickness(0, 2, 0, 0) };
        for (var i = 0; i < 7; i++)
            _levelBars.Children.Add(new BoxView { WidthRequest = 4, HeightRequest = 3, CornerRadius = 2, Color = Ui.Accent, VerticalOptions = LayoutOptions.Center });
        _bubble = new Border
        {
            BackgroundColor = Ui.Paper,
            Stroke = Ui.Ink,
            StrokeThickness = 2.5,
            StrokeShape = Ui.Round(18, 18, 6, 18),
            Padding = new Thickness(13, 9, 13, 10),
            HorizontalOptions = LayoutOptions.Start,
            VerticalOptions = LayoutOptions.End,
            Margin = new Thickness(veraLeft + 52 * _unit, 0, 12, (VeraSprite.SeatLine - 36) * _unit),
            IsVisible = false,
            Opacity = 0,
            Shadow = Ui.HardShadow(3),
            Content = new VerticalStackLayout { Spacing = 2, Children = { _bubbleHead, _bubbleText, _levelBars, _bubbleActions } },
        };
        var bubbleTap = new TapGestureRecognizer();
        bubbleTap.Tapped += (_, _) => { if (!_bubbleSticky) HideBubble(); };
        _bubble.GestureRecognizers.Add(bubbleTap);

        _fx = new AbsoluteLayout { InputTransparent = true, CascadeInputTransparent = true };

        _attachImage = new Image { Aspect = Aspect.AspectFill, WidthRequest = 64, HeightRequest = 64 };
        var attachClose = Ui.Icon("ic_close.png", (_, _) => ClearAttachments(), 28, 5);
        attachClose.BackgroundColor = Color.FromArgb("#cc15131c");
        attachClose.HorizontalOptions = LayoutOptions.End;
        attachClose.VerticalOptions = LayoutOptions.Start;
        _attachBox = new Border
        {
            StrokeThickness = 2,
            Stroke = Ui.Accent,
            StrokeShape = Ui.Round(14),
            HorizontalOptions = LayoutOptions.End,
            VerticalOptions = LayoutOptions.End,
            Margin = new Thickness(0, 0, 14, 10),
            IsVisible = false,
            Content = new Grid { Children = { _attachImage, attachClose } },
        };

        // ------------------------------------------------ панель ввода
        _input = new Editor
        {
            Placeholder = "Напиши Вере…",
            PlaceholderColor = Ui.Faint,
            TextColor = Ui.Text,
            FontSize = 15,
            AutoSize = EditorAutoSizeOption.TextChanges,
            MaximumHeightRequest = 140,
            MinimumHeightRequest = 44,
            BackgroundColor = Colors.Transparent,
            VerticalOptions = LayoutOptions.Center,
            Margin = new Thickness(0, 0),
        };
        _input.TextChanged += (_, _) => { Touch(); UpdateSendIcon(); };
        _input.Focused += (_, _) => { Touch(); ScrollToEnd(true); };

        // слева «+» (фото, камера, документ), справа золотая кнопка: микрофон → отправить → стоп
        var plusBtn = new ImageButton
        {
            Source = "ic_plus.png",
            WidthRequest = 44,
            HeightRequest = 44,
            CornerRadius = 14,
            Padding = new Thickness(12),
            BackgroundColor = Ui.Surface2,
            BorderColor = Ui.Stroke,
            BorderWidth = 2,
            VerticalOptions = LayoutOptions.End,
        };
        SemanticProperties.SetDescription(plusBtn, "Приложить файл");
        plusBtn.Clicked += async (_, _) => await AttachMenuAsync();
        _sendBtn = new ImageButton
        {
            Source = "ic_mic_dark.png",
            WidthRequest = 52,
            HeightRequest = 44,
            CornerRadius = 14,
            Padding = new Thickness(16, 12),
            BackgroundColor = Ui.Accent,
            BorderColor = Ui.Ink,
            BorderWidth = 2.5,
            VerticalOptions = LayoutOptions.End,
        };
        SemanticProperties.SetDescription(_sendBtn, "Сказать голосом");
        _sendBtn.Clicked += (_, _) => OnActionButton();

        var composerGrid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto),
            },
            ColumnSpacing = 8,
            Padding = new Thickness(8),
        };
        composerGrid.Add(plusBtn, 0);
        composerGrid.Add(_input, 1);
        composerGrid.Add(_sendBtn, 2);
        // панель ввода — тёмный стикер с чёрной обводкой и жёсткой тенью
        var composer = new Border
        {
            BackgroundColor = Ui.Surface,
            Stroke = Ui.Ink,
            StrokeThickness = 2.5,
            StrokeShape = Ui.Round(22),
            Margin = new Thickness(12, 0, 16, 18),
            Shadow = Ui.HardShadow(4),
            Content = composerGrid,
        };

        // приложенные документы
        _docLabel = new Label { FontSize = 13, TextColor = Ui.InkText, FontFamily = Ui.FontBold, LineBreakMode = LineBreakMode.TailTruncation, MaxLines = 2 };
        var docClose = Ui.Icon("ic_close.png", (_, _) => { _docs.Clear(); UpdateDocs(); }, 28, 6);
        docClose.BackgroundColor = Ui.OnAccent;
        var docGrid = new Grid { ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) }, ColumnSpacing = 6 };
        docGrid.Add(_docLabel, 0);
        docGrid.Add(docClose, 1);
        _docBox = new Border
        {
            BackgroundColor = Ui.Accent,
            Stroke = Ui.Ink,
            StrokeThickness = 2,
            StrokeShape = Ui.Round(14),
            Padding = new Thickness(12, 6, 6, 6),
            HorizontalOptions = LayoutOptions.End,
            VerticalOptions = LayoutOptions.End,
            MaximumWidthRequest = 260,
            Margin = new Thickness(0, 0, 14, 10),
            IsVisible = false,
            Content = docGrid,
        };

        // ------------------------------------------------ раскладка
        var root = new Grid
        {
            RowDefinitions =
            {
                new RowDefinition(GridLength.Auto),        // шапка
                new RowDefinition(GridLength.Star),        // сообщения
                new RowDefinition(new GridLength(stageHeight)), // сцена Веры (тело над панелью)
                new RowDefinition(GridLength.Auto),        // панель ввода
            },
            BackgroundColor = Ui.Bg,
        };
        root.Add(header, 0, 0);
        root.Add(headerLine, 0, 0);
        root.Add(_scroll, 0, 1);
        root.Add(_attachBox, 0, 2);
        root.Add(_docBox, 0, 2);
        root.Add(composer, 0, 3);
        // Вера поверх панели: строка SeatLine спрайта совпадает с верхним краем панели
        root.Add(_vera, 0, 2);
        Grid.SetRowSpan(_vera, 2);
        root.Add(_fx, 0, 2);
        root.Add(_bubble, 0, 1);
        Grid.SetRowSpan(_bubble, 2);
        Content = root;

        // ------------------------------------------------ события
        Core.Agent.Event += e => MainThread.BeginInvokeOnMainThread(() => OnAgentEvent(e));
        Core.Reminders.Changed += () => MainThread.BeginInvokeOnMainThread(() => { UpdateBadge(); RefreshSummary(); });
        Core.Settings.Changed += _ => MainThread.BeginInvokeOnMainThread(() =>
        {
            _vera.Skin = Core.Settings.Current.Skin ?? "vera";
            UpdateStatus();
            LoadWeatherIfNeeded();
            RefreshSummary();
        });
        Core.Agent.HistoryChanged += () => MainThread.BeginInvokeOnMainThread(() => { if (!_busy) LoadHistory(); });
        Core.Sync.Synced += _ => MainThread.BeginInvokeOnMainThread(UpdateStatus);
        _vera.Skin = Core.Settings.Current.Skin ?? "vera";
        Core.Secrets.Changed += () => MainThread.BeginInvokeOnMainThread(UpdateStatus);
        ReminderAlerts.ForegroundDue += OnReminderDue;
        App.ReminderOpened += ShowReminderFromNotification;

        _speech.Level += l => _vera.SetLevel(l);
        _speech.SpeakingChanged += s => MainThread.BeginInvokeOnMainThread(() =>
        {
            _vera.Speaking = s;
            if (s) { _vera.Mode = PetMode.Talking; Touch(); }
            else if (_vera.Mode == PetMode.Talking && !_busy) _vera.Mode = PetMode.Idle;
            UpdateSendIcon();
        });
        _speech.Error += msg => MainThread.BeginInvokeOnMainThread(() => ShowBubble(msg, "🔊 голос", ms: 8000));

        _voice.StateChanged += OnVoiceState;
        _voice.Level += l => _micLevel = l;
        _voice.Heard += text => Send(text, "voice");
        _voice.Notice += (text, head) => { _vera.SetEmotion("sad", 5000); ShowBubble(text, head, ms: 8000); };

        LoadHistory();
        UpdateStatus();
        UpdateBadge();
        RefreshSummary();
    }

    // ================================================================== жизненный цикл

    protected override void OnAppearing()
    {
        base.OnAppearing();
        AppState.ChatVisible = true;
        UpdateStatus();
        UpdateBadge();
        RefreshSummary();
        LoadWeatherIfNeeded();
        _lifeTimer ??= StartTimer(2500, LifeTick);
        if (!_greeted)
        {
            _greeted = true;
            Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(900), Greet);
        }
        if (App.PendingReminderId is { } rid)
        {
            App.PendingReminderId = null;
            ShowReminderFromNotification(rid);
        }
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        AppState.ChatVisible = false;
    }

    private IDispatcherTimer StartTimer(int ms, Action tick)
    {
        var t = Dispatcher.CreateTimer();
        t.Interval = TimeSpan.FromMilliseconds(ms);
        t.Tick += (_, _) => tick();
        t.Start();
        return t;
    }

    private async Task OpenPage(Page page)
    {
        Touch();
        try { await Navigation.PushAsync(page); } catch { }
    }

    private void Touch()
    {
        _lastInteraction = System.Environment.TickCount64;
        if (_vera.Mode == PetMode.Sleeping)
        {
            _vera.Mode = PetMode.Idle;
            _vera.SetEmotion("surprised", 1500);
        }
    }

    private void Greet()
    {
        if (_bubbleKind is "reminder" or "voice") return; // не перебивать напоминание
        try
        {
            var (text, noKey) = Core.Greeting();
            _vera.SetEmotion(noKey ? "angry" : "smug", 7000);
            _vera.Hop();
            if (noKey)
                ShowBubble(text, null, new[] { ("Открыть настройки", true, (Action)(async () => { HideBubble(); await OpenPage(new SettingsPage()); })) }, ms: 15000);
            else ShowBubble(text, ms: 9000);
        }
        catch { }
    }

    /// <summary>Каждые 2.5 с: засыпание при простое, «z», иногда болтает сама.</summary>
    private void LifeTick()
    {
        var now = System.Environment.TickCount64;
        var idleFor = now - _lastInteraction;
        var calm = _vera.Mode == PetMode.Idle && !_busy && !_speech.IsActive && !_voice.IsCommandActive;
        if (calm && idleFor > 4 * 60_000 && !_bubble.IsVisible) _vera.Mode = PetMode.Sleeping;
        if (_vera.Mode == PetMode.Sleeping) Fx("z", Color.FromArgb("#b9b0d6"), 38 + Random.Shared.Next(6), 4, 15);
        var s = Core.Settings.Current.Pet;
        if (s.IdleChatter && calm && now > _nextIdleChat && idleFor > 90_000 && !_bubble.IsVisible)
        {
            _nextIdleChat = now + (6 + Random.Shared.Next(8)) * 60_000L;
            _vera.SetEmotion("smug", 6000);
            ShowBubble(Core.Line("idle"));
        }
        if (_live.Count == 0 && (now / 2500) % 12 == 0)
        {
            UpdateStatus();
            RefreshSummary();
            LoadWeatherIfNeeded();
        }
    }

    // ================================================================== статус и бейдж

    private void UpdateStatus()
    {
        var s = Core.Settings.Current;
        var title = ProviderRegistry.Known.FirstOrDefault(k => k.Id == s.Provider).Title ?? s.Provider;
        _statusDot.IsVisible = false;
        if (_busy) { _status.Text = "печатает…"; _status.TextColor = Ui.AccentText; return; }
        if (_voice.IsCommandActive) { _status.Text = "слушает…"; _status.TextColor = Ui.AccentText; return; }
        _status.TextColor = Ui.Muted;
        var pc = Core.Sync.Peers.OrderByDescending(p => p.LastSync ?? DateTimeOffset.MinValue).FirstOrDefault();
        if (!Core.ProviderReady) _status.Text = $"нет ключа {title} — открой настройки";
        else if (_vera.Mode == PetMode.Sleeping) _status.Text = "спит… тапни, чтобы разбудить";
        else if (pc is not null)
        {
            // привязан ПК: «ПК «Дом» · синхр. только что»; точка зелёная, если синхронизация свежая
            var fresh = pc.LastSync is { } at && DateTimeOffset.Now - at < TimeSpan.FromHours(1);
            _statusDot.Color = fresh ? Ui.Ok : Ui.Faint;
            _statusDot.IsVisible = true;
            _status.Text = $"ПК «{pc.Name}» · " + (pc.LastSync is { } t ? "синхр. " + Ago(t) : "ещё не синхронизирована");
        }
        else _status.Text = $"{title} · {s.CurrentModel}";
    }

    private static string Ago(DateTimeOffset t) =>
        DateTimeOffset.Now - t < TimeSpan.FromMinutes(1) ? "только что" : RuText.Relative(t);

    private void UpdateBadge()
    {
        var n = Core.Reminders.Upcoming().Count;
        _remBadge.IsVisible = n > 0;
        _remBadgeText.Text = n > 99 ? "99+" : n.ToString();
    }

    // ================================================================== история

    private void LoadHistory()
    {
        _list.Children.Clear();
        _live.Clear();
        var hist = Core.Agent.GetVisibleHistory();
        var today = DateTime.Today;
        var summaryPlaced = false;
        foreach (var m in hist.Skip(Math.Max(0, hist.Count - HistoryShown)))
        {
            // сводка дня — перед первым сообщением за сегодня
            if (!summaryPlaced && m.Time.LocalDateTime.Date >= today)
            {
                _list.Children.Add(_summary);
                summaryPlaced = true;
            }
            var v = new MessageView(m.Role == "user", m.Time);
            if (m.Role == "user") v.SetUser(m.Text, null, m.HasImage);
            else v.SetVera(m.Text, m.Tools.Select(t => new ChipState { Id = t.Id, Name = t.Name, Label = t.Label, IsError = t.IsError }).ToList(), false);
            _list.Children.Add(v);
        }
        // сегодня ещё не говорили — сводка в конце ленты, новые сообщения пойдут под ней
        if (!summaryPlaced) _list.Children.Add(_summary);
        UpdateEmpty();
        RefreshSummary();
        ScrollToEnd(false, force: true);
    }

    private bool HasMessages => _list.Children.Any(c => c is MessageView);

    /// <summary>Пустой чат: подсказка с быстрыми просьбами — последней строкой ленты (под сводкой).</summary>
    private void UpdateEmpty()
    {
        var has = _list.Children.Contains(_empty);
        if (HasMessages) { if (has) _list.Children.Remove(_empty); }
        else if (!has) _list.Children.Add(_empty);
    }

    private View BuildEmpty()
    {
        var chips = new FlexLayout
        {
            Wrap = Microsoft.Maui.Layouts.FlexWrap.Wrap,
            JustifyContent = Microsoft.Maui.Layouts.FlexJustify.Center,
            Margin = new Thickness(0, 6, 0, 0),
        };
        foreach (var (label, text) in new[]
                 {
                     ("Напомни через 20 минут…", "Напомни мне через 20 минут сделать перерыв"),
                     ("Погода на завтра", "Какая погода в Москве на завтра?"),
                     ("Включи фонарик", "Включи фонарик"),
                     ("Открой YouTube", "Открой YouTube"),
                 })
        {
            var b = Ui.Chip(label, (_, _) => Send(text));
            b.Margin = new Thickness(4);
            chips.Children.Add(b);
        }
        return new VerticalStackLayout
        {
            Spacing = 8,
            Padding = new Thickness(14, 28, 14, 0),
            Children =
            {
                new Label { Text = "Ну что, начнём?", FontSize = 22, FontFamily = Ui.Display, TextColor = Ui.Text, HorizontalTextAlignment = TextAlignment.Center },
                new Label
                {
                    Text = "Я Вера. Спроси что-нибудь, попроси напомнить, открыть приложение или включить фонарик. Долгий тап по мне — сказать голосом.",
                    FontSize = 14.5, TextColor = Ui.Muted, HorizontalTextAlignment = TextAlignment.Center,
                },
                chips,
            },
        };
    }

    // ================================================================== сводка дня

    private VerticalStackLayout BuildSummary()
    {
        _sumCaption = new Label { FontSize = 12, FontFamily = Ui.FontBold, TextColor = Ui.Faint, HorizontalOptions = LayoutOptions.Center };
        _sumTemp = new Label { FontSize = 30, FontFamily = Ui.Display, TextColor = Ui.Accent, VerticalOptions = LayoutOptions.Center };
        _sumDesc = new Label { FontSize = 13, TextColor = Ui.Soft, LineBreakMode = LineBreakMode.WordWrap, VerticalOptions = LayoutOptions.Center };
        _sumWeather = new Grid
        {
            ColumnDefinitions = { new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star) },
            ColumnSpacing = 12,
            IsVisible = false,
        };
        _sumWeather.Add(_sumTemp, 0);
        _sumWeather.Add(_sumDesc, 1);
        _sumPills = new FlexLayout { Wrap = Microsoft.Maui.Layouts.FlexWrap.Wrap };
        _sumNoPills = new Label { Text = "На сегодня напоминаний нет", FontSize = 12.5, TextColor = Ui.Faint, IsVisible = false };
        var card = new Border
        {
            BackgroundColor = Ui.Surface,
            Stroke = Ui.Ink,
            StrokeThickness = 2.5,
            StrokeShape = Ui.Round(18),
            Padding = new Thickness(14),
            Margin = new Thickness(0, 0, 3, 3),
            Shadow = Ui.HardShadow(3),
            Content = new VerticalStackLayout { Spacing = 10, Children = { _sumWeather, _sumPills, _sumNoPills } },
        };
        var tap = new TapGestureRecognizer();
        tap.Tapped += async (_, _) => await OpenPage(new RemindersPage());
        card.GestureRecognizers.Add(tap);
        return new VerticalStackLayout { Spacing = 8, IsVisible = false, Children = { _sumCaption, card } };
    }

    /// <summary>Обновить сводку: подпись по времени суток, погода (если загружена), пилюли напоминаний на сегодня.</summary>
    private void RefreshSummary()
    {
        if (_sumCaption is null) return;
        var now = DateTimeOffset.Now;
        var h = now.Hour;
        _sumCaption.Text = (h < 5 ? "ночь" : h < 12 ? "утро" : h < 18 ? "день" : "вечер") + " · сводка";

        var city = (Core.Settings.Current.City ?? "").Trim();
        var w = city.Length > 0 && _weatherCity == city ? _weather : null;
        _sumWeather.IsVisible = w is not null;
        if (w is not null)
        {
            _sumTemp.Text = Signed(w.Temp) + "°";
            _sumDesc.Text = WeatherLine(w);
        }

        _sumPills.Children.Clear();
        var today = DateTime.Today;
        var items = Core.Reminders.Upcoming()
            .Where(r => r.Due.LocalDateTime.Date == today && r.Due > now.AddMinutes(-1))
            .Take(4)
            .ToList();
        foreach (var r in items)
        {
            // ближайшее — золотая пилюля, остальные — серые
            var first = _sumPills.Children.Count == 0;
            var text = r.Text.Length > 24 ? r.Text[..23].TrimEnd() + "…" : r.Text;
            _sumPills.Children.Add(new Border
            {
                BackgroundColor = first ? Ui.Accent : Colors.Transparent,
                Stroke = first ? Ui.Ink : Ui.Stroke,
                StrokeThickness = 2,
                StrokeShape = Ui.Round(13),
                Padding = new Thickness(9, 3),
                Margin = new Thickness(0, 0, 6, 6),
                Content = new Label
                {
                    Text = r.Due.LocalDateTime.ToString("HH:mm") + " " + text,
                    FontSize = 12,
                    FontFamily = Ui.FontBold,
                    TextColor = first ? Ui.OnAccent : Ui.Soft,
                    MaxLines = 1,
                    LineBreakMode = LineBreakMode.TailTruncation,
                },
            });
        }
        _sumPills.IsVisible = items.Count > 0;
        _sumNoPills.IsVisible = items.Count == 0 && w is not null;
        // нечего показать — сводку прячем
        _summary.IsVisible = w is not null || items.Count > 0;
    }

    private static string Signed(int t) => t > 0 ? "+" + t : t.ToString();

    /// <summary>«Облачно, −3…+1°, осадки 70%. Шарф не забудь.»</summary>
    private static string WeatherLine(WeatherNow w)
    {
        var d = string.IsNullOrWhiteSpace(w.Description) ? w.Place : char.ToUpper(w.Description[0]) + w.Description[1..];
        var s = $"{d}, {Signed(w.Min)}…{Signed(w.Max)}°";
        if (w.Precip is >= 50) s += $", осадки {w.Precip}%";
        var tip = w.Max <= 0 ? "Шарф не забудь."
            : w.Precip is >= 60 ? "Зонт не забудь."
            : w.Wind >= 10 ? "Ветрено — держи шапку."
            : w.Max >= 27 ? "Жарко — возьми воды."
            : "";
        return s + "." + (tip.Length > 0 ? " " + tip : "");
    }

    /// <summary>Погода для сводки — в фоне, не чаще раза в 30 минут; ошибки молча скрываем.</summary>
    private void LoadWeatherIfNeeded()
    {
        var city = (Core.Settings.Current.City ?? "").Trim();
        if (city.Length == 0 || _weatherLoading) return;
        if (_weatherCity == city && System.Environment.TickCount64 - _weatherAt < 30 * 60_000L) return;
        _weatherLoading = true;
        var core = Core;
        _ = Task.Run(async () =>
        {
            WeatherNow? w = null;
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                w = await core.Tools.WeatherNowAsync(city, cts.Token);
            }
            catch { /* нет сети или города — просто без погоды */ }
            MainThread.BeginInvokeOnMainThread(() =>
            {
                _weatherLoading = false;
                if (w is not null || _weatherCity != city) _weather = w;
                _weatherCity = city;
                // неудача — повторим минут через 5
                _weatherAt = System.Environment.TickCount64 - (w is null ? 25 * 60_000L : 0);
                RefreshSummary();
            });
        });
    }

    private void ScrollToEnd(bool animated, bool force = false)
    {
        if (!force && !_stickToBottom) return;
        Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(40), async () =>
        {
            try
            {
                var y = Math.Max(0, _list.Height - _scroll.Height);
                await _scroll.ScrollToAsync(0, y + 200, animated);
            }
            catch { }
        });
    }

    /// <summary>Меню «⋯» в шапке: новый диалог, синхронизация, память.</summary>
    private async Task MoreMenuAsync()
    {
        Touch();
        const string newChat = "Новый диалог", sync = "Синхронизация с ПК", memory = "Что Вера помнит";
        var choice = await DisplayActionSheetAsync("Ещё", "Отмена", null, newChat, sync, memory);
        switch (choice)
        {
            case newChat: await NewChatAsync(); break;
            case sync: await OpenPage(new SyncPage()); break;
            case memory: await OpenPage(new MemoryPage()); break;
        }
    }

    private async Task NewChatAsync()
    {
        Touch();
        if (!HasMessages) return;
        var ok = await DisplayAlertAsync("Новый диалог", "Текущая переписка будет очищена (память и напоминания останутся).", "Очистить", "Отмена");
        if (!ok) return;
        _speech.Stop();
        Core.Agent.ClearHistory();
        _busy = false;
        LoadHistory();
        UpdateSendIcon();
        UpdateStatus();
        _vera.SetEmotion("happy", 4000);
        ShowBubble("Начнём с чистого листа~", ms: 3500);
    }

    // ================================================================== отправка

    private void Send() => Send(null, "chat");

    private void Send(string? textOverride, string source = "chat")
    {
        Touch();
        if (_busy && textOverride is null)
        {
            Core.Agent.Stop();
            return;
        }
        if (_speech.IsActive && textOverride is null && string.IsNullOrWhiteSpace(_input.Text) && _attachments.Count == 0)
        {
            _speech.Stop();
            return;
        }
        var text = (textOverride ?? _input.Text ?? "").Trim();
        if (text.Length == 0 && _attachments.Count == 0 && _docs.Count == 0) return;
        var s = Core.Settings.Current;
        if (!Core.ProviderReady)
        {
            var title = ProviderRegistry.Known.FirstOrDefault(k => k.Id == s.Provider).Title ?? s.Provider;
            _vera.SetEmotion("angry", 5000);
            ShowBubble(Core.Line("noKey"), null, new[] { ("Открыть настройки", true, (Action)(async () => { HideBubble(); await OpenPage(new SettingsPage()); })) }, ms: 12000);
            Ui.Toast($"Сначала добавь ключ {title} в настройках");
            return;
        }
        if (Core.Agent.IsBusy)
        {
            Ui.Toast("Вера ещё отвечает на прошлое сообщение");
            return;
        }
        var images = _attachments.Count > 0 && textOverride is null ? _attachments.ToList() : null;
        var docs = _docs.Count > 0 && textOverride is null ? _docs.ToList() : null;
        if (textOverride is null)
        {
            _input.Text = "";
            _pendingPreview = _attachPreview;
            _pendingDocs = docs?.Select(d => d.Name).ToList();
            ClearAttachments();
            _docs.Clear();
            UpdateDocs();
        }
        else _pendingPreview = null;
        _speech.ResetErrors();
        _stickToBottom = true;
        _ = Task.Run(async () =>
        {
            try
            {
                await Core.Agent.SendAsync(text, images, source, documents: docs);
            }
            catch (AgentBusyException e)
            {
                Ui.Toast(e.Message);
            }
            catch
            {
                // уже показано событием TurnFailed
            }
        });
    }

    private bool HasDraft => !string.IsNullOrWhiteSpace(_input.Text) || _attachments.Count > 0 || _docs.Count > 0;

    /// <summary>Золотая кнопка: во время ответа/речи — стоп, с текстом/вложениями — отправить, иначе — голос.</summary>
    private void OnActionButton()
    {
        Touch();
        if (_busy || _speech.IsActive || HasDraft) { Send(); return; }
        _voice.Toggle();
    }

    private void UpdateSendIcon()
    {
        string src, hint;
        if (_busy || (_speech.IsActive && !HasDraft)) (src, hint) = ("ic_stop_dark.png", "Остановить");
        else if (HasDraft) (src, hint) = ("ic_send_dark.png", "Отправить");
        else if (_voice.State is RecorderState.Listening or RecorderState.Recording) (src, hint) = ("ic_mic_on_dark.png", "Перестать слушать");
        else (src, hint) = ("ic_mic_dark.png", "Сказать голосом");
        if (_sendBtn.Source is FileImageSource f && f.File == src) return;
        _sendBtn.Source = src;
        SemanticProperties.SetDescription(_sendBtn, hint);
    }

    // ================================================================== вложения

    /// <summary>Меню кнопки «+»: фото из галереи, камера, документ.</summary>
    private async Task AttachMenuAsync()
    {
        Touch();
        const string gallery = "Фото из галереи", camera = "Снять камерой", doc = "Файл или документ";
        var choice = await DisplayActionSheetAsync("Приложить", "Отмена", null, gallery, camera, doc);
        switch (choice)
        {
            case gallery: await PickImageAsync(false); break;
            case camera: await PickImageAsync(true); break;
            case doc: await PickDocumentsAsync(); break;
        }
    }

    private async Task PickImageAsync(bool camera)
    {
        Touch();
        try
        {
            FileResult? file;
            if (camera)
            {
                if (!MediaPicker.Default.IsCaptureSupported) { Ui.Toast("Камера недоступна"); return; }
                file = await MediaPicker.Default.CapturePhotoAsync();
            }
            else
            {
                file = await MediaPicker.Default.PickPhotoAsync();
            }
            if (file is null) return;
            await using var stream = await file.OpenReadAsync();
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            var raw = ms.ToArray();
            var jpeg = await Task.Run(() => ImageTools.PrepareJpeg(raw));
            _attachments.Clear();
            _attachments.Add(new ImageData("image/jpeg", Convert.ToBase64String(jpeg)));
            _attachPreview = jpeg;
            _attachImage.Source = ImageSource.FromStream(() => new MemoryStream(jpeg));
            _attachBox.IsVisible = true;
            if (string.IsNullOrWhiteSpace(_input.Text)) _input.Placeholder = "Что спросить про фото?";
            UpdateSendIcon();
            _vera.SetEmotion("surprised", 2500);
        }
        catch (PermissionException)
        {
            Ui.Toast("Нет разрешения на камеру или фото");
        }
        catch (Exception e)
        {
            Ui.Toast("Не получилось добавить фото: " + e.Message, true);
        }
    }

    // ================================================================== документы

    private async Task PickDocumentsAsync()
    {
        Touch();
        try
        {
            var files = await FilePicker.Default.PickMultipleAsync(new PickOptions { PickerTitle = "Что показать Вере?" });
            if (files is null) return;
            foreach (var f in files)
            {
                if (f is null) continue;
                var name = f.FileName;
                if (!DocumentReader.IsSupported(name))
                {
                    Ui.Toast($"«{name}»: такой формат пока не читаю. Подойдут PDF, DOCX, TXT/MD/CSV и код.", true);
                    continue;
                }
                await using var stream = await f.OpenReadAsync();
                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms);
                var bytes = ms.ToArray();
                var doc = await Task.Run(() => DocumentReader.Read(bytes, name));
                if (string.IsNullOrWhiteSpace(doc.Text)) { Ui.Toast($"В «{name}» не нашлось текста", true); continue; }
                _docs.Add(doc);
            }
            UpdateDocs();
            _vera.SetEmotion("think", 2500);
        }
        catch (PermissionException)
        {
            Ui.Toast("Нет разрешения на файлы");
        }
        catch (Exception e)
        {
            Ui.Toast("Не получилось прочитать файл: " + e.Message, true);
        }
    }

    private void UpdateDocs()
    {
        _docBox.IsVisible = _docs.Count > 0;
        _docLabel.Text = string.Join("\n", _docs.Select(d => "📄 " + d.Name + (d.Pages > 0 ? $" · {d.Pages} стр." : "")));
        if (_docs.Count > 0 && string.IsNullOrWhiteSpace(_input.Text)) _input.Placeholder = "Что сделать с документом?";
        UpdateSendIcon();
    }

    private void ClearAttachments()
    {
        _attachments.Clear();
        _attachPreview = null;
        _attachBox.IsVisible = false;
        _attachImage.Source = null;
        _input.Placeholder = "Напиши Вере…";
        UpdateSendIcon();
    }

    // ================================================================== события агента

    private bool ShouldSpeak(string source)
    {
        var t = Core.Settings.Current.Tts;
        if (t.Engine == "off") return false;
        return t.AutoSpeak switch
        {
            "always" => true,
            "never" => false,
            _ => source == "voice",
        };
    }

    private void OnAgentEvent(AgentEvent e)
    {
        Touch();
        switch (e)
        {
            case TurnStarted ts:
            {
                _speech.Stop();
                _busy = true;
                HideBubbleIf("turn");
                if (!ts.Hidden)
                {
                    var u = new MessageView(true, DateTimeOffset.Now);
                    var preview = _pendingPreview;
                    var userText = ts.UserText + (_pendingDocs is { Count: > 0 } pd ? "\n" + string.Join("\n", pd.Select(n => "📄 " + n)) : "");
                    u.SetUser(userText, preview is not null ? ImageSource.FromStream(() => new MemoryStream(preview)) : null, ts.HasImage);
                    _list.Children.Add(u);
                }
                _pendingPreview = null;
                _pendingDocs = null;
                var v = new MessageView(false, DateTimeOffset.Now);
                v.SetVera("", Array.Empty<ChipState>(), typing: true);
                _list.Children.Add(v);
                var speak = ShouldSpeak(ts.Source);
                var turn = new LiveTurn { View = v, Speak = speak, Source = ts.Source };
                if (speak) turn.Chunker = new SentenceChunker(c => _speech.Enqueue(c));
                _live[ts.TurnId] = turn;
                _vera.Mode = PetMode.Thinking;
                UpdateEmpty();
                UpdateSendIcon();
                UpdateStatus();
                ScrollToEnd(true, force: true);
                break;
            }
            case EmotionChanged em:
                _vera.SetEmotion(em.Emotion, 20000);
                ReactToEmotion(em.Emotion);
                break;
            case TextDelta d:
            {
                if (!_live.TryGetValue(d.TurnId, out var t)) return;
                if (t.LastStep is not null && t.LastStep != d.Step && t.Text.Length > 0)
                {
                    var trimmed = t.Text.ToString().TrimEnd();
                    t.Text.Clear().Append(trimmed).Append("\n\n");
                }
                if (!t.StepStart.ContainsKey(d.Step)) t.StepStart[d.Step] = t.Text.Length;
                t.LastStep = d.Step;
                t.Text.Append(d.Delta);
                if (_vera.Mode == PetMode.Thinking) _vera.Mode = PetMode.Talking;
                if (!t.Speak) _vera.Flap();
                t.Chunker?.Push(d.Delta);
                MarkDirty(d.TurnId);
                break;
            }
            case TextReplaced tr:
            {
                // модель написала команду JSON-ом — её выполнили, убираем JSON из текста
                if (!_live.TryGetValue(tr.TurnId, out var t)) return;
                var start = t.StepStart.TryGetValue(tr.Step, out var st) ? Math.Min(st, t.Text.Length) : t.Text.Length;
                t.Text.Length = start;
                t.Text.Append(tr.Text);
                t.StepStart[tr.Step] = start;
                t.LastStep = tr.Step;
                MarkDirty(tr.TurnId);
                break;
            }
            case ToolActivity ta:
            {
                if (!_live.TryGetValue(ta.TurnId, out var t)) return;
                var chip = t.Tools.FirstOrDefault(c => c.Id == ta.CallId);
                if (chip is null) t.Tools.Add(chip = new ChipState { Id = ta.CallId, Name = ta.Name });
                chip.Running = ta.Running;
                chip.Label = ta.Label;
                chip.IsError = ta.IsError;
                if (ta.Running && _vera.Mode != PetMode.Talking) _vera.Mode = PetMode.Thinking;
                if (!ta.Running && ta.Kind == "reminder") { Fx("⏰", Ui.Accent, 46, -4); _vera.Hop(); }
                if (!ta.Running && ta.Kind == "memory") Fx("✦", Color.FromArgb("#ffd166"), 46, -2);
                MarkDirty(ta.TurnId);
                break;
            }
            case TurnEnded te:
            {
                if (_live.Remove(te.TurnId, out var t))
                {
                    t.Chunker?.End();
                    if (t.Text.Length == 0 && te.Text.Length > 0) t.Text.Append(te.Text);
                    var text = t.Text.ToString();
                    t.View.SetVera(text.Length > 0 || t.Tools.Count > 0 ? text : "…", t.Tools, false);
                    _dirty.Remove(te.TurnId);
                }
                _busy = false;
                if (!_speech.IsActive) _vera.Mode = PetMode.Idle;
                _vera.SetEmotion(te.Emotion, 12000);
                UpdateSendIcon();
                UpdateStatus();
                ScrollToEnd(true);
                break;
            }
            case TurnFailed tf:
            {
                _live.Remove(tf.TurnId, out var t);
                _dirty.Remove(tf.TurnId);
                _busy = false;
                _vera.Mode = PetMode.Idle;
                if (t is not null)
                {
                    var text = t.Text.ToString();
                    if (tf.Aborted)
                    {
                        _speech.Stop();
                        t.View.SetVera(text.Length > 0 ? text + " …" : "*(остановлено)*", t.Tools, false);
                    }
                    else
                    {
                        t.Chunker = null;
                        t.View.SetVera((text.Length > 0 ? text + "\n\n" : "") + "⚠️ " + tf.Message, t.Tools, false, error: true);
                    }
                }
                if (!tf.Aborted)
                {
                    _vera.SetEmotion("sad", 8000);
                    Sfx.Error();
                    ShowBubble(tf.Message, "ой…", ms: 10000);
                }
                UpdateSendIcon();
                UpdateStatus();
                ScrollToEnd(true);
                break;
            }
        }
    }

    private void MarkDirty(string turnId)
    {
        _dirty.Add(turnId);
        if (_renderScheduled) return;
        _renderScheduled = true;
        Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(80), () =>
        {
            _renderScheduled = false;
            foreach (var id in _dirty.ToList())
            {
                if (!_live.TryGetValue(id, out var t)) continue;
                var text = t.Text.ToString();
                t.View.SetVera(text, t.Tools, typing: text.Length == 0);
            }
            _dirty.Clear();
            ScrollToEnd(false);
        });
    }

    private void ReactToEmotion(string emotion)
    {
        switch (emotion)
        {
            case "happy":
            case "laugh":
                _vera.Hop();
                break;
            case "angry":
                _vera.Shake();
                Fx("💢", Ui.Danger, 46, 0);
                break;
            case "shy":
                Fx("♥", Ui.Accent, 44, 2);
                break;
            case "surprised":
                Fx("!", Ui.Warn, 46, -2);
                break;
            case "think":
                Fx("?", Ui.Muted, 46, 0);
                break;
        }
    }

    // ================================================================== голос

    private void OnVoiceState(RecorderState state, string? reason)
    {
        Touch();
        switch (state)
        {
            case RecorderState.Listening:
                _speech.Stop();
                _vera.Mode = PetMode.Listening;
                ShowBubble(Core.Line("listening"), "🎙️ слушаю", sticky: true, kind: "voice");
                SetBars(true);
                break;
            case RecorderState.Recording:
                _vera.Mode = PetMode.Recording;
                SetBars(true);
                break;
            case RecorderState.Processing:
                _vera.Mode = PetMode.Processing;
                SetBars(false);
                ShowBubble("распознаю…", "🎙️", sticky: true, kind: "voice");
                break;
            default:
                SetBars(false);
                if (_vera.Mode is PetMode.Listening or PetMode.Recording or PetMode.Processing) _vera.Mode = PetMode.Idle;
                if (reason is "timeout" or "short") ShowBubble(Core.Line("notHeard"), ms: 3500);
                else HideBubbleIf("voice");
                break;
        }
        UpdateSendIcon();
        UpdateStatus();
    }

    private void SetBars(bool on)
    {
        _levelBars.IsVisible = on;
        if (on && _barsTimer is null)
        {
            _barsTimer = StartTimer(70, () =>
            {
                var v = _micLevel;
                var t = System.Environment.TickCount64 / 90.0;
                for (var i = 0; i < _levelBars.Children.Count; i++)
                {
                    var k = Math.Max(0, v * 18 * (0.6 + 0.4 * Math.Sin(i * 1.7 + t)));
                    ((BoxView)_levelBars.Children[i]).HeightRequest = 3 + Math.Min(15, k);
                }
            });
        }
        else if (!on && _barsTimer is not null)
        {
            _barsTimer.Stop();
            _barsTimer = null;
        }
    }

    // ================================================================== тап по Вере

    private void Poke()
    {
        Touch();
        if (_voice.IsCommandActive) { _voice.Toggle(); return; }
        if (_speech.IsActive) { _speech.Stop(); return; }
        _pokes++;
        _vera.Hop();
        var emo = _pokes % 4 == 0 ? "angry" : Random.Shared.NextDouble() < 0.5 ? "laugh" : "smug";
        _vera.SetEmotion(emo, 4000);
        if (emo == "angry") _vera.Shake();
        try { HapticFeedback.Default.Perform(HapticFeedbackType.Click); } catch { }
        ShowBubble(Core.Line("poke"), ms: 3500);
        Dispatcher.DispatchDelayed(TimeSpan.FromSeconds(8), () => _pokes = Math.Max(0, _pokes - 1));
    }

    // ================================================================== напоминания

    private void OnReminderDue(Reminder r, bool missed, string phrase)
    {
        Touch();
        var s = Core.Settings.Current;
        _vera.Mode = PetMode.Idle;
        if (s.Reminders.Sound) Sfx.Reminder();
        _vera.SetEmotion(missed ? "surprised" : "happy", 15000);
        Fx("!", Ui.Warn, 46, -2);
        _vera.Hop();
        ShowReminderBubble(r, missed ? "⏰ пропущенное" : "⏰ напоминание", phrase);
        if (s.Reminders.Speak && s.Tts.Engine != "off")
        {
            _speech.Stop();
            var delay = s.Reminders.Sound ? 900 : 0;
            Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(delay), () => _speech.Enqueue(phrase));
        }
    }

    private void ShowReminderBubble(Reminder r, string head, string text)
    {
        ShowBubble(text, head, new[]
        {
            ("Готово ✓", true, (Action)(() =>
            {
                Core.Reminders.Complete(r.Id);
                ReminderNotifier.Cancel(r.Id);
                _vera.SetEmotion("smug", 6000);
                HideBubble();
            })),
            ("+10 мин", false, (Action)(() =>
            {
                Core.Reminders.Snooze(r.Id, 10);
                ReminderNotifier.Cancel(r.Id);
                HideBubble();
                Ui.Toast("Напомню через 10 минут");
            })),
            ("+1 час", false, (Action)(() =>
            {
                Core.Reminders.Snooze(r.Id, 60);
                ReminderNotifier.Cancel(r.Id);
                HideBubble();
                Ui.Toast("Напомню через час");
            })),
        }, sticky: true, kind: "reminder");
        var gen = _bubbleGen;
        Dispatcher.DispatchDelayed(TimeSpan.FromMinutes(2), () => { if (gen == _bubbleGen) HideBubble(); });
    }

    private void ShowReminderFromNotification(string id)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            var r = Core.Reminders.Get(id);
            if (r is null) return;
            ReminderNotifier.Cancel(id);
            _vera.SetEmotion("happy", 8000);
            _vera.Hop();
            ShowReminderBubble(r, "⏰ напоминание", r.Text);
        });
    }

    // ================================================================== облачко и эффекты

    private void ShowBubble(string text, string? head = null, IEnumerable<(string Label, bool Primary, Action OnClick)>? actions = null,
        bool sticky = false, int? ms = null, string kind = "")
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var gen = ++_bubbleGen;
        _bubbleKind = kind;
        _bubbleSticky = sticky;
        _bubbleHead.Text = head ?? "";
        _bubbleHead.IsVisible = !string.IsNullOrEmpty(head);
        _bubbleText.Text = text;
        _bubbleActions.Children.Clear();
        foreach (var a in actions ?? Array.Empty<(string, bool, Action)>())
        {
            var b = a.Primary ? Ui.Primary(a.Label, (_, _) => a.OnClick()) : Ui.Ghost(a.Label, (_, _) => a.OnClick());
            b.FontSize = 13;
            b.MinimumHeightRequest = 34;
            b.HeightRequest = 36;
            b.Padding = new Thickness(10, 2);
            _bubbleActions.Children.Add(b);
        }
        _bubbleActions.IsVisible = _bubbleActions.Children.Count > 0;
        var width = Width > 0 ? Width : DeviceDisplay.MainDisplayInfo.Width / DeviceDisplay.MainDisplayInfo.Density;
        _bubble.MaximumWidthRequest = Math.Max(180, width - _bubble.Margin.Left - 12);
        if (!_bubble.IsVisible)
        {
            _bubble.IsVisible = true;
            _bubble.Opacity = 0;
            _bubble.Scale = 0.92;
            _bubble.AnchorX = 0;
            _bubble.AnchorY = 1;
            _ = _bubble.FadeToAsync(1, 140);
            _ = _bubble.ScaleToAsync(1, 160, Easing.CubicOut);
        }
        if (!sticky)
        {
            var baseMs = Math.Max(3, Core.Settings.Current.Pet.BubbleSeconds) * 1000;
            var life = ms ?? Math.Min(baseMs + text.Length * 45, 40000);
            Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(life), () => { if (gen == _bubbleGen) HideBubble(); });
        }
    }

    private void HideBubbleIf(string kind)
    {
        if (_bubbleKind == kind) HideBubble();
    }

    private async void HideBubble()
    {
        var gen = ++_bubbleGen;
        _bubbleSticky = false;
        _bubbleKind = "";
        SetBars(false);
        if (!_bubble.IsVisible) return;
        try { await _bubble.FadeToAsync(0, 140); } catch { }
        if (gen == _bubbleGen) _bubble.IsVisible = false;
    }

    /// <summary>Всплывающий символ у головы Веры (координаты — пиксели спрайта).</summary>
    private async void Fx(string symbol, Color color, double sx, double sy, double size = 20)
    {
        var l = new Label { Text = symbol, TextColor = color, FontSize = size, FontAttributes = FontAttributes.Bold };
        AbsoluteLayout.SetLayoutBounds(l, new Rect(4 + sx * _unit, sy * _unit, -1, -1));
        _fx.Children.Add(l);
        try
        {
            var rise = Task.WhenAll(l.TranslateToAsync(8, -28, 1600, Easing.CubicOut), l.FadeToAsync(0, 1600, Easing.CubicIn));
            await rise;
        }
        catch { }
        _fx.Children.Remove(l);
    }
}
