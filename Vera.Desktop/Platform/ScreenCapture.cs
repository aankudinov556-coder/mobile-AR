using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;

namespace Vera.Desktop.Platform;

/// <summary>Снимок экрана в JPEG (base64).</summary>
public sealed record ScreenShot(string Base64, int Width, int Height)
{
    public const string Mime = "image/jpeg";
}

public static class ScreenCapture
{
    public const int MaxEdge = 1600;

    /// <summary>Снимок монитора, на котором сейчас курсор (координаты физические — процесс PerMonitorV2).</summary>
    public static ScreenShot CaptureUnderCursor()
    {
        Native.GetCursorPos(out var pt);
        var r = Native.MonitorAt(pt.X, pt.Y);
        if (r.Width <= 0 || r.Height <= 0) throw new InvalidOperationException("Не удалось определить экран");
        using var bmp = new Bitmap(r.Width, r.Height, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(bmp))
            g.CopyFromScreen(r.Left, r.Top, 0, 0, new Size(r.Width, r.Height), CopyPixelOperation.SourceCopy);
        return Encode(bmp);
    }

    /// <summary>Уменьшить до MaxEdge по большей стороне и закодировать в JPEG (качество 82).</summary>
    public static ScreenShot Encode(Image img, int maxEdge = MaxEdge)
    {
        var w = img.Width;
        var h = img.Height;
        Image src = img;
        Bitmap? scaled = null;
        try
        {
            if (Math.Max(w, h) > maxEdge)
            {
                var k = (double)maxEdge / Math.Max(w, h);
                w = Math.Max(1, (int)Math.Round(w * k));
                h = Math.Max(1, (int)Math.Round(h * k));
                scaled = new Bitmap(w, h, PixelFormat.Format24bppRgb);
                using var g = Graphics.FromImage(scaled);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.Clear(Color.White);
                g.DrawImage(img, new Rectangle(0, 0, w, h));
                src = scaled;
            }
            else if (img.PixelFormat != PixelFormat.Format24bppRgb)
            {
                // прозрачность в JPEG не бывает — подложим белый фон
                scaled = new Bitmap(w, h, PixelFormat.Format24bppRgb);
                using var g = Graphics.FromImage(scaled);
                g.Clear(Color.White);
                g.DrawImage(img, new Rectangle(0, 0, w, h));
                src = scaled;
            }

            var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
            using var prms = new EncoderParameters(1);
            prms.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 82L);
            using var ms = new MemoryStream();
            src.Save(ms, codec, prms);
            return new ScreenShot(Convert.ToBase64String(ms.ToArray()), w, h);
        }
        finally
        {
            scaled?.Dispose();
        }
    }
}
