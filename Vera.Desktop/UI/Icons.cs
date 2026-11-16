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
        ["phone"] = "M8,2.5 L16,2.5 A2,2 0 0 1 18,4.5 L18,19.5 A2,2 0 0 1 16,21.5 L8,21.5 A2,2 0 0 1 6,19.5 L6,4.5 A2,2 0 0 1 8,2.5 M11,18 L13,18",
        ["clip"] = "M20,11.5 L12,19.5 A5,5 0 0 1 5,12.5 L13.5,4 A3.3,3.3 0 0 1 18.2,8.7 L9.7,17.2 A1.7,1.7 0 0 1 7.3,14.8 L15.1,7",
        ["expand"] = "M14,4 L20,4 L20,10 M10,20 L4,20 L4,14 M20,4 L13,11 M4,20 L11,13",
        ["shrink"] = "M4,14 L10,14 L10,20 M20,10 L14,10 L14,4 M10,14 L3,21 M14,10 L21,3",
        ["chat"] = "M21,15 A2,2 0 0 1 19,17 L7,17 L3,21 L3,5 A2,2 0 0 1 5,3 L19,3 A2,2 0 0 1 21,5 Z",
        ["bell"] = "M18,8 A6,6 0 0 0 6,8 C6,15 3,17 3,17 L21,17 C21,17 18,15 18,8 M13.73,21 A2,2 0 0 1 10.27,21",
        ["star"] = "M12,2 L15.09,8.26 L22,9.27 L17,14.14 L18.18,21.02 L12,17.77 L5.82,21.02 L7,14.14 L2,9.27 L8.91,8.26 Z",
        ["gear"] = "M9,12 A3,3 0 1 0 15,12 A3,3 0 1 0 9,12 Z " +
                   "M12,1.5 L14,1.5 L14.6,4.3 L16.5,5.1 L18.9,3.6 L20.4,5.1 L18.9,7.5 L19.7,9.4 L22.5,10 L22.5,14 L19.7,14.6 L18.9,16.5 " +
                   "L20.4,18.9 L18.9,20.4 L16.5,18.9 L14.6,19.7 L14,22.5 L10,22.5 L9.4,19.7 L7.5,18.9 L5.1,20.4 L3.6,18.9 L5.1,16.5 " +
                   "L4.3,14.6 L1.5,14 L1.5,10 L4.3,9.4 L5.1,7.5 L3.6,5.1 L5.1,3.6 L7.5,5.1 L9.4,4.3 L10,1.5 Z",
        ["check"] = "M20,6 L9,17 L4,12",
        ["code"] = "M8,7 L3,12 L8,17 M16,7 L21,12 L16,17 M14,4 L10,20",
        ["power"] = "M12,3 L12,11 M6.3,7.5 A8,8 0 1 0 17.7,7.5",
        ["window"] = "M5.5,4.5 L18.5,4.5 A2.5,2.5 0 0 1 21,7 L21,17 A2.5,2.5 0 0 1 18.5,19.5 L5.5,19.5 A2.5,2.5 0 0 1 3,17 L3,7 A2.5,2.5 0 0 1 5.5,4.5 Z M3,9 L21,9",
        ["globe"] = "M3.5,12 A8.5,8.5 0 1 0 20.5,12 A8.5,8.5 0 1 0 3.5,12 Z M3.5,12 L20.5,12 M12,3.5 C14.6,6.1 14.6,17.9 12,20.5 M12,3.5 C9.4,6.1 9.4,17.9 12,20.5",
        ["volume"] = "M4,9.5 L7.5,9.5 L12,5.5 L12,18.5 L7.5,14.5 L4,14.5 Z M16,9 A4,4 0 0 1 16,15 M18.5,6.5 A7.5,7.5 0 0 1 18.5,17.5",
        ["media"] = "M6,5.5 L6,18.5 L15,12 Z M18,6 L18,18",
        ["type"] = "M5,6 L19,6 M12,6 L12,18 M9,18 L15,18",
        ["screenshot"] = "M4,8 L4,5 L7,5 M17,5 L20,5 L20,8 M20,16 L20,19 L17,19 M7,19 L4,19 L4,16 M9,12 A3,3 0 1 0 15,12 A3,3 0 1 0 9,12 Z",
        ["terminal"] = "M5.5,4.5 L18.5,4.5 A2.5,2.5 0 0 1 21,7 L21,17 A2.5,2.5 0 0 1 18.5,19.5 L5.5,19.5 A2.5,2.5 0 0 1 3,17 L3,7 A2.5,2.5 0 0 1 5.5,4.5 Z M7,10 L10,12.5 L7,15 M12.5,15 L17,15",
        ["branch"] = "M12,3.5 L20.5,12 L12,20.5 L3.5,12 Z",
        ["hourglass"] = "M7,3.5 L17,3.5 M7,20.5 L17,20.5 M8,3.5 L8,5.5 A4,4 0 0 0 16,5.5 L16,3.5 M8,20.5 L8,18.5 A4,4 0 0 1 16,18.5 L16,20.5",
        ["ask"] = "M4,5 L20,5 L20,16 L9,16 L4,20 Z M10,9 A2,2 0 1 1 12.5,10.9 C12.1,11.1 12,11.4 12,11.8 M12,13.5 L12.01,13.5",
        ["sparkle"] = "M12,3 L13.8,8.2 L19,10 L13.8,11.8 L12,17 L10.2,11.8 L5,10 L10.2,8.2 Z",
        ["voice"] ="M4,10 L4,14 M8,7 L8,17 M12,4 L12,20 M16,8 L16,16 M20,11 L20,13",
        ["keyboard"] = "M5,6 L19,6 A2,2 0 0 1 21,8 L21,16 A2,2 0 0 1 19,18 L5,18 A2,2 0 0 1 3,16 L3,8 A2,2 0 0 1 5,6 Z M7,10 L7.01,10 M11,10 L11.01,10 M15,10 L15.01,10 M7,14 L17,14",
        ["nodes"] = "M4.5,3.5 L8.5,3.5 A1.5,1.5 0 0 1 10,5 L10,8 A1.5,1.5 0 0 1 8.5,9.5 L4.5,9.5 A1.5,1.5 0 0 1 3,8 L3,5 A1.5,1.5 0 0 1 4.5,3.5 Z M15.5,14.5 L19.5,14.5 A1.5,1.5 0 0 1 21,16 L21,19 A1.5,1.5 0 0 1 19.5,20.5 L15.5,20.5 A1.5,1.5 0 0 1 14,19 L14,16 A1.5,1.5 0 0 1 15.5,14.5 Z M6.5,9.5 L6.5,12.5 A2,2 0 0 0 8.5,14.5 L14,14.5",
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
