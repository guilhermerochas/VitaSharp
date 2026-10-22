using System.Text;

namespace VitaSharp.Core.Hle.Font;

/// <summary>Decoded PGF glyph: bitmap (one 0..15 value per pixel, row-major) plus the metrics libpgf reports.</summary>
internal sealed class PgfGlyph
{
    public int Width, Height, Left, Top, Flags, ShadowFlags, ShadowId;
    /// <summary>26.6 metrics: dimension (width/height), bearings (x/y adjust, H and V) and advances.</summary>
    public int DimW, DimH, XAdjH, XAdjV, YAdjH, YAdjV, AdvH, AdvV;
    public byte[] Pixels = [];
    /// <summary>Overlay glyphs (bitmap layout 3, revision 3 Hangul): character codes of the parts drawn on top of
    /// each other, each at its own left/top relative to this glyph's. Null for ordinary bitmaps.</summary>
    public int[]? Components;
}

/// <summary>
/// PGF bitmap font (the PSP libfont format, used unchanged by the Vita's libpgf and the firmware fonts in
/// sa0:data/font/pgf). Layout checked against ltn0/jpn0/kr0/arib from a firmware dump and against libpgf itself:
/// header (0x188 bytes, +0x14 in revision 3), four (H, V) metric tables, the shadow charmap, the revision-3 range
/// tables, the charmap and the glyph pointer table (all LSB-first bit-packed, each table 4-byte aligned), then the
/// glyphs. Glyph bitmaps are 4 bpp, nibble RLE.
/// </summary>
internal sealed class PgfFont
{
    public int Revision, Version, CharMapLength, CharPointerLength, CharMapBpe, CharPointerBpe, Bpp;
    /// <summary>Nominal size and resolution, 26.6 (0x288 = 10.125 pt, 0x2000 = 128 dpi).</summary>
    public int HSize, VSize, HResolution, VResolution;
    public string FontName = "", FontType = "";
    public int FirstGlyph, LastGlyph;
    public int MaxAscender, MaxDescender, MaxLeftXAdjust, MaxBaseYAdjust, MinCenterXAdjust, MaxTopYAdjust;
    public int MaxAdvanceH, MaxAdvanceV, MaxSizeW, MaxSizeH, MaxGlyphWidth, MaxGlyphHeight;
    public int ShadowMapLength, ShadowMapBpe;
    /// <summary>Revision 3: number of parts in an overlay glyph (header 0x198; 3 in kr0).</summary>
    public int ComponentCount;

    readonly byte[] _d;
    (int H, int V)[] _dim = [], _xAdj = [], _yAdj = [], _adv = [];
    /// <summary>Revision 3: charmap ranges (start, count) and ranges of internal glyphs that are never exposed.</summary>
    (int Start, int Count)[] _ranges = [], _hidden = [];
    long _charMapBit, _charPtrBit;
    int _glyphBase, _ptrScale;
    readonly Dictionary<int, PgfGlyph?> _cache = new();

    PgfFont(byte[] data) => _d = data;

    /// <summary>Parses the header and tables; null when the data is not a usable PGF font.</summary>
    public static PgfFont? Parse(byte[] data)
    {
        try { return ParseCore(data); }
        catch (IndexOutOfRangeException) { return null; }
        catch (ArgumentException) { return null; }
    }

