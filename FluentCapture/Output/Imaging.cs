using System.Globalization;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using FluentSnip.Annotations;
using FluentSnip.Capture;
using FluentSnip.Core;

namespace FluentSnip;

public static class Imaging
{
    /// <summary>Crops the source, draws annotations (+ optional cursor) and returns a Pbgra32 bitmap.</summary>
    public static BitmapSource Compose(IPixelSource src, Int32Rect crop, IEnumerable<Annotation> items, Rect canvas,
                                       BitmapSource cursorPatch = null, Point cursorPos = default)
    {
        var dv = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(dv, BitmapScalingMode.NearestNeighbor);
        using (var dc = dv.RenderOpen())
        {
            dc.PushClip(new RectangleGeometry(new Rect(0, 0, crop.Width, crop.Height)));
            dc.PushTransform(new TranslateTransform(-crop.X, -crop.Y));
            dc.DrawImage(src.Image, new Rect(0, 0, src.PixelWidth, src.PixelHeight));
            if (cursorPatch != null) dc.DrawImage(cursorPatch, new Rect(cursorPos.X, cursorPos.Y, cursorPatch.PixelWidth, cursorPatch.PixelHeight));
            if (items != null) AnnotationLayer.RenderItems(dc, items, src, canvas);
            dc.Pop(); dc.Pop();
        }
        var rtb = new RenderTargetBitmap(crop.Width, crop.Height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv); rtb.Freeze();
        return rtb;
    }

    /// <summary>Applies watermark, rounded corners, border and shadow according to settings. pad = extra pixels added on each side.</summary>
    public static BitmapSource ApplyOutputStyle(BitmapSource img, out int pad)
    {
        var s = SettingsService.Current; pad = 0;
        if (s.WatermarkEnabled && !string.IsNullOrWhiteSpace(s.WatermarkText)) img = Watermark(img, s.WatermarkText, s.WatermarkOpacity / 100.0);
        if (!s.RoundedCorners && !s.OutputBorder && !s.OutputShadow) return img;

        pad = s.OutputShadow ? 18 : 0;
        int w = img.PixelWidth + pad * 2, h = img.PixelHeight + pad * 2;
        var inner = new Rect(pad, pad, img.PixelWidth, img.PixelHeight);
        double rad = s.RoundedCorners ? s.CornerRadius : 0;

        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.PushClip(new RectangleGeometry(inner, rad, rad));
            dc.DrawImage(img, inner);
            dc.Pop();
            if (s.OutputBorder)
                dc.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(ThemeManager.Accent), 2), Rect.Inflate(inner, -1, -1), rad, rad);
        }
        if (s.OutputShadow)
            dv.Effect = new DropShadowEffect { BlurRadius = 16, ShadowDepth = 3, Direction = 270, Opacity = 0.5, Color = Colors.Black };
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv); rtb.Freeze();
        return rtb;
    }

    static BitmapSource Watermark(BitmapSource img, string text, double opacity)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            var full = new Rect(0, 0, img.PixelWidth, img.PixelHeight);
            dc.DrawImage(img, full);
            dc.PushClip(new RectangleGeometry(full));
            double size = Math.Clamp(Math.Min(img.PixelWidth, img.PixelHeight) / 9.0, 14, 40);
            var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size,
                new SolidColorBrush(Color.FromArgb((byte)(255 * Math.Clamp(opacity, 0.02, 1)), 128, 128, 128)), 1.0);
            dc.PushTransform(new RotateTransform(-25, full.Width / 2, full.Height / 2));
            double stepX = ft.Width + 80, stepY = ft.Height + 70, ext = Math.Max(full.Width, full.Height);
            for (double y = -ext; y < ext * 2; y += stepY)
                for (double x = -ext; x < ext * 2; x += stepX) dc.DrawText(ft, new Point(x, y));
            dc.Pop(); dc.Pop();
        }
        var rtb = new RenderTargetBitmap(img.PixelWidth, img.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv); rtb.Freeze(); return rtb;
    }

    public static BitmapSource Flatten(BitmapSource img, Color bg)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(bg), null, new Rect(0, 0, img.PixelWidth, img.PixelHeight));
            dc.DrawImage(img, new Rect(0, 0, img.PixelWidth, img.PixelHeight));
        }
        var rtb = new RenderTargetBitmap(img.PixelWidth, img.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv); rtb.Freeze(); return rtb;
    }

    public static BitmapSource Crop(BitmapSource img, Int32Rect r)
    {
        var c = new CroppedBitmap(img, r); c.Freeze(); return c;
    }

    public static byte[] EncodePng(BitmapSource img)
    {
        var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(img));
        using var ms = new MemoryStream(); enc.Save(ms); return ms.ToArray();
    }

    /// <summary>Copies PNG (keeps alpha) and a flattened bitmap for apps that ignore PNG.</summary>
    public static void CopyToClipboard(BitmapSource img)
    {
        var data = new DataObject();
        data.SetData("PNG", new MemoryStream(EncodePng(img)));
        data.SetImage(Flatten(img, Colors.White));
        for (int i = 0; i < 8; i++)
        {
            try { Clipboard.SetDataObject(data, true); return; }
            catch (System.Runtime.InteropServices.COMException) { Thread.Sleep(60); }
        }
    }

    public static BitmapSource FromClipboardImage()
    {
        try
        {
            if (Clipboard.ContainsData("PNG") && Clipboard.GetData("PNG") is MemoryStream ms)
            {
                var d = new PngBitmapDecoder(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                var f = d.Frames[0]; f.Freeze(); return f;
            }
            if (Clipboard.ContainsImage()) { var i = Clipboard.GetImage(); i?.Freeze(); return i; }
        }
        catch { }
        return null;
    }

    public static BitmapSource LoadFile(string path)
    {
        var bi = new BitmapImage();
        bi.BeginInit(); bi.CacheOption = BitmapCacheOption.OnLoad; bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        bi.UriSource = new Uri(path); bi.EndInit(); bi.Freeze();
        return bi;
    }
}
