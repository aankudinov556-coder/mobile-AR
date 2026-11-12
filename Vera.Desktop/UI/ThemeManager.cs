using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace Vera.Desktop.UI;

/// <summary>Тёмная и светлая темы (цвета из app.css). Кисти подменяются в ресурсах приложения — DynamicResource обновится сам.</summary>
public static class ThemeManager
{
    private static readonly Dictionary<string, string> Dark = new()
    {
        ["Bg"] = "#15131C", ["Bg2"] = "#1D1A27", ["Bg3"] = "#262232", ["Bg4"] = "#302B3F", ["Line"] = "#2F2A3D",
        ["Fg"] = "#ECE9F5", ["Muted"] = "#9A93AD", ["Accent"] = "#E0479A", ["Accent2"] = "#9B5CF0", ["AccentSoft"] = "#24E0479A",
        ["Ok"] = "#4FD18B", ["Warn"] = "#FFB347", ["Err"] = "#FF6B6B", ["ErrSoft"] = "#1AFF6B6B", ["WarnSoft"] = "#1AFFB347",
        ["HoverOverlay"] = "#14FFFFFF", ["CodeBg"] = "#0E0C14", ["CodeFg"] = "#E6E1F2", ["ToastErrBg"] = "#4A1F2A", ["ToastErrFg"] = "#FFD6DC",
    };

    private static readonly Dictionary<string, string> Light = new()
    {
        ["Bg"] = "#F6F4FA", ["Bg2"] = "#FFFFFF", ["Bg3"] = "#F0EDF6", ["Bg4"] = "#E6E1EF", ["Line"] = "#E1DCEA",
        ["Fg"] = "#221D2E", ["Muted"] = "#6F6782", ["Accent"] = "#D63A8D", ["Accent2"] = "#8248E0", ["AccentSoft"] = "#1AD63A8D",
        ["Ok"] = "#1E9E5A", ["Warn"] = "#B86E00", ["Err"] = "#D64545", ["ErrSoft"] = "#14D64545", ["WarnSoft"] = "#14B86E00",
        ["HoverOverlay"] = "#0C000000", ["CodeBg"] = "#221D2E", ["CodeFg"] = "#E6E1F2", ["ToastErrBg"] = "#FBE3E7", ["ToastErrFg"] = "#8A1C2C",
    };

    public static bool IsDark { get; private set; } = true;

    public static event Action<bool>? Changed;

    /// <summary>Светлая ли тема приложений в Windows (для режима «как в системе»).</summary>
    public static bool SystemPrefersLight()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("AppsUseLightTheme") is int v && v == 1;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static bool ResolveDark(string? setting) => setting switch
    {
        "light" => false,
        "dark" => true,
        _ => !SystemPrefersLight(),
    };

    private static SolidColorBrush Brush(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    /// <summary>Применить тему из настроек ("auto" | "dark" | "light").</summary>
    public static void Apply(string? setting)
    {
        var dark = ResolveDark(setting);
        var res = Application.Current.Resources;
        var map = dark ? Dark : Light;
        foreach (var (k, v) in map) res[k] = Brush(v);
        var grad = new LinearGradientBrush(
            (Color)ColorConverter.ConvertFromString(map["Accent"]),
            (Color)ColorConverter.ConvertFromString(map["Accent2"]),
            new Point(0, 0), new Point(1, 1));
        grad.Freeze();
        res["AccentGrad"] = grad;
        var changed = dark != IsDark;
        IsDark = dark;
        if (changed) Changed?.Invoke(dark);
    }
}
