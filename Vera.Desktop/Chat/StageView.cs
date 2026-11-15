using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Vera.Core.Sprites;
using Vera.Desktop.UI;

namespace Vera.Desktop.Chat;

/// <summary>
/// «Сцена» над чатом: Вера крупно (дышит, меняет позу по эмоции, «говорит» сменой кадров),
/// значки «!!», «~», «+» над головой, имя, статус и плашки (модель, телефон, характер).
/// В узком режиме сворачивается в строку с круглой аватаркой.
/// </summary>
public sealed class StageView : Border
{
    private const double FullHeight = 156, SlimHeight = 64;

    private readonly Image _back = new() { Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Bottom };
    private readonly Image _front = new() { Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Bottom };
    private readonly Grid _sprite;
    private readonly Border _avatar;
    private readonly TextBlock _mark;
    private readonly TextBlock _status;
    private readonly WrapPanel _chips;
    private readonly DispatcherTimer _talk;
    private Func<string?> _skin;
    private string _emotion = "neutral";
    private bool _talking, _mouth, _slim;

    public StageView(Func<string?> skin, UIElement windowButtons)
    {
        _skin = skin;
        Height = FullHeight;
        ClipToBounds = true;
        BorderThickness = new Thickness(0, 0, 0, 2.5);
        BorderBrush = Brushes.Black;
        this.Res(BackgroundProperty, "Bg2");

        foreach (var img in new[] { _back, _front })
        {
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            img.Height = 140;
            img.Width = 170;
        }
        _sprite = new Grid { Width = 170, Height = 146, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(10, 0, 4, 0), Children = { _back, _front } };
        Anim.Breathe(_sprite);

        _mark = new TextBlock
        {
            FontFamily = Ui.Display,
            FontWeight = FontWeights.Black,
            FontSize = 30,
            Foreground = new SolidColorBrush(Color.FromRgb(0xF5, 0xCF, 0x3A)),
            Effect = Anim.HardShadow(2),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 6, -6, 0),
            Opacity = 0,
            IsHitTestVisible = false,
        };
        var spriteHost = new Grid { Children = { _sprite, _mark }, VerticalAlignment = VerticalAlignment.Bottom };

        _avatar = SkinArt.Avatar(skin(), "neutral", 44);
        _avatar.VerticalAlignment = VerticalAlignment.Center;
        _avatar.Margin = new Thickness(14, 0, 4, 0);
        _avatar.Visibility = Visibility.Collapsed;

