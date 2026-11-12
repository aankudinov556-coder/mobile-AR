using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using System.Windows.Interop;

namespace Vera.Desktop.Platform;

/// <summary>Сочетания клавиш в формате «Ctrl+Alt+V» (принимается и электроновское «CommandOrControl+…»).</summary>
public static class Accelerator
{
    private static readonly Dictionary<string, uint> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Space"] = 0x20, ["Tab"] = 0x09, ["Enter"] = 0x0D, ["Return"] = 0x0D, ["Esc"] = 0x1B, ["Escape"] = 0x1B,
        ["Backspace"] = 0x08, ["Delete"] = 0x2E, ["Del"] = 0x2E, ["Insert"] = 0x2D, ["Ins"] = 0x2D,
        ["Home"] = 0x24, ["End"] = 0x23, ["PageUp"] = 0x21, ["PageDown"] = 0x22,
        ["Up"] = 0x26, ["Down"] = 0x28, ["Left"] = 0x25, ["Right"] = 0x27,
        ["PrintScreen"] = 0x2C, ["Pause"] = 0x13, ["CapsLock"] = 0x14, ["NumLock"] = 0x90, ["ScrollLock"] = 0x91,
        ["Plus"] = 0xBB, ["="] = 0xBB, ["-"] = 0xBD, ["Minus"] = 0xBD, [","] = 0xBC, ["."] = 0xBE, ["/"] = 0xBF,
        [";"] = 0xBA, ["'"] = 0xDE, ["["] = 0xDB, ["]"] = 0xDD, ["\\"] = 0xDC, ["`"] = 0xC0,
        ["NumAdd"] = 0x6B, ["NumSub"] = 0x6D, ["NumMult"] = 0x6A, ["NumDiv"] = 0x6F, ["NumDec"] = 0x6E,
        ["VolumeUp"] = 0xAF, ["VolumeDown"] = 0xAE, ["VolumeMute"] = 0xAD,
        ["MediaNextTrack"] = 0xB0, ["MediaPreviousTrack"] = 0xB1, ["MediaStop"] = 0xB2, ["MediaPlayPause"] = 0xB3,
    };

    /// <summary>Разобрать строку в модификаторы и виртуальную клавишу для RegisterHotKey.</summary>
    public static bool TryParse(string? text, out uint modifiers, out uint vk)
    {
        modifiers = 0;
        vk = 0;
        var s = (text ?? "").Trim();
        if (s.Length == 0) return false;
        // «Ctrl++» — клавиша плюс
        if (s.EndsWith("++", StringComparison.Ordinal)) s = s[..^2] + "+Plus";
        var parts = s.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return false;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].ToLowerInvariant())
            {
                case "ctrl": case "control": case "commandorcontrol": case "cmdorctrl": case "command": case "cmd":
                    modifiers |= Native.MOD_CONTROL; break;
                case "alt": case "option": case "altgr":
                    modifiers |= Native.MOD_ALT; break;
                case "shift":
                    modifiers |= Native.MOD_SHIFT; break;
                case "super": case "win": case "windows": case "meta":
                    modifiers |= Native.MOD_WIN; break;
                default:
                    return false;
            }
        }
        var key = parts[^1];
        if (key.Length == 1 && char.IsAsciiLetterOrDigit(key[0])) { vk = char.ToUpperInvariant(key[0]); }
        else if (Named.TryGetValue(key, out var named)) vk = named;
        else if ((key.StartsWith('F') || key.StartsWith('f')) && int.TryParse(key[1..], out var fn) && fn is >= 1 and <= 24) vk = (uint)(0x70 + fn - 1);
        else if (key.StartsWith("num", StringComparison.OrdinalIgnoreCase) && int.TryParse(key[3..], out var nd) && nd is >= 0 and <= 9) vk = (uint)(0x60 + nd);
        else return false;
        // без модификатора разрешаем только F-клавиши и медиа-клавиши
        if (modifiers == 0 && !(vk is >= 0x70 and <= 0x87) && !(vk is >= 0xAD and <= 0xB3)) return false;
        return true;
    }

    /// <summary>«CommandOrControl+Alt+V» → «Ctrl + Alt + V».</summary>
    public static string Pretty(string? accel)
    {
        if (string.IsNullOrWhiteSpace(accel)) return "";
        var parts = accel.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.ToLowerInvariant() switch
            {
                "commandorcontrol" or "cmdorctrl" or "command" or "cmd" or "control" => "Ctrl",
                "super" or "meta" or "windows" => "Win",
                _ => p,
            });
        return string.Join(" + ", parts);
    }

    /// <summary>Строка для сохранения из нажатия в WPF (null — одиночный модификатор / неподходящее сочетание).</summary>
    public static string? FromKeyEvent(Key key, ModifierKeys mods, out string? error)
    {
        error = null;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin
            or Key.System or Key.None or Key.ImeProcessed or Key.DeadCharProcessed)
            return null;
        string? token = key switch
        {
            >= Key.A and <= Key.Z => ((char)('A' + (key - Key.A))).ToString(),
            >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
            >= Key.NumPad0 and <= Key.NumPad9 => "num" + (key - Key.NumPad0),
            >= Key.F1 and <= Key.F24 => "F" + (key - Key.F1 + 1),
            Key.Space => "Space",
            Key.Enter => "Enter",
            Key.Tab => "Tab",
            Key.Escape => "Esc",
            Key.Up => "Up", Key.Down => "Down", Key.Left => "Left", Key.Right => "Right",
            Key.Home => "Home", Key.End => "End", Key.PageUp => "PageUp", Key.PageDown => "PageDown",
            Key.Insert => "Insert",
            Key.PrintScreen => "PrintScreen",
            Key.Pause => "Pause",
            Key.Add => "NumAdd", Key.Subtract => "NumSub", Key.Multiply => "NumMult", Key.Divide => "NumDiv", Key.Decimal => "NumDec",
            Key.OemPlus => "Plus", Key.OemMinus => "-", Key.OemComma => ",", Key.OemPeriod => ".",
            Key.OemQuestion => "/", Key.OemSemicolon => ";", Key.OemQuotes => "'", Key.OemOpenBrackets => "[",
            Key.OemCloseBrackets => "]", Key.OemPipe => "\\", Key.OemTilde => "`",
            _ => null,
        };
        if (token is null) { error = "Эту клавишу нельзя назначить"; return null; }
        var parts = new List<string>();
        if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (mods.HasFlag(ModifierKeys.Windows)) parts.Add("Super");
        var isF = key is >= Key.F1 and <= Key.F24;
        if (parts.Count == 0 && !isF) { error = "Нужен Ctrl / Alt / Shift"; return null; }
        parts.Add(token);
        return string.Join("+", parts);
    }
}

