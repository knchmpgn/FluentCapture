using System.Diagnostics;
using System.Windows.Automation;

namespace FluentSnip.Capture;

/// <summary>
/// Finds the nested UI element rectangles under a screen point by descending the UI Automation tree of a
/// top-level window (avoids hit-testing the capture overlay itself).
/// </summary>
public static class UiaProbe
{
    /// <summary>Returns rects from the window down to the deepest element (screen pixel coordinates).</summary>
    public static List<Rect> Probe(IntPtr hwnd, Point screenPt, CancellationToken ct, int budgetMs = 220)
    {
        var chain = new List<Rect>();
        try
        {
            var sw = Stopwatch.StartNew();
            var walker = TreeWalker.ControlViewWalker;
            var cur = AutomationElement.FromHandle(hwnd);
            for (int depth = 0; depth < 24; depth++)
            {
                AutomationElement next = null; Rect nextRect = Rect.Empty;
                var child = walker.GetFirstChild(cur);
                int count = 0;
                while (child != null && count++ < 600)
                {
                    if (ct.IsCancellationRequested || sw.ElapsedMilliseconds > budgetMs) return chain;
                    Rect r;
                    try { r = child.Current.BoundingRectangle; } catch { r = Rect.Empty; }
                    if (!r.IsEmpty && r.Width > 1 && r.Height > 1 && r.Contains(screenPt)) { next = child; nextRect = r; }   // later siblings are on top
                    try { child = walker.GetNextSibling(child); } catch { break; }
                }
                if (next == null) break;
                if (chain.Count == 0 || chain[^1] != nextRect) chain.Add(nextRect);
                cur = next;
            }
        }
        catch { }
        return chain;
    }
}
