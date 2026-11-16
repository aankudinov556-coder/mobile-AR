using Microsoft.Maui.Controls.Shapes;
using Vera.Core;
using Vera.Core.Agent;
using Vera.Core.Speech;
using Vera.Core.Sprites;
using Vera.Mobile.Services;

namespace Vera.Mobile.Views;

/// <summary>
/// Голосовой режим на телефоне (концепт «Ореол»): Вера крупно в центре, слова по ходу речи,
/// ответ пузырём с плашками действий; после ответа снова слушает, после тишины — назад в чат.
/// </summary>
public sealed class VoiceModePage : ContentPage
{
    private static VeraCore Core => App.Core;
    private readonly VoiceController _voice = App.Voice;
    private readonly SpeechOutput _speech = App.Speech;
    private readonly VoiceModeFlow _flow = new(() => App.Core.Settings.Current.VoiceMode.ExitAfterSeconds);
    private readonly bool _alreadyListening;
    private readonly VeraView _vera;
    private readonly Grid _stage;
    private readonly Border _halo;
    private readonly Label _phase;
    private readonly Label _transcript;
    private readonly Border _userBubble;
    private readonly Label _userText;
    private readonly Border _answer;
    private readonly Label _answerText;
    private readonly FlexLayout _chips;
    private readonly ImageButton _mic;
    private readonly System.Text.StringBuilder _reply = new();
    private IDispatcherTimer? _timer;
    private bool _closing;
    private bool _entering;
    private double _t;

    public VoiceModePage(bool alreadyListening = false)
    {
        _alreadyListening = alreadyListening;
        Ui.StylePage(this, "Вера");
        NavigationPage.SetHasNavigationBar(this, false);
        BackgroundColor = Ui.Bg;

        // ------------------------------------------------ шапка
        var back = Ui.SquareIcon("ic_back.png", async (_, _) => await CloseAsync(), "Назад в чат");
        var title = new VerticalStackLayout
        {
            VerticalOptions = LayoutOptions.Center,
            Children = { Ui.Heading("Вера", 20), Ui.Label("голосовой режим", 12, Ui.Muted) },
        };
        var style = Core.Settings.Current.Sound.Style;
        var soundChip = Ui.Tag(ReplySound.Styles.FirstOrDefault(x => x.Id == style).Title ?? "", Ui.Accent, Ui.InkText);
        soundChip.IsVisible = style != "off";
        soundChip.VerticalOptions = LayoutOptions.Center;
        var header = new Grid
        {
            Padding = new Thickness(16, 18, 16, 8),
            ColumnSpacing = 10,
            ColumnDefinitions = { new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) },
        };
        header.Add(back, 0);
        header.Add(title, 1);
        header.Add(soundChip, 2);

        // ------------------------------------------------ центр
        _userText = Ui.Label("", 15, Ui.InkText);
        _userText.FontFamily = Ui.FontBold;
        _userBubble = new Border
        {
            BackgroundColor = Ui.Accent,
            Stroke = Ui.Ink,
            StrokeThickness = 2.5,
            StrokeShape = Ui.Round(18, 18, 18, 6),
            Padding = new Thickness(14, 10),
            HorizontalOptions = LayoutOptions.End,
            MaximumWidthRequest = 320,
            Shadow = Ui.HardShadow(),
            Content = _userText,
            IsVisible = false,
        };

        _vera = new VeraView(VeraView.PickUnit(DeviceDisplay.MainDisplayInfo.Density) * 2.1)
        {
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.End,
            Skin = Core.Settings.Current.Skin,
        };
        var ring = new Ellipse { Stroke = new SolidColorBrush(Color.FromArgb("#2A2720")), StrokeThickness = 2, WidthRequest = 280, HeightRequest = 280, HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center };
        var disk = new Ellipse { Fill = new SolidColorBrush(Ui.Surface), Stroke = new SolidColorBrush(Ui.Ink), StrokeThickness = 2.5, WidthRequest = 190, HeightRequest = 190, HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center, TranslationY = 18 };
        _halo = new Border
        {
            WidthRequest = 96,
            HeightRequest = 22,
            Stroke = Ui.Accent,
            StrokeThickness = 4,
            StrokeShape = new Ellipse(),
            BackgroundColor = Colors.Transparent,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Start,
            Margin = new Thickness(0, 26, 0, 0),
        };
        _stage = new Grid { HeightRequest = 300, WidthRequest = 300, HorizontalOptions = LayoutOptions.Center, Children = { ring, disk, _vera, _halo } };

