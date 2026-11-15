using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace Vera.Desktop.UI;

/// <summary>Тёмная и светлая темы (цвета из app.css). Кисти подменяются в ресурсах приложения — DynamicResource обновится сам.</summary>
public static class ThemeManager
{
    // ---------------------------------------------------------------- «золото нимба»: скины halo и чиби
    private static readonly Dictionary<string, string> GoldDark = new()
    {
        ["Bg"] = "#111015", ["Bg2"] = "#1A1920", ["Bg3"] = "#23222B", ["Bg4"] = "#2E2C37", ["Line"] = "#34323E",
        ["Fg"] = "#F4F1E8", ["Muted"] = "#A29DAE", ["Accent"] = "#F5CF3A", ["Accent2"] = "#FFB020", ["AccentSoft"] = "#24F5CF3A",
        ["AccentText"] = "#F5CF3A", ["OnAccent"] = "#16141A", ["Ink"] = "#050407", ["Paper"] = "#FFFDF4",
        ["Ok"] = "#5FD38F", ["Warn"] = "#FFB347", ["Err"] = "#FF6B6B", ["ErrSoft"] = "#1AFF6B6B", ["WarnSoft"] = "#1AFFB347",
        ["HoverOverlay"] = "#14FFFFFF", ["CodeBg"] = "#0B0A0F", ["CodeFg"] = "#ECE7DA", ["ToastErrBg"] = "#4A1F2A", ["ToastErrFg"] = "#FFD6DC",
        ["UserBubbleFg"] = "#16141A", ["VeraBubble"] = "#1E1D25", ["VeraBubbleLine"] = "#3B3946",
    };

    private static readonly Dictionary<string, string> GoldLight = new()
    {
        ["Bg"] = "#F6F2E7", ["Bg2"] = "#FFFDF6", ["Bg3"] = "#EFE9DA", ["Bg4"] = "#E4DCC8", ["Line"] = "#DAD1BD",
        ["Fg"] = "#1D1B22", ["Muted"] = "#6E6757", ["Accent"] = "#F2C21B", ["Accent2"] = "#F59E0B", ["AccentSoft"] = "#33F2C21B",
        ["AccentText"] = "#8A6300", ["OnAccent"] = "#1D1B22", ["Ink"] = "#1D1B22", ["Paper"] = "#FFFDF4",
        ["Ok"] = "#1E9E5A", ["Warn"] = "#B86E00", ["Err"] = "#D64545", ["ErrSoft"] = "#14D64545", ["WarnSoft"] = "#14B86E00",
        ["HoverOverlay"] = "#0C000000", ["CodeBg"] = "#1D1B22", ["CodeFg"] = "#ECE7DA", ["ToastErrBg"] = "#FBE3E7", ["ToastErrFg"] = "#8A1C2C",
        ["UserBubbleFg"] = "#1D1B22", ["VeraBubble"] = "#FFFFFF", ["VeraBubbleLine"] = "#1D1B22",
    };

    // ---------------------------------------------------------------- розовая: пиксельная Вера
    private static readonly Dictionary<string, string> PinkDark = new()
    {
        ["Bg"] = "#15131C", ["Bg2"] = "#1D1A27", ["Bg3"] = "#262232", ["Bg4"] = "#302B3F", ["Line"] = "#2F2A3D",
        ["Fg"] = "#ECE9F5", ["Muted"] = "#9A93AD", ["Accent"] = "#E0479A", ["Accent2"] = "#9B5CF0", ["AccentSoft"] = "#24E0479A",
        ["AccentText"] = "#F06BB0", ["OnAccent"] = "#FFFFFF", ["Ink"] = "#0B0A0E", ["Paper"] = "#FFFAFF",
        ["Ok"] = "#4FD18B", ["Warn"] = "#FFB347", ["Err"] = "#FF6B6B", ["ErrSoft"] = "#1AFF6B6B", ["WarnSoft"] = "#1AFFB347",
        ["HoverOverlay"] = "#14FFFFFF", ["CodeBg"] = "#0E0C14", ["CodeFg"] = "#E6E1F2", ["ToastErrBg"] = "#4A1F2A", ["ToastErrFg"] = "#FFD6DC",
        ["UserBubbleFg"] = "#FFFFFF", ["VeraBubble"] = "#1D1A27", ["VeraBubbleLine"] = "#2F2A3D",
    };

    private static readonly Dictionary<string, string> PinkLight = new()
    {
        ["Bg"] = "#F6F4FA", ["Bg2"] = "#FFFFFF", ["Bg3"] = "#F0EDF6", ["Bg4"] = "#E6E1EF", ["Line"] = "#E1DCEA",
        ["Fg"] = "#221D2E", ["Muted"] = "#6F6782", ["Accent"] = "#D63A8D", ["Accent2"] = "#8248E0", ["AccentSoft"] = "#1AD63A8D",
        ["AccentText"] = "#B82A75", ["OnAccent"] = "#FFFFFF", ["Ink"] = "#221D2E", ["Paper"] = "#FFFAFF",
        ["Ok"] = "#1E9E5A", ["Warn"] = "#B86E00", ["Err"] = "#D64545", ["ErrSoft"] = "#14D64545", ["WarnSoft"] = "#14B86E00",
        ["HoverOverlay"] = "#0C000000", ["CodeBg"] = "#221D2E", ["CodeFg"] = "#E6E1F2", ["ToastErrBg"] = "#FBE3E7", ["ToastErrFg"] = "#8A1C2C",
        ["UserBubbleFg"] = "#FFFFFF", ["VeraBubble"] = "#FFFFFF", ["VeraBubbleLine"] = "#E1DCEA",
    };

    private static string _theme = "auto";
    private static string _accent = "pink";

    /// <summary>Акцент текущей темы: "gold" | "pink".</summary>
    public static string Accent => _accent;

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

    /// <summary>Применить тему ("auto" | "dark" | "light") и акцент по скину (halo/чиби — золото, пиксельная — розовый).</summary>
    public static void Apply(string? setting, string? skin = null)
    {
        _theme = setting ?? "auto";
        if (skin is not null) _accent = Vera.Core.Sprites.Skins.AccentFor(skin);
        var dark = ResolveDark(_theme);
        var res = Application.Current.Resources;
        var map = (_accent, dark) switch
        {
            ("gold", true) => GoldDark,
            ("gold", false) => GoldLight,
            (_, true) => PinkDark,
            _ => PinkLight,
        };
        foreach (var (k, v) in map) res[k] = Brush(v);
        var grad = new LinearGradientBrush(
            (Color)ColorConverter.ConvertFromString(map["Accent"]),
            (Color)ColorConverter.ConvertFromString(map["Accent2"]),
            new Point(0, 0), new Point(1, 1));
        grad.Freeze();
        res["AccentGrad"] = grad;
        res["InkColor"] = (Color)ColorConverter.ConvertFromString(map["Ink"]);
        var changed = dark != IsDark;
        IsDark = dark;
        Changed?.Invoke(dark);
        _ = changed;
    }
}
