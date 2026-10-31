using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace FluentSnip.Annotations;

/// <summary>
/// Canvas that renders annotations and implements the drawing/edit interaction.
/// The host forwards mouse events (MouseDown/Move/Up) so the same layer works in the capture overlay and in pins.
/// </summary>
public sealed class AnnotationLayer : Canvas
{
    enum Mode { None, Draw, Move, Handle, Erase }

    public IPixelSource Source;
    public Rect CanvasRect;                       // area used by the spotlight dim
    public List<Annotation> Items = new();
    public Annotation Selected;
    public AnnotTool Tool = AnnotTool.None;
    public Color Color = Colors.Red;
    public double Size = 3, FontSize = 20;
    public bool Fill, Rounded, Bold, TextBg;
    public bool ShowAdorners = true;
    public event Action Changed;

    readonly Stack<List<Annotation>> undo = new(), redo = new();
    Mode mode = Mode.None;
    Annotation drawing;
    Point start, last;
    int dragHandle = -1;
    bool moved;
    TextBox editor;
    TextAnnotation editing;

    public bool IsEditing => editor != null;
    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;

    // ---------- history ----------
    static List<Annotation> CloneAll(IEnumerable<Annotation> l) => l.Select(a => a.Clone()).ToList();
    void Snapshot() { undo.Push(CloneAll(Items)); redo.Clear(); }
    void DropSnapshot() { if (undo.Count > 0) undo.Pop(); }
    void Fire() { InvalidateVisual(); Changed?.Invoke(); }

    public void Undo()
    {
        CommitText();
        if (undo.Count == 0) return;
        redo.Push(CloneAll(Items)); Items = undo.Pop(); Selected = null; Renumber(); Fire();
    }

    public void Redo()
    {
        CommitText();
        if (redo.Count == 0) return;
        undo.Push(CloneAll(Items)); Items = redo.Pop(); Selected = null; Renumber(); Fire();
    }

    public void Clear() { Items.Clear(); undo.Clear(); redo.Clear(); Selected = null; Fire(); }

    public void DeleteSelected()
    {
        if (Selected == null) return;
        Snapshot(); Items.Remove(Selected); Selected = null; Renumber(); Fire();
    }

    void Renumber()
    {
        int n = 1;
        foreach (var s in Items.OfType<SerialAnnotation>()) s.Number = n++;
    }

    /// <summary>Applies the current style (color/size) to the selected annotation when the user changes it.</summary>
    public void ApplyStyleToSelected()
    {
        if (Selected == null) return;
        Snapshot();
        Selected.Color = Color; Selected.Size = Size;
        if (Selected is TextAnnotation t) { t.FontSize = FontSize; t.Bold = Bold; t.Background = TextBg; }
        if (Selected is ShapeAnnotation s) { s.Fill = Fill; s.Rounded = Rounded; }
        Fire();
    }

