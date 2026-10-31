using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FluentSnip.Annotations;
using FluentSnip.Native;

namespace FluentSnip.Capture;

public sealed class MonitorInfoEx { public Rect Bounds; public double Scale = 1; }   // Bounds in snapshot coordinates
public sealed class WindowInfo { public IntPtr Hwnd; public Rect Bounds; public uint Pid; public string Title = ""; }

/// <summary>Frozen copy of the whole virtual desktop + window list taken right before the overlay opens.</summary>
public sealed class ScreenSnapshot : IPixelSource
{
    public int OriginX, OriginY;
    public int PixelWidth { get; private set; }
    public int PixelHeight { get; private set; }
    public int Stride { get; private set; }
    public byte[] Pixels { get; private set; }
    public BitmapSource Image { get; private set; }
    public List<WindowInfo> Windows { get; } = new();
    public List<MonitorInfoEx> Monitors { get; } = new();
    public BitmapSource CursorPatch;
    public Point CursorPatchPos;
    public IntPtr ForegroundHwnd;

    public static ScreenSnapshot Capture(IEnumerable<IntPtr> ignore = null)
    {
        var s = new ScreenSnapshot
        {
            OriginX = NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN),
            OriginY = NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN),
            PixelWidth = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN),
            PixelHeight = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN)
        };
        s.Stride = s.PixelWidth * 4;
        s.ForegroundHwnd = NativeMethods.GetForegroundWindow();

        IntPtr sdc = NativeMethods.GetDC(IntPtr.Zero), mdc = NativeMethods.CreateCompatibleDC(sdc);
        var bmi = NativeMethods.MakeBmi(s.PixelWidth, s.PixelHeight);
        IntPtr hbm = NativeMethods.CreateDIBSection(sdc, ref bmi, 0, out IntPtr bits, IntPtr.Zero, 0);
        IntPtr old = NativeMethods.SelectObject(mdc, hbm);
        NativeMethods.BitBlt(mdc, 0, 0, s.PixelWidth, s.PixelHeight, sdc, s.OriginX, s.OriginY, NativeMethods.SRCCOPY | NativeMethods.CAPTUREBLT);
        s.Pixels = new byte[s.Stride * s.PixelHeight];
        Marshal.Copy(bits, s.Pixels, 0, s.Pixels.Length);
        for (int i = 3; i < s.Pixels.Length; i += 4) s.Pixels[i] = 255;
        NativeMethods.SelectObject(mdc, old); NativeMethods.DeleteObject(hbm); NativeMethods.DeleteDC(mdc); NativeMethods.ReleaseDC(IntPtr.Zero, sdc);

        var img = BitmapSource.Create(s.PixelWidth, s.PixelHeight, 96, 96, PixelFormats.Bgr32, null, s.Pixels, s.Stride);
        img.Freeze(); s.Image = img;

        s.EnumMonitors();
        s.EnumWindowList(ignore?.ToHashSet() ?? new HashSet<IntPtr>());
        s.BuildCursorPatch();
        return s;
    }

    void EnumMonitors()
    {
        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hm, IntPtr dc, ref RECT r, IntPtr d) =>
        {
            NativeMethods.GetDpiForMonitor(hm, 0, out uint dx, out _);
            Monitors.Add(new MonitorInfoEx
            {
                Bounds = new Rect(r.Left - OriginX, r.Top - OriginY, r.Right - r.Left, r.Bottom - r.Top),
                Scale = dx == 0 ? 1 : dx / 96.0
            });
            return true;
        }, IntPtr.Zero);
        if (Monitors.Count == 0) Monitors.Add(new MonitorInfoEx { Bounds = new Rect(0, 0, PixelWidth, PixelHeight) });
    }

    void EnumWindowList(HashSet<IntPtr> ignore)
    {
        uint self = (uint)Environment.ProcessId;
        NativeMethods.EnumWindows((h, l) =>
        {
            if (ignore.Contains(h) || !NativeMethods.IsWindowVisible(h) || NativeMethods.IsIconic(h)) return true;
            if (NativeMethods.DwmGetWindowAttribute(h, NativeMethods.DWMWA_CLOAKED, out int cloaked, 4) == 0 && cloaked != 0) return true;
            string cls = NativeMethods.ClassName(h);
            if (cls is "Progman" or "WorkerW") return true;
            RECT r;
            if (NativeMethods.DwmGetWindowAttribute(h, NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS, out r, Marshal.SizeOf<RECT>()) != 0)
                NativeMethods.GetWindowRect(h, out r);
            if (r.Right - r.Left < 2 || r.Bottom - r.Top < 2) return true;
            NativeMethods.GetWindowThreadProcessId(h, out uint pid);
            Windows.Add(new WindowInfo
            {
                Hwnd = h, Pid = pid, Title = NativeMethods.WindowText(h),
                Bounds = new Rect(r.Left - OriginX, r.Top - OriginY, r.Right - r.Left, r.Bottom - r.Top)
            });
            return true;
        }, IntPtr.Zero);
    }

    void BuildCursorPatch()
    {
        try
        {
            var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
            if (!NativeMethods.GetCursorInfo(ref ci) || (ci.flags & 1) == 0) return;
            if (!NativeMethods.GetIconInfo(ci.hCursor, out var ii)) return;
            if (ii.hbmColor != IntPtr.Zero) NativeMethods.DeleteObject(ii.hbmColor);
            if (ii.hbmMask != IntPtr.Zero) NativeMethods.DeleteObject(ii.hbmMask);
            const int N = 64;
            int px = ci.pt.X - OriginX - ii.xHotspot, py = ci.pt.Y - OriginY - ii.yHotspot;
            if (px < 0 || py < 0 || px + N > PixelWidth || py + N > PixelHeight) return;

            IntPtr sdc = NativeMethods.GetDC(IntPtr.Zero), mdc = NativeMethods.CreateCompatibleDC(sdc);
            var bmi = NativeMethods.MakeBmi(N, N);
            IntPtr hbm = NativeMethods.CreateDIBSection(sdc, ref bmi, 0, out IntPtr bits, IntPtr.Zero, 0);
            IntPtr old = NativeMethods.SelectObject(mdc, hbm);
            var patch = new byte[N * N * 4];
            for (int y = 0; y < N; y++) Buffer.BlockCopy(Pixels, (py + y) * Stride + px * 4, patch, y * N * 4, N * 4);
            Marshal.Copy(patch, 0, bits, patch.Length);
            NativeMethods.DrawIconEx(mdc, 0, 0, ci.hCursor, 0, 0, 0, IntPtr.Zero, NativeMethods.DI_NORMAL);
            Marshal.Copy(bits, patch, 0, patch.Length);
            for (int i = 3; i < patch.Length; i += 4) patch[i] = 255;
            NativeMethods.SelectObject(mdc, old); NativeMethods.DeleteObject(hbm); NativeMethods.DeleteDC(mdc); NativeMethods.ReleaseDC(IntPtr.Zero, sdc);
            var bs = BitmapSource.Create(N, N, 96, 96, PixelFormats.Bgr32, null, patch, N * 4); bs.Freeze();
            CursorPatch = bs; CursorPatchPos = new Point(px, py);
        }
        catch { }
    }

    public Color PixelAt(int x, int y)
    {
        x = Math.Clamp(x, 0, PixelWidth - 1); y = Math.Clamp(y, 0, PixelHeight - 1);
        int i = y * Stride + x * 4;
        return Color.FromRgb(Pixels[i + 2], Pixels[i + 1], Pixels[i]);
    }

    public WindowInfo WindowAt(Point p) => Windows.FirstOrDefault(w => w.Bounds.Contains(p));

    public MonitorInfoEx MonitorAt(Point p) =>
        Monitors.FirstOrDefault(m => m.Bounds.Contains(p)) ?? Monitors.OrderBy(m => Dist(m.Bounds, p)).First();

    static double Dist(Rect r, Point p)
    {
        double dx = Math.Max(Math.Max(r.Left - p.X, 0), p.X - r.Right), dy = Math.Max(Math.Max(r.Top - p.Y, 0), p.Y - r.Bottom);
        return dx * dx + dy * dy;
    }

    /// <summary>Capture context (window title / process) for the window at the given point.</summary>
    public Core.CaptureContext ContextAt(Point p, int w, int h)
    {
        var win = WindowAt(p) ?? Windows.FirstOrDefault(x => x.Hwnd == ForegroundHwnd);
        return new Core.CaptureContext { Title = win?.Title ?? "", Process = win != null ? NativeMethods.ProcessName(win.Pid) : "", Width = w, Height = h };
    }
}
