using Android.Graphics;
using Android.Media;

namespace Vera.Mobile.Platform;

/// <summary>Подготовка фото для модели: уменьшить до 1568 px по длинной стороне, развернуть по EXIF, JPEG 85.</summary>
public static class ImageTools
{
    public static byte[] PrepareJpeg(byte[] data, int maxSide = 1568)
    {
        var bounds = new BitmapFactory.Options { InJustDecodeBounds = true };
        BitmapFactory.DecodeByteArray(data, 0, data.Length, bounds);
        if (bounds.OutWidth <= 0 || bounds.OutHeight <= 0) throw new InvalidOperationException("Не получилось прочитать картинку");
        var sample = 1;
        while (Math.Max(bounds.OutWidth, bounds.OutHeight) / (sample * 2) >= maxSide) sample *= 2;
        var bmp = BitmapFactory.DecodeByteArray(data, 0, data.Length, new BitmapFactory.Options { InSampleSize = sample })
                  ?? throw new InvalidOperationException("Не получилось прочитать картинку");

        var rotate = 0;
        try
        {
            using var ms = new MemoryStream(data);
            var exif = new ExifInterface(ms);
            rotate = exif.GetAttributeInt(ExifInterface.TagOrientation, 1) switch { 6 => 90, 3 => 180, 8 => 270, _ => 0 };
        }
        catch { }

        var scale = Math.Min(1.0, (double)maxSide / Math.Max(bmp.Width, bmp.Height));
        if (rotate != 0 || scale < 0.999)
        {
            var m = new Matrix();
            if (scale < 0.999) m.PostScale((float)scale, (float)scale);
            if (rotate != 0) m.PostRotate(rotate);
            var t = Bitmap.CreateBitmap(bmp, 0, 0, bmp.Width, bmp.Height, m, true);
            if (!ReferenceEquals(t, bmp)) bmp.Recycle();
            bmp = t;
        }
        using var outStream = new MemoryStream();
        bmp.Compress(Bitmap.CompressFormat.Jpeg!, 85, outStream);
        bmp.Recycle();
        return outStream.ToArray();
    }
}
