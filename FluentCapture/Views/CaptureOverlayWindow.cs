using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using FluentSnip.Annotations;
using FluentSnip.Capture;
using FluentSnip.Core;
using FluentSnip.Native;
using FluentSnip.Services;

namespace FluentSnip.Views;

public enum OverlayIntent { Capture, Record }

/// <summary>
/// Full virtual-desktop overlay: frozen screenshot, window/UI-element detection, selection editing,
/// annotation toolbar, magnifier + color picker, OCR / QR / scroll-capture / record / pin / save / copy.
/// All coordinates inside are raw device pixels relative to the virtual desktop origin.
/// </summary>
public sealed class CaptureOverlayWindow : Window
{
    enum Phase { Idle, Selecting, Selected }

    readonly ScreenSnapshot snap;
    readonly OverlayIntent intent;
    readonly AppSettings cfg = SettingsService.Current;
    readonly Rect bounds;

    readonly Canvas root = new() { Background = Brushes.Transparent };
    readonly SelectionVisual vis = new();
    readonly AnnotationLayer layer = new();
    readonly MagnifierPanel mag = new();
    readonly Border sizeBadge = new();
    readonly TextBlock sizeText = new();
    Border toolbar, optionBar;
    readonly Dictionary<AnnotTool, ToggleButton> toolButtons = new();
    Button qrButton, undoBtn, redoBtn;
    StackPanel toolOptions, swatchRow;
    Slider sizeSlider;
    Popup popup;
    Image cursorImg;

    Phase phase = Phase.Idle;
    Rect sel;
    Point pressPos, curPos, dragStart;
    Handle dragHandle = Handle.None;
    Rect selAtDrag;
    bool pressMoved, annotating, showCursor, closing;
    AnnotTool tool = AnnotTool.None;
    ColorFormat colorFmt;
    double winScale = 1;

    // candidate detection
    List<Rect> levels; int levelIdx;
    Rect? candidate; IntPtr candHwnd;
    CancellationTokenSource probeCts; Point lastProbe = new(-100, -100); DateTime lastProbeTime;

    // QR
    List<QrHit> qrHits = new();
    readonly DispatcherTimer qrTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    int qrToken;

    public CaptureOverlayWindow(ScreenSnapshot snapshot, OverlayIntent intent)
    {
        snap = snapshot; this.intent = intent;
        bounds = new Rect(0, 0, snap.PixelWidth, snap.PixelHeight);
        Enum.TryParse(cfg.MagnifierColorFormat, out colorFmt);
        showCursor = cfg.ShowCursor && snap.CursorPatch != null;

        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false; Topmost = true;
        AllowsTransparency = false; Background = Brushes.Black; Cursor = Cursors.Cross;
        WindowStartupLocation = WindowStartupLocation.Manual; Left = 0; Top = 0; Width = 200; Height = 200;
        UseLayoutRounding = true; SnapsToDevicePixels = true;

        BuildScene();
        SourceInitialized += OnSourceInitialized;
        ContentRendered += (_, _) =>
        {
            WinHelper.Foreground(this); Focus(); Keyboard.Focus(this);
            var cp = WinHelper.CursorPos();
            curPos = Clamp(new Point(cp.X - snap.OriginX, cp.Y - snap.OriginY));
            UpdateCandidate(curPos); RefreshVisual(); UpdateMagnifier(curPos);
        };
        PreviewKeyDown += OnKeyDown;
        qrTimer.Tick += (_, _) => { qrTimer.Stop(); DetectQr(); };
        Closed += (_, _) => { probeCts?.Cancel(); qrTimer.Stop(); };
    }

    // ------------------------------------------------------------------ setup
    void OnSourceInitialized(object s, EventArgs e)
    {
        var h = WinHelper.Handle(this);
        WinHelper.SwallowDpiChanges(this);
        winScale = WinHelper.Scale(this);
        WinHelper.SetPixelRect(this, snap.OriginX, snap.OriginY, snap.PixelWidth, snap.PixelHeight, true, true);
        var host = (Grid)Content;
        host.LayoutTransform = new ScaleTransform(1 / winScale, 1 / winScale);
        var src = HwndSource.FromHwnd(h);
        if (src != null) src.CompositionTarget.BackgroundColor = Colors.Black;
    }

