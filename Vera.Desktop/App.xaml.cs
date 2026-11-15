using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Vera.Desktop.Platform;
using Vera.Desktop.UI;

namespace Vera.Desktop;

public partial class App : Application
{
    private SingleInstance? _single;
    private AppHost? _host;
    private bool _errorShown;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // после смены языка новая копия ждёт, пока старая закроется
        var restarted = e.Args.Any(a => a.Equals("--restarted", StringComparison.OrdinalIgnoreCase));
        _single = SingleInstance.TryAcquire(restarted ? 15000 : 0);
        if (_single is null)
        {
            // Вера уже запущена — ей отправлен сигнал показать чат
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnDispatcherError;
        AppDomain.CurrentDomain.UnhandledException += (_, a) => Log.Error("domain", a.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, a) =>
        {
            Log.Error("task", a.Exception);
            a.SetObserved();
        };

        try
        {
            var hidden = e.Args.Any(a => a.Equals("--hidden", StringComparison.OrdinalIgnoreCase));
            _host = new AppHost(Dispatcher, hidden);
            _host.Start();
            _single.ListenForActivation(() => Dispatcher.BeginInvoke(() => _host?.ShowChat("chat")));
        }
        catch (Exception ex)
        {
            Log.Error("startup", ex);
            MessageBox.Show(Vera.Core.L10n.T("Вера не смогла запуститься:") + "\n\n" + ex.Message, Vera.Core.L10n.T("Вера"), MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    private void OnDispatcherError(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("ui", e.Exception);
        e.Handled = true; // Вера живёт в трее — не падаем из-за одной ошибки интерфейса
        if (_errorShown || _host is null || _host.Quitting) return;
        _errorShown = true;
        try
        {
            ConfirmDialog.Ask(null, "Вера: ошибка", "Что-то пошло не так, но я продолжаю работать. Подробности — в файле error.log в папке данных.",
                e.Exception.Message, "Понятно", "Закрыть");
        }
        catch (Exception)
        {
            // окно ошибки само упало — молчим
        }
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        _host?.Flush();
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            if (_host is { Quitting: false }) _host.Quit();
        }
        catch (Exception) { }
        _single?.Dispose();
        base.OnExit(e);
    }
}
