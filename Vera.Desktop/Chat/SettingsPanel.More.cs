using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Vera.Core;
using Vera.Core.Sprites;
using Vera.Desktop.UI;
using Vera.LocalAI;
using Vera.LocalAI.Llm;

namespace Vera.Desktop.Chat;

/// <summary>
/// Новые разделы настроек v2.2: навигация и поиск, готовые наборы, локальный ИИ (скачивание моделей),
/// поиск в интернете, расход, перенос настроек.
/// </summary>
public sealed partial class SettingsPanel
{
    private sealed record Section(string Id, string Title);

    private static readonly Section[] Sections =
    {
        new("all", "Все"),
        new("main", "Модель и ключи"),
        new("local", "Локальный ИИ"),
        new("voice", "Голос"),
        new("vera", "Вера"),
        new("pc", "ПК"),
        new("system", "Система"),
    };

    private sealed record CardEntry(Border Card, string Section, string Keywords);

    private readonly List<CardEntry> _cards = new();
    private readonly Dictionary<string, Button> _sectionButtons = new();
    private string _section = "all";
    private TextBox _search = null!;

    /// <summary>Идущие загрузки моделей (живут дольше, чем перерисовка настроек).</summary>
    private static readonly Dictionary<string, (CancellationTokenSource Cts, DownloadProgress? Last)> Downloads = new();

    private LocalAiService? Local => _host.Local;

    // ================================================================== navigation + search

    private UIElement NavBar()
    {
        _search = new TextBox { Tag = "🔍 Найти настройку: голос, ключ, модель…" };
        _search.TextChanged += (_, _) => ApplyFilter();
        var chips = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        foreach (var s in Sections)
        {
            var b = new Button { Content = s.Title, Style = Ui.Style("Btn.Chip"), Margin = new Thickness(0, 0, 6, 6) };
            var id = s.Id;
            b.Click += (_, _) => { _section = id; ApplyFilter(); };
            _sectionButtons[id] = b;
            chips.Children.Add(b);
        }
        var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
        sp.Children.Add(_search);
        sp.Children.Add(chips);
        return sp;
    }

    private Border Add(StackPanel root, Border card, string section)
    {
        root.Children.Add(card);
        _cards.Add(new CardEntry(card, section, Words(card).ToLowerInvariant()));
        return card;
    }

    /// <summary>Все тексты карточки — для поиска.</summary>
    private static string Words(DependencyObject root)
    {
        var sb = new System.Text.StringBuilder();
        void Walk(object o)
        {
            switch (o)
            {
                case TextBlock tb:
                    sb.Append(' ').Append(tb.Text);
                    foreach (var r in tb.Inlines.OfType<Run>()) sb.Append(' ').Append(r.Text);
                    break;
                case ContentControl { Content: string s }:
                    sb.Append(' ').Append(s);
                    break;
            }
            if (o is ComboBox cb) foreach (var it in cb.Items) Walk(it);
            if (o is DependencyObject d)
                foreach (var child in LogicalTreeHelper.GetChildren(d)) Walk(child);
        }
        Walk(root);
        return sb.ToString();
    }

    private void ApplyFilter()
    {
        var q = (_search?.Text ?? "").Trim().ToLowerInvariant();
        foreach (var c in _cards)
        {
            var inSection = q.Length > 0 || _section == "all" || c.Section == _section;
            var match = q.Length == 0 || q.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(w => c.Keywords.Contains(w));
            c.Card.Visibility = inSection && match ? Visibility.Visible : Visibility.Collapsed;
        }
        foreach (var (id, b) in _sectionButtons)
        {
            var active = id == _section && q.Length == 0;
            if (active)
            {
                b.SetResourceReference(Control.BackgroundProperty, "Accent");
                b.Foreground = Brushes.White;
            }
            else
            {
                b.SetResourceReference(Control.BackgroundProperty, "Bg3");
                b.SetResourceReference(Control.ForegroundProperty, "Muted");
            }
        }
    }

