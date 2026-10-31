using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FluentSnip.Core;

/// <summary>Describes what was captured so that naming templates can use title/process etc.</summary>
public sealed class CaptureContext
{
    public string Title = "";
    public string Process = "";
    public int Width, Height;
    public DateTime Time = DateTime.Now;
}

/// <summary>
/// Save locations + naming conventions.
/// Template syntax (modelled on PixPin):  $yyyy-MM-dd_HH-mm-ss$  = date/time (.NET custom format),
/// %title% %process% %computername% %username% %os% %width% %height% %counter%,
/// modifiers: %title#10% (first 10 chars)  %process!% (strip ".exe").
/// </summary>
public static class SaveService
{
    static readonly Regex DateRx = new(@"\$([^$]+)\$", RegexOptions.Compiled);
    static readonly Regex VarRx = new(@"%(?<n>[a-zA-Z]+)(?:#(?<len>\d+))?(?<bang>!)?%", RegexOptions.Compiled);

    public static string Expand(string template, CaptureContext ctx, DateTime? now = null)
    {
        var t = now ?? ctx.Time;
        string s = DateRx.Replace(template ?? "", m =>
        {
            try { return t.ToString(m.Groups[1].Value, CultureInfo.InvariantCulture); } catch { return m.Value; }
        });
        s = VarRx.Replace(s, m =>
        {
            string v = m.Groups["n"].Value.ToLowerInvariant() switch
            {
                "title" => ctx.Title,
                "process" => ctx.Process,
                "computername" => Environment.MachineName,
                "username" => Environment.UserName,
                "os" => "windows",
                "width" => ctx.Width.ToString(),
                "height" => ctx.Height.ToString(),
                "counter" => PeekCounter().ToString("0000"),
                _ => m.Value
            };
            if (m.Groups["bang"].Success && v.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) v = v[..^4];
            if (m.Groups["len"].Success && int.TryParse(m.Groups["len"].Value, out int len) && v.Length > len) v = v[..len];
            return v;
        });
        return s;
    }

    static int PeekCounter() => SettingsService.Current.Counter + 1;

    static string Sanitize(string seg)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) seg = seg.Replace(c, '_');
        seg = seg.Trim().TrimEnd('.');
        return seg.Length == 0 ? "_" : (seg.Length > 120 ? seg[..120] : seg);
    }

    /// <summary>Builds the full unique target path (does not create the file).</summary>
    public static string BuildPath(string folderTemplate, string nameTemplate, string ext, CaptureContext ctx)
    {
        string folder = Expand(folderTemplate, ctx);
        string root = "";
        if (folder.Length >= 2 && folder[1] == ':') { root = folder[..2] + "\\"; folder = folder[2..]; }
        else if (folder.StartsWith("\\\\")) { root = "\\\\"; folder = folder[2..]; }
        var segs = folder.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries).Select(Sanitize);
        string dir = root + string.Join("\\", segs);
        if (dir.Length == 0) dir = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);

        string name = Sanitize(Expand(nameTemplate, ctx));
        if (nameTemplate.Contains("%counter%", StringComparison.OrdinalIgnoreCase)) { SettingsService.Current.Counter++; SettingsService.Save(); }
        string path = Path.Combine(dir, name + "." + ext);
        for (int i = 2; File.Exists(path); i++) path = Path.Combine(dir, $"{name} ({i}).{ext}");
        return path;
    }

    public static string ScreenshotPath(CaptureContext ctx, string ext = null)
    {
        var s = SettingsService.Current;
        return BuildPath(s.ScreenshotFolder, s.ScreenshotTemplate, ext ?? s.ImageFormat, ctx);
    }

    public static string RecordingPath(CaptureContext ctx, string ext)
    {
        var s = SettingsService.Current;
        return BuildPath(s.RecordingFolder, s.RecordingTemplate, ext, ctx);
    }

    public static string TemplatePreview(string folder, string template, string ext)
    {
        var ctx = new CaptureContext { Title = "Document1 - Word", Process = "WINWORD.EXE", Width = 1280, Height = 720 };
        try
        {
            int saved = SettingsService.Current.Counter;
            var p = BuildPath(folder, template, ext, ctx);
            SettingsService.Current.Counter = saved;
            return p;
        }
        catch { return "(invalid template)"; }
    }

    public static void WriteImage(BitmapSource img, string path, string fmt, int quality)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        BitmapEncoder enc;
        switch (fmt.ToLowerInvariant())
        {
            case "jpg": case "jpeg": enc = new JpegBitmapEncoder { QualityLevel = Math.Clamp(quality, 1, 100) }; img = Imaging.Flatten(img, Colors.White); break;
            case "bmp": enc = new BmpBitmapEncoder(); img = Imaging.Flatten(img, Colors.White); break;
            default: enc = new PngBitmapEncoder(); break;
        }
        enc.Frames.Add(BitmapFrame.Create(img));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    /// <summary>Saves straight to the configured folder using the naming template.</summary>
    public static string SaveDirect(BitmapSource img, CaptureContext ctx)
    {
        var s = SettingsService.Current;
        string path = ScreenshotPath(ctx);
        WriteImage(img, path, s.ImageFormat, s.JpegQuality);
        return path;
    }

    /// <summary>Shows a Save As dialog starting in the configured folder with the templated name.</summary>
    public static string SaveWithDialog(BitmapSource img, CaptureContext ctx, Window owner)
    {
        var s = SettingsService.Current;
        string suggested = ScreenshotPath(ctx);
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save screenshot",
            InitialDirectory = Path.GetDirectoryName(suggested),
            FileName = Path.GetFileName(suggested),
            Filter = "PNG image|*.png|JPEG image|*.jpg|Bitmap image|*.bmp",
            FilterIndex = s.ImageFormat switch { "jpg" => 2, "bmp" => 3, _ => 1 },
            AddExtension = true, OverwritePrompt = true
        };
        bool ok = owner != null ? dlg.ShowDialog(owner) == true : dlg.ShowDialog() == true;
        if (!ok) return null;
        string ext = Path.GetExtension(dlg.FileName).TrimStart('.').ToLowerInvariant();
        WriteImage(img, dlg.FileName, ext, s.JpegQuality);
        if (ext is "png" or "jpg" or "bmp") { s.ImageFormat = ext; SettingsService.Save(); }
        return dlg.FileName;
    }
}
