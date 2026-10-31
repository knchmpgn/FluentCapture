using System.Globalization;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FluentSnip.Annotations;

public enum AnnotTool { None, Rectangle, Ellipse, Line, Arrow, Pencil, Highlighter, Serial, Text, Eraser }

/// <summary>32bpp BGRA/BGRX pixels in "layer coordinates" (annotation space == pixel space).</summary>
public interface IPixelSource
{
    int PixelWidth { get; }
    int PixelHeight { get; }
    byte[] Pixels { get; }
    int Stride { get; }
    BitmapSource Image { get; }
}

public sealed class ImagePixelSource : IPixelSource
{
    public int PixelWidth { get; }
    public int PixelHeight { get; }
    public byte[] Pixels { get; }
    public int Stride { get; }
    public BitmapSource Image { get; }

    public ImagePixelSource(BitmapSource img)
    {
        BitmapSource s = img;
        if (s.Format != PixelFormats.Bgra32 && s.Format != PixelFormats.Bgr32)
            s = new FormatConvertedBitmap(img, PixelFormats.Bgra32, null, 0);
        PixelWidth = s.PixelWidth; PixelHeight = s.PixelHeight; Stride = PixelWidth * 4;
        Pixels = new byte[Stride * PixelHeight];
        s.CopyPixels(Pixels, Stride, 0);
        Image = img;
    }
}

public abstract class Annotation
{
    public Color Color = Colors.Red;
    public double Size = 3;
    public abstract AnnotTool Kind { get; }
    public abstract Annotation Clone();
    public abstract void Draw(DrawingContext dc, IPixelSource src);
    public abstract Rect Bounds { get; }
    public abstract void Move(Vector v);
    public virtual bool HitTest(Point p) => Rect.Inflate(Bounds, 6, 6).Contains(p);
    public virtual IReadOnlyList<Point> Handles => Array.Empty<Point>();
    public virtual void SetHandle(int index, Point p) { }

    protected Pen MakePen(double? width = null)
    {
        var pen = new Pen(new SolidColorBrush(Color), width ?? Size)
        { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        return pen;
    }

    protected static double DistToSegment(Point p, Point a, Point b)
    {
        var ab = b - a; double l2 = ab.LengthSquared;
        if (l2 < 1e-6) return (p - a).Length;
        double t = Math.Clamp(((p.X - a.X) * ab.X + (p.Y - a.Y) * ab.Y) / l2, 0, 1);
        return (p - new Point(a.X + ab.X * t, a.Y + ab.Y * t)).Length;
    }
}

/// <summary>Rectangle / ellipse / spotlight / mosaic share this corner-based geometry.</summary>
public abstract class RectBased : Annotation
{
    public double L, T, R, B;
    public Rect Rect => new(Math.Min(L, R), Math.Min(T, B), Math.Abs(R - L), Math.Abs(B - T));
    public override Rect Bounds => Rect;
    public override void Move(Vector v) { L += v.X; R += v.X; T += v.Y; B += v.Y; }
    public override IReadOnlyList<Point> Handles { get { var r = Rect; return new[] { r.TopLeft, r.TopRight, r.BottomRight, r.BottomLeft }; } }

    public override void SetHandle(int i, Point p)
    {
        var r = Rect; L = r.Left; T = r.Top; R = r.Right; B = r.Bottom;
        switch (i)
        {
            case 0: L = p.X; T = p.Y; break;
            case 1: R = p.X; T = p.Y; break;
            case 2: R = p.X; B = p.Y; break;
            case 3: L = p.X; B = p.Y; break;
        }
    }

    protected void CopyTo(RectBased o) { o.Color = Color; o.Size = Size; o.L = L; o.T = T; o.R = R; o.B = B; }
}

public sealed class ShapeAnnotation : RectBased
{
    public bool IsEllipse, Fill, Rounded;
    public override AnnotTool Kind => IsEllipse ? AnnotTool.Ellipse : AnnotTool.Rectangle;
    public override Annotation Clone() { var a = new ShapeAnnotation { IsEllipse = IsEllipse, Fill = Fill, Rounded = Rounded }; CopyTo(a); return a; }

