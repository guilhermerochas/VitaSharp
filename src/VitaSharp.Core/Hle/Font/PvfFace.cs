using System.Buffers.Binary;
using System.Runtime.InteropServices;
using static StbTrueTypeSharp.StbTrueType;

namespace VitaSharp.Core.Hle.Font;

/// <summary>Size and synthetic style a glyph is produced with: pixels per em (H, V), skew angles in degrees and
/// the embolden strength (26.6 pixels, total growth of the stroke).</summary>
internal readonly record struct PvfStyle(float PpemX, float PpemY, float SkewX, float SkewY, float Embolden);

/// <summary>One glyph at a given style: FreeType-style grid-fitted metrics (26.6) plus, when rendered, an 8-bit
/// coverage bitmap whose top-left pixel is (Left, Top) from the horizontal origin (y up).</summary>
internal sealed class PvfGlyph
{
    public int Width, Height, HoriBearingX, HoriBearingY, HoriAdvance, VertBearingX, VertBearingY, VertAdvance;
    public byte[] Bitmap = [];
    public int BitmapWidth, BitmapHeight, Left, Top;
    /// <summary>Outline in 26.6 pixels (y up): points, FreeType tags (1 on, 0 conic control, 2 cubic control), contour ends.</summary>
    public float[] X = [], Y = [];
    public byte[] Tags = [];
    public short[] ContourEnds = [];
    public bool ReverseFill;
}

/// <summary>
/// OpenType/TrueType face for the ScePvf HLE (firmware .pvf files are plain CFF OpenType, cn0/cn1 TrueType).
/// Outlines come from stb_truetype (StbTrueTypeSharp, MIT/Unlicense, managed); scaling, skew, embolden, grid
/// fitting of the metrics and the bitmap box follow FreeType, which is what the real libpvf uses through
/// libSceFt2. Differences: no hinting and stb's anti-aliasing, so coverage values (not sizes or positions) can be
/// off by a little. Vertical forms come from the GSUB 'vert'/'vrt2' single substitutions, vertical metrics from
/// vhea/vmtx (synthesized like FreeType when missing). Kerning reads only the 'kern' table, like FT_Get_Kerning.
/// </summary>
internal sealed unsafe class PvfFace : IDisposable
{
    readonly stbtt_fontinfo _info;
    byte* _data;
    readonly int _length;
    public int UnitsPerEm, Ascender, Descender, LineGap, AdvanceWidthMax, NumGlyphs;
    public int XMin, YMin, XMax, YMax;
    int _vmtx, _numVMetrics, _vAdvanceMax;
    Dictionary<int, int>? _vert;
    int _charCount = -1;
    readonly Dictionary<(int, PvfStyle, bool, bool), PvfGlyph> _cache = new();
    const int CacheLimit = 1024;

    PvfFace(byte[] src)
    {
        _length = src.Length;
        _data = (byte*)NativeMemory.Alloc((nuint)Math.Max(1, src.Length));
        src.CopyTo(new Span<byte>(_data, src.Length));
        _info = new stbtt_fontinfo();
    }

    /// <summary>Loads face <paramref name="subIndex"/> of a font file or collection; null when it is not a font stb
    /// (and so most likely FreeType) can read.</summary>
    public static PvfFace? Open(byte[] data, int subIndex)
    {
        if (data.Length < 12) return null;
        var f = new PvfFace(data);
        int offset = stbtt_GetFontOffsetForIndex(f._data, subIndex);
        if (offset < 0 || offset >= data.Length || stbtt_InitFont(f._info, f._data, offset) == 0 || !f.ReadTables(offset))
        {
            f.Dispose();
            return null;
        }
        return f;
    }

    public void Dispose()
    {
        if (_data == null) return;
        NativeMemory.Free(_data);
        _data = null;
    }

    int U16(int o) => o + 2 <= _length ? BinaryPrimitives.ReadUInt16BigEndian(new ReadOnlySpan<byte>(_data + o, 2)) : 0;
    int S16(int o) => (short)U16(o);
    uint U32(int o) => o + 4 <= _length ? BinaryPrimitives.ReadUInt32BigEndian(new ReadOnlySpan<byte>(_data + o, 4)) : 0;

