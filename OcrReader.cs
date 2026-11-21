using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace WinControl;

internal static class OcrReader
{
    private static readonly JsonSerializerOptions PrettyJson = new() { WriteIndented = true };

    public static string RunOcrJson(string? target, int? monitorIdx, string? regionSpec, string? langTag)
    {
        return Task.Run(() => RunOcrAsync(target, monitorIdx, regionSpec, langTag)).GetAwaiter().GetResult();
    }

    private static async Task<string> RunOcrAsync(string? target, int? monitorIdx, string? regionSpec, string? langTag)
    {
        var sw = Stopwatch.StartNew();
        var engine = ResolveEngine(langTag);
        var region = ScreenCapture.ParseRegion(regionSpec);
        var capture = ScreenCapture.CaptureBitmap(target, monitorIdx, region, "auto");

        using var sourceBmp = capture.Bitmap;
        System.Drawing.Bitmap bmpForOcr = sourceBmp;
        System.Drawing.Bitmap? scaledBmp = null;
        double scale = 1.0;

        int maxDim = (int)OcrEngine.MaxImageDimension;
        if (maxDim > 0 && (sourceBmp.Width > maxDim || sourceBmp.Height > maxDim))
        {
            scale = Math.Min((double)maxDim / sourceBmp.Width, (double)maxDim / sourceBmp.Height);
            int newW = Math.Max(1, (int)Math.Round(sourceBmp.Width * scale));
            int newH = Math.Max(1, (int)Math.Round(sourceBmp.Height * scale));
            scaledBmp = new System.Drawing.Bitmap(newW, newH, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = System.Drawing.Graphics.FromImage(scaledBmp))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(sourceBmp, 0, 0, newW, newH);
            }
            bmpForOcr = scaledBmp;
        }

        try
        {
            using var ms = new MemoryStream();
            bmpForOcr.Save(ms, System.Drawing.Imaging.ImageFormat.Bmp);
            ms.Position = 0;

            using var ras = ms.AsRandomAccessStream();
            var decoder = await BitmapDecoder.CreateAsync(ras);
            using var softBmp = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            var ocrResult = await engine.RecognizeAsync(softBmp);

            var lines = new List<object>(ocrResult.Lines.Count);
            foreach (var line in ocrResult.Lines)
            {
                var words = new List<object>(line.Words.Count);
                double minX = double.MaxValue, minY = double.MaxValue;
                double maxX = double.MinValue, maxY = double.MinValue;

                foreach (var w in line.Words)
                {
                    var r = w.BoundingRect;
                    int wx = capture.PhysicalBounds.Left + (int)Math.Round(r.X / scale);
                    int wy = capture.PhysicalBounds.Top + (int)Math.Round(r.Y / scale);
                    int ww = Math.Max(1, (int)Math.Round(r.Width / scale));
                    int wh = Math.Max(1, (int)Math.Round(r.Height / scale));

                    minX = Math.Min(minX, wx);
                    minY = Math.Min(minY, wy);
                    maxX = Math.Max(maxX, wx + ww);
                    maxY = Math.Max(maxY, wy + wh);

                    words.Add(new
                    {
                        w.Text,
                        Bounds = new { X = wx, Y = wy, Width = ww, Height = wh }
                    });
                }

                object? lineBounds = line.Words.Count > 0
                    ? new
                    {
                        X = (int)Math.Round(minX),
                        Y = (int)Math.Round(minY),
                        Width = Math.Max(1, (int)Math.Round(maxX - minX)),
                        Height = Math.Max(1, (int)Math.Round(maxY - minY))
                    }
                    : null;

                lines.Add(new
                {
                    line.Text,
                    Bounds = lineBounds,
                    Words = words
                });
            }

            sw.Stop();
            return JsonSerializer.Serialize(new
            {
                Language = engine.RecognizerLanguage.LanguageTag,
                CaptureMode = capture.ModeUsed,
                PhysicalBounds = new
                {
                    capture.PhysicalBounds.Left,
                    capture.PhysicalBounds.Top,
                    capture.PhysicalBounds.Width,
                    capture.PhysicalBounds.Height
                },
                ElapsedMs = sw.ElapsedMilliseconds,
                LineCount = lines.Count,
                Text = ocrResult.Text,
                Lines = lines
            }, PrettyJson);
        }
        finally
        {
            scaledBmp?.Dispose();
        }
    }

    private static OcrEngine ResolveEngine(string? langTag)
    {
        if (!string.IsNullOrWhiteSpace(langTag))
        {
            var requested = new Language(langTag);
            var specific = OcrEngine.TryCreateFromLanguage(requested);
            if (specific != null) return specific;

            string available = string.Join(", ", OcrEngine.AvailableRecognizerLanguages.Select(l => l.LanguageTag));
            throw new InvalidOperationException(
                $"Idioma OCR '{langTag}' no disponible en Windows. Idiomas instalados: {(string.IsNullOrEmpty(available) ? "ninguno" : available)}.");
        }

        var engine = OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine != null) return engine;

        var first = OcrEngine.AvailableRecognizerLanguages.FirstOrDefault();
        if (first != null)
        {
            engine = OcrEngine.TryCreateFromLanguage(first);
            if (engine != null) return engine;
        }

        throw new InvalidOperationException(
            "No hay paquetes de idioma OCR instalados en Windows. Instala un idioma en Configuracion > Hora e idioma > Idioma y region.");
    }
}
