using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FluentSnip.Core;
using FluentSnip.Native;
using FluentSnip.Views;

namespace FluentSnip.Services;

/// <summary>Owns all pins, pin groups, clipboard pinning and batch operations.</summary>
public static class PinManager
{
    static readonly List<PinWindow> pins = new();
    static bool hidden;
    static AppSettings Cfg => SettingsService.Current;

    public static int VisibleCount => pins.Count(p => p.IsVisible);
    public static int Count => pins.Count;

    public static PinWindow PinImage(BitmapSource img, Rect screenPx, CaptureContext ctx, PinKind kind = PinKind.Image, string payload = null)
    {
        var w = new PinWindow(img, screenPx, kind, payload, ctx);
        pins.Add(w);
        if (hidden) { hidden = false; foreach (var p in pins) p.Show(); }
        w.Show();
        return w;
    }

    public static void Remove(PinWindow w) => pins.Remove(w);

    // ---------------- clipboard ----------------
    public static void PinClipboard()
    {
        try
        {
            var img = Imaging.FromClipboardImage();
            if (img != null) { PinImage(img, Centered(img.PixelWidth, img.PixelHeight), null); return; }

            if (Clipboard.ContainsFileDropList())
            {
                var files = Clipboard.GetFileDropList();
                if (files.Count > 0)
                {
                    string f = files[0]; string ext = System.IO.Path.GetExtension(f).ToLowerInvariant();
                    if (ext is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".tif" or ".tiff" or ".webp" or ".ico")
                    {
                        var bi = Imaging.LoadFile(f); PinImage(bi, Centered(bi.PixelWidth, bi.PixelHeight), null, PinKind.File, f);
                    }
                    else { var card = RenderFileCard(f); PinImage(card, Centered(card.PixelWidth, card.PixelHeight), null, PinKind.File, f); }
                    return;
                }
            }

            if (Clipboard.ContainsText())
            {
                string t = Clipboard.GetText();
                if (string.IsNullOrWhiteSpace(t)) { ToastWindow.Show("Clipboard is empty", Icons.Info); return; }
                if (TryParseColor(t.Trim(), out var c))
                {
                    var sw = RenderColor(c, t.Trim()); PinImage(sw, Centered(sw.PixelWidth, sw.PixelHeight), null, PinKind.Color, Ui.ToHex(c));
                }
                else { var tx = RenderText(t); PinImage(tx, Centered(tx.PixelWidth, tx.PixelHeight), null, PinKind.Text, t); }
                return;
            }
            ToastWindow.Show("Nothing pinnable on the clipboard", Icons.Info);
        }
        catch (Exception ex) { SettingsService.Log("Pin clipboard failed: " + ex); ToastWindow.Show("Pin failed: " + ex.Message, Icons.Info); }
    }

    static Rect Centered(int w, int h)
    {
        var p = WinHelper.CursorPos();
        var (work, _, _) = WinHelper.MonitorAt(p.X, p.Y);
        double maxW = work.Width * 0.9, maxH = work.Height * 0.9;
        double k = Math.Min(1, Math.Min(maxW / w, maxH / h));
        double rw = w * k, rh = h * k;
        return new Rect(work.X + (work.Width - rw) / 2, work.Y + (work.Height - rh) / 2, rw, rh);
    }

    static bool TryParseColor(string s, out Color c)
    {
        c = default;
        if (Regex.IsMatch(s, @"^#?([0-9a-fA-F]{3}|[0-9a-fA-F]{6}|[0-9a-fA-F]{8})$"))
        {
            try { c = (Color)ColorConverter.ConvertFromString(s.StartsWith("#") ? s : "#" + s); return true; } catch { return false; }
        }
        var m = Regex.Match(s, @"^rgb\(\s*(\d{1,3})\s*,\s*(\d{1,3})\s*,\s*(\d{1,3})\s*\)$", RegexOptions.IgnoreCase);
        if (m.Success) { c = Color.FromRgb((byte)Math.Min(255, int.Parse(m.Groups[1].Value)), (byte)Math.Min(255, int.Parse(m.Groups[2].Value)), (byte)Math.Min(255, int.Parse(m.Groups[3].Value))); return true; }
        return false;
    }

