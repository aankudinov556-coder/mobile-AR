using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Toolkit.Uwp.Notifications;
using Microsoft.Win32;
using Vera.Core;
using Vera.Core.Agent;
using Vera.Core.Providers;
using Vera.LocalAI;
using Vera.Desktop.Audio;
using Vera.Desktop.Chat;
using Vera.Desktop.Pet;
using Vera.Desktop.Platform;
using Vera.Desktop.UI;

namespace Vera.Desktop;

/// <summary>Простой журнал ошибок в папке данных (error.log) — без Windows отлаживать больше нечем.</summary>
public static class Log
{
    private static string? _path;
    private static readonly object Gate = new();

    public static void Init(string dir) => _path = Path.Combine(dir, "error.log");

    public static void Error(string where, Exception? e)
    {
        if (_path is null) return;
        try
        {
            lock (Gate)
            {
                var fi = new FileInfo(_path);
                if (fi.Exists && fi.Length > 512 * 1024) fi.Delete();
                File.AppendAllText(_path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {where}: {e}\n\n");
            }
        }
        catch (Exception)
        {
            // журнал недоступен — ничего не поделать
        }
    }
}

/// <summary>
/// Сердце ПК-версии: создаёт ядро, окна, трей, горячие клавиши, голос, уведомления и связывает их
/// (роль src/main/main.js). Все методы — в потоке UI.
/// </summary>
public sealed class AppHost : IToolHost
{
    private sealed record Snapshot(string Hotkeys, int Scale, bool Topmost, bool PetVisible, bool Autostart, string Theme, string Voice, string Mic, string Skin)
    {
        public static Snapshot Of(VeraSettings s) => new(
            $"{s.Hotkeys.ToggleChat}|{s.Hotkeys.Talk}|{s.Hotkeys.Screenshot}",
            s.Pet.Scale, s.Pet.AlwaysOnTop, s.Pet.Visible, s.Autostart, s.Theme ?? "auto",
            $"{s.Voice.WakeWord}|{s.Voice.WakeEngine}|{s.Voice.Vad}|{s.Voice.WakePhrases}", s.Voice.MicDeviceId ?? "", s.Skin ?? "vera");
    }

    private readonly Dispatcher _ui;
    private readonly bool _startHidden;
    private TrayIcon? _tray;
    private HotkeyManager? _hotkeys;
    private Notifier? _notifier;
    private Snapshot _prev;
    private bool _hotkeysPaused;

    public AppHost(Dispatcher ui, bool startHidden)
    {
        _ui = ui;
        _startHidden = startHidden;
        Storage = new FileStorage(FileStorage.DefaultDirectory());
        Log.Init(Storage.Directory);
        Core = new VeraCore(Storage, new DpapiSecretStore(Storage), new WindowsTools(this), platform: "desktop");
        _prev = Snapshot.Of(Core.Settings.Current);
        ThemeManager.Apply(Core.Settings.Current.Theme);
        Local = CreateLocalAi(Core);
        Voice = new VoiceController(Core, ui, Local);
        Pet = new PetWindow(this);
        Chat = new ChatWindow(this);
    }

    public FileStorage Storage { get; }
    public VeraCore Core { get; }
    /// <summary>Офлайн-ИИ: встроенная модель, Whisper, Piper, Silero, Vosk (null — не удалось инициализировать).</summary>
    public LocalAiService? Local { get; }

    /// <summary>Папка моделей: %LOCALAPPDATA%\VeraAssistant\models (не в роуминге — файлы большие).</summary>
    public static string ModelsDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VeraAssistant", "models");

    private static LocalAiService? CreateLocalAi(VeraCore core)
    {
        try
        {
            var silero = Path.Combine(AppContext.BaseDirectory, "assets", "models", "silero_vad.onnx");
            var local = new LocalAiService(core, ModelsDirectory, silero);
            local.Attach();
            return local;
        }
        catch (Exception e)
        {
            Log.Error("local ai", e);
            return null;
        }
    }
    public VoiceController Voice { get; }
    public PetWindow Pet { get; }
    public ChatWindow Chat { get; }
    public bool Quitting { get; private set; }
    public List<string> HotkeyErrors { get; private set; } = new();

    public VeraSettings Settings => Core.Settings.Current;
    private VeraSettings S => Core.Settings.Current;

