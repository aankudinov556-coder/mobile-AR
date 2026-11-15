using Microsoft.Maui.Controls.Shapes;

namespace Vera.Mobile.Views;

/// <summary>Палитра и маленькие фабрики контролов (интерфейс собран кодом, без XAML).</summary>
public static class Ui
{
    // «Ореол»: чернила, золото нимба, бумага реплик
    public static readonly Color Bg = Color.FromArgb("#0e0d12");
    public static readonly Color Surface = Color.FromArgb("#17161d");
    public static readonly Color Surface2 = Color.FromArgb("#24222c");
    public static readonly Color Surface3 = Color.FromArgb("#2e2c37");
    public static readonly Color Stroke = Color.FromArgb("#3e3b48");
    public static readonly Color Text = Color.FromArgb("#f4f1e8");
    public static readonly Color Muted = Color.FromArgb("#a6a1b2");
    public static readonly Color Faint = Color.FromArgb("#8e899a");
    /// <summary>Вторичный светлый текст (описания, серые пилюли).</summary>
    public static readonly Color Soft = Color.FromArgb("#c9c4d4");
    public static readonly Color Accent = Color.FromArgb("#f5cf3a");
    public static readonly Color Accent2 = Color.FromArgb("#f5cf3a");
    public static readonly Color AccentSoft = Color.FromArgb("#2c2814");
    public static readonly Color AccentText = Color.FromArgb("#f5cf3a");
    /// <summary>Текст на золоте.</summary>
    public static readonly Color OnAccent = Color.FromArgb("#16141a");
    /// <summary>Бумага реплик Веры и поля ввода.</summary>
    public static readonly Color Paper = Color.FromArgb("#fffdf5");
    /// <summary>Чернила: обводки стикеров и текст на бумаге.</summary>
    public static readonly Color Ink = Color.FromArgb("#000000");
    public static readonly Color InkText = Color.FromArgb("#16141a");
    /// <summary>Ссылки на бумаге (тёмное золото — читается на белом).</summary>
    public static readonly Color LinkOnPaper = Color.FromArgb("#7a5800");
    public static readonly Color Danger = Color.FromArgb("#ff7a6b");
    public static readonly Color DangerSoft = Color.FromArgb("#3a1d22");
    public static readonly Color Warn = Color.FromArgb("#ffb347");
    public static readonly Color Ok = Color.FromArgb("#6fd69b");
    public static readonly Color CodeBg = Color.FromArgb("#0a090d");

    /// <summary>Шрифты (встроены): Onest — текст, Unbounded — заголовки.</summary>
    public const string Font = "Onest";
    public const string FontBold = "OnestBold";
    public const string Display = "UnboundedBlack";
    public const string DisplayBold = "UnboundedBold";

    /// <summary>Жёсткая «стикерная» тень без размытия.</summary>
    public static Shadow HardShadow(double depth = 3) => new()
    {
        Brush = new SolidColorBrush(Ink),
        Offset = new Point(depth, depth),
        Radius = 0,
        Opacity = 1f,
    };

    /// <summary>Крупный заголовок шрифтом Unbounded.</summary>
    public static Label Heading(string text, double size = 22) => new()
    {
        Text = text,
        FontSize = size,
        FontFamily = Display,
        TextColor = Text,
        LineBreakMode = LineBreakMode.TailTruncation,
    };

    public static Brush AccentGradient => new SolidColorBrush(Accent);

    public const string Mono = "monospace";

    public static RoundRectangle Round(double r) => new() { CornerRadius = new CornerRadius(r) };
    public static RoundRectangle Round(double tl, double tr, double bl, double br) => new() { CornerRadius = new CornerRadius(tl, tr, bl, br) };

    public static Label Label(string text, double size = 15, Color? color = null, bool bold = false) => new()
    {
        Text = text,
        FontSize = size,
        TextColor = color ?? Text,
        FontAttributes = bold ? FontAttributes.Bold : FontAttributes.None,
        LineBreakMode = LineBreakMode.WordWrap,
    };

    public static Label MutedLabel(string text, double size = 13) => Label(text, size, Muted);

    public static Label H3(string text) => new()
    {
        Text = text,
        FontSize = 16,
        FontFamily = DisplayBold,
        TextColor = Text,
        Margin = new Thickness(0, 0, 0, 2),
    };

