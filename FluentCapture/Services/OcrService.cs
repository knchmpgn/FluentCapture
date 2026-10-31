using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FluentSnip.Core;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace FluentSnip.Services;

/// <summary>Offline OCR through the Windows.Media.Ocr engine (uses installed Windows language packs).</summary>
public static class OcrService
{
    public static IReadOnlyList<(string Tag, string Name)> Languages()
    {
        try { return OcrEngine.AvailableRecognizerLanguages.Select(l => (l.LanguageTag, l.DisplayName)).ToList(); }
        catch { return new List<(string, string)>(); }
    }

    static OcrEngine CreateEngine()
    {
        string tag = SettingsService.Current.OcrLanguage;
        OcrEngine e = null;
        if (!string.IsNullOrEmpty(tag)) e = OcrEngine.TryCreateFromLanguage(new Language(tag));
        e ??= OcrEngine.TryCreateFromUserProfileLanguages();
        if (e == null)
        {
            var first = OcrEngine.AvailableRecognizerLanguages.FirstOrDefault();
            if (first != null) e = OcrEngine.TryCreateFromLanguage(first);
        }
        return e;
    }

    public static async Task<string> RecognizeAsync(BitmapSource bmp)
    {
        var engine = CreateEngine() ?? throw new InvalidOperationException(
            "No OCR language is installed. Add one under Windows Settings > Time & language > Language & region.");

        BitmapSource src = bmp;
        double scale = 1;
        if (Math.Max(src.PixelWidth, src.PixelHeight) < 700) scale = 2;
        double max = OcrEngine.MaxImageDimension - 8;
        scale = Math.Min(scale, max / Math.Max(src.PixelWidth, src.PixelHeight));
        if (Math.Abs(scale - 1) > 0.01) { src = new TransformedBitmap(src, new ScaleTransform(scale, scale)); }
        if (src.Format != PixelFormats.Bgra32) src = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);

        int w = src.PixelWidth, h = src.PixelHeight;
        var data = new byte[w * h * 4];
        src.CopyPixels(data, w * 4, 0);

        using var sb = SoftwareBitmap.CreateCopyFromBuffer(data.AsBuffer(), BitmapPixelFormat.Bgra8, w, h, BitmapAlphaMode.Ignore);
        var result = await engine.RecognizeAsync(sb);

        string lang = engine.RecognizerLanguage.LanguageTag.ToLowerInvariant();
        bool cjk = lang.StartsWith("zh") || lang.StartsWith("ja") || lang.StartsWith("ko");
        var lines = result.Lines.Select(l => string.Join(cjk ? "" : " ", l.Words.Select(x => x.Text)));
        return string.Join(Environment.NewLine, lines).Trim();
    }
}