    int Table(int fontStart, string tag)
    {
        int n = U16(fontStart + 4);
        uint t = (uint)(tag[0] << 24 | tag[1] << 16 | tag[2] << 8 | tag[3]);
        for (int i = 0; i < n; i++)
        {
            int rec = fontStart + 12 + 16 * i;
            if (U32(rec) == t) return (int)U32(rec + 8);
        }
        return 0;
    }

    bool ReadTables(int fontStart)
    {
        int head = Table(fontStart, "head"), hhea = Table(fontStart, "hhea");
        if (head == 0 || hhea == 0) return false;
        UnitsPerEm = U16(head + 18);
        if (UnitsPerEm == 0) return false;
        XMin = S16(head + 36); YMin = S16(head + 38); XMax = S16(head + 40); YMax = S16(head + 42);
        Ascender = S16(hhea + 4); Descender = S16(hhea + 6); LineGap = S16(hhea + 8); AdvanceWidthMax = U16(hhea + 10);
        NumGlyphs = _info.numGlyphs;
        int vhea = Table(fontStart, "vhea");
        _vmtx = Table(fontStart, "vmtx");
        if (vhea != 0 && _vmtx != 0)
        {
            _vAdvanceMax = U16(vhea + 10);
            _numVMetrics = U16(vhea + 34);
        }
        else _vmtx = 0;
        int gsub = Table(fontStart, "GSUB");
        if (gsub != 0) _vert = ReadVert(gsub);
        return true;
    }

    public int GlyphIndex(int code) => stbtt_FindGlyphIndex(_info, code);

    /// <summary>Vertical form of a glyph (GSUB 'vert', else 'vrt2'); the glyph itself when there is none.</summary>
    public int VerticalGlyph(int glyph) => _vert != null && _vert.TryGetValue(glyph, out int v) ? v : glyph;
    public bool HasVerticalForm(int glyph) => _vert != null && _vert.ContainsKey(glyph);

    /// <summary>Number of BMP characters the cmap maps to a glyph.</summary>
    public int CharCount
    {
        get
        {
            if (_charCount >= 0) return _charCount;
            int n = 0;
            for (int c = 1; c <= 0xFFFF; c++)
                if (stbtt_FindGlyphIndex(_info, c) != 0) n++;
            return _charCount = n;
        }
    }

    /// <summary>Kerning of a glyph pair in font units from the 'kern' table only (GPOS is not read).</summary>
    public int KernUnits(int left, int right) => _info.kern != 0 ? stbtt__GetGlyphKernInfoAdvance(_info, left, right) : 0;

    /// <summary>Largest vertical advance (vhea), or the synthesized one (ascender - descender + line gap).</summary>
    public int MaxVerticalAdvanceUnits => _vmtx != 0 ? _vAdvanceMax : Ascender - Descender + LineGap;

    public PvfGlyph Load(int glyph, in PvfStyle style, bool vertical, bool render)
    {
        var key = (glyph, style, vertical, render);
        lock (_cache)
        {
            if (_cache.TryGetValue(key, out var hit)) return hit;
            var g = Build(glyph, style, render);
            if (_cache.Count >= CacheLimit) _cache.Clear();
            _cache[key] = g;
            return g;
        }
    }

