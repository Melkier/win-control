using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;

namespace WinControl;

internal static class ScreenCapture
{
    public sealed record CaptureResult(Bitmap Bitmap, Rectangle PhysicalBounds, string ModeUsed);
    public sealed record EncodedImage(byte[] Bytes, string MimeType, int Width, int Height, double ScaleToImage);

    public static Rectangle? ParseRegion(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec)) return null;
        var parts = spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 4
            || !int.TryParse(parts[0], out int x)
            || !int.TryParse(parts[1], out int y)
            || !int.TryParse(parts[2], out int w)
            || !int.TryParse(parts[3], out int h)
            || w <= 0 || h <= 0)
        {
            throw new ArgumentException($"Region invalida: '{spec}'. Usa el formato x,y,width,height (ej: 100,100,800,600).");
        }
        return new Rectangle(x, y, w, h);
    }

    public static CaptureResult CaptureBitmap(string? target, int? monitorIdx, Rectangle? region, string captureMode = "auto")
    {
        if (region.HasValue)
        {
            var r = region.Value;
            var bmp = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
                g.CopyFromScreen(r.Left, r.Top, 0, 0, r.Size, CopyPixelOperation.SourceCopy);
            return new CaptureResult(bmp, r, "screen_region");
        }

        if (!string.IsNullOrWhiteSpace(target))
        {
            var win = Program.ResolveWindow(target)
                      ?? throw new InvalidOperationException($"No se encontro ventana para: {target}");
            var bounds = new Rectangle(win.X, win.Y, Math.Max(1, win.Width), Math.Max(1, win.Height));

            bool tryPrintWindow = !captureMode.Equals("screen", StringComparison.OrdinalIgnoreCase)
                                  && !Native.IsIconic(win.Hwnd);

            if (tryPrintWindow)
            {
                var pwBmp = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
                bool ok;
                using (var g = Graphics.FromImage(pwBmp))
                {
                    IntPtr hdc = g.GetHdc();
                    try
                    {
                        ok = Native.PrintWindow(win.Hwnd, hdc, Native.PW_RENDERFULLCONTENT);
                    }
                    finally
                    {
                        g.ReleaseHdc(hdc);
                    }
                }

                if (ok)
                    return new CaptureResult(pwBmp, bounds, "print_window");

                pwBmp.Dispose();
            }

            var fbBmp = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(fbBmp))
                g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
            return new CaptureResult(fbBmp, bounds, tryPrintWindow ? "screen_fallback" : "screen");
        }

        Rectangle screenBounds = Program.ResolveCaptureBounds(null, monitorIdx);
        var fullBmp = new Bitmap(screenBounds.Width, screenBounds.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(fullBmp))
            g.CopyFromScreen(screenBounds.Left, screenBounds.Top, 0, 0, screenBounds.Size, CopyPixelOperation.SourceCopy);
        return new CaptureResult(fullBmp, screenBounds, monitorIdx.HasValue ? "monitor" : "virtual_screen");
    }

    public static EncodedImage EncodeBitmap(Bitmap fullBmp, int maxWidth, string format = "png", int quality = 80)
    {
        Bitmap toEncode = fullBmp;
        Bitmap? resized = null;
        double scale = 1.0;

        if (maxWidth > 0 && fullBmp.Width > maxWidth)
        {
            scale = (double)maxWidth / fullBmp.Width;
            int newW = maxWidth;
            int newH = Math.Max(1, (int)Math.Round(fullBmp.Height * scale));
            resized = new Bitmap(newW, newH, PixelFormat.Format32bppArgb);
            using (var rg = Graphics.FromImage(resized))
            {
                rg.InterpolationMode = InterpolationMode.HighQualityBicubic;
                rg.DrawImage(fullBmp, 0, 0, newW, newH);
            }
            toEncode = resized;
        }

        try
        {
            using var ms = new MemoryStream();
            bool isJpeg = format.Equals("jpg", StringComparison.OrdinalIgnoreCase)
                          || format.Equals("jpeg", StringComparison.OrdinalIgnoreCase);

            if (isJpeg)
            {
                var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
                using var encParams = new EncoderParameters(1);
                encParams.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)Math.Clamp(quality, 10, 100));
                toEncode.Save(ms, codec, encParams);
                return new EncodedImage(ms.ToArray(), "image/jpeg", toEncode.Width, toEncode.Height, scale);
            }

            toEncode.Save(ms, ImageFormat.Png);
            return new EncodedImage(ms.ToArray(), "image/png", toEncode.Width, toEncode.Height, scale);
        }
        finally
        {
            resized?.Dispose();
        }
    }
}
