using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FluentSnip.Capture;
using FluentSnip.Core;

namespace FluentSnip.Views;

public enum Handle { None, TL, T, TR, R, BR, B, BL, L, Move }

/// <summary>Draws dim layer, selection hole/border/handles, hover candidate, crosshair and QR boxes.</summary>
public sealed class SelectionVisual : FrameworkElement
{
    public double DimAlpha = 0.55, Ui = 1, Radius;
    public Rect? Hole; public bool IsSelection, ShowHandles;
    public Point? Cross; public List<Rect> Qr = new();

    public static Point[] HandlePoints(Rect r) => new[]
    {
        r.TopLeft, new Point(r.X + r.Width / 2, r.Top), r.TopRight, new Point(r.Right, r.Y + r.Height / 2),
        r.BottomRight, new Point(r.X + r.Width / 2, r.Bottom), r.BottomLeft, new Point(r.Left, r.Y + r.Height / 2)
    };

    protected override void OnRender(DrawingContext dc)
    {
        var full = new Rect(0, 0, ActualWidth, ActualHeight);
        Geometry g = new RectangleGeometry(full);
        if (Hole is Rect h && h.Width > 0 && h.Height > 0)
            g = new CombinedGeometry(GeometryCombineMode.Exclude, g, new RectangleGeometry(h, Radius, Radius));
        dc.DrawGeometry(new SolidColorBrush(Color.FromArgb((byte)(255 * DimAlpha), 0, 0, 0)), null, g);

        var accent = new SolidColorBrush(ThemeManager.Accent);
        if (Cross is Point c && !IsSelection)
        {
            var pen = new Pen(new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)), 1) { DashStyle = DashStyles.Dash };
            dc.DrawLine(pen, new Point(0, c.Y + .5), new Point(ActualWidth, c.Y + .5));
            dc.DrawLine(pen, new Point(c.X + .5, 0), new Point(c.X + .5, ActualHeight));
        }
        if (Hole is Rect r && r.Width > 0)
        {
            dc.DrawRoundedRectangle(null, new Pen(accent, IsSelection ? 1.5 : 2.5 * Ui), r, Radius, Radius);
            if (IsSelection && ShowHandles)
            {
                double s = 8 * Ui;
                foreach (var p in HandlePoints(r))
                    dc.DrawRoundedRectangle(Brushes.White, new Pen(accent, 1.5), new Rect(p.X - s / 2, p.Y - s / 2, s, s), 2, 2);
            }
        }
        foreach (var q in Qr)
            dc.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(Color.FromRgb(0x16, 0xC6, 0x0C)), 2.5), Rect.Inflate(q, 6, 6), 6, 6);
    }
}

public enum ColorFormat { RGB, HEX, HSV, HSL }

public static class ColorText
{
    /// <summary>Labelled value for display, e.g. "R: 255 G: 128 B: 0".</summary>
    public static string Format(Color c, ColorFormat f)
    {
        switch (f)
        {
            case ColorFormat.HEX: return $"HEX: #{c.R:X2}{c.G:X2}{c.B:X2}";
            case ColorFormat.HSV: { Hsv(c, out double h, out double s, out double v); return $"H: {h:0}° S: {s:0}% V: {v:0}%"; }
            case ColorFormat.HSL: { Hsl(c, out double h, out double s, out double l); return $"H: {h:0}° S: {s:0}% L: {l:0}%"; }
            default: return $"R: {c.R} G: {c.G} B: {c.B}";
        }
    }

    /// <summary>Unlabelled value that is handy to paste, e.g. "255, 128, 0" or "#FF8000".</summary>
    public static string FormatPlain(Color c, ColorFormat f)
    {
        switch (f)
        {
            case ColorFormat.HEX: return $"#{c.R:X2}{c.G:X2}{c.B:X2}";
            case ColorFormat.HSV: { Hsv(c, out double h, out double s, out double v); return $"{h:0}, {s:0}%, {v:0}%"; }
            case ColorFormat.HSL: { Hsl(c, out double h, out double s, out double l); return $"{h:0}, {s:0}%, {l:0}%"; }
            default: return $"{c.R}, {c.G}, {c.B}";
        }
    }

    public static void Hsv(Color c, out double h, out double s, out double v)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0, max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        h = Hue(r, g, b, max, d); s = max == 0 ? 0 : d / max * 100; v = max * 100;
    }

    public static void Hsl(Color c, out double h, out double s, out double l)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0, max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        h = Hue(r, g, b, max, d); l = (max + min) / 2;
        s = d == 0 ? 0 : d / (1 - Math.Abs(2 * l - 1)) * 100; l *= 100;
    }

    static double Hue(double r, double g, double b, double max, double d)
    {
        if (d == 0) return 0;
        double h = max == r ? ((g - b) / d) % 6 : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
        h *= 60; return h < 0 ? h + 360 : h;
    }
}

