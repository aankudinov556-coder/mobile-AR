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
        ["Bg"] = "#0E0D12", ["Bg2"] = "#17161D", ["Bg3"] = "#24222C", ["Bg4"] = "#2E2C37", ["Line"] = "#3E3B48",
        ["Fg"] = "#F4F1E8", ["Muted"] = "#A6A1B2", ["Accent"] = "#F5CF3A", ["Accent2"] = "#F5CF3A", ["AccentSoft"] = "#24F5CF3A",
        ["AccentText"] = "#F5CF3A", ["OnAccent"] = "#16141A", ["Ink"] = "#000000", ["Paper"] = "#FFFDF5",
        ["Ok"] = "#6FD69B", ["Warn"] = "#FFB347", ["Err"] = "#FF7A6B", ["ErrSoft"] = "#1AFF7A6B", ["WarnSoft"] = "#1AFFB347",
        ["HoverOverlay"] = "#14FFFFFF", ["CodeBg"] = "#0A090D", ["CodeFg"] = "#ECE7DA", ["ToastErrBg"] = "#4A1F2A", ["ToastErrFg"] = "#FFD6DC",
        ["UserBubbleFg"] = "#16141A", ["VeraBubble"] = "#FFFDF5", ["VeraBubbleLine"] = "#000000", ["VeraBubbleFg"] = "#16141A",
        ["Rail"] = "#0A090D", ["Side"] = "#121117", ["Dot"] = "#1B1A21", ["Subtle"] = "#8E899A", ["Panel2"] = "#2E2C37",
    };

    private static readonly Dictionary<string, string> GoldLight = new()
    {
        ["Bg"] = "#F6F2E7", ["Bg2"] = "#FFFDF6", ["Bg3"] = "#EFE9DA", ["Bg4"] = "#E4DCC8", ["Line"] = "#CFC6B1",
        ["Fg"] = "#1D1B22", ["Muted"] = "#5F5949", ["Accent"] = "#F5CF3A", ["Accent2"] = "#F5CF3A", ["AccentSoft"] = "#40F5CF3A",
        ["AccentText"] = "#7A5800", ["OnAccent"] = "#16141A", ["Ink"] = "#16141A", ["Paper"] = "#FFFFFF",
        ["Ok"] = "#1E8A50", ["Warn"] = "#A86400", ["Err"] = "#C73B3B", ["ErrSoft"] = "#14C73B3B", ["WarnSoft"] = "#14A86400",
        ["HoverOverlay"] = "#0C000000", ["CodeBg"] = "#1D1B22", ["CodeFg"] = "#ECE7DA", ["ToastErrBg"] = "#FBE3E7", ["ToastErrFg"] = "#8A1C2C",
        ["UserBubbleFg"] = "#16141A", ["VeraBubble"] = "#FFFFFF", ["VeraBubbleLine"] = "#16141A", ["VeraBubbleFg"] = "#16141A",
        ["Rail"] = "#EAE3D2", ["Side"] = "#F0EADC", ["Dot"] = "#E2DAC6", ["Subtle"] = "#6E6757", ["Panel2"] = "#E4DCC8",
    };

    // ---------------------------------------------------------------- розовая: пиксельная Вера
    private static readonly Dictionary<string, string> PinkDark = new()
    {
        ["Bg"] = "#15131C", ["Bg2"] = "#1D1A27", ["Bg3"] = "#262232", ["Bg4"] = "#302B3F", ["Line"] = "#2F2A3D",
        ["Fg"] = "#ECE9F5", ["Muted"] = "#9A93AD", ["Accent"] = "#E0479A", ["Accent2"] = "#9B5CF0", ["AccentSoft"] = "#24E0479A",
        ["AccentText"] = "#F06BB0", ["OnAccent"] = "#FFFFFF", ["Ink"] = "#0B0A0E", ["Paper"] = "#FFFAFF",
        ["Ok"] = "#4FD18B", ["Warn"] = "#FFB347", ["Err"] = "#FF6B6B", ["ErrSoft"] = "#1AFF6B6B", ["WarnSoft"] = "#1AFFB347",
        ["HoverOverlay"] = "#14FFFFFF", ["CodeBg"] = "#0E0C14", ["CodeFg"] = "#E6E1F2", ["ToastErrBg"] = "#4A1F2A", ["ToastErrFg"] = "#FFD6DC",
        ["UserBubbleFg"] = "#FFFFFF", ["VeraBubble"] = "#FFFDFF", ["VeraBubbleLine"] = "#0B0A0E", ["VeraBubbleFg"] = "#1B1724",
        ["Rail"] = "#0F0D15", ["Side"] = "#18151F", ["Dot"] = "#221E2C", ["Subtle"] = "#8A8399", ["Panel2"] = "#302B3F",
    };

    private static readonly Dictionary<string, string> PinkLight = new()
    {
        ["Bg"] = "#F6F4FA", ["Bg2"] = "#FFFFFF", ["Bg3"] = "#F0EDF6", ["Bg4"] = "#E6E1EF", ["Line"] = "#E1DCEA",
        ["Fg"] = "#221D2E", ["Muted"] = "#6F6782", ["Accent"] = "#D63A8D", ["Accent2"] = "#8248E0", ["AccentSoft"] = "#1AD63A8D",
        ["AccentText"] = "#B82A75", ["OnAccent"] = "#FFFFFF", ["Ink"] = "#221D2E", ["Paper"] = "#FFFAFF",
        ["Ok"] = "#1E9E5A", ["Warn"] = "#B86E00", ["Err"] = "#D64545", ["ErrSoft"] = "#14D64545", ["WarnSoft"] = "#14B86E00",
        ["HoverOverlay"] = "#0C000000", ["CodeBg"] = "#221D2E", ["CodeFg"] = "#E6E1F2", ["ToastErrBg"] = "#FBE3E7", ["ToastErrFg"] = "#8A1C2C",
        ["UserBubbleFg"] = "#FFFFFF", ["VeraBubble"] = "#FFFFFF", ["VeraBubbleLine"] = "#221D2E", ["VeraBubbleFg"] = "#1B1724",
        ["Rail"] = "#ECE8F3", ["Side"] = "#F1EEF7", ["Dot"] = "#E3DEEC", ["Subtle"] = "#8A8399", ["Panel2"] = "#302B3F",
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
        // фон переписки — мелкий точечный узор, как растр в манге
        var dot = new GeometryDrawing(Brush(map["Dot"]), null, new EllipseGeometry(new Point(7, 7), 1.2, 1.2));
        var bg = new GeometryDrawing(Brush(map["Bg"]), null, new RectangleGeometry(new Rect(0, 0, 14, 14)));
        var dots = new DrawingBrush(new DrawingGroup { Children = { bg, dot } })
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, 14, 14),
            ViewportUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(0, 0, 14, 14),
            ViewboxUnits = BrushMappingMode.Absolute,
        };
        dots.Freeze();
        res["Dots"] = dots;
        var changed = dark != IsDark;
        IsDark = dark;
        Changed?.Invoke(dark);
        _ = changed;
    }
}