    void BuildScene()
    {
        root.Width = snap.PixelWidth; root.Height = snap.PixelHeight; root.ClipToBounds = true;
        var bg = new Image { Source = snap.Image, Width = snap.PixelWidth, Height = snap.PixelHeight, Stretch = Stretch.None };
        RenderOptions.SetBitmapScalingMode(bg, BitmapScalingMode.NearestNeighbor);
        root.Children.Add(bg);

        if (snap.CursorPatch != null)
        {
            cursorImg = new Image { Source = snap.CursorPatch, Width = snap.CursorPatch.PixelWidth, Height = snap.CursorPatch.PixelHeight, Visibility = showCursor ? Visibility.Visible : Visibility.Collapsed, IsHitTestVisible = false };
            Canvas.SetLeft(cursorImg, snap.CursorPatchPos.X); Canvas.SetTop(cursorImg, snap.CursorPatchPos.Y);
            root.Children.Add(cursorImg);
        }

        vis.Width = snap.PixelWidth; vis.Height = snap.PixelHeight; vis.IsHitTestVisible = false;
        vis.DimAlpha = cfg.DimOpacity / 100.0;
        root.Children.Add(vis);

        layer.Width = snap.PixelWidth; layer.Height = snap.PixelHeight; layer.Source = snap;
        layer.Color = Ui.ParseColor(cfg.AnnotColor, Colors.Red); layer.Size = cfg.AnnotSize; layer.FontSize = cfg.TextSize;
        layer.Changed += () => { UpdateUndoState(); };
        root.Children.Add(layer);

        // size badge
        sizeText.FontSize = 12; sizeText.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        sizeBadge.Child = sizeText; sizeBadge.Padding = new Thickness(8, 3, 8, 3); sizeBadge.CornerRadius = new CornerRadius(6);
        sizeBadge.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        sizeBadge.SetResourceReference(Border.BorderBrushProperty, "StrokeBrush"); sizeBadge.BorderThickness = new Thickness(1);
        sizeBadge.Visibility = Visibility.Collapsed; sizeBadge.Cursor = Cursors.IBeam; sizeBadge.ToolTip = "Click to type an exact size";
        sizeBadge.MouseLeftButtonDown += (_, e) => { e.Handled = true; ShowSizePopup(); };
        root.Children.Add(sizeBadge);

        BuildToolbar();
        mag.Visibility = Visibility.Collapsed; root.Children.Add(mag);

        popup = new Popup { AllowsTransparency = true, StaysOpen = false, Placement = PlacementMode.Bottom, PlacementTarget = sizeBadge };

        root.MouseLeftButtonDown += OnLeftDown;
        root.MouseMove += OnMove;
        root.MouseLeftButtonUp += OnLeftUp;
        root.MouseRightButtonUp += OnRightUp;
        root.MouseWheel += OnWheel;

        Content = new Grid { Children = { root }, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
    }

    // ------------------------------------------------------------------ toolbar
    void BuildToolbar()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        void Tool(AnnotTool t, object content, string tip)
        {
            var b = Ui.IconToggle(content, tip, on => SetTool(on ? t : AnnotTool.None));
            toolButtons[t] = b; row.Children.Add(b);
        }
        Tool(AnnotTool.Rectangle, Icons.Rectangle, "Rectangle (Shift+1)");
        Tool(AnnotTool.Ellipse, Icons.Ellipse, "Ellipse (Shift+2)");
        Tool(AnnotTool.Line, Ui.Glyph(Icons.Line, 16, -45), "Line (Shift+3)");
        Tool(AnnotTool.Arrow, Ui.Glyph(Icons.Arrow, 16, -45), "Arrow (Shift+4)");
        Tool(AnnotTool.Pencil, Icons.Pen, "Pencil (Shift+5)");
        Tool(AnnotTool.Highlighter, Icons.Highlighter, "Highlighter (Shift+6)");
        Tool(AnnotTool.Serial, Icons.Number, "Number (Shift+7)");
        Tool(AnnotTool.Text, Icons.Text, "Text (Shift+8)");
        Tool(AnnotTool.Eraser, Icons.Eraser, "Eraser (Shift+E)");
        row.Children.Add(Ui.VSeparator());
        undoBtn = Ui.IconButton(Icons.Undo, "Undo (Ctrl+Z)", () => layer.Undo()); redoBtn = Ui.IconButton(Icons.Redo, "Redo (Ctrl+Y)", () => layer.Redo());
        row.Children.Add(undoBtn); row.Children.Add(redoBtn);
        row.Children.Add(Ui.VSeparator());
        row.Children.Add(Ui.IconButton(Icons.Ocr, "OCR (Shift+C)", () => _ = RunOcr(false)));
        qrButton = Ui.IconButton(Icons.Qr, "QR Code (Shift+Q)", ShowQr); row.Children.Add(qrButton);
        row.Children.Add(Ui.IconButton(Icons.Scroll, "Scrolling Screenshot (Shift+S)", StartScroll));
        row.Children.Add(Ui.IconButton(Icons.Video, "Record (Shift+R)", StartRecording));
        row.Children.Add(Ui.VSeparator());
        row.Children.Add(Ui.IconButton(Icons.Pin, "Pin to screen (Ctrl+T)", PinSelection));
        row.Children.Add(Ui.IconButton(Icons.Save, "Save (Ctrl+S) - Ctrl+Shift+S saves directly", () => SaveSelection(false)));
        row.Children.Add(Ui.IconButton(Icons.Copy, "Copy (Enter / Ctrl+C)", () => CopySelection(true)));
        var closeB = Ui.IconButton(Icons.Close, "Close (Esc)", () => CloseOverlay()); row.Children.Add(closeB);

        toolbar = new Border { Style = Ui.Style("PanelBorder"), Child = row, Visibility = Visibility.Collapsed };
        toolbar.MouseLeftButtonDown += (_, e) => e.Handled = true;
        toolbar.MouseRightButtonUp += (_, e) => e.Handled = true;
        toolbar.MouseWheel += (_, e) => e.Handled = true;
        root.Children.Add(toolbar);

        // ---- option bar ----
        var o = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        swatchRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        string[] colors = { "#E81123", "#F7630C", "#FFB900", "#16C60C", "#0078D4", "#8764B8", "#000000", "#FFFFFF" };
        foreach (var hex in colors)
        {
            var c = Ui.ParseColor(hex, Colors.Red);
            var sw = new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(10), Margin = new Thickness(3, 0, 3, 0), Background = new SolidColorBrush(c), Cursor = Cursors.Hand, BorderThickness = new Thickness(1), ToolTip = hex };
            sw.SetResourceReference(Border.BorderBrushProperty, "StrokeBrush");
            sw.MouseLeftButtonDown += (_, e) => { e.Handled = true; SetColor(c); };
            swatchRow.Children.Add(sw);
        }
        var more = Ui.IconButton(Icons.Color, "More colors", ShowColorPopup); more.Width = 28; more.Height = 28; more.FontSize = 14;
        swatchRow.Children.Add(more);
        o.Children.Add(swatchRow);
        o.Children.Add(Ui.VSeparator());