    private void OnUi(Action a)
    {
        if (_ui.CheckAccess()) a();
        else _ui.BeginInvoke(a);
    }

    // ================================================================== startup

    public void Start()
    {
        // установщик мог включить автозапуск — принимаем его выбор при первом запуске
        if (S.FirstRun && Autostart.IsEnabled() && !S.Autostart) Core.Settings.Update(s => s.Autostart = true);
        _prev = Snapshot.Of(S);

        Voice.Send = (text, source) => Send(text, null, source);
        Voice.TurnActive = () => Core.Agent.IsBusy || Pet.HasActiveTurn;

        Core.Agent.Event += e => _ui.BeginInvoke(() => OnAgentEvent(e));
        Core.Reminders.Changed += () => OnUi(() => Chat.RemindersPanel.Refresh());
        Core.Reminders.Due += (r, missed) => _ui.BeginInvoke(() => OnReminderDue(r, missed));
        Core.Memory.Changed += () => OnUi(() => Chat.MemoryPanel.Refresh());
        Core.Settings.Changed += _ => OnUi(OnSettingsChanged);
        Core.Secrets.Changed += () => OnUi(Chat.OnSecretsChanged);
        if (Local is not null) Local.Llm.Status += msg => OnUi(() => Chat.ChatPanel.SetStatus(msg));

        Pet.Prepare();
        if (S.Pet.Visible) Pet.Show();

        _tray = new TrayIcon(TrayMenu);
        _tray.ApplyTheme(ThemeManager.IsDark);
        _tray.Click += ToggleChat;
        _tray.DoubleClick += () => ShowChat("chat");
        ThemeManager.Changed += dark => _tray?.ApplyTheme(dark);

        _hotkeys = new HotkeyManager();
        RegisterHotkeys();
        Autostart.Apply(S.Autostart);

        _notifier = new Notifier(_ui, Storage.Directory);
        _notifier.Activated += OnToastActivated;

        Voice.SyncSettings();

        if (S.FirstRun)
        {
            if (!_startHidden) ShowChat("settings");
            Core.Settings.Update(s => s.FirstRun = false);
        }

        // планировщик напоминаний — когда Вера уже на экране и готова показывать пузыри
        var start = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        start.Tick += (_, _) =>
        {
            start.Stop();
            Core.Reminders.Start();
        };
        start.Start();

        SystemEvents.PowerModeChanged += OnPowerMode;
        SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
        SystemEvents.UserPreferenceChanged += OnUserPreference;
    }

    private void OnPowerMode(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) _ui.BeginInvoke(() => Core.Reminders.Tick(false));
    }

    private void OnDisplayChanged(object? sender, EventArgs e) => _ui.BeginInvoke(() => Pet.ApplySettings());

