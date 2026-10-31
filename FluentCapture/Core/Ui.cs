using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace FluentSnip.Core;

/// <summary>Segoe Fluent Icons code points used across the app.</summary>
public static class Icons
{
    public const string Camera = "\uE722", Video = "\uE714", Pin = "\uE718", Unpin = "\uE77A", Save = "\uE74E", Copy = "\uE8C8",
        Close = "\uE711", Undo = "\uE7A7", Redo = "\uE7A6", Delete = "\uE74D", Settings = "\uE713", Accept = "\uE8FB",
        Edit = "\uE70F", Stop = "\uE71A", Pause = "\uE769", Play = "\uE768", Text = "\uE8D2", Highlighter = "\uE7E6",
        Eraser = "\uE75C", Rectangle = "\uE739", Ellipse = "\uEA3A", Line = "\uE738", Arrow = "\uE72A",
        Ocr = "\uF19D", Qr = "\uED14", Scroll = "\uECE7", Lock = "\uE72E", Unlock = "\uE785",
        Rotate = "\uE7AD", Folder = "\uE8B7", Link = "\uE71B", Eye = "\uE7B3", Hide = "\uE890", Color = "\uE790",
        Eyedropper = "\uE790", Paste = "\uE77F", Clock = "\uE917", Picture = "\uE8B9", Home = "\uE80F", Info = "\uE946",
        Keyboard = "\uE765", Save2 = "\uE78C", Chevron = "\uE76C", Back = "\uE72B", Add = "\uE710", Check = "\uE73E",
        Brush = "\uE790", Library = "\uE8F1", Number = "\uF146", Volume = "\uE767", Mic = "\uE720", Exit = "\uE7E8", Page = "\uE7C3", Globe = "\uE774", Capture = "\uE7A8", Pen = "\uE76D", Record = "\uE7C8", Clear = "\uE894";
}

public static class Ui
{
    public static Style Style(string key) => (Style)Application.Current.FindResource(key);

    public static FontFamily IconFont => (FontFamily)Application.Current.FindResource("IconFont");

    public static TextBlock Glyph(string glyph, double size = 16, double rotate = 0, string brush = "TextBrush")
    {
        var t = new TextBlock
        {
            Text = glyph, FontFamily = IconFont, FontSize = size,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };
        t.SetResourceReference(TextBlock.ForegroundProperty, brush);
        if (rotate != 0) { t.RenderTransformOrigin = new Point(.5, .5); t.RenderTransform = new RotateTransform(rotate); }
        return t;
    }

    public static Button IconButton(string glyph, string tip, Action click, double rotate = 0)
    {
        var b = new Button { Style = Style("IconButton"), ToolTip = tip };
        b.Content = rotate == 0 ? glyph : (object)Glyph(glyph, 16, rotate);
        b.Click += (_, _) => click();
        return b;
    }

    public static ToggleButton IconToggle(object content, string tip, Action<bool> changed)
    {
        var b = new ToggleButton { Style = Style("IconToggle"), ToolTip = tip, Content = content };
        b.Click += (_, _) => changed(b.IsChecked == true);
        return b;
    }

    public static ToggleButton Chip(string text, bool on, Action<bool> changed)
    {
        var b = new ToggleButton { Style = Style("ChipToggle"), Content = text, IsChecked = on };
        b.Click += (_, _) => changed(b.IsChecked == true);
        return b;
    }

    public static Border VSeparator() => new()
    {
        Width = 1, Height = 20, Margin = new Thickness(5, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center,
        Background = (Brush)Application.Current.FindResource("StrokeBrush")
    };

    public static TextBlock Label(string text, double size = 13, bool secondary = false, FontWeight? weight = null)
    {
        var t = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        t.SetResourceReference(TextBlock.ForegroundProperty, secondary ? "TextSecondaryBrush" : "TextBrush");
        if (weight.HasValue) t.FontWeight = weight.Value;
        return t;
    }

    public static Color ParseColor(string s, Color fallback)
    {
        try { return (Color)ColorConverter.ConvertFromString(s); } catch { return fallback; }
    }

    public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
}
