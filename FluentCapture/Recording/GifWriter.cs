namespace FluentSnip.Recording;

/// <summary>Streaming GIF89a writer: per-frame adaptive 256-color palette, LZW, real frame delays.</summary>
public sealed class GifWriter : IDisposable
{
    readonly Stream s; readonly int w, h; readonly bool loop;
    bool headerWritten; double carryCs;
    readonly int[] hist = new int[32768];
    readonly byte[] map = new byte[32768];

    public GifWriter(string path, int w, int h, bool loop)
    {
        s = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16);
        this.w = w; this.h = h; this.loop = loop;
    }

    void Header()
    {
        headerWritten = true;
        s.Write("GIF89a"u8);
        W16(w); W16(h); s.WriteByte(0x70); s.WriteByte(0); s.WriteByte(0);
        if (loop)
        {
            s.Write(new byte[] { 0x21, 0xFF, 0x0B });
            s.Write("NETSCAPE2.0"u8);
            s.Write(new byte[] { 0x03, 0x01, 0x00, 0x00, 0x00 });
        }
    }

    void W16(int v) { s.WriteByte((byte)(v & 255)); s.WriteByte((byte)(v >> 8)); }

    public void AddFrame(byte[] bgra, TimeSpan duration)
    {
        if (!headerWritten) Header();
        double cs = duration.TotalSeconds * 100 + carryCs;
        int delay = Math.Max(2, (int)Math.Round(cs)); carryCs = cs - delay;

        // ---- palette: 255 most common 15-bit colors (+ nearest mapping for the rest) ----
        Array.Clear(hist);
        int n = w * h;
        for (int i = 0; i < n; i += 2)
        {
            int o = i * 4;
            hist[((bgra[o + 2] >> 3) << 10) | ((bgra[o + 1] >> 3) << 5) | (bgra[o] >> 3)]++;
        }
        var keys = new List<int>();
        for (int k = 0; k < hist.Length; k++) if (hist[k] > 0) keys.Add(k);
        keys.Sort((a, b) => hist[b].CompareTo(hist[a]));
        int pc = Math.Min(256, keys.Count);
        var pal = new byte[768]; var pr = new int[256]; var pg = new int[256]; var pb = new int[256];
        for (int i = 0; i < pc; i++)
        {
            int k = keys[i]; int r = ((k >> 10) & 31) << 3 | 4, g = ((k >> 5) & 31) << 3 | 4, b = (k & 31) << 3 | 4;
            pr[i] = r; pg[i] = g; pb[i] = b; pal[i * 3] = (byte)r; pal[i * 3 + 1] = (byte)g; pal[i * 3 + 2] = (byte)b;
            map[k] = (byte)i; hist[k] = -1;               // -1 = resolved
        }
        var idx = new byte[n];
        for (int i = 0; i < n; i++)
        {
            int o = i * 4; int k = ((bgra[o + 2] >> 3) << 10) | ((bgra[o + 1] >> 3) << 5) | (bgra[o] >> 3);
            if (hist[k] != -1)
            {
                int r = bgra[o + 2], g = bgra[o + 1], b = bgra[o], best = 0, bd = int.MaxValue;
                for (int p = 0; p < pc; p++)
                {
                    int dr = r - pr[p], dg = g - pg[p], db = b - pb[p], d = dr * dr * 3 + dg * dg * 4 + db * db * 2;
                    if (d < bd) { bd = d; best = p; }
                }
                map[k] = (byte)best; hist[k] = -1;
            }
            idx[i] = map[k];
        }

        // graphic control extension (disposal = leave in place)
        s.Write(new byte[] { 0x21, 0xF9, 0x04, 0x04 }); W16(delay); s.WriteByte(0); s.WriteByte(0);
        s.WriteByte(0x2C); W16(0); W16(0); W16(w); W16(h); s.WriteByte(0x87);   // local color table, 256 entries
        s.Write(pal);
        s.WriteByte(8);
        Lzw(idx, n);
        s.WriteByte(0);
    }

    void Lzw(byte[] px, int count)
    {
        const int min = 8; int clear = 1 << min, eoi = clear + 1, next = eoi + 1, size = min + 1;
        var dict = new Dictionary<int, int>(8192);
        var bw = new BitWriter(s);
        bw.Write(clear, size);
        int prefix = px[0];
        for (int i = 1; i < count; i++)
        {
            int k = px[i], key = (prefix << 8) | k;
            if (dict.TryGetValue(key, out int code)) { prefix = code; continue; }
            bw.Write(prefix, size);
            if (next >= (1 << size) && size < 12) size++;
            if (next < 4096) dict[key] = next++;
            else { bw.Write(clear, size); dict.Clear(); next = eoi + 1; size = min + 1; }
            prefix = k;
        }
        bw.Write(prefix, size);
        bw.Write(eoi, size);
        bw.Flush();
    }

    sealed class BitWriter
    {
        readonly Stream s; readonly byte[] block = new byte[255]; int len, bits, nbits;
        public BitWriter(Stream s) { this.s = s; }
        public void Write(int code, int size)
        {
            bits |= code << nbits; nbits += size;
            while (nbits >= 8) { Put((byte)(bits & 255)); bits >>= 8; nbits -= 8; }
        }
        void Put(byte b) { block[len++] = b; if (len == 255) FlushBlock(); }
        void FlushBlock() { if (len == 0) return; s.WriteByte((byte)len); s.Write(block, 0, len); len = 0; }
        public void Flush() { if (nbits > 0) Put((byte)(bits & 255)); bits = 0; nbits = 0; FlushBlock(); }
    }

    public void Dispose()
    {
        if (!headerWritten) Header();
        s.WriteByte(0x3B); s.Flush(); s.Dispose();
    }
}