    // ---------- rendering ----------
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        RenderItems(dc, Items, Source, CanvasRect);
        if (ShowAdorners && Selected != null) DrawAdorners(dc, Selected);
    }

    public static void RenderItems(DrawingContext dc, IEnumerable<Annotation> items, IPixelSource src, Rect canvas)
    {
        foreach (var a in items) a.Draw(dc, src);
    }

    void DrawAdorners(DrawingContext dc, Annotation a)
    {
        var accent = new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD4));
        var b = a.Bounds;
        if (!b.IsEmpty)
            dc.DrawRectangle(null, new Pen(accent, 1) { DashStyle = DashStyles.Dash }, Rect.Inflate(b, 3, 3));
        foreach (var h in a.Handles)
            dc.DrawRectangle(Brushes.White, new Pen(accent, 1.5), new Rect(h.X - 4, h.Y - 4, 8, 8));
    }

    // ---------- interaction ----------
    public bool PointerDown(Point p, bool shift)
    {
        if (Tool == AnnotTool.None) return false;
        CommitText();
        moved = false; start = last = p;

        if (Tool == AnnotTool.Eraser) { Snapshot(); mode = Mode.Erase; EraseAt(p); return true; }

        if (Selected != null && Selected.Kind == Tool)
        {
            var hs = Selected.Handles;
            for (int i = 0; i < hs.Count; i++)
                if ((hs[i] - p).Length <= 9) { Snapshot(); mode = Mode.Handle; dragHandle = i; return true; }
        }

        var hit = Items.LastOrDefault(a => a.Kind == Tool && a.HitTest(p));
        if (hit != null) { Selected = hit; Snapshot(); mode = Mode.Move; InvalidateVisual(); return true; }
        Selected = null;

        switch (Tool)
        {
            case AnnotTool.Text: StartTextEdit(p, null); return true;
            case AnnotTool.Serial:
                Snapshot();
                var s = new SerialAnnotation { Center = p, Color = Color, Size = Size, Number = Items.OfType<SerialAnnotation>().Count() + 1 };
                Items.Add(s); Selected = s; mode = Mode.Move; Fire(); return true;
        }

        Snapshot();
        drawing = Create(p);
        Items.Add(drawing); Selected = null; mode = Mode.Draw;
        InvalidateVisual();
        return true;
    }

    Annotation Create(Point p)
    {
        switch (Tool)
        {
            case AnnotTool.Rectangle: case AnnotTool.Ellipse:
                return new ShapeAnnotation { IsEllipse = Tool == AnnotTool.Ellipse, Fill = Fill, Rounded = Rounded, Color = Color, Size = Size, L = p.X, T = p.Y, R = p.X, B = p.Y };
            case AnnotTool.Line: case AnnotTool.Arrow: return new LineAnnotation { Arrow = Tool == AnnotTool.Arrow, Color = Color, Size = Size, P0 = p, P1 = p };
            default:
                var st = new StrokeAnnotation { Highlighter = Tool == AnnotTool.Highlighter, Color = Color, Size = Size };
                st.Points.Add(p); return st;
        }
    }

    public void PointerMove(Point p, bool shift)
    {
        if (mode == Mode.None) return;
        if ((p - start).Length > 2) moved = true;
        switch (mode)
        {
            case Mode.Erase: EraseAt(p); break;
            case Mode.Move:
                if (Selected != null) { Selected.Move(p - last); last = p; InvalidateVisual(); }
                break;
            case Mode.Handle:
                if (Selected != null) { Selected.SetHandle(dragHandle, shift ? Constrain(Selected, dragHandle, p) : p); InvalidateVisual(); }
                break;
            case Mode.Draw:
                switch (drawing)
                {
                    case RectBased r:
                        var q = p;
                        if (shift) { double d = Math.Max(Math.Abs(p.X - start.X), Math.Abs(p.Y - start.Y)); q = new Point(start.X + Math.Sign(p.X - start.X) * d, start.Y + Math.Sign(p.Y - start.Y) * d); }
                        r.R = q.X; r.B = q.Y; break;
                    case LineAnnotation l:
                        l.P1 = shift ? Snap45(l.P0, p) : p; break;
                    case StrokeAnnotation s:
                        if ((p - s.Points[^1]).Length >= 1.5) s.Points.Add(p); break;
                }
                InvalidateVisual();
                break;
        }
    }

    static Point Snap45(Point a, Point p)
    {
        var v = p - a; double len = v.Length, ang = Math.Atan2(v.Y, v.X), step = Math.PI / 4;
        ang = Math.Round(ang / step) * step;
        return new Point(a.X + Math.Cos(ang) * len, a.Y + Math.Sin(ang) * len);
    }

    static Point Constrain(Annotation a, int handle, Point p)
    {
        if (a is not RectBased r) return p;
        var corners = r.Handles; var opp = corners[(handle + 2) % 4];
        double d = Math.Max(Math.Abs(p.X - opp.X), Math.Abs(p.Y - opp.Y));
        return new Point(opp.X + Math.Sign(p.X - opp.X) * d, opp.Y + Math.Sign(p.Y - opp.Y) * d);
    }

    public void PointerUp(Point p)
    {
        var m = mode; mode = Mode.None; dragHandle = -1;
        if (m == Mode.None) return;
        if (m == Mode.Draw && drawing != null)
        {
            bool tiny = drawing switch
            {
                RectBased r => r.Rect.Width < 3 && r.Rect.Height < 3,
                LineAnnotation l => (l.P1 - l.P0).Length < 3,
                _ => false
            };
            if (tiny) { Items.Remove(drawing); DropSnapshot(); }
            else Selected = drawing is StrokeAnnotation ? null : drawing;
            drawing = null;
        }
        else if (m == Mode.Move && !moved)
        {
            DropSnapshot();
            if (Selected is TextAnnotation t) { StartTextEdit(t.Pos, t); return; }
        }
        else if (m == Mode.Handle && !moved) DropSnapshot();
        Fire();
    }

    void EraseAt(Point p)
    {
        var hit = Items.LastOrDefault(a => a.HitTest(p));
        if (hit != null) { Items.Remove(hit); if (Selected == hit) Selected = null; Renumber(); Fire(); }
    }

    public void AdjustSize(double delta)
    {
        Size = Math.Clamp(Size + delta, 1, 40);
        if (Tool is AnnotTool.Text) FontSize = Math.Clamp(FontSize + delta * 2, 8, 120);
        ApplyStyleToSelected();
    }

    // ---------- text editing ----------
    void StartTextEdit(Point p, TextAnnotation existing)
    {
        CommitText();
        editing = existing;
        if (existing != null) { Snapshot(); Items.Remove(existing); Selected = null; InvalidateVisual(); }
        var col = existing?.Color ?? Color;
        editor = new TextBox
        {
            Text = existing?.Text ?? "", FontFamily = new FontFamily("Segoe UI"),
            FontSize = existing?.FontSize ?? FontSize, FontWeight = (existing?.Bold ?? Bold) ? FontWeights.Bold : FontWeights.Normal,
            Foreground = new SolidColorBrush(col), CaretBrush = new SolidColorBrush(col),
            Background = new SolidColorBrush(Color.FromArgb(70, 128, 128, 128)),
            BorderBrush = new SolidColorBrush(col), BorderThickness = new Thickness(1),
            AcceptsReturn = true, MinWidth = 120, Padding = new Thickness(4, 2, 4, 2), TextWrapping = TextWrapping.NoWrap
        };
        SetLeft(editor, p.X); SetTop(editor, p.Y);
        editor.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { CancelText(); e.Handled = true; }
            else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control) { CommitText(); e.Handled = true; }
        };
        Children.Add(editor);
        editor.Loaded += (_, _) => editor?.Focus();
        editor.Focus();
    }

    public void CommitText()
    {
        if (editor == null) return;
        var tb = editor; editor = null; Children.Remove(tb);
        string txt = tb.Text.TrimEnd();
        if (txt.Length > 0)
        {
            if (editing == null) Snapshot();
            var t = editing ?? new TextAnnotation { Color = Color, Size = Size, FontSize = FontSize, Bold = Bold, Background = TextBg };
            t.Text = txt; t.Pos = new Point(GetLeft(tb), GetTop(tb));
            Items.Add(t); Selected = t;
        }
        else if (editing != null) { /* emptied existing text = deleted (snapshot already taken) */ }
        editing = null; Fire();
    }

    void CancelText()
    {
        if (editor == null) return;
        var tb = editor; editor = null; Children.Remove(tb);
        if (editing != null) { Items.Add(editing); }   // restore
        editing = null; Fire();
    }
}
