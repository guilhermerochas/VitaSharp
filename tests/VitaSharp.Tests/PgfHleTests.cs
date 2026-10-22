using System.Reflection;
using VitaSharp.Core.Hle.Font;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;

namespace VitaSharp.Tests;

/// <summary>ScePgf: PGF parsing, library/font lifecycle, the system font table and matching, char info with the
/// alt char, and the additive subpixel glyph blit (expected values follow libpgf's routine).</summary>
[Collection("guest")]
public sealed class PgfHleTests(GuestMemoryFixture fx)
{
    static object? Invoke(Pgf m, string name, params object[] args) =>
        typeof(Pgf).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.Invoke(m, args);
    static int Call(Pgf m, string name, params object[] args) => (int)Invoke(m, name, args)!;
    static uint CallU(Pgf m, string name, params object[] args) => (uint)Invoke(m, name, args)!;
    static float CallF(Pgf m, string name, params object[] args) => (float)Invoke(m, name, args)!;
    static Ptr<uint> P(uint a) => new(a);

    const uint AllocFn = 0x100, FreeFn = 0x200, OpenFn = 0x300, CloseFn = 0x400, ReadFn = 0x500, SeekFn = 0x600;
    int _allocs, _frees;
    bool _failAlloc;
    byte[] _userFile = [];
    int _userPos;

    uint Guest(uint fn, uint[] a)
    {
        switch (fn)
        {
            case AllocFn:
                if (_failAlloc) return 0;
                _allocs++;
                return fx.Memory.Alloc(Math.Max(a[1], 4u), "pgf-test", MemoryRegion.User, 16);
            case FreeFn: _frees++; fx.Memory.Free(a[1]); return 0;
            case OpenFn: _userPos = 0; return 0x77;
            case SeekFn: _userPos = (int)a[2]; return 0;
            case ReadFn:
            {
                int n = (int)Math.Min(a[3] * a[4], (uint)(_userFile.Length - _userPos));
                _userFile.AsSpan(_userPos, n).CopyTo(GuestMemory.Span(a[2], n));
                _userPos += n;
                return (uint)n / a[3];
            }
            case CloseFn: return 0;
        }
        throw new InvalidOperationException($"unexpected guest call {fn:X}");
    }

    Pgf Module(Func<string, byte[]?>? files = null) => new()
    {
        GuestCallOverride = Guest,
        SystemFontOverride = files ?? (name => name == "ltn0.pgf" ? FontTestData.Pgf() : null),
    };

    uint Params(uint numFonts, bool io = false)
    {
        uint p = fx.Memory.Alloc(0x40, "pgf-params", MemoryRegion.User, 16);
        GuestMemory.Fill(p, 0, 0x40);
        GuestMemory.W32(p, 0xABC);
        GuestMemory.W32(p + 4, numFonts);
        GuestMemory.W32(p + 0xC, AllocFn);
        GuestMemory.W32(p + 0x10, FreeFn);
        if (io)
        {
            GuestMemory.W32(p + 0x14, OpenFn); GuestMemory.W32(p + 0x18, CloseFn);
            GuestMemory.W32(p + 0x1C, ReadFn); GuestMemory.W32(p + 0x20, SeekFn);
        }
        return p;
    }

    uint Err() { uint e = fx.Memory.Alloc(16, "pgf-err", MemoryRegion.User, 16); GuestMemory.W32(e, 0x12345678); return e; }

    (Pgf m, uint lib) NewLib(uint numFonts = 4, Func<string, byte[]?>? files = null, bool io = false)
    {
        var m = Module(files);
        uint e = Err();
        uint lib = CallU(m, "sceFontNewLib", Params(numFonts, io), P(e));
        Assert.NotEqual(0u, lib);
        Assert.Equal(0u, GuestMemory.R32(e));
        return (m, lib);
    }

    uint Open(Pgf m, uint lib, int index = 1)
    {
        uint e = Err();
        uint f = CallU(m, "sceFontOpen", lib, index, 0, P(e));
        Assert.Equal(0u, GuestMemory.R32(e));
        Assert.NotEqual(0u, f);
        return f;
    }

