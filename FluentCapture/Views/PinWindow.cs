using System.Diagnostics;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FluentSnip.Annotations;
using FluentSnip.Core;
using FluentSnip.Native;
using FluentSnip.Services;

namespace FluentSnip.Views;

public enum PinKind { Image, Text, Color, File }

/// <summary>Compact floating annotation toolbar for a pin (separate owned window, follows the pin).</summary>
public sealed class PinToolbarWindow : Window
{
    public PinToolbarWindow(Window owner, AnnotationLayer layer)
    {
        Owner = owner; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false; Topmost = true;
        ShowActivated = false; SizeToContent = SizeToContent.WidthAndHeight; WindowStartupLocation = WindowStartupLocation.Manual;
        Left = -20000; Top = -20000;
        SourceInitialized += (_, _) => { WinHelper.ApplyChrome(this, Backdrop.Acrylic); WinHelper.ToolWindow(this, true); };

        var tools = new Dictionary<AnnotTool, ToggleButton>();
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6) };
        void T(AnnotTool t, object c, string tip)
        {
            var b = Ui.IconToggle(c, tip, on =>
            {
                layer.CommitText(); layer.Tool = on ? t : AnnotTool.None; layer.Selected = null; layer.InvalidateVisual();
                foreach (var kv in tools) kv.Value.IsChecked = kv.Key == layer.Tool;
            });
            tools[t] = b; row.Children.Add(b);
        }
        T(AnnotTool.Rectangle, Icons.Rectangle, "Rectangle"); T(AnnotTool.Ellipse, Icons.Ellipse, "Ellipse");
        T(AnnotTool.Line, Ui.Glyph(Icons.Line, 16, -45), "Line"); T(AnnotTool.Arrow, Ui.Glyph(Icons.Arrow, 16, -45), "Arrow");
        T(AnnotTool.Pencil, Icons.Pen, "Pencil"); T(AnnotTool.Highlighter, Icons.Highlighter, "Highlighter");
        T(AnnotTool.Text, Icons.Text, "Text"); T(AnnotTool.Eraser, Icons.Eraser, "Eraser");
        row.Children.Add(Ui.VSeparator());
        foreach (var hex in new[] { "#E81123", "#FFB900", "#16C60C", "#0078D4", "#000000", "#FFFFFF" })
        {
            var c = Ui.ParseColor(hex, Colors.Red);
            var sw = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(9), Margin = new Thickness(3, 0, 3, 0), Background = new SolidColorBrush(c), Cursor = Cursors.Hand, BorderThickness = new Thickness(1) };
            sw.SetResourceReference(Border.BorderBrushProperty, "StrokeBrush");
            sw.MouseLeftButtonDown += (_, _) => { layer.Color = c; layer.ApplyStyleToSelected(); };
            row.Children.Add(sw);
        }
        row.Children.Add(Ui.VSeparator());
        var size = new Slider { Minimum = 1, Maximum = 30, Value = layer.Size, Width = 80, VerticalAlignment = VerticalAlignment.Center, Focusable = false };
        size.ValueChanged += (_, e) => { layer.Size = e.NewValue; layer.ApplyStyleToSelected(); };
        row.Children.Add(size);
        row.Children.Add(Ui.IconButton(Icons.Undo, "Undo", () => layer.Undo()));
        row.Children.Add(Ui.IconButton(Icons.Redo, "Redo", () => layer.Redo()));
        Content = row;
    }

    public void PlaceBelow(Rect pinPx)
    {
        double s = WinHelper.Scale(this); double w = ActualWidth * s, h = ActualHeight * s;
        if (w <= 0) { UpdateLayout(); w = ActualWidth * s; h = ActualHeight * s; }
        var (work, _, _) = WinHelper.MonitorAt(pinPx.X + pinPx.Width / 2, pinPx.Bottom);
        double x = Math.Min(Math.Max(work.Left, pinPx.X), work.Right - w);
        double y = pinPx.Bottom + 8 * s; if (y + h > work.Bottom) y = pinPx.Top - h - 8 * s;
        WinHelper.SetPixelRect(this, x, y, w, h, true);
    }
}

