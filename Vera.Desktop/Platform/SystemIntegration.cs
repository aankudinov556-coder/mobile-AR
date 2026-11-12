using System;
using System.Diagnostics;
using System.Threading;
using Microsoft.Win32;

namespace Vera.Desktop.Platform;

/// <summary>Автозапуск: HKCU\Software\Microsoft\Windows\CurrentVersion\Run → "Vera.exe" --hidden.</summary>
public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Vera";

    private static string Command()
    {
        var exe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "Vera.exe";
        return $"\"{exe}\" --hidden";
    }

    public static bool IsEnabled()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey, false);
            return k?.GetValue(ValueName) is string s && s.Length > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static void Apply(bool enable)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey, true) ?? Registry.CurrentUser.CreateSubKey(RunKey, true);
            if (enable)
            {
                var cmd = Command();
                if (!string.Equals(k.GetValue(ValueName) as string, cmd, StringComparison.OrdinalIgnoreCase)) k.SetValue(ValueName, cmd);
            }
            else if (k.GetValue(ValueName) is not null)
            {
                k.DeleteValue(ValueName, false);
            }
        }
        catch (Exception)
        {
            // политика запрещает запись в реестр — молча пропускаем
        }
    }
}

/// <summary>Один экземпляр: именованный мьютекс + событие «покажись» для уже запущенной Веры.</summary>
public sealed class SingleInstance : IDisposable
{
    // Имя мьютекса совпадает с AppMutex в установщике (vera.iss)
    public const string MutexName = "VeraAssistantSingleInstance";
    private const string EventName = "VeraAssistantActivate";

    private readonly Mutex _mutex;
    private EventWaitHandle? _event;
    private RegisteredWaitHandle? _wait;

    private SingleInstance(Mutex mutex) => _mutex = mutex;

    /// <summary>null — Вера уже запущена (ей отправлен сигнал показаться).</summary>
    public static SingleInstance? TryAcquire()
    {
        var mutex = new Mutex(true, MutexName, out var created);
        if (!created)
        {
            // мьютекс мог остаться «брошенным» после падения — тогда он наш
            try
            {
                if (!mutex.WaitOne(0))
                {
                    SignalExisting();
                    mutex.Dispose();
                    return null;
                }
            }
            catch (AbandonedMutexException)
            {
                // предыдущий процесс упал — продолжаем как единственный
            }
        }
        return new SingleInstance(mutex);
    }

    private static void SignalExisting()
    {
        try
        {
            Native.AllowSetForegroundWindow(-1); // ASFW_ANY — пусть первая Вера сможет вывести окно вперёд
            using var ev = EventWaitHandle.OpenExisting(EventName);
            ev.Set();
        }
        catch (Exception)
        {
            // первая копия ещё не успела создать событие
        }
    }

    /// <summary>Слушать сигнал от вторых копий (обработчик вызывается в фоновом потоке).</summary>
    public void ListenForActivation(Action onActivate)
    {
        _event = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
        _wait = ThreadPool.RegisterWaitForSingleObject(_event, (_, _) => onActivate(), null, Timeout.Infinite, false);
    }

    public void Dispose()
    {
        _wait?.Unregister(null);
        _event?.Dispose();
        try { _mutex.ReleaseMutex(); } catch (Exception) { }
        _mutex.Dispose();
    }
}
