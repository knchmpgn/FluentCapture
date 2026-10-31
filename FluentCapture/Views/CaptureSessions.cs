using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FluentSnip.Core;
using FluentSnip.Native;
using FluentSnip.Recording;
using FluentSnip.Services;

namespace FluentSnip.Views;

/// <summary>Click-through, capture-excluded rectangle drawn just outside the capture region.</summary>
public sealed class RegionFrameWindow : Window
{
    readonly Border border = new() { BorderThickness = new Thickness(3), CornerRadius = new CornerRadius(2) };
    readonly Rect region;
    public const int Margin = 4;

    public RegionFrameWindow(Rect regionPx)
    {
        region = regionPx;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false; Topmost = true; ShowActivated = false;
        AllowsTransparency = true; Background = Brushes.Transparent; WindowStartupLocation = WindowStartupLocation.Manual; Left = -20000; Top = -20000;
        Content = border; SetColor(false);
        SourceInitialized += (_, _) =>
        {
            WinHelper.SwallowDpiChanges(this);
            WinHelper.ToolWindow(this, true, true);
            WinHelper.ExcludeFromCapture(this);
        };
        Loaded += (_, _) => WinHelper.SetPixelRect(this, region.X - Margin, region.Y - Margin, region.Width + Margin * 2, region.Height + Margin * 2, true);
    }

    public void SetColor(bool recording)
    {
        border.BorderBrush = new SolidColorBrush(recording ? Color.FromRgb(0xE8, 0x11, 0x23) : ThemeManager.Accent);
        double s = Math.Max(1, WinHelper.Scale(this));
        border.BorderThickness = new Thickness(3 / s * 1.0);
    }
}

public static class RegionBar
{
    public static void Place(Window bar, Rect region)
    {
        bar.UpdateLayout();
        double s = WinHelper.Scale(bar), w = bar.ActualWidth * s, h = bar.ActualHeight * s;
        var (work, _, _) = WinHelper.MonitorAt(region.X + region.Width / 2, region.Bottom);
        double x = Math.Min(Math.Max(work.Left + 4, region.Right - w), work.Right - w - 4);
        double y = region.Bottom + 12 * s;
        if (y + h > work.Bottom) y = region.Top - h - 12 * s;
        if (y < work.Top) y = Math.Min(region.Bottom - h - 12 * s, work.Bottom - h - 4);
        WinHelper.SetPixelRect(bar, x, y, w, h, true, true);
    }
}

/// <summary>Region recording: frame + control bar (format, fps, delay, record / pause / stop).</summary>
public sealed class RecordingSession
{
    static RecordingSession active;
    readonly Rect region; readonly CaptureContext ctx;
    readonly RegionFrameWindow frame; readonly Window bar = new();
    readonly TextBlock timeText = Ui.Label("00:00", 14, false, FontWeights.SemiBold);
    readonly DispatcherTimer ui = new() { Interval = TimeSpan.FromMilliseconds(250) };
    readonly DispatcherTimer countdown = new() { Interval = TimeSpan.FromSeconds(1) };
    Button recBtn, pauseBtn; ComboBox fpsBox, delayBox; ToggleButton gifChip, mp4Chip, sysBtn, micBtn;
    ScreenRecorder rec; bool recording, busy; int cd;

    public static void Begin(Rect regionPx, CaptureContext ctx)
    {
        if (active != null) return;
        try { active = new RecordingSession(regionPx, ctx); }
        catch (Exception ex) { active = null; SettingsService.Log("Recording session failed: " + ex); ToastWindow.Show("Could not start recording: " + ex.Message, Icons.Info); }
    }

    RecordingSession(Rect region, CaptureContext ctx)
    {
        this.region = region; this.ctx = ctx;
        frame = new RegionFrameWindow(region); frame.Show();
        BuildBar();
        bar.Loaded += (_, _) => RegionBar.Place(bar, region);
        bar.Show();
        ui.Tick += (_, _) => { if (rec != null) timeText.Text = Fmt(rec.Elapsed); };
        countdown.Tick += (_, _) => { cd--; if (cd <= 0) { countdown.Stop(); StartNow(); } else timeText.Text = cd.ToString(); };
    }