    public static Border Card(params IView[] children)
    {
        var stack = new VerticalStackLayout { Spacing = 10 };
        foreach (var c in children) stack.Children.Add(c);
        return new Border
        {
            BackgroundColor = Surface,
            Stroke = Surface3,
            StrokeThickness = 2,
            StrokeShape = Round(20),
            Padding = new Thickness(16, 14),
            Content = stack,
        };
    }

    public static VerticalStackLayout Field(string title, View control, string? hint = null)
    {
        var s = new VerticalStackLayout { Spacing = 5 };
        s.Children.Add(Label(title, 13, Muted));
        s.Children.Add(control);
        if (!string.IsNullOrEmpty(hint)) s.Children.Add(Label(hint, 12, Faint));
        return s;
    }

    /// <summary>Поле ввода в скруглённой рамке.</summary>
    public static Border Box(View control, double minHeight = 44) => new()
    {
        BackgroundColor = Surface2,
        Stroke = Stroke,
        StrokeThickness = 2,
        StrokeShape = Round(14),
        Padding = new Thickness(12, 0),
        MinimumHeightRequest = minHeight,
        Content = control,
    };

    public static Entry Entry(string placeholder = "", bool password = false) => new()
    {
        Placeholder = placeholder,
        PlaceholderColor = Faint,
        TextColor = Text,
        FontSize = 15,
        IsPassword = password,
        BackgroundColor = Colors.Transparent,
        ClearButtonVisibility = ClearButtonVisibility.WhileEditing,
    };

    public static Editor Editor(string placeholder = "", double height = 90) => new()
    {
        Placeholder = placeholder,
        PlaceholderColor = Faint,
        TextColor = Text,
        FontSize = 15,
        BackgroundColor = Colors.Transparent,
        HeightRequest = height,
    };

    public static Picker Picker(IEnumerable<string> items, string title = "") => new()
    {
        ItemsSource = items.ToList(),
        Title = title,
        TitleColor = Muted,
        TextColor = Text,
        FontSize = 15,
        BackgroundColor = Colors.Transparent,
    };

    public static Button Primary(string text, EventHandler onClick)
    {
        var b = new Button
        {
            Text = text,
            TextColor = OnAccent,
            BackgroundColor = Accent,
            BorderColor = Ink,
            BorderWidth = 2.5,
            CornerRadius = 14,
            FontFamily = FontBold,
            FontSize = 15,
            Padding = new Thickness(16, 10),
            MinimumHeightRequest = 46,
            Shadow = HardShadow(3),
        };
        b.Clicked += onClick;
        return b;
    }

    public static Button Ghost(string text, EventHandler onClick, Color? color = null)
    {
        var b = new Button
        {
            Text = text,
            TextColor = color ?? Text,
            BackgroundColor = Surface2,
            BorderColor = Stroke,
            BorderWidth = 2,
            CornerRadius = 14,
            FontFamily = FontBold,
            FontSize = 14,
            Padding = new Thickness(14, 8),
            MinimumHeightRequest = 44,
        };
        b.Clicked += onClick;
        return b;
    }

    public static Button Chip(string text, EventHandler onClick)
    {
        var b = new Button
        {
            Text = text,
            TextColor = Text,
            BackgroundColor = Colors.Transparent,
            BorderColor = Stroke,
            BorderWidth = 2,
            CornerRadius = 18,
            FontFamily = FontBold,
            FontSize = 13,
            Padding = new Thickness(14, 4),
            MinimumHeightRequest = 36,
            HeightRequest = 38,
        };
        b.Clicked += onClick;
        return b;
    }

    public static ImageButton Icon(string icon, EventHandler onClick, double size = 40, double pad = 8)
    {
        var b = new ImageButton
        {
            Source = icon,
            WidthRequest = size,
            HeightRequest = size,
            Padding = new Thickness(pad),
            BackgroundColor = Colors.Transparent,
            CornerRadius = (int)(size / 2),
        };
        b.Clicked += onClick;
        return b;
    }