    [Fact]
    public void ParsesSyntheticFont()
    {
        var f = PgfFont.Parse(FontTestData.Pgf())!;
        Assert.NotNull(f);
        Assert.Equal("Test Sans", f.FontName);
        Assert.Equal(2, f.CharPointerLength);
        var a = f.GetGlyph('A')!;
        Assert.Equal((3, 2, 1, 2), (a.Width, a.Height, a.Left, a.Top));
        Assert.Equal(new byte[] { 15, 15, 15, 0, 8, 0 }, a.Pixels);
        Assert.Equal((192, 128, 64, -96, 128, 16, 256, 320), (a.DimW, a.DimH, a.XAdjH, a.XAdjV, a.YAdjH, a.YAdjV, a.AdvH, a.AdvV));
        Assert.Equal((0x33, 7), (a.ShadowFlags, a.ShadowId));
        Assert.Null(f.GetGlyph('B'));
        Assert.Equal(new byte[] { 1, 3, 2, 4 }, f.GetGlyph('C')!.Pixels);   // column-major bitmap
        Assert.Null(f.GetGlyph('D'));
        Assert.Null(PgfFont.Parse(new byte[0x200]));
    }

    [Fact]
    public void NewLibValidatesAndAllocatesThroughTheGame()
    {
        var m = Module();
        uint e = Err();
        Assert.Equal(0u, CallU(m, "sceFontNewLib", Params(4), P(0)));
        Assert.Equal(0u, CallU(m, "sceFontNewLib", 0u, P(e)));
        Assert.Equal(unchecked((uint)Pgf.ERR_INVALID_PARAMETER), GuestMemory.R32(e));
        uint noFree = Params(4);
        GuestMemory.W32(noFree + 0x10, 0);
        Assert.Equal(0u, CallU(m, "sceFontNewLib", noFree, P(e)));
        Assert.Equal(unchecked((uint)Pgf.ERR_INVALID_PARAMETER), GuestMemory.R32(e));
        _allocs = _frees = 0;
        foreach (uint bad in new uint[] { 0, 10 })
        {
            Assert.Equal(0u, CallU(m, "sceFontNewLib", Params(bad), P(e)));
            Assert.Equal(unchecked((uint)Pgf.ERR_INVALID_PARAMETER), GuestMemory.R32(e));
        }
        Assert.Equal(_allocs, _frees);
        _failAlloc = true;
        Assert.Equal(0u, CallU(m, "sceFontNewLib", Params(4), P(e)));
        Assert.Equal(unchecked((uint)Pgf.ERR_OUT_OF_MEMORY), GuestMemory.R32(e));
        _failAlloc = false;

        _allocs = _frees = 0;
        uint lib = CallU(m, "sceFontNewLib", Params(9), P(e));
        Assert.NotEqual(0u, lib);
        Assert.Equal(3, _allocs);                       // library block, font table, internal font data
        Assert.Equal(0, Call(m, "sceFontDoneLib", lib));
        Assert.Equal(3, _frees);
        Assert.Equal(Pgf.ERR_INVALID_LIBID, Call(m, "sceFontDoneLib", lib));
    }

    static uint StyleBuf(GuestMemoryFixture fx, int count = 1)
    {
        uint s = fx.Memory.Alloc((uint)(0xA8 * count), "pgf-style", MemoryRegion.User, 16);
        GuestMemory.Fill(s, 0, (uint)(0xA8 * count));
        return s;
    }