    private void OnUserPreference(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)
            _ui.BeginInvoke(() => { if ((S.Theme ?? "auto") == "auto") ThemeManager.Apply("auto"); });
    }

    // ================================================================== settings

    private void OnSettingsChanged()
    {
        var now = Snapshot.Of(S);
        var prev = _prev;
        _prev = now;
        if (now.Hotkeys != prev.Hotkeys) RegisterHotkeys();
        if (now.Scale != prev.Scale || now.Topmost != prev.Topmost) Pet.ApplySettings();
        if (now.PetVisible != prev.PetVisible)
        {
            if (now.PetVisible) Pet.Show();
            else Pet.Hide();
        }
        if (now.Autostart != prev.Autostart) Autostart.Apply(now.Autostart);
        if (now.Theme != prev.Theme) ThemeManager.Apply(now.Theme);
        if (now.Voice != prev.Voice || now.Mic != prev.Mic) Voice.SyncSettings();
        if (now.Skin != prev.Skin) Pet.ApplySettings();
        Chat.OnSettingsChanged();
    }

    private void RegisterHotkeys()
    {
        if (_hotkeys is null) return;
        if (_hotkeysPaused)
        {
            _hotkeys.UnregisterAll();
            return;
        }
        var hk = S.Hotkeys;
        HotkeyErrors = _hotkeys.RegisterAll(new (string?, Action)[]
        {
            (hk.ToggleChat, ToggleChat),
            (hk.Talk, TalkHotkey),
            (hk.Screenshot, AskAboutScreen),
        });
        Chat.SettingsPanel.Refresh();
    }

    /// <summary>Пока в настройках записывают новое сочетание, глобальные клавиши отключены.</summary>
    public void PauseHotkeys(bool pause)
    {
        if (_hotkeysPaused == pause) return;
        _hotkeysPaused = pause;
        RegisterHotkeys();
    }

    private void TalkHotkey()
    {
        Pet.Interaction();
        if (!Pet.IsVisible && S.Pet.Visible) Pet.Show();
        Voice.ListenToggle();
    }

    // ================================================================== tray

    private IEnumerable<TrayMenuItem> TrayMenu()
    {
        var s = S;
        yield return new TrayMenuItem("Открыть чат", () => ShowChat("chat"));
        yield return new TrayMenuItem("Сказать Вере (голос)", () => Voice.StartListening());
        yield return new TrayMenuItem("Что у меня на экране?", AskAboutScreen);
        yield return TrayMenuItem.Separator;
        yield return new TrayMenuItem("Напоминания", () => ShowChat("reminders"));
        yield return new TrayMenuItem("Память", () => ShowChat("memory"));
        yield return new TrayMenuItem("Настройки", () => ShowChat("settings"));
        yield return TrayMenuItem.Separator;
        yield return new TrayMenuItem("Показывать Веру на экране", () => SetPetVisible(!S.Pet.Visible), s.Pet.Visible);
        yield return new TrayMenuItem("Поверх всех окон", () => Core.Settings.Update(x => x.Pet.AlwaysOnTop = !x.Pet.AlwaysOnTop), s.Pet.AlwaysOnTop);
        yield return new TrayMenuItem("Голосовая активация «Вера»", () => Core.Settings.Update(x => x.Voice.WakeWord = !x.Voice.WakeWord), s.Voice.WakeWord);
        yield return TrayMenuItem.Separator;
        yield return new TrayMenuItem("Выход", Quit);
    }

    // ================================================================== windows

    private IntPtr PetHwnd => new WindowInteropHelper(Pet).Handle;

    public void ShowChat(string? tab = null)
    {
        Chat.ShowAndActivate(PetHwnd);
        if (tab is not null) Chat.ShowTab(tab);
    }

    public void ToggleChat()
    {
        if (Chat.IsVisible && Chat.IsActive) Chat.Hide();
        else ShowChat("chat");
    }

    /// <summary>Клик по Вере: чат открыт — спрятать, закрыт — показать.</summary>
    public void PetClicked()
    {
        if (Chat.IsVisible && Chat.WindowState != WindowState.Minimized) Chat.Hide();
        else ShowChat("chat");
    }

    public void SetPetVisible(bool visible) => Core.Settings.Update(s => s.Pet.Visible = visible);

    public void SpeakTest(string text)
    {
        if (!Pet.IsVisible) Pet.Show();
        Pet.SpeakTest(text);
    }

    // ================================================================== agent

    /// <summary>Отправить сообщение агенту (из чата, голосом или со скриншотом).</summary>
    public void Send(string text, IReadOnlyList<ImageData>? images, string source, IReadOnlyList<DocumentText>? documents = null)
    {
        if (!Core.ProviderReady)
        {
            Pet.Say(Core.Line("noKey"), "angry", 8000, opensChat: true);
            return;
        }
        if (Core.Agent.IsBusy)
        {
            Pet.Say("Погоди, я ещё отвечаю на прошлое~", "think", 4000);
            return;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                await Core.Agent.SendAsync(text, images, source, documents: documents);
            }
            catch (AgentBusyException e)
            {
                _ = _ui.BeginInvoke(() => Pet.Say(e.Message, "think", 4000));
            }
            catch (Exception e)
            {
                // пользователю ошибка уже показана событием TurnFailed
                Log.Error("agent", e);
            }
        });
    }

    private void OnAgentEvent(AgentEvent e)
    {
        try { Voice.OnAgentEvent(e); } catch (Exception ex) { Log.Error("voice event", ex); }
        try { Pet.OnAgentEvent(e); } catch (Exception ex) { Log.Error("pet event", ex); }
        try { Chat.OnAgentEvent(e); } catch (Exception ex) { Log.Error("chat event", ex); }
    }

    public async void AskAboutScreen()
    {
        if (!S.Capabilities.Screenshots)
        {
            Pet.Say("Скриншоты выключены в настройках~");
            return;
        }
        try
        {
            var shot = await CaptureScreenAsync();
            Send("Посмотри на мой экран. Что тут? Если видишь ошибку или проблему — подскажи, что делать.",
                new[] { new ImageData(ScreenShot.Mime, shot.Base64) }, "screen");
        }
        catch (Exception e)
        {
            Pet.Say(e.Message, "surprised");
        }
    }

    // ================================================================== reminders

    private void OnReminderDue(Reminder r, bool missed)
    {
        try
        {
            var phrase = Core.ReminderPhrase(r, missed);
            if (S.Reminders.Notification && !(_notifier?.ShowReminder(r, missed) ?? false))
                _tray?.Balloon(missed ? "⏰ Пропущенное напоминание" : "⏰ Вера напоминает", r.Text);
            if (!Pet.IsVisible) Pet.Show();
            Pet.OnReminderDue(r, missed, phrase);
        }
        catch (Exception e)
        {
            Log.Error("reminder", e);
        }
    }

    private void OnToastActivated(ToastArguments args)
    {
        try
        {
            args.TryGetValue("action", out string? action);
            args.TryGetValue("id", out string? id);
            switch (action)
            {
                case "complete" when !string.IsNullOrEmpty(id):
                    Core.Reminders.Complete(id);
                    break;
                case "snooze" when !string.IsNullOrEmpty(id):
                    Core.Reminders.Snooze(id, 10);
                    break;
                default:
                    args.TryGetValue("tab", out string? tab);
                    ShowChat(string.IsNullOrEmpty(tab) ? "reminders" : tab);
                    break;
            }
        }
        catch (Exception e)
        {
            Log.Error("toast", e);
        }
    }

    // ================================================================== IToolHost

    public Task<ScreenShot> CaptureScreenAsync() => _ui.InvokeAsync(CaptureCoreAsync).Task.Unwrap();

    private async Task<ScreenShot> CaptureCoreAsync()
    {
        // прячем окна Веры на время снимка
        var hidden = new List<IntPtr>();
        foreach (var w in new Window[] { Pet, Chat })
        {
            if (!w.IsVisible) continue;
            var h = new WindowInteropHelper(w).Handle;
            if (h == IntPtr.Zero) continue;
            Native.Cloak(h, true);
            hidden.Add(h);
        }
        var petOpacity = Pet.Opacity;
        Pet.Opacity = 0;
        try
        {
            await Task.Delay(180);
            return await Task.Run(ScreenCapture.CaptureUnderCursor);
        }
        finally
        {
            Pet.Opacity = petOpacity;
            foreach (var h in hidden) Native.Cloak(h, false);
        }
    }

    public Task<bool> ConfirmCommandAsync(string command, string? reason) =>
        _ui.InvokeAsync(() => ConfirmDialog.Ask(Chat.IsVisible ? Chat : null, "Вера хочет выполнить команду",
            string.IsNullOrWhiteSpace(reason) ? "Разрешить выполнение команды?" : reason!, command, "Выполнить", "Отмена")).Task;

    private static T Retry<T>(Func<T> f)
    {
        for (var i = 0; ; i++)
        {
            try { return f(); }
            catch (System.Runtime.InteropServices.COMException) when (i < 5)
            {
                Thread.Sleep(60); // буфер обмена занят другой программой
            }
        }
    }

    public Task<string?> ReadClipboardAsync() =>
        _ui.InvokeAsync(() => Retry(() => Clipboard.ContainsText() ? Clipboard.GetText() : null)).Task;

    public Task WriteClipboardAsync(string text) =>
        _ui.InvokeAsync(() => Retry(() => { Clipboard.SetText(text ?? ""); return true; })).Task;

    // ================================================================== exit

    /// <summary>Сохранить всё (выход, завершение сеанса Windows).</summary>
    public void Flush()
    {
        try { Core.Flush(); } catch (Exception e) { Log.Error("flush", e); }
    }

    public void Quit()
    {
        if (Quitting) return;
        Quitting = true;
        try
        {
            SystemEvents.PowerModeChanged -= OnPowerMode;
            SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
            SystemEvents.UserPreferenceChanged -= OnUserPreference;
            Core.Reminders.Stop();
            Voice.Dispose();
            Local?.Dispose();
            _hotkeys?.Dispose();
            _tray?.Dispose();
            Core.Dispose();
        }
        catch (Exception e)
        {
            Log.Error("quit", e);
        }
        Application.Current.Shutdown();
    }
}
