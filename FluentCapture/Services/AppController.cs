using System.Threading;
using FluentSnip.Core;
using FluentSnip.Views;

namespace FluentSnip.Services;

/// <summary>Wires hotkeys, tray, capture flows and the tray menu together.</summary>
public static class AppController
{
    static HotkeyService hotkeys;
    static TrayIconService tray;
    static bool busy;
    static AppSettings Cfg => SettingsService.Current;

    public static void Start()
    {
        hotkeys = new HotkeyService();
        tray = new TrayIconService();
        tray.LeftClick += () =>
        {
            switch (Cfg.TrayClickAction) { case "Capture": StartCapture(OverlayIntent.Capture); break; case "Settings": OpenSettings(); break; }
        };
        tray.RightClick += ShowTrayMenu;
        ApplyTray(); ApplyHotkeys();
        ThemeManager.Changed += () => { };
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += (_, _) =>
            Application.Current.Dispatcher.BeginInvoke(() => { if (Cfg.Theme == "System") ThemeManager.Apply("System"); });
        SettingsService.ApplyStartup();
    }

    public static void ApplyTray()
    {
        // never leave the user without any way to reach the app
        bool show = Cfg.ShowTrayIcon;
        tray.SetVisible(show);
    }

    /// <summary>Releases every global hotkey (used while the settings window captures a new combination).</summary>
    public static void SuspendHotkeys() => hotkeys?.UnregisterAll();

    public static void ApplyHotkeys()
    {
        hotkeys.UnregisterAll();
        var failed = new List<string>();
        void Reg(string id, string label, Action a)
        {
            string g = Cfg.Hotkeys.GetValueOrDefault(id, "");
            if (!string.IsNullOrWhiteSpace(g) && !hotkeys.Register(g, a)) failed.Add($"{label} ({g})");
        }
        Reg(HK.Capture, "Screenshot", () => StartCapture(OverlayIntent.Capture));
        Reg(HK.Record, "Record", () => StartCapture(OverlayIntent.Record));
        Reg(HK.PinClipboard, "Pin clipboard", PinManager.PinClipboard);
        Reg(HK.ToggleHide, "Hide pins", PinManager.ToggleHideAll);
        Reg(HK.Penetrate, "Pass-through", PinManager.TogglePenetrationUnderCursor);
        Reg(HK.Settings, "Settings", OpenSettings);
        if (failed.Count > 0) ToastWindow.Show("Hotkey already in use: " + string.Join(", ", failed), Icons.Info);
    }

    public static void StartCapture(OverlayIntent intent, int delaySec = 0)
    {
        if (busy) return;
        if (delaySec > 0)
        {
            ToastWindow.Show($"Capturing in {delaySec} s…", Icons.Clock);
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(delaySec) };
            t.Tick += (_, _) => { t.Stop(); StartCapture(intent, 0); };
            t.Start(); return;
        }
        busy = true;
        try
        {
            ToastWindow.CloseCurrent();
            Application.Current.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);
            Thread.Sleep(40);
            var snap = Capture.ScreenSnapshot.Capture();
            var w = new CaptureOverlayWindow(snap, intent);
            w.Closed += (_, _) => busy = false;
            w.Show();
        }
        catch (Exception ex)
        {
            busy = false; SettingsService.Log("Capture failed: " + ex);
            ToastWindow.Show("Capture failed: " + ex.Message, Icons.Info);
        }
    }

    public static void OpenSettings() => SettingsWindow.ShowSingle();

    static List<MenuItemSpec> BuildMenu()
    {
        string H(string id) => Cfg.Hotkeys.GetValueOrDefault(id, "");
        return new List<MenuItemSpec>
        {
            new() { Glyph = Icons.Capture, Text = "Screenshot", Shortcut = H(HK.Capture), Click = () => StartCapture(OverlayIntent.Capture) },
            new() { Glyph = Icons.Clock, Text = "Screenshot after delay", Children = new List<MenuItemSpec>
                {
                    new() { Glyph = Icons.Clock, Text = "3 seconds", Click = () => StartCapture(OverlayIntent.Capture, 3) },
                    new() { Glyph = Icons.Clock, Text = "5 seconds", Click = () => StartCapture(OverlayIntent.Capture, 5) },
                    new() { Glyph = Icons.Clock, Text = "10 seconds", Click = () => StartCapture(OverlayIntent.Capture, 10) },
                } },
            new() { Glyph = Icons.Video, Text = "Record screen area", Shortcut = H(HK.Record), Click = () => StartCapture(OverlayIntent.Record) },
            MenuItemSpec.Sep(),
            new() { Glyph = Icons.Pin, Text = "Pin clipboard", Shortcut = H(HK.PinClipboard), Click = PinManager.PinClipboard },
            new() { Glyph = Icons.Pin, Text = $"Pins ({PinManager.Count})", Children = new List<MenuItemSpec>
                {
                    new() { Glyph = Icons.Hide, Text = "Hide / show all", Shortcut = H(HK.ToggleHide), Click = PinManager.ToggleHideAll },
                    new() { Glyph = Icons.Picture, Text = "Show all as thumbnails", Click = () => PinManager.ThumbnailsAll(true) },
                    new() { Glyph = Icons.Back, Text = "Cancel thumbnails", Click = () => PinManager.ThumbnailsAll(false) },
                    new() { Glyph = Icons.Home, Text = "Bring pins on-screen", Click = PinManager.EnsureVisible },
                    new() { Glyph = Icons.Save, Text = "Save all pins…", Click = PinManager.SaveAll },
                    new() { Glyph = Icons.Close, Text = "Close all pins", Click = PinManager.CloseAll },
                } },
            MenuItemSpec.Sep(),
            new() { Glyph = Icons.Folder, Text = "Open screenshot folder", Click = () => OpenFolder(Cfg.ScreenshotFolder) },
            new() { Glyph = Icons.Settings, Text = "Settings", Shortcut = H(HK.Settings), Click = OpenSettings },
            new() { Glyph = Icons.Exit, Text = "Exit", Click = Exit },
        };
    }

    static void OpenFolder(string path)
    {
        try
        {
            string p = SaveService.BuildPath(path, "x", "tmp", new CaptureContext());
            string dir = Path.GetDirectoryName(p)!;
            Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start("explorer.exe", dir);
        }
        catch { }
    }

    static void ShowTrayMenu() => new TrayMenuWindow(BuildMenu).Show();

    public static void Exit()
    {
        SettingsService.Save();
        try { tray?.Dispose(); hotkeys?.Dispose(); } catch { }
        Application.Current.Shutdown();
    }
}