        sizeSlider = new Slider { Minimum = 1, Maximum = 30, Width = 100, Value = cfg.AnnotSize, VerticalAlignment = VerticalAlignment.Center, Focusable = false, ToolTip = "Size (mouse wheel)" };
        sizeSlider.ValueChanged += (_, e) => { layer.Size = e.NewValue; cfg.AnnotSize = e.NewValue; layer.ApplyStyleToSelected(); };
        o.Children.Add(sizeSlider);

        toolOptions = new StackPanel { Orientation = Orientation.Horizontal };
        toolOptions.Children.Add(Ui.Chip("Filled", false, on => { layer.Fill = on; layer.ApplyStyleToSelected(); }));
        toolOptions.Children.Add(Ui.Chip("Rounded rect", false, on => { layer.Rounded = on; layer.ApplyStyleToSelected(); }));
        toolOptions.Children.Add(Ui.Chip("Bold", false, on => { layer.Bold = on; layer.ApplyStyleToSelected(); }));
        toolOptions.Children.Add(Ui.Chip("Text background", false, on => { layer.TextBg = on; layer.ApplyStyleToSelected(); }));
        o.Children.Add(toolOptions);
        optionBar = new Border { Style = Ui.Style("PanelBorder"), Child = o, Padding = new Thickness(8, 5, 8, 5), Visibility = Visibility.Collapsed };
        optionBar.MouseLeftButtonDown += (_, e) => e.Handled = true;
        optionBar.MouseRightButtonUp += (_, e) => e.Handled = true;
        optionBar.MouseWheel += (_, e) => e.Handled = true;
        root.Children.Add(optionBar);
        UpdateOptionVisibility();
        UpdateUndoState();
    }

    void UpdateUndoState() { if (undoBtn != null) { undoBtn.IsEnabled = layer.CanUndo; redoBtn.IsEnabled = layer.CanRedo; } }

    void SetTool(AnnotTool t)
    {
        layer.CommitText();
        tool = t; layer.Tool = t; layer.Selected = null; layer.InvalidateVisual();
        foreach (var kv in toolButtons) kv.Value.IsChecked = kv.Key == t;
        UpdateOptionVisibility();
        Cursor = t == AnnotTool.None ? Cursors.Cross : Cursors.Pen;
        LayoutToolbars();
    }

    void UpdateOptionVisibility()
    {
        bool any = tool != AnnotTool.None;
        swatchRow.Visibility = sizeSlider.Visibility = any && tool != AnnotTool.Eraser ? Visibility.Visible : Visibility.Collapsed;
        var ch = toolOptions.Children.Cast<ToggleButton>().ToArray();
        ch[0].Visibility = tool is AnnotTool.Rectangle or AnnotTool.Ellipse ? Visibility.Visible : Visibility.Collapsed;
        ch[1].Visibility = tool == AnnotTool.Rectangle ? Visibility.Visible : Visibility.Collapsed;
        ch[2].Visibility = ch[3].Visibility = tool == AnnotTool.Text ? Visibility.Visible : Visibility.Collapsed;
    }

    void SetColor(Color c)
    {
        layer.Color = c; cfg.AnnotColor = Ui.ToHex(c); SettingsService.Save(); layer.ApplyStyleToSelected();
    }

    void ShowColorPopup()
    {
        var pop = new Popup { AllowsTransparency = true, StaysOpen = false, Placement = PlacementMode.Top, PlacementTarget = swatchRow };
        var c = layer.Color;
        var sp = new StackPanel { Width = 220 };
        var hex = new TextBox { Text = Ui.ToHex(c), Margin = new Thickness(0, 6, 0, 0) };
        Slider S(string name, byte v, Action<byte> set)
        {
            var sl = new Slider { Minimum = 0, Maximum = 255, Value = v, Margin = new Thickness(0, 4, 0, 0), Focusable = false, ToolTip = name };
            sl.ValueChanged += (_, e) => set((byte)e.NewValue);
            return sl;
        }
        byte r = c.R, g = c.G, b = c.B;
        void Push() { var nc = Color.FromRgb(r, g, b); hex.Text = Ui.ToHex(nc); SetColor(nc); }
        sp.Children.Add(S("Red", r, v => { r = v; Push(); }));
        sp.Children.Add(S("Green", g, v => { g = v; Push(); }));
        sp.Children.Add(S("Blue", b, v => { b = v; Push(); }));
        hex.KeyDown += (_, e) => { if (e.Key == Key.Enter) SetColor(Ui.ParseColor(hex.Text, layer.Color)); };
        sp.Children.Add(hex);
        pop.Child = new Border { Style = Ui.Style("PanelBorder"), Padding = new Thickness(12), Child = sp, Margin = new Thickness(12) };
        pop.IsOpen = true;
    }

    void ShowSizePopup()
    {
        if (phase != Phase.Selected) return;
        var w = new TextBox { Text = ((int)sel.Width).ToString(), Width = 64, Padding = new Thickness(4) };
        var h = new TextBox { Text = ((int)sel.Height).ToString(), Width = 64, Padding = new Thickness(4), Margin = new Thickness(8, 0, 0, 0) };
        var ok = Ui.IconButton(Icons.Accept, "Apply", () => { });
        void Apply()
        {
            if (int.TryParse(w.Text, out int nw) && int.TryParse(h.Text, out int nh) && nw > 1 && nh > 1)
            {
                var r = new Rect(sel.X, sel.Y, nw, nh);
                if (r.Right > bounds.Right) r.X = Math.Max(0, bounds.Right - nw);
                if (r.Bottom > bounds.Bottom) r.Y = Math.Max(0, bounds.Bottom - nh);
                SetSelection(Rect.Intersect(r, bounds));
            }
            popup.IsOpen = false;
        }
        ok.Click += (_, _) => Apply();
        KeyEventHandler kd = (_, e) => { if (e.Key == Key.Enter) Apply(); };
        w.KeyDown += kd; h.KeyDown += kd;
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(w); row.Children.Add(Ui.Label("×", 14)); ((TextBlock)row.Children[^1]).Margin = new Thickness(6, 0, 0, 0);
        row.Children.Add(h); row.Children.Add(ok);
        popup.Child = new Border { Style = Ui.Style("PanelBorder"), Padding = new Thickness(8), Child = row, Margin = new Thickness(12) };
        popup.IsOpen = true; w.Focus(); w.SelectAll();
    }

    // ------------------------------------------------------------------ geometry helpers
    int UiScaleFor(Point p) => 0;
    double UiScale(Point p) => snap.MonitorAt(p).Scale;

    void SetSelection(Rect r)
    {
        sel = r; layer.CanvasRect = r;
        layer.Clip = new RectangleGeometry(r, cfg.RoundedCorners ? cfg.CornerRadius : 0, cfg.RoundedCorners ? cfg.CornerRadius : 0);
        RefreshVisual();
        LayoutToolbars();
        ScheduleQr();
    }

    void RefreshVisual()
    {
        double ui = UiScale(curPos);
        vis.Ui = ui; vis.Radius = cfg.RoundedCorners ? cfg.CornerRadius : 0;
        vis.IsSelection = phase != Phase.Idle;
        vis.ShowHandles = phase == Phase.Selected;
        vis.Hole = phase != Phase.Idle ? sel : candidate;
        vis.Cross = (cfg.ShowCrosshair && phase != Phase.Selected) ? curPos : null;
        vis.Qr = qrHits.Where(h => !h.Bounds.IsEmpty).Select(h => new Rect(h.Bounds.X + sel.X, h.Bounds.Y + sel.Y, h.Bounds.Width, h.Bounds.Height)).ToList();
        vis.InvalidateVisual();

        var r = phase != Phase.Idle ? sel : candidate;
        if (r is Rect rr && rr.Width > 0)
        {
            sizeText.Text = $"{(int)rr.Width} × {(int)rr.Height}";
            sizeBadge.LayoutTransform = new ScaleTransform(ui, ui);
            sizeBadge.Visibility = Visibility.Visible; sizeBadge.IsHitTestVisible = phase == Phase.Selected;
            sizeBadge.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double bh = sizeBadge.DesiredSize.Height;
            double y = rr.Top - bh - 6 * ui; if (y < 0) y = rr.Top + 6 * ui;
            Canvas.SetLeft(sizeBadge, Math.Max(0, rr.Left)); Canvas.SetTop(sizeBadge, y);
        }
        else sizeBadge.Visibility = Visibility.Collapsed;
    }

    void LayoutToolbars()
    {
        if (phase != Phase.Selected) { toolbar.Visibility = optionBar.Visibility = Visibility.Collapsed; return; }
        var mon = snap.MonitorAt(new Point(sel.X + sel.Width / 2, sel.Y + sel.Height / 2));
        double ui = mon.Scale; var mb = mon.Bounds;
        toolbar.LayoutTransform = optionBar.LayoutTransform = new ScaleTransform(ui, ui);
        toolbar.Visibility = Visibility.Visible;
        optionBar.Visibility = tool != AnnotTool.None ? Visibility.Visible : Visibility.Collapsed;
        var inf = new Size(double.PositiveInfinity, double.PositiveInfinity);
        toolbar.Measure(inf); optionBar.Measure(inf);
        Size ts = toolbar.DesiredSize, os = optionBar.DesiredSize;
        double gap = 8 * ui, total = ts.Height + os.Height + gap;

        double x = sel.Right - ts.Width; x = Math.Min(Math.Max(mb.Left + 4, x), mb.Right - ts.Width - 4);
        double yBelow = sel.Bottom + gap, yAbove = sel.Top - gap - total;
        double y;
        if (yBelow + total <= mb.Bottom - 4) y = yBelow;
        else if (yAbove >= mb.Top + 4) y = yAbove;
        else y = Math.Max(mb.Top + 4, Math.Min(sel.Bottom - total - gap, mb.Bottom - total - 4));
        Canvas.SetLeft(toolbar, x); Canvas.SetTop(toolbar, y);
        double cx = sel.Left + sel.Width / 2;
double ox;
if (cx < mb.Left + mb.Width / 2) ox = x; else ox = x + ts.Width - os.Width;
ox = Math.Clamp(ox, mb.Left + 4, mb.Right - os.Width - 4);
        Canvas.SetLeft(optionBar, ox); Canvas.SetTop(optionBar, y + ts.Height + gap);
    }

    void UpdateMagnifier(Point p)
    {
        bool show = cfg.ShowMagnifier && (phase == Phase.Idle || phase == Phase.Selecting || (phase == Phase.Selected && dragHandle != Handle.None && dragHandle != Handle.Move));
        if (!show) { mag.Visibility = Visibility.Collapsed; return; }
        double ui = UiScale(p);
        mag.LayoutTransform = new ScaleTransform(ui, ui);
        mag.Visibility = Visibility.Visible;
        mag.Update(snap, (int)p.X, (int)p.Y, colorFmt);
        mag.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var ms = mag.DesiredSize; var mb = snap.MonitorAt(p).Bounds;
        double x = p.X + 22 * ui, y = p.Y + 22 * ui;
        if (x + ms.Width > mb.Right - 4) x = p.X - ms.Width - 22 * ui;
        if (y + ms.Height > mb.Bottom - 4) y = p.Y - ms.Height - 22 * ui;
        Canvas.SetLeft(mag, Math.Max(mb.Left, x)); Canvas.SetTop(mag, Math.Max(mb.Top, y));
    }

    static Rect Norm(Point a, Point b) => new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
    Point Clamp(Point p) => new(Math.Clamp(p.X, 0, bounds.Right), Math.Clamp(p.Y, 0, bounds.Bottom));

    Handle HitHandle(Point p)
    {
        double tol = 7 * UiScale(p);
        var hp = SelectionVisual.HandlePoints(sel);
        Handle[] order = { Handle.TL, Handle.T, Handle.TR, Handle.R, Handle.BR, Handle.B, Handle.BL, Handle.L };
        for (int i = 0; i < 8; i++) if ((hp[i] - p).Length <= tol + 2) return order[i];
        bool inX = p.X >= sel.Left - tol && p.X <= sel.Right + tol, inY = p.Y >= sel.Top - tol && p.Y <= sel.Bottom + tol;
        if (inX && inY)
        {
            if (Math.Abs(p.X - sel.Left) <= tol) return Handle.L;
            if (Math.Abs(p.X - sel.Right) <= tol) return Handle.R;
            if (Math.Abs(p.Y - sel.Top) <= tol) return Handle.T;
            if (Math.Abs(p.Y - sel.Bottom) <= tol) return Handle.B;
        }
        return sel.Contains(p) ? Handle.Move : Handle.None;
    }

    static Cursor CursorFor(Handle h) => h switch
    {
        Handle.TL or Handle.BR => Cursors.SizeNWSE, Handle.TR or Handle.BL => Cursors.SizeNESW,
        Handle.T or Handle.B => Cursors.SizeNS, Handle.L or Handle.R => Cursors.SizeWE, Handle.Move => Cursors.SizeAll, _ => Cursors.Cross
    };

    // ------------------------------------------------------------------ mouse
    bool OverUi => (toolbar?.IsMouseOver ?? false) || (optionBar?.IsMouseOver ?? false);

    void OnLeftDown(object s, MouseButtonEventArgs e)
    {
        layer.CommitText();
        var p = e.GetPosition(root); pressPos = dragStart = p; pressMoved = false;
        if (phase == Phase.Selected)
        {
            if (e.ClickCount == 2 && tool == AnnotTool.None && sel.Contains(p)) { CopySelection(true); return; }
            if (tool != AnnotTool.None && sel.Contains(p))
            {
                if (layer.PointerDown(p, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))) { annotating = true; root.CaptureMouse(); return; }
            }
            if (tool == AnnotTool.None || !sel.Contains(p))
            {
                var h = HitHandle(p);
                if (h != Handle.None) { dragHandle = h; selAtDrag = sel; root.CaptureMouse(); UpdateMagnifier(p); return; }
            }
        }
        // start a new selection (click without drag adopts the hovered window/element)
        phase = Phase.Selecting; selAtDrag = sel; dragHandle = Handle.None; root.CaptureMouse();
        qrHits.Clear(); toolbar.Visibility = optionBar.Visibility = Visibility.Collapsed;
        SetSelection(new Rect(p, new Size(0, 0)));
    }

    void OnMove(object s, MouseEventArgs e)
    {
        var p = Clamp(e.GetPosition(root)); curPos = p;
        if (cfg.DetectUiElements || true) { /* detection only in idle */ }

        if (annotating) { layer.PointerMove(p, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)); return; }

        if (phase == Phase.Idle)
        {
            UpdateCandidate(p); RefreshVisual(); UpdateMagnifier(p); return;
        }

        if (e.LeftButton == MouseButtonState.Pressed && root.IsMouseCaptured)
        {
            if ((p - pressPos).Length > 3) pressMoved = true;
            if (phase == Phase.Selecting && pressMoved)
            {
                var a = pressPos; var q = p;
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                {
                    double d = Math.Max(Math.Abs(q.X - a.X), Math.Abs(q.Y - a.Y));
                    q = Clamp(new Point(a.X + Math.Sign(q.X - a.X) * d, a.Y + Math.Sign(q.Y - a.Y) * d));
                }
                SetSelection(Norm(a, q));
            }
            else if (phase == Phase.Selected && dragHandle != Handle.None) DragSelection(p);
            UpdateMagnifier(p);
            return;
        }

        if (phase == Phase.Selected)
        {
            if (OverUi) { Cursor = Cursors.Arrow; mag.Visibility = Visibility.Collapsed; return; }
            if (tool != AnnotTool.None && sel.Contains(p)) Cursor = tool == AnnotTool.Eraser ? Cursors.Cross : Cursors.Pen;
            else Cursor = CursorFor(HitHandle(p));
        }
        RefreshVisual(); UpdateMagnifier(p);
    }

    void DragSelection(Point p)
    {
        var r = selAtDrag;
        if (dragHandle == Handle.Move)
        {
            var d = p - dragStart;
            double x = Math.Clamp(r.X + d.X, 0, bounds.Right - r.Width), y = Math.Clamp(r.Y + d.Y, 0, bounds.Bottom - r.Height);
            SetSelection(new Rect(x, y, r.Width, r.Height)); return;
        }
        double l = r.Left, t = r.Top, rt = r.Right, b = r.Bottom;
        if (dragHandle is Handle.TL or Handle.L or Handle.BL) l = p.X;
        if (dragHandle is Handle.TR or Handle.R or Handle.BR) rt = p.X;
        if (dragHandle is Handle.TL or Handle.T or Handle.TR) t = p.Y;
        if (dragHandle is Handle.BL or Handle.B or Handle.BR) b = p.Y;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && dragHandle is Handle.TL or Handle.TR or Handle.BL or Handle.BR && r.Height > 0)
        {
            double ratio = r.Width / r.Height, w = Math.Abs(rt - l), h = Math.Abs(b - t);
            if (w / Math.Max(h, 1) > ratio) w = h * ratio; else h = w / ratio;
            if (dragHandle is Handle.TL or Handle.BL) l = rt - w * Math.Sign(rt - l == 0 ? 1 : rt - l); else rt = l + w * Math.Sign(rt - l == 0 ? 1 : rt - l);
            if (dragHandle is Handle.TL or Handle.TR) t = b - h * Math.Sign(b - t == 0 ? 1 : b - t); else b = t + h * Math.Sign(b - t == 0 ? 1 : b - t);
        }
        var nr = Norm(Clamp(new Point(l, t)), Clamp(new Point(rt, b)));
        if (nr.Width >= 2 && nr.Height >= 2) SetSelection(nr);
    }

    void OnLeftUp(object s, MouseButtonEventArgs e)
    {
        var p = e.GetPosition(root);
        if (root.IsMouseCaptured) root.ReleaseMouseCapture();
        if (annotating) { annotating = false; layer.PointerUp(p); return; }

        if (phase == Phase.Selecting)
        {
            if (!pressMoved)
            {
                // click: take the hovered window / element
                if (candidate is Rect c && c.Width > 1) { phase = Phase.Selected; SetSelection(c); }
                else { phase = Phase.Idle; sel = default; }
            }
            else if (sel.Width < 3 || sel.Height < 3) { phase = Phase.Idle; sel = default; }
            else phase = Phase.Selected;
            if (phase == Phase.Selected && intent == OverlayIntent.Record) { StartRecording(); return; }
            if (phase == Phase.Selected) { SetSelection(sel); Cursor = Cursors.Arrow; }
            RefreshVisual(); LayoutToolbars(); UpdateMagnifier(p);
        }
        dragHandle = Handle.None;
        UpdateMagnifier(p);
    }

    void OnRightUp(object s, MouseButtonEventArgs e)
    {
        if (phase != Phase.Idle)
        {
            phase = Phase.Idle; sel = default; layer.Clear(); SetTool(AnnotTool.None); qrHits.Clear();
            toolbar.Visibility = optionBar.Visibility = Visibility.Collapsed; Cursor = Cursors.Cross;
            RefreshVisual(); UpdateCandidate(curPos); UpdateMagnifier(curPos);
        }
        else CloseOverlay();
    }

    void OnWheel(object s, MouseWheelEventArgs e)
    {
        int dir = e.Delta > 0 ? 1 : -1;
        if (phase == Phase.Idle && levels != null && levels.Count > 1)
        {
            levelIdx = Math.Clamp(levelIdx + dir, 0, levels.Count - 1);
            candidate = levels[levelIdx]; RefreshVisual(); return;
        }
        if (phase == Phase.Selected && tool != AnnotTool.None)
        {
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                var c = layer.Color; int a = Math.Clamp(c.A + dir * 25, 25, 255);
                layer.Color = Color.FromArgb((byte)a, c.R, c.G, c.B); layer.ApplyStyleToSelected();
            }
            else { layer.AdjustSize(dir); sizeSlider.Value = layer.Size; }
        }
    }

    // ------------------------------------------------------------------ candidate detection
    void UpdateCandidate(Point p)
    {
        var win = snap.WindowAt(p);
        if (win == null) { candidate = null; levels = null; candHwnd = IntPtr.Zero; return; }
        var winRect = Rect.Intersect(win.Bounds, bounds);
        if (win.Hwnd != candHwnd) { candHwnd = win.Hwnd; levels = null; }
        if (levels == null || !levels.Any(l => l.Contains(p)))
        {
            levels = new List<Rect> { winRect }; levelIdx = 0; candidate = winRect;
        }
        else
        {
            var keep = levels.Where(l => l.Contains(p)).ToList();
            if (keep.Count != levels.Count) { levels = keep; levelIdx = Math.Min(levelIdx, levels.Count - 1); }
            candidate = levels[Math.Clamp(levelIdx, 0, levels.Count - 1)];
        }
        if (cfg.DetectUiElements && ((p - lastProbe).Length > 3) && (DateTime.UtcNow - lastProbeTime).TotalMilliseconds > 60)
        {
            lastProbe = p; lastProbeTime = DateTime.UtcNow; ProbeAsync(p, win, winRect);
        }
    }

    void ProbeAsync(Point p, WindowInfo win, Rect winRect)
    {
        probeCts?.Cancel(); probeCts = new CancellationTokenSource();
        var ct = probeCts.Token;
        var screenPt = new Point(p.X + snap.OriginX, p.Y + snap.OriginY);
        Task.Run(() => UiaProbe.Probe(win.Hwnd, screenPt, ct)).ContinueWith(t =>
        {
            if (t.IsFaulted || ct.IsCancellationRequested) return;
            Dispatcher.BeginInvoke(() =>
            {
                if (ct.IsCancellationRequested || phase != Phase.Idle || closing) return;
                var list = new List<Rect>();
                foreach (var r in t.Result.AsEnumerable().Reverse())
                {
                    var lr = Rect.Intersect(new Rect(r.X - snap.OriginX, r.Y - snap.OriginY, r.Width, r.Height), winRect);
                    if (!lr.IsEmpty && lr.Width > 1 && lr.Height > 1 && (list.Count == 0 || list[^1] != lr)) list.Add(lr);
                }
                if (list.Count == 0 || list[^1] != winRect) list.Add(winRect);
                levels = list;
                levelIdx = Math.Max(0, list.FindIndex(r => r.Width >= 24 && r.Height >= 24));
                candidate = levels[levelIdx];
                RefreshVisual();
            });
        });
    }

    // ------------------------------------------------------------------ keyboard
    void OnKeyDown(object s, KeyEventArgs e)
    {
        if (layer.IsEditing || Keyboard.FocusedElement is TextBox) return;   // let text boxes (size / hex / text tool) receive typing
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        bool ctrl = mods.HasFlag(ModifierKeys.Control), shift = mods.HasFlag(ModifierKeys.Shift);
        e.Handled = true;

        if (key == Key.Escape) { CloseOverlay(); return; }
        if (key is Key.LeftShift or Key.RightShift)
        {
            if (!e.IsRepeat && phase != Phase.Selected) { colorFmt = (ColorFormat)(((int)colorFmt + 1) % 4); cfg.MagnifierColorFormat = colorFmt.ToString(); UpdateMagnifier(curPos); }
            e.Handled = false; return;
        }
        if (ctrl && key == Key.Z) { layer.Undo(); return; }
        if (ctrl && key == Key.Y) { layer.Redo(); return; }
        if (key == Key.Oem3 && !ctrl) { ToggleCursor(); return; }

        if (phase == Phase.Selected)
        {
            if (ctrl && key == Key.C) { CopySelection(true); return; }
            if (ctrl && key == Key.S) { SaveSelection(shift); return; }
            if (ctrl && key == Key.T) { PinSelection(); return; }
            if (key == Key.Enter) { CopySelection(true); return; }
            if (key == Key.Delete || key == Key.Back) { layer.DeleteSelected(); return; }
            if (shift && key == Key.C) { _ = RunOcr(true); return; }
            if (shift && key == Key.Q) { ShowQr(); return; }
            if (shift && key == Key.S) { StartScroll(); return; }
            if (shift && key == Key.R) { StartRecording(); return; }
            if (shift)
            {
                AnnotTool? t = key switch
                {
                    Key.D1 => AnnotTool.Rectangle, Key.D2 => AnnotTool.Ellipse, Key.D3 => AnnotTool.Line, Key.D4 => AnnotTool.Arrow,
                    Key.D5 => AnnotTool.Pencil, Key.D6 => AnnotTool.Highlighter, Key.D7 => AnnotTool.Serial,
                    Key.D8 => AnnotTool.Text, Key.E => AnnotTool.Eraser, _ => null
                };
                if (t != null) { SetTool(tool == t ? AnnotTool.None : t.Value); return; }
            }
            int dx = key == Key.Left ? -1 : key == Key.Right ? 1 : 0, dy = key == Key.Up ? -1 : key == Key.Down ? 1 : 0;
            if (dx != 0 || dy != 0)
            {
                double l = sel.Left, t = sel.Top, r = sel.Right, b = sel.Bottom;
                if (shift) { l += dx > 0 ? 1 : 0; r += dx < 0 ? -1 : 0; t += dy > 0 ? 1 : 0; b += dy < 0 ? -1 : 0; }     // shrink the side you push toward
                else if (ctrl) { l += dx < 0 ? -1 : 0; r += dx > 0 ? 1 : 0; t += dy < 0 ? -1 : 0; b += dy > 0 ? 1 : 0; }  // grow
                else { l += dx; r += dx; t += dy; b += dy; }
                var nr = Rect.Intersect(new Rect(new Point(l, t), new Point(r, b)), bounds);
                if (nr.Width >= 2 && nr.Height >= 2) SetSelection(nr);
                return;
            }
        }
        else
        {
            if (key == Key.C && !ctrl && !shift) { CopyColor(); return; }
            int dx = key == Key.Left ? -1 : key == Key.Right ? 1 : 0, dy = key == Key.Up ? -1 : key == Key.Down ? 1 : 0;
            if (dx != 0 || dy != 0)
            {
                NativeMethods.GetCursorPos(out var cp); NativeMethods.SetCursorPos(cp.X + dx, cp.Y + dy); return;
            }
            if (key == Key.Enter && candidate is Rect c) { phase = Phase.Selected; SetSelection(c); CopySelection(true); return; }
        }
        e.Handled = false;
    }

    void CopyColor()
    {
        var c = snap.PixelAt((int)curPos.X, (int)curPos.Y);
        try { Clipboard.SetText(ColorText.FormatPlain(c, colorFmt)); ToastWindow.Show("Color copied: " + ColorText.FormatPlain(c, colorFmt), Icons.Eyedropper); } catch { }
    }

    void ToggleCursor()
    {
        if (snap.CursorPatch == null) return;
        showCursor = !showCursor; cursorImg.Visibility = showCursor ? Visibility.Visible : Visibility.Collapsed;
    }

    // ------------------------------------------------------------------ QR
    void ScheduleQr() { qrTimer.Stop(); if (phase == Phase.Selected || phase == Phase.Selecting) qrTimer.Start(); }

    void DetectQr()
    {
        if (phase != Phase.Selected || sel.Width < 20) return;
        int token = ++qrToken; var crop = IntCrop();
        var bmp = Imaging.Crop(snap.Image, crop);
        Task.Run(() => QrService.Scan(bmp)).ContinueWith(t =>
        {
            if (t.IsFaulted) return;
            Dispatcher.BeginInvoke(() =>
            {
                if (token != qrToken || closing) return;
                qrHits = t.Result;
                qrButton.Background = qrHits.Count > 0 ? (Brush)FindResource("AccentSubtleBrush") : Brushes.Transparent;
                RefreshVisual();
            });
        });
    }

    void ShowQr()
    {
        if (qrHits.Count == 0) { ToastWindow.Show("No QR code found in the selection", Icons.Info); return; }
        string text = string.Join(Environment.NewLine, qrHits.Select(h => h.Text));
        CloseOverlay();
        new ResultWindow("QR code", Icons.Qr, text, true).Show();
    }

    // ------------------------------------------------------------------ outputs
    Int32Rect IntCrop()
    {
        int x = (int)Math.Floor(sel.X), y = (int)Math.Floor(sel.Y);
        int w = Math.Max(1, (int)Math.Round(sel.Right) - x), h = Math.Max(1, (int)Math.Round(sel.Bottom) - y);
        w = Math.Min(w, snap.PixelWidth - x); h = Math.Min(h, snap.PixelHeight - y);
        return new Int32Rect(x, y, Math.Max(1, w), Math.Max(1, h));
    }

    System.Windows.Media.Imaging.BitmapSource Render(bool styled, out int pad)
    {
        layer.CommitText();
        var crop = IntCrop(); pad = 0;
        var img = Imaging.Compose(snap, crop, layer.Items, layer.CanvasRect, showCursor ? snap.CursorPatch : null, snap.CursorPatchPos);
        return styled ? Imaging.ApplyOutputStyle(img, out pad) : img;
    }

    CaptureContext Context() { var c = IntCrop(); return snap.ContextAt(new Point(sel.X + sel.Width / 2, sel.Y + sel.Height / 2), c.Width, c.Height); }

    void CopySelection(bool close)
    {
        var img = Render(true, out _); var ctx = Context();
        CloseOverlay(); CaptureActions.Copy(img, ctx);
    }

    void SaveSelection(bool direct)
    {
        var img = Render(true, out _); var ctx = Context();
        if (direct) { CloseOverlay(); CaptureActions.QuickSave(img, ctx); return; }
        if (CaptureActions.Save(img, ctx, this)) CloseOverlay();
    }

    void PinSelection()
    {
        var img = Render(true, out int pad); var ctx = Context(); var c = IntCrop();
        double sx = snap.OriginX + c.X - pad, sy = snap.OriginY + c.Y - pad;
        CloseOverlay(); CaptureActions.Pin(img, new Rect(sx, sy, img.PixelWidth, img.PixelHeight), ctx);
    }

    async Task RunOcr(bool copyDirect)
    {
        var plain = Imaging.Crop(snap.Image, IntCrop());
        CloseOverlay();
        await CaptureActions.Ocr(plain, copyDirect);
    }

    void StartScroll()
    {
        var c = IntCrop();
        var rect = new Rect(snap.OriginX + c.X, snap.OriginY + c.Y, c.Width, c.Height);
        var ctx = Context();
        CloseOverlay();
        ScrollCaptureSession.Begin(rect, ctx);
    }

    void StartRecording()
    {
        var c = IntCrop();
        var rect = new Rect(snap.OriginX + c.X, snap.OriginY + c.Y, c.Width, c.Height);
        var ctx = Context();
        CloseOverlay();
        RecordingSession.Begin(rect, ctx);
    }

    void CloseOverlay()
    {
        if (closing) return;
        closing = true; layer.CommitText();
        try { Close(); } catch { }
    }
}