/// <summary>One pinned item (image / text / color / file) floating on top of the desktop.</summary>
public sealed class PinWindow : Window
{
    public PinKind Kind { get; }
    public string Payload { get; }
    
    public bool IsLocked { get; private set; }
    public bool IsPenetrating { get; private set; }
    public bool IsThumbnail { get; private set; }

    BitmapSource image; ImagePixelSource pix; readonly CaptureContext ctx;
    readonly AnnotationLayer layer = new();
    readonly Grid inner = new();
    readonly Image imgEl = new();
    readonly TextBlock titleLabel = new();
    PinToolbarWindow toolbar;
    Rect rect;                       // current window rect in device px
    Rect savedRect;                  // rect before thumbnail mode
    double zoom = 1, opacity = 1;
    bool onTop = true, annotating, drawingNow, placed;

    public PinWindow(BitmapSource img, Rect screenPx, PinKind kind, string payload, CaptureContext ctx)
    {
        Kind = kind; Payload = payload; this.ctx = ctx;
        opacity = Math.Clamp(SettingsService.Current.PinDefaultOpacity / 100.0, 0.1, 1);
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false; Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual; Left = -20000; Top = -20000; Width = 100; Height = 100;
        AllowsTransparency = false; UseLayoutRounding = true; Title = "FluentCapture pin";
        rect = screenPx;

        imgEl.Stretch = Stretch.Fill; RenderOptions.SetBitmapScalingMode(imgEl, BitmapScalingMode.HighQuality);
        inner.Children.Add(imgEl); inner.Children.Add(layer);
        var vb = new Viewbox { Stretch = Stretch.Fill, Child = inner };
        titleLabel.FontSize = 12; titleLabel.Padding = new Thickness(8, 2, 8, 2); titleLabel.HorizontalAlignment = HorizontalAlignment.Left;
        titleLabel.VerticalAlignment = VerticalAlignment.Bottom; titleLabel.Background = new SolidColorBrush(Color.FromArgb(170, 0, 0, 0));
        titleLabel.Foreground = Brushes.White; titleLabel.Visibility = Visibility.Collapsed; titleLabel.IsHitTestVisible = false;
        Content = new Grid { Children = { vb, titleLabel } };
        SetImage(img);

        SourceInitialized += (_, _) =>
        {
            WinHelper.SwallowDpiChanges(this);
            WinHelper.ApplyChrome(this, Backdrop.None);
            WinHelper.ExtendShadow(this);
            WinHelper.ToolWindow(this, false);
            UpdateBorder();
        };
        Loaded += (_, _) => { Apply(); placed = true; if (opacity < 1) WinHelper.SetOpacity(this, opacity); };
        Activated += (_, _) => UpdateBorder();
        Deactivated += (_, _) => UpdateBorder();
        LocationChanged += (_, _) => { if (!placed) return; rect = WinHelper.GetPixelRect(this); toolbar?.PlaceBelow(rect); };
        MouseLeftButtonDown += OnLeftDown; MouseLeftButtonUp += OnLeftUp; MouseMove += OnMove;
        MouseWheel += OnWheel; MouseDown += OnOtherDown; PreviewKeyDown += OnKey;
        Closed += (_, _) => { toolbar?.Close(); PinManager.Remove(this); };
        BuildMenu();
    }

    // ---------- image / layout ----------
    void SetImage(BitmapSource img)
    {
        image = img; pix = new ImagePixelSource(img);
        imgEl.Source = img; inner.Width = img.PixelWidth; inner.Height = img.PixelHeight;
        imgEl.Width = img.PixelWidth; imgEl.Height = img.PixelHeight;
        layer.Source = pix; layer.CanvasRect = new Rect(0, 0, img.PixelWidth, img.PixelHeight);
        layer.Width = img.PixelWidth; layer.Height = img.PixelHeight;
        layer.Items.Clear(); layer.Selected = null; layer.InvalidateVisual();
    }