        _phase = new Label { Text = L10n.T("Слушаю…"), FontFamily = Ui.Display, FontSize = 22, TextColor = Ui.Accent, HorizontalTextAlignment = TextAlignment.Center };
        _transcript = new Label { FontSize = 19, TextColor = Ui.Text, HorizontalTextAlignment = TextAlignment.Center, LineBreakMode = LineBreakMode.WordWrap };

        _chips = new FlexLayout { Wrap = Microsoft.Maui.Layouts.FlexWrap.Wrap, JustifyContent = Microsoft.Maui.Layouts.FlexJustify.Center, IsVisible = false };
        _answerText = Ui.Label("", 16, Ui.InkText);
        _answer = new Border
        {
            BackgroundColor = Ui.Paper,
            Stroke = Ui.Ink,
            StrokeThickness = 2.5,
            StrokeShape = Ui.Round(18, 18, 6, 18),
            Padding = new Thickness(15, 12),
            Shadow = Ui.HardShadow(),
            Content = _answerText,
            IsVisible = false,
        };

        var center = new VerticalStackLayout
        {
            Padding = new Thickness(18, 0),
            Spacing = 12,
            Children = { _userBubble, _stage, _phase, _transcript, _chips, _answer },
        };

        // ------------------------------------------------ кнопки
        var keyboard = Ui.SquareIcon("ic_keyboard.png", async (_, _) => await CloseAsync(), "Перейти к клавиатуре");
        _mic = new ImageButton
        {
            Source = "ic_mic_dark.png",
            WidthRequest = 88,
            HeightRequest = 88,
            CornerRadius = 44,
            Padding = 26,
            BackgroundColor = Ui.Accent,
            BorderColor = Ui.Ink,
            BorderWidth = 3,
            Shadow = Ui.HardShadow(4),
        };
        SemanticProperties.SetDescription(_mic, L10n.T("Говорить"));
        _mic.Clicked += (_, _) => Mic();
        var stop = Ui.SquareIcon("ic_stop.png", (_, _) => { Core.Agent.Stop(); _speech.Stop(); }, "Остановить ответ");
        var footer = new HorizontalStackLayout { Spacing = 22, HorizontalOptions = LayoutOptions.Center, Padding = new Thickness(0, 10, 0, 30), Children = { keyboard, _mic, stop } };
        keyboard.VerticalOptions = stop.VerticalOptions = LayoutOptions.Center;