    PvfGlyph Build(int glyph, in PvfStyle s, bool render)
    {
        float sx = s.PpemX * 64f / UnitsPerEm, sy = s.PpemY * 64f / UnitsPerEm;
        var g = new PvfGlyph();
        ReadOutline(glyph, sx, sy, g);
        if (s.SkewX != 0 || s.SkewY != 0)
        {
            float tx = MathF.Tan(s.SkewX * MathF.PI / 180f), ty = MathF.Tan(s.SkewY * MathF.PI / 180f);
            for (int i = 0; i < g.X.Length; i++)
            {
                float x = g.X[i], y = g.Y[i];
                g.X[i] = x + y * tx;
                g.Y[i] = y + x * ty;
            }
        }
        float strength = s.Embolden;
        if (strength != 0) Embolden(g, strength);

        // FreeType metrics: control box of the outline, grid-fitted (bearings floored/ceiled, advances rounded)
        float xMin = 0, yMin = 0, xMax = 0, yMax = 0;
        if (g.X.Length > 0)
        {
            xMin = xMax = g.X[0]; yMin = yMax = g.Y[0];
            for (int i = 1; i < g.X.Length; i++)
            {
                xMin = MathF.Min(xMin, g.X[i]); xMax = MathF.Max(xMax, g.X[i]);
                yMin = MathF.Min(yMin, g.Y[i]); yMax = MathF.Max(yMax, g.Y[i]);
            }
        }
        int left = Floor64(xMin), right = Ceil64(xMax), bottom = Floor64(yMin), top = Ceil64(yMax);
        int advW, lsb;
        stbtt_GetGlyphHMetrics(_info, glyph, &advW, &lsb);
        float hAdv = advW * sx + strength;
        g.HoriBearingX = left;
        g.HoriBearingY = top;
        g.Width = right - left;
        g.Height = top - bottom;
        g.HoriAdvance = Round64(hAdv);

        float vAdv, vBearingY;
        float rawHeight = yMax - yMin;
        if (_vmtx != 0)
        {
            int i = Math.Min(glyph, _numVMetrics - 1);
            int advH = U16(_vmtx + 4 * i);
            int tsb = glyph < _numVMetrics ? S16(_vmtx + 4 * glyph + 2) : S16(_vmtx + 4 * _numVMetrics + 2 * (glyph - _numVMetrics));
            vAdv = advH * sy;
            vBearingY = tsb * sy;
        }
        else
        {
            vAdv = MathF.Round((Ascender - Descender + LineGap) * sy / 64f) * 64f;
            if (vAdv == 0) vAdv = rawHeight * 12 / 10;
            vBearingY = (vAdv - rawHeight) / 2;
        }
        float vBearingX = xMin - hAdv / 2;
        g.VertBearingX = Floor64(vBearingX);
        g.VertBearingY = Floor64(vBearingY);
        g.VertAdvance = Round64(vAdv + strength);

        if (render) Rasterize(g, xMin, yMin, xMax, yMax);
        return g;
    }

    static int Floor64(float v) => (int)MathF.Floor(v / 64f) * 64;
    static int Ceil64(float v) => (int)MathF.Ceiling(v / 64f) * 64;
    static int Round64(float v) => (int)MathF.Floor((v + 32f) / 64f) * 64;

    void ReadOutline(int glyph, float sx, float sy, PvfGlyph g)
    {
        stbtt_vertex* v;
        int n = stbtt_GetGlyphShape(_info, glyph, &v);
        var xs = new List<float>(n * 2);
        var ys = new List<float>(n * 2);
        var tags = new List<byte>(n * 2);
        var ends = new List<short>();
        int contourStart = 0;
        for (int i = 0; i < n; i++)
        {
            var p = v[i];
            switch (p.type)
            {
                case 1: // vmove
                    CloseContour(xs, ys, tags, ends, contourStart);
                    contourStart = xs.Count;
                    Add(p.x, p.y, 1);
                    break;
                case 2: Add(p.x, p.y, 1); break; // vline
                case 3: Add(p.cx, p.cy, 0); Add(p.x, p.y, 1); break; // vcurve (quadratic)
                case 4: Add(p.cx, p.cy, 2); Add(p.cx1, p.cy1, 2); Add(p.x, p.y, 1); break; // vcubic
            }
        }
        CloseContour(xs, ys, tags, ends, contourStart);
        if (n > 0) stbtt_FreeShape(_info, v);
        g.X = xs.ToArray(); g.Y = ys.ToArray(); g.Tags = tags.ToArray(); g.ContourEnds = ends.ToArray();
        g.ReverseFill = SignedArea(g) > 0; // counter-clockwise outer contours (PostScript orientation)

        void Add(float x, float y, byte tag) { xs.Add(x * sx); ys.Add(y * sy); tags.Add(tag); }
    }

    /// <summary>stb repeats the first point at the end of each contour; FreeType contours are implicitly closed.</summary>
    static void CloseContour(List<float> xs, List<float> ys, List<byte> tags, List<short> ends, int start)
    {
        int last = xs.Count - 1;
        if (last < start) return;
        if (last > start && tags[last] == 1 && xs[last] == xs[start] && ys[last] == ys[start])
        {
            xs.RemoveAt(last); ys.RemoveAt(last); tags.RemoveAt(last);
            last--;
        }
        ends.Add((short)last);
    }

