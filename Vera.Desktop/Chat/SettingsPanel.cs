using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Vera.Core;
using Vera.Core.Providers;
using Vera.Desktop.Audio;
using Vera.Desktop.Platform;
using Vera.Desktop.UI;

namespace Vera.Desktop.Chat;

/// <summary>Вкладка «Настройки» — те же разделы, что в src/renderer/app.html.</summary>
public sealed class SettingsPanel : Grid
{
    private readonly AppHost _host;
    private readonly ChatWindow _window;
    private readonly List<Action> _refreshers = new();
    private readonly StackPanel _keys = new();
    private readonly Border _welcome;
    private readonly TextBlock _hotkeyErrors;
    private readonly Border _hotkeyErrorsBox;
    private bool _loading;

    private ComboBox _model = null!;
    private TextBlock _modelHint = null!;
    private readonly Dictionary<string, RadioButton> _providerButtons = new();
    private ComboBox _elevenVoice = null!;
    private ComboBox _systemVoice = null!;
    private ComboBox _mic = null!;
    private StackPanel _apps = null!;
    private TextBox _appName = null!;
    private TextBlock _appStatus = null!;

    public SettingsPanel(AppHost host, ChatWindow window)
    {
        _host = host;
        _window = window;
        var root = new StackPanel();

        // ------------------------------------------------------------ welcome
        var welcomeText = new TextBlock { TextWrapping = TextWrapping.Wrap, LineHeight = 20, Margin = new Thickness(0, 4, 0, 0) };
        welcomeText.Inlines.Add(new Run("Чтобы Вера заработала, вставь хотя бы один API-ключ ниже — например, "));
        welcomeText.Inlines.Add(new Run("OpenRouter") { FontWeight = FontWeights.Bold });
        welcomeText.Inlines.Add(new Run(" (один ключ даёт доступ к сотням моделей). Для голоса и распознавания речи нужен ключ "));
        welcomeText.Inlines.Add(new Run("OpenAI") { FontWeight = FontWeights.Bold });
        welcomeText.Inlines.Add(new Run(" (или "));
        welcomeText.Inlines.Add(new Run("Groq") { FontWeight = FontWeights.Bold });
        welcomeText.Inlines.Add(new Run(" для распознавания + системный голос Windows бесплатно)."));
        _welcome = Ui.Card(Ui.Title("Добро пожаловать!"), welcomeText);
        _welcome.SetResourceReference(Border.BorderBrushProperty, "Accent");
        _welcome.SetResourceReference(Border.BackgroundProperty, "AccentSoft");
        root.Children.Add(_welcome);

        root.Children.Add(ModelCard());
        root.Children.Add(KeysCard());
        root.Children.Add(PersonaCard());
        root.Children.Add(VoiceCard());
        root.Children.Add(SttCard());

        _hotkeyErrors = Ui.Text("", 12, "Err");
        _hotkeyErrorsBox = Ui.Note("", error: true);
        _hotkeyErrorsBox.Child = _hotkeyErrors;
        root.Children.Add(HotkeysCard());
        root.Children.Add(PetCard());
        root.Children.Add(RemindersCard());
        root.Children.Add(CapabilitiesCard());
        root.Children.Add(AppsCard());
        root.Children.Add(SystemCard());

        Children.Add(Ui.Scroll(root));
        RenderKeys();
        Refresh();
    }

    private VeraCore Core => _host.Core;
    private VeraSettings S => _host.Core.Settings.Current;

    // ================================================================== binding helpers

    private void Save(Action<VeraSettings> change)
    {
        if (_loading) return;
        try { Core.Settings.Update(change); }
        catch (Exception e) { _window.Toast(e.Message, true); }
    }

    public void Refresh()
    {
        _loading = true;
        try
        {
            foreach (var r in _refreshers)
            {
                try { r(); } catch (Exception) { /* один сломанный элемент не должен ломать остальные */ }
            }
        }
        finally
        {
            _loading = false;
        }
    }

    private CheckBox Toggle(string label, Func<VeraSettings, bool> get, Action<VeraSettings, bool> set, string? hint = null)
    {
        var cb = Ui.Toggle(label, hint);
        _refreshers.Add(() => cb.IsChecked = get(S));
        cb.Click += (_, _) => Save(s => set(s, cb.IsChecked == true));
        return cb;
    }

    private ComboBox Combo(IEnumerable<(string Value, string Text)> options, Func<VeraSettings, string?> get, Action<VeraSettings, string> set)
    {
        var c = Ui.Combo(options);
        _refreshers.Add(() => { if (!c.IsDropDownOpen) Ui.Select(c, get(S) ?? ""); });
        c.SelectionChanged += (_, _) =>
        {
            if (_loading) return;
            var v = Ui.Selected(c);
            if (v is not null && v != (get(S) ?? "")) Save(s => set(s, v));
        };
        return c;
    }