    public override void Draw(DrawingContext dc, IPixelSource src)
    {
        var r = Rect; if (r.Width < 1 && r.Height < 1) return;
        Brush fill = Fill ? new SolidColorBrush(Color) : null;
        Pen pen = Fill ? null : MakePen();
        if (IsEllipse) dc.DrawEllipse(fill, pen, new Point(r.X + r.Width / 2, r.Y + r.Height / 2), r.Width / 2, r.Height / 2);
        else if (Rounded) dc.DrawRoundedRectangle(fill, pen, r, 8, 8);
        else dc.DrawRectangle(fill, pen, r);
    }

    public override bool HitTest(Point p)
    {
        var r = Rect; double tol = Math.Max(6, Size / 2 + 4);
        if (Fill) return Rect.Inflate(r, tol, tol).Contains(p);
        var outer = Rect.Inflate(r, tol, tol);
        var inner = Rect.Inflate(r, -tol, -tol);
        return outer.Contains(p) && (inner.Width <= 0 || inner.Height <= 0 || !inner.Contains(p));
    }
}

public sealed class LineAnnotation : Annotation
{
    public Point P0, P1; public bool Arrow;
    public override AnnotTool Kind => Arrow ? AnnotTool.Arrow : AnnotTool.Line;
    public override Annotation Clone() => new LineAnnotation { Color = Color, Size = Size, P0 = P0, P1 = P1, Arrow = Arrow };
    public override Rect Bounds => new Rect(P0, P1);
    public override void Move(Vector v) { P0 += v; P1 += v; }
    public override IReadOnlyList<Point> Handles => new[] { P0, P1 };
    public override void SetHandle(int i, Point p) { if (i == 0) P0 = p; else P1 = p; }
    public override bool HitTest(Point p) => DistToSegment(p, P0, P1) <= Math.Max(7, Size / 2 + 5);

    public override void Draw(DrawingContext dc, IPixelSource src)
    {
        var d = P1 - P0; if (d.Length < 1) return;
        var pen = MakePen();
        if (!Arrow) { dc.DrawLine(pen, P0, P1); return; }
        d.Normalize();
        double head = Math.Max(14, Size * 4.5), half = head * 0.45;
        var baseP = P1 - d * head * 0.85;
        var n = new Vector(-d.Y, d.X);
        dc.DrawLine(pen, P0, baseP);
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(P1, true, true);
            c.LineTo(baseP + n * half, true, false);
            c.LineTo(baseP - n * half, true, false);
        }
        g.Freeze();
        dc.DrawGeometry(new SolidColorBrush(Color), new Pen(new SolidColorBrush(Color), 1) { LineJoin = PenLineJoin.Round }, g);
    }
}

public sealed class StrokeAnnotation : Annotation
{
    public List<Point> Points = new(); public bool Highlighter;
    public override AnnotTool Kind => Highlighter ? AnnotTool.Highlighter : AnnotTool.Pencil;
    public override Annotation Clone() => new StrokeAnnotation { Color = Color, Size = Size, Highlighter = Highlighter, Points = new List<Point>(Points) };
    public override void Move(Vector v) { for (int i = 0; i < Points.Count; i++) Points[i] += v; }
    public override Rect Bounds
    {
        get
        {
            if (Points.Count == 0) return Rect.Empty;
            double x0 = Points.Min(p => p.X), y0 = Points.Min(p => p.Y), x1 = Points.Max(p => p.X), y1 = Points.Max(p => p.Y);
            return new Rect(x0, y0, x1 - x0, y1 - y0);
        }
    }

    public override bool HitTest(Point p)
    {
        double tol = Math.Max(7, Width / 2 + 5);
        if (Points.Count == 1) return (p - Points[0]).Length <= tol;
        for (int i = 1; i < Points.Count; i++) if (DistToSegment(p, Points[i - 1], Points[i]) <= tol) return true;
        return false;
    }

