using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Vera.Core;
using Vera.Core.Speech;
using Vera.Core.Sprites;
using Vera.Desktop.UI;

namespace Vera.Desktop.Chat;

/// <summary>
/// Голосовой режим (концепт «Ореол»): Вера в центре с нимбом, волна микрофона, текст по ходу речи, ответ пузырём.
/// Лежит поверх окна чата; вход и выход — с анимацией по настройке.
/// </summary>
public sealed class VoiceModeView : Grid
{
    private readonly AppHost _host;
    private readonly Image _sprite = new() { Height = 270, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Bottom };
    private readonly Grid _stage;
    private readonly ScaleTransform _stageScale = new(1, 1);
    private readonly TranslateTransform _stageMove = new();
    private readonly Ellipse _halo;
    private readonly ScaleTransform _haloScale = new(1, 1);
    private readonly ScaleTransform _haloPulse = new(1, 1);
    private readonly Ellipse[] _rings;
    private readonly Rectangle[] _bars = new Rectangle[17];
    private readonly TextBlock _phase;
    private readonly TextBlock _transcript;
    private readonly Run _heard = new();
    private readonly Run _partial = new();
    private readonly Border _caret;
    private readonly TextBlock _hint;
    private readonly Border _answer;
    private readonly TextBlock _answerText;
    private readonly StackPanel _history;
    private readonly Grid _center;
    private readonly DockPanel _footer;
    private readonly Button _micBtn;
    private readonly DispatcherTimer _pulse;
    private VoicePhase _phaseNow = VoicePhase.Listening;
    private string _emotion = "neutral";
    private double _level;
    private double _pulseT;

    public VoiceModeView(AppHost host, Action exit, Action mic)
    {
        _host = host;
        this.Res(BackgroundProperty, "Bg");
        Visibility = Visibility.Collapsed;
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // ------------------------------------------------------------ шапка
        var dot = new Ellipse { Width = 8, Height = 8, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        dot.SetResourceReference(Shape.FillProperty, "OnAccent");
        var chipText = Ui.Text("Голосовой режим", 13, "OnAccent", FontWeights.Bold, wrap: false);
        var chip = new Border
        {
            CornerRadius = new CornerRadius(999),
            BorderThickness = new Thickness(2),
            BorderBrush = Brushes.Black,
            Padding = new Thickness(12, 5, 12, 5),
            Effect = Anim.HardShadow(2),
            Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { dot, chipText } },
            VerticalAlignment = VerticalAlignment.Center,
        };
        chip.SetResourceReference(Border.BackgroundProperty, "Accent");
        var engine = Ui.Muted(EngineText(), 13);
        engine.VerticalAlignment = VerticalAlignment.Center;
        engine.Margin = new Thickness(12, 0, 0, 0);
        var esc = Ui.Muted("Esc — вернуться в чат", 12.5);
        esc.VerticalAlignment = VerticalAlignment.Center;
        var header = new DockPanel { Margin = new Thickness(22, 16, 22, 0), LastChildFill = false };
        header.Children.Add(chip);
        header.Children.Add(engine);
        DockPanel.SetDock(esc, Dock.Right);
        header.Children.Add(esc);
        Children.Add(header);
        _host.Core.Settings.Changed += _ => Dispatcher.BeginInvoke(() => engine.Text = EngineText());

        // прошлая реплика — бледно слева сверху
        _history = new StackPanel { Width = 260, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(28, 18, 0, 0), Opacity = 0.45, IsHitTestVisible = false };
        SetRow(_history, 1);
        Children.Add(_history);

        // ------------------------------------------------------------ центр: Вера, нимб, волна, текст
        _rings = new[] { Ring(400), Ring(300) };
        var disk = new Ellipse { Width = 210, Height = 210, StrokeThickness = 2.5, Stroke = Brushes.Black };
        disk.SetResourceReference(Shape.FillProperty, "Bg2");
        // кольца шире сцены — Canvas их не обрезает
        var back = new Canvas { Width = 420, Height = 300, IsHitTestVisible = false };
        foreach (var e in _rings.Append(disk))
        {
            Canvas.SetLeft(e, 210 - e.Width / 2);
            Canvas.SetTop(e, 160 - e.Height / 2);
            back.Children.Add(e);
        }
        _halo = new Ellipse
        {
            Width = 120, Height = 26, StrokeThickness = 4,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 22, 0, 0),
            RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new TransformGroup { Children = { _haloScale, _haloPulse } },
        };
        _halo.SetResourceReference(Shape.StrokeProperty, "Accent");
        var ringsBox = new Grid { Width = 420, Height = 300, ClipToBounds = false };
        ringsBox.Children.Add(back);
        ringsBox.Children.Add(_halo);
        ringsBox.Children.Add(_sprite);
        _stage = new Grid
        {
            Width = 420, Height = 300,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new TransformGroup { Children = { _stageScale, _stageMove } },
            Children = { ringsBox },
        };

