using System.Diagnostics;
using System.Windows.Media.Imaging;
using FluentSnip.Core;
using FluentSnip.Views;

namespace FluentSnip.Services;

/// <summary>What happens to a finished capture: copy, save, quick-save, pin, OCR.</summary>
public static class CaptureActions
{
    static AppSettings Cfg => SettingsService.Current;

    public static void ShowInFolder(string path)
    {
        try { Process.Start("explorer.exe", $"/select,\"{path}\""); } catch { }
    }

    static string AutoSaveIfEnabled(BitmapSource img, CaptureContext ctx)
    {
        if (!Cfg.AutoSave) return null;
        try { return SaveService.SaveDirect(img, ctx); }
        catch (Exception ex) { SettingsService.Log("Auto-save failed: " + ex.Message); return null; }
    }

    public static void Copy(BitmapSource img, CaptureContext ctx)
    {
        Imaging.CopyToClipboard(img);
        string saved = AutoSaveIfEnabled(img, ctx);
        if (saved != null) ToastWindow.Show("Copied and saved", Icons.Accept, ("Show in folder", () => ShowInFolder(saved)));
        else ToastWindow.Show("Copied to clipboard");
    }

    /// <summary>Manual save (dialog or direct, according to settings). Returns false when the user cancelled.</summary>
    public static bool Save(BitmapSource img, CaptureContext ctx, Window owner)
    {
        try
        {
            string path = Cfg.AskWhereToSave ? SaveService.SaveWithDialog(img, ctx, owner) : SaveService.SaveDirect(img, ctx);
            if (path == null) return false;
            if (Cfg.CopyOnSave) Imaging.CopyToClipboard(img);
            ToastWindow.Show("Saved " + System.IO.Path.GetFileName(path), Icons.Save, ("Show in folder", () => ShowInFolder(path)));
            return true;
        }
        catch (Exception ex) { ToastWindow.Show("Save failed: " + ex.Message, Icons.Info); return false; }
    }

    public static void QuickSave(BitmapSource img, CaptureContext ctx)
    {
        try
        {
            string path = SaveService.SaveDirect(img, ctx);
            if (Cfg.CopyOnSave) Imaging.CopyToClipboard(img);
            ToastWindow.Show("Saved " + System.IO.Path.GetFileName(path), Icons.Save, ("Show in folder", () => ShowInFolder(path)));
        }
        catch (Exception ex) { ToastWindow.Show("Save failed: " + ex.Message, Icons.Info); }
    }

    public static void Pin(BitmapSource img, Rect screenPx, CaptureContext ctx)
    {
        try { PinManager.PinImage(img, screenPx, ctx); }
        catch (Exception ex) { SettingsService.Log("Pin failed: " + ex); ToastWindow.Show("Pin failed: " + ex.Message, Icons.Info); return; }
        AutoSaveIfEnabled(img, ctx);
    }

    public static async Task Ocr(BitmapSource img, bool copyDirect)
    {
        try
        {
            ToastWindow.Show("Recognizing text…", Icons.Ocr);
            string text = await OcrService.RecognizeAsync(img);
            ToastWindow.CloseCurrent();
            if (string.IsNullOrWhiteSpace(text)) { ToastWindow.Show("No text recognized", Icons.Info); return; }
            if (copyDirect) { Clipboard.SetText(text); ToastWindow.Show($"Copied {text.Length} characters", Icons.Ocr); }
            else new ResultWindow("Recognized text", Icons.Ocr, text, false).Show();
        }
        catch (Exception ex) { SettingsService.Log("OCR failed: " + ex); ToastWindow.Show("OCR failed: " + ex.Message, Icons.Info); }
    }

    public static void Qr(BitmapSource img)
    {
        var hits = QrService.Scan(img);
        if (hits.Count == 0) { ToastWindow.Show("No QR code found", Icons.Info); return; }
        new ResultWindow("QR code", Icons.Qr, string.Join(Environment.NewLine, hits.Select(h => h.Text)), true).Show();
    }
}