    [Fact]
    public void SystemFontListAndMatching()
    {
        var (m, lib) = NewLib();
        uint e = Err();
        Assert.Equal(18, Call(m, "sceFontGetNumFontList", lib, P(e)));
        Assert.Equal(0u, GuestMemory.R32(e));
        Assert.Equal(0, Call(m, "sceFontGetNumFontList", 0x1234u, P(e)));
        Assert.Equal(unchecked((uint)Pgf.ERR_INVALID_LIBID), GuestMemory.R32(e));

        uint list = StyleBuf(fx, 2);
        Assert.Equal(0, Call(m, "sceFontGetFontList", lib, list, 2));
        Assert.Equal(10.125f, GuestMemory.Read<float>(list));
        Assert.Equal(128f, GuestMemory.Read<float>(list + 8));
        Assert.Equal((ushort)103, GuestMemory.Read<ushort>(list + 0x16));           // jpn0: DB
        Assert.Equal("FTT-NewRodin Pro DB", GuestMemory.ReadCString(list + 0x20));
        Assert.Equal("jpn0.pgf", GuestMemory.ReadCString(list + 0x60));
        Assert.Equal("ltn0.pgf", GuestMemory.ReadCString(list + 0xA8 + 0x60));
        Assert.Equal(Pgf.ERR_INVALID_PARAMETER, Call(m, "sceFontGetFontList", lib, 0u, 2));

        uint s = StyleBuf(fx);
        Assert.Equal(0, Call(m, "sceFontGetFontInfoByIndexNumber", lib, s, 17, 0));
        Assert.Equal("kr0.pgf", GuestMemory.ReadCString(s + 0x60));
        Assert.Equal((ushort)3, GuestMemory.Read<ushort>(s + 0x1A));
        Assert.Equal(Pgf.ERR_INVALID_PARAMETER, Call(m, "sceFontGetFontInfoByIndexNumber", lib, s, 18, 0));
        Assert.Equal(Pgf.ERR_INVALID_LIBID, Call(m, "sceFontGetFontInfoByIndexNumber", 0u, s, 0, 0));

        int Optimum(Action<uint> set)
        {
            uint st = StyleBuf(fx);
            set(st);
            return Call(m, "sceFontFindOptimumFont", lib, st, P(e));
        }
        int Find(Action<uint> set)
        {
            uint st = StyleBuf(fx);
            set(st);
            return Call(m, "sceFontFindFont", lib, st, P(e));
        }
        Assert.Equal(0, Optimum(_ => { }));
        // 7 pt at the library's 128 dpi = ltn8 (index 9), an exact size match
        Assert.Equal(9, Optimum(st => { GuestMemory.Write(st, 7f); GuestMemory.Write<ushort>(st + 0x1A, 2); }));
        // 9 pt: 10.125 pt is closer than 7 pt
        Assert.Equal(1, Optimum(st => { GuestMemory.Write(st, 9f); GuestMemory.Write<ushort>(st + 0x1A, 2); }));
        // no size criterion: the LAST matching font wins
        Assert.Equal(16, Optimum(st => GuestMemory.Write<ushort>(st + 0x14, 2)));
        // nothing is rounded: retried with the language alone
        Assert.Equal(17, Optimum(st => { GuestMemory.Write<ushort>(st + 0x14, 3); GuestMemory.Write<ushort>(st + 0x1A, 3); }));
        Assert.Equal(-1, Find(st => GuestMemory.Write<ushort>(st + 0x14, 3)));
        Assert.Equal(0, Find(st => GuestMemory.Write<ushort>(st + 0x16, 103)));
        Assert.Equal(-1, Find(st => { GuestMemory.Write(st, 9f); GuestMemory.Write<ushort>(st + 0x1A, 2); }));
        Assert.Equal(9, Find(st => { GuestMemory.Write(st, 7f); GuestMemory.Write<ushort>(st + 0x1A, 2); }));
        Assert.Equal(4, Find(st => GuestMemory.WriteCString(st + 0x60, "ltn3.pgf")));
        Assert.Equal(0u, GuestMemory.R32(e));
    }

