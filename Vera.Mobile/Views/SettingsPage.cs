using System.Globalization;
using Vera.Core;
using Vera.Core.Speech;
using Vera.Core.Providers;
using Vera.Mobile.Platform;
using Vera.Mobile.Services;

namespace Vera.Mobile.Views;

/// <summary>Настройки: модель, ключи, характер, голос, распознавание, напоминания, разрешения.</summary>
public sealed class SettingsPage : ContentPage
{
    private VeraCore Core => App.Core;
    private VeraSettings S => Core.Settings.Current;
    private bool _loading = true;

    private readonly Border _welcome;
    private readonly Dictionary<string, Button> _providerButtons = new();
    private readonly Entry _model;
    private readonly Label _modelHint;
    private readonly VerticalStackLayout _keys = new() { Spacing = 12 };
    private readonly View _sttOpenai;
    private readonly View _sttGroq;
    private readonly View _wakeSub;
    private readonly VerticalStackLayout _permRows = new() { Spacing = 8 };

    public SettingsPage()
    {
        Ui.StylePage(this, L10n.T("Настройки"));

        // ------------------------------------------------ приветствие
        _welcome = Ui.Card(
            Ui.H3(L10n.T("Добро пожаловать!")),
            Ui.Label(L10n.T("Чтобы Вера заработала, вставь хотя бы один API-ключ ниже — например, OpenRouter (один ключ даёт доступ к сотням моделей). ") +
                     L10n.T("Для распознавания речи — ключ OpenAI или Groq, или бесплатно средствами телефона."), 14, Ui.Text));
        _welcome.Stroke = Ui.Accent.WithAlpha(0.5f);

        // ------------------------------------------------ модель
        // провайдеры карточками 2×2 (встроенная модель — только на ПК)
        var seg = new Grid
        {
            ColumnSpacing = 8,
            RowSpacing = 8,
            ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Star) },
        };
        var i = 0;
        foreach (var (id, title, _) in ProviderRegistry.Known.Where(k => App.Core.Providers.IsAvailable(k.Id)))
        {
            if (i % 2 == 0) seg.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var b = new Button
            {
                Text = title,
                CornerRadius = 14,
                FontSize = 13.5,
                FontFamily = Ui.FontBold,
                Padding = new Thickness(4, 8),
                MinimumHeightRequest = 46,
                BorderWidth = 2,
            };
            var pid = id;
            b.Clicked += (_, _) => SetProvider(pid);
            _providerButtons[id] = b;
            seg.Add(b, i % 2, i / 2);
            i++;
        }
        _model = Ui.Entry(L10n.T("название модели"));
        _model.ClearButtonVisibility = ClearButtonVisibility.Never;
        _model.Completed += (_, _) => SaveModel();
        _model.Unfocused += (_, _) => SaveModel();
        var modelRow = new Grid { ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) }, ColumnSpacing = 8 };
        modelRow.Add(Ui.Box(_model), 0);
        modelRow.Add(Ui.Ghost(L10n.T("Список…"), async (_, _) => await PickModelAsync()), 1);
        _modelHint = Ui.Label("", 12, Ui.Faint);

        var modelCard = Ui.Card(
            Ui.H3(L10n.T("🧠 Модель")),
            Ui.Field(L10n.T("Провайдер"), seg),
            Ui.Field(L10n.T("Модель"), new VerticalStackLayout { Spacing = 4, Children = { modelRow, _modelHint } }),
            Ui.Field(L10n.T("Глубина размышлений"), Choice(
                new[] { "low", "medium", "high" },
                new[] { L10n.T("Быстро (рекомендую для болтовни)"), L10n.T("Сбалансированно"), L10n.T("Глубоко (медленнее, дороже)") },
                s => s.Effort, (s, v) => s.Effort = v)),
            Ui.Toggle(L10n.T("Поиск в интернете"), S.WebSearch, v => Set(s => s.WebSearch = v),
                L10n.T("Anthropic — web search, OpenRouter — плагин web; платно у провайдера")),
            SliderRow(L10n.T("Сообщений истории в контексте"), 6, 200, 2, s => s.HistoryLimit, (s, v) => s.HistoryLimit = (int)v, v => ((int)v).ToString()));

        // ------------------------------------------------ ключи
        var keysCard = Ui.Card(
            Ui.H3(L10n.T("🔑 API-ключи")),
            Ui.MutedLabel(L10n.T("Ключи хранятся только на этом телефоне в зашифрованном виде (Android Keystore).")),
            _keys);

        // ------------------------------------------------ характер
        var personaIds = Personas.All.Keys.ToArray();
        var personaCard = Ui.Card(
            Ui.H3(L10n.T("💁 Характер")),
            Ui.Field("Язык / Language", Choice(new[] { "ru", "en" }, new[] { "RU · Русский", "EN · English" },
                s => s.Language == "en" ? "en" : "ru", (s, v) => s.Language = v, () => _ = AskRestartAsync())),
            Ui.Field(L10n.T("Как Вере тебя называть"), Ui.Box(TextSetting(L10n.T("семпай"), s => s.UserName, (s, v) => s.UserName = v, 40))),
            Ui.Field(L10n.T("Город (для погоды и сводки)"), Ui.Box(TextSetting(L10n.T("Москва"), s => s.City, (s, v) => s.City = v.Trim(), 60))),
            Ui.Field(L10n.T("Характер"), Choice(personaIds, personaIds.Select(p => Personas.All[p].Label).ToArray(), s => s.Persona, (s, v) => s.Persona = v)),
            Ui.Field(L10n.T("Дополнительные указания (системный промпт)"), Ui.Box(EditorSetting(
                L10n.T("Например: я студент-медик, объясняй просто и с примерами"), s => s.CustomPrompt, (s, v) => s.CustomPrompt = v), 90)));

        // ------------------------------------------------ звук ответа (вместо синтеза речи)
        const string sample = "Поставила на 18:00. Опоздаешь — оформлю служебную записку, а пока свободен~";
        void Listen() { if (S.Sound.Style != "off") App.Speech.Say(L10n.T(sample)); }
        var voiceCard = Ui.Card(
            Ui.H3(L10n.T("🔊 Звук ответа")),
            Ui.MutedLabel(L10n.T("Пока Вера печатает ответ, звучит тихий «голосок» в такт буквам. Тон подстраивается под характер.")),
            Ui.Field(L10n.T("Звук"), Choice(
                ReplySound.Styles.Select(x => x.Id).ToArray(),
                ReplySound.Styles.Select(x => L10n.T(x.Title) + " — " + L10n.T(x.About)).ToArray(),
                s => s.Sound.Style, (s, v) => s.Sound.Style = v, Listen)),
            Ui.Field(L10n.T("Громкость"), Choice(
                ReplySound.Volumes.Select(x => x.Id).ToArray(),
                ReplySound.Volumes.Select(x => L10n.T(x.Title)).ToArray(),
                s => s.Sound.Volume, (s, v) => s.Sound.Volume = v, Listen)),
            Ui.Primary(L10n.T("▶ Послушать"), (_, _) =>
            {
                if (S.Sound.Style == "off") { Ui.Toast("Звук ответа выключен — выбери вариант"); return; }
                Listen();
            }));

        // ------------------------------------------------ распознавание
        _sttOpenai = Ui.Field(L10n.T("Модель OpenAI"), Choice(new[] { "gpt-4o-mini-transcribe", "gpt-4o-transcribe", "whisper-1" }, null,
            s => s.Stt.OpenaiModel, (s, v) => s.Stt.OpenaiModel = v));
        _sttGroq = Ui.Field(L10n.T("Модель Groq"), Choice(new[] { "whisper-large-v3-turbo", "whisper-large-v3" }, null,
            s => s.Stt.GroqModel, (s, v) => s.Stt.GroqModel = v));
        _wakeSub = new VerticalStackLayout
        {
            Spacing = 10,
            Children =
            {
                Ui.Field(L10n.T("Слова-активаторы (через запятую)"), Ui.Box(TextSetting(L10n.T("вера, верочка"), s => s.Voice.WakePhrases, (s, v) => s.Voice.WakePhrases = v, 200))),
                Ui.Note(L10n.T("Пока приложение открыто, микрофон слушает постоянно, и каждая услышанная фраза отправляется на распознавание — это тратит баланс STT-сервиса. ") +
                        L10n.T("С Groq это почти бесплатно, с распознаванием телефона — бесплатно (но Android может пикать при каждом перезапуске). Пока Вера говорит, она себя не слушает."), Ui.Warn),
            },
        };
        var sttCard = Ui.Card(
            Ui.H3(L10n.T("🎙️ Распознавание речи и активация")),
            Ui.Field(L10n.T("Сервис"), Choice(
                new[] { "openai", "groq", "device" },
                new[] { L10n.T("OpenAI (ключ OpenAI)"), L10n.T("Groq Whisper (быстро и дёшево)"), L10n.T("Телефон (распознавание Android, бесплатно)") },
                s => s.Stt.Provider, (s, v) => s.Stt.Provider = v, UpdateVisibility)),
            Ui.Field(L10n.T("Язык"), Choice(new[] { "ru", "en", "uk", "" }, new[] { L10n.T("русский"), L10n.T("английский"), L10n.T("украинский"), L10n.T("авто") },
                s => s.Stt.Language, (s, v) => s.Stt.Language = v)),
            _sttOpenai, _sttGroq,
            SliderRow(L10n.T("Чувствительность микрофона"), 0, 1, 0.05, s => s.Voice.Sensitivity, (s, v) => s.Voice.Sensitivity = v, v => ((int)Math.Round(v * 100)) + "%"),
            SliderRow(L10n.T("Пауза, после которой фраза считается законченной"), 500, 2500, 100, s => s.Voice.SilenceMs, (s, v) => s.Voice.SilenceMs = (int)v, v => (int)v + L10n.T(" мс")),
            Ui.Toggle(L10n.T("Звук начала/конца записи"), S.Voice.Chime, v => Set(s => s.Voice.Chime = v)),
            Ui.Toggle(L10n.T("Голосовая активация по слову «Вера»"), S.Voice.WakeWord, v => _ = SetWakeAsync(v), L10n.T("работает, пока приложение открыто")),
            _wakeSub,
            Ui.MutedLabel(L10n.T("Долгий тап по Вере или кнопка микрофона — сказать одну фразу.")));

        // ------------------------------------------------ напоминания
        var remCard = Ui.Card(
            Ui.H3(L10n.T("⏰ Напоминания")),
            Ui.Toggle(L10n.T("Уведомление Android"), S.Reminders.Notification, v => Set(s => s.Reminders.Notification = v)),
            Ui.Toggle(L10n.T("Звуковой сигнал"), S.Reminders.Sound, v => Set(s => s.Reminders.Sound = v)),
            Ui.Toggle(L10n.T("«Голосок» Веры вместе с напоминанием"), S.Reminders.Speak, v => Set(s => s.Reminders.Speak = v), L10n.T("если приложение открыто")),
            _permRows);

        // ------------------------------------------------ возможности
        var capsCard = Ui.Card(
            Ui.H3(L10n.T("📱 Что Вере можно делать")),
            Ui.Toggle(L10n.T("Открывать сайты, приложения, звонилку, карты, фонарик и настройки"), S.Capabilities.PcControl, v => Set(s => s.Capabilities.PcControl = v)),
            Ui.Toggle(L10n.T("Читать и записывать буфер обмена"), S.Capabilities.Clipboard, v => Set(s => s.Capabilities.Clipboard = v)),
            Ui.MutedLabel(L10n.T("Напоминания, долгая память и погода доступны всегда. Звонить сама Вера не может — только откроет звонилку с номером.")));

        // ------------------------------------------------ Вера
        var skinIds = Vera.Core.Sprites.Skins.All.Select(x => x.Id).ToArray();
        var petCard = Ui.Card(
            Ui.H3(L10n.T("🧸 Вера")),
            Ui.Field(L10n.T("Скин"), Choice(skinIds, Vera.Core.Sprites.Skins.All.Select(x => x.Title).ToArray(), s => s.Skin ?? "vera", (s, v) => s.Skin = v)),
            Ui.Toggle(L10n.T("Иногда болтает сама (без API)"), S.Pet.IdleChatter, v => Set(s => s.Pet.IdleChatter = v)),
            SliderRow(L10n.T("Сколько показывать реплику"), 3, 30, 1, s => s.Pet.BubbleSeconds, (s, v) => s.Pet.BubbleSeconds = (int)v, v => (int)v + L10n.T(" с")),
            Ui.MutedLabel(L10n.T("Тап по Вере — потыкать, долгий тап — сказать голосом. Если долго ничего не делать — она уснёт.")));

        var syncBtn = Ui.Primary(L10n.T("Синхронизация с ПК"), async (_, _) => await Navigation.PushAsync(new SyncPage()));
        var syncCard = Ui.Card(
            Ui.H3(L10n.T("💻 Компьютер")),
            Ui.MutedLabel(L10n.T("Память, напоминания, чат и характер — одинаковые на телефоне и ПК. Связь только в домашней сети, всё шифруется.")),
            syncBtn);

        var about = Ui.Card(
            Ui.H3(L10n.T("ℹ️ О приложении")),
            Ui.Label(L10n.F("Вера v{0} для Android", AppInfo.Current.VersionString), 14),
            Ui.MutedLabel(L10n.T("Данные хранятся на телефоне; с ПК — только если привязан (Синхронизация с ПК).")));

        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Spacing = 14,
                Padding = new Thickness(14, 12, 14, 32),
                Children = { _welcome, modelCard, keysCard, syncCard, personaCard, voiceCard, sttCard, remCard, capsCard, petCard, about },
            },
        };
        _loading = false;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _loading = true;
        RefreshProvider();
        RenderKeys();
        RenderPermissions();
        UpdateVisibility();
        _loading = false;
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        SaveModel();
        Core.Settings.Flush();
    }

    // ================================================================== язык

    /// <summary>Язык интерфейса меняется целиком после перезапуска приложения.</summary>
    private async Task AskRestartAsync()
    {
        var en = S.Language == "en";
        if ((en ? "en" : "ru") == L10n.Lang) return;
        var ok = await DisplayAlertAsync(en ? "Restart Vera?" : "Перезапустить Веру?",
            en ? "The interface language changes after a restart." : "Язык интерфейса сменится после перезапуска.",
            en ? "Restart" : "Перезапустить", en ? "Later" : "Позже");
        if (ok) AppRestart.Run();
    }

    // ================================================================== помощники привязки

    private void Set(Action<VeraSettings> change)
    {
        if (_loading) return;
        Core.Settings.Update(change);
    }

    private View Choice(string[] values, string[]? labels, Func<VeraSettings, string> get, Action<VeraSettings, string> set, Action? after = null)
    {
        var vals = values.ToList();
        var labs = (labels ?? values).ToList();
        var cur = get(S) ?? "";
        if (!vals.Contains(cur) && cur.Length > 0) { vals.Add(cur); labs.Add(cur); }
        var p = Ui.Picker(labs);
        p.SelectedIndex = Math.Max(0, vals.IndexOf(cur));
        p.SelectedIndexChanged += (_, _) =>
        {
            if (_loading || p.SelectedIndex < 0) return;
            var v = vals[p.SelectedIndex];
            Core.Settings.Update(s => set(s, v));
            after?.Invoke();
        };
        return Ui.Box(p);
    }

    private View SliderRow(string title, double min, double max, double step, Func<VeraSettings, double> get, Action<VeraSettings, double> set, Func<double, string> fmt)
    {
        var value = Math.Clamp(get(S), min, max);
        var label = Ui.Label(fmt(value), 13, Ui.Text, bold: true);
        label.HorizontalOptions = LayoutOptions.End;
        var slider = new Slider
        {
            Minimum = min,
            Maximum = max,
            Value = value,
            MinimumTrackColor = Ui.Accent,
            MaximumTrackColor = Ui.Surface3,
            ThumbColor = Color.FromArgb("#f08fc4"),
        };
        var last = value;
        slider.ValueChanged += (_, e) =>
        {
            var v = Math.Round(e.NewValue / step) * step;
            v = Math.Clamp(Math.Round(v, 4), min, max);
            label.Text = fmt(v);
            if (Math.Abs(v - last) < step / 2) return;
            last = v;
            Set(s => set(s, v));
        };
        var head = new Grid { ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) } };
        head.Add(Ui.Label(title, 13, Ui.Muted), 0);
        head.Add(label, 1);
        return new VerticalStackLayout { Spacing = 0, Children = { head, slider } };
    }

    private Entry TextSetting(string placeholder, Func<VeraSettings, string> get, Action<VeraSettings, string> set, int maxLength)
    {
        var e = Ui.Entry(placeholder);
        e.Text = get(S);
        e.MaxLength = maxLength;
        void Save()
        {
            var v = (e.Text ?? "").Trim();
            if (v == (get(S) ?? "")) return;
            Set(s => set(s, v));
        }
        e.Completed += (_, _) => Save();
        e.Unfocused += (_, _) => Save();
        return e;
    }

    private Editor EditorSetting(string placeholder, Func<VeraSettings, string> get, Action<VeraSettings, string> set, double height = 90)
    {
        var e = Ui.Editor(placeholder, height);
        e.Text = get(S);
        e.Unfocused += (_, _) =>
        {
            var v = e.Text ?? "";
            if (v == (get(S) ?? "")) return;
            Set(s => set(s, v));
        };
        return e;
    }

    private void UpdateVisibility()
    {
        _sttOpenai.IsVisible = S.Stt.Provider == "openai";
        _sttGroq.IsVisible = S.Stt.Provider == "groq";
        _wakeSub.IsVisible = S.Voice.WakeWord;
    }

    // ================================================================== провайдер и модель

    private void SetProvider(string id)
    {
        SaveModel();
        Core.Settings.Update(s => s.Provider = id);
        RefreshProvider();
        RenderKeys();
    }

    private void RefreshProvider()
    {
        var p = S.Provider;
        foreach (var (id, b) in _providerButtons)
        {
            var on = id == p;
            b.BackgroundColor = on ? Ui.Accent : Ui.Surface2;
            b.TextColor = on ? Ui.OnAccent : Ui.Text;
            b.BorderColor = on ? Ui.Ink : Ui.Stroke;
            b.BorderWidth = on ? 2.5 : 2;
        }
        _model.Text = S.CurrentModel;
        _modelHint.Text = ProviderRegistry.Known.FirstOrDefault(k => k.Id == p).Hint ?? "";
        _welcome.IsVisible = !Core.Secrets.Has(p);
    }

    private void SaveModel()
    {
        var v = (_model.Text ?? "").Trim();
        if (v.Length == 0 || v == S.CurrentModel) return;
        Core.Settings.Update(s => s.Models[s.Provider] = v);
    }

    private async Task PickModelAsync()
    {
        var provider = S.Provider;
        if (!Core.Secrets.Has(provider) && provider != "openrouter")
        {
            Ui.Toast(L10n.T("Сначала добавь ключ этого провайдера"));
            return;
        }
        IReadOnlyList<ModelInfo> models;
        try
        {
            Ui.Toast(L10n.T("Загружаю список моделей…"));
            models = await Task.Run(() => Core.Providers.ListModelsAsync(provider));
        }
        catch (Exception e)
        {
            await DisplayAlertAsync(L10n.T("Не удалось загрузить модели"), e.Message, "OK");
            return;
        }
        if (models.Count == 0) { Ui.Toast(L10n.T("Список пуст")); return; }
        var page = new ModelPickerPage(models, S.CurrentModel, id =>
        {
            _model.Text = id;
            Core.Settings.Update(s => s.Models[provider] = id);
        });
        await Navigation.PushAsync(page);
    }

    // ================================================================== ключи

    private void RenderKeys()
    {
        _keys.Children.Clear();
        var current = S.Provider;
        foreach (var st in Core.Secrets.Status())
        {
            var entry = Ui.Entry(st.IsSet ? L10n.T("заменить ключ…") : L10n.T("вставь ключ…"), password: true);
            entry.IsSpellCheckEnabled = false;
            entry.IsTextPredictionEnabled = false;
            var result = Ui.Label("", 12.5, Ui.Muted);
            result.IsVisible = false;
            var name = st.Name;

            var save = Ui.Primary(L10n.T("Сохранить"), (_, _) =>
            {
                var v = (entry.Text ?? "").Trim();
                if (v.Length == 0) { Ui.Toast(L10n.T("Вставь ключ в поле")); return; }
                Core.Secrets.Set(name, v);
                entry.Text = "";
                Ui.Toast(L10n.T("Ключ сохранён"));
                RenderKeys();
                RefreshProvider();
            });
            var test = Ui.Ghost(L10n.T("Проверить"), async (_, _) =>
            {
                var typed = (entry.Text ?? "").Trim();
                if (typed.Length > 0) Core.Secrets.Set(name, typed);
                if (!Core.Secrets.Has(name)) { Ui.Toast(L10n.T("Ключ не задан")); return; }
                result.IsVisible = true;
                result.TextColor = Ui.Muted;
                result.Text = L10n.T("Проверяю…");
                try
                {
                    var msg = await Task.Run(() => Core.TestKeyAsync(name));
                    result.TextColor = Ui.Ok;
                    result.Text = "✓ " + msg;
                }
                catch (Exception e)
                {
                    result.TextColor = Ui.Danger;
                    result.Text = "✗ " + e.Message;
                }
                if (typed.Length > 0) { entry.Text = ""; RefreshProvider(); }
            });
            var buttons = new FlexLayout { Wrap = Microsoft.Maui.Layouts.FlexWrap.Wrap };
            foreach (var b in new View[] { save, test })
            {
                b.Margin = new Thickness(0, 0, 8, 6);
                buttons.Children.Add(b);
            }
            if (st.IsSet)
            {
                var del = Ui.Ghost(L10n.T("Удалить"), async (_, _) =>
                {
                    if (!await DisplayAlertAsync(L10n.T("Удалить ключ?"), st.Title, L10n.T("Удалить"), L10n.T("Отмена"))) return;
                    Core.Secrets.Set(name, null);
                    RenderKeys();
                    RefreshProvider();
                }, Ui.Danger);
                del.Margin = new Thickness(0, 0, 8, 6);
                buttons.Children.Add(del);
            }
            var url = st.Url;
            var link = new Label { Text = L10n.T("Где взять ключ →"), FontSize = 13, TextColor = Color.FromArgb("#f08fc4"), TextDecorations = TextDecorations.Underline };
            var tap = new TapGestureRecognizer();
            tap.Tapped += async (_, _) => { try { await Launcher.Default.OpenAsync(new Uri(url)); } catch { } };
            link.GestureRecognizers.Add(tap);

            var head = new Grid { ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) }, ColumnSpacing = 8 };
            head.Add(new VerticalStackLayout
            {
                Spacing = 1,
                Children =
                {
                    Ui.Label(st.Title + (st.Name == current ? L10n.T("  ·  текущий") : ""), 15, Ui.Text, bold: true),
                    Ui.Label(st.Purpose, 12.5, Ui.Muted),
                },
            }, 0);
            head.Add(st.IsSet ? Ui.Tag("✓ " + st.Hint, Ui.Ok.WithAlpha(0.15f), Ui.Ok) : Ui.Tag(L10n.T("не задан")), 1);

            _keys.Children.Add(new Border
            {
                BackgroundColor = Ui.Surface2,
                Stroke = st.Name == current && !st.IsSet ? Ui.Accent.WithAlpha(0.6f) : Ui.Stroke,
                StrokeThickness = 1,
                StrokeShape = Ui.Round(14),
                Padding = new Thickness(12, 10),
                Content = new VerticalStackLayout { Spacing = 8, Children = { head, Ui.Box(entry), buttons, result, link } },
            });
        }
    }

    // ================================================================== активация и разрешения

    private async Task SetWakeAsync(bool on)
    {
        if (_loading) return;
        if (on && !await VoiceController.EnsurePermissionAsync())
        {
            Ui.Toast(L10n.T("Без доступа к микрофону активация по слову не работает"), true);
        }
        Core.Settings.Update(s => s.Voice.WakeWord = on);
        _wakeSub.IsVisible = on;
        _ = App.Voice.SyncWakeAsync();
    }

    private void RenderPermissions()
    {
        _permRows.Children.Clear();
        var notif = ReminderNotifier.Enabled();
        var exact = ReminderScheduler.CanScheduleExact();
        _permRows.Children.Add(PermRow(notif ? L10n.T("✓ Уведомления разрешены") : L10n.T("✗ Уведомления запрещены"), notif, PermissionsUi.OpenNotificationSettings));
        _permRows.Children.Add(PermRow(exact ? L10n.T("✓ Точные будильники разрешены") : L10n.T("✗ Нет точных будильников — напоминания могут опаздывать"), exact, PermissionsUi.OpenExactAlarmSettings));
    }

    private static View PermRow(string text, bool ok, Action open)
    {
        var g = new Grid { ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) }, ColumnSpacing = 8 };
        var l = Ui.Label(text, 13, ok ? Ui.Ok : Ui.Warn);
        l.VerticalOptions = LayoutOptions.Center;
        g.Add(l, 0);
        if (!ok) g.Add(Ui.Ghost(L10n.T("Разрешить"), (_, _) => open(), Ui.Warn), 1);
        return g;
    }
}

