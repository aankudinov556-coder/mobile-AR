using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Vera.Core;
using Vera.Core.Agent;
using Vera.Core.Providers;
using Vera.Core.Speech;
using Vera.Desktop.Pet;
using Vera.Desktop.Platform;
using Vera.Desktop.UI;

namespace Vera.Desktop.Chat;

/// <summary>Вкладка «Чат»: сообщения с Markdown, чипы инструментов, «печатает…», вложения, голос.</summary>
public sealed class ChatPanel : DockPanel
{
    private sealed class Chip
    {
        public string Id = "", Name = "", Label = "";
        public bool Running, IsError;
    }

    private sealed class VeraMessageView
    {
        public required Border Bubble;
        public required WrapPanel Tools;
        public readonly StringBuilder Text = new();
        public readonly List<Chip> Chips = new();
        public int LastStep = -1;
        public bool Dirty;
    }

    private readonly AppHost _host;
    private readonly ChatWindow _window;
    private readonly ScrollViewer _scroll;
    private readonly StackPanel _messages;
    private readonly FrameworkElement _empty;
    private readonly TextBox _input;
    private readonly Button _send, _mic;
    private readonly WrapPanel _attachBox;
    private readonly List<ImageData> _attachments = new();
    private readonly Dictionary<string, VeraMessageView> _live = new();
    private string? _pendingPreview;
    private bool _stick = true;
    private bool _renderQueued;

    public ChatPanel(AppHost host, ChatWindow window)
    {
        _host = host;
        _window = window;
        LastChildFill = true;

        // ------------------------------------------------------------ toolbar
        var hint = Ui.Muted("Enter — отправить · Shift+Enter — перенос");
        hint.VerticalAlignment = VerticalAlignment.Center;
        hint.TextWrapping = TextWrapping.NoWrap;
        hint.TextTrimming = TextTrimming.CharacterEllipsis;
        var newChat = Ui.Button("Новый диалог", "Btn.GhostSmall", "edit", NewChat, "Очистить диалог");
        var toolbar = new Border { Padding = new Thickness(12, 6, 12, 6), BorderThickness = new Thickness(0, 0, 0, 1), Child = Ui.Row(new UIElement[] { hint, newChat }, 8, 0) };
        toolbar.SetResourceReference(Border.BorderBrushProperty, "Line");
        SetDock(toolbar, Dock.Top);
        Children.Add(toolbar);

        // ------------------------------------------------------------ composer
        var shot = Ui.IconButton("camera", "Приложить скриншот экрана", AttachScreenshot);
        _input = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 38,
            MaxHeight = 160,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Tag = "Напиши Вере…",
            Padding = new Thickness(10, 8, 10, 8),
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        Ui.SetRadius(_input, new CornerRadius(12));
        _input.PreviewKeyDown += OnInputKey;
        _mic = Ui.IconButton("mic", "Сказать голосом", () => _host.Voice.StartListening());
        _send = new Button { Style = Ui.Style("Btn.Send"), Content = Icons.Make("send"), ToolTip = "Отправить" };
        _send.Click += (_, _) => Send();
        foreach (var b in new FrameworkElement[] { shot, _mic, _send }) b.VerticalAlignment = VerticalAlignment.Bottom;
        var composerRow = Ui.Row(new UIElement[] { shot, _input, _mic, _send }, 6, 1);
        var composer = new Border { Padding = new Thickness(10, 10, 10, 12), BorderThickness = new Thickness(0, 1, 0, 0), Child = composerRow };
        composer.SetResourceReference(Border.BorderBrushProperty, "Line");
        composer.SetResourceReference(Border.BackgroundProperty, "Bg");
        SetDock(composer, Dock.Bottom);
        Children.Add(composer);

        _attachBox = new WrapPanel { Margin = new Thickness(12, 8, 12, 0), Visibility = Visibility.Collapsed };
        SetDock(_attachBox, Dock.Bottom);
        Children.Add(_attachBox);

        // ------------------------------------------------------------ messages
        _messages = new StackPanel { Margin = new Thickness(12, 14, 12, 8) };
        _empty = BuildEmpty();
        var stack = new Grid();
        stack.Children.Add(_messages);
        stack.Children.Add(_empty);
        _scroll = new ScrollViewer
        {
            Content = stack,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
        };
        _scroll.ScrollChanged += (_, e) =>
        {
            // пользователь прокрутил сам (а не содержимое выросло)
            if (e.ExtentHeightChange == 0 && e.ViewportHeightChange == 0)
                _stick = _scroll.ScrollableHeight - _scroll.VerticalOffset < 60;
        };
        Children.Add(_scroll);
        // пустое состояние — по центру видимой области, как margin:auto в app.css
        stack.SetBinding(MinHeightProperty, new System.Windows.Data.Binding(nameof(ScrollViewer.ViewportHeight)) { Source = _scroll });

        host.Voice.StateChanged += (st, _) =>
            Ui.SetActive(_mic, st is RecorderState.Listening or RecorderState.Recording);

        LoadHistory();
        SetSendMode();
    }