        var wave = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Height = 44, Margin = new Thickness(0, 18, 0, 0) };
        for (var i = 0; i < _bars.Length; i++)
        {
            var b = new Rectangle { Width = 5, Height = 6, RadiusX = 2.5, RadiusY = 2.5, Margin = new Thickness(2, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center };
            b.SetResourceReference(Shape.FillProperty, "Accent");
            _bars[i] = b;
            wave.Children.Add(b);
        }

        _phase = Ui.Heading("Слушаю…", 26);
        _phase.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        _phase.HorizontalAlignment = HorizontalAlignment.Center;
        _phase.Margin = new Thickness(0, 16, 0, 0);

        _partial.SetResourceReference(TextElement.ForegroundProperty, "Muted");
        _caret = new Border { Width = 3, Height = 26, Margin = new Thickness(4, 0, 0, -4) };
        _caret.SetResourceReference(Border.BackgroundProperty, "Accent");
        _transcript = new TextBlock { FontSize = 24, FontWeight = FontWeights.Medium, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, MaxWidth = 640, Margin = new Thickness(24, 10, 24, 0), HorizontalAlignment = HorizontalAlignment.Center };
        _transcript.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
        _transcript.Inlines.Add(_heard);
        _transcript.Inlines.Add(new Run(" "));
        _transcript.Inlines.Add(_partial);
        _transcript.Inlines.Add(new InlineUIContainer(_caret));

        _hint = Ui.Muted("Серым — слова, которые ещё уточняются. Замолчишь на полсекунды — Вера начнёт отвечать.", 13);
        _hint.TextAlignment = TextAlignment.Center;
        _hint.HorizontalAlignment = HorizontalAlignment.Center;
        _hint.Margin = new Thickness(24, 8, 24, 0);
        _hint.MaxWidth = 640;

        _answerText = new TextBlock { FontSize = 16, LineHeight = 23, TextWrapping = TextWrapping.Wrap };
        _answerText.SetResourceReference(TextBlock.ForegroundProperty, "VeraBubbleFg");
        _answer = new Border
        {
            CornerRadius = new CornerRadius(20, 20, 20, 6),
            BorderThickness = new Thickness(2.5),
            BorderBrush = Brushes.Black,
            Padding = new Thickness(16, 12, 16, 12),
            Effect = Anim.HardShadow(3),
            MaxWidth = 640,
            Margin = new Thickness(24, 14, 24, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = _answerText,
            Visibility = Visibility.Collapsed,
        };
        _answer.SetResourceReference(Border.BackgroundProperty, "Paper");

        var col = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        col.Children.Add(_stage);
        col.Children.Add(wave);
        col.Children.Add(_phase);
        col.Children.Add(_transcript);
        col.Children.Add(_hint);
        col.Children.Add(_answer);
        _center = new Grid { Children = { col } };
        SetRow(_center, 1);
        Children.Add(_center);

        // ------------------------------------------------------------ кнопки
        var keyboard = Ui.IconButton("keyboard", "Перейти к клавиатуре", exit, 52, 22);
        _micBtn = new Button
        {
            Width = 80, Height = 80,
            Style = Ui.Style("Btn.Base"),
            BorderThickness = new Thickness(3),
            BorderBrush = Brushes.Black,
            Effect = Anim.HardShadow(4),
            Content = Icons.Make("mic", 30),
            ToolTip = L10n.T("Говорить / перестать слушать"),
            Margin = new Thickness(18, 0, 18, 0),
        };
        Ui.SetRadius(_micBtn, new CornerRadius(40));
        _micBtn.SetResourceReference(Control.BackgroundProperty, "Accent");
        _micBtn.SetResourceReference(Control.ForegroundProperty, "OnAccent");
        _micBtn.Click += (_, _) => mic();
        var close = Ui.IconButton("x", "Выйти из голосового режима", exit, 52, 22);
        _footer = new DockPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 26), LastChildFill = false };
        _footer.Children.Add(keyboard);
        _footer.Children.Add(_micBtn);
        _footer.Children.Add(close);
        SetRow(_footer, 2);
        Children.Add(_footer);

        // нимб пульсирует, волна дышит
        _pulse = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        _pulse.Tick += (_, _) => Animate();
    }

    private static Ellipse Ring(double size)
    {
        var e = new Ellipse { Width = size, Height = size, StrokeThickness = 2 };
        e.SetResourceReference(Shape.StrokeProperty, "Line");
        return e;
    }

    private string EngineText()
    {
        var s = _host.Core.Settings.Current.Stt;
        return L10n.T(s.Provider switch
        {
            "stream" or "whisper" => "Распознавание на этом ПК · без интернета",
            "groq" => "Распознавание Groq",
            _ => "Распознавание OpenAI",
        });
    }

    public bool Showing => Visibility == Visibility.Visible;

    // ------------------------------------------------------------ вход и выход

    /// <summary>Показать с анимацией: Вера «вылетает» из шапки в центр, нимб раскрывается.</summary>
    public void Enter(string animation)
    {
        LoadHistory();
        _answer.Visibility = Visibility.Collapsed;
        _heard.Text = _partial.Text = "";
        _sprite.Source = SkinArt.Pose(_host.Core.Settings.Current.Skin, _emotion);
        RenderOptions.SetBitmapScalingMode(_sprite, SkinArt.IsPixel(_host.Core.Settings.Current.Skin) ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
        // сбросить следы прошлой анимации (выход оставляет сцену уменьшенной)
        foreach (var dp in new[] { ScaleTransform.ScaleXProperty, ScaleTransform.ScaleYProperty })
        {
            _stageScale.BeginAnimation(dp, null);
            _haloScale.BeginAnimation(dp, null);
        }
        _stageMove.BeginAnimation(TranslateTransform.XProperty, null);
        _stageMove.BeginAnimation(TranslateTransform.YProperty, null);
        BeginAnimation(OpacityProperty, null);
        _stageScale.ScaleX = _stageScale.ScaleY = _haloScale.ScaleX = 1;
        _stageMove.X = _stageMove.Y = 0;
        Visibility = Visibility.Visible;
        _pulse.Start();
        var ms = VoiceModeFlow.EnterMs(animation, Anim.Reduced);
        if (ms == 0)
        {
            Opacity = 1;
            return;
        }
        var d = TimeSpan.FromMilliseconds(ms);
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(Math.Max(120, ms / 3))));
        if (animation == "fast") return;
        // полная: сцена растёт из левого верхнего угла (там, где аватар в шапке), с лёгким перелётом
        var grow = new DoubleAnimation(0.28, 1, d) { EasingFunction = Anim.Back, BeginTime = TimeSpan.FromMilliseconds(ms / 4) };
        _stageScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        _stageScale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        _stageMove.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-ActualWidth * 0.32, 0, d) { EasingFunction = Anim.Out, BeginTime = grow.BeginTime });
        _stageMove.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-ActualHeight * 0.28, 0, d) { EasingFunction = Anim.Out, BeginTime = grow.BeginTime });
        var halo = new DoubleAnimation(0.3, 1, TimeSpan.FromMilliseconds(ms * 0.6)) { EasingFunction = Anim.Back, BeginTime = TimeSpan.FromMilliseconds(ms * 0.55) };
        _haloScale.BeginAnimation(ScaleTransform.ScaleXProperty, halo);
        foreach (var el in new UIElement[] { _phase, _transcript, _footer })
            el.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { BeginTime = TimeSpan.FromMilliseconds(ms * 0.75) });
    }

    /// <summary>Спрятать (обратная анимация вдвое быстрее), потом done.</summary>
    public void Leave(string animation, Action done)
    {
        _pulse.Stop();
        var ms = VoiceModeFlow.ExitMs(animation, Anim.Reduced);
        if (ms == 0)
        {
            Visibility = Visibility.Collapsed;
            done();
            return;
        }
        var d = TimeSpan.FromMilliseconds(ms);
        var fade = new DoubleAnimation(1, 0, d) { EasingFunction = Anim.Out };
        fade.Completed += (_, _) =>
        {
            Visibility = Visibility.Collapsed;
            BeginAnimation(OpacityProperty, null);
            Opacity = 1;
            done();
        };
        if (animation != "fast")
        {
            var shrink = new DoubleAnimation(1, 0.4, d) { EasingFunction = Anim.Out };
            _stageScale.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
            _stageScale.BeginAnimation(ScaleTransform.ScaleYProperty, shrink);
        }
        BeginAnimation(OpacityProperty, fade);
    }

    private void LoadHistory()
    {
        _history.Children.Clear();
        var h = _host.Core.Agent.GetVisibleHistory();
        var lastUser = h.LastOrDefault(m => m.Role == "user");
        var lastVera = h.LastOrDefault(m => m.Role == "assistant");
        if (lastUser is not null) _history.Children.Add(Mini(lastUser.Text, true));
        if (lastVera is not null) _history.Children.Add(Mini(lastVera.Text, false));
    }

    private static Border Mini(string text, bool user)
    {
        var t = text.Length > 90 ? text[..90] + "…" : text;
        var tb = new TextBlock { Text = t, FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
        tb.SetResourceReference(TextBlock.ForegroundProperty, user ? "OnAccent" : "VeraBubbleFg");
        var b = new Border
        {
            CornerRadius = user ? new CornerRadius(14, 14, 4, 14) : new CornerRadius(14, 14, 14, 4),
            BorderThickness = new Thickness(2),
            BorderBrush = Brushes.Black,
            Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(0, 0, 0, 8),
            HorizontalAlignment = user ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            Child = tb,
        };
        b.SetResourceReference(Border.BackgroundProperty, user ? "Accent" : "Paper");
        return b;
    }

    // ------------------------------------------------------------ состояние

    public void SetPhase(VoicePhase phase, string heard, string partial)
    {
        _phaseNow = phase;
        _phase.Text = L10n.T(phase switch
        {
            VoicePhase.Listening => "Слушаю…",
            VoicePhase.Hearing => "Слушаю…",
            VoicePhase.Thinking => "Думаю…",
            VoicePhase.Answering => "Отвечаю",
            _ => "Говори, я слушаю",
        });
        if (phase is VoicePhase.Listening or VoicePhase.Hearing)
        {
            if (phase == VoicePhase.Listening && partial.Length == 0) _heard.Text = "";
            _partial.Text = partial;
            _answer.Visibility = Visibility.Collapsed;
        }
        else
        {
            _heard.Text = heard;
            _partial.Text = "";
        }
        _caret.Visibility = phase is VoicePhase.Listening or VoicePhase.Hearing ? Visibility.Visible : Visibility.Collapsed;
        _hint.Visibility = phase is VoicePhase.Listening or VoicePhase.Hearing ? Visibility.Visible : Visibility.Collapsed;
        _micBtn.Content = Icons.Make(phase is VoicePhase.Thinking or VoicePhase.Answering ? "stop" : "mic", 30);
    }

    /// <summary>Текст ответа целиком (по мере печати).</summary>
    public void SetAnswer(string text)
    {
        _answerText.Text = text;
        _answer.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public void SetLevel(float level) => _level = Math.Max(_level * 0.6, level);

    public void SetEmotion(string emotion)
    {
        _emotion = string.IsNullOrEmpty(emotion) ? "neutral" : emotion;
        if (!Showing) return;
        var skin = _host.Core.Settings.Current.Skin;
        _sprite.Source = SkinArt.Pose(skin, _emotion, _phaseNow == VoicePhase.Answering ? PetMode.Talking : PetMode.Idle,
            _phaseNow == VoicePhase.Answering && _host.Voice.Speaker.Level > 0.2f);
    }

    private void Animate()
    {
        _pulseT += 0.033;
        var listening = _phaseNow is VoicePhase.Listening or VoicePhase.Hearing;
        // нимб: медленная пульсация, при речи — быстрее
        var pulse = 1 + (listening ? 0.06 : 0.03) * Math.Sin(_pulseT * (listening ? 5 : 2.5));
        if (!Anim.Reduced) _haloPulse.ScaleX = pulse;
        _rings[0].Opacity = 0.5 + 0.5 * Math.Abs(Math.Sin(_pulseT * 1.3));
        // волна: от уровня микрофона, по краям ниже
        _level *= 0.88;
        for (var i = 0; i < _bars.Length; i++)
        {
            var edge = 1 - Math.Abs(i - _bars.Length / 2) / (double)(_bars.Length / 2 + 1);
            var wobble = 0.6 + 0.4 * Math.Sin(i * 1.7 + _pulseT * 9);
            var h = listening ? 6 + Math.Min(38, _level * 120 * edge * wobble) : 6;
            _bars[i].Height = h;
            _bars[i].Opacity = listening ? 0.45 + 0.55 * edge : 0.3;
        }
        // рот шевелится в такт «голоску»
        if (_phaseNow == VoicePhase.Answering && ((int)(_pulseT * 30) % 4 == 0)) SetEmotion(_emotion);
    }
}
