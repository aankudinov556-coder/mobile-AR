using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Vera.Desktop.UI;

/// <summary>Вложенные свойства для шаблонов и маленькие фабрики элементов (интерфейс строится в коде).</summary>
public static class Ui
{
    // ------------------------------------------------------------ attached properties

    public static readonly DependencyProperty RadiusProperty = DependencyProperty.RegisterAttached(
        "Radius", typeof(CornerRadius), typeof(Ui), new FrameworkPropertyMetadata(new CornerRadius(9)));

    public static CornerRadius GetRadius(DependencyObject d) => (CornerRadius)d.GetValue(RadiusProperty);
    public static void SetRadius(DependencyObject d, CornerRadius v) => d.SetValue(RadiusProperty, v);

    /// <summary>«Включённое» состояние кнопки (микрофон слушает, кнопка «стоп» и т.п.).</summary>
    public static readonly DependencyProperty ActiveProperty = DependencyProperty.RegisterAttached(
        "Active", typeof(bool), typeof(Ui), new FrameworkPropertyMetadata(false));

    public static bool GetActive(DependencyObject d) => (bool)d.GetValue(ActiveProperty);
    public static void SetActive(DependencyObject d, bool v) => d.SetValue(ActiveProperty, v);

    // ------------------------------------------------------------ fonts

    public static readonly FontFamily Font = new("Segoe UI Variable Text, Segoe UI, Segoe UI Emoji, Segoe UI Symbol");
    public static readonly FontFamily Mono = new("Cascadia Code, Cascadia Mono, Consolas, Courier New");

    public static Style? Style(string key) => Application.Current.TryFindResource(key) as Style;

    public static T Res<T>(this T el, DependencyProperty dp, string key) where T : FrameworkElement
    {
        el.SetResourceReference(dp, key);
        return el;
    }

    // ------------------------------------------------------------ text

    public static TextBlock Text(string text, double size = 14, string brush = "Fg", FontWeight? weight = null, bool wrap = true)
    {
        var t = new TextBlock
        {
            Text = text,
            FontSize = size,
            TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
            FontWeight = weight ?? FontWeights.Normal,
        };
        if (!wrap) t.TextTrimming = TextTrimming.CharacterEllipsis;
        t.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return t;
    }

    public static TextBlock Muted(string text, double size = 12) => Text(text, size, "Muted");

    public static TextBlock Title(string text) => Text(text, 14.5, "Fg", FontWeights.Bold);

    // ------------------------------------------------------------ buttons

    /// <summary>Содержимое кнопки: иконка + подпись.</summary>
    public static object Content(string? text, string? icon, double iconSize = 18)
    {
        if (icon is null) return text ?? "";
        var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        sp.Children.Add(Icons.Make(icon, iconSize));
        if (!string.IsNullOrEmpty(text))
            sp.Children.Add(new TextBlock { Text = text, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        return sp;
    }

    public static Button Button(string? text, string style = "Btn.Ghost", string? icon = null, Action? click = null, string? tip = null)
    {
        var b = new Button { Content = Content(text, icon, style == "Btn.GhostSmall" ? 14 : 18), Style = Style(style) };
        if (tip is not null) b.ToolTip = tip;
        if (click is not null) b.Click += (_, _) => click();
        return b;
    }

    public static Button IconButton(string icon, string tip, Action? click = null, double size = 36, double iconSize = 18)
    {
        var b = new Button { Content = Icons.Make(icon, iconSize), Style = Style("Btn.Icon"), ToolTip = tip, Width = size, Height = size };
        if (click is not null) b.Click += (_, _) => click();
        return b;
    }

    public static Button LinkButton(string text, Action click)
    {
        var tb = new TextBlock { Text = text, TextDecorations = TextDecorations.Underline };
        var b = new Button { Content = tb, Style = Style("Btn.Link") };
        b.Click += (_, _) => click();
        return b;
    }

    // ------------------------------------------------------------ layout

    public static Border Card(params UIElement[] children)
    {
        var sp = new StackPanel();
        foreach (var c in children) sp.Children.Add(c);
        return new Border { Style = Style("Card"), Child = sp };
    }

    public static StackPanel CardBody(Border card) => (StackPanel)card.Child;

    /// <summary>Подпись над полем (как .field в app.css), справа от подписи — необязательное значение/кнопка.</summary>
    public static StackPanel Field(string label, UIElement control, UIElement? extra = null)
    {
        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 5) };
        head.Children.Add(Text(label, 12.5, "Muted", FontWeights.SemiBold, wrap: false));
        if (extra is not null)
        {
            if (extra is FrameworkElement fe) fe.Margin = new Thickness(8, 0, 0, 0);
            head.Children.Add(extra);
        }
        var sp = new StackPanel { Margin = new Thickness(0, 8, 0, 8) };
        sp.Children.Add(head);
        sp.Children.Add(control);
        return sp;
    }