/// <summary>Pixel magnifier (4:3 view) with coordinates, labelled color value and key hints, all centered.</summary>
public sealed class MagnifierPanel : Border
{
    const int GW = 24, GH = 18;            // 24x18 source pixels
readonly int Zoom;
readonly bool ShowGrid;
    readonly WriteableBitmap wb = new(GW, GH, 96, 96, PixelFormats.Bgra32, null);
    readonly TextBlock pos = new(), col = new();
readonly StackPanel hint = new();
    readonly Border swatch = new() { Width = 14, Height = 14, CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 0, 6, 0), BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Center };

    public MagnifierPanel()
    {
        var cfg = SettingsService.Current;
        Zoom = Math.Clamp(cfg.MagnifierZoom, 2, 16);
        ShowGrid = cfg.MagnifierGrid;
        Style = Core.Ui.Style("PanelBorder"); Padding = new Thickness(8); IsHitTestVisible = false;
        Width = GW * Zoom + 8 * 2 + 2;

        var img = new Image { Source = wb, Width = GW * Zoom, Height = GH * Zoom, Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.NearestNeighbor);
        var view = new Canvas { Width = GW * Zoom, Height = GH * Zoom };
        view.Children.Add(img);
        var marker = new System.Windows.Shapes.Rectangle { Width = Zoom, Height = Zoom, Stroke = Brushes.Red, StrokeThickness = 1.5 };
        Canvas.SetLeft(marker, (GW / 2) * Zoom); Canvas.SetTop(marker, (GH / 2) * Zoom);   // the sampled pixel sits at column 12, row 9
        view.Children.Add(marker);
        if (ShowGrid)
        {
            var gridBrush = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255));
            for (int i = 1; i < GW; i++) view.Children.Add(new System.Windows.Shapes.Line { X1 = i * Zoom, Y1 = 0, X2 = i * Zoom, Y2 = GH * Zoom, Stroke = gridBrush, StrokeThickness = 1 });
            for (int j = 1; j < GH; j++) view.Children.Add(new System.Windows.Shapes.Line { X1 = 0, Y1 = j * Zoom, X2 = GW * Zoom, Y2 = j * Zoom, Stroke = gridBrush, StrokeThickness = 1 });
        }
        var viewHost = new Border { CornerRadius = new CornerRadius(6), ClipToBounds = true, Child = view, HorizontalAlignment = HorizontalAlignment.Center };

        foreach (var t in new[] { pos, col })
        {
            t.FontSize = 12; t.TextAlignment = TextAlignment.Center; t.HorizontalAlignment = HorizontalAlignment.Center;
            t.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        }
        hint.HorizontalAlignment = HorizontalAlignment.Center;
        hint.Margin = new Thickness(0, 2, 0, 0);
        pos.Margin = new Thickness(0, 6, 0, 2);
        swatch.SetResourceReference(Border.BorderBrushProperty, "StrokeBrush");
        var colRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 4) };
        colRow.Children.Add(swatch); colRow.Children.Add(col);

        Border KeyCap(string key) => new Border
        {
            Background = (Brush)Application.Current.FindResource("CardBrush"),
            BorderBrush = (Brush)Application.Current.FindResource("StrokeBrush"),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4),
            Padding = new Thickness(4, 1, 4, 1), Margin = new Thickness(0, 0, 4, 0),
            Child = new TextBlock { Text = key, FontSize = 10, FontWeight = FontWeights.SemiBold }
        };
        TextBlock HintText(string text) { var t = new TextBlock { Text = text, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) }; t.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush"); return t; }

        var copyHint = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        copyHint.Children.Add(KeyCap("C")); copyHint.Children.Add(HintText("Copy"));
        var formatHint = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        formatHint.Children.Add(KeyCap("Shift")); formatHint.Children.Add(HintText("Format"));
        hint.Children.Add(copyHint);
        hint.Children.Add(formatHint);

        var sp = new StackPanel();
        sp.Children.Add(viewHost); sp.Children.Add(pos); sp.Children.Add(colRow); sp.Children.Add(hint);
        Child = sp;
    }

    public void Update(ScreenSnapshot s, int x, int y, ColorFormat fmt)
    {
        var buf = new byte[GW * GH * 4];
        int hx = GW / 2, hy = GH / 2;
        for (int j = 0; j < GH; j++)
            for (int i = 0; i < GW; i++)
            {
                int sx = x - hx + i, sy = y - hy + j, d = (j * GW + i) * 4;
                if (sx < 0 || sy < 0 || sx >= s.PixelWidth || sy >= s.PixelHeight) { buf[d + 3] = 255; continue; }
                int si = sy * s.Stride + sx * 4;
                buf[d] = s.Pixels[si]; buf[d + 1] = s.Pixels[si + 1]; buf[d + 2] = s.Pixels[si + 2]; buf[d + 3] = 255;
            }
        wb.WritePixels(new Int32Rect(0, 0, GW, GH), buf, GW * 4, 0);
        var c = s.PixelAt(x, y);
        pos.Text = $"{x + s.OriginX}, {y + s.OriginY}";
        col.Text = ColorText.Format(c, fmt);
        swatch.Background = new SolidColorBrush(c);
    }
}