    /// <summary>Открыть раздел настроек (например, из подсказки «скачай модель»).</summary>
    public void ShowSection(string id)
    {
        _section = Sections.Any(s => s.Id == id) ? id : "all";
        if (_search is not null) _search.Text = "";
        ApplyFilter();
    }

    // ================================================================== presets / first run

    private Border PresetsCard()
    {
        var sp = new StackPanel();
        foreach (var p in SettingsPresets.All)
        {
            var preset = p;
            var title = Ui.Text(p.Title, 13.5, "Fg", FontWeights.SemiBold);
            var about = Ui.Muted(p.About, 11.5);
            var body = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            body.Children.Add(title);
            body.Children.Add(about);
            var apply = Ui.Button("Включить", "Btn.GhostSmall", null, () => ApplyPreset(preset));
            apply.VerticalAlignment = VerticalAlignment.Center;
            var frame = RemindersPanel.ItemFrame(Ui.Row(new UIElement[] { body, apply }, 10, 0));
            frame.Margin = new Thickness(0, 0, 0, 6);
            sp.Children.Add(frame);
        }
        var note = Ui.Muted("Набор меняет провайдера, голос и распознавание. Остальное (характер, клавиши, напоминания) не трогается.");
        note.Margin = new Thickness(0, 4, 0, 0);
        return Ui.Card(Ui.Title("🎛️ Готовые наборы"), sp, note);
    }

    private void ApplyPreset(SettingsPresets.Preset p)
    {
        Save(p.Apply);
        var missing = SettingsPresets.MissingKeys(p.Id, Core.Secrets.Has);
        if (missing.Count > 0)
        {
            var titles = Core.Secrets.Status().Where(s => missing.Contains(s.Name)).Select(s => s.Title);
            _window.Toast("Включено. Осталось добавить ключи: " + string.Join(", ", titles), true);
            ShowSection("main");
        }
        else if (p.Id == "offline")
        {
            var need = OfflineMissing();
            _window.Toast(need.Count == 0 ? "Всё офлайн и готово~" : "Включено. Осталось скачать: " + string.Join(", ", need), need.Count > 0);
            if (need.Count > 0) ShowSection("local");
        }
        else _window.Toast("Набор включён");
    }

    /// <summary>Чего не хватает для режима «офлайн».</summary>
    private List<string> OfflineMissing()
    {
        var list = new List<string>();
        if (Local is not { } l) return new List<string> { "модуль локального ИИ не загрузился (см. error.log)" };
        if (l.Llm.ResolveModelPath(S.CurrentModel) is null) list.Add("языковую модель");
        if (l.WhisperModelPath() is null) list.Add("Whisper");
        if (!l.PiperReady(out _)) list.Add("голос Piper");
        if (!l.Store.IsInstalled("vosk-small-ru")) list.Add("Vosk");
        return list;
    }

    // ================================================================== download row

