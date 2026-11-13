using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using Vera.Core;
using Vera.Core.Agent;
using Vera.Core.Speech;
using Vera.Core.Sprites;
using Vera.Desktop.Audio;
using Vera.Desktop.Platform;
using Vera.Desktop.UI;

namespace Vera.Desktop.Pet;

/// <summary>
/// Прозрачное окно с чиби-Верой: мимика, дыхание, моргание, липсинк, прыжки, пузырь речи,
/// перетаскивание, «тык», сон ночью, болтовня. Повторяет src/renderer/pet.js.
/// </summary>
public sealed partial class PetWindow : Window
{
    private const double BubbleSpace = 210;
    private static readonly Color Ink = Color.FromRgb(0x2c, 0x1e, 0x34);
    private static readonly Color Paper = Color.FromRgb(0xff, 0xfa, 0xff);
    private static readonly Color PetAccent = Color.FromRgb(0xe0, 0x47, 0x9a);
    private static readonly Color PetMuted = Color.FromRgb(0x7d, 0x6f, 0x8f);
    private static readonly Random Rnd = new();

    private sealed class ToolView
    {
        public string Id = "", Name = "", Label = "";
        public bool Running, IsError;
    }

    private sealed class TurnView
    {
        public readonly StringBuilder Text = new();
        public readonly List<ToolView> Tools = new();
        public bool Speak;
        public int LastStep = -1;
        public readonly Dictionary<int, int> StepStart = new();
    }

    private enum FxKind { Plain, Zzz, Alert, Heart }

    private readonly AppHost _host;
    private readonly SpriteSheet _sheet = new();
    private readonly Dictionary<string, System.Windows.Media.Imaging.BitmapSource?> _haloFrames = new();
    private readonly ScaleTransform _talkScale = new();
    private readonly Border _glow;
    private SkinInfo _skin = Skins.All[0];
    private Posture _posture = Posture.Stand;

    private readonly Grid _stage;
    private readonly Grid _spriteArea;
    private readonly Grid _veraBox;
    private readonly Image _sprite;
    private readonly Ellipse _shadow;
    private readonly Grid _bubbleWrap;
    private readonly Border _bubble;
    private readonly TextBlock _bubbleHead;
    private readonly TextBlock _bubbleText;
    private readonly StackPanel _levels;
    private readonly Rectangle[] _bars = new Rectangle[7];
    private readonly WrapPanel _actions;
    private readonly Canvas _fx;
    private readonly TranslateTransform _breathe = new(), _hop = new(), _shake = new(), _lift = new();
    private readonly RotateTransform _tilt = new();
    private readonly TranslateTransform _bubbleShift = new();
    private readonly ContextMenu _menu;

    private readonly DispatcherTimer _frameTimer, _lifeTimer, _bubbleTimer, _dotsTimer, _clickTimer;

    private IntPtr _hwnd;
    private double _scale = 3;
    private double _dpi = 1;

    // ------------------------------------------------------------ state
    private string _emotion = "neutral";
    private DateTime _emotionUntil = DateTime.MinValue;
    private PetMode _mode = PetMode.Idle;
    private bool _blink;
    private DateTime _textFlapUntil = DateTime.MinValue;
    private bool _dragging;
    private DateTime _lastInteraction = DateTime.Now;
    private string _frameKey = "";
    private bool _bubbleSticky, _bubbleOpensChat, _bubbleVisible;
    private int _bubbleTextLen;
    private int _bubbleGen;
    private Run? _dotsRun;
    private string _dotsBase = "";
    private int _dotsPhase;
    private readonly Dictionary<string, TurnView> _turns = new();
    private int _pokes;
    private DateTime _nextIdleChat = DateTime.Now.AddMinutes(25 + Rnd.NextDouble() * 20);
    private Point? _pressPoint;
    private DateTime _hoverSince = DateTime.MinValue;
    private bool _greeted;