    [Fact]
    public void OpenCloseAndFontInfo()
    {
        var (m, lib) = NewLib(numFonts: 2);
        uint e = Err();
        Assert.Equal(0u, CallU(m, "sceFontOpen", lib, 1, 0, P(0)));
        Assert.Equal(0u, CallU(m, "sceFontOpen", lib, 18, 0, P(e)));
        Assert.Equal(unchecked((uint)Pgf.ERR_INVALID_PARAMETER), GuestMemory.R32(e));
        Assert.Equal(0u, CallU(m, "sceFontOpen", lib, -1, 0, P(e)));
        Assert.Equal(unchecked((uint)Pgf.ERR_INVALID_PARAMETER), GuestMemory.R32(e));
        Assert.Equal(0u, CallU(m, "sceFontOpen", 0u, 1, 0, P(e)));
        Assert.Equal(unchecked((uint)Pgf.ERR_INVALID_LIBID), GuestMemory.R32(e));
        Assert.Equal(0u, CallU(m, "sceFontOpen", lib, 0, 0, P(e)));                 // jpn0.pgf is absent
        Assert.Equal(unchecked((uint)Pgf.ERR_HANDLER_OPEN_FAILED), GuestMemory.R32(e));

        uint f1 = Open(m, lib), f2 = Open(m, lib);
        Assert.NotEqual(f1, f2);
        Assert.Equal(0u, CallU(m, "sceFontOpen", lib, 1, 0, P(e)));
        Assert.Equal(unchecked((uint)Pgf.ERR_TOO_MANY_OPEN_FONTS), GuestMemory.R32(e));
        Assert.Equal(0, Call(m, "sceFontClose", f2));
        Assert.Equal(Pgf.ERR_INVALID_PARAMETER, Call(m, "sceFontClose", f2));
        Assert.Equal(f2, Open(m, lib));
        Assert.Equal(0, Call(m, "sceFontFlush", f1));
        Assert.Equal(Pgf.ERR_INVALID_PARAMETER, Call(m, "sceFontFlush", 0u));

        uint info = fx.Memory.Alloc(0x108, "pgf-info", MemoryRegion.User, 16);
        Assert.Equal(0, Call(m, "sceFontGetFontInfo", f1, info));
        Assert.Equal(1200u, GuestMemory.R32(info));                                 // maxGlyphWidthI = maxSize[0]
        Assert.Equal(896u, GuestMemory.R32(info + 8));                              // maxGlyphAscenderI
        Assert.Equal(-256, GuestMemory.Read<int>(info + 0xC));
        Assert.Equal(1200f / 64, GuestMemory.Read<float>(info + 0x28));
        Assert.Equal(-4f, GuestMemory.Read<float>(info + 0x34));
        Assert.Equal((short)23, GuestMemory.Read<short>(info + 0x50));
        Assert.Equal(2u, GuestMemory.R32(info + 0x54));                             // number of glyphs
        Assert.Equal(0u, GuestMemory.R32(info + 0x58));
        Assert.Equal(10.125f, GuestMemory.Read<float>(info + 0x5C));
        Assert.Equal(128f, GuestMemory.Read<float>(info + 0x64));
        Assert.Equal((ushort)2, GuestMemory.Read<ushort>(info + 0x5C + 0x1A));
        Assert.Equal("ltn0.pgf", GuestMemory.ReadCString(info + 0x5C + 0x60));
        Assert.Equal((byte)4, GuestMemory.Read<byte>(info + 0x104));
        Assert.Equal(Pgf.ERR_INVALID_PARAMETER, Call(m, "sceFontGetFontInfo", f1, 0u));
        Assert.Equal(0, Call(m, "sceFontDoneLib", lib));
        Assert.Equal(Pgf.ERR_INVALID_PARAMETER, Call(m, "sceFontGetFontInfo", f1, info));
    }