    /// <summary>
    /// Строка «модель в каталоге»: название, размер, состояние, прогресс и кнопки Скачать / Отмена / Удалить / Выбрать.
    /// </summary>
    private FrameworkElement DownloadRow(AssetInfo a, Func<VeraSettings, bool>? isSelected = null, Action<VeraSettings>? select = null,
        string? badge = null, Action? onInstalled = null)
    {
        var store = Local?.Store;
        var title = Ui.Text(a.Title + (badge is null ? "" : "  " + badge), 13, "Fg", FontWeights.SemiBold);
        var size = a.SizeMB >= 1000 ? $"{a.SizeMB / 1024.0:0.#} ГБ" : $"{a.SizeMB} МБ";
        var marks = a.Kind == AssetKind.Llm
            ? $" · память от {a.MinRamGB} ГБ · команды {Stars(a.ToolsQuality)} · русский {Stars(a.RussianQuality)}"
            : "";
        var about = Ui.Muted(a.Note, 11.5);
        var info = Ui.Muted(size + marks, 11);
        var status = Ui.Muted("", 11.5);
        var bar = new ProgressBar { Height = 5, Minimum = 0, Maximum = 1, Margin = new Thickness(0, 5, 0, 2), Visibility = Visibility.Collapsed };
        bar.SetResourceReference(Control.ForegroundProperty, "Accent");
        bar.SetResourceReference(Control.BackgroundProperty, "Bg4");
        bar.BorderThickness = new Thickness(0);

        var body = new StackPanel();
        body.Children.Add(title);
        body.Children.Add(about);
        body.Children.Add(info);
        body.Children.Add(bar);
        body.Children.Add(status);

        var download = Ui.Button("Скачать", "Btn.GhostSmall", null, null);
        var cancel = Ui.Button("Отмена", "Btn.GhostSmall", null, null);
        var remove = Ui.IconButton("trash", "Удалить с диска", null, 30, 15);
        var choose = Ui.Button("Выбрать", "Btn.GhostSmall", null, null);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var b in new FrameworkElement[] { choose, download, cancel, remove })
        {
            b.Margin = new Thickness(4, 0, 0, 0);
            buttons.Children.Add(b);
        }

        void Update()
        {
            var installed = store?.IsInstalled(a) == true;
            var active = Downloads.TryGetValue(a.Id, out var d);
            download.Visibility = !installed && !active ? Visibility.Visible : Visibility.Collapsed;
            cancel.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
            remove.Visibility = installed && !active ? Visibility.Visible : Visibility.Collapsed;
            var selected = isSelected?.Invoke(S) == true;
            choose.Visibility = installed && select is not null && !selected ? Visibility.Visible : Visibility.Collapsed;
            bar.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
            if (active && d.Last is { } p)
            {
                bar.Value = p.Fraction;
                status.Text = p.Stage == "extract"
                    ? "Распаковываю…"
                    : $"{p.Done / 1048576.0:0} из {Math.Max(p.Total, 1) / 1048576.0:0} МБ" + (p.BytesPerSecond > 0 ? $" · {p.BytesPerSecond / 1048576.0:0.0} МБ/с" : "");
            }
            else if (active) status.Text = "Подключаюсь…";
            else if (installed) status.Text = selected ? "✓ Скачано и выбрано" : "✓ Скачано";
            else if (status.Tag is not string) status.Text = "";
            status.SetResourceReference(TextBlock.ForegroundProperty, installed ? "Ok" : status.Tag is string ? "Err" : "Muted");
        }

        download.Click += async (_, _) =>
        {
            if (store is null) return;
            if (Downloads.ContainsKey(a.Id)) return;
            var cts = new CancellationTokenSource();
            Downloads[a.Id] = (cts, null);
            status.Tag = null;
            Update();
            var progress = new Progress<DownloadProgress>(p =>
            {
                if (Downloads.ContainsKey(a.Id)) Downloads[a.Id] = (cts, p);
                Update();
            });
            try
            {
                await Task.Run(() => store.InstallAsync(a, progress, cts.Token));
                Downloads.Remove(a.Id);
                if (select is not null && isSelected?.Invoke(S) != true && onInstalled is null) Save(select);
                onInstalled?.Invoke();
                _window.Toast($"Скачано: {a.Title}");
            }
            catch (OperationCanceledException)
            {
                Downloads.Remove(a.Id);
                status.Text = "Отменено — при следующем нажатии докачаю с того же места.";
            }
            catch (Exception e)
            {
                Downloads.Remove(a.Id);
                status.Tag = "error";
                status.Text = "✗ " + (e is System.Net.Http.HttpRequestException ? "Нет связи с сервером: " + e.Message : e.Message);
                Log.Error("download " + a.Id, e);
            }
            finally
            {
                cts.Dispose();
                Update();
                Refresh();
            }
        };
        cancel.Click += (_, _) =>
        {
            if (Downloads.TryGetValue(a.Id, out var d)) d.Cts.Cancel();
        };
        remove.Click += (_, _) =>
        {
            if (store is null) return;
            if (!ConfirmDialog.Ask(_window, "Удалить с диска?", $"«{a.Title}» ({size}) будет удалено. Скачать можно снова в любой момент.", okText: "Удалить", danger: true))
                return;
            try
            {
                if (a.Kind == AssetKind.Llm) Local?.Llm.Engine.Unload();
                store.Delete(a);
            }
            catch (Exception e) { _window.Toast("Не удалось удалить: " + e.Message, true); }
            Update();
            Refresh();
        };
        choose.Click += (_, _) =>
        {
            if (select is null) return;
            Save(select);
            Update();
        };
        _refreshers.Add(Update);