    static PgfFont? ParseCore(byte[] d)
    {
        if (d.Length < 0x188 || d[4] != 'P' || d[5] != 'G' || d[6] != 'F' || d[7] != '0') return null;
        var f = new PgfFont(d);
        int headerSize = U16(d, 2);
        f.Revision = I32(d, 8);
        f.Version = I32(d, 0xC);
        f.CharMapLength = I32(d, 0x10);
        f.CharPointerLength = I32(d, 0x14);
        f.CharMapBpe = I32(d, 0x18);
        f.CharPointerBpe = I32(d, 0x1C);
        f.Bpp = d[0x22];
        f.HSize = I32(d, 0x24); f.VSize = I32(d, 0x28);
        f.HResolution = I32(d, 0x2C); f.VResolution = I32(d, 0x30);
        f.FontName = CString(d, 0x35, 64);
        f.FontType = CString(d, 0x75, 64);
        f.FirstGlyph = U16(d, 0xB6); f.LastGlyph = U16(d, 0xB8);
        f.MaxAscender = I32(d, 0xD4); f.MaxDescender = I32(d, 0xD8);
        f.MaxLeftXAdjust = I32(d, 0xDC); f.MaxBaseYAdjust = I32(d, 0xE0);
        f.MinCenterXAdjust = I32(d, 0xE4); f.MaxTopYAdjust = I32(d, 0xE8);
        f.MaxAdvanceH = I32(d, 0xEC); f.MaxAdvanceV = I32(d, 0xF0);
        f.MaxSizeW = I32(d, 0xF4); f.MaxSizeH = I32(d, 0xF8);
        f.MaxGlyphWidth = U16(d, 0xFC); f.MaxGlyphHeight = U16(d, 0xFE);
        f._ptrScale = U16(d, 0x100) is var s and > 0 ? s : 4;
        f.ShadowMapLength = I32(d, 0x16C); f.ShadowMapBpe = I32(d, 0x170);
        if (headerSize >= 0x19C) f.ComponentCount = Math.Min(U16(d, 0x198), 16);
        if (headerSize < 0x188 || headerSize > d.Length) return null;
        if (f.CharMapBpe is < 1 or > 32 || f.CharPointerBpe is < 1 or > 32 || f.ShadowMapBpe is < 0 or > 32) return null;
        if (f.CharMapLength < 0 || f.CharPointerLength < 0 || f.ShadowMapLength < 0) return null;

        int p = headerSize;
        f._dim = Table(d, ref p, d[0x102]);
        f._xAdj = Table(d, ref p, d[0x103]);
        f._yAdj = Table(d, ref p, d[0x104]);
        f._adv = Table(d, ref p, d[0x105]);
        p += Packed(f.ShadowMapLength, f.ShadowMapBpe);
        if (f.Revision >= 3)
        {
            // extension after the base header: u32 bpe1, u16 count1, u16 ?, u32 bpe2, u16 count2, u16 ?, u32 ?
            // (kr0: 16, 5, 16, 2); each range is (u16 start, u16 count)
            f._ranges = Ranges(d, ref p, U16(d, 0x18C));
            f._hidden = Ranges(d, ref p, U16(d, 0x194));
        }
        f._charMapBit = (long)p * 8;
        p += Packed(f.CharMapLength, f.CharMapBpe);
        f._charPtrBit = (long)p * 8;
        p += Packed(f.CharPointerLength, f.CharPointerBpe);
        f._glyphBase = p;
        return p > d.Length ? null : f;
    }

    static int Packed(int count, int bpe) => (int)((((long)count * bpe + 31) & ~31L) / 8);

    static (int, int)[] Table(byte[] d, ref int p, int n)
    {
        var t = new (int, int)[n];
        for (int i = 0; i < n; i++, p += 8) t[i] = (I32(d, p), I32(d, p + 4));
        return t;
    }

    static (int, int)[] Ranges(byte[] d, ref int p, int n)
    {
        var t = new (int, int)[n];
        for (int i = 0; i < n; i++, p += 4) t[i] = (U16(d, p), U16(d, p + 2));
        return t;
    }

    /// <summary>Codes of revision-3 internal glyphs (Hangul composition parts): libpgf answers them with the alt char.</summary>
    public bool IsHidden(int code)
    {
        foreach (var (start, count) in _hidden)
            if (code >= start && code < start + count) return true;
        return false;
    }

    /// <summary>The glyph for a character code, or null when the font has none (libpgf's lookup, including the
    /// "all ones" missing-entry marker of the charmap).</summary>
    public PgfGlyph? GetGlyph(int code)
    {
        lock (_cache)
        {
            if (_cache.TryGetValue(code, out var g)) return g;
            try { g = Decode(code); }
            catch (IndexOutOfRangeException) { g = null; }
            _cache[code] = g;
            return g;
        }
    }