    static string Fmt(TimeSpan t) => $"{(int)t.TotalMinutes:00}:{t.Seconds:00}";

    void BuildBar()
    {
        var s = SettingsService.Current;
        bar.WindowStyle = WindowStyle.None; bar.ResizeMode = ResizeMode.NoResize; bar.ShowInTaskbar = false; bar.Topmost = true;
        bar.SizeToContent = SizeToContent.WidthAndHeight; bar.WindowStartupLocation = WindowStartupLocation.Manual; bar.Left = -20000; bar.Top = -20000;
        bar.SourceInitialized += (_, _) => { WinHelper.ApplyChrome(bar, Backdrop.Acrylic); WinHelper.ExcludeFromCapture(bar); WinHelper.SwallowDpiChanges(bar); };
        bar.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Cancel(); };
        bar.MouseLeftButtonDown += (_, e) => { if (e.OriginalSource is Border || e.OriginalSource is Grid || e.OriginalSource is StackPanel) { try { bar.DragMove(); } catch { } } };

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 6, 8, 6) };
        row.Children.Add(Ui.IconButton(Icons.Close, "Cancel (Esc)", Cancel));
        row.Children.Add(Ui.VSeparator());
        gifChip = Ui.Chip("GIF", s.RecordFormat == "gif", on => { if (on) { s.RecordFormat = "gif"; mp4Chip.IsChecked = false; SettingsService.Save(); } else gifChip.IsChecked = true; RefreshAudioButtons(); });
        mp4Chip = Ui.Chip("MP4", s.RecordFormat != "gif", on => { if (on) { s.RecordFormat = "mp4"; gifChip.IsChecked = false; SettingsService.Save(); } else mp4Chip.IsChecked = true; RefreshAudioButtons(); });
        row.Children.Add(gifChip); row.Children.Add(mp4Chip);
        fpsBox = new ComboBox { Width = 82, Margin = new Thickness(8, 0, 0, 0), ToolTip = "Frame rate" };
        foreach (var f in new[] { 5, 10, 15, 20, 24, 30, 60 }) fpsBox.Items.Add(f + " fps");
        fpsBox.SelectedItem = s.RecordFps + " fps"; if (fpsBox.SelectedItem == null) fpsBox.SelectedIndex = 5;
        fpsBox.SelectionChanged += (_, _) => { if (fpsBox.SelectedItem is string t && int.TryParse(t.Split(' ')[0], out int v)) { s.RecordFps = v; SettingsService.Save(); } };
        row.Children.Add(fpsBox);
        delayBox = new ComboBox { Width = 76, Margin = new Thickness(6, 0, 0, 0), ToolTip = "Start delay" };
        foreach (var d in new[] { 0, 3, 5, 10 }) delayBox.Items.Add(d == 0 ? "No delay" : d + " s");
        delayBox.SelectedIndex = Math.Max(0, new[] { 0, 3, 5, 10 }.ToList().IndexOf(s.RecordDelay));
        delayBox.SelectionChanged += (_, _) => { s.RecordDelay = new[] { 0, 3, 5, 10 }[Math.Max(0, delayBox.SelectedIndex)]; SettingsService.Save(); };
        row.Children.Add(delayBox);
        row.Children.Add(Ui.VSeparator());
        sysBtn = Ui.IconToggle(Icons.Volume, "Record computer audio (MP4 only)", on => { s.RecordSystemAudio = on; SettingsService.Save(); });
        sysBtn.IsChecked = s.RecordSystemAudio;
        micBtn = Ui.IconToggle(Icons.Mic, "Record microphone (MP4 only)", on => { s.RecordMic = on; SettingsService.Save(); });
        micBtn.IsChecked = s.RecordMic;
        row.Children.Add(sysBtn); row.Children.Add(micBtn);
        row.Children.Add(Ui.VSeparator());
        timeText.Margin = new Thickness(4, 0, 8, 0); timeText.MinWidth = 44; row.Children.Add(timeText);
        pauseBtn = Ui.IconButton(Icons.Pause, "Pause / resume", TogglePause); pauseBtn.IsEnabled = false; row.Children.Add(pauseBtn);
        recBtn = Ui.IconButton(Icons.Record, "Start recording", ToggleRecord);
        recBtn.Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0x11, 0x23)); row.Children.Add(recBtn);
        bar.Content = row;
        RefreshAudioButtons();
    }

    void RefreshAudioButtons()
    {
        bool mp4 = SettingsService.Current.RecordFormat != "gif";
        sysBtn.IsEnabled = micBtn.IsEnabled = mp4 && !recording;
    }

    void ToggleRecord()
    {
        if (busy) return;
        if (countdown.IsEnabled) { countdown.Stop(); timeText.Text = "00:00"; return; }
        if (!recording)
        {
            int d = SettingsService.Current.RecordDelay;
            if (d > 0) { cd = d; timeText.Text = cd.ToString(); countdown.Start(); } else StartNow();
        }
        else _ = StopAsync();
    }

    void StartNow()
    {
        var s = SettingsService.Current; bool gif = s.RecordFormat == "gif";
        string path;
        try { path = SaveService.RecordingPath(ctx, gif ? "gif" : "mp4"); Directory.CreateDirectory(Path.GetDirectoryName(path)!); }
        catch (Exception ex) { ToastWindow.Show("Cannot create recording file: " + ex.Message, Icons.Info); return; }
        rec = new ScreenRecorder(region, s.RecordFps, s.RecordCursor, gif, s.GifLoop, s.Mp4Quality, path, s.RecordSystemAudio, s.RecordMic);
        try { rec.Start(); }
        catch (Exception ex) { SettingsService.Log("Recorder start failed: " + ex); ToastWindow.Show("Could not start recording: " + ex.Message, Icons.Info); rec = null; return; }
        recording = true;
        frame.SetColor(true); ui.Start();
        recBtn.Content = Icons.Stop; recBtn.ToolTip = "Stop recording"; pauseBtn.IsEnabled = true;
        gifChip.IsEnabled = mp4Chip.IsEnabled = fpsBox.IsEnabled = delayBox.IsEnabled = false;
        RefreshAudioButtons();
    }

    void TogglePause()
    {
        if (rec == null) return;
        if (rec.IsPaused) { rec.Resume(); pauseBtn.Content = Icons.Pause; frame.SetColor(true); }
        else { rec.Pause(); pauseBtn.Content = Icons.Play; frame.SetColor(false); }
    }

    async Task StopAsync()
    {
        busy = true; ui.Stop(); recBtn.IsEnabled = pauseBtn.IsEnabled = false; timeText.Text = "Saving…";
        string path = rec.Path; bool ok = true;
        try { await rec.StopAsync(); }
        catch (Exception ex) { ok = false; SettingsService.Log("Recording failed: " + ex); ToastWindow.Show("Recording failed: " + ex.Message, Icons.Info); }
        Finish();
        if (ok && File.Exists(path))
            ToastWindow.Show("Recording saved: " + Path.GetFileName(path), Icons.Video,
                ("Open", () => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })),
                ("Show in folder", () => CaptureActions.ShowInFolder(path)));
    }

    void Cancel()
    {
        if (busy) return;
        if (recording) { _ = StopAsync(); return; }     // never throw away a running recording
        countdown.Stop(); Finish();
    }

    async Task CancelRecording()
    {
        busy = true; string path = rec.Path;
        try { await rec.StopAsync(); } catch { }
        try { File.Delete(path); } catch { }
        Finish();
    }

    void Finish()
    {
        ui.Stop(); countdown.Stop();
        try { bar.Close(); } catch { }
        try { frame.Close(); } catch { }
        active = null;
    }
}