    [Fact]
    public void UserFontsFromMemoryAndCallbacks()
    {
        var (m, lib) = NewLib(io: false);
        uint e = Err();
        var data = FontTestData.Pgf();
        uint buf = fx.Memory.Alloc((uint)data.Length, "pgf-user", MemoryRegion.User, 16);
        data.CopyTo(GuestMemory.Span(buf, data.Length));
        uint f = CallU(m, "sceFontOpenUserMemory", lib, buf, (uint)data.Length, P(e));
        Assert.NotEqual(0u, f);
        uint info = fx.Memory.Alloc(0x108, "pgf-info", MemoryRegion.User, 16);
        Assert.Equal(0, Call(m, "sceFontGetFontInfo", f, info));
        Assert.Equal(10.125f, GuestMemory.Read<float>(info + 0x5C));
        Assert.Equal((ushort)0, GuestMemory.Read<ushort>(info + 0x5C + 0x14));     // user fonts have no style codes
        Assert.Equal(0, GuestMemory.Read<byte>(info + 0x5C + 0x60));
        GuestMemory.Fill(buf, 0, 16);
        Assert.Equal(0u, CallU(m, "sceFontOpenUserMemory", lib, buf, (uint)data.Length, P(e)));
        Assert.Equal(unchecked((uint)Pgf.ERR_INVALID_FONT_DATA), GuestMemory.R32(e));
        Assert.Equal(0u, CallU(m, "sceFontOpenUserMemory", lib, buf, 0u, P(e)));
        Assert.Equal(unchecked((uint)Pgf.ERR_INVALID_PARAMETER), GuestMemory.R32(e));

        uint name = fx.Memory.Alloc(32, "pgf-name", MemoryRegion.User, 16);
        GuestMemory.WriteCString(name, "app0:font.pgf");
        Assert.Equal(0u, CallU(m, "sceFontOpenUserFile", lib, name, 0, P(e)));      // stream mode needs the I/O callbacks
        Assert.Equal(unchecked((uint)Pgf.ERR_INVALID_PARAMETER), GuestMemory.R32(e));

        var (m2, lib2) = NewLib(io: true);
        _userFile = data;
        f = CallU(m2, "sceFontOpenUserFile", lib2, name, 0, P(e));
        Assert.NotEqual(0u, f);
        uint rect = fx.Memory.Alloc(4, "pgf-rect", MemoryRegion.User, 16);
        Assert.Equal(0, Call(m2, "sceFontGetCharImageRect", f, (uint)'A', rect));
        Assert.Equal((ushort)3, GuestMemory.Read<ushort>(rect));
    }

    [Fact]
    public void CharInfoUsesTheAltChar()
    {
        var (m, lib) = NewLib();
        uint f = Open(m, lib);
        uint ci = fx.Memory.Alloc(0x3C, "pgf-ci", MemoryRegion.User, 16);
        Assert.Equal(0, Call(m, "sceFontGetCharInfo", f, (uint)'A', ci));
        int[] expected = [3, 2, 1, 2, 192, 128, 128, 0, 64, 128, -96, 16, 256, 320];
        for (int i = 0; i < expected.Length; i++) Assert.Equal(expected[i], GuestMemory.Read<int>(ci + (uint)i * 4));
        Assert.Equal((short)0x33, GuestMemory.Read<short>(ci + 0x38));
        Assert.Equal((short)7, GuestMemory.Read<short>(ci + 0x3A));

        // 'B' is missing and the default alt char (0x5F) too: everything is 0, still a success
        Assert.Equal(0, Call(m, "sceFontGetCharInfo", f, (uint)'B', ci));
        Assert.Equal(0u, GuestMemory.R32(ci));
        Assert.Equal(0, Call(m, "sceFontSetAltCharacterCode", lib, (uint)'C'));
        Assert.Equal(0, Call(m, "sceFontGetCharInfo", f, (uint)'B', ci));
        Assert.Equal((2, 2, 0, 1), (GuestMemory.Read<int>(ci), GuestMemory.Read<int>(ci + 4), GuestMemory.Read<int>(ci + 8), GuestMemory.Read<int>(ci + 12)));
        Assert.Equal(0, Call(m, "sceFontGetCharInfo", f, 0x10u, ci));               // control codes never use the alt char
        Assert.Equal(0u, GuestMemory.R32(ci));
        Assert.Equal(Pgf.ERR_INVALID_LIBID, Call(m, "sceFontSetAltCharacterCode", 0u, 0x41u));
        Assert.Equal(Pgf.ERR_INVALID_PARAMETER, Call(m, "sceFontGetCharInfo", f, (uint)'A', 0u));

        uint rect = fx.Memory.Alloc(4, "pgf-rect", MemoryRegion.User, 16);
        Assert.Equal(0, Call(m, "sceFontGetCharImageRect", f, (uint)'A', rect));
        Assert.Equal((ushort)3, GuestMemory.Read<ushort>(rect));
        Assert.Equal((ushort)2, GuestMemory.Read<ushort>(rect + 2));
    }