    PgfGlyph? Decode(int code)
    {
        if (code < FirstGlyph || code > LastGlyph) return null;
        int index = code - FirstGlyph;
        if (Revision >= 3 && _ranges.Length > 0)
        {
            index = -1;
            int cum = 0;
            foreach (var (start, count) in _ranges)
            {
                if (code < start) return null;
                if (code <= start + count) { index = cum + code - start; break; }
                cum += count;
            }
        }
        if (index < 0 || index >= CharMapLength) return null;
        long v = Bits(_charMapBit + (long)index * CharMapBpe, CharMapBpe);
        if (v == (1L << CharMapBpe) - 1 || v >= CharPointerLength) return null;
        long ptr = Bits(_charPtrBit + v * CharPointerBpe, CharPointerBpe);
        long pos = (_glyphBase + ptr * _ptrScale) * 8;
        if (pos >= (long)_d.Length * 8) return null;

        var g = new PgfGlyph();
        int size = (int)Read(ref pos, 14);
        long end = pos - 14 + (long)size * 8;
        g.Width = (int)Read(ref pos, 7);
        g.Height = (int)Read(ref pos, 7);
        g.Left = Signed7((int)Read(ref pos, 7));
        g.Top = Signed7((int)Read(ref pos, 7));
        g.Flags = (int)Read(ref pos, 6);
        if (Revision >= 2)
        {
            // shadow flags are stored as 2 + 2 + 3 bits, most significant group first (PPSSPP's reading)
            int sf = (int)Read(ref pos, 2) << 5;
            sf |= (int)Read(ref pos, 2) << 3;
            sf |= (int)Read(ref pos, 3);
            g.ShadowFlags = sf;
            g.ShadowId = (int)Read(ref pos, 9);
        }
        (g.DimW, g.DimH) = Metric(ref pos, (g.Flags & 4) != 0, _dim);
        (g.XAdjH, g.XAdjV) = Metric(ref pos, (g.Flags & 8) != 0, _xAdj);
        (g.YAdjH, g.YAdjV) = Metric(ref pos, (g.Flags & 16) != 0, _yAdj);
        (g.AdvH, g.AdvV) = Metric(ref pos, (g.Flags & 32) != 0, _adv);

        if ((g.Flags & 3) == 3 && Revision >= 3)
        {
            // overlay: ComponentCount little-endian u16 character codes instead of a bitmap
            g.Components = new int[ComponentCount];
            for (int k = 0; k < ComponentCount; k++) g.Components[k] = (int)Read(ref pos, 16);
            return g;
        }
        int w = g.Width, h = g.Height, n = w * h;
        var pix = new byte[n];
        bool columns = (g.Flags & 3) == 2;
        long limit = Math.Min(end, (long)_d.Length * 8);
        int i = 0;
        while (i < n && pos + 4 <= limit)
        {
            int nib = (int)Read(ref pos, 4);
            int count = nib < 8 ? nib + 1 : 16 - nib;
            int value = nib < 8 ? (int)Read(ref pos, 4) : 0;
            for (int k = 0; k < count && i < n; k++, i++)
            {
                if (nib >= 8) value = (int)Read(ref pos, 4);
                int x = columns ? i / h : i % w, y = columns ? i % h : i / w;
                pix[y * w + x] = (byte)value;
            }
        }
        g.Pixels = pix;
        return g;
    }

    (int, int) Metric(ref long pos, bool indexed, (int H, int V)[] table)
    {
        if (indexed)
        {
            int idx = (int)Read(ref pos, 8);
            return idx < table.Length ? table[idx] : (0, 0);
        }
        int hv = (int)Read(ref pos, 32);
        return (hv, (int)Read(ref pos, 32));
    }

    static int Signed7(int v) => v >= 64 ? v - 128 : v;

    long Read(ref long pos, int bits)
    {
        long v = Bits(pos, bits);
        pos += bits;
        return v;
    }

    /// <summary>LSB-first bit field (bit i of the stream = bit (i &amp; 7) of byte i / 8).</summary>
    long Bits(long pos, int bits)
    {
        long v = 0;
        for (int b = 0; b < bits; b++, pos++)
            if (((_d[(int)(pos >> 3)] >> (int)(pos & 7)) & 1) != 0) v |= 1L << b;
        return v;
    }

    static int I32(byte[] d, int o) => BitConverter.ToInt32(d, o);
    static int U16(byte[] d, int o) => BitConverter.ToUInt16(d, o);

    static string CString(byte[] d, int o, int max)
    {
        int n = 0;
        while (n < max && d[o + n] != 0) n++;
        return Encoding.ASCII.GetString(d, o, n);
    }
}
