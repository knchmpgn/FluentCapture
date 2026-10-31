using System.Windows.Controls;
using System.Windows.Threading;
using FluentSnip.Core;

namespace FluentSnip.Views;

/// <summary>Small Fluent notification card above the taskbar (non-activating).</summary>
public sealed class ToastWindow : Window
{
    static ToastWindow current;
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(4.5) };

    public static void CloseCurrent() { try { current?.Close(); } catch { } current = null; }

    public static void Show(string message, string glyph = Icons.Accept, params (string Text, Action Action)[] actions)
    {
        if (!SettingsService.Current.Notifications) return;
        CloseCurrent();
        current = new ToastWindow(message, glyph, actions);
        current.Show();
    }

    ToastWindow(string message, string glyph, (string Text, Action Action)[] actions)
    {
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false; Topmost = true;
        ShowActivated = false; SizeToContent = SizeToContent.WidthAndHeight; MaxWidth = 460;
        AllowsTransparency = false; WindowStartupLocation = WindowStartupLocation.Manual; Left = -20000; Top = -20000;
        UseLayoutRounding = true;

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(14, 10, 14, 10) };
        row.Children.Add(Ui.Glyph(glyph, 18, 0, "AccentBrush"));
        var msg = Ui.Label(message, 13); msg.Margin = new Thickness(12, 0, 8, 0); msg.MaxWidth = 280;
        row.Children.Add(msg);
        foreach (var (text, act) in actions)
        {
            var b = new Button { Content = text, Margin = new Thickness(4, 0, 0, 0), Padding = new Thickness(10, 3, 10, 3), Focusable = false };
            b.Click += (_, _) => { try { act(); } catch { } Close(); };
            row.Children.Add(b);
        }
        Content = row;
        SourceInitialized += (_, _) =>
        {
            WinHelper.ApplyChrome(this, Backdrop.Acrylic);
            WinHelper.ToolWindow(this, true);
        };
        ContentRendered += (_, _) =>
        {
            double s = WinHelper.Scale(this);
            var (work, _, _) = WinHelper.MonitorAt(0, 0);
            var p = WinHelper.CursorPos();
            (work, _, _) = WinHelper.MonitorAt(p.X, p.Y);
            double w = ActualWidth * s, h = ActualHeight * s;
            WinHelper.SetPixelRect(this, work.Right - w - 16 * s, work.Bottom - h - 16 * s, w, h, true);
        };
        timer.Tick += (_, _) => { timer.Stop(); Close(); };
        timer.Start();
        Closed += (_, _) => { timer.Stop(); if (current == this) current = null; };
    }
}