    void Apply()
    {
        WinHelper.SetPixelRect(this, rect.X, rect.Y, rect.Width, rect.Height, onTop);
        toolbar?.PlaceBelow(rect);
    }

    void Resize(double newZoom, Point? anchorScreenPx = null)
    {
        newZoom = Math.Clamp(newZoom, 0.05, 20);
        double w = image.PixelWidth * newZoom, h = image.PixelHeight * newZoom;
        double ax = anchorScreenPx?.X ?? rect.X + rect.Width / 2, ay = anchorScreenPx?.Y ?? rect.Y + rect.Height / 2;
        double fx = (ax - rect.X) / Math.Max(1, rect.Width), fy = (ay - rect.Y) / Math.Max(1, rect.Height);
        rect = new Rect(ax - fx * w, ay - fy * h, w, h); zoom = newZoom; Apply();
    }

    void UpdateBorder()
    {
        Color c;
        if (IsPenetrating) c = Color.FromRgb(0x16, 0xC6, 0x0C);
        else if (IsLocked) c = Color.FromRgb(0xF7, 0x63, 0x0C);
        else if (IsActive)
        {
            c = ThemeManager.Accent;
        }
        else c = Color.FromRgb(0x80, 0x80, 0x80);
        WinHelper.SetBorderColor(this, SettingsService.Current.PinShadowBorder ? c : Color.FromArgb(0, 0, 0, 0));
    }

    // ---------- mouse ----------
    Point LocalToImage(MouseEventArgs e) => e.GetPosition(inner);