/// <summary>Scrolling screenshot: scroll the content yourself (or use auto-scroll) and the frames are stitched live.</summary>
public sealed class ScrollCaptureSession
{
    static ScrollCaptureSession active;
    readonly Rect region; readonly CaptureContext ctx;
    readonly RegionFrameWindow frame; readonly Window bar = new();
    readonly DispatcherTimer poll = new() { Interval = TimeSpan.FromMilliseconds(80) };
    readonly Image thumb = new() { MaxWidth = 110, MaxHeight = 120, Stretch = Stretch.Uniform, Margin = new Thickness(6, 0, 6, 0) };
    readonly TextBlock info = Ui.Label("Ready", 12, true);
    Button startBtn, pinBtn, saveBtn, copyBtn;
    ToggleButton vChip, hChip, autoChip;
    ScrollStitcher stitcher; bool capturing, horizontal, auto; int idleTicks, rw, rh;
    BitmapSource result;

    public static void Begin(Rect regionPx, CaptureContext ctx)
    {
        if (active != null) return;
        try { active = new ScrollCaptureSession(regionPx, ctx); }
        catch (Exception ex) { active = null; SettingsService.Log("Scroll session failed: " + ex); ToastWindow.Show("Could not start scrolling capture: " + ex.Message, Icons.Info); }
    }