    uint Image(uint format, int x64, int y64, int w, int h, int bpl, byte fill)
    {
        uint buf = fx.Memory.Alloc((uint)(bpl * h), "pgf-buf", MemoryRegion.User, 16);
        GuestMemory.Fill(buf, fill, (uint)(bpl * h));
        uint img = fx.Memory.Alloc(0x18, "pgf-img", MemoryRegion.User, 16);
        GuestMemory.W32(img, format);
        GuestMemory.Write(img + 4, x64);
        GuestMemory.Write(img + 8, y64);
        GuestMemory.Write(img + 0xC, (ushort)w);
        GuestMemory.Write(img + 0xE, (ushort)h);
        GuestMemory.Write(img + 0x10, (ushort)bpl);
        GuestMemory.W32(img + 0x14, buf);
        return img;
    }

    static byte[] Row(uint img, int y, int n) => GuestMemory.ReadSpan(GuestMemory.R32(img + 0x14) + (uint)(y * GuestMemory.Read<ushort>(img + 0x10)), n).ToArray();

    [Fact]
    public void GlyphImageIsAddedWithSubpixelSpread()
    {
        var (m, lib) = NewLib();
        uint f = Open(m, lib);
        // 8 bpp, x = 2 + 32/64: each pixel gives half of its coverage to its right neighbour
        uint img = Image(2, 2 * 64 + 32, 64, 8, 4, 8, 0x10);
        Assert.Equal(0, Call(m, "sceFontGetCharGlyphImage", f, (uint)'A', img));
        Assert.Equal(new byte[] { 16, 16, 16, 16, 16, 16, 16, 16 }, Row(img, 0, 8));
        Assert.Equal(new byte[] { 16, 16, 144, 255, 255, 143, 16, 16 }, Row(img, 1, 8));
        Assert.Equal(new byte[] { 16, 16, 16, 84, 84, 16, 16, 16 }, Row(img, 2, 8));

        // 4 bpp: even x in the low nibble
        img = Image(0, 64, 0, 8, 4, 4, 0);
        Assert.Equal(0, Call(m, "sceFontGetCharGlyphImage", f, (uint)'A', img));
        Assert.Equal(new byte[] { 0xF0, 0xFF, 0, 0 }, Row(img, 0, 4));
        Assert.Equal(new byte[] { 0x00, 0x08, 0, 0 }, Row(img, 1, 4));

        foreach (uint bad in new uint[] { 1, 3, 4 })
            Assert.Equal(Pgf.ERR_INVALID_PARAMETER, Call(m, "sceFontGetCharGlyphImage", f, (uint)'A', Image(bad, 0, 0, 8, 4, 8, 0)));
        Assert.Equal(Pgf.ERR_INVALID_PARAMETER, Call(m, "sceFontGetCharGlyphImage", f, (uint)'A', 0u));
        // a missing glyph draws nothing and succeeds whatever the format
        Assert.Equal(0, Call(m, "sceFontGetCharGlyphImage", f, (uint)'B', Image(3, 0, 0, 8, 4, 8, 0)));
    }

    [Fact]
    public void ClippedGlyphImage()
    {
        var (m, lib) = NewLib();
        uint f = Open(m, lib);
        uint img = Image(2, 2 * 64 + 32, 64, 8, 4, 8, 0x10);
        Assert.Equal(0, Call(m, "sceFontGetCharGlyphImage_Clip", f, (uint)'A', img, 0, 0, 4, 4));
        // x = 4 is outside the clip, but the spill from x = 3 still lands there (only the buffer width limits it)
        Assert.Equal(new byte[] { 16, 16, 144, 255, 143, 16, 16, 16 }, Row(img, 1, 8));

        img = Image(2, 2 * 64, 64, 8, 4, 8, 0);
        Assert.Equal(0, Call(m, "sceFontGetCharGlyphImage_Clip", f, (uint)'A', img, 0, 0, 0, 4));
        Assert.Equal(0, Call(m, "sceFontGetCharGlyphImage_Clip", f, (uint)'A', img, 8, 0, 4, 4));
        Assert.Equal(0, Call(m, "sceFontGetCharGlyphImage_Clip", f, (uint)'A', img, -4, 0, 4, 4));
        Assert.All(Row(img, 1, 8), b => Assert.Equal(0, b));
        Assert.Equal(0, Call(m, "sceFontGetCharGlyphImage_Clip", f, (uint)'A', img, 0, 2, 8, 8));
        Assert.All(Row(img, 1, 8), b => Assert.Equal(0, b));
        Assert.Equal(new byte[] { 0, 0, 0, 136, 0, 0, 0, 0 }, Row(img, 2, 8));
    }

