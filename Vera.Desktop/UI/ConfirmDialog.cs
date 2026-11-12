using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Vera.Desktop.UI;

/// <summary>Окно подтверждения в стиле приложения (вместо системного MessageBox).</summary>
public sealed class ConfirmDialog : Window
{
    private ConfirmDialog(string title, string message, string? detail, string okText, string cancelText, bool danger)
    {
        Title = "Вера";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        Topmost = true;
        ShowInTaskbar = true;
        FontFamily = Ui.Font;
        FontSize = 14;
        UseLayoutRounding = true;
        this.Res(BackgroundProperty, "Bg2");

        var body = new StackPanel { Margin = new Thickness(20, 18, 20, 18), MaxWidth = 460, MinWidth = 320 };
        body.Children.Add(Ui.Text(title, 15, "Fg", FontWeights.Bold));
        if (!string.IsNullOrWhiteSpace(message))
        {
            var m = Ui.Text(message, 13.5);
            m.Margin = new Thickness(0, 8, 0, 0);
            body.Children.Add(m);
        }
        if (!string.IsNullOrWhiteSpace(detail))
        {
            var code = new TextBox
            {
                Text = detail,
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                FontFamily = Ui.Mono,
                FontSize = 12.5,
                MaxHeight = 220,
                VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
                Margin = new Thickness(0, 10, 0, 0),
                VerticalContentAlignment = VerticalAlignment.Top,
            };
            code.SetResourceReference(BackgroundProperty, "CodeBg");
            code.SetResourceReference(ForegroundProperty, "CodeFg");
            body.Children.Add(code);
        }

        var ok = Ui.Button(okText, danger ? "Btn.Ghost" : "Btn.Primary");
        if (danger) ok.SetResourceReference(ForegroundProperty, "Err");
        ok.IsDefault = !danger;
        ok.Click += (_, _) => { DialogResult = true; };
        var cancel = Ui.Button(cancelText, "Btn.Ghost");
        cancel.IsCancel = true;
        cancel.IsDefault = danger;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        cancel.Margin = new Thickness(0, 0, 8, 0);
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        body.Children.Add(buttons);

        var frame = new Border { BorderThickness = new Thickness(1), Child = body };
        frame.SetResourceReference(Border.BorderBrushProperty, "Accent");
        Content = frame;
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed) try { DragMove(); } catch (InvalidOperationException) { }
        };
        Loaded += (_, _) => (danger ? cancel : ok).Focus();
    }

    /// <summary>Показать модально. owner может быть null (тогда окно по центру экрана, поверх всех).</summary>
    public static bool Ask(Window? owner, string title, string message, string? detail = null,
        string okText = "Да", string cancelText = "Отмена", bool danger = false)
    {
        var d = new ConfirmDialog(title, message, detail, okText, cancelText, danger);
        if (owner is { IsVisible: true })
        {
            d.Owner = owner;
            d.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            d.ShowInTaskbar = false;
        }
        else
        {
            d.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        d.Activated += (_, _) => d.Topmost = true;
        return d.ShowDialog() == true;
    }
}
