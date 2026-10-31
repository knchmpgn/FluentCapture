using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FluentSnip.Core;
using FluentSnip.Native;

namespace FluentSnip.Services;

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
struct NOTIFYICONDATA
{
    public int cbSize; public IntPtr hWnd; public uint uID, uFlags, uCallbackMessage; public IntPtr hIcon;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
    public uint dwState, dwStateMask;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
    public uint uTimeoutOrVersion;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
    public uint dwInfoFlags; public Guid guidItem; public IntPtr hBalloonIcon;
}

/// <summary>Draws the app icon at runtime (accent rounded square + Segoe Fluent camera glyph).</summary>
public static class IconFactory
{
    public static BitmapSource Render(int size)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            var accent = ThemeManager.Accent;
            var grad = new LinearGradientBrush(Color.FromRgb((byte)Math.Min(255, accent.R + 30), (byte)Math.Min(255, accent.G + 30), (byte)Math.Min(255, accent.B + 30)), accent, 90);
            dc.DrawRoundedRectangle(grad, null, new Rect(0, 0, size, size), size * 0.24, size * 0.24);
            var ft = new FormattedText("\uE722", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe Fluent Icons, Segoe MDL2 Assets"), size * 0.58, Brushes.White, 1.0);
            dc.DrawText(ft, new Point((size - ft.Width) / 2, (size - ft.Height) / 2));
        }
        var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv); rtb.Freeze(); return rtb;
    }

    public static IntPtr CreateHIcon(int size)
    {
        var bmp = Render(size);
        var px = new byte[size * size * 4];
        bmp.CopyPixels(px, size * 4, 0);
        for (int i = 0; i < px.Length; i += 4)   // un-premultiply
        {
            int a = px[i + 3]; if (a == 0 || a == 255) continue;
            px[i] = (byte)Math.Min(255, px[i] * 255 / a); px[i + 1] = (byte)Math.Min(255, px[i + 1] * 255 / a); px[i + 2] = (byte)Math.Min(255, px[i + 2] * 255 / a);
        }
        IntPtr dc = NativeMethods.GetDC(IntPtr.Zero);
        var bmi = NativeMethods.MakeBmi(size, size);
        IntPtr color = NativeMethods.CreateDIBSection(dc, ref bmi, 0, out IntPtr bits, IntPtr.Zero, 0);
        Marshal.Copy(px, 0, bits, px.Length);
        NativeMethods.ReleaseDC(IntPtr.Zero, dc);
        IntPtr mask = NativeMethods.CreateBitmap(size, size, 1, 1, new byte[((size + 15) / 16) * 2 * size]);
        var ii = new ICONINFO { fIcon = true, hbmColor = color, hbmMask = mask };
        IntPtr icon = NativeMethods.CreateIconIndirect(ref ii);
        NativeMethods.DeleteObject(color); NativeMethods.DeleteObject(mask);
        return icon;
    }
}

public sealed class TrayIconService : IDisposable
{
    const int WM_CALLBACK = 0x8001, WM_LBUTTONUP = 0x202, WM_RBUTTONUP = 0x205, WM_MBUTTONUP = 0x208;
    readonly HwndSource src;
    readonly uint taskbarCreated;
    IntPtr hIcon;
    bool visible;

    public event Action LeftClick, RightClick, MiddleClick;

    public TrayIconService()
    {
        src = new HwndSource(new HwndSourceParameters("FluentSnip.Tray") { ParentWindow = new IntPtr(-3), WindowStyle = 0, Width = 0, Height = 0 });
        src.AddHook(Hook);
        taskbarCreated = NativeMethods.RegisterWindowMessageW("TaskbarCreated");
        hIcon = IconFactory.CreateHIcon(32);
    }

    IntPtr Hook(IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled)
    {
        if (msg == WM_CALLBACK)
        {
            int m = (int)(l.ToInt64() & 0xFFFF);
            if (m == WM_LBUTTONUP) LeftClick?.Invoke();
            else if (m == WM_RBUTTONUP) RightClick?.Invoke();
            else if (m == WM_MBUTTONUP) MiddleClick?.Invoke();
            handled = true;
        }
        else if (msg == taskbarCreated && visible) { Notify(0); handled = true; }
        return IntPtr.Zero;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern bool Shell_NotifyIconW(uint msg, ref NOTIFYICONDATA d);

    void Notify(uint msg)
    {
        var d = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(), hWnd = src.Handle, uID = 1, uFlags = 1 | 2 | 4,
            uCallbackMessage = WM_CALLBACK, hIcon = hIcon, szTip = "FluentCapture", szInfo = "", szInfoTitle = ""
        };
        Shell_NotifyIconW(msg, ref d);
    }

    public void SetVisible(bool v)
    {
        if (v == visible) return;
        Notify(v ? 0u : 2u); visible = v;
    }

    public void Dispose() { SetVisible(false); if (hIcon != IntPtr.Zero) NativeMethods.DestroyIcon(hIcon); src.Dispose(); }
}