        var root = new Grid
        {
            RowDefinitions = { new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Star), new RowDefinition(GridLength.Auto) },
        };
        root.Add(header, 0, 0);
        root.Add(new ScrollView { Content = center, VerticalScrollBarVisibility = ScrollBarVisibility.Never }, 0, 1);
        root.Add(footer, 0, 2);
        Content = root;

        _flow.Changed += () => MainThread.BeginInvokeOnMainThread(Render);
    }

    // ================================================================== жизнь страницы

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _voice.StateChanged += OnVoiceState;
        _voice.PartialText += OnPartial;
        _voice.Heard += OnHeard;
        _voice.NothingHeard += OnNothing;
        _voice.Level += OnLevel;
        _speech.SpeakingChanged += OnSpeaking;
        _speech.Level += OnSoundLevel;
        Core.Agent.Event += OnAgentEventAnyThread;

        var action = _flow.Enter(DateTimeOffset.Now);
        if (Core.Agent.IsBusy) _flow.OnTurnStarted();
        else if (_alreadyListening) _flow.OnListening();
        else Run(action);
        _timer = Dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(100);
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        _ = AnimateInAsync();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _timer?.Stop();
        _voice.StateChanged -= OnVoiceState;
        _voice.PartialText -= OnPartial;
        _voice.Heard -= OnHeard;
        _voice.NothingHeard -= OnNothing;
        _voice.Level -= OnLevel;
        _speech.SpeakingChanged -= OnSpeaking;
        _speech.Level -= OnSoundLevel;
        Core.Agent.Event -= OnAgentEventAnyThread;
        if (_flow.Active)
        {
            _flow.Exit();
            _voice.Cancel();
        }
    }

    protected override bool OnBackButtonPressed()
    {
        _ = CloseAsync();
        return true;
    }

    private async Task CloseAsync()
    {
        if (_closing) return;
        _closing = true;
        _flow.Exit();
        _voice.Cancel();
        var ms = (uint)VoiceModeFlow.ExitMs(Core.Settings.Current.VoiceMode.Animation, AnimationsOff());
        if (ms > 0)
        {
            await Task.WhenAll(Content.FadeToAsync(0, ms, Easing.CubicIn), _stage.ScaleToAsync(0.4, ms, Easing.CubicIn));
        }
        try { await Navigation.PopModalAsync(false); } catch { }
    }

    /// <summary>Вход: Вера «вылетает» из шапки в центр, нимб раскрывается.</summary>
    private async Task AnimateInAsync()
    {
        var anim = Core.Settings.Current.VoiceMode.Animation;
        var ms = (uint)VoiceModeFlow.EnterMs(anim, AnimationsOff());
        if (ms == 0) return;
        _entering = true;
        try { await AnimateInCoreAsync(anim, ms); }
        finally { _entering = false; }
    }

    private async Task AnimateInCoreAsync(string anim, uint ms)
    {
        Content.Opacity = 0;
        if (anim == "fast")
        {
            await Content.FadeToAsync(1, ms, Easing.CubicOut);
            return;
        }
        _stage.Scale = 0.28;
        _stage.TranslationX = -Width * 0.3;
        _stage.TranslationY = -Height * 0.25;
        _halo.ScaleX = 0.3;
        _phase.Opacity = _transcript.Opacity = 0;
        _ = Content.FadeToAsync(1, ms / 3);
        await Task.Delay((int)(ms / 4));
        await Task.WhenAll(
            _stage.ScaleToAsync(1, ms * 3 / 4, Easing.SpringOut),
            _stage.TranslateToAsync(0, 0, ms * 3 / 4, Easing.CubicOut),
            _halo.ScaleXToAsync(1, ms / 2, Easing.SpringOut));
        await Task.WhenAll(_phase.FadeToAsync(1, 180), _transcript.FadeToAsync(1, 180));
    }

    /// <summary>Анимации выключены в системе (Параметры разработчика → масштаб анимации 0).</summary>
    private static bool AnimationsOff()
    {
        try
        {
            var cr = Android.App.Application.Context.ContentResolver;
            return Android.Provider.Settings.Global.GetFloat(cr, Android.Provider.Settings.Global.AnimatorDurationScale, 1f) == 0f;
        }
        catch { return false; }
    }

    // ================================================================== события

    private void Run(VoiceModeAction action)
    {
        switch (action)
        {
            case VoiceModeAction.StartListening:
                if (!Core.Agent.IsBusy && !_voice.IsCommandActive) _ = _voice.StartCommandAsync();
                break;
            case VoiceModeAction.Exit:
                _ = CloseAsync();
                break;
        }
    }

    private void Tick()
    {
        Run(_flow.Tick(DateTimeOffset.Now));
        // нимб дышит; пока слушает — быстрее
        _t += 0.1;
        var listening = _flow.Phase is VoicePhase.Listening or VoicePhase.Hearing;
        if (!_entering && !AnimationsOff())
            _halo.ScaleX = 1 + (listening ? 0.07 : 0.03) * Math.Sin(_t * (listening ? 5 : 2.5));
    }

    private void Mic()
    {
        if (_flow.Phase is VoicePhase.Thinking or VoicePhase.Answering)
        {
            Core.Agent.Stop();
            _speech.Stop();
            return;
        }
        _voice.Toggle();
    }

    private void OnVoiceState(RecorderState st, string? reason) => MainThread.BeginInvokeOnMainThread(() =>
    {
        var now = DateTimeOffset.Now;
        switch (st)
        {
            case RecorderState.Listening: _flow.OnListening(); break;
            case RecorderState.Recording: _flow.OnSpeech(now); break;
            case RecorderState.Off or RecorderState.Wake when reason is "timeout" or "short":
                Run(_flow.OnNothingHeard(now));
                break;
        }
        _vera.Mode = st is RecorderState.Listening or RecorderState.Recording ? PetMode.Listening : _vera.Mode;
    });

    private void OnPartial(string text) => _flow.OnSpeech(DateTimeOffset.Now, text);
    private void OnHeard(string text) => _flow.OnHeard(DateTimeOffset.Now, text);
    private void OnNothing() => MainThread.BeginInvokeOnMainThread(() => Run(_flow.OnNothingHeard(DateTimeOffset.Now)));
    private void OnLevel(float l) => _vera.SetLevel(l);
    private void OnSoundLevel(float l) => _vera.SetLevel(l);

    private void OnSpeaking(bool on) => MainThread.BeginInvokeOnMainThread(() =>
    {
        _vera.Speaking = on;
        if (on) _vera.Mode = PetMode.Talking;
        else if (_vera.Mode == PetMode.Talking) _vera.Mode = PetMode.Idle;
        _flow.OnSound(on, DateTimeOffset.Now);
    });

    private void OnAgentEventAnyThread(AgentEvent e) => MainThread.BeginInvokeOnMainThread(() => OnAgentEvent(e));

    private void OnAgentEvent(AgentEvent e)
    {
        var now = DateTimeOffset.Now;
        switch (e)
        {
            case TurnStarted ts:
                _reply.Clear();
                _answerText.Text = "";
                _answer.IsVisible = false;
                _chips.Children.Clear();
                _chips.IsVisible = false;
                if (!ts.Hidden && ts.UserText.Length > 0) { _userText.Text = ts.UserText; _userBubble.IsVisible = true; }
                _vera.Mode = PetMode.Thinking;
                _flow.OnTurnStarted();
                break;
            case EmotionChanged em:
                _vera.SetEmotion(em.Emotion, 20000);
                break;
            case TextDelta d:
                _reply.Append(d.Delta);
                _answerText.Text = _reply.ToString().Trim();
                _answer.IsVisible = _answerText.Text.Length > 0;
                if (_vera.Mode == PetMode.Thinking) _vera.Mode = PetMode.Talking;
                _flow.OnAnswerText();
                break;
            case ToolActivity t when !t.Running:
                _chips.Children.Add(Pill(string.IsNullOrEmpty(t.Label) ? t.Name : t.Label, t.IsError));
                _chips.IsVisible = true;
                break;
            case TurnEnded te:
                if (te.Text.Length > 0) { _answerText.Text = te.Text.Trim(); _answer.IsVisible = true; }
                _vera.SetEmotion(te.Emotion, 12000);
                if (!_speech.IsActive) _vera.Mode = PetMode.Idle;
                _flow.OnTurnEnded(now);
                break;
            case TurnFailed tf:
                if (!tf.Aborted) { _answerText.Text = "⚠️ " + tf.Message; _answer.IsVisible = true; }
                _vera.Mode = PetMode.Idle;
                _flow.OnTurnEnded(now);
                break;
        }
    }

    private static Border Pill(string text, bool error) => new()
    {
        BackgroundColor = error ? Ui.DangerSoft : Ui.Accent,
        Stroke = Ui.Ink,
        StrokeThickness = 2,
        StrokeShape = Ui.Round(999),
        Padding = new Thickness(10, 4),
        Margin = new Thickness(3),
        Content = new Label { Text = text, FontSize = 12, FontFamily = Ui.FontBold, TextColor = error ? Ui.Text : Ui.InkText },
    };

    private void Render()
    {
        var p = _flow.Phase;
        _phase.Text = L10n.T(p switch
        {
            VoicePhase.Listening or VoicePhase.Hearing => "Слушаю…",
            VoicePhase.Thinking => "Думаю…",
            VoicePhase.Answering => "Отвечаю",
            _ => "Говори, я слушаю",
        });
        if (p is VoicePhase.Listening or VoicePhase.Hearing)
        {
            var fs = new FormattedString();
            if (_flow.Partial.Length > 0) fs.Spans.Add(new Span { Text = _flow.Partial, TextColor = Ui.Muted });
            fs.Spans.Add(new Span { Text = " ▍", TextColor = Ui.Accent });
            _transcript.FormattedText = fs;
            _transcript.IsVisible = true;
        }
        else _transcript.IsVisible = false;
        _mic.Source = p is VoicePhase.Thinking or VoicePhase.Answering ? "ic_stop_dark.png"
            : _voice.IsCommandActive ? "ic_mic_on_dark.png" : "ic_mic_dark.png";
    }
}