        var name = Ui.Heading("Вера", 30);
        _status = Ui.Text("", 13.5, "Muted", FontWeights.SemiBold, wrap: false);
        _status.Margin = new Thickness(12, 0, 0, 4);
        _status.VerticalAlignment = VerticalAlignment.Bottom;
        var nameRow = new StackPanel { Orientation = Orientation.Horizontal, Children = { name, _status } };
        _chips = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(10, 0, 0, 22), Children = { nameRow, _chips } };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(_avatar, 0);
        Grid.SetColumn(spriteHost, 0);
        Grid.SetColumn(text, 1);
        Grid.SetColumnSpan(text, 2);
        var buttons = windowButtons as FrameworkElement;
        if (buttons is not null)
        {
            buttons.HorizontalAlignment = HorizontalAlignment.Right;
            buttons.VerticalAlignment = VerticalAlignment.Top;
            buttons.Margin = new Thickness(0, 8, 8, 0);
            Grid.SetColumn(buttons, 2);
            grid.Children.Add(buttons);
        }
        grid.Children.Add(_avatar);
        grid.Children.Add(spriteHost);
        grid.Children.Add(text);
        Child = grid;

        _talk = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(170) };
        _talk.Tick += (_, _) =>
        {
            _mouth = !_mouth;
            Render(false);
        };
        Render(true);
    }

    /// <summary>Строка статуса («печатает ответ…», «думает…»); accent — золотом.</summary>
    public void SetStatus(string text, bool accent)
    {
        _status.Text = text;
        _status.SetResourceReference(TextBlock.ForegroundProperty, accent ? "AccentText" : "Muted");
    }

    /// <summary>Плашки под именем: (текст, есть ли зелёная точка «онлайн»).</summary>
    public void SetChips(IEnumerable<(string Text, bool Online)> chips)
    {
        _chips.Children.Clear();
        foreach (var (t, online) in chips)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            if (online)
            {
                var dot = new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
                dot.SetResourceReference(Border.BackgroundProperty, "Ok");
                sp.Children.Add(dot);
            }
            sp.Children.Add(Ui.Text(t, 12, "Muted", FontWeights.SemiBold, wrap: false));
            var chip = new Border { Child = sp, BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(999), Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 8, 6), MaxWidth = 320 };
            chip.SetResourceReference(Border.BorderBrushProperty, "Line");
            _chips.Children.Add(chip);
        }
    }

    /// <summary>Узкий режим: строка с аватаркой вместо большой сцены.</summary>
    public bool Slim
    {
        get => _slim;
        set
        {
            if (_slim == value) return;
            _slim = value;
            var h = value ? SlimHeight : FullHeight;
            if (Anim.Reduced) Height = h;
            else BeginAnimation(HeightProperty, new DoubleAnimation(h, TimeSpan.FromMilliseconds(260)) { EasingFunction = Anim.Out });
            _sprite.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
            _avatar.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            _chips.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
            if (Child is Grid g && g.Children[^1] is StackPanel text) text.Margin = value ? new Thickness(10, 0, 0, 12) : new Thickness(10, 0, 0, 22);
        }
    }

    /// <summary>Сменить эмоцию: новая поза плавно проявляется, над головой — значок.</summary>
    public void SetEmotion(string emotion, bool burst = true)
    {
        if (string.IsNullOrEmpty(emotion) || emotion == _emotion) return;
        _emotion = emotion;
        Render(false);
        if (!burst) return;
        var mark = emotion switch
        {
            "surprised" => "!!",
            "angry" => "#",
            "happy" or "laugh" => "+",
            "smug" => "~",
            "shy" => "♥",
            "think" => "?",
            "sad" => "…",
            _ => "",
        };
        if (mark.Length > 0)
        {
            _mark.Text = mark;
            Anim.Burst(_mark);
        }
        if (emotion is "happy" or "laugh") Anim.Hop(_sprite, 8);
        if (emotion == "angry") Anim.Shake(_sprite);
    }

    /// <summary>Вера «говорит» (идёт текст ответа или озвучка): рот открывается и закрывается.</summary>
    public void SetTalking(bool talking)
    {
        if (_talking == talking) return;
        _talking = talking;
        _mouth = false;
        if (talking && !Anim.Reduced) _talk.Start();
        else _talk.Stop();
        Render(false);
    }

    public void OnSkinChanged()
    {
        Render(true);
        SkinArt.SetAvatar(_avatar, _skin(), _emotion);
    }

    private void Render(bool force)
    {
        var skin = _skin();
        var mode = _talking ? PetMode.Talking : PetMode.Idle;
        var src = SkinArt.Pose(skin, _emotion, mode, _talking && _mouth);
        var pixel = SkinArt.IsPixel(skin);
        RenderOptions.SetBitmapScalingMode(_front, pixel ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
        RenderOptions.SetBitmapScalingMode(_back, pixel ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
        if (force || _talking)
        {
            // кадры речи — без растворения
            _back.BeginAnimation(OpacityProperty, null);
            _front.BeginAnimation(OpacityProperty, null);
            _back.Opacity = 0;
            _front.Opacity = 1;
            _front.Source = src;
        }
        else Anim.CrossFade(_back, _front, src);
        if (_slim) SkinArt.SetAvatar(_avatar, skin, _emotion);
    }
}