    static float SignedArea(PvfGlyph g)
    {
        float area = 0;
        int first = 0;
        foreach (int end in g.ContourEnds)
        {
            for (int i = first; i <= end; i++)
            {
                int j = i == end ? first : i + 1;
                area += g.X[i] * g.Y[j] - g.X[j] * g.Y[i];
            }
            first = end + 1;
        }
        return area;
    }

    /// <summary>FT_Outline_Embolden: every point moves along the bisector of its neighbouring edges so the stroke
    /// grows by <paramref name="strength"/> (each side half of it), then the whole outline shifts by half the
    /// strength up and right (FreeType keeps the left/bottom edges in place).</summary>
    static void Embolden(PvfGlyph g, float strength)
    {
        float half = strength / 2;
        bool trueType = !g.ReverseFill;
        int first = 0;
        foreach (int last in g.ContourEnds)
        {
            int count = last - first + 1;
            if (count < 2) { first = last + 1; continue; }
            var nx = new float[count];
            var ny = new float[count];
            for (int k = 0; k < count; k++)
            {
                int i = first + k, prev = first + (k + count - 1) % count, next = first + (k + 1) % count;
                float inX = g.X[i] - g.X[prev], inY = g.Y[i] - g.Y[prev];
                float outX = g.X[next] - g.X[i], outY = g.Y[next] - g.Y[i];
                float lIn = MathF.Sqrt(inX * inX + inY * inY), lOut = MathF.Sqrt(outX * outX + outY * outY);
                if (lIn == 0 || lOut == 0) continue;
                inX /= lIn; inY /= lIn; outX /= lOut; outY /= lOut;
                float d = inX * outX + inY * outY;
                if (d <= -0.9375f) continue; // turn sharper than ~160 degrees: no shift
                d += 1;
                float shX = inY + outY, shY = inX + outX;
                if (trueType) shX = -shX; else shY = -shY;
                float q = outX * inY - outY * inX;
                if (trueType) q = -q;
                float l = MathF.Min(lIn, lOut);
                nx[k] = half * q <= l * d ? shX * half / d : shX * l / q;
                ny[k] = half * q <= l * d ? shY * half / d : shY * l / q;
            }
            for (int k = 0; k < count; k++)
            {
                g.X[first + k] += half + nx[k];
                g.Y[first + k] += half + ny[k];
            }
            first = last + 1;
        }
    }

    void Rasterize(PvfGlyph g, float xMin, float yMin, float xMax, float yMax)
    {
        int ix0 = (int)MathF.Floor(xMin / 64f), iy0 = (int)MathF.Floor(-yMax / 64f);
        int ix1 = (int)MathF.Ceiling(xMax / 64f), iy1 = (int)MathF.Ceiling(-yMin / 64f);
        int w = Math.Max(0, ix1 - ix0), h = Math.Max(0, iy1 - iy0);
        g.Left = ix0; g.Top = -iy0;
        g.BitmapWidth = w; g.BitmapHeight = h;
        g.Bitmap = new byte[w * h];
        if (w == 0 || h == 0 || g.X.Length == 0) return;

        // back to stb vertices in 1/unit pixels (short coordinates: lower the precision for very large glyphs)
        float unit = 64;
        float maxAbs = Math.Max(Math.Max(MathF.Abs(xMin), MathF.Abs(xMax)), Math.Max(MathF.Abs(yMin), MathF.Abs(yMax)));
        while (maxAbs * unit / 64 > 32000 && unit > 1) unit /= 2;
        float k = unit / 64;
        var verts = new List<stbtt_vertex>(g.X.Length + g.ContourEnds.Length * 2);
        int first = 0;
        foreach (int last in g.ContourEnds)
        {
            int count = last - first + 1;
            // start on an on-curve point
            int s = 0;
            while (s < count && g.Tags[first + s] != 1) s++;
            if (s == count) { first = last + 1; continue; }
            short P(float c) => (short)MathF.Round(c * k);
            int i0 = first + s;
            verts.Add(new stbtt_vertex { type = 1, x = P(g.X[i0]), y = P(g.Y[i0]) });
            for (int m = 1; m <= count; m++)
            {
                int i = first + (s + m) % count;
                if (g.Tags[i] == 1) verts.Add(new stbtt_vertex { type = 2, x = P(g.X[i]), y = P(g.Y[i]) });
                else if (g.Tags[i] == 2)
                {
                    int j = first + (s + m + 1) % count, e = first + (s + m + 2) % count;
                    verts.Add(new stbtt_vertex { type = 4, cx = P(g.X[i]), cy = P(g.Y[i]), cx1 = P(g.X[j]), cy1 = P(g.Y[j]), x = P(g.X[e]), y = P(g.Y[e]) });
                    m += 2;
                }
                else
                {
                    // conic control: the end point is the next on point or the implied midpoint of two controls
                    int j = first + (s + m + 1) % count;
                    float ex = g.X[j], ey = g.Y[j];
                    if (g.Tags[j] != 1) { ex = (g.X[i] + g.X[j]) / 2; ey = (g.Y[i] + g.Y[j]) / 2; }
                    else m++;
                    verts.Add(new stbtt_vertex { type = 3, cx = P(g.X[i]), cy = P(g.Y[i]), x = P(ex), y = P(ey) });
                }
            }
            first = last + 1;
        }
        var arr = verts.ToArray();
        fixed (byte* pix = g.Bitmap)
        fixed (stbtt_vertex* pv = arr)
        {
            var bmp = new stbtt__bitmap { w = w, h = h, stride = w, pixels = pix };
            stbtt_Rasterize(&bmp, 0.35f, pv, arr.Length, 1f / unit, 1f / unit, 0, 0, ix0, iy0, 1, null, false);
        }
    }