    /// <summary>Строка элементов; grow — индексы элементов, растягиваемых на остаток ширины.</summary>
    public static Grid Row(IEnumerable<UIElement> items, double gap = 8, params int[] grow)
    {
        var g = new Grid();
        var i = 0;
        foreach (var item in items)
        {
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = Array.IndexOf(grow, i) >= 0 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
            if (i > 0 && item is FrameworkElement fe)
                fe.Margin = new Thickness(gap, fe.Margin.Top, fe.Margin.Right, fe.Margin.Bottom);
            Grid.SetColumn(item, i);
            g.Children.Add(item);
            i++;
        }
        return g;
    }

    public static WrapPanel Wrap(params UIElement[] items)
    {
        var w = new WrapPanel();
        foreach (var it in items)
        {
            if (it is FrameworkElement fe) fe.Margin = new Thickness(0, 0, 6, 6);
            w.Children.Add(it);
        }
        return w;
    }

    /// <summary>Подсказка-плашка (.note warn/err).</summary>
    public static Border Note(string text, bool error = false)
    {
        var tb = Text(text, 12, error ? "Err" : "Warn");
        var b = new Border { CornerRadius = new CornerRadius(9), Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(0, 8, 0, 8), Child = tb };
        b.SetResourceReference(Border.BackgroundProperty, error ? "ErrSoft" : "WarnSoft");
        return b;
    }

    /// <summary>Вложенный блок с полосой слева (.sub).</summary>
    public static Border Sub(params UIElement[] children)
    {
        var sp = new StackPanel();
        foreach (var c in children) sp.Children.Add(c);
        var b = new Border { BorderThickness = new Thickness(2, 0, 0, 0), Padding = new Thickness(12, 0, 0, 0), Margin = new Thickness(2, 6, 0, 6), Child = sp };
        b.SetResourceReference(Border.BorderBrushProperty, "Line");
        return b;
    }

    public static CheckBox Toggle(string label, string? hint = null)
    {
        var tb = Text(label, 14);
        if (hint is not null)
        {
            tb.Inlines.Add(new System.Windows.Documents.Run("  " + hint) { FontSize = 12 });
            ((System.Windows.Documents.Run)tb.Inlines.LastInline).SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "Muted");
        }
        return new CheckBox { Content = tb };
    }

    public static ScrollViewer Scroll(UIElement content, double padding = 12) => new()
    {
        Content = new Border { Padding = new Thickness(padding), Child = content },
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        Focusable = false,
    };

    public static ComboBox Combo(IEnumerable<(string Value, string Text)> options)
    {
        var c = new ComboBox();
        SetOptions(c, options);
        return c;
    }

    public static void SetOptions(ComboBox c, IEnumerable<(string Value, string Text)> options)
    {
        c.Items.Clear();
        foreach (var (v, t) in options) c.Items.Add(new ComboBoxItem { Content = t, Tag = v });
    }

    public static string? Selected(ComboBox c) => (c.SelectedItem as ComboBoxItem)?.Tag as string;

    public static void Select(ComboBox c, string? value)
    {
        foreach (var it in c.Items)
            if (it is ComboBoxItem ci && (ci.Tag as string) == (value ?? ""))
            {
                c.SelectedItem = ci;
                return;
            }
        c.SelectedIndex = -1;
    }

    public static Slider Slider(double min, double max, double step) => new()
    {
        Minimum = min,
        Maximum = max,
        SmallChange = step,
        LargeChange = step,
        TickFrequency = step,
        IsSnapToTickEnabled = true,
    };

    public static RadioButton Radio(string text, string group, string style) =>
        new() { Content = text, GroupName = group, Style = Style(style) };
}
