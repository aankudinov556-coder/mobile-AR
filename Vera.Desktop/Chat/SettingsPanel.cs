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
using Vera.Core.Speech;
using Vera.Core.Providers;
using Vera.LocalAI;
using Vera.Desktop.Audio;
using Vera.Desktop.Platform;
using Vera.Desktop.UI;

namespace Vera.Desktop.Chat;

/// <summary>Вкладка «Настройки»: разделы с поиском, готовые наборы, модели, голос, Вера, ПК, система.</summary>
public sealed partial class SettingsPanel : Grid
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
        welcomeText.Inlines.Add(new Run(L10n.T("Выбери, как Вера будет думать. ")));
        welcomeText.Inlines.Add(new Run(L10n.T("Бесплатно и офлайн")) { FontWeight = FontWeights.Bold });
        welcomeText.Inlines.Add(new Run(L10n.T(" — скачай модель в разделе «Локальный ИИ», ключи не нужны. ")));
        welcomeText.Inlines.Add(new Run(L10n.T("Через облако")) { FontWeight = FontWeights.Bold });
        welcomeText.Inlines.Add(new Run(L10n.T(" — вставь ключ ниже: например, OpenRouter (один ключ — сотни моделей). ") +
                                        L10n.T("Для живого голоса и распознавания — ключ OpenAI или Groq. Или просто нажми готовый набор ниже.")));
        var goLocal = Ui.Button("Скачать модель для ПК", "Btn.Primary", null, () => ShowSection("brain"));
        var goKeys = Ui.Button("У меня есть ключ", "Btn.Ghost", null, () => ShowSection("keys"));
        goLocal.Margin = new Thickness(0, 0, 8, 0);
        var welcomeButtons = new WrapPanel { Margin = new Thickness(0, 10, 0, 0), Children = { goLocal, goKeys } };
        _welcome = Ui.Card(Ui.Title("Добро пожаловать! 👋"), welcomeText, welcomeButtons);
        _welcome.SetResourceReference(Border.BorderBrushProperty, "Accent");
        _welcome.SetResourceReference(Border.BackgroundProperty, "AccentSoft");
        root.Children.Add(NavBar());
        root.Children.Add(_welcome);

        // разделы как в концепте «Ореол»
        Add(root, ModelCard(), "brain");
        Add(root, LocalCard(), "brain");
        Add(root, PresetsCard(), "brain");
        Add(root, SearchCard(), "brain");
        Add(root, VoiceCard(), "voice");
        Add(root, SttCard(), "voice");

        _hotkeyErrors = Ui.Text("", 12, "Err");
        _hotkeyErrorsBox = Ui.Note("", error: true);
        _hotkeyErrorsBox.Child = _hotkeyErrors;
        Add(root, PersonaCard(), "look");
        Add(root, PetCard(), "look");
        Add(root, SyncCard(), "sync");
        Add(root, CapabilitiesCard(), "caps");
        Add(root, RemindersCard(), "caps");
        Add(root, AppsCard(), "caps");
        Add(root, KeysCard(), "keys");
        Add(root, HotkeysCard(), "system");
        Add(root, UsageCard(), "system");
        Add(root, SystemCard(), "system");

        _scroll = Ui.Scroll(root, 24);
        var side = SideNav();
        var aside = Aside();
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        SetColumn(side, 0);
        SetColumn(_scroll, 1);
        SetColumn(aside, 2);
        Children.Add(side);
        Children.Add(_scroll);
        Children.Add(aside);
        SizeChanged += (_, _) => Relayout();
        Loaded += (_, _) => Relayout();
        RenderKeys();
        Refresh();
        ShowSection(_section);
    }

    private VeraCore Core => _host.Core;
    private VeraSettings S => _host.Core.Settings.Current;

    // ================================================================== binding helpers

    private void Save(Action<VeraSettings> change)
    {
        if (_loading) return;
        try
        {
            Core.Settings.Update(change);
            ReactSaved();
        }
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
        // «Мозг Веры»: три большие карточки — облако, на этом ПК, свой сервер
        var cloudIds = new[] { "openrouter", "anthropic", "openai" };
        var brains = new UniformGrid { Columns = 3, Margin = new Thickness(0, 4, 0, 6) };
        foreach (var (kind, title, about) in new[]
                 {
                     ("cloud", "Облако", "OpenRouter, Anthropic или OpenAI. Умнее всего, нужен ключ."),
                     ("local", "На этом ПК", "Бесплатно и без интернета. Переписка не уходит с компьютера."),
                     ("custom", "Свой сервер", "Ollama, LM Studio — любая модель с OpenAI-совместимым адресом."),
                 })
        {
            var head = Ui.Heading(title, 15.5, FontWeights.Bold);
            var text = Ui.Text(about, 12.5, "Muted");
            text.Margin = new Thickness(0, 6, 0, 0);
            // нимб над выбранной карточкой
            var halo = new System.Windows.Shapes.Ellipse
            {
                Width = 34, Height = 11, StrokeThickness = 3, Stroke = Brushes.Black,
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, -21, 8, 0), Visibility = Visibility.Collapsed, IsHitTestVisible = false,
            };
            var body = new Grid { Children = { new StackPanel { Children = { head, text } }, halo } };
            var rb = new RadioButton
            {
                Content = body,
                GroupName = "brain",
                Style = Ui.Style("Btn.Base"),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Top,
                Padding = new Thickness(16, 14, 16, 16),
                BorderThickness = new Thickness(2),
                Margin = new Thickness(0, 6, 12, 8),
                MinHeight = 118,
            };
            Ui.SetRadius(rb, new CornerRadius(18));
            var k = kind;
            void Paint()
            {
                var on = rb.IsChecked == true;
                rb.SetResourceReference(Control.BackgroundProperty, on ? "Accent" : "Bg2");
                if (on) rb.BorderBrush = Brushes.Black; else rb.SetResourceReference(Control.BorderBrushProperty, "Line");
                rb.Effect = on ? Anim.HardShadow(4) : null;
                head.SetResourceReference(TextBlock.ForegroundProperty, on ? "OnAccent" : "Fg");
                text.SetResourceReference(TextBlock.ForegroundProperty, on ? "OnAccent" : "Muted");
                halo.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
                if (on) Anim.Pop(halo, 300, 0.4);
            }
            rb.Checked += (_, _) =>
            {
                Paint();
                if (_loading) return;
                var target = k switch
                {
                    "local" => "local",
                    "custom" => "custom",
                    _ => cloudIds.Contains(S.Provider) ? S.Provider : cloudIds.FirstOrDefault(Core.Secrets.Has) ?? "openrouter",
                };
                if (target == S.Provider) return;
                _model.Items.Clear();
                Save(st => st.Provider = target);
            };
            rb.Unchecked += (_, _) => Paint();
            _refreshers.Add(() =>
            {
                var cur = S.Provider == "local" ? "local" : S.Provider == "custom" ? "custom" : "cloud";
                if ((cur == k) != (rb.IsChecked == true)) rb.IsChecked = cur == k;
                Paint();
            });
            brains.Children.Add(rb);
        }

        // облачный провайдер — переключатель под карточками
        var seg = new UniformGrid { Columns = 3 };
        foreach (var (id, title, _) in ProviderRegistry.Known.Where(x => cloudIds.Contains(x.Id)))
        {
            var rb = Ui.Radio(title, "provider", "Segment");
            rb.Margin = new Thickness(0, 0, 8, 0);
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
        var segBox = seg;
        var cloudField = Ui.Field("Сервис", segBox);
        ShowIf(cloudField, st => cloudIds.Contains(st.Provider));
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
            _model.Text = ci.Tag as string ?? ci.Content as string ?? "";
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

        // свой сервер (Ollama, LM Studio, llama-server, vLLM…)
        var customUrl = Text(s => s.CustomBaseUrl, (s, v) => s.CustomBaseUrl = v.Trim(), placeholder: "http://localhost:11434/v1");
        var customSub = Ui.Sub(
            Ui.Field("Адрес OpenAI-совместимого сервера", customUrl),
            Ui.Muted("Ollama: http://localhost:11434/v1 · LM Studio: http://localhost:1234/v1. Ключ (если нужен) — в разделе ключей, строка «Свой сервер»."));
        ShowIf(customSub, s => s.Provider == "custom");
        var localSub = Ui.Sub(
            Ui.Muted("Модель работает прямо на этом ПК. Скачать или выбрать модель — в разделе «Локальный ИИ»."),
            Ui.Muted("Модели на этом ПК — карточка ниже."));
        ShowIf(localSub, s => s.Provider == "local");
        modelBox.Children.Add(customSub);

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

        var modelField = Ui.Field("Модель", modelBox);
        ShowIf(modelField, st => st.Provider != "local");
        var inner = Ui.Card(
            cloudField,
            modelField,
            localSub,
            Ui.Field("Глубина размышлений", effort),
            Toggle("Разбор команд из текста", s => s.ToolCallFallback, (s, v) => s.ToolCallFallback = v,
                "(если модель пишет команду JSON-ом — выполнить её; для моделей без инструментов)"),
            Ui.Field("Сообщений истории в контексте", history));
        // карточки «мозга» — над настройками модели, без общей рамки
        var card = new Border { Child = new StackPanel { Children = { brains, inner } } };
        return card;
    }

    private static string Price(ModelInfo m)
    {
        if (m.InputPerMTok is not { } i || m.OutputPerMTok is not { } o) return "";
        if (i == 0 && o == 0) return " · бесплатно";
        return string.Create(CultureInfo.InvariantCulture, $" · ${i:0.##}/${o:0.##}");
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
            // модели с инструментами — первыми; без них Вера не сможет ставить напоминания и открывать программы
            var rows = list.OrderByDescending(m => m.Tools).ThenBy(m => m.Id, StringComparer.OrdinalIgnoreCase).ToList();
            _model.Items.Clear();
            foreach (var m in rows)
            {
                var marks = (m.Tools ? " 🛠" : "") + (m.Vision ? " 👁" : "") + Price(m);
                var label = provider == "local" ? m.Name : m.Id;
                var item = new ComboBoxItem
                {
                    Content = label + marks,
                    Tag = m.Id,
                    ToolTip = m.Name + (m.Tools ? " · 🛠 умеет команды" : " · без инструментов (только текст)") + (m.Vision ? " · 👁 видит картинки" : ""),
                };
                TextSearch.SetText(item, m.Id);
                _model.Items.Add(item);
            }
            _model.Text = S.CurrentModel;
            _window.Toast(L10n.F("Моделей: {0} (🛠 — умеет команды, 👁 — видит картинки, цена — $ за 1 млн токенов: запрос/ответ).", rows.Count));
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
        _welcome.Visibility = statuses.Any(s => s.IsSet) || (S.Provider == "local" && _host.Local?.Llm.ResolveModelPath(S.CurrentModel) is not null)
            ? Visibility.Collapsed : Visibility.Visible;
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
            status.Text = L10n.T("Проверяю…");
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
                if (!ConfirmDialog.Ask(_window, L10n.T("Удалить ключ?"), L10n.F("Ключ {0} будет удалён с этого компьютера.", st.Title), okText: L10n.T("Удалить"), danger: true)) return;
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
        status.Text = L10n.T("Сохранено. Проверяю…");
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
        var lang = Ui.Combo(new[] { ("ru", "RU · Русский"), ("en", "EN · English") });
        _refreshers.Add(() => { if (!lang.IsDropDownOpen) Ui.Select(lang, S.Language == "en" ? "en" : "ru"); });
        lang.SelectionChanged += (_, _) =>
        {
            if (_loading) return;
            var v = Ui.Selected(lang);
            if (v is null || v == (S.Language == "en" ? "en" : "ru")) return;
            Save(s => s.Language = v);
            // язык меняется целиком только после перезапуска — спрашиваем на выбранном языке
            var en = v == "en";
            if (ConfirmDialog.Ask(_window, en ? "Restart Vera?" : "Перезапустить Веру?",
                    en ? "The interface language changes after a restart. Restart now?" : "Язык интерфейса сменится после перезапуска. Перезапустить сейчас?",
                    okText: en ? "Restart" : "Перезапустить", cancelText: en ? "Later" : "Позже"))
                _host.Restart();
        };
        return Ui.Card(
            Ui.Title("💁 Характер"),
            Ui.Field("Язык / Language", lang),
            Ui.Field("Как Вере тебя называть", name),
            Ui.Field("Город (погода на главном экране и в сводке)", Text(s => s.City, (s, v) => s.City = v.Trim(), placeholder: "Москва", maxLength: 60)),
            Ui.Field("Характер", persona),
            Ui.Field("Дополнительные указания (системный промпт)", prompt));
    }

    // ================================================================== reply sound (вместо синтеза речи)

    private const string SoundSample = "Поставила на 18:00. Опоздаешь — оформлю служебную записку, а пока свободен~";

    private Border VoiceCard()
    {
        var list = new StackPanel();
        foreach (var (id, title, about) in ReplySound.Styles)
        {
            var head = Ui.Text(title, 14.5, "Fg", FontWeights.Bold);
            var text = Ui.Muted(about, 12.5);
            var dot = new System.Windows.Shapes.Ellipse { Width = 18, Height = 18, StrokeThickness = 2.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
            var row = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(dot, Dock.Left);
            row.Children.Add(dot);
            row.Children.Add(new StackPanel { Children = { head, text } });
            var rb = new RadioButton
            {
                Content = row,
                GroupName = "replySound",
                Style = Ui.Style("Btn.Base"),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(14, 11, 14, 11),
                BorderThickness = new Thickness(2),
                Margin = new Thickness(0, 0, 0, 8),
            };
            Ui.SetRadius(rb, new CornerRadius(14));
            void Paint()
            {
                var on = rb.IsChecked == true;
                rb.SetResourceReference(Control.BackgroundProperty, on ? "Bg3" : "Bg2");
                rb.SetResourceReference(Control.BorderBrushProperty, on ? "Accent" : "Line");
                rb.Effect = on ? Anim.HardShadow(3) : null;
                dot.Stroke = on ? Brushes.Black : (Brush)(TryFindResource("Muted") ?? Brushes.Gray);
                if (on) dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "Accent"); else dot.Fill = Brushes.Transparent;
            }
            var sid = id;
            rb.Checked += (_, _) =>
            {
                Paint();
                if (_loading) return;
                if (S.Sound.Style != sid) Save(st => st.Sound.Style = sid);
                if (sid != "off") _host.SpeakTest(L10n.T(SoundSample));
            };
            rb.Unchecked += (_, _) => Paint();
            _refreshers.Add(() =>
            {
                var cur = ReplySound.Styles.Any(x => x.Id == S.Sound.Style) ? S.Sound.Style : "pixel";
                if ((cur == sid) != (rb.IsChecked == true)) rb.IsChecked = cur == sid;
                Paint();
            });
            list.Children.Add(rb);
        }

        var vol = new UniformGrid { Columns = 3 };
        foreach (var (id, title) in ReplySound.Volumes)
        {
            var rb = Ui.Radio(title, "replyVolume", "Segment");
            rb.Margin = new Thickness(0, 0, 8, 0);
            var vid = id;
            rb.Checked += (_, _) =>
            {
                if (_loading || S.Sound.Volume == vid) return;
                Save(st => st.Sound.Volume = vid);
                _host.SpeakTest(L10n.T(SoundSample));
            };
            _refreshers.Add(() => { if ((S.Sound.Volume == vid) != (rb.IsChecked == true)) rb.IsChecked = S.Sound.Volume == vid; });
            vol.Children.Add(rb);
        }

        var test = Ui.Button("Послушать", "Btn.Primary", "play", () =>
        {
            if (S.Sound.Style == "off")
            {
                _window.Toast("Звук ответа выключен — выбери вариант", true);
                return;
            }
            _host.SpeakTest(L10n.T(SoundSample));
        });
        test.HorizontalAlignment = HorizontalAlignment.Left;
        test.Margin = new Thickness(0, 6, 0, 0);

        return Ui.Card(
            Ui.Title("🔊 Звук ответа"),
            Ui.Muted("Пока Вера печатает ответ, звучит тихий «голосок» в такт буквам. Тон подстраивается под характер.", 12.5),
            list,
            Ui.Field("Громкость", vol),
            test);
    }

    // ================================================================== STT

    private Border SttCard()
    {
        var provider = Combo(new[]
            {
                ("stream", "На этом ПК, быстро — слова видны, пока говоришь (рекомендую)"),
                ("openai", "OpenAI (ключ OpenAI)"),
                ("groq", "Groq Whisper (быстро и дёшево)"),
                ("whisper", "Whisper на этом ПК (офлайн, точнее, но медленнее)"),
            },
            s => s.Stt.Provider, (s, v) => s.Stt.Provider = v);
        var lang = Combo(new[] { ("ru", "русский"), ("en", "английский"), ("uk", "украинский"), ("", "авто") },
            s => s.Stt.Language, (s, v) => s.Stt.Language = v);
        var oModel = Combo(Same("gpt-4o-mini-transcribe", "gpt-4o-transcribe", "whisper-1"), s => s.Stt.OpenaiModel, (s, v) => s.Stt.OpenaiModel = v);
        var gModel = Combo(Same("whisper-large-v3-turbo", "whisper-large-v3"), s => s.Stt.GroqModel, (s, v) => s.Stt.GroqModel = v);
        var oField = Ui.Field("Модель OpenAI", oModel);
        var gField = Ui.Field("Модель Groq", gModel);
        ShowIf(oField, s => s.Stt.Provider == "openai");
        ShowIf(gField, s => s.Stt.Provider == "groq");
        var whisperSub = Ui.Sub(Ui.Muted("Модель распознавания работает на процессоре. small — лучший баланс для русского."), WhisperRows());
        ShowIf(whisperSub, s => s.Stt.Provider == "whisper");
        var streamSub = Ui.Sub(
            Ui.Muted("Текст готов почти сразу после паузы — без интернета и ключей. Нужна модель для языка распознавания.", 12),
            DownloadRow(ModelCatalog.StreamFor("ru")!, onInstalled: () => Local?.WarmUpStreaming(), badge: "⭐ русский"),
            DownloadRow(ModelCatalog.StreamFor("en")!, onInstalled: () => Local?.WarmUpStreaming(), badge: "English"));
        ShowIf(streamSub, s => s.Stt.Provider == "stream");
        var models = new StackPanel { Children = { new Grid { Children = { oField, gField } }, whisperSub, streamSub } };

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
        var wakeEngine = Combo(new[]
        {
            ("vosk", "Vosk — офлайн, бесплатно (рекомендую)"),
            ("stt", "Через сервис распознавания (каждая фраза — в облако)"),
        }, s => s.Voice.WakeEngine, (s, v) => s.Voice.WakeEngine = v);
        var sttNote = Ui.Note(L10n.T("Микрофон слушает постоянно, и каждая услышанная фраза отправляется на распознавание — это тратит баланс STT-сервиса. ") +
                              L10n.T("С Groq это почти бесплатно. Пока Вера говорит, она себя не слушает."));
        ShowIf(sttNote, s => s.Voice.WakeEngine != "vosk");
        var voskRow = DownloadRow(ModelCatalog.Get("vosk-small-ru")!, onInstalled: () => _host.Voice.SyncSettings());
        ShowIf(voskRow, s => s.Voice.WakeEngine == "vosk");
        var wakeSub = Ui.Sub(
            Ui.Field("Чем слушать имя", wakeEngine),
            voskRow,
            Ui.Field("Слова-активаторы (через запятую)", phrases),
            sttNote);
        ShowIf(wakeSub, s => s.Voice.WakeWord);
        var vad = Combo(new[]
        {
            ("silero", "Нейросеть Silero — не путает речь с шумом (рекомендую)"),
            ("energy", "По громкости (как раньше)"),
        }, s => s.Voice.Vad, (s, v) => s.Voice.Vad = v);

        var wakeLabel = new TextBlock { TextWrapping = TextWrapping.Wrap };
        wakeLabel.Inlines.Add(new Run(L10n.T("Голосовая активация по слову ")));
        wakeLabel.Inlines.Add(new Run(L10n.T("«Вера»")) { FontWeight = FontWeights.Bold });
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
                (s, v) => s.Voice.SilenceMs = (int)Math.Round(v), v => L10n.F("{0:0} мс", v)),
            Ui.Field("Как понять, что ты говоришь", vad),
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
            tb.Text = L10n.T("Нажми сочетание…");
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
            Ui.Field("Скин", Combo(SkinOptions(), s => s.Skin, (s, v) => s.Skin = v)),
            Toggle("Иногда болтает сама (без API)", s => s.Pet.IdleChatter, (s, v) => s.Pet.IdleChatter = v),
            Toggle("Садится на пол, когда скучно", s => s.Pet.SitWhenBored, (s, v) => s.Pet.SitWhenBored = v),
            SliderField(L10n.T("Через сколько садится"), 1, 30, 1, s => s.Pet.SitAfterMinutes, (s, v) => s.Pet.SitAfterMinutes = (int)Math.Round(v), v => L10n.F("{0:0} мин", v)),
            SliderField(L10n.T("Размер"), 2, 6, 1, s => s.Pet.Scale, (s, v) => s.Pet.Scale = (int)Math.Round(v), v => $"{v:0}×"),
            SliderField(L10n.T("Сколько показывать реплику"), 3, 30, 1, s => s.Pet.BubbleSeconds, (s, v) => s.Pet.BubbleSeconds = (int)Math.Round(v), v => L10n.F("{0:0} с", v)),
            hint);
    }

    private Border RemindersCard() => Ui.Card(
        Ui.Title("⏰ Напоминания"),
        Toggle("Уведомление Windows", s => s.Reminders.Notification, (s, v) => s.Reminders.Notification = v),
        Toggle("Звуковой сигнал", s => s.Reminders.Sound, (s, v) => s.Reminders.Sound = v),
        Toggle("«Голосок» Веры вместе с напоминанием", s => s.Reminders.Speak, (s, v) => s.Reminders.Speak = v));

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
        _appName = new TextBox { Tag = L10n.T("Название (как будешь говорить)") };
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
            Title = L10n.T("Выбери программу или ярлык"),
            Filter = "Программы и ярлыки|*.exe;*.lnk;*.url;*.bat;*.cmd;*.appref-ms|Все файлы|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog(_window) != true) return;
        var p = dlg.FileName;
        var name = _appName.Text.Trim();
        if (name.Length == 0) name = System.IO.Path.GetFileNameWithoutExtension(p);
        Save(s => s.AppShortcuts.Add(new AppShortcut { Name = name, Path = p }));
        _appName.Clear();
        _appStatus.Text = L10n.F("Добавлено: {0}", name);
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
        var about = Ui.Muted(L10n.F("Вера v{0} · данные: {1}", v?.ToString(3) ?? "?", dataDir));
        return Ui.Card(
            Ui.Title("⚙️ Система"),
            Toggle("Запускать вместе с Windows", s => s.Autostart, (s, val) => s.Autostart = val),
            Ui.Field("Тема", theme),
            buttons,
            TransferButtons(),
            about);
    }
}