/// <summary>Выбор модели из списка провайдера с поиском.</summary>
public sealed class ModelPickerPage : ContentPage
{
    private readonly IReadOnlyList<ModelInfo> _models;
    private readonly string _current;
    private readonly Action<string> _picked;
    private readonly VerticalStackLayout _list = new() { Spacing = 6 };
    private readonly Label _count = Ui.Label("", 12, Ui.Faint);

    public ModelPickerPage(IReadOnlyList<ModelInfo> models, string current, Action<string> picked)
    {
        Ui.StylePage(this, L10n.T("Выбор модели"));
        _models = models.OrderByDescending(m => m.Tools).ThenBy(m => m.Id).ToList();
        _current = current;
        _picked = picked;
        var search = Ui.Entry(L10n.T("поиск: claude, gpt, gemini…"));
        search.TextChanged += (_, e) => Render(e.NewTextValue);
        Content = new Grid
        {
            RowDefinitions = { new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Star) },
            Padding = new Thickness(14, 10, 14, 0),
            RowSpacing = 8,
            Children =
            {
                new VerticalStackLayout { Spacing = 4, Children = { Ui.Box(search), _count } },
            },
        };
        var scroll = new ScrollView { Content = _list };
        ((Grid)Content).Add(scroll, 0, 1);
        Render("");
    }

    private void Render(string? q)
    {
        q = (q ?? "").Trim().ToLowerInvariant();
        var found = _models.Where(m => q.Length == 0 || m.Id.ToLowerInvariant().Contains(q) || m.Name.ToLowerInvariant().Contains(q)).ToList();
        _count.Text = L10n.F("Найдено: {0}", found.Count) + (found.Count > 150 ? L10n.T(" (показаны первые 150)") : "") + L10n.T(" · ⚙️ — умеет инструменты, 👁 — видит картинки");
        _list.Children.Clear();
        foreach (var m in found.Take(150))
        {
            var row = new Border
            {
                BackgroundColor = m.Id == _current ? Ui.AccentSoft : Ui.Surface,
                Stroke = m.Id == _current ? Ui.Accent : Ui.Stroke,
                StrokeThickness = 1,
                StrokeShape = Ui.Round(12),
                Padding = new Thickness(12, 8),
                Content = new VerticalStackLayout
                {
                    Spacing = 2,
                    Children =
                    {
                        Ui.Label(m.Name + (m.Tools ? "  ⚙️" : "") + (m.Vision ? "  👁" : ""), 14.5, m.Tools ? Ui.Text : Ui.Muted, bold: m.Id == _current),
                        Ui.Label(m.Id, 12, Ui.Faint),
                    },
                },
            };
            var id = m.Id;
            var tap = new TapGestureRecognizer();
            tap.Tapped += async (_, _) =>
            {
                _picked(id);
                try { await Navigation.PopAsync(); } catch { }
            };
            row.GestureRecognizers.Add(tap);
            _list.Children.Add(row);
        }
    }
}
