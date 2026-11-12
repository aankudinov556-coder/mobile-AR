using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Toolkit.Uwp.Notifications;
using Vera.Core;

namespace Vera.Desktop.Platform;

/// <summary>Уведомления Windows (toast) о напоминаниях. Клик/кнопки приходят событием Activated в поток UI.</summary>
public sealed class Notifier
{
    private readonly Dispatcher _ui;
    private readonly string? _logoPath;
    private bool _broken;

    /// <summary>Аргументы: action = open|complete|snooze, tab, id.</summary>
    public event Action<ToastArguments>? Activated;

    public Notifier(Dispatcher ui, string dataDir)
    {
        _ui = ui;
        _logoPath = ExtractLogo(dataDir);
        try
        {
            ToastNotificationManagerCompat.OnActivated += e =>
            {
                ToastArguments args;
                try { args = ToastArguments.Parse(e.Argument ?? ""); }
                catch (Exception) { args = new ToastArguments(); }
                _ui.BeginInvoke(() => Activated?.Invoke(args));
            };
        }
        catch (Exception)
        {
            _broken = true;
        }
    }

    /// <summary>Иконку для уведомления берём из ресурсов и кладём рядом с данными (toast нужен путь к файлу).</summary>
    private static string? ExtractLogo(string dataDir)
    {
        try
        {
            var path = Path.Combine(dataDir, "vera-icon.png");
            if (File.Exists(path)) return path;
            var res = Application.GetResourceStream(new Uri("pack://application:,,,/assets/icon.png"));
            if (res is null) return null;
            using (var s = res.Stream)
            using (var f = File.Create(path))
                s.CopyTo(f);
            return path;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Показать уведомление. false — toast недоступен (тогда покажем подсказку из трея).</summary>
    public bool ShowReminder(Reminder r, bool missed)
    {
        if (_broken) return false;
        try
        {
            var b = new ToastContentBuilder()
                .AddArgument("action", "open")
                .AddArgument("tab", "reminders")
                .AddText(missed ? "⏰ Пропущенное напоминание" : "⏰ Вера напоминает")
                .AddText(r.Text)
                .AddButton(new ToastButton().SetContent("Готово ✓").AddArgument("action", "complete").AddArgument("id", r.Id))
                .AddButton(new ToastButton().SetContent("+10 мин").AddArgument("action", "snooze").AddArgument("id", r.Id))
                .AddAudio(new ToastAudio { Silent = true })
                .SetToastScenario(ToastScenario.Reminder);
            if (_logoPath is not null) b.AddAppLogoOverride(new Uri(_logoPath), ToastGenericAppLogoCrop.Circle);
            b.Show();
            return true;
        }
        catch (Exception)
        {
            _broken = true;
            return false;
        }
    }
}