    /// <summary>Строка с переключателем: текст слева, Switch справа.</summary>
    public static Grid Toggle(string text, bool value, Action<bool> changed, string? hint = null)
    {
        var sw = new Switch { IsToggled = value, OnColor = Accent, ThumbColor = Paper, VerticalOptions = LayoutOptions.Center };
        sw.Toggled += (_, e) => changed(e.Value);
        var texts = new VerticalStackLayout { Spacing = 2, VerticalOptions = LayoutOptions.Center };
        texts.Children.Add(Label(text, 15));
        if (!string.IsNullOrEmpty(hint)) texts.Children.Add(Label(hint, 12, Faint));
        var g = new Grid
        {
            ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) },
            ColumnSpacing = 10,
        };
        g.Add(texts, 0);
        g.Add(sw, 1);
        // тап по тексту тоже переключает
        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) => sw.IsToggled = !sw.IsToggled;
        texts.GestureRecognizers.Add(tap);
        return g;
    }

    public static Border Note(string text, Color color) => new()
    {
        BackgroundColor = color.WithAlpha(0.12f),
        Stroke = color.WithAlpha(0.35f),
        StrokeThickness = 1,
        StrokeShape = Round(12),
        Padding = new Thickness(12, 10),
        Content = Label(text, 13, color.WithLuminosity(Math.Min(0.85f, color.GetLuminosity() + 0.15f))),
    };

    public static Border Tag(string text, Color? bg = null, Color? fg = null) => new()
    {
        BackgroundColor = bg ?? Surface3,
        StrokeThickness = 0,
        StrokeShape = Round(8),
        Padding = new Thickness(7, 2),
        VerticalOptions = LayoutOptions.Center,
        Content = new Label { Text = text, FontSize = 11.5, TextColor = fg ?? Muted },
    };

    /// <summary>Квадратная кнопка-иконка 44×44 с обводкой (шапки экранов).</summary>
    public static ImageButton SquareIcon(string icon, EventHandler onClick, string? description = null)
    {
        var b = new ImageButton
        {
            Source = icon,
            WidthRequest = 44,
            HeightRequest = 44,
            Padding = new Thickness(12),
            CornerRadius = 14,
            BackgroundColor = Colors.Transparent,
            BorderColor = Stroke,
            BorderWidth = 2,
            VerticalOptions = LayoutOptions.Center,
        };
        if (description is not null) SemanticProperties.SetDescription(b, description);
        b.Clicked += onClick;
        return b;
    }

    /// <summary>Шапка вложенного экрана: квадратная «назад» и заголовок Unbounded. Системную панель надо скрыть.</summary>
    public static Grid PageHeader(Page page, string title, View? right = null)
    {
        var back = SquareIcon("ic_back.png", async (_, _) =>
        {
            try { await page.Navigation.PopAsync(); } catch { }
        }, "Назад");
        var g = new Grid
        {
            Padding = new Thickness(16, 14, 16, 10),
            ColumnSpacing = 10,
            ColumnDefinitions = { new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) },
        };
        var h = Heading(title, 22);
        h.VerticalOptions = LayoutOptions.Center;
        g.Add(back, 0);
        g.Add(h, 1);
        if (right is not null) g.Add(right, 2);
        return g;
    }

    /// <summary>Подпись раздела: мелкие прописные буквы с разрядкой.</summary>
    public static Label Caps(string text, Color? color = null) => new()
    {
        Text = text.ToUpperInvariant(),
        FontSize = 12,
        FontFamily = FontBold,
        CharacterSpacing = 1,
        TextColor = color ?? Faint,
    };

    /// <summary>Картинка из MauiAsset (кадры скинов: "skins/chibi/wave.png").</summary>
    public static ImageSource Asset(string path) =>
        ImageSource.FromStream(() => FileSystem.OpenAppPackageFileAsync(path).GetAwaiter().GetResult());

    public static Image Art(string path, double height) => new()
    {
        Source = Asset(path),
        HeightRequest = height,
        Aspect = Aspect.AspectFit,
    };

    public static ContentPage StylePage(ContentPage page, string title)
    {
        page.Title = title;
        page.BackgroundColor = Bg;
        page.SafeAreaEdges = SafeAreaEdges.All;
        return page;
    }

    /// <summary>Короткое системное уведомление внизу экрана.</summary>
    public static void Toast(string text, bool longer = false)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            try
            {
                Android.Widget.Toast.MakeText(Android.App.Application.Context, text,
                    longer ? Android.Widget.ToastLength.Long : Android.Widget.ToastLength.Short)?.Show();
            }
            catch { /* нет UI — не страшно */ }
        });
    }
}
