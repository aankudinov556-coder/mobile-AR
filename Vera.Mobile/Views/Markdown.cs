using Vera.Core.Markdown;

namespace Vera.Mobile.Views;

/// <summary>MarkdownLite → родные элементы MAUI (Label со Span, блоки кода, списки, цитаты).</summary>
public static class Markdown
{
    public static View Render(string md, Color text, double size = 15)
    {
        var stack = new VerticalStackLayout { Spacing = 6 };
        foreach (var b in MarkdownLite.Parse(md))
        {
            var v = Block(b, text, size);
            if (v is not null) stack.Children.Add(v);
        }
        return stack;
    }

    private static View? Block(MdBlock b, Color text, double size)
    {
        switch (b.Kind)
        {
            case MdBlockKind.Paragraph:
                return Inline(b.Inlines, text, size);
            case MdBlockKind.Heading:
            {
                var l = Inline(b.Inlines, text, b.Level <= 1 ? size + 4 : b.Level == 2 ? size + 2 : size + 1);
                l.FontAttributes = FontAttributes.Bold;
                l.Margin = new Thickness(0, 4, 0, 0);
                return l;
            }
            case MdBlockKind.Code:
                return CodeBlock(b.Code ?? "", b.Language);
            case MdBlockKind.Bullet:
            case MdBlockKind.Numbered:
            {
                var list = new VerticalStackLayout { Spacing = 3 };
                var n = b.Start;
                foreach (var item in b.Items)
                {
                    var g = new Grid
                    {
                        ColumnDefinitions = { new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star) },
                        ColumnSpacing = 6,
                    };
                    var marker = new Label
                    {
                        Text = b.Kind == MdBlockKind.Bullet ? "•" : $"{n++}.",
                        TextColor = Ui.Accent,
                        FontSize = size,
                        FontAttributes = FontAttributes.Bold,
                        MinimumWidthRequest = 14,
                    };
                    g.Add(marker, 0);
                    g.Add(Inline(item, text, size), 1);
                    list.Children.Add(g);
                }
                return list;
            }
            case MdBlockKind.Quote:
            {
                var g = new Grid
                {
                    ColumnDefinitions = { new ColumnDefinition(new GridLength(3)), new ColumnDefinition(GridLength.Star) },
                    ColumnSpacing = 10,
                };
                g.Add(new BoxView { Color = Ui.Accent2, CornerRadius = 2 }, 0);
                var l = Inline(b.Inlines, Ui.Muted, size);
                l.FontAttributes = FontAttributes.Italic;
                g.Add(l, 1);
                return g;
            }
            case MdBlockKind.Rule:
                return new BoxView { Color = Ui.Stroke, HeightRequest = 1, Margin = new Thickness(0, 4) };
        }
        return null;
    }

    public static Label Inline(IReadOnlyList<MdInline> inlines, Color text, double size)
    {
        var fs = new FormattedString();
        foreach (var i in inlines)
        {
            var span = new Span { Text = i.Text, TextColor = text, FontSize = size };
            switch (i.Kind)
            {
                case MdInlineKind.Bold: span.FontAttributes = FontAttributes.Bold; break;
                case MdInlineKind.Italic: span.FontAttributes = FontAttributes.Italic; break;
                case MdInlineKind.Strike: span.TextDecorations = TextDecorations.Strikethrough; break;
                case MdInlineKind.Code:
                    span.FontFamily = Ui.Mono;
                    span.FontSize = size - 1.5;
                    span.TextColor = Color.FromArgb("#ffb3d9");
                    span.BackgroundColor = Color.FromArgb("#33000000");
                    span.Text = " " + i.Text + " ";
                    break;
                case MdInlineKind.Link:
                {
                    span.TextColor = Color.FromArgb("#f08fc4");
                    span.TextDecorations = TextDecorations.Underline;
                    var url = i.Url ?? i.Text;
                    var tap = new TapGestureRecognizer();
                    tap.Tapped += async (_, _) =>
                    {
                        try { await Launcher.Default.OpenAsync(new Uri(url)); }
                        catch { Ui.Toast("Не получилось открыть ссылку"); }
                    };
                    span.GestureRecognizers.Add(tap);
                    break;
                }
            }
            fs.Spans.Add(span);
        }
        return new Label { FormattedText = fs, LineBreakMode = LineBreakMode.WordWrap, LineHeight = 1.15 };
    }

    private static View CodeBlock(string code, string? language)
    {
        var copy = new Label
        {
            Text = "Копировать",
            FontSize = 12,
            TextColor = Ui.Muted,
            HorizontalOptions = LayoutOptions.End,
            Padding = new Thickness(8, 2),
        };
        var tap = new TapGestureRecognizer();
        tap.Tapped += async (_, _) =>
        {
            try
            {
                await Clipboard.Default.SetTextAsync(code);
                copy.Text = "Скопировано";
                await Task.Delay(1500);
                copy.Text = "Копировать";
            }
            catch { }
        };
        copy.GestureRecognizers.Add(tap);
        var header = new Grid
        {
            ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) },
        };
        header.Add(new Label { Text = string.IsNullOrEmpty(language) ? "код" : language, FontSize = 12, TextColor = Ui.Faint, VerticalOptions = LayoutOptions.Center }, 0);
        header.Add(copy, 1);
        var body = new ScrollView
        {
            Orientation = ScrollOrientation.Horizontal,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Never,
            Content = new Label
            {
                Text = code,
                FontFamily = Ui.Mono,
                FontSize = 13,
                TextColor = Color.FromArgb("#e6e1f2"),
                LineBreakMode = LineBreakMode.NoWrap,
            },
        };
        return new Border
        {
            BackgroundColor = Ui.CodeBg,
            Stroke = Ui.Stroke,
            StrokeThickness = 1,
            StrokeShape = Ui.Round(10),
            Padding = new Thickness(10, 6, 10, 10),
            Content = new VerticalStackLayout { Spacing = 4, Children = { header, body } },
        };
    }
}
