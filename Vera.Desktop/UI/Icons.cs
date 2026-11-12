using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Vera.Desktop.UI;

/// <summary>Контурные иконки 24×24 (те же, что SVG-символы в app.html). Цвет — от Foreground кнопки.</summary>
public static class Icons
{
    private static readonly Dictionary<string, string> Paths = new()
    {
        ["send"] = "M22,2 L11,13 M22,2 L15,22 L11,13 L2,9 Z",
        ["mic"] = "M12,2 A3,3 0 0 1 15,5 L15,11 A3,3 0 0 1 9,11 L9,5 A3,3 0 0 1 12,2 Z M5,10 L5,11 A7,7 0 0 0 19,11 L19,10 M12,18 L12,22",
        ["stop"] = "M8,6 L16,6 A2,2 0 0 1 18,8 L18,16 A2,2 0 0 1 16,18 L8,18 A2,2 0 0 1 6,16 L6,8 A2,2 0 0 1 8,6 Z",
        ["camera"] = "M23,19 A2,2 0 0 1 21,21 L3,21 A2,2 0 0 1 1,19 L1,8 A2,2 0 0 1 3,6 L7,6 L9,3 L15,3 L17,6 L21,6 A2,2 0 0 1 23,8 Z M8,13 A4,4 0 1 0 16,13 A4,4 0 1 0 8,13 Z",
        ["trash"] = "M3,6 L21,6 M19,6 L18,20 A2,2 0 0 1 16,22 L8,22 A2,2 0 0 1 6,20 L5,6 M10,11 L10,17 M14,11 L14,17 M9,6 L9,4 A1,1 0 0 1 10,3 L14,3 A1,1 0 0 1 15,4 L15,6",
        ["plus"] = "M12,5 L12,19 M5,12 L19,12",
        ["chat"] = "M21,15 A2,2 0 0 1 19,17 L7,17 L3,21 L3,5 A2,2 0 0 1 5,3 L19,3 A2,2 0 0 1 21,5 Z",
        ["bell"] = "M18,8 A6,6 0 0 0 6,8 C6,15 3,17 3,17 L21,17 C21,17 18,15 18,8 M13.73,21 A2,2 0 0 1 10.27,21",
        ["star"] = "M12,2 L15.09,8.26 L22,9.27 L17,14.14 L18.18,21.02 L12,17.77 L5.82,21.02 L7,14.14 L2,9.27 L8.91,8.26 Z",
        ["gear"] = "M9,12 A3,3 0 1 0 15,12 A3,3 0 1 0 9,12 Z " +
                   "M12,1.5 L14,1.5 L14.6,4.3 L16.5,5.1 L18.9,3.6 L20.4,5.1 L18.9,7.5 L19.7,9.4 L22.5,10 L22.5,14 L19.7,14.6 L18.9,16.5 " +
                   "L20.4,18.9 L18.9,20.4 L16.5,18.9 L14.6,19.7 L14,22.5 L10,22.5 L9.4,19.7 L7.5,18.9 L5.1,20.4 L3.6,18.9 L5.1,16.5 " +
                   "L4.3,14.6 L1.5,14 L1.5,10 L4.3,9.4 L5.1,7.5 L3.6,5.1 L5.1,3.6 L7.5,5.1 L9.4,4.3 L10,1.5 Z",
        ["check"] = "M20,6 L9,17 L4,12",
        ["x"] = "M18,6 L6,18 M6,6 L18,18",
        ["minus"] = "M5,12 L19,12",
        ["refresh"] = "M23,4 L23,10 L17,10 M20.49,15 A9,9 0 1 1 18.37,5.64 L23,10",
        ["edit"] = "M12,20 L21,20 M16.5,3.5 A2.12,2.12 0 0 1 19.5,6.5 L7,19 L3,20 L4,16 Z",
        ["copy"] = "M11,9 L20,9 A2,2 0 0 1 22,11 L22,20 A2,2 0 0 1 20,22 L11,22 A2,2 0 0 1 9,20 L9,11 A2,2 0 0 1 11,9 Z M5,15 L4,15 A2,2 0 0 1 2,13 L2,4 A2,2 0 0 1 4,2 L13,2 A2,2 0 0 1 15,4 L15,5",
        ["repeat"] = "M17,1 L21,5 L17,9 M3,11 L3,9 A4,4 0 0 1 7,5 L21,5 M7,23 L3,19 L7,15 M21,13 L21,15 A4,4 0 0 1 17,19 L3,19",
        ["folder"] = "M22,19 A2,2 0 0 1 20,21 L4,21 A2,2 0 0 1 2,19 L2,5 A2,2 0 0 1 4,3 L9,3 L11,6 L20,6 A2,2 0 0 1 22,8 Z",
        ["play"] = "M6,3 L20,12 L6,21 Z",
        ["clock"] = "M2,12 A10,10 0 1 0 22,12 A10,10 0 1 0 2,12 Z M12,6 L12,12 L16,14",
    };

    private static readonly Dictionary<string, Geometry> Cache = new();

    public static Geometry Geometry(string name)
    {
        if (Cache.TryGetValue(name, out var g)) return g;
        try
        {
            g = System.Windows.Media.Geometry.Parse(Paths.TryGetValue(name, out var d) ? d : "");
        }
        catch (Exception)
        {
            g = System.Windows.Media.Geometry.Empty;
        }
        if (g.CanFreeze) g.Freeze();
        Cache[name] = g;
        return g;
    }

    /// <summary>Иконка размером size×size, цвет линии берётся из унаследованного Foreground.</summary>
    public static FrameworkElement Make(string name, double size = 18)
    {
        var path = new Path
        {
            Data = Geometry(name),
            StrokeThickness = 2,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Width = 24,
            Height = 24,
            Stretch = Stretch.None,
        };
        path.SetBinding(Shape.StrokeProperty, new Binding { Path = new PropertyPath(TextElement.ForegroundProperty), RelativeSource = RelativeSource.Self });
        return new Viewbox { Width = size, Height = size, Child = path, IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center };
    }
}
