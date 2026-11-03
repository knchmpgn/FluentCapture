using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using FluentSnip.Core;
using FluentSnip.Services;

namespace FluentSnip.Views;

/// <summary>Fluent settings window (Mica backdrop, rounded cards, Segoe Fluent Icons).</summary>
public sealed class SettingsWindow : Window
{
    static SettingsWindow instance;
    readonly AppSettings s = SettingsService.Current;
    readonly ScrollViewer pageHost = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 8, 0) };
    readonly List<(ToggleButton Btn, Func<UIElement> Build)> pages = new();

    public static void ShowSingle()
    {
        if (instance != null)
        {
            if (!instance.IsVisible) instance.Show();
            instance.WindowState = WindowState.Normal;
            WinHelper.Foreground(instance);
            return;
        }
        instance = new SettingsWindow(); instance.Show(); WinHelper.Foreground(instance);
    }

    SettingsWindow()
    {
        Title = "FluentCapture settings"; Width = 960; Height = 680; MinWidth = 780; MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        SourceInitialized += (_, _) => WinHelper.ApplyChrome(this, Backdrop.Mica);
        ThemeManager.Changed += OnTheme;
        Closed += (_, _) => { ThemeManager.Changed -= OnTheme; SettingsService.Save(); instance = null; };

        var root = new Grid { Margin = new Thickness(12) };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
        root.ColumnDefinitions.Add(new ColumnDefinition());

        var nav = new StackPanel { Margin = new Thickness(0, 4, 12, 0) };
        var title = Ui.Label("FluentSnip", 20, false, FontWeights.SemiBold); title.Margin = new Thickness(10, 4, 0, 16); nav.Children.Add(title);
        void Page(string name, string glyph, Func<UIElement> build)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(Ui.Glyph(glyph, 16)); var t = Ui.Label(name, 13); t.Margin = new Thickness(12, 0, 0, 0); sp.Children.Add(t);
            var b = new ToggleButton { Style = Ui.Style("NavToggle"), Content = sp };
            b.Click += (_, _) => Navigate(b);
            nav.Children.Add(b); pages.Add((b, build));
        }
        Page("General", Icons.Settings, PageGeneral);
        Page("Capture", Icons.Capture, PageCapture);
        Page("Saving", Icons.Save, PageSave);
        Page("Recording", Icons.Video, PageRecord);
        Page("Pins", Icons.Pin, PagePins);
        Page("Hotkeys", Icons.Keyboard, PageHotkeys);
        Page("About", Icons.Info, PageAbout);
        Grid.SetColumn(nav, 0); root.Children.Add(nav);
        Grid.SetColumn(pageHost, 1); root.Children.Add(pageHost);
        Content = root;
        Navigate(pages[0].Btn);
    }

    void OnTheme() { WinHelper.ApplyChrome(this, Backdrop.Mica); }

    void Navigate(ToggleButton b)
    {
        foreach (var p in pages) p.Btn.IsChecked = p.Btn == b;
        pageHost.Content = pages.First(p => p.Btn == b).Build();
        pageHost.ScrollToTop();
    }

    // ------------------------------------------------------------ builders
    StackPanel NewPage(string title)
    {
        var sp = new StackPanel { Margin = new Thickness(8, 4, 8, 24) };
        sp.Children.Add(Ui.Label(title, 24, false, FontWeights.SemiBold));
        ((TextBlock)sp.Children[0]).Margin = new Thickness(0, 0, 0, 14);
        return sp;
    }

    static Border Card(params UIElement[] rows)
    {
        var sp = new StackPanel();
        for (int i = 0; i < rows.Length; i++)
        {
            if (i > 0) sp.Children.Add(new Border { Height = 1, Background = (System.Windows.Media.Brush)Application.Current.FindResource("StrokeBrush") });
            sp.Children.Add(rows[i]);
        }
        return new Border { Style = Ui.Style("CardBorder"), Child = sp, Margin = new Thickness(0, 0, 0, 12) };
    }

    static UIElement TemplateHelp()
    {
        var variables = new[]
        {
            ("$yyyy-MM-dd_HH-mm-ss$", "Date and time"), ("%title%", "Window title"),
            ("%process%", "Process name"), ("%computername%", "Computer name"),
            ("%username%", "User name"), ("%width%", "Capture width"),
            ("%height%", "Capture height"), ("%counter%", "Incrementing number"),
            ("%title#10%", "Limit title to 10 characters"), ("%process!%", "Process name without .exe"),
            ("$yyyy-MM-dd$", "Date"), ("$HH-mm-ss$", "Time"),
            ("$yyyy-MM-dd_HH-mm-ss$", "Date and time"), ("$yyyyMMdd_HHmmss$", "Compact date and time")
        };
        var content = new StackPanel { Margin = new Thickness(16, 12, 16, 14) };
        content.Children.Add(Ui.Label("File name template variables", 13, false, FontWeights.SemiBold));
        var grid = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        for (int i = 0; i < variables.Length; i += 2)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (int j = 0; j < 2; j++)
            {
                var (token, description) = variables[i + j];
                var line = new Grid { Margin = new Thickness(0, 4, 12, 4) };
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                line.ColumnDefinitions.Add(new ColumnDefinition());
                var key = Ui.Label(token, 11, false, FontWeights.SemiBold);
                key.Margin = new Thickness(0, 0, 8, 0);
                var detail = Ui.Label(description, 11, true);
                Grid.SetColumn(detail, 1);
                line.Children.Add(key); line.Children.Add(detail);
                Grid.SetRow(line, i / 2); Grid.SetColumn(line, j); grid.Children.Add(line);
            }
        }
        content.Children.Add(grid);
        return new Border { Style = Ui.Style("CardBorder"), Child = content, Margin = new Thickness(0, 0, 0, 12) };
    }

    static Grid Row(string label, UIElement control, string desc = null, string glyph = null)
    {
        var g = new Grid { Margin = new Thickness(16, 12, 16, 12) };
        g.ColumnDefinitions.Add(new ColumnDefinition()); g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(Ui.Label(label, 14));
        if (desc != null) { var d = Ui.Label(desc, 12, true); d.Margin = new Thickness(0, 2, 12, 0); left.Children.Add(d); }
        Grid.SetColumn(left, 0); g.Children.Add(left);
        if (control is FrameworkElement fe) fe.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(control, 1); g.Children.Add(control);
        return g;
    }

    static CheckBox Check(bool v, Action<bool> set)
    {
        var c = new CheckBox { IsChecked = v, MinWidth = 0, Margin = new Thickness(0) };
        c.Click += (_, _) => set(c.IsChecked == true);
        return c;
    }

    static ComboBox Combo(IEnumerable<string> items, string current, Action<string> set, double width = 160)
    {
        var c = new ComboBox { Width = width };
        foreach (var i in items) c.Items.Add(i);
        c.SelectedItem = current; if (c.SelectedItem == null && c.Items.Count > 0) c.SelectedIndex = 0;
        c.SelectionChanged += (_, _) => { if (c.SelectedItem is string t) set(t); };
        return c;
    }

    static Slider Slide(double min, double max, double v, Action<double> set, double width = 180)
    {
        var sl = new Slider { Minimum = min, Maximum = max, Value = v, Width = width, IsSnapToTickEnabled = true, TickFrequency = 1, AutoToolTipPlacement = AutoToolTipPlacement.TopLeft, AutoToolTipPrecision = 0 };
        sl.ValueChanged += (_, e) => set(e.NewValue);
        return sl;
    }

    TextBox Text(string v, Action<string> set, double width = 300, Action<string> changed = null)
    {
        var t = new TextBox { Text = v, Width = width, Padding = new Thickness(8, 5, 8, 5) };
        if (changed != null) t.TextChanged += (_, _) => changed(t.Text);
        t.LostFocus += (_, _) => set(t.Text);
        return t;
    }

    UIElement FolderPicker(string current, Action<string> set, Action<string> changed = null)
    {
        var box = new TextBox { Text = current, Width = 300, Padding = new Thickness(8, 5, 8, 5) };
        if (changed != null) box.TextChanged += (_, _) => changed(box.Text);
        box.LostFocus += (_, _) => set(box.Text);
        var b = new Button { Content = "Browse…", Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(12, 5, 12, 5) };
        b.Click += (_, _) =>
        {
            var d = new Microsoft.Win32.OpenFolderDialog { Title = "Choose folder" };
            try { if (Directory.Exists(Environment.ExpandEnvironmentVariables(box.Text))) d.InitialDirectory = box.Text; } catch { }
            if (d.ShowDialog(this) == true) { box.Text = d.FolderName; set(d.FolderName); }
        };
        var sp = new StackPanel { Orientation = Orientation.Horizontal }; sp.Children.Add(box); sp.Children.Add(b);
        return sp;
    }

    void Save() => SettingsService.Save();

    // ------------------------------------------------------------ pages
    UIElement PageGeneral()
    {
        var p = NewPage("General");
        p.Children.Add(Card(
            Row("Theme", Combo(new[] { "System", "Light", "Dark" }, s.Theme, v => { s.Theme = v; Save(); ThemeManager.Apply(v); })),
            Row("Start with Windows", Check(s.StartWithWindows, v => { s.StartWithWindows = v; Save(); SettingsService.ApplyStartup(); })),
            Row("Show tray icon", Check(s.ShowTrayIcon, v => { s.ShowTrayIcon = v; Save(); AppController.ApplyTray(); }), "Hotkey reopens settings"),
            Row("Tray icon click", Combo(new[] { "Capture", "Settings", "None" }, s.TrayClickAction, v => { s.TrayClickAction = v; Save(); })),
            Row("Notifications", Check(s.Notifications, v => { s.Notifications = v; Save(); }), "Toasts after actions")
        ));
        var langs = OcrService.Languages();
        var items = new List<string> { "Windows profile languages" }; items.AddRange(langs.Select(l => $"{l.Name} ({l.Tag})"));
        string cur = string.IsNullOrEmpty(s.OcrLanguage) ? items[0] : items.FirstOrDefault(i => i.EndsWith($"({s.OcrLanguage})")) ?? items[0];
        p.Children.Add(Card(
            Row("OCR language", Combo(items, cur, v =>
            {
                s.OcrLanguage = v == items[0] ? "" : v[(v.LastIndexOf('(') + 1)..^1]; Save();
            }, 260), langs.Count == 0 ? "No OCR language pack installed" : "Offline OCR engine")
        ));
        return p;
    }

    UIElement PageCapture()
    {
        var p = NewPage("Capture");
        p.Children.Add(Card(
            Row("Detect windows and UI elements", Check(s.DetectUiElements, v => { s.DetectUiElements = v; Save(); }), "Hover highlight; wheel changes element"),
            Row("Show cursor in screenshots", Check(s.ShowCursor, v => { s.ShowCursor = v; Save(); })),
            Row("Crosshair guides", Check(s.ShowCrosshair, v => { s.ShowCrosshair = v; Save(); })),
            Row("Dim amount", Slide(0, 90, s.DimOpacity, v => { s.DimOpacity = (int)v; Save(); }), "Outside selection darkness")
        ));
        p.Children.Add(Card(
            Row("Magnifier", Check(s.ShowMagnifier, v => { s.ShowMagnifier = v; Save(); })),
            Row("Magnifier grid", Check(s.MagnifierGrid, v => { s.MagnifierGrid = v; Save(); })),
            Row("Magnification", Slide(2, 16, s.MagnifierZoom, v => { s.MagnifierZoom = (int)v; Save(); }, 160)),
            Row("Color format", Combo(new[] { "RGB", "HEX", "HSV", "HSL" }, s.MagnifierColorFormat, v => { s.MagnifierColorFormat = v; Save(); }, 120), "Shift cycles, C copies")
        ));
        p.Children.Add(Card(
            Row("Rounded corners", Check(s.RoundedCorners, v => { s.RoundedCorners = v; Save(); })),
            Row("Corner radius", Slide(2, 48, s.CornerRadius, v => { s.CornerRadius = (int)v; Save(); }, 160)),
            Row("Border", Check(s.OutputBorder, v => { s.OutputBorder = v; Save(); })),
            Row("Shadow", Check(s.OutputShadow, v => { s.OutputShadow = v; Save(); }), "Copied/saved/pinned")
        ));
        p.Children.Add(Card(
            Row("Watermark", Check(s.WatermarkEnabled, v => { s.WatermarkEnabled = v; Save(); })),
            Row("Watermark text", Text(s.WatermarkText, v => { s.WatermarkText = v; Save(); }, 220)),
            Row("Watermark opacity", Slide(3, 80, s.WatermarkOpacity, v => { s.WatermarkOpacity = (int)v; Save(); }, 160))
        ));
        return p;
    }

    UIElement PageSave()
    {
        var p = NewPage("Saving");
        string folderTemplate = s.ScreenshotFolder, nameTemplate = s.ScreenshotTemplate;
        var preview = Ui.Label("", 12, true); preview.Margin = new Thickness(16, 12, 16, 12);
        void Refresh() => preview.Text = "Live preview: " + SaveService.TemplatePreview(folderTemplate, nameTemplate, s.ImageFormat, DateTime.Now);
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => Refresh(); timer.Start();
        p.Unloaded += (_, _) => timer.Stop();
        p.Children.Add(Card(
            Row("Screenshot folder", FolderPicker(s.ScreenshotFolder, v => { folderTemplate = s.ScreenshotFolder = v; Save(); Refresh(); }, v => { folderTemplate = v; Refresh(); }), "Default save location"),
            Row("File name template", Text(s.ScreenshotTemplate, v => { nameTemplate = s.ScreenshotTemplate = v; Save(); Refresh(); }, 300, v => { nameTemplate = v; Refresh(); })),
            preview,
            Row("Image format", Combo(new[] { "png", "jpg", "bmp" }, s.ImageFormat, v => { s.ImageFormat = v; Save(); Refresh(); }, 100)),
            Row("JPEG quality", Slide(10, 100, s.JpegQuality, v => { s.JpegQuality = (int)v; Save(); }, 160))
        ));
        Refresh();
        p.Children.Add(Card(
            Row("Ask where to save (Save button / Ctrl+S)", Check(s.AskWhereToSave, v => { s.AskWhereToSave = v; Save(); }), "Ctrl+Shift+S always direct"),
            Row("Auto-save every capture", Check(s.AutoSave, v => { s.AutoSave = v; Save(); }), "On copy or pin"),
            Row("Copy to clipboard when saving", Check(s.CopyOnSave, v => { s.CopyOnSave = v; Save(); }))
        ));
        p.Children.Add(TemplateHelp());
        return p;
    }

    UIElement PageRecord()
    {
        var p = NewPage("Recording");
        string folderTemplate = s.RecordingFolder, nameTemplate = s.RecordingTemplate;
        var preview = Ui.Label("", 12, true); preview.Margin = new Thickness(16, 12, 16, 12);
        void Refresh() => preview.Text = "Live preview: " + SaveService.TemplatePreview(folderTemplate, nameTemplate, s.RecordFormat, DateTime.Now);
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => Refresh(); timer.Start();
        p.Unloaded += (_, _) => timer.Stop();
        p.Children.Add(Card(
            Row("Recording folder", FolderPicker(s.RecordingFolder, v => { folderTemplate = s.RecordingFolder = v; Save(); Refresh(); }, v => { folderTemplate = v; Refresh(); })),
            Row("File name template", Text(s.RecordingTemplate, v => { nameTemplate = s.RecordingTemplate = v; Save(); Refresh(); }, 300, v => { nameTemplate = v; Refresh(); })),
            preview
        ));
        Refresh();
        p.Children.Add(Card(
            Row("Format", Combo(new[] { "mp4", "gif" }, s.RecordFormat, v => { s.RecordFormat = v; Save(); Refresh(); }, 100)),
            Row("Frame rate", Combo(new[] { "5", "10", "15", "20", "24", "30", "60" }, s.RecordFps.ToString(), v => { s.RecordFps = int.Parse(v); Save(); }, 100)),
            Row("Start delay (seconds)", Combo(new[] { "0", "3", "5", "10" }, s.RecordDelay.ToString(), v => { s.RecordDelay = int.Parse(v); Save(); }, 100)),
            Row("Record mouse cursor", Check(s.RecordCursor, v => { s.RecordCursor = v; Save(); })),
            Row("Record computer audio", Check(s.RecordSystemAudio, v => { s.RecordSystemAudio = v; Save(); }), "MP4 only"),
            Row("Record microphone", Check(s.RecordMic, v => { s.RecordMic = v; Save(); }), "MP4 only; mic permission"),
            Row("MP4 quality", Combo(new[] { "Low", "Medium", "High" }, s.Mp4Quality, v => { s.Mp4Quality = v; Save(); }, 120)),
            Row("Loop GIF", Check(s.GifLoop, v => { s.GifLoop = v; Save(); }))
        ));
        p.Children.Add(TemplateHelp());
        return p;
    }

    UIElement PagePins()
    {
        var p = NewPage("Pins");
        p.Children.Add(Card(
            Row("Colored border by state", Check(s.PinShadowBorder, v => { s.PinShadowBorder = v; Save(); }), "State colors"),
            Row("Default opacity (%)", Slide(20, 100, s.PinDefaultOpacity, v => { s.PinDefaultOpacity = (int)v; Save(); }, 160)),
            Row("Zoom step (%)", Slide(2, 30, s.PinZoomStep, v => { s.PinZoomStep = (int)v; Save(); }, 160), "Wheel zoom; Ctrl+wheel opacity")
        ));
        return p;
    }

    UIElement PageHotkeys()
    {
        var p = NewPage("Hotkeys");
        var note = Ui.Label("Press a new combination in a box (PrintScreen alone works). ✕/Backspace/Delete removes. In-use keys can't register.", 12, true);
        note.Margin = new Thickness(0, 0, 0, 10); p.Children.Add(note);
        var rows = new List<UIElement>();
        foreach (var (id, label, def) in HK.All)
        {
            string hid = id;
            string cur = s.Hotkeys.GetValueOrDefault(id, def);
            var box = new TextBox { Text = string.IsNullOrEmpty(cur) ? "None" : cur, Width = 200, IsReadOnly = true, IsReadOnlyCaretVisible = false, Padding = new Thickness(8, 5, 8, 5), TextAlignment = TextAlignment.Center };
            void Set(string g) { box.Text = string.IsNullOrEmpty(g) ? "None" : g; s.Hotkeys[hid] = g; Save(); if (!box.IsKeyboardFocused) AppController.ApplyHotkeys(); }   // while focused, hotkeys stay released until focus leaves
            box.PreviewKeyDown += (_, e) =>
            {
                e.Handled = true;
                var key = e.Key == Key.System ? e.SystemKey : e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key;
                if (key == Key.Back || key == Key.Delete) { Set(""); return; }
                if (Gesture.IsModifierKey(key) || key == Key.Escape || key == Key.Tab) return;
                var mods = Keyboard.Modifiers;
                if (mods == ModifierKeys.None && !SingleKeyOk(key)) return;
                Set(Gesture.Format(mods, key));
            };
            // PrintScreen normally only reports a key-up to applications
            box.PreviewKeyUp += (_, e) => { if (e.Key == Key.Snapshot) { e.Handled = true; Set(Gesture.Format(Keyboard.Modifiers, Key.Snapshot)); } };
            // while a box has focus no global hotkey may fire, otherwise pressing an existing combination would trigger it
            box.GotKeyboardFocus += (_, _) => AppController.SuspendHotkeys();
            box.LostKeyboardFocus += (_, _) => AppController.ApplyHotkeys();
            var clear = Ui.IconButton(Icons.Clear, "Remove this hotkey (no shortcut)", () => Set(""));
            var reset = Ui.IconButton(Icons.Undo, "Reset to default", () => Set(def));
            var sp = new StackPanel { Orientation = Orientation.Horizontal }; sp.Children.Add(box); sp.Children.Add(clear); sp.Children.Add(reset);
            rows.Add(Row(label, sp));
        }
        p.Children.Add(Card(rows.ToArray()));

        var inApp = Ui.Label(
            "While capturing:  Esc close · Enter / Ctrl+C copy · Ctrl+S save · Ctrl+Shift+S quick save · Ctrl+T pin · Shift+C OCR to clipboard · Shift+Q QR code · Shift+S scrolling screenshot · Shift+R record · C copy color · Shift switch color format · ~ toggle cursor\n" +
            "Selection:  arrows move 1 px · Shift+arrows shrink · Ctrl+arrows grow · Shift while dragging keeps 1:1 · double-click copies · right-click cancels\n" +
            "Annotate:  Shift+1…8 and Shift+E select tools · Ctrl+Z / Ctrl+Y undo / redo · Delete removes · wheel changes size · Ctrl+wheel changes opacity\n" +
            "On a pin:  wheel zoom · Ctrl+wheel opacity · middle-click reset · L lock · T always-on-top · R thumbnail · Space annotate · F2 title · Ctrl+drag drag content out · Esc / double-click close",
            12, true);
        inApp.Margin = new Thickness(4, 0, 0, 12); p.Children.Add(inApp);
        return p;
    }

    static bool SingleKeyOk(Key k) => (k >= Key.F1 && k <= Key.F24) || k == Key.Snapshot;

    UIElement PageAbout()
    {
        var p = NewPage("About");
        var sp = new StackPanel { Margin = new Thickness(16) };
        sp.Children.Add(Ui.Label("FluentCapture 1.0", 16, false, FontWeights.SemiBold));
        sp.Children.Add(Ui.Label("Screenshot, pin, OCR, QR and recording in a Fluent design. Portable: settings and log next to the .exe.", 13, true));
        ((TextBlock)sp.Children[1]).Margin = new Thickness(0, 8, 0, 12);
        var open = new Button { Content = "Open settings folder", HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(14, 6, 14, 6) };
        open.Click += (_, _) => { try { System.Diagnostics.Process.Start("explorer.exe", SettingsService.Dir); } catch { } };
        sp.Children.Add(open);
        p.Children.Add(Card(sp));
        return p;
    }
}