    static BitmapSource Rtb(DrawingVisual dv, int w, int h)
    {
        var r = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32); r.Render(dv); r.Freeze(); return r;
    }

    static BitmapSource RenderColor(Color c, string label)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(c), null, new Rect(0, 0, 220, 150));
            double lum = 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;
            var ft = new FormattedText(Ui.ToHex(c), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI Semibold"), 22,
                lum > 150 ? Brushes.Black : Brushes.White, 1.0);
            dc.DrawText(ft, new Point(14, 150 - ft.Height - 12));
        }
        return Rtb(dv, 220, 150);
    }

    static BitmapSource RenderText(string text)
    {
        bool dark = ThemeManager.IsDark;
        var fg = dark ? Brushes.White : new SolidColorBrush(Color.FromRgb(0x1B, 0x1B, 0x1B));
        var bg = new SolidColorBrush(dark ? Color.FromRgb(0x2B, 0x2B, 0x2B) : Color.FromRgb(0xFA, 0xFA, 0xFA));
        if (text.Length > 6000) text = text[..6000] + "…";
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 18, fg, 1.0) { MaxTextWidth = 640, MaxTextHeight = 1400 };
        int w = (int)Math.Ceiling(ft.Width) + 36, h = (int)Math.Ceiling(ft.Height) + 32;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(bg, null, new Rect(0, 0, w, h));
            dc.DrawText(ft, new Point(18, 16));
        }
        return Rtb(dv, w, h);
    }

    static BitmapSource RenderFileCard(string path)
    {
        bool dark = ThemeManager.IsDark;
        var fg = dark ? Brushes.White : new SolidColorBrush(Color.FromRgb(0x1B, 0x1B, 0x1B));
        var sub = dark ? new SolidColorBrush(Color.FromRgb(0xB5, 0xB5, 0xB5)) : new SolidColorBrush(Color.FromRgb(0x60, 0x60, 0x60));
        var bg = new SolidColorBrush(dark ? Color.FromRgb(0x2B, 0x2B, 0x2B) : Color.FromRgb(0xFA, 0xFA, 0xFA));
        BitmapSource icon = null;
        try
        {
            var fi = new SHFILEINFO();
            NativeMethods.SHGetFileInfoW(path, 0, ref fi, (uint)Marshal.SizeOf<SHFILEINFO>(), 0x100 | 0x0); // SHGFI_ICON (large)
            if (fi.hIcon != IntPtr.Zero)
            {
                icon = Imaging2.FromHIcon(fi.hIcon); NativeMethods.DestroyIcon(fi.hIcon);
            }
        }
        catch { }
        string size = "";
        try { size = new FileInfo(path).Exists ? FormatSize(new FileInfo(path).Length) : "Folder"; } catch { }
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(bg, null, new Rect(0, 0, 460, 110));
            if (icon != null) dc.DrawImage(icon, new Rect(18, 27, 56, 56));
            var name = new FormattedText(System.IO.Path.GetFileName(path), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI Semibold"), 18, fg, 1.0) { MaxTextWidth = 350, MaxTextHeight = 26, Trimming = TextTrimming.CharacterEllipsis };
            var det = new FormattedText(size + "\n" + path, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 12, sub, 1.0) { MaxTextWidth = 350, MaxTextHeight = 48, Trimming = TextTrimming.CharacterEllipsis };
            dc.DrawText(name, new Point(90, 18)); dc.DrawText(det, new Point(90, 48));
        }
        return Rtb(dv, 460, 110);
    }

    static string FormatSize(long b) => b < 1024 ? b + " B" : b < 1048576 ? (b / 1024.0).ToString("0.#") + " KB" : b < 1073741824 ? (b / 1048576.0).ToString("0.#") + " MB" : (b / 1073741824.0).ToString("0.##") + " GB";

    // ---------------- batch ----------------
    static IEnumerable<PinWindow> InGroup => pins;

    public static void ToggleHideAll()
    {
        hidden = !hidden;
        foreach (var p in InGroup) { if (hidden) p.Hide(); else p.Show(); }
    }

    public static void CloseAll() { foreach (var p in InGroup) p.Close(); }
    public static void ThumbnailsAll(bool on) { foreach (var p in InGroup) p.ToggleThumbnail(on); }

    public static void EnsureVisible()
    {
        foreach (var p in InGroup)
        {
            var r = WinHelper.GetPixelRect(p);
            var (work, _, _) = WinHelper.MonitorAt(r.X + r.Width / 2, r.Y + r.Height / 2);
            double x = Math.Min(Math.Max(work.Left, r.X), Math.Max(work.Left, work.Right - r.Width));
            double y = Math.Min(Math.Max(work.Top, r.Y), Math.Max(work.Top, work.Bottom - r.Height));
            if (x != r.X || y != r.Y) WinHelper.SetPixelRect(p, x, y, r.Width, r.Height, true);
        }
    }

    public static void SaveAll()
    {
        var items = InGroup.ToList();
        if (items.Count == 0) { ToastWindow.Show("No pins to save", Icons.Info); return; }
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Save all pins to folder" };
        if (dlg.ShowDialog() != true) return;
        int n = 0;
        foreach (var p in items)
        {
            try
            {
                string path = System.IO.Path.Combine(dlg.FolderName, $"Pin_{DateTime.Now:yyyyMMdd_HHmmss}_{++n}.png");
                SaveService.WriteImage(p.Current(), path, "png", 100);
            }
            catch { }
        }
        ToastWindow.Show($"Saved {n} pins", Icons.Save);
    }

    public static void TogglePenetrationUnderCursor()
    {
        var c = WinHelper.CursorPos();
        var hit = pins.Where(p => p.IsVisible).LastOrDefault(p => WinHelper.GetPixelRect(p).Contains(c.X, c.Y));
        hit?.TogglePenetration();
    }
}

static class Imaging2
{
    public static BitmapSource FromHIcon(IntPtr h)
    {
        var s = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(h, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        s.Freeze(); return s;
    }
}
