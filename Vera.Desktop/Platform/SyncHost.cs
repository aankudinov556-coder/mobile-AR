using System;
using System.Runtime.InteropServices;
using Vera.Core;
using Vera.Core.Sync;

namespace Vera.Desktop.Platform;

/// <summary>
/// ПК — сервер синхронизации: запускается, когда в настройках включено «Принимать телефон»,
/// перезапускается при смене порта. Сообщает ядру, когда пользователь последний раз трогал мышь/клавиатуру.
/// </summary>
public sealed class SyncHost : IDisposable
{
    private readonly VeraCore _core;
    private SyncServer? _server;
    private int _port;

    public SyncHost(VeraCore core)
    {
        _core = core;
        core.Sync.LastActive = LastInput;
    }

    public bool Running => _server?.Running == true;
    public int Port => _server?.Port ?? 0;
    public string? Error { get; private set; }

    /// <summary>Состояние изменилось (запущен / остановлен / ошибка).</summary>
    public event Action? Changed;

    /// <summary>Привести сервер в соответствие с настройками.</summary>
    public void Apply()
    {
        var s = _core.Settings.Current.Sync;
        if (!s.Enabled)
        {
            Stop();
            return;
        }
        if (_server is not null && _port == s.Port) return;
        Stop();
        try
        {
            var server = new SyncServer(_core.Sync);
            server.Start(s.Port);
            _server = server;
            _port = s.Port;
            Error = server.Error;
        }
        catch (Exception e)
        {
            Error = e.Message;
            Log.Error("sync server", e);
        }
        Changed?.Invoke();
    }

    private void Stop()
    {
        if (_server is null) return;
        _server.Dispose();
        _server = null;
        Changed?.Invoke();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    /// <summary>Когда пользователь последний раз трогал мышь или клавиатуру.</summary>
    public static DateTimeOffset? LastInput()
    {
        try
        {
            var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
            if (!GetLastInputInfo(ref info)) return null;
            var idleMs = unchecked((uint)Environment.TickCount - info.dwTime);
            return DateTimeOffset.Now - TimeSpan.FromMilliseconds(idleMs);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void Dispose() => Stop();
}
