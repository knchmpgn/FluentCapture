using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media;
using FluentSnip.Native;

namespace FluentSnip.Core;

public enum Backdrop { None = 1, Mica = 2, Acrylic = 3, MicaAlt = 4 }

/// <summary>Win32 / DWM helpers shared by all windows.</summary>
public static class WinHelper
{
    static int Build => Environment.OSVersion.Version.Build;
    public static bool IsWin11 => Build >= 22000;
    public static bool HasBackdrop => Build >= 22621;

    public static IntPtr Handle(Window w) => new WindowInteropHelper(w).EnsureHandle();
    public static double Scale(Window w) { try { return VisualTreeHelper.GetDpi(w).DpiScaleX; } catch { return 1; } }

    /// <summary>Place a window using raw device pixels (no DIP conversion).</summary>
    public static void SetPixelRect(Window w, double x, double y, double cx, double cy, bool topmost = false, bool activate = false)
    {
        uint flags = SWP_(activate);
        NativeMethods.SetWindowPos(Handle(w), topmost ? NativeMethods.HWND_TOPMOST : IntPtr.Zero,
            (int)Math.Round(x), (int)Math.Round(y), Math.Max(1, (int)Math.Round(cx)), Math.Max(1, (int)Math.Round(cy)),
            flags | (topmost ? 0u : NativeMethods.SWP_NOZORDER));
    }

    static uint SWP_(bool activate) => NativeMethods.SWP_SHOWWINDOW | (activate ? 0u : NativeMethods.SWP_NOACTIVATE);

    public static Rect GetPixelRect(Window w)
    {
        NativeMethods.GetWindowRect(Handle(w), out var r);
        return new Rect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
    }

    /// <summary>Stops WPF from resizing the window when it crosses monitors with different DPI.</summary>
    public static void SwallowDpiChanges(Window w)
    {
        var src = HwndSource.FromHwnd(Handle(w));
        src?.AddHook((IntPtr h, int msg, IntPtr wp, IntPtr lp, ref bool handled) =>
        {
            if (msg == NativeMethods.WM_DPICHANGED) handled = true;
            return IntPtr.Zero;
        });
    }

    /// <summary>Dark title bar, rounded corners and Mica/Acrylic backdrop (Windows 11), solid fallback otherwise.</summary>
    public static void ApplyChrome(Window w, Backdrop kind = Backdrop.Mica, bool roundCorners = true)
    {
        var h = Handle(w);
        int dark = ThemeManager.IsDark ? 1 : 0;
        NativeMethods.DwmSetWindowAttribute(h, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, 4);
        if (IsWin11 && roundCorners)
        {
            int pref = 2; // DWMWCP_ROUND
            NativeMethods.DwmSetWindowAttribute(h, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, 4);
        }
        if (HasBackdrop && kind != Backdrop.None)
        {
            int t = (int)kind;
            NativeMethods.DwmSetWindowAttribute(h, NativeMethods.DWMWA_SYSTEMBACKDROP_TYPE, ref t, 4);
            var src = HwndSource.FromHwnd(h);
            if (src?.CompositionTarget != null) src.CompositionTarget.BackgroundColor = Colors.Transparent;
            var m = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            NativeMethods.DwmExtendFrameIntoClientArea(h, ref m);
            w.Background = Brushes.Transparent;
        }
        else
        {
            w.SetResourceReference(Window.BackgroundProperty, "SurfaceSolidBrush");
        }
    }

    public static void SetDarkTitle(Window w)
    {
        int dark = ThemeManager.IsDark ? 1 : 0;
        NativeMethods.DwmSetWindowAttribute(Handle(w), NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, 4);
    }

    /// <summary>1px system shadow for borderless windows.</summary>
    public static void ExtendShadow(Window w)
    {
        var m = new MARGINS { Left = 1, Right = 1, Top = 1, Bottom = 1 };
        NativeMethods.DwmExtendFrameIntoClientArea(Handle(w), ref m);
    }

    public static void SetBorderColor(Window w, Color c)
    {
        if (!IsWin11) return;
        int cr = c.R | (c.G << 8) | (c.B << 16);
        NativeMethods.DwmSetWindowAttribute(Handle(w), NativeMethods.DWMWA_BORDER_COLOR, ref cr, 4);
    }

    public static void SetExStyle(Window w, int style, bool on)
    {
        var h = Handle(w);
        int ex = NativeMethods.GetWindowLongW(h, NativeMethods.GWL_EXSTYLE);
        ex = on ? ex | style : ex & ~style;
        NativeMethods.SetWindowLongW(h, NativeMethods.GWL_EXSTYLE, ex);
    }

    public static void ToolWindow(Window w, bool noActivate, bool clickThrough = false)
    {
        SetExStyle(w, NativeMethods.WS_EX_TOOLWINDOW, true);
        if (noActivate) SetExStyle(w, NativeMethods.WS_EX_NOACTIVATE, true);
        if (clickThrough) SetExStyle(w, NativeMethods.WS_EX_TRANSPARENT, true);
    }

    public static void SetOpacity(Window w, double opacity01)
    {
        var h = Handle(w);
        SetExStyle(w, NativeMethods.WS_EX_LAYERED, true);
        NativeMethods.SetLayeredWindowAttributes(h, 0, (byte)Math.Clamp((int)Math.Round(opacity01 * 255), 20, 255), 2);
    }

    public static void ExcludeFromCapture(Window w) =>
        NativeMethods.SetWindowDisplayAffinity(Handle(w), NativeMethods.WDA_EXCLUDEFROMCAPTURE);

    public static void Foreground(Window w) { w.Activate(); NativeMethods.SetForegroundWindow(Handle(w)); }

    public static POINT CursorPos() { NativeMethods.GetCursorPos(out var p); return p; }

    public static (Rect work, Rect full, double scale) MonitorAt(double x, double y)
    {
        var pt = new POINT { X = (int)x, Y = (int)y };
        var hm = NativeMethods.MonitorFromPoint(pt, 2);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        NativeMethods.GetMonitorInfoW(hm, ref mi);
        NativeMethods.GetDpiForMonitor(hm, 0, out uint dx, out _);
        double scale = dx == 0 ? 1 : dx / 96.0;
        return (new Rect(mi.rcWork.Left, mi.rcWork.Top, mi.rcWork.Right - mi.rcWork.Left, mi.rcWork.Bottom - mi.rcWork.Top),
                new Rect(mi.rcMonitor.Left, mi.rcMonitor.Top, mi.rcMonitor.Right - mi.rcMonitor.Left, mi.rcMonitor.Bottom - mi.rcMonitor.Top),
                scale);
    }
}
