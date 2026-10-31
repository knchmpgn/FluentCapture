using System.Windows.Media;
using Microsoft.Win32;

namespace FluentSnip.Core;

/// <summary>Owns the themed brushes (Light / Dark / System) that the XAML styles and code-built UI bind to.</summary>
public static class ThemeManager
{
    public static bool IsDark { get; private set; } = true;
    public static Color Accent { get; private set; } = Color.FromRgb(0x00, 0x78, 0xD4);
    public static event Action Changed;

    public static bool SystemUsesDark()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return true; }
    }

    public static void Apply(string mode)
    {
        bool dark = mode == "Dark" || (mode != "Light" && SystemUsesDark());
        IsDark = dark;
        var a = SystemParameters.WindowGlassColor;
        Accent = Color.FromRgb(a.R, a.G, a.B);
        var res = Application.Current.Resources;

        void Set(string key, Color c) { var b = new SolidColorBrush(c); b.Freeze(); res[key] = b; }
        static Color Rgba(byte a, byte r, byte g, byte b) => Color.FromArgb(a, r, g, b);

        if (dark)
        {
            Set("SurfaceBrush", Rgba(0xEE, 0x2B, 0x2B, 0x2B));
            Set("SurfaceSolidBrush", Rgba(0xFF, 0x20, 0x20, 0x20));
            Set("CardBrush", Rgba(0x0D, 0xFF, 0xFF, 0xFF));
            Set("StrokeBrush", Rgba(0x33, 0xFF, 0xFF, 0xFF));
            Set("TextBrush", Rgba(0xFF, 0xFF, 0xFF, 0xFF));
            Set("TextSecondaryBrush", Rgba(0xC8, 0xFF, 0xFF, 0xFF));
            Set("HoverBrush", Rgba(0x1A, 0xFF, 0xFF, 0xFF));
            Set("PressedBrush", Rgba(0x10, 0xFF, 0xFF, 0xFF));
        }
        else
        {
            Set("SurfaceBrush", Rgba(0xF2, 0xF9, 0xF9, 0xF9));
            Set("SurfaceSolidBrush", Rgba(0xFF, 0xF3, 0xF3, 0xF3));
            Set("CardBrush", Rgba(0xB3, 0xFF, 0xFF, 0xFF));
            Set("StrokeBrush", Rgba(0x26, 0x00, 0x00, 0x00));
            Set("TextBrush", Rgba(0xFF, 0x1B, 0x1B, 0x1B));
            Set("TextSecondaryBrush", Rgba(0xB0, 0x00, 0x00, 0x00));
            Set("HoverBrush", Rgba(0x0F, 0x00, 0x00, 0x00));
            Set("PressedBrush", Rgba(0x08, 0x00, 0x00, 0x00));
        }
        Set("AccentBrush", Accent);
        Set("AccentSubtleBrush", Color.FromArgb(0x55, Accent.R, Accent.G, Accent.B));
        double lum = 0.299 * Accent.R + 0.587 * Accent.G + 0.114 * Accent.B;
        Set("AccentTextBrush", lum > 160 ? Colors.Black : Colors.White);

        try
        {
            Application.Current.ThemeMode = mode == "Dark" ? ThemeMode.Dark : mode == "Light" ? ThemeMode.Light : ThemeMode.System;
        }
        catch (Exception ex) { SettingsService.Log("ThemeMode failed: " + ex.Message); }

        Changed?.Invoke();
    }

    public static Brush Res(string key) => (Brush)Application.Current.FindResource(key);
}
