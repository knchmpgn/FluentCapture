using System.Windows.Media;
using System.Windows.Media.Imaging;
using ZXing;
using ZXing.Common;

namespace FluentSnip.Services;

public sealed record QrHit(string Text, Rect Bounds);

public static class QrService
{
    public static List<QrHit> Scan(BitmapSource bmp)
    {
        var hits = new List<QrHit>();
        try
        {
            foreach (double scale in new[] { 1.0, 2.0, 0.5 })
            {
                BitmapSource src = bmp;
                if (scale != 1.0)
                {
                    if (scale > 1 && Math.Max(bmp.PixelWidth, bmp.PixelHeight) > 1400) continue;
                    if (scale < 1 && Math.Max(bmp.PixelWidth, bmp.PixelHeight) < 1200) continue;
                    src = new TransformedBitmap(bmp, new ScaleTransform(scale, scale));
                }
                if (src.Format != PixelFormats.Bgra32) src = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
                int w = src.PixelWidth, h = src.PixelHeight;
                var data = new byte[w * h * 4];
                src.CopyPixels(data, w * 4, 0);

                var reader = new BarcodeReaderGeneric
                {
                    AutoRotate = true, TryInverted = true,
                    Options = new DecodingOptions { TryHarder = true, PossibleFormats = new List<BarcodeFormat> { BarcodeFormat.QR_CODE } }
                };
                var lum = new RGBLuminanceSource(data, w, h, RGBLuminanceSource.BitmapFormat.BGRA32);
                Result[] results = null;
                try { results = reader.DecodeMultiple(lum); } catch { }
                if (results == null || results.Length == 0)
                {
                    var one = reader.Decode(lum);
                    results = one != null ? new[] { one } : null;
                }
                if (results == null) continue;
                foreach (var r in results)
                {
                    if (string.IsNullOrEmpty(r.Text) || hits.Any(x => x.Text == r.Text)) continue;
                    var pts = r.ResultPoints;
                    Rect b = Rect.Empty;
                    if (pts != null && pts.Length > 0)
                        b = new Rect(new Point(pts.Min(p => p.X) / scale, pts.Min(p => p.Y) / scale),
                                     new Point(pts.Max(p => p.X) / scale, pts.Max(p => p.Y) / scale));
                    hits.Add(new QrHit(r.Text, b));
                }
                if (hits.Count > 0) break;
            }
        }
        catch (Exception ex) { Core.SettingsService.Log("QR scan failed: " + ex.Message); }
        return hits;
    }
}