    public PetWindow(AppHost host)
    {
        _host = host;
        Title = "Вера";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        Topmost = S.Pet.AlwaysOnTop;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        FontFamily = new FontFamily("Segoe UI, Segoe UI Emoji, Segoe UI Symbol");
        Width = 340;
        Height = 72 * 3 + BubbleSpace + 12;

        // ------------------------------------------------------------ sprite
        _sprite = new Image { Stretch = Stretch.Fill, SnapsToDevicePixels = true };
        RenderOptions.SetBitmapScalingMode(_sprite, BitmapScalingMode.NearestNeighbor);
        RenderOptions.SetEdgeMode(_sprite, EdgeMode.Aliased);
        // drop-shadow(0 0 1px белый) + drop-shadow(0 2px 3px тень), как в pet.css
        _sprite.RenderTransformOrigin = new Point(0.5, 1);
        _sprite.RenderTransform = _talkScale; // покадровый скин «говорит» лёгким покачиванием
        _glow = new Border
        {
            Child = _sprite,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Effect = new DropShadowEffect { Color = Colors.White, ShadowDepth = 0, BlurRadius = 2, Opacity = 0.55 },
        };
        _veraBox = new Grid
        {
            Children = { _glow },
            Effect = new DropShadowEffect { Color = Colors.Black, Direction = 270, ShadowDepth = 2, BlurRadius = 5, Opacity = 0.35 },
            RenderTransformOrigin = new Point(0.5, 1),
            RenderTransform = new TransformGroup { Children = { _tilt, _lift, _breathe, _hop, _shake } },
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Top,
        };
        _shadow = new Ellipse
        {
            VerticalAlignment = VerticalAlignment.Bottom,
            Fill = new RadialGradientBrush
            {
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0x59, 20, 10, 30), 0),
                    new GradientStop(Color.FromArgb(0x00, 20, 10, 30), 0.7),
                },
            },
            IsHitTestVisible = false,
        };
        _spriteArea = new Grid { HorizontalAlignment = HorizontalAlignment.Center };
        _spriteArea.Children.Add(_shadow);
        _spriteArea.Children.Add(_veraBox);

        // ------------------------------------------------------------ bubble
        _bubbleHead = new TextBlock
        {
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(PetAccent),
            Margin = new Thickness(0, 0, 0, 2),
            Visibility = Visibility.Collapsed,
        };
        _bubbleText = new TextBlock
        {
            FontSize = 13.5,
            LineHeight = 18.6,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxHeight = 150,
            Foreground = new SolidColorBrush(Ink),
        };
        _levels = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Height = 18,
            Margin = new Thickness(0, 6, 0, 0),
            Visibility = Visibility.Collapsed,
        };
        for (var i = 0; i < _bars.Length; i++)
        {
            _bars[i] = new Rectangle
            {
                Width = 5,
                Height = 3,
                Fill = new SolidColorBrush(PetAccent),
                Stroke = new SolidColorBrush(Ink),
                StrokeThickness = 1,
                Margin = new Thickness(i == 0 ? 0 : 3, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Bottom,
            };
            _levels.Children.Add(_bars[i]);
        }
        _actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
        var bubbleBody = new StackPanel();
        bubbleBody.Children.Add(_bubbleHead);
        bubbleBody.Children.Add(_bubbleText);
        bubbleBody.Children.Add(_levels);
        bubbleBody.Children.Add(_actions);
        _bubble = new Border
        {
            Background = new SolidColorBrush(Paper),
            BorderBrush = new SolidColorBrush(Ink),
            BorderThickness = new Thickness(3),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(12, 9, 12, 10),
            MinWidth = 64,
            Child = bubbleBody,
            Cursor = Cursors.Arrow,
        };
        var tail = new Path
        {
            Data = Geometry.Parse("M0,0 L8,9 L16,0"),
            Fill = new SolidColorBrush(Paper),
            Stroke = new SolidColorBrush(Ink),
            StrokeThickness = 3,
            StrokeLineJoin = PenLineJoin.Miter,
        };
        Canvas.SetLeft(tail, -8);
        Canvas.SetTop(tail, -3.5);
        var tailHost = new Canvas
        {
            Width = 0,
            Height = 0,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            IsHitTestVisible = false,
            Children = { tail },
        };
        _bubbleWrap = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(12, 0, 12, 12),
            Visibility = Visibility.Collapsed,
            Opacity = 0,
            RenderTransform = _bubbleShift,
            Effect = new DropShadowEffect { BlurRadius = 0, ShadowDepth = 4.2, Direction = 315, Color = Ink, Opacity = 0.35 },
            Children = { _bubble, tailHost },
        };
        _bubble.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (_bubbleOpensChat) _host.ShowChat("chat");
            else if (!_bubbleSticky) HideBubble();
        };

        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 0, 0, 6) };
        stack.Children.Add(_bubbleWrap);
        stack.Children.Add(_spriteArea);
        _fx = new Canvas { IsHitTestVisible = false, ClipToBounds = false };
        _stage = new Grid { Children = { stack, _fx } };
        Content = _stage;

        // ------------------------------------------------------------ menu
        _menu = new ContextMenu();
        AddMenu("Открыть чат", () => _host.ShowChat("chat"));
        AddMenu("Поговорить голосом", () => _host.Voice.StartListening());
        AddMenu("Что у меня на экране?", () => _host.AskAboutScreen());
        _menu.Items.Add(new Separator());
        AddMenu("Напоминания", () => _host.ShowChat("reminders"));
        AddMenu("Настройки", () => _host.ShowChat("settings"));
        _menu.Items.Add(new Separator());
        AddMenu("Спрятать Веру", () => _host.SetPetVisible(false));
        AddMenu("Выход", () => _host.Quit());

        // ------------------------------------------------------------ mouse
        _veraBox.MouseLeftButtonDown += OnSpriteDown;
        _veraBox.MouseMove += OnSpriteMove;
        _veraBox.MouseLeftButtonUp += OnSpriteUp;
        _veraBox.MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            Interaction();
            _menu.PlacementTarget = _veraBox;
            _menu.Placement = PlacementMode.MousePoint;
            _menu.IsOpen = true;
        };
        _veraBox.MouseEnter += (_, _) => _hoverSince = DateTime.Now;
        _veraBox.MouseLeave += (_, _) => _hoverSince = DateTime.MinValue;

        // ------------------------------------------------------------ timers
        _frameTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        _frameTimer.Tick += (_, _) => DrawFrame(false);
        _lifeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        _lifeTimer.Tick += (_, _) => LifeTick();
        _bubbleTimer = new DispatcherTimer();
        _bubbleTimer.Tick += (_, _) => { _bubbleTimer.Stop(); HideBubble(); };
        _dotsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _dotsTimer.Tick += (_, _) =>
        {
            if (_dotsRun is null) { _dotsTimer.Stop(); return; }
            _dotsPhase = (_dotsPhase + 1) % 4;
            _dotsRun.Text = _dotsBase + new string('.', _dotsPhase);
        };
        _clickTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(260) };
        _clickTimer.Tick += (_, _) =>
        {
            _clickTimer.Stop();
            _host.PetClicked();
        };

        // ------------------------------------------------------------ voice
        var voice = _host.Voice;
        voice.StateChanged += OnVoiceState;
        voice.MicLevel += OnMicLevel;
        voice.Problem += (head, text) =>
        {
            _mode = PetMode.Idle;
            SetEmotion("sad", 6000);
            ShowText(text, head: head, ms: 9000);
        };
        voice.NotHeard += () =>
        {
            _mode = PetMode.Idle;
            ShowText(Core.Line("notHeard"), ms: 3500);
        };
        voice.Interaction += Interaction;

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            // не показываться в Alt+Tab
            Native.AddExStyle(_hwnd, Native.WS_EX_TOOLWINDOW, Native.WS_EX_APPWINDOW);
        };
        Loaded += (_, _) =>
        {
            DrawFrame(true);
            _frameTimer.Start();
            _lifeTimer.Start();
            _ = BlinkLoopAsync();
        };
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible && !_greeted)
            {
                _greeted = true;
                _ = GreetAsync();
            }
        };
        DrawFrame(true);
    }

    private VeraCore Core => _host.Core;
    private VeraSettings S => _host.Core.Settings.Current;

    private void AddMenu(string header, Action click)
    {
        var mi = new MenuItem { Header = header };
        mi.Click += (_, _) => click();
        _menu.Items.Add(mi);
    }

    // ================================================================== placement & size

    /// <summary>Создать окно, выставить размер и позицию (до первого показа).</summary>
    public void Prepare()
    {
        _hwnd = new WindowInteropHelper(this).EnsureHandle();
        ApplySize(false);
        var r = Native.WindowRect(_hwnd);
        int x, y;
        if (S.Pet.X is { } px && S.Pet.Y is { } py && !double.IsNaN(px) && !double.IsNaN(py))
        {
            x = (int)Math.Round(px);
            y = (int)Math.Round(py);
        }
        else
        {
            var wa = Native.PrimaryWorkArea();
            x = wa.Right - r.Width - (int)(20 * _dpi);
            y = wa.Bottom - r.Height;
        }
        Native.MoveWindow(_hwnd, x, y);
        ClampToScreen(false);
    }

    /// <summary>Масштаб и «поверх всех окон» из настроек.</summary>
    public void ApplySettings()
    {
        Topmost = S.Pet.AlwaysOnTop;
        ApplySize(true);
    }

    private void ApplySize(bool keepAnchor)
    {
        var s = Math.Clamp(S.Pet.Scale, 1, 8);
        if (_hwnd != IntPtr.Zero) _dpi = Native.DpiScale(_hwnd);
        // целое число физических пикселей на пиксель спрайта — пиксель-арт без «рваных» пикселей
        var phys = Math.Max(1, Math.Round(s * _dpi, MidpointRounding.AwayFromZero));
        var scale = phys / _dpi;
        var sizeChanged = Math.Abs(scale - _scale) > 0.001 || !keepAnchor;
        _scale = scale;

        _skin = Skins.Get(S.Skin);
        var pixel = _skin.PixelArt;
        RenderOptions.SetBitmapScalingMode(_sprite, pixel ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
        RenderOptions.SetEdgeMode(_sprite, pixel ? EdgeMode.Aliased : EdgeMode.Unspecified);
        _veraBox.Width = _sprite.Width = 64 * _scale;
        _veraBox.Height = _sprite.Height = 72 * _scale;
        _frameKey = "";
        _spriteArea.Height = 74 * _scale;
        _shadow.Width = 36 * _scale;
        _shadow.Height = 5 * _scale;
        StartBreathing();

        var w = Math.Max(340, 64 * _scale + 60);
        var h = 72 * _scale + BubbleSpace + 12;
        _bubbleWrap.MaxWidth = w - 24;
        if (!sizeChanged && Math.Abs(Width - w) < 0.5 && Math.Abs(Height - h) < 0.5) return;

        var old = _hwnd != IntPtr.Zero ? Native.WindowRect(_hwnd) : default;
        Width = w;
        Height = h;
        if (keepAnchor && _hwnd != IntPtr.Zero && old.Width > 0)
        {
            // якорь — нижний центр персонажа
            var nw = (int)Math.Round(w * _dpi);
            var nh = (int)Math.Round(h * _dpi);
            Native.MoveWindow(_hwnd, (old.Left + old.Right) / 2 - nw / 2, old.Bottom - nh);
            ClampToScreen(false);
        }
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        Dispatcher.BeginInvoke(() => ApplySize(true), DispatcherPriority.Background);
    }

    /// <summary>Не дать уехать за край экрана (часть пустого окна может выходить — как в Electron).</summary>
    public void ClampToScreen(bool save)
    {
        if (_hwnd == IntPtr.Zero) return;
        var r = Native.WindowRect(_hwnd);
        var w = r.Width;
        var h = r.Height;
        var wa = Native.WorkAreaAt(r.Left + w / 2, r.Top + h / 2);
        var margin = (int)(40 * _dpi);
        var bubble = (int)(BubbleSpace * _dpi);
        var minX = wa.Left - w / 2 + margin;
        var maxX = Math.Max(minX, wa.Right - w / 2 - margin);
        var minY = wa.Top - bubble;
        var maxY = Math.Max(minY, wa.Bottom - h);
        var nx = Math.Clamp(r.Left, minX, maxX);
        var ny = Math.Clamp(r.Top, minY, maxY);
        if (nx != r.Left || ny != r.Top) Native.MoveWindow(_hwnd, nx, ny);
        if (save) Core.Settings.Update(st => { st.Pet.X = nx; st.Pet.Y = ny; });
    }

    // ================================================================== animation

    private void StartBreathing()
    {
        var a = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(1.6), RepeatBehavior = RepeatBehavior.Forever };
        a.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        a.KeyFrames.Add(new DiscreteDoubleKeyFrame(_scale, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.6 * 0.56))));
        _breathe.BeginAnimation(TranslateTransform.YProperty, _dragging ? null : a);
    }

    private void Hop()
    {
        var s = _scale;
        var a = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(0.42), FillBehavior = FillBehavior.Stop };
        var up = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        var down = new QuadraticEase { EasingMode = EasingMode.EaseIn };
        a.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(0)));
        a.KeyFrames.Add(new EasingDoubleKeyFrame(-6 * s, KeyTime.FromPercent(0.35), up));
        a.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(0.70), down));
        a.KeyFrames.Add(new EasingDoubleKeyFrame(-1 * s, KeyTime.FromPercent(0.85), up));
        a.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(1.0), down));
        _hop.BeginAnimation(TranslateTransform.YProperty, a);
    }

    private void Shake()
    {
        var s = _scale;
        var a = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(0.8), FillBehavior = FillBehavior.Stop };
        for (var i = 0; i < 8; i++)
            a.KeyFrames.Add(new DiscreteDoubleKeyFrame(i % 2 == 0 ? -s : s, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(i * 0.1))));
        a.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.79))));
        _shake.BeginAnimation(TranslateTransform.XProperty, a);
    }

    private void SetDragVisual(bool on)
    {
        _tilt.Angle = on ? -4 : 0;
        _lift.Y = on ? -4 : 0;
        StartBreathing();
    }

    private async Task BlinkLoopAsync()
    {
        while (true)
        {
            await Task.Delay(2200 + Rnd.Next(4200));
            _blink = true;
            await Task.Delay(120);
            _blink = false;
            if (Rnd.NextDouble() < 0.18) // двойное моргание
            {
                await Task.Delay(160);
                _blink = true;
                await Task.Delay(110);
                _blink = false;
            }
        }
    }

    private void SetEmotion(string e, int ms = 9000)
    {
        if (!Personas.Emotions.Contains(e)) return;
        _emotion = e;
        _emotionUntil = DateTime.Now.AddMilliseconds(ms);
    }

    private void DrawFrame(bool force)
    {
        var now = DateTime.Now;
        var emo = now < _emotionUntil ? _emotion : "neutral";
        var speaker = _host.Voice.Speaker;
        var level = speaker.Level;
        var flap = now < _textFlapUntil;
        var toggle = ((Environment.TickCount64 >> 7) & 1) == 0;
        var mouthOpen = level > 0.18f ? (level > 0.35f || toggle) : (flap && toggle);
        var mode = _mode;
        if (mode is PetMode.Idle or PetMode.Talking)
            mode = level > 0.18f || flap || speaker.Active ? PetMode.Talking : PetMode.Idle;
        _posture = Bored(now, speaker.Active) ? Posture.Floor : Posture.Stand;
        if (_skin.Kind == SkinKind.Frames)
        {
            DrawFrameSkin(emo, mode, mouthOpen, force);
            return;
        }
        var frame = VeraSprite.Compose(emo, mode, _blink, mouthOpen, _dragging, _posture);
        if (!force && frame.Key == _frameKey) return;
        _frameKey = frame.Key;
        _sprite.Width = 64 * _scale;
        _sprite.Height = 72 * _scale;
        _talkScale.ScaleY = 1;
        _sprite.Source = _sheet.Compose(frame);
    }

    /// <summary>Заскучала: давно не трогали и ничего не происходит — садится на пол.</summary>
    private bool Bored(DateTime now, bool speaking) =>
        S.Pet.SitWhenBored && !_dragging && !speaking && _turns.Count == 0 &&
        _mode is PetMode.Idle or PetMode.Sleeping &&
        now - _lastInteraction > TimeSpan.FromMinutes(Math.Max(1, S.Pet.SitAfterMinutes));

    /// <summary>Покадровый скин (рисунок пользователя): готовая поза по эмоции, плавное масштабирование.</summary>
    private void DrawFrameSkin(string emo, PetMode mode, bool mouthOpen, bool force)
    {
        var pose = Skins.HaloPose(emo, mode, _posture, _dragging);
        var talking = mode == PetMode.Talking && mouthOpen;
        var key = _skin.Id + "|" + pose.Name + (talking ? "|t" : "");
        if (!force && key == _frameKey) return;
        _frameKey = key;
        _talkScale.ScaleY = talking ? 1.025 : 1;
        if (!_haloFrames.TryGetValue(pose.Name, out var img))
            _haloFrames[pose.Name] = img = SpriteSheet.LoadImage($"/{_skin.Folder}/{pose.Name}.png");
        if (img is null) return;
        // высота позы относительно стоящей; стоя — почти во весь рост пиксельной Веры
        var h = 70 * _scale * pose.Height;
        var w = h * img.PixelWidth / Math.Max(1, img.PixelHeight);
        if (w > 64 * _scale) { h *= 64 * _scale / w; w = 64 * _scale; }
        _sprite.Width = w;
        _sprite.Height = h;
        _sprite.Source = img;
    }

    private void Fx(string text, FxKind kind, double dx, double dy)
    {
        try
        {
            var s = _scale;
            var size = (kind == FxKind.Alert ? 10 : 7) * s;
            var color = kind switch
            {
                FxKind.Zzz => Color.FromRgb(0xc9, 0xc3, 0xff),
                FxKind.Alert => Color.FromRgb(0xff, 0xd2, 0x3f),
                FxKind.Heart => Color.FromRgb(0xff, 0x6f, 0xa8),
                _ => Colors.White,
            };
            var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI, Segoe UI Symbol, Segoe UI Emoji"), FontStyles.Normal, FontWeights.Black, FontStretches.Normal),
                size, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            var geo = ft.BuildGeometry(new Point(0, 0));
            var path = new Path
            {
                Data = geo,
                Fill = new SolidColorBrush(color),
                Stroke = new SolidColorBrush(Ink),
                StrokeThickness = 1.5,
                StrokeLineJoin = PenLineJoin.Round,
                RenderTransformOrigin = new Point(0.5, 0.5),
                Opacity = 0,
            };
            var scaleT = new ScaleTransform(1, 1);
            var moveT = new TranslateTransform();
            path.RenderTransform = new TransformGroup { Children = { scaleT, moveT } };
            var ox = ActualWidth / 2;
            var oy = ActualHeight - 6 - 60 * s;
            Canvas.SetLeft(path, ox + dx * s);
            Canvas.SetTop(path, oy + dy * s);
            _fx.Children.Add(path);

            TimeSpan total;
            if (kind == FxKind.Alert)
            {
                total = TimeSpan.FromSeconds(1.2);
                var op = new DoubleAnimationUsingKeyFrames { Duration = total };
                op.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(0)));
                op.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromPercent(0.2)));
                op.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromPercent(0.85)));
                op.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(1)));
                var sc = new DoubleAnimationUsingKeyFrames { Duration = total };
                sc.KeyFrames.Add(new LinearDoubleKeyFrame(0.3, KeyTime.FromPercent(0)));
                sc.KeyFrames.Add(new LinearDoubleKeyFrame(1.25, KeyTime.FromPercent(0.2)));
                sc.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromPercent(0.4)));
                path.BeginAnimation(OpacityProperty, op);
                scaleT.BeginAnimation(ScaleTransform.ScaleXProperty, sc);
                scaleT.BeginAnimation(ScaleTransform.ScaleYProperty, sc);
            }
            else
            {
                total = TimeSpan.FromSeconds(1.8);
                var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
                var op = new DoubleAnimationUsingKeyFrames { Duration = total };
                op.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(0)));
                op.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromPercent(0.15)));
                op.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(1)));
                path.BeginAnimation(OpacityProperty, op);
                var sc = new DoubleAnimation(0.6, 1.1, total) { EasingFunction = ease };
                scaleT.BeginAnimation(ScaleTransform.ScaleXProperty, sc);
                scaleT.BeginAnimation(ScaleTransform.ScaleYProperty, sc);
                moveT.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, 6 * s, total) { EasingFunction = ease });
                moveT.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, -16 * s, total) { EasingFunction = ease });
            }
            var cleanup = new DispatcherTimer { Interval = total + TimeSpan.FromMilliseconds(200) };
            cleanup.Tick += (_, _) =>
            {
                cleanup.Stop();
                _fx.Children.Remove(path);
            };
            cleanup.Start();
        }
        catch (Exception)
        {
            // эффекты — украшение, ошибка не критична
        }
    }

    // ================================================================== bubble

    private sealed record BubbleAction(string Label, bool Primary, Action Click);

    private void ShowBubble(Action<InlineCollection> fill, string head = "", bool sticky = false, int? ms = null,
        bool opensChat = false, IReadOnlyList<BubbleAction>? actions = null)
    {
        _bubbleTimer.Stop();
        _bubbleGen++;
        _dotsRun = null;
        _dotsTimer.Stop();
        _bubbleHead.Text = head.ToUpperInvariant();
        _bubbleHead.Visibility = string.IsNullOrEmpty(head) ? Visibility.Collapsed : Visibility.Visible;
        _bubbleText.Inlines.Clear();
        fill(_bubbleText.Inlines);
        _bubbleTextLen = _bubbleText.Inlines.OfType<Run>().Sum(r => r.Text.Length);
        _bubbleText.Visibility = _bubbleText.Inlines.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _actions.Children.Clear();
        foreach (var a in actions ?? Array.Empty<BubbleAction>())
        {
            var b = new Button
            {
                Content = a.Label,
                Style = Ui.Style(a.Primary ? "Pet.ButtonPrimary" : "Pet.Button"),
                Margin = new Thickness(3),
            };
            var click = a.Click;
            b.Click += (_, e) => { e.Handled = true; click(); };
            _actions.Children.Add(b);
        }
        _actions.Visibility = _actions.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _bubbleSticky = sticky;
        _bubbleOpensChat = opensChat;
        _bubble.Cursor = opensChat ? Cursors.Hand : Cursors.Arrow;

        if (!_bubbleVisible)
        {
            _bubbleVisible = true;
            _bubbleWrap.Visibility = Visibility.Visible;
            _bubbleWrap.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)));
            _bubbleShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(6, 0, TimeSpan.FromMilliseconds(180)));
        }
        if (!sticky) HideBubbleLater(ms);
    }

    private void ShowText(string text, string head = "", bool sticky = false, int? ms = null, bool opensChat = false,
        IReadOnlyList<BubbleAction>? actions = null) =>
        ShowBubble(inl => { if (!string.IsNullOrEmpty(text)) inl.Add(new Run(text)); }, head, sticky, ms, opensChat, actions);

    private Run DotsRun(string baseText)
    {
        var r = new Run(baseText);
        _dotsBase = baseText;
        _dotsPhase = 0;
        return r;
    }

    private void ShowDots(string baseText, string head = "")
    {
        Run? dots = null;
        ShowBubble(inl => { dots = DotsRun(baseText); inl.Add(dots); }, head, sticky: true);
        StartDots(dots);
    }

    private void StartDots(Run? run)
    {
        _dotsRun = run;
        if (run is not null) _dotsTimer.Start();
    }

    private void HideBubbleLater(int? ms = null)
    {
        _bubbleTimer.Stop();
        var baseMs = Math.Max(3, S.Pet.BubbleSeconds) * 1000;
        var wait = ms ?? Math.Min(baseMs + _bubbleTextLen * 45, 40000);
        _bubbleTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(500, wait));
        _bubbleTimer.Start();
    }

    private void HideBubble()
    {
        _bubbleTimer.Stop();
        _bubbleSticky = false;
        _levels.Visibility = Visibility.Collapsed;
        _dotsRun = null;
        _dotsTimer.Stop();
        if (!_bubbleVisible) return;
        _bubbleVisible = false;
        var gen = _bubbleGen;
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(180));
        fade.Completed += (_, _) =>
        {
            if (!_bubbleVisible && gen == _bubbleGen) _bubbleWrap.Visibility = Visibility.Collapsed;
        };
        _bubbleWrap.BeginAnimation(OpacityProperty, fade);
        _bubbleShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, 6, TimeSpan.FromMilliseconds(180)));
    }

    [GeneratedRegex(@"```[\s\S]*?```")] private static partial Regex FencedCode();
    [GeneratedRegex(@"```[\s\S]*$")] private static partial Regex OpenFence();
    [GeneratedRegex(@"\*\*|__|`")] private static partial Regex Marks();
    [GeneratedRegex(@"^#+\s*", RegexOptions.Multiline)] private static partial Regex Hashes();
    [GeneratedRegex(@"\s+\S*$")] private static partial Regex LastWord();

    private static (string Text, bool Cut) PlainPreview(string text, int max = 230)
    {
        var t = FencedCode().Replace(text ?? "", "[код]");
        t = OpenFence().Replace(t, "[код]");
        t = Marks().Replace(t, "");
        t = Hashes().Replace(t, "").Trim();
        var cut = false;
        if (t.Length > max)
        {
            t = LastWord().Replace(t[..max], "");
            cut = true;
        }
        return (t, cut);
    }

    private static string ToolHuman(string name) => name switch
    {
        "create_reminder" => "ставлю напоминание",
        "list_reminders" => "смотрю напоминания",
        "delete_reminder" => "удаляю напоминание",
        "update_reminder" => "меняю напоминание",
        "remember" => "запоминаю",
        "forget" => "забываю",
        "get_weather" => "смотрю погоду",
        "get_system_info" => "смотрю ПК",
        "open_url" => "открываю ссылку",
        "search_web" => "ищу",
        "open_app" => "запускаю приложение",
        "open_folder" => "открываю папку",
        "media_control" => "управляю звуком",
        "system_action" => "выполняю",
        "clipboard_read" => "читаю буфер",
        "clipboard_write" => "копирую",
        "take_screenshot" => "смотрю на экран",
        "run_command" => "выполняю команду",
        _ => name,
    };

    private void RenderTurnBubble(TurnView t, bool streaming)
    {
        var (text, cut) = PlainPreview(t.Text.ToString());
        Run? dots = null;
        ShowBubble(inl =>
        {
            foreach (var tool in t.Tools.TakeLast(3))
            {
                var label = tool.Running ? $"⚙️ {ToolHuman(tool.Name)}…" : (string.IsNullOrEmpty(tool.Label) ? tool.Name : tool.Label);
                inl.Add(new Run(label) { FontSize = 12, Foreground = new SolidColorBrush(tool.IsError ? Color.FromRgb(0xd6, 0x45, 0x45) : PetMuted) });
                inl.Add(new LineBreak());
            }
            if (text.Length > 0)
            {
                inl.Add(new Run(text));
                if (cut) inl.Add(new Run(" …(в чате)") { Foreground = new SolidColorBrush(PetAccent), FontWeight = FontWeights.SemiBold });
            }
            else if (streaming)
            {
                dots = DotsRun("хм");
                inl.Add(dots);
            }
            if (inl.LastInline is LineBreak lb) inl.Remove(lb);
        }, sticky: streaming, opensChat: cut || !streaming);
        StartDots(dots);
        if (!streaming) HideBubbleLater();
    }

    // ================================================================== public API

    /// <summary>Пользователь что-то сделал — Вера просыпается, таймер сна сбрасывается.</summary>
    public void Interaction()
    {
        _lastInteraction = DateTime.Now;
        if (_mode == PetMode.Sleeping) _mode = PetMode.Idle;
    }

    /// <summary>Сказать что-то в пузыре (без голоса).</summary>
    public void Say(string text, string emotion = "neutral", int? ms = null, string head = "", bool opensChat = false)
    {
        Interaction();
        // распознавание закончилось, но отправить не вышло — выходим из позы «слушаю/думаю»
        if (_mode is PetMode.Listening or PetMode.Recording or PetMode.Processing && _turns.Count == 0) _mode = PetMode.Idle;
        _levels.Visibility = Visibility.Collapsed;
        SetEmotion(emotion, 6000);
        ShowText(text, head: head, ms: ms, opensChat: opensChat);
    }

    /// <summary>Тест голоса из настроек: пузырь + озвучка.</summary>
    public void SpeakTest(string text)
    {
        Interaction();
        SetEmotion("smug", 6000);
        ShowText(text, ms: 6000);
        _host.Voice.Say(text, resetErrors: true);
    }

    private async Task GreetAsync()
    {
        await Task.Delay(900);
        try
        {
            var (text, noKey) = Core.Greeting();
            SetEmotion(noKey ? "angry" : "smug", 7000);
            Hop();
            ShowText(text, ms: 9000, opensChat: noKey);
        }
        catch (Exception) { }
    }

    public void OnAgentEvent(AgentEvent e)
    {
        Interaction();
        switch (e)
        {
            case TurnStarted ts:
            {
                _turns[ts.TurnId] = new TurnView { Speak = _host.Voice.ShouldSpeak(ts.Source) };
                _mode = PetMode.Thinking;
                ShowDots("хм");
                break;
            }
            case EmotionChanged em:
                SetEmotion(em.Emotion, 20000);
                if (em.Emotion is "happy" or "laugh") Hop();
                if (em.Emotion == "angry") Shake();
                if (em.Emotion == "shy") Fx("♥", FxKind.Heart, 8, -2);
                if (em.Emotion == "surprised") Fx("!", FxKind.Alert, 12, -4);
                break;
            case TextDelta d:
            {
                if (!_turns.TryGetValue(d.TurnId, out var t)) return;
                if (_mode == PetMode.Thinking) _mode = PetMode.Talking;
                if (t.LastStep >= 0 && t.LastStep != d.Step && t.Text.Length > 0)
                {
                    var trimmed = t.Text.ToString().TrimEnd();
                    t.Text.Clear().Append(trimmed).Append('\n');
                }
                if (!t.StepStart.ContainsKey(d.Step)) t.StepStart[d.Step] = t.Text.Length;
                t.LastStep = d.Step;
                t.Text.Append(d.Delta);
                if (!t.Speak) _textFlapUntil = DateTime.Now.AddMilliseconds(260);
                RenderTurnBubble(t, true);
                break;
            }
            case TextReplaced tr:
            {
                // вызов команды JSON-ом выполнен — убираем его из пузыря
                if (!_turns.TryGetValue(tr.TurnId, out var t)) return;
                var start = t.StepStart.TryGetValue(tr.Step, out var st) ? Math.Min(st, t.Text.Length) : t.Text.Length;
                t.Text.Length = start;
                t.Text.Append(tr.Text);
                t.StepStart[tr.Step] = start;
                t.LastStep = tr.Step;
                RenderTurnBubble(t, true);
                break;
            }
            case ToolActivity ta:
            {
                if (!_turns.TryGetValue(ta.TurnId, out var t)) return;
                var tool = t.Tools.FirstOrDefault(x => x.Id == ta.CallId);
                if (tool is null) t.Tools.Add(tool = new ToolView { Id = ta.CallId });
                tool.Name = ta.Name;
                tool.Running = ta.Running;
                tool.Label = ta.Label;
                tool.IsError = ta.IsError;
                if (ta.Running && _mode != PetMode.Talking) _mode = PetMode.Thinking;
                if (!ta.Running && ta.Kind == "reminder")
                {
                    Fx("⏰", FxKind.Alert, 14, -6);
                    Hop();
                }
                RenderTurnBubble(t, true);
                break;
            }
            case TurnEnded te:
            {
                _mode = PetMode.Idle;
                if (_turns.Remove(te.TurnId, out var t))
                {
                    if (t.Text.Length == 0 && !string.IsNullOrEmpty(te.Text)) t.Text.Append(te.Text);
                    RenderTurnBubble(t, false);
                }
                SetEmotion(string.IsNullOrEmpty(te.Emotion) ? "neutral" : te.Emotion, 12000);
                break;
            }
            case TurnFailed tf:
            {
                _turns.Remove(tf.TurnId, out var t);
                _mode = PetMode.Idle;
                if (tf.Aborted)
                {
                    if (t is { Text.Length: > 0 }) RenderTurnBubble(t, false);
                    else HideBubble();
                    return;
                }
                SetEmotion("sad", 8000);
                Sfx.Error();
                ShowText(tf.Message, head: "ой…", ms: 12000, opensChat: true);
                break;
            }
        }
    }

    public void OnReminderDue(Reminder r, bool missed, string phrase)
    {
        Interaction();
        _mode = PetMode.Idle;
        var s = S;
        if (s.Reminders.Sound) Sfx.Reminder();
        SetEmotion(missed ? "surprised" : "happy", 15000);
        Fx("!", FxKind.Alert, 12, -4);
        Hop();
        var id = r.Id;
        ShowText(phrase, head: missed ? "⏰ пропущенное" : "⏰ напоминание", sticky: true, actions: new[]
        {
            new BubbleAction("Готово ✓", true, () =>
            {
                try { Core.Reminders.Complete(id); } catch (Exception) { }
                SetEmotion("smug", 6000);
                HideBubble();
            }),
            new BubbleAction("+10 мин", false, () =>
            {
                try { Core.Reminders.Snooze(id, 10); } catch (Exception) { }
                HideBubble();
            }),
            new BubbleAction("+1 час", false, () =>
            {
                try { Core.Reminders.Snooze(id, 60); } catch (Exception) { }
                HideBubble();
            }),
        });
        var gen = _bubbleGen;
        _ = Task.Delay(TimeSpan.FromMinutes(2)).ContinueWith(_ =>
        {
            if (gen == _bubbleGen && _bubbleSticky) HideBubbleLater(15000);
        }, TaskScheduler.FromCurrentSynchronizationContext());
        if (s.Reminders.Speak && s.Tts.Engine != "off")
        {
            _host.Voice.StopSpeaking();
            _ = Task.Delay(s.Reminders.Sound ? 900 : 0).ContinueWith(_ => _host.Voice.Say(phrase),
                TaskScheduler.FromCurrentSynchronizationContext());
        }
    }

    // ================================================================== voice UI

    private void OnVoiceState(RecorderState st, string? reason)
    {
        Interaction();
        switch (st)
        {
            case RecorderState.Listening:
                _mode = PetMode.Listening;
                ShowText(Core.Line("listening"), head: "🎙️ слушаю", sticky: true);
                _levels.Visibility = Visibility.Visible;
                break;
            case RecorderState.Recording:
                _mode = PetMode.Recording;
                _levels.Visibility = Visibility.Visible;
                break;
            case RecorderState.Processing:
                _mode = PetMode.Processing;
                _levels.Visibility = Visibility.Collapsed;
                ShowDots("распознаю");
                break;
            case RecorderState.Off:
            case RecorderState.Wake:
                if (reason is null) break;
                _mode = PetMode.Idle;
                _levels.Visibility = Visibility.Collapsed;
                if (reason is "timeout" or "short") ShowText(Core.Line("notHeard"), ms: 3500);
                else HideBubble();
                break;
        }
    }

    private void OnMicLevel(float v)
    {
        if (_levels.Visibility != Visibility.Visible) return;
        var t = Environment.TickCount64 / 90.0;
        for (var i = 0; i < _bars.Length; i++)
        {
            var k = Math.Max(0, v * 18 * (0.6 + 0.4 * Math.Sin(i * 1.7 + t)));
            _bars[i].Height = 3 + Math.Min(16, k);
        }
    }

    // ================================================================== mouse

    private void OnSpriteDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        Interaction();
        _pressPoint = PointToScreen(e.GetPosition(this));
    }

    private void OnSpriteMove(object sender, MouseEventArgs e)
    {
        if (_pressPoint is { } p && e.LeftButton == MouseButtonState.Pressed && !_dragging)
        {
            var cur = PointToScreen(e.GetPosition(this));
            if ((cur - p).Length > 4 * _dpi) BeginDrag();
            return;
        }
        if (_hoverSince != DateTime.MinValue && DateTime.Now - _hoverSince > TimeSpan.FromSeconds(1.5) &&
            _mode == PetMode.Idle && DateTime.Now > _emotionUntil && Rnd.NextDouble() < 0.02)
            SetEmotion("smug", 3000);
    }

    private void BeginDrag()
    {
        _pressPoint = null;
        _clickTimer.Stop();
        _dragging = true;
        SetDragVisual(true);
        HideBubble();
        ShowText(Core.Line("drag"), ms: 2500);
        DrawFrame(true);
        try
        {
            DragMove(); // модальный цикл Windows до отпускания кнопки
        }
        catch (InvalidOperationException)
        {
            // кнопку уже отпустили
        }
        _dragging = false;
        SetDragVisual(false);
        ClampToScreen(true);
        SetEmotion("angry", 2500);
        DrawFrame(true);
    }

    private void OnSpriteUp(object sender, MouseButtonEventArgs e)
    {
        if (_pressPoint is null) return;
        _pressPoint = null;
        e.Handled = true;
        if (_clickTimer.IsEnabled)
        {
            _clickTimer.Stop();
            Poke();
            return;
        }
        _clickTimer.Start();
    }

    private void Poke()
    {
        _pokes++;
        Hop();
        var emo = _pokes % 4 == 0 ? "angry" : (Rnd.NextDouble() < 0.5 ? "laugh" : "smug");
        SetEmotion(emo, 4000);
        if (emo == "angry") Shake();
        ShowText(Core.Line("poke"), ms: 3500);
        _ = Task.Delay(8000).ContinueWith(_ => _pokes = Math.Max(0, _pokes - 1), TaskScheduler.FromCurrentSynchronizationContext());
    }

    // ================================================================== idle life

    private void LifeTick()
    {
        var now = DateTime.Now;
        var idleFor = now - _lastInteraction;
        var speaking = _host.Voice.Speaker.Active;
        if (_mode == PetMode.Idle && !speaking && now.Hour >= 1 && now.Hour < 6 && idleFor > TimeSpan.FromMinutes(15))
            _mode = PetMode.Sleeping;
        if (_mode == PetMode.Sleeping) Fx("z", FxKind.Zzz, 14 + Rnd.NextDouble() * 4, -2);
        if (S.Pet.IdleChatter && _mode == PetMode.Idle && now > _nextIdleChat && _turns.Count == 0 && !_bubbleVisible && !speaking)
        {
            _nextIdleChat = now.AddMinutes(25 + Rnd.NextDouble() * 25);
            SetEmotion("smug", 6000);
            ShowText(Core.Line("idle"));
        }
    }

    /// <summary>Есть ли незавершённый ход (для голосовой активации).</summary>
    public bool HasActiveTurn => _turns.Count > 0;
}