    public bool Busy { get; private set; }

    public void FocusInput() => Dispatcher.BeginInvoke(() => { _input.Focus(); Keyboard.Focus(_input); }, DispatcherPriority.Input);

    // ================================================================== empty state

    private FrameworkElement BuildEmpty()
    {
        var art = new Image { Source = SpriteSheet.LoadImage("/assets/vera/preview.png"), Width = 128, Height = 144, HorizontalAlignment = HorizontalAlignment.Center };
        RenderOptions.SetBitmapScalingMode(art, BitmapScalingMode.NearestNeighbor);
        art.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 12, ShadowDepth = 6, Direction = 270, Opacity = 0.3 };
        var p = new TextBlock { TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 12, 0, 0), LineHeight = 21 };
        p.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        var bold = new System.Windows.Documents.Run("Хе-хе, привет~") { FontWeight = FontWeights.Bold };
        bold.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "Fg");
        p.Inlines.Add(bold);
        p.Inlines.Add(new System.Windows.Documents.LineBreak());
        p.Inlines.Add(new System.Windows.Documents.Run("Я Вера. Спроси что-нибудь, попроси напомнить, открыть программу или глянуть на экран."));
        var sp = new StackPanel { MaxWidth = 330, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(12, 20, 12, 20) };
        sp.Children.Add(art);
        sp.Children.Add(p);
        var sugg = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        foreach (var (label, text) in new[]
                 {
                     ("⏰ Напомни через 20 минут…", "Напомни мне через 20 минут сделать перерыв"),
                     ("🌦️ Погода на завтра", "Какая погода в Москве на завтра?"),
                     ("📸 Что у меня на экране?", "Посмотри на мой экран и скажи, что там"),
                     ("🌐 Открой YouTube", "Открой YouTube"),
                 })
        {
            var b = new Button { Content = label, Style = Ui.Style("Btn.Suggest"), Margin = new Thickness(0, 0, 0, 6), HorizontalAlignment = HorizontalAlignment.Stretch };
            var t = text;
            b.Click += (_, _) => { _input.Text = t; Send(); };
            sugg.Children.Add(b);
        }
        sp.Children.Add(sugg);
        return sp;
    }

    private void UpdateEmpty() =>
        _empty.Visibility = _messages.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    // ================================================================== messages

    private static string TimeStr(DateTimeOffset t) => t.ToLocalTime().ToString("HH:mm");

    private static BitmapSource? FromBase64(string b64, int decodeWidth)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.StreamSource = new MemoryStream(Convert.FromBase64String(b64));
            bmp.DecodePixelWidth = decodeWidth;
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static ContextMenu CopyMenu(Func<string> text)
    {
        var mi = new MenuItem { Header = "Копировать текст" };
        mi.Click += (_, _) =>
        {
            try { Clipboard.SetText(text()); } catch (Exception) { }
        };
        return new ContextMenu { Items = { mi } };
    }

    private void AddUser(string text, bool hasImage, string? imageB64, DateTimeOffset time)
    {
        var body = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(44, 0, 0, 12) };
        if (imageB64 is not null && FromBase64(imageB64, 440) is { } img)
        {
            var image = new Image { Source = img, MaxWidth = 220, Stretch = Stretch.Uniform };
            var frame = new Border { CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Child = image, ClipToBounds = true, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 0, 5) };
            frame.SetResourceReference(Border.BorderBrushProperty, "Line");
            body.Children.Add(frame);
        }
        var tb = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.White, LineHeight = 21 };
        var inner = new StackPanel();
        inner.Children.Add(tb);
        if (hasImage && imageB64 is null)
            inner.Children.Add(new TextBlock { Text = "📎 изображение", FontSize = 11, Foreground = Brushes.White, Opacity = 0.85 });
        var bubble = new Border
        {
            CornerRadius = new CornerRadius(14, 14, 4, 14),
            Padding = new Thickness(12, 9, 12, 9),
            Child = inner,
            HorizontalAlignment = HorizontalAlignment.Right,
            ContextMenu = CopyMenu(() => text),
        };
        bubble.SetResourceReference(Border.BackgroundProperty, "AccentGrad");
        body.Children.Add(bubble);
        var meta = Ui.Muted(TimeStr(time), 10.5);
        meta.HorizontalAlignment = HorizontalAlignment.Right;
        meta.Margin = new Thickness(0, 4, 4, 0);
        body.Children.Add(meta);
        _messages.Children.Add(body);
        UpdateEmpty();
    }

    private VeraMessageView AddVera(DateTimeOffset time)
    {
        var avatarImg = new Image { Source = SpriteSheet.LoadImage("/assets/icon.png"), Width = 30, Height = 30 };
        RenderOptions.SetBitmapScalingMode(avatarImg, BitmapScalingMode.NearestNeighbor);
        var avatar = new Border { CornerRadius = new CornerRadius(8), Child = avatarImg, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 8, 0) };
        avatar.SetResourceReference(Border.BackgroundProperty, "AccentSoft");
        var tools = new WrapPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 5) };
        var bubble = new Border
        {
            CornerRadius = new CornerRadius(4, 14, 14, 14),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 9, 12, 9),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        bubble.SetResourceReference(Border.BackgroundProperty, "Bg2");
        bubble.SetResourceReference(Border.BorderBrushProperty, "Line");
        var meta = Ui.Muted(TimeStr(time), 10.5);
        meta.Margin = new Thickness(4, 4, 0, 0);
        var body = new StackPanel();
        body.Children.Add(tools);
        body.Children.Add(bubble);
        body.Children.Add(meta);
        var row = Ui.Row(new UIElement[] { avatar, body }, 0, 1);
        row.Margin = new Thickness(0, 0, 0, 12);
        _messages.Children.Add(row);
        UpdateEmpty();
        var view = new VeraMessageView { Bubble = bubble, Tools = tools };
        bubble.ContextMenu = CopyMenu(() => view.Text.ToString());
        return view;
    }

    private static FrameworkElement Typing()
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
        for (var i = 0; i < 3; i++)
        {
            var dot = new Ellipse { Width = 7, Height = 7, Margin = new Thickness(i == 0 ? 0 : 4, 0, 0, 0), Opacity = 0.4, RenderTransform = new TranslateTransform() };
            dot.SetResourceReference(Shape.FillProperty, "Muted");
            var begin = TimeSpan.FromMilliseconds(150 * i);
            var op = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(1.2), RepeatBehavior = RepeatBehavior.Forever, BeginTime = begin };
            op.KeyFrames.Add(new LinearDoubleKeyFrame(0.4, KeyTime.FromPercent(0)));
            op.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromPercent(0.4)));
            op.KeyFrames.Add(new LinearDoubleKeyFrame(0.4, KeyTime.FromPercent(0.8)));
            op.KeyFrames.Add(new LinearDoubleKeyFrame(0.4, KeyTime.FromPercent(1)));
            var y = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(1.2), RepeatBehavior = RepeatBehavior.Forever, BeginTime = begin };
            y.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(0)));
            y.KeyFrames.Add(new LinearDoubleKeyFrame(-4, KeyTime.FromPercent(0.4)));
            y.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(0.8)));
            y.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(1)));
            dot.BeginAnimation(OpacityProperty, op);
            ((TranslateTransform)dot.RenderTransform).BeginAnimation(TranslateTransform.YProperty, y);
            sp.Children.Add(dot);
        }
        return sp;
    }

    private static FrameworkElement ChipView(Chip c)
    {
        var label = c.Running ? $"{c.Name}…" : (string.IsNullOrEmpty(c.Label) ? c.Name : c.Label);
        var text = new TextBlock { Text = label, FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 340 };
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        if (c.Running)
        {
            var spin = new Ellipse { Width = 9, Height = 9, StrokeThickness = 2, Margin = new Thickness(0, 0, 5, 0), RenderTransformOrigin = new Point(0.5, 0.5) };
            spin.SetResourceReference(Shape.StrokeProperty, "Accent");
            spin.StrokeDashArray = new DoubleCollection { 2.2, 1.3 };
            var rot = new RotateTransform();
            spin.RenderTransform = rot;
            rot.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.8)) { RepeatBehavior = RepeatBehavior.Forever });
            sp.Children.Add(spin);
        }
        sp.Children.Add(text);
        var b = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(9, 3, 9, 3), Margin = new Thickness(0, 0, 5, 5), Child = sp, ToolTip = label };
        if (c.IsError)
        {
            b.SetResourceReference(Border.BackgroundProperty, "ErrSoft");
            text.SetResourceReference(TextBlock.ForegroundProperty, "Err");
        }
        else
        {
            b.SetResourceReference(Border.BackgroundProperty, "AccentSoft");
            text.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
        }
        if (c.Running) b.Opacity = 0.75;
        return b;
    }

    private void RenderVera(VeraMessageView v, bool typing, bool error = false)
    {
        v.Dirty = false;
        v.Tools.Children.Clear();
        foreach (var c in v.Chips) v.Tools.Children.Add(ChipView(c));
        v.Tools.Visibility = v.Chips.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        var text = v.Text.ToString();
        if (text.Length > 0)
        {
            var md = MarkdownView.Render(text);
            if (error) md.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "Err");
            v.Bubble.Child = md;
        }
        else if (typing) v.Bubble.Child = Typing();
        else v.Bubble.Child = null;
        v.Bubble.Visibility = text.Length > 0 || typing ? Visibility.Visible : Visibility.Collapsed;
        if (error)
        {
            v.Bubble.SetResourceReference(Border.BorderBrushProperty, "Err");
            v.Bubble.SetResourceReference(Border.BackgroundProperty, "ErrSoft");
        }
    }

    private void ScrollDown(bool force)
    {
        if (!force && !_stick) return;
        _stick = true;
        Dispatcher.BeginInvoke(() => _scroll.ScrollToEnd(), DispatcherPriority.Loaded);
    }

    public void LoadHistory()
    {
        _messages.Children.Clear();
        _live.Clear();
        foreach (var m in _host.Core.Agent.GetVisibleHistory())
        {
            if (m.Role == "user") AddUser(m.Text, m.HasImage, null, m.Time);
            else
            {
                var v = AddVera(m.Time);
                v.Text.Append(m.Text);
                foreach (var t in m.Tools) v.Chips.Add(new Chip { Id = t.Id, Name = t.Name, Label = t.Label, IsError = t.IsError });
                RenderVera(v, false);
            }
        }
        UpdateEmpty();
        ScrollDown(true);
    }

    // ================================================================== agent events

    private void QueueRender()
    {
        if (_renderQueued) return;
        _renderQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _renderQueued = false;
            foreach (var v in _live.Values.Where(x => x.Dirty)) RenderVera(v, v.Text.Length == 0);
            ScrollDown(false);
        }, DispatcherPriority.Background);
    }

    public void OnAgentEvent(AgentEvent e)
    {
        switch (e)
        {
            case TurnStarted ts:
            {
                Busy = true;
                SetSendMode();
                if (!ts.Hidden) AddUser(ts.UserText, ts.HasImage, _pendingPreview, DateTimeOffset.Now);
                _pendingPreview = null;
                var v = AddVera(DateTimeOffset.Now);
                RenderVera(v, true);
                _live[ts.TurnId] = v;
                ScrollDown(true);
                break;
            }
            case TextDelta d:
            {
                if (!_live.TryGetValue(d.TurnId, out var v)) return;
                if (v.LastStep >= 0 && v.LastStep != d.Step && v.Text.Length > 0)
                {
                    var trimmed = v.Text.ToString().TrimEnd();
                    v.Text.Clear().Append(trimmed).Append("\n\n");
                }
                v.LastStep = d.Step;
                v.Text.Append(d.Delta);
                v.Dirty = true;
                QueueRender();
                break;
            }
            case ToolActivity ta:
            {
                if (!_live.TryGetValue(ta.TurnId, out var v)) return;
                var c = v.Chips.FirstOrDefault(x => x.Id == ta.CallId);
                if (c is null) v.Chips.Add(c = new Chip { Id = ta.CallId });
                c.Name = ta.Name;
                c.Running = ta.Running;
                c.Label = ta.Label;
                c.IsError = ta.IsError;
                v.Dirty = true;
                QueueRender();
                break;
            }
            case TurnEnded te:
            {
                if (_live.Remove(te.TurnId, out var v))
                {
                    if (v.Text.Length == 0 && !string.IsNullOrEmpty(te.Text)) v.Text.Append(te.Text);
                    if (v.Text.Length == 0 && v.Chips.Count == 0) v.Text.Append('…');
                    RenderVera(v, false);
                }
                Busy = false;
                SetSendMode();
                ScrollDown(false);
                break;
            }
            case TurnFailed tf:
            {
                if (_live.Remove(tf.TurnId, out var v))
                {
                    if (tf.Aborted)
                    {
                        if (v.Text.Length > 0) v.Text.Append(" …");
                        else v.Text.Append("*(остановлено)*");
                        RenderVera(v, false);
                    }
                    else
                    {
                        if (v.Text.Length > 0) v.Text.Append("\n\n");
                        v.Text.Append("⚠️ ").Append(tf.Message);
                        RenderVera(v, false, error: true);
                    }
                }
                Busy = false;
                SetSendMode();
                ScrollDown(false);
                break;
            }
        }
    }

    private void SetSendMode()
    {
        Ui.SetActive(_send, Busy);
        _send.Content = Icons.Make(Busy ? "stop" : "send");
        _send.ToolTip = Busy ? "Остановить" : "Отправить";
    }

    // ================================================================== sending

    private void OnInputKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0 && !e.IsRepeat)
        {
            e.Handled = true;
            Send();
            return;
        }
        var paste = (e.Key == Key.V && (Keyboard.Modifiers & ModifierKeys.Control) != 0) ||
                    (e.Key == Key.Insert && (Keyboard.Modifiers & ModifierKeys.Shift) != 0);
        if (paste && TryPasteImage()) e.Handled = true;
    }

    /// <summary>Картинка из буфера обмена → вложение (JPEG ≤1600 px).</summary>
    private bool TryPasteImage()
    {
        try
        {
            if (!System.Windows.Forms.Clipboard.ContainsImage()) return false;
            using var img = System.Windows.Forms.Clipboard.GetImage();
            if (img is null) return false;
            var shot = ScreenCapture.Encode(img);
            _attachments.Add(new ImageData(ScreenShot.Mime, shot.Base64));
            RenderAttachments();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void RenderAttachments()
    {
        _attachBox.Children.Clear();
        for (var i = 0; i < _attachments.Count; i++)
        {
            var idx = i;
            var img = new Image { Source = FromBase64(_attachments[i].Base64, 160), Height = 56, Stretch = Stretch.Uniform };
            var frame = new Border { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Child = img, ClipToBounds = true };
            frame.SetResourceReference(Border.BorderBrushProperty, "Line");
            var rm = new Button
            {
                Style = Ui.Style("Btn.Base"),
                Width = 20,
                Height = 20,
                Padding = new Thickness(0),
                Content = Icons.Make("x", 12),
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, -6, -6, 0),
                ToolTip = "Убрать",
            };
            rm.SetResourceReference(BackgroundProperty, "Err");
            Ui.SetRadius(rm, new CornerRadius(10));
            rm.Click += (_, _) =>
            {
                if (idx < _attachments.Count) _attachments.RemoveAt(idx);
                RenderAttachments();
            };
            var g = new Grid { Margin = new Thickness(0, 6, 10, 0), Children = { frame, rm } };
            _attachBox.Children.Add(g);
        }
        _attachBox.Visibility = _attachments.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void AttachScreenshot()
    {
        try
        {
            var shot = await _host.CaptureScreenAsync();
            _attachments.Clear();
            _attachments.Add(new ImageData(ScreenShot.Mime, shot.Base64));
            RenderAttachments();
            if (_input.Text.Trim().Length == 0) _input.Text = "Что у меня на экране?";
            _input.CaretIndex = _input.Text.Length;
            FocusInput();
        }
        catch (Exception ex)
        {
            _window.Toast(ex.Message, true);
        }
    }

    private void Send()
    {
        var core = _host.Core;
        if (Busy || core.Agent.IsBusy)
        {
            core.Agent.Stop();
            return;
        }
        var text = _input.Text.Trim();
        if (text.Length == 0 && _attachments.Count == 0) return;
        var provider = core.Settings.Current.Provider;
        if (!core.Secrets.Has(provider))
        {
            var title = ProviderRegistry.Known.FirstOrDefault(k => k.Id == provider).Title ?? provider;
            _window.Toast($"Сначала добавь ключ {title} в настройках", true);
            _window.ShowTab("settings");
            return;
        }
        var images = _attachments.ToList();
        _pendingPreview = images.FirstOrDefault()?.Base64;
        _input.Clear();
        _attachments.Clear();
        RenderAttachments();
        _host.Send(text, images.Count > 0 ? images : null, "chat");
    }

    private void NewChat()
    {
        if (!ConfirmDialog.Ask(_window, "Начать новый диалог?", "Текущая переписка будет очищена (память и напоминания останутся).", okText: "Очистить"))
            return;
        _host.Core.Agent.ClearHistory();
        LoadHistory();
    }
}
