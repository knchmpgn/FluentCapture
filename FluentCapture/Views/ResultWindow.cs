using System.Diagnostics;
using System.Windows.Controls;
using FluentSnip.Core;

namespace FluentSnip.Views;

/// <summary>Shows OCR text or QR contents with copy / open actions.</summary>
public sealed class ResultWindow : Window
{
    readonly TextBox box;

    public ResultWindow(string title, string glyph, string text, bool allowOpenLink)
    {
        Title = title; Width = 520; Height = 380; MinWidth = 360; MinHeight = 240;
        WindowStartupLocation = WindowStartupLocation.CenterScreen; Topmost = true;
        SourceInitialized += (_, _) => WinHelper.ApplyChrome(this);

        var root = new Grid { Margin = new Thickness(18) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        head.Children.Add(Ui.Glyph(glyph, 20, 0, "AccentBrush"));
        var t = Ui.Label(title, 18, false, FontWeights.SemiBold); t.Margin = new Thickness(10, 0, 0, 0);
        head.Children.Add(t);
        root.Children.Add(head);

        box = new TextBox
        {
            Text = text, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontSize = 14,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(10)
        };
        Grid.SetRow(box, 1); root.Children.Add(box);

        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var copy = new Button { Content = "Copy", Style = Ui.Style("AccentButton") };
        copy.Click += (_, _) => { SetClip(box.Text); ToastWindow.Show("Copied to clipboard"); };
        var copyClose = new Button { Content = "Copy && close", Margin = new Thickness(4, 0, 0, 0), Padding = new Thickness(14, 6, 14, 6) };
        copyClose.Click += (_, _) => { SetClip(box.Text); Close(); };
        if (allowOpenLink)
        {
            var open = new Button { Content = "Open link", Margin = new Thickness(0, 0, 4, 0), Padding = new Thickness(14, 6, 14, 6) };
            open.Click += (_, _) =>
            {
                var first = box.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).FirstOrDefault(x => Uri.TryCreate(x, UriKind.Absolute, out var u) && (u.Scheme == "http" || u.Scheme == "https"));
                if (first != null) Process.Start(new ProcessStartInfo(first) { UseShellExecute = true });
                else ToastWindow.Show("No web link found in the result", Icons.Info);
            };
            bar.Children.Add(open);
        }
        bar.Children.Add(copy); bar.Children.Add(copyClose);
        Grid.SetRow(bar, 2); root.Children.Add(bar);
        Content = root;
        PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) Close(); };
    }

    static void SetClip(string s) { try { Clipboard.SetText(s); } catch { } }
}
