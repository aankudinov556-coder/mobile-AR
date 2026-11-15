using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Shell;
using System.Windows.Threading;
using Vera.Core;
using Vera.Core.Agent;
using Vera.Core.Providers;
using Vera.Desktop.Platform;
using Vera.Desktop.UI;

namespace Vera.Desktop.Chat;

/// <summary>
/// Главное окно «Ореол»: слева панель разделов с нимбом над выбранным, сверху сцена с живой Верой,
/// в середине раздел (чат, напоминания, память, настройки), справа «Сегодня» (на широком окне).
/// </summary>
public sealed class ChatWindow : Window
{
    private readonly AppHost _host;
    private readonly Dictionary<string, RadioButton> _tabs = new();
    private readonly Dictionary<string, FrameworkElement> _panels = new();
    private readonly Border _badge;
    private readonly TextBlock _badgeText;
    private readonly Border _toast;
    private readonly TextBlock _toastText;
    private readonly DispatcherTimer _toastTimer = new();
    private readonly Border _root;
    private readonly StageView _stage;
    private readonly TodayPanel _today;
    private readonly Border _rail;
    private readonly Grid _railGrid;
    private readonly Ellipse _halo;
    private readonly TranslateTransform _haloMove = new(), _haloBob = new();
    private double _haloY = double.NaN;
    private readonly Button _sizeBtn;
    private readonly UniformGrid _topTabs;
    private readonly Dictionary<string, RadioButton> _topTabButtons = new();
    private bool _compact;
    private double _wideWidth = 1060;
    private bool _positioned;
    private bool _typing;
    private string _current = "chat";

