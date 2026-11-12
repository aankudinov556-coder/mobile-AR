using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;
using Vera.Core.Agent;
using Vera.Core.Providers;
using Vera.Desktop.Pet;
using Vera.Desktop.Platform;
using Vera.Desktop.UI;

namespace Vera.Desktop.Chat;

/// <summary>Главное окно: заголовок, вкладки Чат / Напоминания / Память / Настройки, всплывающие подсказки.</summary>
public sealed class ChatWindow : Window
{
    private readonly AppHost _host;
    private readonly TextBlock _status;
    private readonly Dictionary<string, RadioButton> _tabs = new();
    private readonly Dictionary<string, FrameworkElement> _panels = new();
    private readonly List<TextBlock> _tabLabels = new();
    private readonly Border _badge;
    private readonly TextBlock _badgeText;
    private readonly Border _toast;
    private readonly TextBlock _toastText;
    private readonly DispatcherTimer _toastTimer = new();
    private readonly Border _root;
    private bool _positioned;
    private string _current = "chat";

    public ChatWindow(AppHost host)
    {
        _host = host;
        Title = "Вера";
        Width = 480;
        Height = 720;
        MinWidth = 380;
        MinHeight = 480;
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
            CaptionHeight = 40,
            ResizeBorderThickness = new Thickness(4),
            GlassFrameThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false,
        });

        // ------------------------------------------------------------ titlebar
        var avatarImg = new Image { Source = SpriteSheet.LoadImage("/assets/icon.png"), Width = 26, Height = 26 };
        RenderOptions.SetBitmapScalingMode(avatarImg, BitmapScalingMode.NearestNeighbor);
        var avatar = new Border { CornerRadius = new CornerRadius(7), Child = avatarImg, VerticalAlignment = VerticalAlignment.Center };
        avatar.SetResourceReference(Border.BackgroundProperty, "AccentSoft");
        _status = Ui.Text("загружаюсь…", 11, "Muted", wrap: false);
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        titles.Children.Add(Ui.Text("Вера", 13.5, "Fg", FontWeights.Bold, wrap: false));
        titles.Children.Add(_status);
        var min = Ui.IconButton("minus", "Свернуть", () => WindowState = WindowState.Minimized, 32, 16);
        var close = Ui.IconButton("x", "Закрыть", Hide, 32, 16);
        min.Height = close.Height = 30;
        WindowChrome.SetIsHitTestVisibleInChrome(min, true);
        WindowChrome.SetIsHitTestVisibleInChrome(close, true);
        var controls = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        controls.Children.Add(min);
        controls.Children.Add(close);
        var titleGrid = Ui.Row(new UIElement[] { avatar, titles, controls }, 0, 1);
        var titleBar = new Border { Height = 40, Padding = new Thickness(12, 0, 6, 0), BorderThickness = new Thickness(0, 0, 0, 1), Child = titleGrid };
        titleBar.SetResourceReference(Border.BackgroundProperty, "Bg2");
        titleBar.SetResourceReference(Border.BorderBrushProperty, "Line");

        // ------------------------------------------------------------ tabs
        _badgeText = new TextBlock { FontSize = 10, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center };
        _badge = new Border
        {
            CornerRadius = new CornerRadius(9),
            MinWidth = 17,
            Height = 17,
            Padding = new Thickness(5, 1, 5, 0),
            Margin = new Thickness(6, 0, 0, 0),
            Child = _badgeText,
            Visibility = Visibility.Collapsed,
        };
        _badge.SetResourceReference(Border.BackgroundProperty, "Accent");
        var tabGrid = new UniformGrid { Columns = 4, Rows = 1 };
        foreach (var (id, text, icon) in new[] { ("chat", "Чат", "chat"), ("reminders", "Напоминания", "bell"), ("memory", "Память", "star"), ("settings", "Настройки", "gear") })
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(Icons.Make(icon, 16));
            var label = new TextBlock { Text = text, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            _tabLabels.Add(label);
            sp.Children.Add(label);
            if (id == "reminders") sp.Children.Add(_badge);
            var rb = new RadioButton { Content = sp, GroupName = "tabs", Style = Ui.Style("Tab"), Margin = new Thickness(2, 0, 2, 0), ToolTip = text };
            var tabId = id;
            rb.Checked += (_, _) => ShowTab(tabId);
            _tabs[id] = rb;
            tabGrid.Children.Add(rb);
        }
        var tabBar = new Border { Padding = new Thickness(6, 6, 6, 6), BorderThickness = new Thickness(0, 0, 0, 1), Child = tabGrid };
        tabBar.SetResourceReference(Border.BorderBrushProperty, "Line");

        // ------------------------------------------------------------ panels
        ChatPanel = new ChatPanel(host, this);
        RemindersPanel = new RemindersPanel(host, this);
        MemoryPanel = new MemoryPanel(host, this);
        SettingsPanel = new SettingsPanel(host, this);
        _panels["chat"] = ChatPanel;
        _panels["reminders"] = RemindersPanel;
        _panels["memory"] = MemoryPanel;
        _panels["settings"] = SettingsPanel;
        var content = new Grid();
        foreach (var p in _panels.Values)
        {
            p.Visibility = Visibility.Collapsed;
            content.Children.Add(p);
        }

        // ------------------------------------------------------------ toast
        _toastText = new TextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap };
        _toast = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 9, 14, 9),
            Margin = new Thickness(16, 0, 16, 84),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            MaxWidth = 420,
            Child = _toastText,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 20, ShadowDepth = 6, Opacity = 0.35, Direction = 270 },
        };
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); _toast.Visibility = Visibility.Collapsed; };

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(titleBar, 0);
        Grid.SetRow(tabBar, 1);
        Grid.SetRow(content, 2);
        Grid.SetRow(_toast, 0);
        Grid.SetRowSpan(_toast, 3);
        grid.Children.Add(titleBar);
        grid.Children.Add(tabBar);
        grid.Children.Add(content);
        grid.Children.Add(_toast);
        _root = new Border { BorderThickness = new Thickness(1), Child = grid };
        _root.SetResourceReference(Border.BorderBrushProperty, "Line");
        Content = _root;

        SizeChanged += (_, _) =>
        {
            var compact = ActualWidth < 430;
            foreach (var l in _tabLabels) l.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        };
        StateChanged += (_, _) =>
        {
            // развёрнутое окно с WindowChrome вылезает за край экрана на толщину рамки
            _root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
        };
        SourceInitialized += (_, _) => Native.RoundCorners(new WindowInteropHelper(this).Handle);
        Closing += OnClosing;

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

    public void ShowTab(string name)
    {
        if (!_panels.ContainsKey(name)) name = "chat";
        _current = name;
        if (_tabs[name].IsChecked != true) _tabs[name].IsChecked = true;
        foreach (var (id, p) in _panels) p.Visibility = id == name ? Visibility.Visible : Visibility.Collapsed;
        switch (name)
        {
            case "chat": ChatPanel.FocusInput(); break;
            case "reminders": RemindersPanel.Refresh(); break;
            case "memory": MemoryPanel.Refresh(); break;
            case "settings": SettingsPanel.Refresh(); break;
        }
    }

    /// <summary>Показать окно (при первом показе — рядом с Верой) и вывести вперёд.</summary>
    public void ShowAndActivate(IntPtr petHwnd)
    {
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
        _toastText.Text = text;
        _toast.SetResourceReference(Border.BackgroundProperty, error ? "ToastErrBg" : "Bg4");
        _toastText.SetResourceReference(TextBlock.ForegroundProperty, error ? "ToastErrFg" : "Fg");
        _toast.Visibility = Visibility.Visible;
        _toast.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));
        _toastTimer.Stop();
        _toastTimer.Interval = TimeSpan.FromMilliseconds(error ? 6000 : 2800);
        _toastTimer.Start();
    }

    public void SetReminderBadge(int count)
    {
        _badge.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _badgeText.Text = count.ToString();
    }

    public void UpdateStatus()
    {
        var core = _host.Core;
        var s = core.Settings.Current;
        var title = ProviderRegistry.Known.FirstOrDefault(k => k.Id == s.Provider).Title ?? s.Provider;
        if (ChatPanel.Busy)
        {
            _status.Text = "печатает…";
            _status.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
            return;
        }
        _status.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        _status.Text = core.Secrets.Has(s.Provider) ? $"{title} · {s.CurrentModel}" : $"нет ключа {title} — открой настройки";
    }

    public void OnAgentEvent(AgentEvent e)
    {
        ChatPanel.OnAgentEvent(e);
        if (e is TurnStarted or TurnEnded or TurnFailed) UpdateStatus();
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