    double Width => Highlighter ? Size * 3 + 8 : Size;

    public override void Draw(DrawingContext dc, IPixelSource src)
    {
        if (Points.Count == 0) return;
        var col = Highlighter ? Color.FromArgb((byte)(Color.A * 0.40), Color.R, Color.G, Color.B) : Color;
        var pen = new Pen(new SolidColorBrush(col), Width)
        { StartLineCap = Highlighter ? PenLineCap.Flat : PenLineCap.Round, EndLineCap = Highlighter ? PenLineCap.Flat : PenLineCap.Round, LineJoin = PenLineJoin.Round };
        if (Points.Count == 1) { dc.DrawEllipse(new SolidColorBrush(col), null, Points[0], Width / 2, Width / 2); return; }
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(Points[0], false, false);
            for (int i = 1; i < Points.Count - 1; i++)
            {
                var mid = new Point((Points[i].X + Points[i + 1].X) / 2, (Points[i].Y + Points[i + 1].Y) / 2);
                c.QuadraticBezierTo(Points[i], mid, true, true);
            }
            c.LineTo(Points[^1], true, true);
        }
        g.Freeze();
        dc.DrawGeometry(null, pen, g);
    }
}

public sealed class SerialAnnotation : Annotation
{
    public Point Center; public int Number = 1;
    public double Radius => 11 + Size * 1.2;
    public override AnnotTool Kind => AnnotTool.Serial;
    public override Annotation Clone() => new SerialAnnotation { Color = Color, Size = Size, Center = Center, Number = Number };
    public override Rect Bounds => new(Center.X - Radius, Center.Y - Radius, Radius * 2, Radius * 2);
    public override void Move(Vector v) => Center += v;
    public override bool HitTest(Point p) => (p - Center).Length <= Radius + 3;

    public override void Draw(DrawingContext dc, IPixelSource src)
    {
        dc.DrawEllipse(new SolidColorBrush(Color), new Pen(Brushes.White, 1.5), Center, Radius, Radius);
        double lum = 0.299 * Color.R + 0.587 * Color.G + 0.114 * Color.B;
        var ft = new FormattedText(Number.ToString(), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
            Radius * 1.15, lum > 150 ? Brushes.Black : Brushes.White, 1.0);
        dc.DrawText(ft, new Point(Center.X - ft.Width / 2, Center.Y - ft.Height / 2));
    }
}

public sealed class TextAnnotation : Annotation
{
    public Point Pos; public string Text = ""; public double FontSize = 20; public bool Bold, Background;
    public override AnnotTool Kind => AnnotTool.Text;
    public override Annotation Clone() => new TextAnnotation { Color = Color, Size = Size, Pos = Pos, Text = Text, FontSize = FontSize, Bold = Bold, Background = Background };

    FormattedText Make()
    {
        return new FormattedText(Text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, Bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal),
            FontSize, new SolidColorBrush(Color), 1.0);
    }

    public override Rect Bounds { get { var ft = Make(); return new Rect(Pos.X, Pos.Y, ft.Width + 10, ft.Height + 6); } }
    public override void Move(Vector v) => Pos += v;
    public override bool HitTest(Point p) => Bounds.Contains(p);

    public override void Draw(DrawingContext dc, IPixelSource src)
    {
        if (string.IsNullOrEmpty(Text)) return;
        var ft = Make();
        if (Background)
        {
            double lum = 0.299 * Color.R + 0.587 * Color.G + 0.114 * Color.B;
            var bg = lum > 140 ? Color.FromArgb(215, 0, 0, 0) : Color.FromArgb(215, 255, 255, 255);
            dc.DrawRoundedRectangle(new SolidColorBrush(bg), null, Bounds, 4, 4);
        }
        dc.DrawText(ft, new Point(Pos.X + 5, Pos.Y + 3));
    }
}