    private static (string, string)[] Same(params string[] values) => values.Select(v => (v, v)).ToArray();

    private TextBox Text(Func<VeraSettings, string?> get, Action<VeraSettings, string> set, bool multiline = false, string? placeholder = null, int maxLength = 0)
    {
        var tb = new TextBox { Tag = placeholder ?? "" };
        if (maxLength > 0) tb.MaxLength = maxLength;
        if (multiline)
        {
            tb.AcceptsReturn = true;
            tb.TextWrapping = TextWrapping.Wrap;
            tb.MinHeight = 64;
            tb.MaxHeight = 180;
            tb.VerticalContentAlignment = VerticalAlignment.Top;
            tb.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        }
        _refreshers.Add(() => { if (!tb.IsKeyboardFocusWithin) tb.Text = get(S) ?? ""; });
        void Commit()
        {
            var v = tb.Text;
            if (v != (get(S) ?? "")) Save(s => set(s, v));
        }
        tb.LostKeyboardFocus += (_, _) => Commit();
        if (!multiline) tb.KeyDown += (_, e) => { if (e.Key == Key.Enter) Commit(); };
        return tb;
    }

    private UIElement SliderField(string label, double min, double max, double step, Func<VeraSettings, double> get,
        Action<VeraSettings, double> set, Func<double, string> format)
    {
        var slider = Ui.Slider(min, max, step);
        var output = Ui.Text("", 12.5, "Fg", FontWeights.Bold, wrap: false);
        var debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        debounce.Tick += (_, _) =>
        {
            debounce.Stop();
            var v = slider.Value;
            if (Math.Abs(v - get(S)) > 1e-9) Save(s => set(s, v));
        };
        _refreshers.Add(() =>
        {
            if (!slider.IsMouseCaptureWithin && !debounce.IsEnabled) slider.Value = get(S);
            output.Text = format(slider.Value);
        });
        slider.ValueChanged += (_, _) =>
        {
            output.Text = format(slider.Value);
            if (_loading) return;
            debounce.Stop();
            debounce.Start();
        };
        return Ui.Field(label, slider, output);
    }

    private void ShowIf(UIElement el, Func<VeraSettings, bool> cond) =>
        _refreshers.Add(() => el.Visibility = cond(S) ? Visibility.Visible : Visibility.Collapsed);

    private static string Num2(double v) => v.ToString("0.00", CultureInfo.InvariantCulture);

    // ================================================================== model

