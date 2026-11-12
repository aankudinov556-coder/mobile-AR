using System.Globalization;
using Vera.Core;
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
    private readonly VerticalStackLayout _ttsOpenai;
    private readonly VerticalStackLayout _ttsEleven;
    private readonly VerticalStackLayout _ttsSystem;
    private readonly Picker _elevenVoice;
    private readonly List<string> _elevenIds = new();
    private readonly Picker _systemVoice;
    private readonly List<string> _systemIds = new();
    private readonly Editor _ttsInstructions;
    private readonly View _sttOpenai;
    private readonly View _sttGroq;
    private readonly View _wakeSub;
    private readonly VerticalStackLayout _permRows = new() { Spacing = 8 };

    public SettingsPage()
    {
        Ui.StylePage(this, "Настройки");

        // ------------------------------------------------ приветствие
        _welcome = Ui.Card(
            Ui.H3("Добро пожаловать!"),
            Ui.Label("Чтобы Вера заработала, вставь хотя бы один API-ключ ниже — например, OpenRouter (один ключ даёт доступ к сотням моделей). " +
                     "Для голоса и распознавания речи нужен ключ OpenAI (или Groq для распознавания + системный голос Android бесплатно, " +
                     "или распознавание средствами телефона).", 14, Ui.Text));
        _welcome.Stroke = Ui.Accent.WithAlpha(0.5f);

        // ------------------------------------------------ модель
        var seg = new Grid { ColumnSpacing = 6 };
        var i = 0;
        foreach (var (id, title, _) in ProviderRegistry.Known)
        {
            seg.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            var b = new Button
            {
                Text = title,
                CornerRadius = 12,
                FontSize = 13.5,
                Padding = new Thickness(4, 6),
                MinimumHeightRequest = 40,
                BorderWidth = 1,
            };
            var pid = id;
            b.Clicked += (_, _) => SetProvider(pid);
            _providerButtons[id] = b;
            seg.Add(b, i++);
        }
        _model = Ui.Entry("название модели");
        _model.ClearButtonVisibility = ClearButtonVisibility.Never;
        _model.Completed += (_, _) => SaveModel();
        _model.Unfocused += (_, _) => SaveModel();
        var modelRow = new Grid { ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) }, ColumnSpacing = 8 };
        modelRow.Add(Ui.Box(_model), 0);
        modelRow.Add(Ui.Ghost("Список…", async (_, _) => await PickModelAsync()), 1);
        _modelHint = Ui.Label("", 12, Ui.Faint);

        var modelCard = Ui.Card(
            Ui.H3("🧠 Модель"),
            Ui.Field("Провайдер", seg),
            Ui.Field("Модель", new VerticalStackLayout { Spacing = 4, Children = { modelRow, _modelHint } }),
            Ui.Field("Глубина размышлений", Choice(
                new[] { "low", "medium", "high" },
                new[] { "Быстро (рекомендую для болтовни)", "Сбалансированно", "Глубоко (медленнее, дороже)" },
                s => s.Effort, (s, v) => s.Effort = v)),
            Ui.Toggle("Поиск в интернете", S.WebSearch, v => Set(s => s.WebSearch = v),
                "Anthropic — web search, OpenRouter — плагин web; платно у провайдера"),
            SliderRow("Сообщений истории в контексте", 6, 200, 2, s => s.HistoryLimit, (s, v) => s.HistoryLimit = (int)v, v => ((int)v).ToString()));

        // ------------------------------------------------ ключи
        var keysCard = Ui.Card(
            Ui.H3("🔑 API-ключи"),
            Ui.MutedLabel("Ключи хранятся только на этом телефоне в зашифрованном виде (Android Keystore)."),
            _keys);

        // ------------------------------------------------ характер
        var personaIds = Personas.All.Keys.ToArray();
        var personaCard = Ui.Card(
            Ui.H3("💁 Характер"),
            Ui.Field("Как Вере тебя называть", Ui.Box(TextSetting("семпай", s => s.UserName, (s, v) => s.UserName = v, 40))),
            Ui.Field("Характер", Choice(personaIds, personaIds.Select(p => Personas.All[p].Label).ToArray(), s => s.Persona, (s, v) => s.Persona = v)),
            Ui.Field("Дополнительные указания (системный промпт)", Ui.Box(EditorSetting(
                "Например: я студент-медик, объясняй просто и с примерами", s => s.CustomPrompt, (s, v) => s.CustomPrompt = v), 90)));

        // ------------------------------------------------ голос
        _ttsInstructions = EditorSetting("", s => s.Tts.OpenaiInstructions, (s, v) => s.Tts.OpenaiInstructions = v, 70);
        var fromPersona = Ui.Ghost("по характеру", (_, _) =>
        {
            var hint = Personas.Get(S.Persona).TtsHint;
            _ttsInstructions.Text = hint;
            Set(s => s.Tts.OpenaiInstructions = hint);
        });
        fromPersona.HorizontalOptions = LayoutOptions.Start;
        _ttsOpenai = new VerticalStackLayout
        {
            Spacing = 10,
            Children =
            {
                Ui.Field("Голос", Choice(
                    new[] { "shimmer", "nova", "coral", "sage", "ballad", "alloy", "verse", "fable", "ash", "echo", "onyx" }, null,
                    s => s.Tts.OpenaiVoice, (s, v) => s.Tts.OpenaiVoice = v)),
                Ui.Field("Модель", Choice(new[] { "gpt-4o-mini-tts", "tts-1", "tts-1-hd" }, null, s => s.Tts.OpenaiModel, (s, v) => s.Tts.OpenaiModel = v)),
                Ui.Field("Интонация (инструкция для голоса)", Ui.Box(_ttsInstructions, 70)),
                fromPersona,
            },
        };

        _elevenVoice = Ui.Picker(Array.Empty<string>(), "Голос ElevenLabs");
        _elevenVoice.SelectedIndexChanged += (_, _) =>
        {
            if (_loading || _elevenVoice.SelectedIndex < 0 || _elevenVoice.SelectedIndex >= _elevenIds.Count) return;
            var v = _elevenIds[_elevenVoice.SelectedIndex];
            Set(s => s.Tts.ElevenVoiceId = v);
        };
        var elevenRow = new Grid { ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) }, ColumnSpacing = 8 };
        elevenRow.Add(Ui.Box(_elevenVoice), 0);
        elevenRow.Add(Ui.Ghost("Загрузить", async (_, _) => await LoadElevenVoicesAsync(true)), 1);
        _ttsEleven = new VerticalStackLayout
        {
            Spacing = 10,
            Children =
            {
                Ui.Field("Голос ElevenLabs", elevenRow),
                Ui.Field("Модель", Choice(
                    new[] { "eleven_multilingual_v2", "eleven_flash_v2_5", "eleven_turbo_v2_5" },
                    new[] { "eleven_multilingual_v2 (качество)", "eleven_flash_v2_5 (быстро)", "eleven_turbo_v2_5" },
                    s => s.Tts.ElevenModel, (s, v) => s.Tts.ElevenModel = v)),
            },
        };

        _systemVoice = Ui.Picker(Array.Empty<string>(), "Системный голос");
        _systemVoice.SelectedIndexChanged += (_, _) =>
        {
            if (_loading || _systemVoice.SelectedIndex < 0 || _systemVoice.SelectedIndex >= _systemIds.Count) return;
            var v = _systemIds[_systemVoice.SelectedIndex];
            Set(s => s.Tts.SystemVoice = v);
        };
        var ttsSettingsBtn = Ui.Ghost("Настройки синтеза речи Android", (_, _) => PermissionsUi.OpenTtsSettings());
        ttsSettingsBtn.HorizontalOptions = LayoutOptions.Start;
        _ttsSystem = new VerticalStackLayout
        {
            Spacing = 10,
            Children =
            {
                Ui.Field("Системный голос", Ui.Box(_systemVoice),
                    "Русский голос можно скачать в настройках Android → Синтез речи (Google TTS → Установка голосовых данных)."),
                ttsSettingsBtn,
            },
        };

        var testText = Ui.Entry();
        testText.Text = "Хе-хе, семпай~ Ну что, слышно меня? Только не влюбись!";
        var testRow = new Grid { ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) }, ColumnSpacing = 8 };
        testRow.Add(Ui.Box(testText), 0);
        testRow.Add(Ui.Primary("▶ Тест", (_, _) =>
        {
            if (S.Tts.Engine == "off") { Ui.Toast("Голос выключен"); return; }
            App.Speech.Stop();
            App.Speech.ResetErrors();
            App.Speech.Enqueue(testText.Text ?? "");
        }), 1);

        var voiceCard = Ui.Card(
            Ui.H3("🔊 Голос Веры"),
            Ui.Field("Движок синтеза речи", Choice(
                new[] { "openai", "elevenlabs", "system", "off" },
                new[] { "OpenAI TTS (живой голос, нужен ключ OpenAI)", "ElevenLabs (нужен ключ ElevenLabs)", "Системный голос Android (бесплатно)", "Выключен" },
                s => s.Tts.Engine, (s, v) => s.Tts.Engine = v, UpdateVisibility)),
            Ui.Field("Когда говорить вслух", Choice(
                new[] { "voice", "always", "never" },
                new[] { "Только на голосовые запросы", "Всегда", "Никогда (кроме напоминаний)" },
                s => s.Tts.AutoSpeak, (s, v) => s.Tts.AutoSpeak = v)),
            _ttsOpenai, _ttsEleven, _ttsSystem,
            SliderRow("Скорость", 0.6, 1.6, 0.05, s => s.Tts.Rate, (s, v) => s.Tts.Rate = v, v => v.ToString("0.00", CultureInfo.InvariantCulture) + "×"),
            SliderRow("Громкость", 0, 1, 0.05, s => s.Tts.Volume, (s, v) => s.Tts.Volume = v, v => ((int)Math.Round(v * 100)) + "%"),
            testRow);

        // ------------------------------------------------ распознавание
        _sttOpenai = Ui.Field("Модель OpenAI", Choice(new[] { "gpt-4o-mini-transcribe", "gpt-4o-transcribe", "whisper-1" }, null,
            s => s.Stt.OpenaiModel, (s, v) => s.Stt.OpenaiModel = v));
        _sttGroq = Ui.Field("Модель Groq", Choice(new[] { "whisper-large-v3-turbo", "whisper-large-v3" }, null,
            s => s.Stt.GroqModel, (s, v) => s.Stt.GroqModel = v));
        _wakeSub = new VerticalStackLayout
        {
            Spacing = 10,
            Children =
            {
                Ui.Field("Слова-активаторы (через запятую)", Ui.Box(TextSetting("вера, верочка", s => s.Voice.WakePhrases, (s, v) => s.Voice.WakePhrases = v, 200))),
                Ui.Note("Пока приложение открыто, микрофон слушает постоянно, и каждая услышанная фраза отправляется на распознавание — это тратит баланс STT-сервиса. " +
                        "С Groq это почти бесплатно, с распознаванием телефона — бесплатно (но Android может пикать при каждом перезапуске). Пока Вера говорит, она себя не слушает.", Ui.Warn),
            },
        };
        var sttCard = Ui.Card(
            Ui.H3("🎙️ Распознавание речи и активация"),
            Ui.Field("Сервис", Choice(
                new[] { "openai", "groq", "device" },
                new[] { "OpenAI (ключ OpenAI)", "Groq Whisper (быстро и дёшево)", "Телефон (распознавание Android, бесплатно)" },
                s => s.Stt.Provider, (s, v) => s.Stt.Provider = v, UpdateVisibility)),
            Ui.Field("Язык", Choice(new[] { "ru", "en", "uk", "" }, new[] { "русский", "английский", "украинский", "авто" },
                s => s.Stt.Language, (s, v) => s.Stt.Language = v)),
            _sttOpenai, _sttGroq,
            SliderRow("Чувствительность микрофона", 0, 1, 0.05, s => s.Voice.Sensitivity, (s, v) => s.Voice.Sensitivity = v, v => ((int)Math.Round(v * 100)) + "%"),
            SliderRow("Пауза, после которой фраза считается законченной", 500, 2500, 100, s => s.Voice.SilenceMs, (s, v) => s.Voice.SilenceMs = (int)v, v => (int)v + " мс"),
            Ui.Toggle("Звук начала/конца записи", S.Voice.Chime, v => Set(s => s.Voice.Chime = v)),
            Ui.Toggle("Голосовая активация по слову «Вера»", S.Voice.WakeWord, v => _ = SetWakeAsync(v), "работает, пока приложение открыто"),
            _wakeSub,
            Ui.MutedLabel("Долгий тап по Вере или кнопка микрофона — сказать одну фразу."));

        // ------------------------------------------------ напоминания
        var remCard = Ui.Card(
            Ui.H3("⏰ Напоминания"),
            Ui.Toggle("Уведомление Android", S.Reminders.Notification, v => Set(s => s.Reminders.Notification = v)),
            Ui.Toggle("Звуковой сигнал", S.Reminders.Sound, v => Set(s => s.Reminders.Sound = v)),
            Ui.Toggle("Вера проговаривает напоминание голосом", S.Reminders.Speak, v => Set(s => s.Reminders.Speak = v), "если приложение открыто"),
            _permRows);

        // ------------------------------------------------ возможности
        var capsCard = Ui.Card(
            Ui.H3("📱 Что Вере можно делать"),
            Ui.Toggle("Открывать сайты, приложения, звонилку, карты, фонарик и настройки", S.Capabilities.PcControl, v => Set(s => s.Capabilities.PcControl = v)),
            Ui.Toggle("Читать и записывать буфер обмена", S.Capabilities.Clipboard, v => Set(s => s.Capabilities.Clipboard = v)),
            Ui.MutedLabel("Напоминания, долгая память и погода доступны всегда. Звонить сама Вера не может — только откроет звонилку с номером."));

        // ------------------------------------------------ Вера
        var petCard = Ui.Card(
            Ui.H3("🧸 Вера"),
            Ui.Toggle("Иногда болтает сама (без API)", S.Pet.IdleChatter, v => Set(s => s.Pet.IdleChatter = v)),
            SliderRow("Сколько показывать реплику", 3, 30, 1, s => s.Pet.BubbleSeconds, (s, v) => s.Pet.BubbleSeconds = (int)v, v => (int)v + " с"),
            Ui.MutedLabel("Тап по Вере — потыкать, долгий тап — сказать голосом. Если долго ничего не делать — она уснёт."));

        var about = Ui.Card(
            Ui.H3("ℹ️ О приложении"),
            Ui.Label($"Вера v{AppInfo.Current.VersionString} для Android", 14),
            Ui.MutedLabel("Данные (настройки, история, напоминания, память) хранятся только на телефоне."));

        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Spacing = 14,
                Padding = new Thickness(14, 12, 14, 32),
                Children = { _welcome, modelCard, keysCard, personaCard, voiceCard, sttCard, remCard, capsCard, petCard, about },
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
        _ = LoadElevenVoicesAsync(false);
        _ = LoadSystemVoicesAsync();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        SaveModel();
        Core.Settings.Flush();
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
        var engine = S.Tts.Engine;
        _ttsOpenai.IsVisible = engine == "openai";
        _ttsEleven.IsVisible = engine == "elevenlabs";
        _ttsSystem.IsVisible = engine == "system";
        _sttOpenai.IsVisible = S.Stt.Provider == "openai";
        _sttGroq.IsVisible = S.Stt.Provider == "groq";
        _wakeSub.IsVisible = S.Voice.WakeWord;
        if (engine == "system") _ = LoadSystemVoicesAsync();
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
            b.Background = on ? Ui.AccentGradient : null;
            b.BackgroundColor = on ? Ui.Accent : Ui.Surface2;
            b.TextColor = on ? Colors.White : Ui.Text;
            b.BorderColor = on ? Colors.Transparent : Ui.Stroke;
            b.FontAttributes = on ? FontAttributes.Bold : FontAttributes.None;
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
            Ui.Toast("Сначала добавь ключ этого провайдера");
            return;
        }
        IReadOnlyList<ModelInfo> models;
        try
        {
            Ui.Toast("Загружаю список моделей…");
            models = await Task.Run(() => Core.Providers.ListModelsAsync(provider));
        }
        catch (Exception e)
        {
            await DisplayAlertAsync("Не удалось загрузить модели", e.Message, "OK");
            return;
        }
        if (models.Count == 0) { Ui.Toast("Список пуст"); return; }
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
            var entry = Ui.Entry(st.IsSet ? "заменить ключ…" : "вставь ключ…", password: true);
            entry.IsSpellCheckEnabled = false;
            entry.IsTextPredictionEnabled = false;
            var result = Ui.Label("", 12.5, Ui.Muted);
            result.IsVisible = false;
            var name = st.Name;

            var save = Ui.Primary("Сохранить", (_, _) =>
            {
                var v = (entry.Text ?? "").Trim();
                if (v.Length == 0) { Ui.Toast("Вставь ключ в поле"); return; }
                Core.Secrets.Set(name, v);
                entry.Text = "";
                Ui.Toast("Ключ сохранён");
                RenderKeys();
                RefreshProvider();
            });
            var test = Ui.Ghost("Проверить", async (_, _) =>
            {
                var typed = (entry.Text ?? "").Trim();
                if (typed.Length > 0) Core.Secrets.Set(name, typed);
                if (!Core.Secrets.Has(name)) { Ui.Toast("Ключ не задан"); return; }
                result.IsVisible = true;
                result.TextColor = Ui.Muted;
                result.Text = "Проверяю…";
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
                var del = Ui.Ghost("Удалить", async (_, _) =>
                {
                    if (!await DisplayAlertAsync("Удалить ключ?", st.Title, "Удалить", "Отмена")) return;
                    Core.Secrets.Set(name, null);
                    RenderKeys();
                    RefreshProvider();
                }, Ui.Danger);
                del.Margin = new Thickness(0, 0, 8, 6);
                buttons.Children.Add(del);
            }
            var url = st.Url;
            var link = new Label { Text = "Где взять ключ →", FontSize = 13, TextColor = Color.FromArgb("#f08fc4"), TextDecorations = TextDecorations.Underline };
            var tap = new TapGestureRecognizer();
            tap.Tapped += async (_, _) => { try { await Launcher.Default.OpenAsync(new Uri(url)); } catch { } };
            link.GestureRecognizers.Add(tap);

            var head = new Grid { ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) }, ColumnSpacing = 8 };
            head.Add(new VerticalStackLayout
            {
                Spacing = 1,
                Children =
                {
                    Ui.Label(st.Title + (st.Name == current ? "  ·  текущий" : ""), 15, Ui.Text, bold: true),
                    Ui.Label(st.Purpose, 12.5, Ui.Muted),
                },
            }, 0);
            head.Add(st.IsSet ? Ui.Tag("✓ " + st.Hint, Ui.Ok.WithAlpha(0.15f), Ui.Ok) : Ui.Tag("не задан"), 1);

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

    // ================================================================== голоса

    private async Task LoadElevenVoicesAsync(bool explicitly)
    {
        var cur = S.Tts.ElevenVoiceId;
        var items = new List<(string Id, string Name)> { ("", "(по умолчанию)") };
        if (Core.Secrets.Has("elevenlabs"))
        {
            try
            {
                var voices = await Task.Run(() => Core.Speech.ElevenVoicesAsync());
                items.AddRange(voices);
            }
            catch (Exception e)
            {
                if (explicitly) Ui.Toast(e.Message, true);
            }
        }
        else if (explicitly) Ui.Toast("Сначала добавь ключ ElevenLabs");
        if (cur.Length > 0 && items.All(v => v.Id != cur)) items.Add((cur, cur));
        _loading = true;
        _elevenIds.Clear();
        _elevenIds.AddRange(items.Select(v => v.Id));
        _elevenVoice.ItemsSource = items.Select(v => v.Name).ToList();
        _elevenVoice.SelectedIndex = Math.Max(0, _elevenIds.IndexOf(cur));
        _loading = false;
    }

    private async Task LoadSystemVoicesAsync()
    {
        if (S.Tts.Engine != "system") return;
        var cur = S.Tts.SystemVoice;
        var items = new List<(string Id, string Name)> { ("", "(авто, русский)") };
        try { items.AddRange(await App.Speech.SystemVoice.VoicesAsync()); } catch { }
        if (cur.Length > 0 && items.All(v => v.Id != cur)) items.Add((cur, cur));
        _loading = true;
        _systemIds.Clear();
        _systemIds.AddRange(items.Select(v => v.Id));
        _systemVoice.ItemsSource = items.Select(v => v.Name).ToList();
        _systemVoice.SelectedIndex = Math.Max(0, _systemIds.IndexOf(cur));
        _loading = false;
    }

    // ================================================================== активация и разрешения

    private async Task SetWakeAsync(bool on)
    {
        if (_loading) return;
        if (on && !await VoiceController.EnsurePermissionAsync())
        {
            Ui.Toast("Без доступа к микрофону активация по слову не работает", true);
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
        _permRows.Children.Add(PermRow(notif ? "✓ Уведомления разрешены" : "✗ Уведомления запрещены", notif, PermissionsUi.OpenNotificationSettings));
        _permRows.Children.Add(PermRow(exact ? "✓ Точные будильники разрешены" : "✗ Нет точных будильников — напоминания могут опаздывать", exact, PermissionsUi.OpenExactAlarmSettings));
    }

    private static View PermRow(string text, bool ok, Action open)
    {
        var g = new Grid { ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) }, ColumnSpacing = 8 };
        var l = Ui.Label(text, 13, ok ? Ui.Ok : Ui.Warn);
        l.VerticalOptions = LayoutOptions.Center;
        g.Add(l, 0);
        if (!ok) g.Add(Ui.Ghost("Разрешить", (_, _) => open(), Ui.Warn), 1);
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
        Ui.StylePage(this, "Выбор модели");
        _models = models.OrderByDescending(m => m.Tools).ThenBy(m => m.Id).ToList();
        _current = current;
        _picked = picked;
        var search = Ui.Entry("поиск: claude, gpt, gemini…");
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
        _count.Text = $"Найдено: {found.Count}" + (found.Count > 150 ? " (показаны первые 150)" : "") + " · ⚙️ — умеет инструменты, 👁 — видит картинки";
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