    ScrollCaptureSession(Rect r, CaptureContext ctx)
    {
        region = r; this.ctx = ctx; rw = (int)r.Width; rh = (int)r.Height;
        horizontal = SettingsService.Current.ScrollDirection == "Horizontal";
        frame = new RegionFrameWindow(r); frame.Show();
        BuildBar();
        bar.Loaded += (_, _) => RegionBar.Place(bar, region);
        bar.Show();
        poll.Tick += (_, _) => Tick();
    }

    void BuildBar()
    {
        bar.WindowStyle = WindowStyle.None; bar.ResizeMode = ResizeMode.NoResize; bar.ShowInTaskbar = false; bar.Topmost = true;
        bar.SizeToContent = SizeToContent.WidthAndHeight; bar.WindowStartupLocation = WindowStartupLocation.Manual; bar.Left = -20000; bar.Top = -20000;
        bar.SourceInitialized += (_, _) => { WinHelper.ApplyChrome(bar, Backdrop.Acrylic); WinHelper.ExcludeFromCapture(bar); WinHelper.SwallowDpiChanges(bar); };
        bar.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 6, 8, 6) };
        row.Children.Add(Ui.IconButton(Icons.Close, "Close (Esc)", Close));
        row.Children.Add(Ui.VSeparator());
        vChip = Ui.Chip("Vertical", !horizontal, on => SetDir(false)); hChip = Ui.Chip("Horizontal", horizontal, on => SetDir(true));
        row.Children.Add(vChip); row.Children.Add(hChip);
        autoChip = Ui.Chip("Auto-scroll", false, on => auto = on); row.Children.Add(autoChip);
        row.Children.Add(Ui.VSeparator());
        startBtn = Ui.IconButton(Icons.Play, "Start capturing - then scroll the content", ToggleCapture); row.Children.Add(startBtn);
        row.Children.Add(thumb); row.Children.Add(info);
        row.Children.Add(Ui.VSeparator());
        pinBtn = Ui.IconButton(Icons.Pin, "Pin", () => Output(o => CaptureActions.Pin(o, new Rect(region.X, region.Y, o.PixelWidth, o.PixelHeight), ctx), true));
        saveBtn = Ui.IconButton(Icons.Save, "Save", () => Output(o => CaptureActions.Save(o, ctx, bar), false));
        copyBtn = Ui.IconButton(Icons.Copy, "Copy", () => Output(o => CaptureActions.Copy(o, ctx), true));
        pinBtn.IsEnabled = saveBtn.IsEnabled = copyBtn.IsEnabled = false;
        row.Children.Add(pinBtn); row.Children.Add(saveBtn); row.Children.Add(copyBtn);
        bar.Content = row;
    }

    void SetDir(bool h)
    {
        if (capturing) { vChip.IsChecked = !horizontal; hChip.IsChecked = horizontal; return; }
        horizontal = h; vChip.IsChecked = !h; hChip.IsChecked = h;
        SettingsService.Current.ScrollDirection = h ? "Horizontal" : "Vertical"; SettingsService.Save();
    }

    byte[] Grab()
    {
        int w = rw, h = rh;
        IntPtr sdc = NativeMethods.GetDC(IntPtr.Zero), mdc = NativeMethods.CreateCompatibleDC(sdc);
        var bmi = NativeMethods.MakeBmi(w, h);
        IntPtr hbm = NativeMethods.CreateDIBSection(sdc, ref bmi, 0, out IntPtr bits, IntPtr.Zero, 0);
        IntPtr old = NativeMethods.SelectObject(mdc, hbm);
        NativeMethods.BitBlt(mdc, 0, 0, w, h, sdc, (int)region.X, (int)region.Y, NativeMethods.SRCCOPY | NativeMethods.CAPTUREBLT);
        var data = new byte[w * h * 4]; Marshal.Copy(bits, data, 0, data.Length);
        NativeMethods.SelectObject(mdc, old); NativeMethods.DeleteObject(hbm); NativeMethods.DeleteDC(mdc); NativeMethods.ReleaseDC(IntPtr.Zero, sdc);
        return data;
    }

    void ToggleCapture()
    {
        if (!capturing)
        {
            stitcher = horizontal ? new ScrollStitcher(rh, rw, 20000) : new ScrollStitcher(rw, rh, 20000);
            capturing = true; idleTicks = 0; result = null;
            frame.SetColor(true); startBtn.Content = Icons.Stop; startBtn.ToolTip = "Stop";
            vChip.IsEnabled = hChip.IsEnabled = false; pinBtn.IsEnabled = saveBtn.IsEnabled = copyBtn.IsEnabled = false;
            if (auto) NativeMethods.SetCursorPos((int)(region.X + region.Width / 2), (int)(region.Y + region.Height / 2));
            Tick(); poll.Start();
        }
        else StopCapture();
    }

    void Tick()
    {
        if (!capturing) return;
        var f = Grab(); if (horizontal) f = ScrollStitcher.Transpose(f, rw, rh);
        bool grew = stitcher.Add(f);
        idleTicks = grew ? 0 : idleTicks + 1;
        UpdatePreview();
        info.Text = stitcher.Full ? "Size limit reached" : !stitcher.LastMatchOk ? "No match - scroll back a little" : horizontal ? $"{stitcher.TotalRows} × {rh}" : $"{rw} × {stitcher.TotalRows}";
        info.Foreground = stitcher.LastMatchOk ? (Brush)Application.Current.FindResource("TextSecondaryBrush") : Brushes.OrangeRed;
        if (auto && !stitcher.Full) SendWheel();
        if (stitcher.Full || (auto && idleTicks > 14 && stitcher.TotalRows > (horizontal ? rw : rh))) StopCapture();
    }

    void SendWheel()
    {
        var inp = new INPUT[1];
        inp[0].type = 0; inp[0].mi.mouseData = horizontal ? 120 : -120; inp[0].mi.dwFlags = horizontal ? 0x1000 : 0x0800;
        NativeMethods.SendInput(1, inp, Marshal.SizeOf<INPUT>());
    }

    void UpdatePreview()
    {
        if (stitcher == null || !stitcher.HasContent) return;
        var t = stitcher.Thumb(horizontal ? 260 : 110, horizontal ? 120 : 110, out int tw, out int th);
        int w = tw, h = th;
        if (horizontal) { t = ScrollStitcher.Transpose(t, tw, th); w = th; h = tw; }
        var bs = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr32, null, t, w * 4); bs.Freeze(); thumb.Source = bs;
    }

    void StopCapture()
    {
        if (!capturing) return;
        capturing = false; poll.Stop();
        frame.SetColor(false); startBtn.Content = Icons.Play; startBtn.ToolTip = "Capture again";
        vChip.IsEnabled = hChip.IsEnabled = true;
        if (stitcher == null || !stitcher.HasContent) return;
        var data = stitcher.Compose(); int rows = stitcher.TotalRows; int w, h;
        if (horizontal) { data = ScrollStitcher.Transpose(data, rh, rows); w = rows; h = rh; } else { w = rw; h = rows; }
        result = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr32, null, data, w * 4); result.Freeze();
        ctx.Width = w; ctx.Height = h; info.Text = $"{w} × {h}";
        pinBtn.IsEnabled = saveBtn.IsEnabled = copyBtn.IsEnabled = true;
    }

    void Output(Action<BitmapSource> act, bool closeAfter)
    {
        if (result == null) return;
        var styled = Imaging.ApplyOutputStyle(result, out _);
        act(styled);
        if (closeAfter) Close();
    }

    void Close()
    {
        poll.Stop();
        try { bar.Close(); } catch { }
        try { frame.Close(); } catch { }
        active = null;
    }
}