/// <summary>Глобальные горячие клавиши через RegisterHotKey и скрытое окно сообщений.</summary>
public sealed class HotkeyManager : IDisposable
{
    private readonly HwndSource _source;
    private readonly Dictionary<int, Action> _actions = new();
    private int _nextId = 0xB000;

    public HotkeyManager()
    {
        var p = new HwndSourceParameters("VeraHotkeys")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
            ParentWindow = new IntPtr(-3), // HWND_MESSAGE
        };
        _source = new HwndSource(p);
        _source.AddHook(Hook);
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_HOTKEY && _actions.TryGetValue(wParam.ToInt32(), out var action))
        {
            handled = true;
            try { action(); } catch (Exception) { /* ошибка обработчика не должна ронять приложение */ }
        }
        return IntPtr.Zero;
    }

    /// <summary>Перерегистрировать все сочетания. Возвращает список ошибок для показа в настройках.</summary>
    public List<string> RegisterAll(IEnumerable<(string? Accel, Action Action)> keys)
    {
        UnregisterAll();
        var errors = new List<string>();
        foreach (var (accel, action) in keys)
        {
            if (string.IsNullOrWhiteSpace(accel)) continue;
            if (!Accelerator.TryParse(accel, out var mods, out var vk))
            {
                errors.Add($"{Accelerator.Pretty(accel)} — неверный формат");
                continue;
            }
            var id = _nextId++;
            if (!Native.RegisterHotKey(_source.Handle, id, mods | Native.MOD_NOREPEAT, vk))
            {
                errors.Add($"{Accelerator.Pretty(accel)} — занято другой программой");
                continue;
            }
            _actions[id] = action;
        }
        return errors;
    }

    public void UnregisterAll()
    {
        foreach (var id in _actions.Keys.ToList()) Native.UnregisterHotKey(_source.Handle, id);
        _actions.Clear();
    }

    public void Dispose()
    {
        UnregisterAll();
        _source.RemoveHook(Hook);
        _source.Dispose();
    }
}
