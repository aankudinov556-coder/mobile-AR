using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Vera.Core.Sprites;

namespace Vera.Desktop.Pet;

/// <summary>Слои пиксель-арта 64×72 из ресурсов и сборка кадра (наложение слоёв с альфой). Кадры кэшируются.</summary>
public sealed class SpriteSheet
{
    private const int W = VeraSprite.Width;
    private const int H = VeraSprite.Height;
    private const int Stride = W * 4;

    private readonly Dictionary<string, byte[]> _layers = new();
    private readonly Dictionary<string, BitmapSource> _frames = new();

    public SpriteSheet()
    {
        foreach (var name in VeraSprite.AllLayerNames()) Load(name);
    }

    private void Load(string name)
    {
        try
        {
            var res = Application.GetResourceStream(new Uri($"pack://application:,,,/assets/vera/{name}.png"));
            if (res is null) return;
            using var s = res.Stream;
            var dec = new PngBitmapDecoder(s, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            BitmapSource frame = dec.Frames[0];
            if (frame.Format != PixelFormats.Pbgra32) frame = new FormatConvertedBitmap(frame, PixelFormats.Pbgra32, null, 0);
            if (frame.PixelWidth != W || frame.PixelHeight != H) return;
            var px = new byte[Stride * H];
            frame.CopyPixels(px, Stride, 0);
            _layers[name] = px;
        }
        catch (Exception)
        {
            // нет слоя — кадр нарисуется без него
        }
    }

    /// <summary>Готовая картинка кадра (Pbgra32, 64×72, заморожена).</summary>
    public BitmapSource Compose(SpriteFrame frame)
    {
        if (_frames.TryGetValue(frame.Key, out var cached)) return cached;
        var dst = new byte[Stride * H];
        foreach (var layer in frame.Layers)
        {
            if (!_layers.TryGetValue(layer, out var src)) continue;
            for (var i = 0; i < dst.Length; i += 4)
            {
                var a = src[i + 3];
                if (a == 0) continue;
                if (a == 255)
                {
                    dst[i] = src[i]; dst[i + 1] = src[i + 1]; dst[i + 2] = src[i + 2]; dst[i + 3] = 255;
                    continue;
                }
                // premultiplied: out = src + dst * (1 - srcA)
                var k = 255 - a;
                dst[i] = (byte)(src[i] + dst[i] * k / 255);
                dst[i + 1] = (byte)(src[i + 1] + dst[i + 1] * k / 255);
                dst[i + 2] = (byte)(src[i + 2] + dst[i + 2] * k / 255);
                dst[i + 3] = (byte)(a + dst[i + 3] * k / 255);
            }
        }
        var bmp = BitmapSource.Create(W, H, 96, 96, PixelFormats.Pbgra32, null, dst, Stride);
        bmp.Freeze();
        if (_frames.Count > 400) _frames.Clear();
        _frames[frame.Key] = bmp;
        return bmp;
    }

    public static BitmapSource? LoadImage(string packPath)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri("pack://application:,,," + packPath);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