    [Fact]
    public void PointPixelConversions()
    {
        var (m, lib) = NewLib();
        uint e = Err();
        Assert.Equal(128f, CallF(m, "sceFontPointToPixelH", lib, 72f, P(e)));
        Assert.Equal(0u, GuestMemory.R32(e));
        Assert.Equal(72f, CallF(m, "sceFontPixelToPointV", lib, 128f, P(e)));
        Assert.Equal(0, Call(m, "sceFontSetResolution", lib, 72f, 96f));
        Assert.Equal(10f, CallF(m, "sceFontPointToPixelH", lib, 10f, P(e)), 4);
        Assert.Equal(96f, CallF(m, "sceFontPointToPixelV", lib, 72f, P(e)), 4);
        Assert.Equal(54f, CallF(m, "sceFontPixelToPointV", lib, 72f, P(e)), 4);
        Assert.Equal(10f, CallF(m, "sceFontPixelToPointH", lib, 10f, P(e)), 4);
        Assert.Equal(Pgf.ERR_INVALID_PARAMETER, Call(m, "sceFontSetResolution", lib, 0f, 96f));
        Assert.Equal(Pgf.ERR_INVALID_LIBID, Call(m, "sceFontSetResolution", 0u, 72f, 72f));
        Assert.Equal(0f, CallF(m, "sceFontPointToPixelH", 0u, 10f, P(e)));
        Assert.Equal(unchecked((uint)Pgf.ERR_INVALID_LIBID), GuestMemory.R32(e));
        Assert.Equal(0f, CallF(m, "sceFontPointToPixelH", lib, 10f, P(0)));
    }

    /// <summary>Against the firmware font when a local Vita3K install has it ('A' of ltn0 is 15x15, see the spec).</summary>
    [Fact]
    public void FirmwareLatinFont()
    {
        if (FontTestData.FirmwareFontDir("pgf") is not { } dir) return;
        var (m, lib) = NewLib(files: name => File.Exists(Path.Combine(dir, name)) ? File.ReadAllBytes(Path.Combine(dir, name)) : null);
        uint f = Open(m, lib, 1);
        uint ci = fx.Memory.Alloc(0x3C, "pgf-ci", MemoryRegion.User, 16);
        Assert.Equal(0, Call(m, "sceFontGetCharInfo", f, (uint)'A', ci));
        Assert.Equal((15, 15, 0, 14), (GuestMemory.Read<int>(ci), GuestMemory.Read<int>(ci + 4), GuestMemory.Read<int>(ci + 8), GuestMemory.Read<int>(ci + 12)));
        Assert.Equal(972, GuestMemory.Read<int>(ci + 0x30));
        uint kr = Open(m, lib, 17);
        uint rect = fx.Memory.Alloc(4, "pgf-rect", MemoryRegion.User, 16);
        Assert.Equal(0, Call(m, "sceFontGetCharImageRect", kr, 0xD55Cu, rect));          // overlay (composed) Hangul
        Assert.True(GuestMemory.Read<ushort>(rect) > 8 && GuestMemory.Read<ushort>(rect + 2) > 8);
        uint img = Image(2, 0, 0, 32, 32, 32, 0);
        Assert.Equal(0, Call(m, "sceFontGetCharGlyphImage", kr, 0xD55Cu, img));
        int ink = 0;
        for (int y = 0; y < 32; y++) ink += Row(img, y, 32).Count(b => b != 0);
        Assert.True(ink > 30);
    }
}