    void OnLeftDown(object s, MouseButtonEventArgs e)
    {
        Focus();
        if (annotating && layer.PointerDown(LocalToImage(e), Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))) { drawingNow = true; CaptureMouse(); return; }
        if (e.ClickCount == 2) { Close(); return; }
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { StartDragOut(); return; }
        if (!IsLocked) { try { DragMove(); } catch { } rect = WinHelper.GetPixelRect(this); toolbar?.PlaceBelow(rect); }
    }

    void OnMove(object s, MouseEventArgs e) { if (drawingNow) layer.PointerMove(LocalToImage(e), Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)); }

    void OnLeftUp(object s, MouseButtonEventArgs e)
    {
        if (drawingNow) { drawingNow = false; ReleaseMouseCapture(); layer.PointerUp(LocalToImage(e)); }
    }

    void OnOtherDown(object s, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle && !IsLocked)
        {
            if (Math.Abs(zoom - 1) > 0.001 || opacity < 1) { savedZoom = zoom; savedOpacity = opacity; zoom = 1; opacity = 1; Resize(1); WinHelper.SetOpacity(this, 1); }
            else { opacity = savedOpacity; Resize(savedZoom); WinHelper.SetOpacity(this, opacity); }
        }
    }
    double savedZoom = 1, savedOpacity = 1;

    void OnWheel(object s, MouseWheelEventArgs e)
    {
        if (IsLocked) return;
        if (annotating && layer.Tool != AnnotTool.None) { layer.AdjustSize(e.Delta > 0 ? 1 : -1); return; }
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            opacity = Math.Clamp(opacity + (e.Delta > 0 ? 0.05 : -0.05), 0.1, 1); WinHelper.SetOpacity(this, opacity);
        }
        else
        {
            double step = 1 + SettingsService.Current.PinZoomStep / 100.0;
            WinHelper.CursorPos(); var p = WinHelper.CursorPos();
            Resize(zoom * (e.Delta > 0 ? step : 1 / step), new Point(p.X, p.Y));
            IsThumbnail = false;
        }
    }

    void StartDragOut()
    {
        try
        {
            var data = new DataObject();
            data.SetImage(Imaging.Flatten(Current(), Colors.White));
            string tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "FluentCapture_" + DateTime.Now.ToString("HHmmssfff") + ".png");
            File.WriteAllBytes(tmp, Imaging.EncodePng(Current()));
            data.SetFileDropList(new System.Collections.Specialized.StringCollection { tmp });
            if (Kind == PinKind.Text && Payload != null) data.SetText(Payload);
            DragDrop.DoDragDrop(this, data, DragDropEffects.Copy);
        }
        catch { }
    }

    // ---------- keyboard ----------
    void OnKey(object s, KeyEventArgs e)
    {
        if (layer.IsEditing) return;
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control), shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        e.Handled = true;
        switch (e.Key)
        {
            case Key.Escape: Close(); break;
            case Key.W when ctrl: Close(); break;
            case Key.D when ctrl: Close(); break;
            case Key.L: ToggleLock(); break;
            case Key.T when !ctrl: ToggleTopmost(); break;
            case Key.R when !ctrl: ToggleThumbnail(); break;
            case Key.F2: Rename(); break;
            case Key.Space: ToggleAnnotate(); break;
            case Key.C when ctrl: Imaging.CopyToClipboard(Current()); ToastWindow.Show("Pin copied"); break;
            case Key.S when ctrl: SaveAs(shift); break;
            case Key.Z when ctrl: layer.Undo(); break;
            case Key.Y when ctrl: layer.Redo(); break;
            case Key.Delete: layer.DeleteSelected(); break;
            case Key.Left or Key.Right or Key.Up or Key.Down when !IsLocked:
                int step = shift ? 10 : 1;
                rect = new Rect(rect.X + (e.Key == Key.Left ? -step : e.Key == Key.Right ? step : 0), rect.Y + (e.Key == Key.Up ? -step : e.Key == Key.Down ? step : 0), rect.Width, rect.Height);
                Apply(); break;
            default: e.Handled = false; break;
        }
    }

    // ---------- actions ----------
    public BitmapSource Current()
    {
        layer.CommitText();
        if (layer.Items.Count == 0) return image;
        return Imaging.Compose(pix, new Int32Rect(0, 0, image.PixelWidth, image.PixelHeight), layer.Items, layer.CanvasRect);
    }

    public void ToggleLock() { IsLocked = !IsLocked; UpdateBorder(); }
    public void ToggleTopmost() { onTop = !onTop; Topmost = onTop; Apply(); }

    public void TogglePenetration()
    {
        IsPenetrating = !IsPenetrating;
        WinHelper.SetExStyle(this, NativeMethods.WS_EX_TRANSPARENT, IsPenetrating);
        UpdateBorder();
        ToastWindow.Show(IsPenetrating ? "Mouse pass-through on" : "Mouse pass-through off", Icons.Pin);
    }

    public void ToggleThumbnail(bool? force = null)
    {
        bool want = force ?? !IsThumbnail;
        if (want == IsThumbnail) return;
        IsThumbnail = want;
        if (want)
        {
            savedRect = rect; double w = 200, h = Math.Max(40, 200.0 * image.PixelHeight / image.PixelWidth);
            rect = new Rect(rect.X, rect.Y, w, h);
        }
        else rect = savedRect;
        Apply();
    }

    void Rename()
    {
        var dlg = new InputDialog("Pin title", "Title shown at the bottom of this pin:", titleLabel.Text, false, null);
        if (dlg.ShowDialog() == true) { titleLabel.Text = dlg.Value; titleLabel.Visibility = dlg.Value.Length > 0 ? Visibility.Visible : Visibility.Collapsed; }
    }

    void ToggleAnnotate()
    {
        annotating = !annotating;
        if (annotating)
        {
            layer.Color = Ui.ParseColor(SettingsService.Current.AnnotColor, Colors.Red); layer.Size = SettingsService.Current.AnnotSize;
            toolbar = new PinToolbarWindow(this, layer); toolbar.Show(); toolbar.PlaceBelow(rect);
        }
        else { layer.CommitText(); layer.Tool = AnnotTool.None; layer.Selected = null; layer.InvalidateVisual(); toolbar?.Close(); toolbar = null; }
    }

    void SaveAs(bool quick)
    {
        var c = ctx ?? new CaptureContext { Width = image.PixelWidth, Height = image.PixelHeight };
        if (quick) CaptureActions.QuickSave(Current(), c); else CaptureActions.Save(Current(), c, this);
    }

    void Transform(Transform t)
    {
        var baked = Current();
        var tb = new TransformedBitmap(baked, t); tb.Freeze();
        double cx = rect.X + rect.Width / 2, cy = rect.Y + rect.Height / 2;
        SetImage(tb);
        double w = tb.PixelWidth * zoom, h = tb.PixelHeight * zoom;
        rect = new Rect(cx - w / 2, cy - h / 2, w, h); Apply();
    }

    // ---------- context menu ----------
    void BuildMenu()
    {
        var m = new ContextMenu();
        MenuItem Item(string text, string glyph, Action a, string gesture = null)
        {
            var mi = new MenuItem { Header = text, Icon = Ui.Glyph(glyph, 14), InputGestureText = gesture };
            mi.Click += (_, _) => a(); return mi;
        }
        m.Items.Add(Item("Copy", Icons.Copy, () => Imaging.CopyToClipboard(Current()), "Ctrl+C"));
        if (Kind == PinKind.Text && Payload != null) m.Items.Add(Item("Copy text", Icons.Text, () => Clipboard.SetText(Payload)));
        if (Kind == PinKind.Color && Payload != null) m.Items.Add(Item("Copy color code", Icons.Color, () => Clipboard.SetText(Payload)));
        if (Kind == PinKind.File && Payload != null) m.Items.Add(Item("Show in folder", Icons.Folder, () => CaptureActions.ShowInFolder(Payload)));
        m.Items.Add(Item("Save as…", Icons.Save, () => SaveAs(false), "Ctrl+S"));
        m.Items.Add(Item("Quick save", Icons.Save, () => SaveAs(true), "Ctrl+Shift+S"));
        m.Items.Add(new Separator());
        m.Items.Add(Item("Annotate", Icons.Edit, ToggleAnnotate, "Space"));
        m.Items.Add(Item("Recognize text (OCR)", Icons.Ocr, () => _ = CaptureActions.Ocr(Current(), false)));
        m.Items.Add(Item("Scan QR code", Icons.Qr, () => CaptureActions.Qr(Current())));
        m.Items.Add(new Separator());
        m.Items.Add(Item("Lock / unlock", Icons.Lock, ToggleLock, "L"));
        m.Items.Add(Item("Always on top", Icons.Pin, ToggleTopmost, "T"));
        m.Items.Add(Item("Thumbnail", Icons.Picture, () => ToggleThumbnail(), "R"));
        m.Items.Add(Item("Mouse pass-through", Icons.Eye, TogglePenetration));
        m.Items.Add(Item("Set title…", Icons.Edit, Rename, "F2"));
        var tr = new MenuItem { Header = "Transform", Icon = Ui.Glyph(Icons.Rotate, 14) };
        void Sub(string t, Transform tf) { var mi = new MenuItem { Header = t }; mi.Click += (_, _) => Transform(tf); tr.Items.Add(mi); }
        Sub("Rotate 90° clockwise", new RotateTransform(90)); Sub("Rotate 90° counter-clockwise", new RotateTransform(-90));
        Sub("Flip horizontally", new ScaleTransform(-1, 1)); Sub("Flip vertically", new ScaleTransform(1, -1));
        m.Items.Add(tr);
        m.Items.Add(Item("Reset size && opacity", Icons.Back, () => { zoom = 1; opacity = 1; Resize(1); WinHelper.SetOpacity(this, 1); }));
        m.Items.Add(new Separator());
        m.Items.Add(Item("Close", Icons.Close, Close, "Esc"));
        ContextMenu = m;
    }
}