    Dictionary<int, int>? ReadVert(int gsub)
    {
        try
        {
            int featureList = gsub + U16(gsub + 6), lookupList = gsub + U16(gsub + 8);
            var lookups = new HashSet<int>();
            foreach (string tag in new[] { "vert", "vrt2" })
            {
                uint t = (uint)(tag[0] << 24 | tag[1] << 16 | tag[2] << 8 | tag[3]);
                int n = U16(featureList);
                for (int i = 0; i < n; i++)
                {
                    int rec = featureList + 2 + 6 * i;
                    if (U32(rec) != t) continue;
                    int feature = featureList + U16(rec + 4);
                    int count = U16(feature + 2);
                    for (int k = 0; k < count; k++) lookups.Add(U16(feature + 4 + 2 * k));
                }
                if (lookups.Count > 0) break;
            }
            if (lookups.Count == 0) return null;
            var map = new Dictionary<int, int>();
            int lookupCount = U16(lookupList);
            foreach (int li in lookups)
            {
                if (li >= lookupCount) continue;
                int lookup = lookupList + U16(lookupList + 2 + 2 * li);
                int type = U16(lookup), subCount = U16(lookup + 4);
                for (int k = 0; k < subCount; k++)
                {
                    int sub = lookup + U16(lookup + 6 + 2 * k), subType = type;
                    if (type == 7) { subType = U16(sub + 2); sub += (int)U32(sub + 4); }
                    if (subType == 1) ReadSingleSubst(sub, map);
                }
            }
            return map;
        }
        catch (IndexOutOfRangeException) { return null; }
    }

    void ReadSingleSubst(int sub, Dictionary<int, int> map)
    {
        int format = U16(sub), coverage = sub + U16(sub + 2);
        var glyphs = Coverage(coverage);
        for (int i = 0; i < glyphs.Count; i++)
        {
            int to = format == 1 ? (glyphs[i] + S16(sub + 4)) & 0xFFFF : i < U16(sub + 4) ? U16(sub + 6 + 2 * i) : -1;
            if (to >= 0) map.TryAdd(glyphs[i], to);
        }
    }

    List<int> Coverage(int cov)
    {
        var list = new List<int>();
        int format = U16(cov), n = U16(cov + 2);
        if (format == 1)
            for (int i = 0; i < n; i++) list.Add(U16(cov + 4 + 2 * i));
        else if (format == 2)
            for (int i = 0; i < n; i++)
            {
                int r = cov + 4 + 6 * i, start = U16(r), end = U16(r + 2);
                for (int gid = start; gid <= end; gid++) list.Add(gid);
            }
        return list;
    }
}
