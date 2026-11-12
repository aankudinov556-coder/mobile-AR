using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using Vera.Core.Markdown;

namespace Vera.Desktop.UI;

/// <summary>Markdown (через MarkdownLite.Parse) → родные элементы WPF.</summary>
public static class MarkdownView
{
    public static void OpenLink(string? url)
    {
        if (string.IsNullOrEmpty(url) || !(url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
            return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose(); }
        catch (Exception) { }
    }

    public static FrameworkElement Render(string markdown, double fontSize = 14)
    {
        var panel = new StackPanel();
        var blocks = MarkdownLite.Parse(markdown);
        for (var i = 0; i < blocks.Count; i++)
        {
            var el = RenderBlock(blocks[i], fontSize);
            if (el is FrameworkElement fe && i == blocks.Count - 1)
                fe.Margin = new Thickness(fe.Margin.Left, fe.Margin.Top, fe.Margin.Right, 0);
            if (i == 0 && el is FrameworkElement first)
                first.Margin = new Thickness(first.Margin.Left, 0, first.Margin.Right, first.Margin.Bottom);
            panel.Children.Add(el);
        }
        return panel;
    }

    private static TextBlock Para(IEnumerable<MdInline> inlines, double size, Thickness margin)
    {
        var tb = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = size, Margin = margin, LineHeight = size * 1.5 };
        AddInlines(tb.Inlines, inlines, size);
        return tb;
    }

    public static void AddInlines(InlineCollection target, IEnumerable<MdInline> inlines, double size)
    {
        foreach (var x in inlines)
        {
            switch (x.Kind)
            {
                case MdInlineKind.Bold:
                    target.Add(new Run(x.Text) { FontWeight = FontWeights.Bold });
                    break;
                case MdInlineKind.Italic:
                    target.Add(new Run(x.Text) { FontStyle = FontStyles.Italic });
                    break;
                case MdInlineKind.Strike:
                    target.Add(new Run(x.Text) { TextDecorations = TextDecorations.Strikethrough });
                    break;
                case MdInlineKind.Code:
                {
                    var r = new Run(" " + x.Text + " ") { FontFamily = Ui.Mono, FontSize = size - 1.5 };
                    r.SetResourceReference(TextElement.BackgroundProperty, "Bg3");
                    target.Add(r);
                    break;
                }
                case MdInlineKind.Link:
                {
                    var h = new Hyperlink(new Run(x.Text)) { ToolTip = x.Url };
                    var url = x.Url;
                    h.Click += (_, _) => OpenLink(url);
                    target.Add(h);
                    break;
                }
                default:
                    target.Add(new Run(x.Text));
                    break;
            }
        }
    }

    private static UIElement RenderBlock(MdBlock b, double size)
    {
        switch (b.Kind)
        {
            case MdBlockKind.Heading:
            {
                var t = Para(b.Inlines, size + 0.5, new Thickness(0, 10, 0, 6));
                t.FontWeight = FontWeights.Bold;
                return t;
            }
            case MdBlockKind.Rule:
            {
                var r = new Border { Height = 1, Margin = new Thickness(0, 10, 0, 10) };
                r.SetResourceReference(Border.BackgroundProperty, "Line");
                return r;
            }
            case MdBlockKind.Quote:
            {
                var t = Para(b.Inlines, size, new Thickness(0));
                t.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
                var q = new Border { BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(10, 2, 4, 2), Margin = new Thickness(0, 6, 0, 6), Child = t };
                q.SetResourceReference(Border.BorderBrushProperty, "Accent");
                return q;
            }
            case MdBlockKind.Bullet:
            case MdBlockKind.Numbered:
            {
                var g = new Grid { Margin = new Thickness(4, 4, 0, 8) };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                for (var i = 0; i < b.Items.Count; i++)
                {
                    g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                    var mark = new TextBlock
                    {
                        Text = b.Kind == MdBlockKind.Bullet ? "•" : $"{b.Start + i}.",
                        FontSize = size,
                        Margin = new Thickness(0, 2, 8, 2),
                        HorizontalAlignment = HorizontalAlignment.Right,
                    };
                    mark.SetResourceReference(TextBlock.ForegroundProperty, b.Kind == MdBlockKind.Bullet ? "Accent" : "Muted");
                    Grid.SetRow(mark, i);
                    g.Children.Add(mark);
                    var t = Para(b.Items[i], size, new Thickness(0, 2, 0, 2));
                    Grid.SetRow(t, i);
                    Grid.SetColumn(t, 1);
                    g.Children.Add(t);
                }
                return g;
            }
            case MdBlockKind.Code:
                return CodeBlock(b.Code ?? "", b.Language);
            default:
                return Para(b.Inlines, size, new Thickness(0, 0, 0, 8));
        }
    }

    /// <summary>Колесо мыши над вложенным полем прокручивает чат, а не застревает в поле.</summary>
    public static void ForwardWheel(FrameworkElement el)
    {
        el.PreviewMouseWheel += (s, e) =>
        {
            if (e.Handled || el.Parent is not UIElement parent) return;
            e.Handled = true;
            parent.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
            {
                RoutedEvent = UIElement.MouseWheelEvent,
                Source = el,
            });
        };
    }

    private static UIElement CodeBlock(string code, string? lang)
    {
        var grid = new Grid { Margin = new Thickness(0, 6, 0, 8) };
        var border = new Border { CornerRadius = new CornerRadius(9), BorderThickness = new Thickness(1) };
        border.SetResourceReference(Border.BackgroundProperty, "CodeBg");
        border.SetResourceReference(Border.BorderBrushProperty, "Line");
        var box = new TextBox
        {
            Text = code,
            IsReadOnly = true,
            FontFamily = Ui.Mono,
            FontSize = 12.5,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            TextWrapping = TextWrapping.NoWrap,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(10, string.IsNullOrEmpty(lang) ? 8 : 20, 10, 8),
            VerticalContentAlignment = VerticalAlignment.Top,
        };
        box.SetResourceReference(Control.ForegroundProperty, "CodeFg");
        ForwardWheel(box);
        border.Child = box;
        grid.Children.Add(border);

        if (!string.IsNullOrEmpty(lang))
        {
            var l = new TextBlock
            {
                Text = lang.ToUpperInvariant(),
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromRgb(0x8d, 0x86, 0xa0)),
                Margin = new Thickness(11, 6, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                IsHitTestVisible = false,
            };
            grid.Children.Add(l);
        }

        var label = new TextBlock { Text = "Копировать", FontSize = 11 };
        var copy = new Button
        {
            Content = label,
            Style = Ui.Style("Btn.Base"),
            Padding = new Thickness(8, 3, 8, 3),
            Margin = new Thickness(0, 5, 5, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Foreground = new SolidColorBrush(Color.FromRgb(0xd8, 0xd2, 0xe6)),
            Background = new SolidColorBrush(Color.FromArgb(0x16, 0xff, 0xff, 0xff)),
            FontWeight = FontWeights.Normal,
        };
        Ui.SetRadius(copy, new CornerRadius(6));
        copy.Click += (_, _) =>
        {
            try
            {
                Clipboard.SetText(code);
                label.Text = "Скопировано";
                var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
                t.Tick += (_, _) => { t.Stop(); label.Text = "Копировать"; };
                t.Start();
            }
            catch (Exception) { }
        };
        grid.Children.Add(copy);
        return grid;
    }
}
