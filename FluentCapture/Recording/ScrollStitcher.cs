using System.Runtime.InteropServices;

namespace FluentSnip.Recording;

/// <summary>
/// Stitches successive captures of a scrolling region into one long image by matching row hashes.
/// Always works in "vertical" space; horizontal captures are transposed by the caller.
/// </summary>
public sealed class ScrollStitcher
{
    readonly int W, H, maxRows;
    byte[] prev; ulong[] prevHash;
    readonly List<byte[]> bands = new();
    readonly List<int> rows = new();
    public int TotalRows { get; private set; }
    public bool LastMatchOk { get; private set; } = true;
    public bool Full { get; private set; }
    public bool HasContent => prev != null;

    public ScrollStitcher(int w, int h, int maxRows) { W = w; H = h; this.maxRows = maxRows; }

    ulong[] Hashes(byte[] f)
    {
        var hs = new ulong[H];
        int x0 = (int)(W * 0.08), x1 = (int)(W * 0.92);
        var px = MemoryMarshal.Cast<byte, uint>(f);
        for (int y = 0; y < H; y++)
        {
            ulong hash = 1469598103934665603UL; int o = y * W;
            for (int x = x0; x < x1; x++) { hash ^= px[o + x]; hash *= 1099511628211UL; }
            hs[y] = hash;
        }
        return hs;
    }

    /// <summary>Returns true when new content was appended/prepended.</summary>
    public bool Add(byte[] frame)
    {
        var hn = Hashes(frame);
        if (prev == null)
        {
            bands.Add((byte[])frame.Clone()); rows.Add(H); TotalRows = H; prev = frame; prevHash = hn; return true;
        }
        bool same = true;
        for (int i = 0; i < H; i++) if (prevHash[i] != hn[i]) { same = false; break; }
        if (same) return false;

        if (!Match(prevHash, hn, out int s, out bool down)) { LastMatchOk = false; return false; }
        LastMatchOk = true;
        if (TotalRows + s > maxRows) { Full = true; return false; }

        var band = new byte[s * W * 4];
        if (down) { Buffer.BlockCopy(frame, (H - s) * W * 4, band, 0, band.Length); bands.Add(band); rows.Add(s); }
        else { Buffer.BlockCopy(frame, 0, band, 0, band.Length); bands.Insert(0, band); rows.Insert(0, s); }
        TotalRows += s; prev = frame; prevHash = hn;
        return true;
    }

    bool Match(ulong[] hp, ulong[] hn, out int shift, out bool down)
    {
        shift = 0; down = true;
        var counts = new Dictionary<ulong, int>();
        foreach (var h in hn) counts[h] = counts.GetValueOrDefault(h) + 1;
        ulong blank = counts.OrderByDescending(k => k.Value).First().Key;
        if (counts[blank] < H / 5) blank = 0;      // only treat a hash as "blank" if it is really dominant

        int minOverlap = Math.Max(24, H / 6);
        double best = 0.0;
        for (int dir = 0; dir < 2; dir++)
        {
            bool d = dir == 0;
            for (int s = 1; s <= H - minOverlap; s++)
            {
                int overlap = H - s, info = 0, ok = 0;
                for (int k = 0; k < overlap; k++)
                {
                    ulong a = d ? hp[s + k] : hp[k], b = d ? hn[k] : hn[s + k];
                    if (a == blank && b == blank) continue;
                    info++; if (a == b) ok++;
                }
                if (info < 6) continue;
                double r = ok / (double)info;
                if (r >= 0.92 && (r > best + 1e-9)) { best = r; shift = s; down = d; }
            }
        }
        return shift > 0;
    }

    public byte[] Compose()
    {
        var o = new byte[TotalRows * W * 4]; int pos = 0;
        foreach (var b in bands) { Buffer.BlockCopy(b, 0, o, pos, b.Length); pos += b.Length; }
        return o;
    }

    /// <summary>Down-sampled preview (width tw, height up to maxH) in stitch space.</summary>
    public byte[] Thumb(int maxW, int maxH, out int tw, out int th)
    {
        double sc = Math.Min(maxW / (double)W, maxH / (double)Math.Max(1, TotalRows));
        sc = Math.Min(sc, 1);
        tw = Math.Max(1, (int)(W * sc)); th = Math.Max(1, (int)(TotalRows * sc));
        var o = new byte[tw * th * 4];
        int bandIdx = 0, bandStart = 0;
        for (int y = 0; y < th; y++)
        {
            int sy = Math.Min(TotalRows - 1, (int)(y / sc));
            while (bandIdx < bands.Count - 1 && sy >= bandStart + rows[bandIdx]) { bandStart += rows[bandIdx]; bandIdx++; }
            var b = bands[bandIdx]; int ry = sy - bandStart;
            for (int x = 0; x < tw; x++)
            {
                int sx = Math.Min(W - 1, (int)(x / sc)); int so = (ry * W + sx) * 4, d = (y * tw + x) * 4;
                o[d] = b[so]; o[d + 1] = b[so + 1]; o[d + 2] = b[so + 2]; o[d + 3] = 255;
            }
        }
        return o;
    }

    public static byte[] Transpose(byte[] src, int sw, int sh)
    {
        var o = new byte[src.Length];
        for (int y = 0; y < sh; y++)
            for (int x = 0; x < sw; x++)
            {
                int s = (y * sw + x) * 4, d = (x * sh + y) * 4;
                o[d] = src[s]; o[d + 1] = src[s + 1]; o[d + 2] = src[s + 2]; o[d + 3] = src[s + 3];
            }
        return o;
    }
}
