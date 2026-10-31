using System.Text.Json;
using Microsoft.Win32;

namespace FluentSnip.Core;

/// <summary>Hotkey ids + defaults (defaults mirror PixPin where it has an equivalent).</summary>
public static class HK
{
    public const string Capture = "Capture", Record = "Record", PinClipboard = "PinClipboard",
        ToggleHide = "ToggleHidePins", Penetrate = "TogglePenetration",
        Settings = "OpenSettings";

    public static readonly (string Id, string Label, string Default)[] All =
    {
        (Capture, "Screenshot", "Ctrl+1"),
        (Record, "Record screen area", "Ctrl+Shift+1"),
        (PinClipboard, "Pin clipboard content", "Ctrl+2"),
        (ToggleHide, "Hide / show all pins", "Ctrl+Shift+2"),
        (Penetrate, "Toggle mouse pass-through (pin under cursor)", "Ctrl+Alt+P"),
        (Settings, "Open settings", ""),
    };
}

public sealed class AppSettings
{
    // ---- general ----
    public string Theme { get; set; } = "System";            // System | Light | Dark
    public bool StartWithWindows { get; set; }
    public bool ShowTrayIcon { get; set; } = true;
    public string TrayClickAction { get; set; } = "Capture"; // Capture | Settings | None
    public bool Notifications { get; set; } = true;
    public Dictionary<string, string> Hotkeys { get; set; } = new();

    // ---- capture ----
    public bool ShowCursor { get; set; }
    public bool DetectUiElements { get; set; } = true;
    public int DimOpacity { get; set; } = 55;                 // 0-90 %
    public bool ShowCrosshair { get; set; } = true;
    public bool ShowMagnifier { get; set; } = true;
    public string MagnifierColorFormat { get; set; } = "RGB"; // RGB | HEX | HSV | HSL
public int MagnifierZoom { get; set; } = 8;                  // 2-16
public bool MagnifierGrid { get; set; } = true;
    public bool RoundedCorners { get; set; }
    public int CornerRadius { get; set; } = 12;
    public bool OutputBorder { get; set; }
    public bool OutputShadow { get; set; }
    public string ScrollDirection { get; set; } = "Vertical";

    // ---- save ----
    public string ScreenshotFolder { get; set; } = "";
    public string ScreenshotTemplate { get; set; } = "FluentCapture_$yyyy-MM-dd_HH-mm-ss$";
    public string ImageFormat { get; set; } = "png";          // png | jpg | bmp
    public int JpegQuality { get; set; } = 90;
    public bool AskWhereToSave { get; set; } = true;           // Save button / Ctrl+S shows dialog
    public bool AutoSave { get; set; }                         // save a file on every copy / pin / save
    public bool CopyOnSave { get; set; }
    public int Counter { get; set; }

    // ---- recording ----
    public string RecordingFolder { get; set; } = "";
    public string RecordingTemplate { get; set; } = "FluentCapture_$yyyy-MM-dd_HH-mm-ss$";
    public string RecordFormat { get; set; } = "mp4";          // mp4 | gif
    public int RecordFps { get; set; } = 30;
    public int RecordDelay { get; set; }
    public bool RecordCursor { get; set; } = true;
    public bool RecordSystemAudio { get; set; }
    public bool RecordMic { get; set; }
    public string Mp4Quality { get; set; } = "High";           // Low | Medium | High
    public bool GifLoop { get; set; } = true;

    // ---- pin ----
    public bool PinShadowBorder { get; set; } = true;
    public int PinDefaultOpacity { get; set; } = 100;
    public int PinZoomStep { get; set; } = 10;
    

    // ---- annotate ----
    public string AnnotColor { get; set; } = "#E81123";
    public double AnnotSize { get; set; } = 3;
    public double TextSize { get; set; } = 20;

    // ---- watermark ----
    public bool WatermarkEnabled { get; set; }
    public string WatermarkText { get; set; } = "FluentCapture";
    public int WatermarkOpacity { get; set; } = 25;

    // ---- OCR ----
    public string OcrLanguage { get; set; } = "";              // "" = user profile languages
}

public static class SettingsService
{
    public static AppSettings Current { get; private set; } = new();
    public static string Dir { get; }
    public static string FilePath => Path.Combine(Dir, "settings.json");

    /// <summary>Settings, log and all app state live next to FluentSnip.exe - nothing is written to AppData or the registry
    /// (the optional "Start with Windows" entry is the only exception and is off by default).</summary>
    public static bool IsWritable { get; private set; } = true;

    static SettingsService()
    {
        Dir = AppContext.BaseDirectory.TrimEnd('\\', '/');
        try
        {
            Directory.CreateDirectory(Dir);
            string probe = Path.Combine(Dir, ".write-test");
            File.WriteAllText(probe, "x"); File.Delete(probe);
        }
        catch { IsWritable = false; }
    }

    public static void Load()
    {
        try
        {
            if (File.Exists(FilePath))
                Current = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch (Exception ex) { Log("Settings load failed: " + ex.Message); Current = new AppSettings(); }

        foreach (var (id, _, def) in HK.All)
            if (!Current.Hotkeys.ContainsKey(id)) Current.Hotkeys[id] = def;
        if (string.IsNullOrWhiteSpace(Current.ScreenshotFolder))
            Current.ScreenshotFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "FluentCapture");
        if (string.IsNullOrWhiteSpace(Current.RecordingFolder))
            Current.RecordingFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "FluentCapture");
    }

    public static void Save()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(Current, new JsonSerializerOptions { WriteIndented = true })); }
        catch (Exception ex) { Log("Settings save failed: " + ex.Message); }
    }

    public static void ApplyStartup()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
            if (key == null) return;
            if (Current.StartWithWindows) key.SetValue("FluentCapture", "\"" + Environment.ProcessPath + "\"");
            else key.DeleteValue("FluentCapture", false);
        }
        catch (Exception ex) { Log("Startup registration failed: " + ex.Message); }
    }

    public static void Log(string msg)
    {
        try { File.AppendAllText(Path.Combine(Dir, "log.txt"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {msg}{Environment.NewLine}"); } catch { }
    }
}
