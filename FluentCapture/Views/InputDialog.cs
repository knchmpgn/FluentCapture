using System.Windows.Controls;
using System.Windows.Media;
using FluentSnip.Core;

namespace FluentSnip.Views;

/// <summary>Small name (+ optional color) prompt used for pin groups and pin titles.</summary>
public sealed class InputDialog : Window
{
    readonly TextBox box;
    string color = "";
    public string Value => box.Text.Trim();
    public string ColorHex => color;

    static readonly string[] Palette = { "", "#E81123", "#F7630C", "#FFB900", "#16C60C", "#0078D4", "#8764B8" };

    public InputDialog(string title, string prompt, string initial, bool pickColor, Window owner = null)
    {
        Title = title; SizeToContent = SizeToContent.WidthAndHeight; MinWidth = 340; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen;
        if (owner != null) Owner = owner;
        Topmost = true;
        SourceInitialized += (_, _) => WinHelper.ApplyChrome(this);

        var sp = new StackPanel { Margin = new Thickness(20) };
        sp.Children.Add(Ui.Label(prompt, 13));
        box = new TextBox { Text = initial, Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(8, 6, 8, 6) };
        sp.Children.Add(box);

        if (pickColor)
        {
            sp.Children.Add(Ui.Label("Color label", 12, true));
            ((TextBlock)sp.Children[^1]).Margin = new Thickness(0, 12, 0, 4);
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var borders = new List<Border>();
            foreach (var hex in Palette)
            {
                var b = new Border
                {
                    Width = 26, Height = 26, CornerRadius = new CornerRadius(13), Margin = new Thickness(0, 0, 8, 0), Cursor = System.Windows.Input.Cursors.Hand,
                    BorderThickness = new Thickness(2), BorderBrush = Brushes.Transparent,
                    Background = hex == "" ? (Brush)Application.Current.FindResource("StrokeBrush") : new SolidColorBrush(Ui.ParseColor(hex, Colors.Gray)),
                    ToolTip = hex == "" ? "No color" : hex
                };
                string h = hex;
                b.MouseLeftButtonDown += (_, _) => { color = h; foreach (var x in borders) x.BorderBrush = Brushes.Transparent; b.BorderBrush = Brushes.White; };
                borders.Add(b); row.Children.Add(b);
            }
            sp.Children.Add(row);
        }

        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        var ok = new Button { Content = "OK", Style = Ui.Style("AccentButton"), IsDefault = true };
        ok.Click += (_, _) => { if (Value.Length > 0) DialogResult = true; };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(4, 0, 0, 0) };
        bar.Children.Add(ok); bar.Children.Add(cancel);
        sp.Children.Add(bar);
        Content = sp;
        Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
    }
}
