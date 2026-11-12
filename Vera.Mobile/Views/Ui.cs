using Microsoft.Maui.Controls.Shapes;

namespace Vera.Mobile.Views;

/// <summary>Палитра и маленькие фабрики контролов (интерфейс собран кодом, без XAML).</summary>
public static class Ui
{
    public static readonly Color Bg = Color.FromArgb("#15131c");
    public static readonly Color Surface = Color.FromArgb("#1d1a27");
    public static readonly Color Surface2 = Color.FromArgb("#262134");
    public static readonly Color Surface3 = Color.FromArgb("#302a41");
    public static readonly Color Stroke = Color.FromArgb("#332d45");
    public static readonly Color Text = Color.FromArgb("#ece8f5");
    public static readonly Color Muted = Color.FromArgb("#9a93ad");
    public static readonly Color Faint = Color.FromArgb("#6f6884");
    public static readonly Color Accent = Color.FromArgb("#e0479a");
    public static readonly Color Accent2 = Color.FromArgb("#9b5cf0");
    public static readonly Color AccentSoft = Color.FromArgb("#3a2140");
    public static readonly Color Danger = Color.FromArgb("#ff5d73");
    public static readonly Color DangerSoft = Color.FromArgb("#3a1d27");
    public static readonly Color Warn = Color.FromArgb("#f5b544");
    public static readonly Color Ok = Color.FromArgb("#4fd18b");
    public static readonly Color CodeBg = Color.FromArgb("#100e16");

    public static Brush AccentGradient => new LinearGradientBrush(new GradientStopCollection
    {
        new GradientStop(Accent, 0f),
        new GradientStop(Accent2, 1f),
    }, new Point(0, 0), new Point(1, 1));

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
        FontSize = 17,
        FontAttributes = FontAttributes.Bold,
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
            Stroke = Stroke,
            StrokeThickness = 1,
            StrokeShape = Round(18),
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
        StrokeThickness = 1,
        StrokeShape = Round(12),
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
            TextColor = Colors.White,
            Background = AccentGradient,
            BackgroundColor = Accent,
            CornerRadius = 12,
            FontAttributes = FontAttributes.Bold,
            FontSize = 14,
            Padding = new Thickness(14, 8),
            MinimumHeightRequest = 42,
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
            BackgroundColor = Surface3,
            BorderColor = Stroke,
            BorderWidth = 1,
            CornerRadius = 12,
            FontSize = 14,
            Padding = new Thickness(12, 6),
            MinimumHeightRequest = 40,
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
            BackgroundColor = Surface3,
            CornerRadius = 16,
            FontSize = 13,
            Padding = new Thickness(12, 4),
            MinimumHeightRequest = 34,
            HeightRequest = 36,
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
        var sw = new Switch { IsToggled = value, OnColor = Accent, ThumbColor = Colors.White, VerticalOptions = LayoutOptions.Center };
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
