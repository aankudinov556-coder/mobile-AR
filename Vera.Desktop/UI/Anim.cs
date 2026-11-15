using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace Vera.Desktop.UI;

/// <summary>
/// Анимации «Ореола»: появление реплик, «поп» значков, плавная смена поз, нимб, покачивание.
/// Всё короткое (150–350 мс) и с замедлением в конце — интерфейс живой, но не мельтешит.
/// </summary>
public static class Anim
{
    public static readonly IEasingFunction Out = new CubicEase { EasingMode = EasingMode.EaseOut };
    public static readonly IEasingFunction Back = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.45 };
    public static readonly IEasingFunction InOut = new SineEase { EasingMode = EasingMode.EaseInOut };

    /// <summary>Пользователь просил меньше движения (Windows: «Показывать анимацию»).</summary>
    public static bool Reduced => !SystemParameters.ClientAreaAnimation;

    private static TranslateTransform Translate(UIElement el)
    {
        if (el.RenderTransform is TranslateTransform t && !t.IsFrozen) return t;
        if (el.RenderTransform is TransformGroup g)
            foreach (var c in g.Children) if (c is TranslateTransform tt && !tt.IsFrozen) return tt;
        var nt = new TranslateTransform();
        el.RenderTransform = nt;
        return nt;
    }

    private static ScaleTransform Scale(UIElement el, Point origin)
    {
        if (el.RenderTransform is ScaleTransform s && !s.IsFrozen) return s;
        var ns = new ScaleTransform(1, 1);
        el.RenderTransformOrigin = origin;
        el.RenderTransform = ns;
        return ns;
    }

    /// <summary>Появление снизу: сдвиг на dy и прозрачность → на место.</summary>
    public static void Appear(UIElement el, double dy = 14, int ms = 260, int delayMs = 0)
    {
        if (Reduced) return;
        var t = Translate(el);
        var begin = TimeSpan.FromMilliseconds(delayMs);
        el.Opacity = 0;
        el.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(ms)) { BeginTime = begin, EasingFunction = Out });
        t.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(dy, 0, TimeSpan.FromMilliseconds(ms + 60)) { BeginTime = begin, EasingFunction = Out });
    }

    /// <summary>Сбоку (для смены разделов).</summary>
    public static void SlideIn(UIElement el, double dx = 18, int ms = 220)
    {
        if (Reduced) return;
        var t = Translate(el);
        el.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(ms)) { EasingFunction = Out });
        t.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(dx, 0, TimeSpan.FromMilliseconds(ms + 40)) { EasingFunction = Out });
    }

    /// <summary>«Поп»: вырастает с лёгким перелётом (значки «!!», бейджи, чипы).</summary>
    public static void Pop(UIElement el, int ms = 320, double from = 0.6)
    {
        if (Reduced) return;
        var s = Scale(el, new Point(0.5, 0.5));
        var a = new DoubleAnimation(from, 1, TimeSpan.FromMilliseconds(ms)) { EasingFunction = Back };
        s.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        s.BeginAnimation(ScaleTransform.ScaleYProperty, a);
        el.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(ms / 2)));
    }

    /// <summary>Значок над Верой: выпрыгивает, висит и тает.</summary>
    public static void Burst(UIElement el, int holdMs = 900)
    {
        var s = Scale(el, new Point(0.5, 1));
        var t = TimeSpan.FromMilliseconds(260 + holdMs + 400);
        var scale = new DoubleAnimationUsingKeyFrames { Duration = t };
        scale.KeyFrames.Add(new EasingDoubleKeyFrame(0.3, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        scale.KeyFrames.Add(new EasingDoubleKeyFrame(1.15, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(180)), Out));
        scale.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(260)), Out));
        var op = new DoubleAnimationUsingKeyFrames { Duration = t };
        op.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        op.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(120))));
        op.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(260 + holdMs))));
        op.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(t)));
        s.BeginAnimation(ScaleTransform.ScaleXProperty, scale);
        s.BeginAnimation(ScaleTransform.ScaleYProperty, scale);
        el.BeginAnimation(UIElement.OpacityProperty, op);
    }

    /// <summary>Плавно сменить картинку: новая проявляется поверх старой.</summary>
    public static void CrossFade(Image back, Image front, ImageSource? next, int ms = 220)
    {
        if (next is null || ReferenceEquals(front.Source, next)) return;
        if (Reduced)
        {
            front.Source = next;
            return;
        }
        back.Source = front.Source;
        back.Opacity = 1;
        front.Source = next;
        front.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(ms)) { EasingFunction = Out });
        back.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(ms)) { EasingFunction = Out });
    }

    /// <summary>Бесконечное мягкое покачивание по Y (нимб, «дыхание»).</summary>
    public static void Bob(TranslateTransform t, double amplitude, double seconds)
    {
        if (Reduced) return;
        t.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-amplitude, amplitude, TimeSpan.FromSeconds(seconds))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = InOut,
        });
    }

    /// <summary>Дыхание: лёгкое растяжение по вертикали от низа.</summary>
    public static void Breathe(UIElement el, double amount = 0.018, double seconds = 1.8)
    {
        if (Reduced) return;
        var s = Scale(el, new Point(0.5, 1));
        s.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, 1 + amount, TimeSpan.FromSeconds(seconds))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = InOut,
        });
    }

    /// <summary>Переехать по Y с замедлением (нимб над выбранным разделом).</summary>
    public static void MoveY(TranslateTransform t, double to, int ms = 380)
    {
        if (Reduced)
        {
            t.BeginAnimation(TranslateTransform.YProperty, null);
            t.Y = to;
            return;
        }
        t.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = Back });
    }

    /// <summary>Встряхнуть (злость, ошибка).</summary>
    public static void Shake(UIElement el)
    {
        if (Reduced) return;
        var t = Translate(el);
        var a = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(420) };
        double[] xs = { 0, -5, 5, -4, 4, -2, 0 };
        for (var i = 0; i < xs.Length; i++) a.KeyFrames.Add(new LinearDoubleKeyFrame(xs[i], KeyTime.FromPercent(i / (double)(xs.Length - 1))));
        t.BeginAnimation(TranslateTransform.XProperty, a);
    }

    /// <summary>Подпрыгнуть (радость).</summary>
    public static void Hop(UIElement el, double height = 10)
    {
        if (Reduced) return;
        var t = Translate(el);
        var a = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(460) };
        a.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(0)));
        a.KeyFrames.Add(new EasingDoubleKeyFrame(-height, KeyTime.FromPercent(0.35), new QuadraticEase { EasingMode = EasingMode.EaseOut }));
        a.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(0.7), new QuadraticEase { EasingMode = EasingMode.EaseIn }));
        a.KeyFrames.Add(new EasingDoubleKeyFrame(-height * 0.25, KeyTime.FromPercent(0.85)));
        a.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(1)));
        t.BeginAnimation(TranslateTransform.YProperty, a);
    }

    /// <summary>Жёсткая «стикерная» тень (без размытия).</summary>
    public static DropShadowEffect HardShadow(double depth = 3) =>
        new() { BlurRadius = 0, ShadowDepth = depth, Direction = 315, Color = Colors.Black, Opacity = 1 };
}
