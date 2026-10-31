using System.Windows.Controls;
using System.Windows.Input;
using FluentSnip.Core;

namespace FluentSnip.Views;

public sealed class MenuItemSpec
{
    public string Glyph = "", Text = "", Shortcut = "";
    public Action Click;
    public List<MenuItemSpec> Children;
    public bool Separator, Checked;
    public static MenuItemSpec Sep() => new() { Separator = true };
}

/// <summary>Fluent tray menu: rounded window with acrylic backdrop, inline sub-pages instead of fly-outs.</summary>
public sealed class TrayMenuWindow : Window
{
    readonly StackPanel host = new() { Margin = new Thickness(6) };
    readonly Func<List<MenuItemSpec>> rootBuilder;
    bool positioned;

    public TrayMenuWindow(Func<List<MenuItemSpec>> build)
    {
        rootBuilder = build;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false; Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight; MinWidth = 270; AllowsTransparency = false;
        WindowStartupLocation = WindowStartupLocation.Manual; Left = -20000; Top = -20000; UseLayoutRounding = true;
        Content = host;
        SourceInitialized += (_, _) =>
        {
            WinHelper.ApplyChrome(this, Backdrop.Acrylic);
            WinHelper.ToolWindow(this, false);
        };
        Deactivated += (_, _) => { if (positioned) Close(); };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        ContentRendered += (_, _) => Place();
        Populate(rootBuilder(), null);
    }

    void Populate(List<MenuItemSpec> items, string backTitle)
    {
        host.Children.Clear();
        if (backTitle != null)
        {
            host.Children.Add(Row(new MenuItemSpec { Glyph = Icons.Back, Text = backTitle, Click = () => Populate(rootBuilder(), null) }, null));
            host.Children.Add(Separator());
        }
        foreach (var it in items)
        {
            if (it.Separator) { host.Children.Add(Separator()); continue; }
            host.Children.Add(Row(it, items));
        }
        if (positioned) { UpdateLayout(); Place(true); }
    }

    static Border Separator() => new() { Height = 1, Margin = new Thickness(8, 4, 8, 4), Background = (System.Windows.Media.Brush)Application.Current.FindResource("StrokeBrush") };

    UIElement Row(MenuItemSpec it, List<MenuItemSpec> siblings)
    {
        var grid = new Grid { Height = 36 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var g = Ui.Glyph(it.Checked ? Icons.Check : it.Glyph, 15); grid.Children.Add(g);
        var t = Ui.Label(it.Text, 13); t.TextWrapping = TextWrapping.NoWrap; Grid.SetColumn(t, 1); grid.Children.Add(t);
        FrameworkElement right = null;
        if (it.Children != null) right = Ui.Glyph(Icons.Chevron, 12, 0, "TextSecondaryBrush");
        else if (!string.IsNullOrEmpty(it.Shortcut)) right = Ui.Label(it.Shortcut, 12, true);
        if (right != null) { right.Margin = new Thickness(16, 0, 10, 0); Grid.SetColumn(right, 2); grid.Children.Add(right); }

        var b = new Border { CornerRadius = new CornerRadius(6), Background = Brushes0(), Child = grid, Cursor = Cursors.Hand };
        b.MouseEnter += (_, _) => b.SetResourceReference(Border.BackgroundProperty, "HoverBrush");
        b.MouseLeave += (_, _) => b.Background = Brushes0();
        b.MouseLeftButtonUp += (_, _) =>
        {
            if (it.Children != null) { Populate(it.Children, it.Text); return; }
            Close();
            if (it.Click != null) Application.Current.Dispatcher.BeginInvoke(it.Click);
        };
        return b;
    }

    static System.Windows.Media.Brush Brushes0() => System.Windows.Media.Brushes.Transparent;

    void Place(bool keepOpen = false)
    {
        double s = WinHelper.Scale(this);
        var p = WinHelper.CursorPos();
        var (work, _, _) = WinHelper.MonitorAt(p.X, p.Y);
        double w = ActualWidth * s, h = ActualHeight * s;
        double x = Math.Min(Math.Max(work.Left + 8, p.X - w / 2), work.Right - w - 8);
        double y = p.Y > work.Top + work.Height / 2 ? p.Y - h - 12 : p.Y + 12;
        y = Math.Min(Math.Max(work.Top + 8, y), work.Bottom - h - 8);
        if (!positioned || keepOpen) { WinHelper.SetPixelRect(this, x, y, w, h, true, true); }
        if (!positioned) { positioned = true; WinHelper.Foreground(this); }
    }
}