        var frame = RemindersPanel.ItemFrame(Ui.Row(new UIElement[] { body, buttons }, 8, 0));
        frame.Margin = new Thickness(0, 0, 0, 6);
        frame.Padding = new Thickness(10, 8, 8, 8);
        return frame;
    }

    private static string Stars(int n) => new string('★', Math.Clamp(n, 0, 3)) + new string('☆', 3 - Math.Clamp(n, 0, 3));

    // ================================================================== local AI

    private Border LocalCard()
    {
        if (Local is not { } local)
            return Ui.Card(Ui.Title("💻 Локальный ИИ"), Ui.Note("Модуль локального ИИ не загрузился. Подробности — в error.log в папке данных.", error: true));

        var hw = local.Hardware;
        var rec = ModelCatalog.Get(hw.RecommendedLlm());
        var hwText = Ui.Muted("Этот ПК: " + hw.Describe() + (rec is null ? "" : $". Рекомендую: {rec.Title}."), 12);
        hwText.Margin = new Thickness(0, 2, 0, 8);

        var llms = new StackPanel();
        foreach (var a in ModelCatalog.Llms)
        {
            var asset = a;
            var path = local.Store.PathOf(a);
            llms.Children.Add(DownloadRow(a,
                s => SamePath(local.Llm.ResolveModelPath(s.Models.GetValueOrDefault("local")), path),
                s => { s.Models["local"] = path; s.Local.ModelPath = path; if (s.Provider != "local" && !ProviderReadyFor(s.Provider)) s.Provider = "local"; },
                badge: a.Id == rec?.Id ? "⭐ рекомендую" : null));
        }

        // свой GGUF
        var current = Ui.Muted("", 11.5);
        current.TextTrimming = TextTrimming.CharacterEllipsis;
        _refreshers.Add(() =>
        {
            var p = local.Llm.ResolveModelPath(S.Models.GetValueOrDefault("local"));
            current.Text = p is null ? "Модель не выбрана" : "Сейчас: " + p;
        });
        var own = Ui.Button("Свой файл .gguf…", "Btn.Ghost", "folder", () =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Модель в формате GGUF", Filter = "GGUF|*.gguf|Все файлы|*.*", CheckFileExists = true };
            if (dlg.ShowDialog(_window) != true) return;
            var p = dlg.FileName;
            Save(s => { s.Models["local"] = p; s.Local.ModelPath = p; });
            _window.Toast("Модель выбрана: " + System.IO.Path.GetFileName(p));
        });
        var folder = Ui.Button("Папка моделей", "Btn.Ghost", "folder", () => OpenFolder(local.Store.Root));
        var test = Ui.Button("Проверить модель", "Btn.Primary", "play", null);
        var testStatus = Ui.Muted("", 12);
        test.Click += async (_, _) =>
        {
            testStatus.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            testStatus.Text = "Загружаю модель и спрашиваю «привет»… (первый раз до минуты)";
            test.IsEnabled = false;
            try
            {
                var r = await Task.Run(() => local.Llm.TestAsync());
                testStatus.Text = "✓ " + r;
                testStatus.SetResourceReference(TextBlock.ForegroundProperty, "Ok");
            }
            catch (Exception e)
            {
                testStatus.Text = "✗ " + e.Message;
                testStatus.SetResourceReference(TextBlock.ForegroundProperty, "Err");
            }
            finally { test.IsEnabled = true; }
        };
        foreach (var b in new FrameworkElement[] { own, folder, test }) b.Margin = new Thickness(0, 0, 8, 6);
        var buttons = new WrapPanel { Margin = new Thickness(0, 6, 0, 0), Children = { own, folder, test } };

        // параметры
        var ctx = Combo(new[]
        {
            ("4096", "4 тыс. токенов (меньше памяти)"),
            ("8192", "8 тыс. (рекомендую)"),
            ("16384", "16 тыс. (длинные документы)"),
            ("32768", "32 тыс. (нужно много памяти)"),
        }, s => s.Local.ContextSize.ToString(CultureInfo.InvariantCulture), (s, v) => s.Local.ContextSize = int.Parse(v, CultureInfo.InvariantCulture));
        var backend = Combo(new[]
        {
            ("auto", "Авто (видеокарта, если ускоритель скачан)"),
            ("cpu", "Только процессор"),
            ("vulkan", "Видеокарта — Vulkan"),
            ("cuda12", "Видеокарта — CUDA 12 (NVIDIA)"),
        }, s => s.Local.Backend, (s, v) => s.Local.Backend = v);
        var restartNote = Ui.Note("Ускоритель поменяется после перезапуска Веры.");
        _refreshers.Add(() => restartNote.Visibility = LlamaEngine.BackendLocked && S.Local.Backend != "auto" && S.Local.Backend != LlamaEngine.ActiveBackend
            ? Visibility.Visible : Visibility.Collapsed);
        var backends = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        foreach (var b in ModelCatalog.Backends)
            backends.Children.Add(DownloadRow(b, badge: b.Id == "backend-vulkan" && hw.HasVulkan ? "⭐ подходит" : null));
        var gpu = Combo(new[] { ("-1", "Все слои (быстрее всего)"), ("0", "Ни одного (только процессор)"), ("16", "16 слоёв (мало видеопамяти)") },
            s => s.Local.GpuLayers.ToString(CultureInfo.InvariantCulture), (s, v) => s.Local.GpuLayers = int.Parse(v, CultureInfo.InvariantCulture));
        var unload = SliderField("Выгружать из памяти после простоя", 0, 60, 5, s => s.Local.UnloadAfterMinutes,
            (s, v) => s.Local.UnloadAfterMinutes = (int)Math.Round(v), v => v < 1 ? "никогда" : $"{v:0} мин");

        var fallbackProvider = Combo(new[] { ("openrouter", "OpenRouter"), ("anthropic", "Anthropic"), ("openai", "OpenAI"), ("custom", "Свой сервер") },
            s => s.Local.FallbackProvider, (s, v) => s.Local.FallbackProvider = v);
        var fbSub = Ui.Sub(Ui.Field("Запасной провайдер (нужен его ключ)", fallbackProvider));
        ShowIf(fbSub, s => s.Local.CloudFallback);

        var usage = Ui.Muted("", 11.5);
        _refreshers.Add(() => usage.Text = $"Занято моделями: {local.Store.UsedBytes() / 1073741824.0:0.0} ГБ · {local.Store.Root}");

        var advanced = new Expander
        {
            Header = Ui.Text("Тонкая настройка", 13, "Fg", FontWeights.SemiBold),
            Margin = new Thickness(0, 6, 0, 0),
            Content = new StackPanel
            {
                Children =
                {
                    Ui.Field("Размер контекста", ctx),
                    Ui.Field("Ускорение", backend),
                    restartNote,
                    backends,
                    Ui.Field("Слои на видеокарте", gpu),
                    unload,
                },
            },
        };

        return Ui.Card(
            Ui.Title("💻 Локальный ИИ — модель прямо на ПК"),
            hwText,
            Ui.Note("Бесплатно и без интернета: переписка не уходит с компьютера. Маленькие модели глупее облачных — " +
                    "но с напоминаниями и программами справляются. Первый ответ после запуска медленнее: модель грузится в память."),
            llms,
            current,
            buttons,
            testStatus,
            advanced,
            Toggle("Гибрид: если встроенная модель не справилась — спросить облако", s => s.Local.CloudFallback, (s, v) => s.Local.CloudFallback = v),
            fbSub,
            usage);
    }

    private static bool SamePath(string? a, string b) =>
        a is not null && string.Equals(System.IO.Path.GetFullPath(a), System.IO.Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    private bool ProviderReadyFor(string provider) =>
        !Vera.Core.Providers.ProviderRegistry.NeedsKey(provider) || Core.Secrets.Has(provider);

    private void OpenFolder(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception e) { _window.Toast(e.Message, true); }
    }

    // ================================================================== web search

    private Border SearchCard()
    {
        var engine = Combo(new[]
        {
            ("auto", "Авто: Tavily → Brave → DuckDuckGo → Википедия"),
            ("tavily", "Tavily (ключ tavily.com, лучший для ИИ)"),
            ("brave", "Brave Search (ключ)"),
            ("duckduckgo", "DuckDuckGo (без ключа)"),
            ("wikipedia", "Только Википедия (без ключа)"),
        }, s => s.Search.Engine, (s, v) => s.Search.Engine = v);
        var sub = Ui.Sub(
            Ui.Field("Поисковик", engine),
            Ui.Muted("Без ключа Вера ищет через DuckDuckGo и Википедию. Ключи Tavily и Brave — в разделе «API-ключи»; у обоих есть бесплатный лимит."));
        ShowIf(sub, s => s.Search.Enabled);
        return Ui.Card(
            Ui.Title("🌐 Поиск в интернете"),
            Toggle("Вера может искать в интернете и читать страницы (любая модель)", s => s.Search.Enabled, (s, v) => s.Search.Enabled = v),
            sub,
            Toggle("Встроенный поиск провайдера вместо этого", s => s.WebSearch, (s, v) => s.WebSearch = v,
                "(Anthropic web search / плагин web OpenRouter; платно у провайдера)"));
    }

    // ================================================================== usage

    private Border UsageCard()
    {
        var today = Ui.Text("", 13);
        var month = Ui.Text("", 13);
        var byProvider = Ui.Muted("", 11.5);
        void Fill()
        {
            static string Line(string head, UsageSummary u) =>
                $"{head}: {u.Requests} запр. · {u.InputTokens + u.OutputTokens:N0} токенов" +
                (u.Cost is { } c ? $" · ≈ ${c:0.00}" + (u.CostPartial ? " (цены известны не для всех моделей)" : "") : "");
            today.Text = Line("Сегодня", Core.Usage.Today());
            month.Text = Line("В этом месяце", Core.Usage.ThisMonth());
            byProvider.Text = string.Join("\n", Core.Usage.MonthByProvider().Select(p =>
                $"  {p.Provider}: {p.Sum.InputTokens + p.Sum.OutputTokens:N0} ток." + (p.Sum.Cost is { } c ? $" · ≈ ${c:0.00}" : "")));
        }
        _refreshers.Add(Fill);
        Core.Usage.Changed += () => Dispatcher.BeginInvoke(() => { if (IsVisible) Fill(); });
        IsVisibleChanged += (_, _) => { if (IsVisible) Fill(); };

        var reset = Ui.Button("Обнулить", "Btn.GhostSmall", null, () =>
        {
            if (!ConfirmDialog.Ask(_window, "Обнулить счётчик?", "Статистика расхода начнётся заново.", okText: "Обнулить")) return;
            Core.Usage.Reset();
            Fill();
        });
        var log = Ui.Button("Открыть журнал запросов", "Btn.GhostSmall", null, () =>
        {
            var path = System.IO.Path.Combine(_host.Storage.Directory, "requests.log");
            if (!File.Exists(path)) File.WriteAllText(path, "");
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose(); }
            catch (Exception e) { _window.Toast(e.Message, true); }
        });
        reset.Margin = new Thickness(0, 0, 8, 0);
        var note = Ui.Muted("Стоимость примерная — по ценам провайдеров. Встроенная модель и свой сервер бесплатны.", 11);
        note.Margin = new Thickness(0, 4, 0, 0);
        return Ui.Card(
            Ui.Title("📊 Расход"),
            today, month, byProvider, note,
            new WrapPanel { Margin = new Thickness(0, 8, 0, 4), Children = { reset, log } },
            Toggle("Записывать ответы моделей в журнал (для отладки)", s => s.Diagnostics.LogRequests, (s, v) => s.Diagnostics.LogRequests = v));
    }

    // ================================================================== transfer

    private UIElement TransferButtons()
    {
        var export = Ui.Button("Экспорт настроек…", "Btn.Ghost", null, () =>
        {
            var dlg = new Microsoft.Win32.SaveFileDialog { FileName = "vera-settings.json", Filter = "JSON|*.json" };
            if (dlg.ShowDialog(_window) != true) return;
            try
            {
                File.WriteAllText(dlg.FileName, Core.Settings.Export());
                _window.Toast("Сохранено. Ключи API в файл не попадают.");
            }
            catch (Exception e) { _window.Toast(e.Message, true); }
        });
        var import = Ui.Button("Импорт…", "Btn.Ghost", null, () =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "JSON|*.json|Все файлы|*.*", CheckFileExists = true };
            if (dlg.ShowDialog(_window) != true) return;
            try
            {
                Core.Settings.Import(File.ReadAllText(dlg.FileName));
                Refresh();
                _window.Toast("Настройки загружены");
            }
            catch (Exception e) { _window.Toast(e.Message, true); }
        });
        var reset = Ui.Button("Сбросить настройки", "Btn.Ghost", null, () =>
        {
            if (!ConfirmDialog.Ask(_window, "Сбросить настройки?", "Всё вернётся как после установки. Ключи, напоминания, память и переписка останутся.",
                    okText: "Сбросить", danger: true)) return;
            Core.Settings.Reset();
            Refresh();
        });
        foreach (var b in new FrameworkElement[] { export, import, reset }) b.Margin = new Thickness(0, 0, 8, 8);
        return new WrapPanel { Margin = new Thickness(0, 4, 0, 0), Children = { export, import, reset } };
    }

    // ================================================================== offline voice helpers

    private UIElement PiperRows()
    {
        var sp = new StackPanel();
        sp.Children.Add(DownloadRow(ModelCatalog.Get("piper")!));
        foreach (var v in ModelCatalog.Voice.Where(x => x.Kind == AssetKind.PiperVoice))
        {
            var id = v.Id;
            sp.Children.Add(DownloadRow(v, s => s.Tts.PiperVoice == id, s => s.Tts.PiperVoice = id,
                badge: id == "ru_RU-irina-medium" ? "⭐ женский" : null));
        }
        return sp;
    }

    private UIElement WhisperRows()
    {
        var sp = new StackPanel();
        var rec = Local?.Hardware.RecommendedWhisper() ?? "small";
        foreach (var v in ModelCatalog.Voice.Where(x => x.Kind == AssetKind.Whisper))
        {
            var size = v.Id["whisper-".Length..];
            sp.Children.Add(DownloadRow(v, s => s.Stt.WhisperModel == size, s => s.Stt.WhisperModel = size,
                badge: size == rec ? "⭐ рекомендую" : null));
        }
        return sp;
    }

    private IEnumerable<(string, string)> SkinOptions() => Skins.All.Select(s => (s.Id, s.Title));
}
