using System.Buffers.Binary;
using System.Text;

namespace VitaSharp.Tests;

/// <summary>Tiny synthetic fonts for the ScePgf / ScePvf tests: a PGF with two bitmap glyphs and a TrueType font with
/// square/triangle glyphs, a 'kern' pair, vertical metrics and a GSUB 'vert' substitution.</summary>
internal static class FontTestData
{
    /// <summary>LSB-first bit writer (the PGF bit order).</summary>
    sealed class Bits
    {
        readonly List<byte> _b = new();
        int _bit;
        public int Position => _b.Count * 8 - (_bit == 0 ? 0 : 8 - _bit);
        public void Put(long v, int n)
        {
            for (int i = 0; i < n; i++)
            {
                if (_bit == 0) _b.Add(0);
                if (((v >> i) & 1) != 0) _b[^1] |= (byte)(1 << _bit);
                _bit = (_bit + 1) & 7;
            }
        }
        public byte[] ToArray() => _b.ToArray();
    }

    public const int PgfFirst = 0x41;

    /// <summary>
    /// PGF (revision 2) with codes 0x41..0x43: 'A' = glyph 0, 'B' missing, 'C' = glyph 1.
    /// Glyph 0: 3x2 bitmap rows [15 15 15 / 0 8 0], left 1, top 2. Glyph 1: 2x2 column-major [1 2 / 3 4] → rows
    /// [1 3 / 2 4], left 0, top 1. Metric tables have one entry each: dim (192, 128), xAdj (64, -96), yAdj (128, 16),
    /// advance (256, 320).
    /// </summary>
    public static byte[] Pgf()
    {
        var h = new byte[0x188];
        void I32(int o, int v) => BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(o), v);
        void U16(int o, int v) => BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(o), (ushort)v);
        U16(2, 0x188);
        "PGF0"u8.CopyTo(h.AsSpan(4));
        I32(8, 2); I32(0xC, 6);
        I32(0x10, 3); I32(0x14, 2); I32(0x18, 8); I32(0x1C, 16);
        h[0x20] = 4; h[0x21] = 4; h[0x22] = 4;
        I32(0x24, 0x288); I32(0x28, 0x288); I32(0x2C, 0x2000); I32(0x30, 0x2000);
        Encoding.ASCII.GetBytes("Test Sans").CopyTo(h, 0x35);
        Encoding.ASCII.GetBytes("Regular").CopyTo(h, 0x75);
        U16(0xB6, PgfFirst); U16(0xB8, PgfFirst + 2);
        I32(0xD4, 896); I32(0xD8, -256); I32(0xDC, 10); I32(0xE0, 20); I32(0xE4, 30); I32(0xE8, 40);
        I32(0xEC, 1000); I32(0xF0, 1100); I32(0xF4, 1200); I32(0xF8, 1300);
        U16(0xFC, 23); U16(0xFE, 20); U16(0x100, 4);
        h[0x102] = h[0x103] = h[0x104] = h[0x105] = 1;
        I32(0x16C, 1); I32(0x170, 16);

        var body = new List<byte>(h);
        void Pair(int a, int b) { body.AddRange(BitConverter.GetBytes(a)); body.AddRange(BitConverter.GetBytes(b)); }
        Pair(192, 128); Pair(64, -96); Pair(128, 16); Pair(256, 320);
        body.AddRange(new byte[] { 0x41, 0, 0, 0 });            // shadow charmap: 1 x 16 bits, padded to 4 bytes
        body.AddRange(new byte[] { 0, 0xFF, 1, 0 });            // charmap: 'A' → 0, 'B' missing, 'C' → 1
        var g0 = Glyph(3, 2, 1, 2, 1, [15, 15, 15, 0, 8, 0]);
        var g1 = Glyph(2, 2, 0, 1, 2, [1, 2, 3, 4]);
        int g1Ptr = (g0.Length + 3) / 4;
        body.AddRange(BitConverter.GetBytes((ushort)0));        // pointer table: 2 x 16 bits (units of 4 bytes)
        body.AddRange(BitConverter.GetBytes((ushort)g1Ptr));
        body.AddRange(g0);
        while ((body.Count - (0x188 + 32 + 4 + 4 + 4)) % 4 != 0) body.Add(0);
        body.AddRange(g1);
        body.AddRange(new byte[8]);
        return body.ToArray();
    }

    /// <summary>One character glyph with indexed metrics (entry 0 of each table) and a literal-run RLE bitmap.</summary>
    static byte[] Glyph(int w, int h, int left, int top, int layout, int[] values)
    {
        var b = new Bits();
        b.Put(0, 14);                                // size, patched below
        b.Put(w, 7); b.Put(h, 7);
        b.Put(left & 0x7F, 7); b.Put(top & 0x7F, 7);
        b.Put(layout | 4 | 8 | 16 | 32, 6);
        b.Put(1, 2); b.Put(2, 2); b.Put(3, 3);       // shadow flags (2+2+3 bits) = 1<<5 | 2<<3 | 3 = 0x33
        b.Put(7, 9);                                 // shadow id
        for (int i = 0; i < 4; i++) b.Put(0, 8);     // metric indices
        for (int i = 0; i < values.Length; i += 8)
        {
            int n = Math.Min(8, values.Length - i);
            b.Put(16 - n, 4);
            for (int k = 0; k < n; k++) b.Put(values[i + k], 4);
        }
        var bytes = b.ToArray();
        int size = bytes.Length;
        bytes[0] = (byte)size;
        bytes[1] = (byte)((bytes[1] & 0xC0) | (size >> 8));
        return bytes;
    }

    // ---- TrueType ------------------------------------------------------------------------------------------------

    public const int UnitsPerEm = 1000;
    /// <summary>Glyphs: 0 .notdef (empty), 1 'A' = square (100,0)-(500,700), 2 'V' = triangle, 3 U+3001 = box
    /// (100,100)-(300,300), 4 = vertical form of 3, box (600,600)-(800,800). Advance 600, vertical advance 1000 /
    /// top side bearing 100, kern ('A','V') = -50.</summary>
    public static byte[] Ttf()
    {
        var glyphs = new List<byte[]>
        {
            Array.Empty<byte>(),
            SimpleGlyph([(100, 0), (100, 700), (500, 700), (500, 0)]),
            SimpleGlyph([(0, 0), (300, 700), (600, 0)]),
            SimpleGlyph([(100, 100), (100, 300), (300, 300), (300, 100)]),
            SimpleGlyph([(600, 600), (600, 800), (800, 800), (800, 600)]),
        };
        var glyf = new List<byte>();
        var loca = new List<byte>();
        foreach (var g in glyphs)
        {
            loca.AddRange(BE16(glyf.Count / 2));
            glyf.AddRange(g);
            while (glyf.Count % 4 != 0) glyf.Add(0);
        }
        loca.AddRange(BE16(glyf.Count / 2));

        var head = new byte[54];
        BinaryPrimitives.WriteUInt32BigEndian(head, 0x00010000);
        BinaryPrimitives.WriteUInt32BigEndian(head.AsSpan(12), 0x5F0F3CF5);
        BinaryPrimitives.WriteUInt16BigEndian(head.AsSpan(18), UnitsPerEm);
        BinaryPrimitives.WriteInt16BigEndian(head.AsSpan(36), 0);
        BinaryPrimitives.WriteInt16BigEndian(head.AsSpan(38), 0);
        BinaryPrimitives.WriteInt16BigEndian(head.AsSpan(40), 800);
        BinaryPrimitives.WriteInt16BigEndian(head.AsSpan(42), 800);
        BinaryPrimitives.WriteInt16BigEndian(head.AsSpan(50), 0);   // short loca

        var hhea = new byte[36];
        BinaryPrimitives.WriteUInt32BigEndian(hhea, 0x00010000);
        BinaryPrimitives.WriteInt16BigEndian(hhea.AsSpan(4), 800);
        BinaryPrimitives.WriteInt16BigEndian(hhea.AsSpan(6), -200);
        BinaryPrimitives.WriteInt16BigEndian(hhea.AsSpan(8), 0);
        BinaryPrimitives.WriteUInt16BigEndian(hhea.AsSpan(10), 600);
        BinaryPrimitives.WriteUInt16BigEndian(hhea.AsSpan(34), (ushort)glyphs.Count);

        var vhea = (byte[])hhea.Clone();
        BinaryPrimitives.WriteUInt16BigEndian(vhea.AsSpan(10), 1000);

        var maxp = new byte[32];
        BinaryPrimitives.WriteUInt32BigEndian(maxp, 0x00010000);
        BinaryPrimitives.WriteUInt16BigEndian(maxp.AsSpan(4), (ushort)glyphs.Count);

        var hmtx = new List<byte>();
        var vmtx = new List<byte>();
        foreach (var _ in glyphs) { hmtx.AddRange(BE16(600)); hmtx.AddRange(BE16(0)); vmtx.AddRange(BE16(1000)); vmtx.AddRange(BE16(100)); }

        // cmap: one (3, 1) format 4 subtable, segments 'A', 'V', U+3001 and the final 0xFFFF
        ushort[] starts = [0x41, 0x56, 0x3001, 0xFFFF];
        ushort[] gids = [1, 2, 3, 0];
        int seg = starts.Length;
        var f4 = new List<byte>();
        f4.AddRange(BE16(4)); f4.AddRange(BE16(16 + seg * 8)); f4.AddRange(BE16(0));
        f4.AddRange(BE16(seg * 2)); f4.AddRange(BE16(8)); f4.AddRange(BE16(2)); f4.AddRange(BE16(0));
        foreach (var s in starts) f4.AddRange(BE16(s));
        f4.AddRange(BE16(0));
        foreach (var s in starts) f4.AddRange(BE16(s));
        for (int i = 0; i < seg; i++) f4.AddRange(BE16(i == seg - 1 ? 1 : (gids[i] - starts[i]) & 0xFFFF));
        for (int i = 0; i < seg; i++) f4.AddRange(BE16(0));
        var cmap = new List<byte>();
        cmap.AddRange(BE16(0)); cmap.AddRange(BE16(1));
        cmap.AddRange(BE16(3)); cmap.AddRange(BE16(1)); cmap.AddRange(BE32(12));
        cmap.AddRange(f4);

        // kern: version 0, one horizontal format 0 subtable with the pair (1, 2) = -50
        var kern = new List<byte>();
        kern.AddRange(BE16(0)); kern.AddRange(BE16(1));
        kern.AddRange(BE16(0)); kern.AddRange(BE16(14 + 6)); kern.AddRange(BE16(1));
        kern.AddRange(BE16(1)); kern.AddRange(BE16(6)); kern.AddRange(BE16(0)); kern.AddRange(BE16(0));
        kern.AddRange(BE16(1)); kern.AddRange(BE16(2)); kern.AddRange(BE16(-50 & 0xFFFF));

        // GSUB: empty script list, feature 'vert' → lookup 0 (single substitution format 1, coverage [3], delta +1)
        var gsub = new List<byte>();
        gsub.AddRange(BE16(1)); gsub.AddRange(BE16(0));
        gsub.AddRange(BE16(10)); gsub.AddRange(BE16(12)); gsub.AddRange(BE16(26));
        gsub.AddRange(BE16(0));                                              // 10: script list (0 scripts)
        gsub.AddRange(BE16(1)); gsub.AddRange("vert"u8.ToArray()); gsub.AddRange(BE16(8)); // 12: feature list
        gsub.AddRange(BE16(0)); gsub.AddRange(BE16(1)); gsub.AddRange(BE16(0));            // 20: feature
        gsub.AddRange(BE16(1)); gsub.AddRange(BE16(4));                                    // 26: lookup list
        gsub.AddRange(BE16(1)); gsub.AddRange(BE16(0)); gsub.AddRange(BE16(1)); gsub.AddRange(BE16(8)); // 30: lookup
        gsub.AddRange(BE16(1)); gsub.AddRange(BE16(6)); gsub.AddRange(BE16(1));            // 38: subtable
        gsub.AddRange(BE16(1)); gsub.AddRange(BE16(1)); gsub.AddRange(BE16(3));            // 44: coverage

        var tables = new SortedDictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["GSUB"] = gsub.ToArray(), ["cmap"] = cmap.ToArray(), ["glyf"] = glyf.ToArray(), ["head"] = head,
            ["hhea"] = hhea, ["hmtx"] = hmtx.ToArray(), ["kern"] = kern.ToArray(), ["loca"] = loca.ToArray(),
            ["maxp"] = maxp, ["vhea"] = vhea, ["vmtx"] = vmtx.ToArray(),
        };
        var font = new List<byte>();
        font.AddRange(BE32(0x00010000)); font.AddRange(BE16(tables.Count));
        font.AddRange(BE16(0)); font.AddRange(BE16(0)); font.AddRange(BE16(0));
        int offset = 12 + 16 * tables.Count;
        var data = new List<byte>();
        foreach (var (tag, t) in tables)
        {
            font.AddRange(Encoding.ASCII.GetBytes(tag)); font.AddRange(BE32(0));
            font.AddRange(BE32(offset + data.Count)); font.AddRange(BE32(t.Length));
            data.AddRange(t);
            while (data.Count % 4 != 0) data.Add(0);
        }
        font.AddRange(data);
        return font.ToArray();
    }

    /// <summary>One closed contour of on-curve points, coordinates as 16-bit deltas.</summary>
    static byte[] SimpleGlyph((int X, int Y)[] pts)
    {
        var b = new List<byte>();
        b.AddRange(BE16(1));
        b.AddRange(BE16(pts.Min(p => p.X) & 0xFFFF)); b.AddRange(BE16(pts.Min(p => p.Y) & 0xFFFF));
        b.AddRange(BE16(pts.Max(p => p.X) & 0xFFFF)); b.AddRange(BE16(pts.Max(p => p.Y) & 0xFFFF));
        b.AddRange(BE16(pts.Length - 1));
        b.AddRange(BE16(0));
        foreach (var _ in pts) b.Add(1);
        int px = 0, py = 0;
        foreach (var p in pts) { b.AddRange(BE16((p.X - px) & 0xFFFF)); px = p.X; }
        foreach (var p in pts) { b.AddRange(BE16((p.Y - py) & 0xFFFF)); py = p.Y; }
        return b.ToArray();
    }

    static byte[] BE16(int v) => [(byte)(v >> 8), (byte)v];
    static byte[] BE32(int v) => [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];

    /// <summary>Firmware font directory of a local Vita3K install, used by optional checks against the real files.</summary>
    public static string? FirmwareFontDir(string sub)
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Vita3K", "Vita3K", "sa0", "data", "font", sub);
        return Directory.Exists(dir) ? dir : null;
    }
}