    public ChatWindow(AppHost host)
    {
        _host = host;
        Title = L10n.T("Вера");
        Width = 1060;
        Height = 720;
        MinWidth = 420;
        MinHeight = 500;
        FontFamily = Ui.Font;
        FontSize = 14;
        UseLayoutRounding = true;
        ShowActivated = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
        this.Res(BackgroundProperty, "Bg");
        this.Res(ForegroundProperty, "Fg");
        try { Icon = BitmapFrame.Create(new Uri("pack://application:,,,/assets/icon.ico")); } catch (Exception) { }

        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 44,
            ResizeBorderThickness = new Thickness(5),
            GlassFrameThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false,
        });

        // ------------------------------------------------------------ кнопки окна (на сцене справа)
        var newChat = Ui.IconButton("edit", "Новый диалог", () => ChatPanel?.NewChat(), 34, 16);
        _sizeBtn = Ui.IconButton("shrink", "Узкий режим — рядом с Верой", ToggleCompact, 34, 16);
        var min = Ui.IconButton("minus", "Свернуть", () => WindowState = WindowState.Minimized, 34, 16);
        var close = Ui.IconButton("x", "Закрыть", Hide, 34, 16);
        foreach (var b in new[] { newChat, _sizeBtn, min, close }) WindowChrome.SetIsHitTestVisibleInChrome(b, true);
        var windowButtons = new StackPanel { Orientation = Orientation.Horizontal, Children = { newChat, _sizeBtn, min, close } };

        _stage = new StageView(() => _host.Core.Settings.Current.Skin, windowButtons);

        // ------------------------------------------------------------ панель разделов
        _badgeText = new TextBlock { FontSize = 10.5, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center };
        _badgeText.SetResourceReference(TextBlock.ForegroundProperty, "OnAccent");
        _badge = new Border
        {
            CornerRadius = new CornerRadius(9),
            MinWidth = 18,
            Height = 18,
            Padding = new Thickness(4, 0, 4, 0),
            BorderThickness = new Thickness(1.5),
            BorderBrush = Brushes.Black,
            Child = _badgeText,
            Visibility = Visibility.Collapsed,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, -6, -8, 0),
            IsHitTestVisible = false,
        };
        _badge.SetResourceReference(Border.BackgroundProperty, "Accent");

        var logoText = Ui.Heading("В", 20);
        logoText.SetResourceReference(TextBlock.ForegroundProperty, "OnAccent");
        logoText.HorizontalAlignment = HorizontalAlignment.Center;
        logoText.VerticalAlignment = VerticalAlignment.Center;
        var logo = new Border
        {
            Width = 44,
            Height = 44,
            CornerRadius = new CornerRadius(14),
            BorderThickness = new Thickness(2.5),
            BorderBrush = Brushes.Black,
            Effect = Anim.HardShadow(3),
            Child = logoText,
            Margin = new Thickness(0, 14, 0, 26),
        };
        logo.SetResourceReference(Border.BackgroundProperty, "Accent");

        var top = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        var bottom = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 14) };
        top.Children.Add(logo);
        foreach (var (id, text, icon) in new[]
                 {
                     ("chat", "Чат", "chat"), ("reminders", "Напоминания", "bell"), ("memory", "Память", "star"),
                     ("phone", "Телефон", "phone"), ("settings", "Настройки", "gear"),
                 })
        {
            var content = new Grid { Children = { Icons.Make(icon, 21) } };
            if (id == "reminders") content.Children.Add(_badge);
            var rb = new RadioButton { Content = content, GroupName = "rail", Style = Ui.Style("RailItem"), ToolTip = text, Margin = new Thickness(0, 0, 0, 10) };
            var tabId = id;
            rb.Checked += (_, _) => ShowTab(tabId);
            _tabs[id] = rb;
            (id == "settings" ? bottom : top).Children.Add(rb);
        }
        // нимб над выбранным разделом: переезжает между кнопками и тихо покачивается
        _halo = new Ellipse
        {
            Width = 30,
            Height = 10,
            StrokeThickness = 3,
            IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            RenderTransform = new TransformGroup { Children = { _haloMove, _haloBob } },
        };
        _halo.SetResourceReference(Shape.StrokeProperty, "Accent");
        Anim.Bob(_haloBob, 1.6, 1.4);
        _railGrid = new Grid { Children = { top, bottom, _halo } };
        _rail = new Border { Width = 78, BorderThickness = new Thickness(0, 0, 2, 0), Child = _railGrid };
        _rail.SetResourceReference(Border.BackgroundProperty, "Rail");
        _rail.SetResourceReference(Border.BorderBrushProperty, "Bg3");
        WindowChrome.SetIsHitTestVisibleInChrome(_railGrid, true);

        // ------------------------------------------------------------ разделы
        ChatPanel = new ChatPanel(host, this);
        RemindersPanel = new RemindersPanel(host, this);
        MemoryPanel = new MemoryPanel(host, this);
        SettingsPanel = new SettingsPanel(host, this);
        _panels["chat"] = ChatPanel;
        _panels["reminders"] = RemindersPanel;
        _panels["memory"] = MemoryPanel;
        _panels["settings"] = SettingsPanel;
        var content2 = new Grid();
        foreach (var p in _panels.Values)
        {
            p.Visibility = Visibility.Collapsed;
            content2.Children.Add(p);
        }

        _today = new TodayPanel(host);

        // ------------------------------------------------------------ вкладки сверху (узкий режим)
        _topTabs = new UniformGrid { Rows = 1, Margin = new Thickness(12, 10, 12, 10), Visibility = Visibility.Collapsed };
        foreach (var (id, text) in new[] { ("chat", "Чат"), ("reminders", "Дела"), ("memory", "Память"), ("settings", "Настройки") })
        {
            var rb = new RadioButton { Content = text, GroupName = "topTabs", Style = Ui.Style("Segment"), Margin = new Thickness(0, 0, 6, 0) };
            var tabId = id;
            rb.Checked += (_, _) => { if (_current != tabId) ShowTab(tabId); };
            _topTabButtons[id] = rb;
            _topTabs.Children.Add(rb);
        }
        var topTabsHost = new Border { Child = _topTabs, BorderThickness = new Thickness(0, 0, 0, 2) };
        topTabsHost.SetResourceReference(Border.BorderBrushProperty, "Bg3");

        // ------------------------------------------------------------ всплывающие подсказки
        _toastText = new TextBlock { FontSize = 13.5, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        _toast = new Border
        {
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(16, 10, 16, 10),
            Margin = new Thickness(16, 0, 16, 92),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            MaxWidth = 460,
            BorderThickness = new Thickness(2.5),
            BorderBrush = Brushes.Black,
            Effect = Anim.HardShadow(3),
            Child = _toastText,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
        };
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); _toast.Visibility = Visibility.Collapsed; };

        var main = new Grid();
        main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        main.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(_stage, 0);
        Grid.SetRow(topTabsHost, 1);
        Grid.SetRow(content2, 2);
        Grid.SetRowSpan(_toast, 3);
        main.Children.Add(_stage);
        main.Children.Add(topTabsHost);
        main.Children.Add(content2);
        main.Children.Add(_toast);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_rail, 0);
        Grid.SetColumn(main, 1);
        Grid.SetColumn(_today, 2);
        grid.Children.Add(_rail);
        grid.Children.Add(main);
        grid.Children.Add(_today);
        _root = new Border { BorderThickness = new Thickness(2.5), BorderBrush = Brushes.Black, Child = grid };
        Content = _root;

        SizeChanged += (_, _) => Relayout();
        StateChanged += (_, _) =>
        {
            // развёрнутое окно с WindowChrome вылезает за край экрана на толщину рамки
            _root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
        };
        SourceInitialized += (_, _) => Native.RoundCorners(new WindowInteropHelper(this).Handle);
        Closing += OnClosing;
        _railGrid.LayoutUpdated += (_, _) => PlaceHalo(false);

        _tabs["chat"].IsChecked = true;
        ShowTab("chat");
        UpdateStatus();
    }

    public ChatPanel ChatPanel { get; }
    public RemindersPanel RemindersPanel { get; }
    public MemoryPanel MemoryPanel { get; }
    public SettingsPanel SettingsPanel { get; }
    public string CurrentTab => _current;

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_host.Quitting) return;
        e.Cancel = true;
        Hide();
    }

    // ================================================================== layout

    /// <summary>Нимб — над выбранной кнопкой раздела.</summary>
    private void PlaceHalo(bool animate)
    {
        if (!_tabs.TryGetValue(_current, out var rb) || !rb.IsLoaded) return;
        var y = rb.TranslatePoint(new Point(0, 0), _railGrid).Y - 7;
        if (Math.Abs(y - _haloY) < 0.5) return;
        var first = double.IsNaN(_haloY);
        _haloY = y;
        if (first || !animate)
        {
            _haloMove.BeginAnimation(TranslateTransform.YProperty, null);
            _haloMove.Y = y;
        }
        else Anim.MoveY(_haloMove, y);
    }

    /// <summary>Узкое окно — без панели «Сегодня»; низкое окно и не-чат — со свёрнутой сценой.</summary>
    private void Relayout()
    {
        // узкий режим (рядом с Верой): без боковой панели, вкладки сверху, сцена — строкой
        var compact = ActualWidth < 640;
        if (compact != _compact)
        {
            _compact = compact;
            _rail.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            _topTabs.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
            ChatPanel.SetCompact(compact);
            _sizeBtn.Content = Icons.Make(compact ? "expand" : "shrink", 16);
            _sizeBtn.ToolTip = compact ? "Развернуть окно" : "Узкий режим — рядом с Верой";
            if (compact) Anim.Appear(_topTabs, 8, 200);
        }
        var side = _current is "chat" or "reminders" or "memory";
        _today.Visibility = ActualWidth >= 920 && side ? Visibility.Visible : Visibility.Collapsed;
        _stage.Slim = compact || ActualHeight < 600 || _current != "chat";
    }

    /// <summary>Переключить узкий (440 px рядом с Верой) и широкий режим окна.</summary>
    private void ToggleCompact()
    {
        if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal;
        var right = Left + ActualWidth;
        if (_compact)
        {
            Width = Math.Max(_wideWidth, 900);
            Left = Math.Max(SystemParameters.WorkArea.Left, right - Width);
        }
        else
        {
            _wideWidth = ActualWidth;
            Width = 440;
            Left = right - Width; // правый край остаётся на месте — ближе к Вере
        }
    }

    public void ShowTab(string name)
    {
        var tab = name == "phone" || _panels.ContainsKey(name) ? name : "chat";
        var panel = tab == "phone" ? "settings" : tab;
        var before = _current == "phone" ? "settings" : _current;
        _current = tab;
        if (_tabs[tab].IsChecked != true) _tabs[tab].IsChecked = true;
        var top = tab == "phone" ? "settings" : tab;
        if (_topTabButtons.TryGetValue(top, out var tb) && tb.IsChecked != true) tb.IsChecked = true;
        foreach (var (id, p) in _panels) p.Visibility = id == panel ? Visibility.Visible : Visibility.Collapsed;
        if (panel != before) Anim.SlideIn(_panels[panel]);
        switch (panel)
        {
            case "chat": ChatPanel.FocusInput(); break;
            case "reminders": RemindersPanel.Refresh(); break;
            case "memory": MemoryPanel.Refresh(); break;
            case "settings":
                SettingsPanel.Refresh();
                if (tab == "phone") SettingsPanel.ShowSection("sync");
                break;
        }
        Relayout();
        PlaceHalo(true);
    }

    /// <summary>Показать окно (при первом показе — рядом с Верой) и вывести вперёд.</summary>
    public void ShowAndActivate(IntPtr petHwnd)
    {
        var appear = !IsVisible || WindowState == WindowState.Minimized;
        if (!IsVisible)
        {
            if (!_positioned)
            {
                PositionNear(petHwnd);
                _positioned = true;
            }
            Show();
        }
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        if (appear) Anim.Pop(_root, 260, 0.96);
        Activate();
        // если Windows не дала активировать окно из фона — хотя бы поднимем его наверх
        Topmost = true;
        Topmost = false;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero) Native.SetForegroundWindow(hwnd);
    }

    private void PositionNear(IntPtr petHwnd)
    {
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        var mb = Native.WindowRect(hwnd);
        if (petHwnd == IntPtr.Zero || mb.Width <= 0) return;
        var pb = Native.WindowRect(petHwnd);
        if (pb.Width <= 0) return;
        var wa = Native.WorkAreaAt(pb.Left + pb.Width / 2, pb.Top + pb.Height / 2);
        var dpi = Native.DpiScale(hwnd);
        var x = pb.Left - mb.Width + (int)(30 * dpi);
        if (x < wa.Left) x = pb.Right - (int)(30 * dpi);
        var y = pb.Bottom - mb.Height;
        x = Math.Clamp(x, wa.Left, Math.Max(wa.Left, wa.Right - mb.Width));
        y = Math.Clamp(y, wa.Top, Math.Max(wa.Top, wa.Bottom - mb.Height));
        Native.MoveWindow(hwnd, x, y);
    }

    public void Toast(string text, bool error = false)
    {
        _toastText.Text = L10n.T(text);
        _toast.SetResourceReference(Border.BackgroundProperty, error ? "ToastErrBg" : "Bg2");
        _toastText.SetResourceReference(TextBlock.ForegroundProperty, error ? "ToastErrFg" : "Fg");
        _toast.Visibility = Visibility.Visible;
        Anim.Appear(_toast, 18, 240);
        _toastTimer.Stop();
        _toastTimer.Interval = TimeSpan.FromMilliseconds(error ? 6000 : 2800);
        _toastTimer.Start();
    }

    public void SetReminderBadge(int count)
    {
        var was = _badgeText.Text;
        _badge.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _badgeText.Text = count.ToString();
        if (count > 0 && was != _badgeText.Text) Anim.Pop(_badge);
        if (_topTabButtons.TryGetValue("reminders", out var rb)) rb.Content = count > 0 ? L10n.F("Дела · {0}", count) : L10n.T("Дела");
    }

    // ================================================================== status

    public void UpdateStatus()
    {
        var core = _host.Core;
        var s = core.Settings.Current;
        var title = ProviderRegistry.Known.FirstOrDefault(k => k.Id == s.Provider).Title ?? s.Provider;
        var model = s.Provider == "local" ? System.IO.Path.GetFileNameWithoutExtension(s.CurrentModel) : s.CurrentModel;
        if (ChatPanel.Busy) _stage.SetStatus(_typing ? "печатает ответ…" : "думает…", true);
        else if (!core.ProviderReady) _stage.SetStatus(L10n.F("нет ключа {0}", L10n.T(title)), true);
        else _stage.SetStatus("", false);
        var chips = new List<(string, bool)> { ($"{L10n.T(title)} · {model}", false) };
        var phone = core.Sync.Peers.Where(p => p.LastSync is not null).OrderByDescending(p => p.LastSync).FirstOrDefault();
        if (phone?.LastSync is { } at)
            chips.Add((L10n.F("{0} · синхр. {1}", phone.Name, RuText.Relative(at)), DateTimeOffset.Now - at < TimeSpan.FromMinutes(30)));
        chips.Add((L10n.T("Характер: ") + L10n.T(Personas.Get(s.Persona).Label), false));
        _stage.SetChips(chips);
    }

    public void OnAgentEvent(AgentEvent e)
    {
        switch (e)
        {
            case TurnStarted:
                _typing = false;
                _stage.SetEmotion("think");
                break;
            case EmotionChanged em:
                _stage.SetEmotion(em.Emotion);
                break;
            case TextDelta:
                if (!_typing) { _typing = true; UpdateStatus(); }
                _stage.SetTalking(true);
                break;
            case TurnEnded te:
                _typing = false;
                _stage.SetTalking(false);
                _stage.SetEmotion(string.IsNullOrEmpty(te.Emotion) ? "neutral" : te.Emotion, burst: false);
                break;
            case TurnFailed tf:
                _typing = false;
                _stage.SetTalking(false);
                if (!tf.Aborted) _stage.SetEmotion("sad");
                break;
        }
        ChatPanel.OnAgentEvent(e);
        if (e is TurnStarted or TurnEnded or TurnFailed) UpdateStatus();
    }

    /// <summary>Вера говорит голосом — сцена шевелит ртом.</summary>
    public void SetSpeaking(bool speaking)
    {
        if (!ChatPanel.Busy) _stage.SetTalking(speaking);
    }

    /// <summary>Скин сменился: новая поза на сцене и аватарки в чате.</summary>
    public void OnSkinChanged()
    {
        _stage.OnSkinChanged();
        ChatPanel.OnSkinChanged();
    }

    public void OnSettingsChanged()
    {
        SettingsPanel.Refresh();
        UpdateStatus();
    }

    public void OnSecretsChanged()
    {
        SettingsPanel.RenderKeys();
        SettingsPanel.Refresh();
        UpdateStatus();
    }
}
