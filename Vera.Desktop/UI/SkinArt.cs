using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Vera.Core.Sprites;
using Vera.Desktop.Pet;

namespace Vera.Desktop.UI;

/// <summary>Картинки текущего скина для окна: поза по эмоции, круглая аватарка с лицом.</summary>
public static class SkinArt
{
    private static SpriteSheet? _sheet;
    private static readonly Dictionary<string, ImageSource?> Cache = new();

    /// <summary>Поза скина для эмоции (пиксельная Вера собирается из слоёв, остальные — готовые кадры).</summary>
    public static ImageSource? Pose(string? skinId, string? emotion, PetMode mode = PetMode.Idle)
    {
        var skin = Skins.Get(skinId);
        var emo = string.IsNullOrEmpty(emotion) ? "neutral" : emotion;
        if (skin.Kind == SkinKind.Layered)
        {
            _sheet ??= new SpriteSheet();
            return _sheet.Compose(VeraSprite.Compose(emo, mode, false, false, false, Posture.Stand));
        }
        var pose = Skins.Pose(skin.Id, emo, mode, Posture.Stand);
        var path = $"/{pose.Folder ?? skin.Folder}/{pose.Name}.png";
        if (!Cache.TryGetValue(path, out var img)) Cache[path] = img = SpriteSheet.LoadImage(path);
        return img;
    }

    public static bool IsPixel(string? skinId) => Skins.Get(skinId).PixelArt;

    /// <summary>Картинка позы во весь рост (для пустого чата и подсказок).</summary>
    public static Image Full(string? skinId, string? emotion, double height)
    {
        var img = new Image { Source = Pose(skinId, emotion), Height = height, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center };
        RenderOptions.SetBitmapScalingMode(img, IsPixel(skinId) ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
        return img;
    }

    /// <summary>Круглая аватарка: лицо из позы, обводка цветом акцента.</summary>
    public static Border Avatar(string? skinId, string? emotion, double size)
    {
        var b = new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size / 2),
            BorderThickness = new Thickness(Math.Max(1.5, size / 20)),
            VerticalAlignment = VerticalAlignment.Top,
        };
        b.SetResourceReference(Border.BorderBrushProperty, "Accent");
        SetAvatar(b, skinId, emotion);
        return b;
    }

    /// <summary>Сменить лицо на аватарке (новая эмоция или другой скин).</summary>
    public static void SetAvatar(Border avatar, string? skinId, string? emotion)
    {
        var src = Pose(skinId, emotion);
        if (src is null) return;
        double w = src.Width, h = src.Height;
        if (src is System.Windows.Media.Imaging.BitmapSource bs) { w = bs.PixelWidth; h = bs.PixelHeight; }
        // лицо — квадрат в верхней части позы, по центру
        var side = Math.Min(w, h * 0.62);
        var brush = new ImageBrush(src)
        {
            ViewboxUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(Math.Max(0, w * 0.48 - side / 2), h * 0.02, side, side),
            Stretch = Stretch.UniformToFill,
        };
        RenderOptions.SetBitmapScalingMode(brush, IsPixel(skinId) ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
        var inner = new Border { CornerRadius = new CornerRadius(avatar.Width / 2), Background = brush };
        inner.SetResourceReference(Border.BorderBrushProperty, "Bg3");
        var bg = new Border { CornerRadius = new CornerRadius(avatar.Width / 2), Child = inner };
        bg.SetResourceReference(Border.BackgroundProperty, "Bg3");
        avatar.Child = bg;
    }
}