    private Border ModelCard()
    {
        // провайдер — сегментированный переключатель
        var seg = new UniformGrid { Rows = 1, Columns = ProviderRegistry.Known.Count };
        foreach (var (id, title, _) in ProviderRegistry.Known)
        {
            var rb = Ui.Radio(title, "provider", "Segment");
            rb.Margin = new Thickness(1.5, 0, 1.5, 0);
            var pid = id;
            rb.Checked += (_, _) =>
            {
                if (_loading) return;
                _model.Items.Clear();
                Save(s => s.Provider = pid);
            };
            _providerButtons[id] = rb;
            seg.Children.Add(rb);
        }
        var segBox = new Border { CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Padding = new Thickness(3), Child = seg };
        segBox.SetResourceReference(Border.BackgroundProperty, "Bg3");
        segBox.SetResourceReference(Border.BorderBrushProperty, "Line");
        _refreshers.Add(() =>
        {
            if (_providerButtons.TryGetValue(S.Provider, out var b) && b.IsChecked != true) b.IsChecked = true;
        });

        _model = new ComboBox { IsEditable = true, IsTextSearchEnabled = false, StaysOpenOnEdit = true };
        // фокус и Enter приходят из внутреннего поля ввода (события всплывают)
        _model.LostKeyboardFocus += (_, _) => { if (!_model.IsKeyboardFocusWithin) CommitModel(); };
        _model.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter) CommitModel(); };
        _model.SelectionChanged += (_, _) =>
        {
            if (_loading || _model.SelectedItem is not ComboBoxItem ci) return;
            _model.Text = ci.Content as string ?? "";
            CommitModel();
        };
        _refreshers.Add(() => { if (!_model.IsKeyboardFocusWithin) _model.Text = S.CurrentModel; });
        var refresh = Ui.IconButton("refresh", "Загрузить список моделей", LoadModels);
        _modelHint = Ui.Muted("");
        _modelHint.Margin = new Thickness(0, 5, 0, 0);
        _refreshers.Add(() => _modelHint.Text = ProviderRegistry.Known.FirstOrDefault(k => k.Id == S.Provider).Hint ?? "");
        var modelBox = new StackPanel();
        modelBox.Children.Add(Ui.Row(new UIElement[] { _model, refresh }, 8, 0));
        modelBox.Children.Add(_modelHint);

        var effort = Combo(new[]
        {
            ("low", "Быстро (рекомендую для болтовни)"),
            ("medium", "Сбалансированно"),
            ("high", "Глубоко (медленнее, дороже)"),
        }, s => s.Effort, (s, v) => s.Effort = v);

        var history = Text(s => s.HistoryLimit.ToString(CultureInfo.InvariantCulture), (s, v) =>
        {
            if (int.TryParse(v.Trim(), out var n)) s.HistoryLimit = Math.Clamp(n, 6, 200);
        });
        history.Width = 110;
        history.HorizontalAlignment = HorizontalAlignment.Left;

        return Ui.Card(
            Ui.Title("🧠 Модель"),
            Ui.Field("Провайдер", segBox),
            Ui.Field("Модель", modelBox),
            Ui.Field("Глубина размышлений", effort),
            Toggle("Поиск в интернете", s => s.WebSearch, (s, v) => s.WebSearch = v,
                "(Anthropic — web search, OpenRouter — плагин web; платно у провайдера)"),
            Ui.Field("Сообщений истории в контексте", history));
    }

    private void CommitModel()
    {
        var v = (_model.Text ?? "").Trim();
        if (v.Length == 0 || v == S.CurrentModel) return;
        var provider = S.Provider;
        Save(s => s.Models[provider] = v);
    }

    private async void LoadModels()
    {
        var provider = S.Provider;
        try
        {
            var list = await Task.Run(() => Core.Providers.ListModelsAsync(provider));
            var rows = provider == "openrouter" ? list.Where(m => m.Tools).ToList() : list.ToList();
            _model.Items.Clear();
            foreach (var m in rows.OrderBy(m => m.Id, StringComparer.OrdinalIgnoreCase))
                _model.Items.Add(new ComboBoxItem { Content = m.Id, ToolTip = m.Name + (m.Vision ? " · 👁 видит картинки" : "") });
            _model.Text = S.CurrentModel;
            _window.Toast($"Загружено моделей: {rows.Count}. Выбери из списка или впиши название.");
            _model.IsDropDownOpen = rows.Count > 0;
        }
        catch (Exception e)
        {
            _window.Toast(e.Message, true);
        }
    }

    // ================================================================== keys

    private Border KeysCard()
    {
        var note = Ui.Muted("Ключи хранятся только на этом компьютере в зашифрованном виде (Windows DPAPI).");
        note.Margin = new Thickness(0, 2, 0, 10);
        return Ui.Card(Ui.Title("🔑 API-ключи"), note, _keys);
    }

    public void RenderKeys()
    {
        _keys.Children.Clear();
        var statuses = Core.Secrets.Status();
        _welcome.Visibility = statuses.Any(s => s.IsSet) ? Visibility.Collapsed : Visibility.Visible;
        foreach (var st in statuses) _keys.Children.Add(KeyRow(st));
    }

    private UIElement KeyRow(SecretStatus st)
    {
        var dot = new Ellipse { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        dot.SetResourceReference(Shape.FillProperty, st.IsSet ? "Ok" : "Bg4");
        var name = Ui.Text(st.Title, 12.5, "Fg", FontWeights.SemiBold, wrap: false);
        var purpose = Ui.Muted(st.Purpose, 11.5);
        purpose.TextWrapping = TextWrapping.NoWrap;
        purpose.TextTrimming = TextTrimming.CharacterEllipsis;
        purpose.Margin = new Thickness(8, 0, 0, 0);
        purpose.VerticalAlignment = VerticalAlignment.Center;
        var url = st.Url;
        var link = Ui.LinkButton("получить ключ ↗", () => MarkdownView.OpenLink(url));
        link.VerticalAlignment = VerticalAlignment.Center;
        var head = Ui.Row(new UIElement[] { dot, name, purpose, link }, 0, 2);

        var box = new PasswordBox();
        var ph = Ui.Muted(st.IsSet ? "сохранён: " + st.Hint : "вставь ключ", 13);
        ph.IsHitTestVisible = false;
        ph.VerticalAlignment = VerticalAlignment.Center;
        ph.Margin = new Thickness(12, 0, 0, 0);
        box.PasswordChanged += (_, _) => ph.Visibility = box.Password.Length > 0 ? Visibility.Collapsed : Visibility.Visible;
        var boxHost = new Grid { Children = { box, ph } };

        var status = Ui.Text("", 11.5, "Muted");
        var keyName = st.Name;
        async Task Test()
        {
            status.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            status.Text = "Проверяю…";
            try
            {
                var r = await Task.Run(() => Core.TestKeyAsync(keyName));
                status.Text = "✓ " + r;
                status.SetResourceReference(TextBlock.ForegroundProperty, "Ok");
            }
            catch (Exception e)
            {
                status.Text = "✗ " + e.Message;
                status.SetResourceReference(TextBlock.ForegroundProperty, "Err");
            }
        }

        async void SaveKey()
        {
            var v = box.Password.Trim();
            if (v.Length == 0)
            {
                _window.Toast("Вставь ключ в поле", true);
                return;
            }
            try
            {
                Core.Secrets.Set(keyName, v); // Changed → RenderKeys() сразу перерисует строки
            }
            catch (Exception e)
            {
                status.Text = "✗ " + e.Message;
                return;
            }
            // строка пересоздана — проверяем ключ уже в новой
            await TestFresh(keyName);
        }

        var save = Ui.Button("Сохранить", "Btn.GhostSmall", null, SaveKey);
        var test = Ui.Button("Проверить", "Btn.GhostSmall", null, () => _ = Test());
        test.IsEnabled = st.IsSet;
        var items = new List<UIElement> { boxHost, save, test };
        if (st.IsSet)
        {
            items.Add(Ui.IconButton("trash", "Удалить ключ", () =>
            {
                if (!ConfirmDialog.Ask(_window, "Удалить ключ?", $"Ключ {st.Title} будет удалён с этого компьютера.", okText: "Удалить", danger: true)) return;
                Core.Secrets.Set(keyName, null);
            }, 32, 16));
        }
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) SaveKey(); };

        var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 12), Tag = keyName };
        sp.Children.Add(head);
        var row2 = Ui.Row(items, 6, 0);
        row2.Margin = new Thickness(0, 5, 0, 4);
        sp.Children.Add(row2);
        sp.Children.Add(status);
        _statusByKey[keyName] = status;
        return sp;
    }

    private readonly Dictionary<string, TextBlock> _statusByKey = new();

    /// <summary>После сохранения строки ключей перерисованы — проверяем ключ в новой строке.</summary>
    private async Task TestFresh(string keyName)
    {
        if (!_statusByKey.TryGetValue(keyName, out var status)) return;
        status.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        status.Text = "Сохранено. Проверяю…";
        try
        {
            var r = await Task.Run(() => Core.TestKeyAsync(keyName));
            status.Text = "✓ " + r;
            status.SetResourceReference(TextBlock.ForegroundProperty, "Ok");
        }
        catch (Exception e)
        {
            status.Text = "✗ " + e.Message;
            status.SetResourceReference(TextBlock.ForegroundProperty, "Err");
        }
    }

    // ================================================================== persona

    private Border PersonaCard()
    {
        var name = Text(s => s.UserName, (s, v) => s.UserName = v.Trim(), maxLength: 40);
        var persona = Combo(Personas.All.Values.Select(p => (p.Id, p.Label)), s => s.Persona, (s, v) => s.Persona = v);
        var prompt = Text(s => s.CustomPrompt, (s, v) => s.CustomPrompt = v, multiline: true,
            placeholder: "Например: я фронтенд-разработчик, отвечай с примерами на TypeScript");
        return Ui.Card(
            Ui.Title("💁 Характер"),
            Ui.Field("Как Вере тебя называть", name),
            Ui.Field("Характер", persona),
            Ui.Field("Дополнительные указания (системный промпт)", prompt));
    }

    // ================================================================== voice (TTS)

    private Border VoiceCard()
    {
        var engine = Combo(new[]
        {
            ("openai", "OpenAI TTS (живой голос, нужен ключ OpenAI)"),
            ("elevenlabs", "ElevenLabs (нужен ключ ElevenLabs)"),
            ("system", "Системный голос Windows (бесплатно)"),
            ("off", "Выключен"),
        }, s => s.Tts.Engine, (s, v) => s.Tts.Engine = v);
        var autoSpeak = Combo(new[]
        {
            ("voice", "Только на голосовые запросы"),
            ("always", "Всегда"),
            ("never", "Никогда (кроме напоминаний)"),
        }, s => s.Tts.AutoSpeak, (s, v) => s.Tts.AutoSpeak = v);

        // OpenAI
        var oVoice = Combo(Same("shimmer", "nova", "coral", "sage", "ballad", "alloy", "verse", "fable", "ash", "echo", "onyx"),
            s => s.Tts.OpenaiVoice, (s, v) => s.Tts.OpenaiVoice = v);
        var oModel = Combo(Same("gpt-4o-mini-tts", "tts-1", "tts-1-hd"), s => s.Tts.OpenaiModel, (s, v) => s.Tts.OpenaiModel = v);
        var instr = Text(s => s.Tts.OpenaiInstructions, (s, v) => s.Tts.OpenaiInstructions = v, multiline: true);
        var fromPersona = Ui.LinkButton("по характеру", () => Save(s => s.Tts.OpenaiInstructions = Personas.Get(s.Persona).TtsHint));
        var openaiSub = Ui.Sub(
            Ui.Row(new UIElement[] { Ui.Field("Голос", oVoice), Ui.Field("Модель", oModel) }, 8, 0, 1),
            Ui.Field("Интонация (инструкция для голоса)", instr, fromPersona));
        ShowIf(openaiSub, s => s.Tts.Engine == "openai");

        // ElevenLabs
        _elevenVoice = new ComboBox();
        _refreshers.Add(() =>
        {
            var cur = S.Tts.ElevenVoiceId ?? "";
            if (_elevenVoice.Items.Count == 0 || (cur.Length > 0 && !_elevenVoice.Items.OfType<ComboBoxItem>().Any(i => (i.Tag as string) == cur)))
            {
                var keep = _elevenVoice.Items.OfType<ComboBoxItem>().Where(i => (i.Tag as string)?.Length > 0).Select(i => ((string)i.Tag, (string)i.Content)).ToList();
                var opts = new List<(string, string)> { ("", "(по умолчанию)") };
                opts.AddRange(keep);
                if (cur.Length > 0 && keep.All(k => k.Item1 != cur)) opts.Add((cur, "текущий: " + cur));
                Ui.SetOptions(_elevenVoice, opts);
            }
            if (!_elevenVoice.IsDropDownOpen) Ui.Select(_elevenVoice, cur);
        });
        _elevenVoice.SelectionChanged += (_, _) =>
        {
            if (_loading) return;
            var v = Ui.Selected(_elevenVoice);
            if (v is not null && v != (S.Tts.ElevenVoiceId ?? "")) Save(s => s.Tts.ElevenVoiceId = v);
        };
        var elevenRefresh = Ui.IconButton("refresh", "Загрузить голоса", LoadElevenVoices);
        elevenRefresh.VerticalAlignment = VerticalAlignment.Bottom;
        elevenRefresh.Margin = new Thickness(0, 0, 0, 8);
        var eModel = Combo(new[]
        {
            ("eleven_multilingual_v2", "eleven_multilingual_v2 (качество)"),
            ("eleven_flash_v2_5", "eleven_flash_v2_5 (быстро)"),
            ("eleven_turbo_v2_5", "eleven_turbo_v2_5"),
        }, s => s.Tts.ElevenModel, (s, v) => s.Tts.ElevenModel = v);
        var elevenSub = Ui.Sub(
            Ui.Row(new UIElement[] { Ui.Field("Голос ElevenLabs", _elevenVoice), elevenRefresh }, 8, 0),
            Ui.Field("Модель", eModel));
        ShowIf(elevenSub, s => s.Tts.Engine == "elevenlabs");

        // системный
        var sysOpts = new List<(string, string)> { ("", "(авто, русский)") };
        sysOpts.AddRange(SystemVoice.Voices().Select(v => (v.Name, $"{v.Name} ({v.Language})")));
        _systemVoice = Combo(sysOpts, s => s.Tts.SystemVoice, (s, v) => s.Tts.SystemVoice = v);
        var sysNote = Ui.Muted("Русский голос «Irina» появляется после установки русского языкового пакета Windows (Параметры → Время и язык → Речь).");
        var systemSub = Ui.Sub(Ui.Field("Системный голос", _systemVoice), sysNote);
        ShowIf(systemSub, s => s.Tts.Engine == "system");

        var rate = SliderField("Скорость", 0.6, 1.6, 0.05, s => s.Tts.Rate, (s, v) => s.Tts.Rate = Math.Round(v, 2), Num2);
        var volume = SliderField("Громкость", 0, 1, 0.05, s => s.Tts.Volume, (s, v) => s.Tts.Volume = Math.Round(v, 2), Num2);

        var testText = new TextBox { Text = "Хе-хе, семпай~ Ну что, слышно меня? Только не влюбись!" };
        var testBtn = Ui.Button("Тест", "Btn.Primary", "play", () =>
        {
            if (S.Tts.Engine == "off")
            {
                _window.Toast("Голос выключен — выбери движок", true);
                return;
            }
            _host.SpeakTest(testText.Text);
        });
        var testRow = Ui.Row(new UIElement[] { testText, testBtn }, 8, 0);
        testRow.Margin = new Thickness(0, 6, 0, 0);

        return Ui.Card(
            Ui.Title("🔊 Голос Веры"),
            Ui.Field("Движок синтеза речи", engine),
            Ui.Field("Когда говорить вслух", autoSpeak),
            openaiSub, elevenSub, systemSub,
            rate, volume, testRow);
    }

    private async void LoadElevenVoices()
    {
        try
        {
            var voices = await Task.Run(() => Core.Speech.ElevenVoicesAsync());
            var opts = new List<(string, string)> { ("", "(по умолчанию)") };
            opts.AddRange(voices.Where(v => v.Id.Length > 0).Select(v => (v.Id, string.IsNullOrEmpty(v.Name) ? v.Id : v.Name)));
            _loading = true;
            try
            {
                Ui.SetOptions(_elevenVoice, opts);
                Ui.Select(_elevenVoice, S.Tts.ElevenVoiceId ?? "");
            }
            finally
            {
                _loading = false;
            }
            _window.Toast($"Голосов: {voices.Count}");
        }
        catch (Exception e)
        {
            _window.Toast(e.Message, true);
        }
    }

    // ================================================================== STT

    private Border SttCard()
    {
        var provider = Combo(new[] { ("openai", "OpenAI (ключ OpenAI)"), ("groq", "Groq Whisper (быстро и дёшево)") },
            s => s.Stt.Provider, (s, v) => s.Stt.Provider = v);
        var lang = Combo(new[] { ("ru", "русский"), ("en", "английский"), ("uk", "украинский"), ("", "авто") },
            s => s.Stt.Language, (s, v) => s.Stt.Language = v);
        var oModel = Combo(Same("gpt-4o-mini-transcribe", "gpt-4o-transcribe", "whisper-1"), s => s.Stt.OpenaiModel, (s, v) => s.Stt.OpenaiModel = v);
        var gModel = Combo(Same("whisper-large-v3-turbo", "whisper-large-v3"), s => s.Stt.GroqModel, (s, v) => s.Stt.GroqModel = v);
        var oField = Ui.Field("Модель OpenAI", oModel);
        var gField = Ui.Field("Модель Groq", gModel);
        ShowIf(oField, s => s.Stt.Provider != "groq");
        ShowIf(gField, s => s.Stt.Provider == "groq");
        var models = new Grid { Children = { oField, gField } };

        _mic = new ComboBox();
        FillMics();
        _refreshers.Add(() => { if (!_mic.IsDropDownOpen) Ui.Select(_mic, S.Voice.MicDeviceId ?? ""); });
        _mic.SelectionChanged += (_, _) =>
        {
            if (_loading) return;
            var v = Ui.Selected(_mic);
            if (v is not null && v != (S.Voice.MicDeviceId ?? "")) Save(s => s.Voice.MicDeviceId = v);
        };
        var micRefresh = Ui.IconButton("refresh", "Обновить список", () =>
        {
            _loading = true;
            try { FillMics(); Ui.Select(_mic, S.Voice.MicDeviceId ?? ""); }
            finally { _loading = false; }
        });

        var phrases = Text(s => s.Voice.WakePhrases, (s, v) => s.Voice.WakePhrases = v);
        var wakeSub = Ui.Sub(
            Ui.Field("Слова-активаторы (через запятую)", phrases),
            Ui.Note("Микрофон слушает постоянно, и каждая услышанная фраза отправляется на распознавание — это тратит баланс STT-сервиса. " +
                    "С Groq это почти бесплатно. Пока Вера говорит, она себя не слушает."));
        ShowIf(wakeSub, s => s.Voice.WakeWord);

        var wakeLabel = new TextBlock { TextWrapping = TextWrapping.Wrap };
        wakeLabel.Inlines.Add(new Run("Голосовая активация по слову "));
        wakeLabel.Inlines.Add(new Run("«Вера»") { FontWeight = FontWeights.Bold });
        var wake = new CheckBox { Content = wakeLabel };
        _refreshers.Add(() => wake.IsChecked = S.Voice.WakeWord);
        wake.Click += (_, _) => Save(s => s.Voice.WakeWord = wake.IsChecked == true);

        return Ui.Card(
            Ui.Title("🎙️ Распознавание речи и активация"),
            Ui.Row(new UIElement[] { Ui.Field("Сервис", provider), Ui.Field("Язык", lang) }, 8, 0, 1),
            models,
            Ui.Field("Микрофон", Ui.Row(new UIElement[] { _mic, micRefresh }, 8, 0)),
            SliderField("Чувствительность микрофона", 0, 1, 0.05, s => s.Voice.Sensitivity, (s, v) => s.Voice.Sensitivity = Math.Round(v, 2), Num2),
            SliderField("Пауза, после которой фраза считается законченной", 500, 2500, 100, s => s.Voice.SilenceMs,
                (s, v) => s.Voice.SilenceMs = (int)Math.Round(v), v => $"{v:0} мс"),
            Toggle("Звук начала/конца записи", s => s.Voice.Chime, (s, v) => s.Voice.Chime = v),
            wake,
            wakeSub);
    }

    private void FillMics()
    {
        var opts = new List<(string, string)> { ("", "По умолчанию") };
        opts.AddRange(Microphone.Devices().Distinct().Select(d => (d, d)));
        var cur = S.Voice.MicDeviceId ?? "";
        if (cur.Length > 0 && opts.All(o => o.Item1 != cur)) opts.Add((cur, cur + " (не подключён)"));
        Ui.SetOptions(_mic, opts);
    }

    // ================================================================== hotkeys

    private Border HotkeysCard()
    {
        var hint = Ui.Muted("Кликни в поле и нажми сочетание. Backspace — очистить.");
        hint.Margin = new Thickness(0, 2, 0, 4);
        _refreshers.Add(() =>
        {
            var errs = _host.HotkeyErrors;
            _hotkeyErrorsBox.Visibility = errs.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            _hotkeyErrors.Text = errs.Count > 0 ? "Не удалось назначить: " + string.Join("; ", errs) : "";
        });
        return Ui.Card(
            Ui.Title("⌨️ Горячие клавиши"),
            hint,
            Ui.Field("Показать / скрыть чат", HotkeyBox(s => s.Hotkeys.ToggleChat, (s, v) => s.Hotkeys.ToggleChat = v)),
            Ui.Field("Говорить с Верой (нажми ещё раз — закончить)", HotkeyBox(s => s.Hotkeys.Talk, (s, v) => s.Hotkeys.Talk = v)),
            Ui.Field("«Что у меня на экране?»", HotkeyBox(s => s.Hotkeys.Screenshot, (s, v) => s.Hotkeys.Screenshot = v)),
            _hotkeyErrorsBox);
    }

    private TextBox HotkeyBox(Func<VeraSettings, string?> get, Action<VeraSettings, string> set)
    {
        var tb = new TextBox
        {
            IsReadOnly = true,
            IsReadOnlyCaretVisible = false,
            Cursor = Cursors.Hand,
            FontFamily = Ui.Mono,
            TextAlignment = TextAlignment.Center,
        };
        var recording = false;
        _refreshers.Add(() => { if (!recording) tb.Text = Accelerator.Pretty(get(S)); });
        void Leave()
        {
            Keyboard.ClearFocus();
            FocusManager.SetFocusedElement(FocusManager.GetFocusScope(tb), null);
        }
        tb.GotKeyboardFocus += (_, _) =>
        {
            recording = true;
            tb.Text = "Нажми сочетание…";
            tb.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "Accent");
            _host.PauseHotkeys(true);
        };
        tb.LostKeyboardFocus += (_, _) =>
        {
            recording = false;
            tb.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "Fg");
            _host.PauseHotkeys(false);
            tb.Text = Accelerator.Pretty(get(S));
        };
        tb.PreviewKeyDown += (_, e) =>
        {
            e.Handled = true;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key is Key.Back or Key.Delete)
            {
                Save(s => set(s, ""));
                Leave();
                return;
            }
            if (key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None)
            {
                Leave();
                return;
            }
            var accel = Accelerator.FromKeyEvent(key, Keyboard.Modifiers, out var err);
            if (accel is null)
            {
                if (err is not null) tb.Text = err;
                return;
            }
            recording = false;
            Save(s => set(s, accel));
            Leave();
        };
        return tb;
    }

    // ================================================================== pet, reminders, capabilities

    private Border PetCard()
    {
        var hint = Ui.Muted("Перетаскивай Веру мышкой. Клик — чат, двойной клик — потыкать, правый клик — меню.");
        hint.Margin = new Thickness(0, 4, 0, 0);
        return Ui.Card(
            Ui.Title("🧸 Вера на рабочем столе"),
            Toggle("Показывать Веру", s => s.Pet.Visible, (s, v) => s.Pet.Visible = v),
            Toggle("Поверх всех окон", s => s.Pet.AlwaysOnTop, (s, v) => s.Pet.AlwaysOnTop = v),
            Toggle("Иногда болтает сама (без API)", s => s.Pet.IdleChatter, (s, v) => s.Pet.IdleChatter = v),
            SliderField("Размер", 2, 6, 1, s => s.Pet.Scale, (s, v) => s.Pet.Scale = (int)Math.Round(v), v => $"{v:0}×"),
            SliderField("Сколько показывать реплику", 3, 30, 1, s => s.Pet.BubbleSeconds, (s, v) => s.Pet.BubbleSeconds = (int)Math.Round(v), v => $"{v:0} с"),
            hint);
    }

    private Border RemindersCard() => Ui.Card(
        Ui.Title("⏰ Напоминания"),
        Toggle("Уведомление Windows", s => s.Reminders.Notification, (s, v) => s.Reminders.Notification = v),
        Toggle("Звуковой сигнал", s => s.Reminders.Sound, (s, v) => s.Reminders.Sound = v),
        Toggle("Вера проговаривает напоминание голосом", s => s.Reminders.Speak, (s, v) => s.Reminders.Speak = v));

    private Border CapabilitiesCard()
    {
        var sub = Ui.Sub(
            Toggle("Спрашивать подтверждение перед каждой командой", s => s.Capabilities.ConfirmCommands, (s, v) => s.Capabilities.ConfirmCommands = v),
            Ui.Note("Команды выполняются с твоими правами. Не отключай подтверждение, если не уверен."));
        ShowIf(sub, s => s.Capabilities.RunCommands);
        return Ui.Card(
            Ui.Title("🖥️ Что Вере можно делать"),
            Toggle("Открывать сайты, программы, папки, управлять звуком", s => s.Capabilities.PcControl, (s, v) => s.Capabilities.PcControl = v),
            Toggle("Смотреть на экран (скриншоты)", s => s.Capabilities.Screenshots, (s, v) => s.Capabilities.Screenshots = v),
            Toggle("Читать и записывать буфер обмена", s => s.Capabilities.Clipboard, (s, v) => s.Capabilities.Clipboard = v),
            Toggle("Выполнять команды PowerShell", s => s.Capabilities.RunCommands, (s, v) => s.Capabilities.RunCommands = v),
            sub);
    }

    // ================================================================== apps

    private Border AppsCard()
    {
        var hint = Ui.Muted("Вера сама ищет программы в меню «Пуск». Здесь можно добавить свои — «открой мою игру».");
        hint.Margin = new Thickness(0, 2, 0, 8);
        _apps = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        _refreshers.Add(RenderApps);
        _appName = new TextBox { Tag = "Название (как будешь говорить)" };
        var pick = Ui.Button("Файл…", "Btn.Ghost", "folder", PickApp);
        _appStatus = Ui.Muted("");
        _appStatus.Margin = new Thickness(0, 6, 0, 0);
        return Ui.Card(Ui.Title("🚀 Мои программы"), hint, _apps, Ui.Row(new UIElement[] { _appName, pick }, 8, 0), _appStatus);
    }

    private void RenderApps()
    {
        _apps.Children.Clear();
        var list = S.AppShortcuts;
        for (var i = 0; i < list.Count; i++)
        {
            var idx = i;
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(Ui.Text(list[i].Name, 13.5, "Fg", FontWeights.SemiBold));
            var path = Ui.Muted(list[i].Path, 11.5);
            path.TextWrapping = TextWrapping.NoWrap;
            path.TextTrimming = TextTrimming.CharacterEllipsis;
            text.Children.Add(path);
            var rm = Ui.IconButton("trash", "Удалить", () => Save(s =>
            {
                if (idx < s.AppShortcuts.Count) s.AppShortcuts.RemoveAt(idx);
            }), 30, 16);
            var frame = RemindersPanel.ItemFrame(Ui.Row(new UIElement[] { text, rm }, 8, 0));
            frame.Margin = new Thickness(0, 0, 0, 4);
            frame.Padding = new Thickness(10, 6, 6, 6);
            _apps.Children.Add(frame);
        }
    }

    private void PickApp()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Выбери программу или ярлык",
            Filter = "Программы и ярлыки|*.exe;*.lnk;*.url;*.bat;*.cmd;*.appref-ms|Все файлы|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog(_window) != true) return;
        var p = dlg.FileName;
        var name = _appName.Text.Trim();
        if (name.Length == 0) name = System.IO.Path.GetFileNameWithoutExtension(p);
        Save(s => s.AppShortcuts.Add(new AppShortcut { Name = name, Path = p }));
        _appName.Clear();
        _appStatus.Text = $"Добавлено: {name}";
    }

    // ================================================================== system

    private Border SystemCard()
    {
        var theme = Combo(new[] { ("auto", "Как в системе"), ("dark", "Тёмная"), ("light", "Светлая") }, s => s.Theme, (s, v) => s.Theme = v);
        var dataDir = _host.Storage.Directory;
        var open = Ui.Button("Папка данных", "Btn.Ghost", "folder", () =>
        {
            try { Process.Start(new ProcessStartInfo(dataDir) { UseShellExecute = true })?.Dispose(); }
            catch (Exception e) { _window.Toast(e.Message, true); }
        });
        var quit = Ui.Button("Выйти из Веры", "Btn.Ghost", "x", () =>
        {
            if (ConfirmDialog.Ask(_window, "Выйти из Веры?", "Напоминания не будут срабатывать, пока она выключена.", okText: "Выйти", danger: true))
                _host.Quit();
        });
        quit.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "Err");
        open.Margin = new Thickness(0, 0, 8, 8);
        quit.Margin = new Thickness(0, 0, 8, 8);
        var buttons = new WrapPanel { Margin = new Thickness(0, 8, 0, 0), Children = { open, quit } };
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        var about = Ui.Muted($"Вера v{v?.ToString(3) ?? "?"} · данные: {dataDir}");
        return Ui.Card(
            Ui.Title("⚙️ Система"),
            Toggle("Запускать вместе с Windows", s => s.Autostart, (s, val) => s.Autostart = val),
            Ui.Field("Тема", theme),
            buttons,
            about);
    }
}
