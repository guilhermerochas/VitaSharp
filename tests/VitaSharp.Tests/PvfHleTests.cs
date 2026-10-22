using System.Reflection;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;

namespace VitaSharp.Tests;

/// <summary>ScePvf: library/font lifecycle, the system font table and matching, FreeType-style metrics of a synthetic
/// TrueType font at the default 10.125 pt / 128 dpi (18 px per em), images, kerning, vertical forms and outlines.</summary>
[Collection("guest")]
public sealed class PvfHleTests(GuestMemoryFixture fx)
{
    static object? Invoke(Pvf m, string name, params object[] args) =>
        typeof(Pvf).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.Invoke(m, args);
    static int Call(Pvf m, string name, params object[] args) => (int)Invoke(m, name, args)!;
    static uint CallU(Pvf m, string name, params object[] args) => (uint)Invoke(m, name, args)!;
    static float CallF(Pvf m, string name, params object[] args) => (float)Invoke(m, name, args)!;
    static Ptr<int> P(uint a) => new(a);

    const uint AllocFn = 0x100, ReallocFn = 0x180, FreeFn = 0x200;
    int _allocs, _frees;

    uint Guest(uint fn, uint[] a)
    {
        switch (fn)
        {
            case AllocFn: _allocs++; return fx.Memory.Alloc(Math.Max(a[1], 4u), "pvf-test", MemoryRegion.User, 16);
            case FreeFn: _frees++; fx.Memory.Free(a[1]); return 0;
        }
        throw new InvalidOperationException($"unexpected guest call {fn:X}");
    }

    Pvf Module(Func<string, byte[]?>? files = null) => new()
    {
        GuestCallOverride = Guest,
        SystemFontOverride = files ?? (name => name is "ltn0.pvf" or "jpn0.pvf" ? FontTestData.Ttf() : null),
    };

    uint InitRec(uint maxFonts)
    {
        uint p = fx.Memory.Alloc(0x20, "pvf-init", MemoryRegion.User, 16);
        GuestMemory.Fill(p, 0, 0x20);
        GuestMemory.W32(p, 0xABC);
        GuestMemory.W32(p + 4, maxFonts);
        GuestMemory.W32(p + 0x10, AllocFn);
        GuestMemory.W32(p + 0x14, ReallocFn);
        GuestMemory.W32(p + 0x18, FreeFn);
        return p;
    }

    uint Err() { uint e = fx.Memory.Alloc(16, "pvf-err", MemoryRegion.User, 16); GuestMemory.W32(e, 0x12345678); return e; }
    uint Buf(uint size) { uint b = fx.Memory.Alloc(size, "pvf-buf", MemoryRegion.User, 16); GuestMemory.Fill(b, 0, size); return b; }

    (Pvf m, uint lib) NewLib(uint maxFonts = 4, Func<string, byte[]?>? files = null)
    {
        var m = Module(files);
        uint e = Err();
        uint lib = CallU(m, "scePvfNewLib", InitRec(maxFonts), P(e));
        Assert.NotEqual(0u, lib);
        Assert.Equal(0, GuestMemory.Read<int>(e));
        return (m, lib);
    }

    uint Open(Pvf m, uint lib, int index = 4)
    {
        uint e = Err();
        uint f = CallU(m, "scePvfOpen", lib, index, 0u, P(e));
        Assert.Equal(0, GuestMemory.Read<int>(e));
        Assert.NotEqual(0u, f);
        return f;
    }

    [Fact]
    public void NewLibValidates()
    {
        var m = Module();
        uint e = Err();
        Assert.Equal(0u, CallU(m, "scePvfNewLib", InitRec(4), P(0)));
        Assert.Equal(0u, CallU(m, "scePvfNewLib", 0u, P(e)));
        Assert.Equal(Pvf.ERR_ARG, GuestMemory.Read<int>(e));
        uint reserved = InitRec(4);
        GuestMemory.W32(reserved + 0xC, 1);
        Assert.Equal(0u, CallU(m, "scePvfNewLib", reserved, P(e)));
        Assert.Equal(Pvf.ERR_ARG, GuestMemory.Read<int>(e));
        uint noRealloc = InitRec(4);
        GuestMemory.W32(noRealloc + 0x14, 0);
        Assert.Equal(0u, CallU(m, "scePvfNewLib", noRealloc, P(e)));
        Assert.Equal(Pvf.ERR_ARG, GuestMemory.Read<int>(e));
        _allocs = _frees = 0;
        foreach (uint bad in new uint[] { 0, 19 })
        {
            Assert.Equal(0u, CallU(m, "scePvfNewLib", InitRec(bad), P(e)));
            Assert.Equal(Pvf.ERR_ARG, GuestMemory.Read<int>(e));
        }
        Assert.Equal(_allocs, _frees);
        _allocs = _frees = 0;
        uint lib = CallU(m, "scePvfNewLib", InitRec(18), P(e));
        Assert.NotEqual(0u, lib);
        Assert.Equal(3, _allocs);
        Assert.Equal(0, Call(m, "scePvfDoneLib", lib));
        Assert.Equal(3, _frees);
        Assert.Equal(Pvf.ERR_LIBID, Call(m, "scePvfDoneLib", lib));
        Assert.Equal(0, Call(m, "__scePvfSetFt2LibCHook", 0x1234u));
        Assert.Equal(0, Call(m, "__scePvfSetFt2DoneLibCHook", 0u));
    }

    [Fact]
    public void SystemFontListAndMatching()
    {
        var (m, lib) = NewLib();
        uint e = Err();
        Assert.Equal(18, Call(m, "scePvfGetNumFontList", lib, P(e)));
        uint list = Buf(0xD8 * 3);
        Assert.Equal(0, Call(m, "scePvfGetFontList", lib, list, 3));
        Assert.Equal("jpn0.pvf", GuestMemory.ReadCString(list + 0x90));
        Assert.Equal("jpn2.pvf", GuestMemory.ReadCString(list + 2 * 0xD8 + 0x90));
        Assert.Equal((ushort)5, GuestMemory.Read<ushort>(list + 2 * 0xD8 + 6));
        uint s = Buf(0xD8);
        Assert.Equal(0, Call(m, "scePvfGetFontInfoByIndexNumber", lib, s, 4));
        Assert.Equal(("ltn0.pvf", (ushort)1, (ushort)1, (ushort)2), (GuestMemory.ReadCString(s + 0x90), GuestMemory.Read<ushort>(s + 4), GuestMemory.Read<ushort>(s + 6), GuestMemory.Read<ushort>(s + 0xA)));
        Assert.Equal(Pvf.ERR_ARG, Call(m, "scePvfGetFontInfoByIndexNumber", lib, s, 18));
        Assert.Equal(Pvf.ERR_LIBID, Call(m, "scePvfGetFontList", 0u, list, 1));

        int Optimum(Action<uint> set) { uint st = Buf(0xD8); set(st); return Call(m, "scePvfFindOptimumFont", lib, st, P(e)); }
        int Find(Action<uint> set) { uint st = Buf(0xD8); set(st); return Call(m, "scePvfFindFont", lib, st, P(e)); }
        Assert.Equal(0, Optimum(_ => { }));
        Assert.Equal(4, Optimum(st => GuestMemory.Write<ushort>(st + 0xA, 2)));
        Assert.Equal(5, Optimum(st => { GuestMemory.Write<ushort>(st + 4, 2); GuestMemory.Write<ushort>(st + 0xA, 2); }));
        Assert.Equal(14, Optimum(st => { GuestMemory.Write<ushort>(st + 6, 5); GuestMemory.Write<ushort>(st + 0xA, 3); }));
        Assert.Equal(4, Optimum(st => { GuestMemory.Write<ushort>(st + 4, 3); GuestMemory.Write<ushort>(st + 0xA, 2); }));
        Assert.Equal(-1, Find(st => GuestMemory.Write<ushort>(st + 4, 3)));
        Assert.Equal(17, Find(st => GuestMemory.WriteCString(st + 0x90, "cn1.pvf")));
        Assert.Equal(-1, Find(st => GuestMemory.Write(st, 400f)));                   // every system entry has weight 0
        Assert.Equal(0, GuestMemory.Read<int>(e));
    }

    [Fact]
    public void OpenAndClose()
    {
        var (m, lib) = NewLib(maxFonts: 2);
        uint e = Err();
        Assert.Equal(0u, CallU(m, "scePvfOpen", lib, 4, 2u, P(e)));
        Assert.Equal(Pvf.ERR_ARG, GuestMemory.Read<int>(e));
        Assert.Equal(0u, CallU(m, "scePvfOpen", lib, 18, 0u, P(e)));
        Assert.Equal(Pvf.ERR_ARG, GuestMemory.Read<int>(e));
        Assert.Equal(0u, CallU(m, "scePvfOpen", 0u, 4, 0u, P(e)));
        Assert.Equal(Pvf.ERR_LIBID, GuestMemory.Read<int>(e));
        Assert.Equal(0u, CallU(m, "scePvfOpen", lib, 1, 0u, P(e)));                 // jpn1.pvf is absent
        Assert.Equal(Pvf.ERR_FILEOPEN, GuestMemory.Read<int>(e));

        uint latin = CallU(m, "scePvfOpenDefaultLatinFontOnSharedMemory", lib, P(e));
        Assert.NotEqual(0u, latin);
        uint japanese = CallU(m, "scePvfOpenDefaultJapaneseFontOnSharedMemory", lib, P(e));
        Assert.NotEqual(0u, japanese);
        Assert.Equal(0u, CallU(m, "scePvfOpen", lib, 4, 0u, P(e)));
        Assert.Equal(Pvf.ERR_TOOMANYOPENED, GuestMemory.Read<int>(e));
        Assert.Equal(0, Call(m, "scePvfClose", japanese));
        Assert.Equal(Pvf.ERR_ARG, Call(m, "scePvfClose", japanese));
        Assert.Equal(japanese, Open(m, lib));
        Assert.Equal(0, Call(m, "scePvfFlush", latin));

        uint info = Buf(0x130);
        Assert.Equal(0, Call(m, "scePvfGetFontInfo", latin, info));
        Assert.Equal(3u, GuestMemory.R32(info + 0x50));                            // numChars
        Assert.Equal(922, GuestMemory.Read<int>(info + 8));                         // ascender 800 units at 1.152
        Assert.Equal(-230, GuestMemory.Read<int>(info + 0xC));
        Assert.Equal("ltn0.pvf", GuestMemory.ReadCString(info + 0x54 + 0x90));
    }

    [Fact]
    public void UserFonts()
    {
        var (m, lib) = NewLib();
        uint e = Err();
        var ttf = FontTestData.Ttf();
        uint mem = Buf((uint)ttf.Length);
        ttf.CopyTo(GuestMemory.Span(mem, ttf.Length));
        uint f = CallU(m, "scePvfOpenUserMemory", lib, mem, (uint)ttf.Length, P(e));
        Assert.NotEqual(0u, f);
        Assert.Equal(1, Call(m, "scePvfIsElement", f, (ushort)'A'));
        uint info = Buf(0x130);
        Assert.Equal(0, Call(m, "scePvfGetFontInfo", f, info));
        Assert.Equal(0, GuestMemory.Read<byte>(info + 0x54 + 0x90));                 // user fonts have no style entry
        Assert.Equal(0u, CallU(m, "scePvfOpenUserMemoryWithSubfontIndex", lib, mem, (uint)ttf.Length, 1u, P(e)));
        Assert.Equal(Pvf.ERR_ARG, GuestMemory.Read<int>(e));
        uint junk = Buf(256);
        Assert.Equal(0u, CallU(m, "scePvfOpenUserMemory", lib, junk, 256u, P(e)));
        Assert.Equal(Pvf.ERR_ILLEGALVERSION, GuestMemory.Read<int>(e));
        Assert.Equal(0u, CallU(m, "scePvfOpenUserMemory", lib, mem, 0u, P(e)));
        Assert.Equal(Pvf.ERR_ARG, GuestMemory.Read<int>(e));
        uint name = Buf(32);
        GuestMemory.WriteCString(name, "app0:none.ttf");
        Assert.Equal(0u, CallU(m, "scePvfOpenUserFile", lib, name, 0u, P(e)));
        Assert.Equal(Pvf.ERR_FILEOPEN, GuestMemory.Read<int>(e));
        Assert.Equal(0u, CallU(m, "scePvfOpenUserFileWithSubfontIndex", lib, 0u, 0u, 0u, P(e)));
        Assert.Equal(Pvf.ERR_ARG, GuestMemory.Read<int>(e));
        Assert.Equal(0, Call(m, "scePvfClose", f));
    }

    [Fact]
    public void CharInfoHasFreeTypeMetrics()
    {
        var (m, lib) = NewLib();
        uint f = Open(m, lib);
        uint ci = Buf(0x40);
        GuestMemory.Fill(ci, 0xEE, 0x40);
        Assert.Equal(0, Call(m, "scePvfGetCharInfo", f, (ushort)'A', ci));
        // bitmap fields are not filled by libpvf here
        for (uint o = 0; o < 0x14; o += 4) Assert.Equal(0u, GuestMemory.R32(ci + o));
        // square (100,0)-(500,700) at 1.152 26.6-units per font unit, grid fitted
        int[] expected = [512, 832, 832, 0, 64, 832, -256, 64, 704, 1152];
        for (int i = 0; i < expected.Length; i++) Assert.Equal(expected[i], GuestMemory.Read<int>(ci + 0x14 + (uint)i * 4));

        Assert.Equal(1, Call(m, "scePvfIsElement", f, (ushort)'V'));
        Assert.Equal(0, Call(m, "scePvfIsElement", f, (ushort)'Z'));
        Assert.Equal(0, Call(m, "scePvfIsElement", f, (ushort)0xFFFF));
        Assert.Equal(0, Call(m, "scePvfIsElement", 0u, (ushort)'A'));
        // missing code: the alt char (0x5F, also missing) → .notdef; alt char 0 → error
        Assert.Equal(0, Call(m, "scePvfGetCharInfo", f, (ushort)'Z', ci));
        Assert.Equal(0, Call(m, "scePvfSetAltCharacterCode", lib, (ushort)0));
        Assert.Equal(Pvf.ERR_NO_ALT_CHAR, Call(m, "scePvfGetCharInfo", f, (ushort)'Z', ci));
        Assert.Equal(0, Call(m, "scePvfSetAltCharacterCode", lib, (ushort)'A'));
        Assert.Equal(0, Call(m, "scePvfGetCharInfo", f, (ushort)'Z', ci));
        Assert.Equal(512, GuestMemory.Read<int>(ci + 0x14));
        Assert.Equal(Pvf.ERR_ARG, Call(m, "scePvfGetCharInfo", f, (ushort)'A', 0u));

        // 36 pt = 64 px per em: height 700 x 4.096 = 2867.2 → 2880
        Assert.Equal(0, Call(m, "scePvfSetCharSize", f, 36f, 36f));
        Assert.Equal(0, Call(m, "scePvfGetCharInfo", f, (ushort)'A', ci));
        Assert.Equal(2880, GuestMemory.Read<int>(ci + 0x18));
        Assert.Equal(Pvf.ERR_ARG, Call(m, "scePvfSetCharSize", f, 0f, 10f));
    }

    uint Image(uint format, int x64, int y64, int w, int h, int bpl)
    {
        uint buf = Buf((uint)(bpl * h));
        uint img = Buf(0x18);
        GuestMemory.W32(img, format);
        GuestMemory.Write(img + 4, x64);
        GuestMemory.Write(img + 8, y64);
        GuestMemory.Write(img + 0xC, (ushort)w);
        GuestMemory.Write(img + 0xE, (ushort)h);
        GuestMemory.Write(img + 0x10, (ushort)bpl);
        GuestMemory.W32(img + 0x14, buf);
        return img;
    }

    static byte Pixel(uint img, int x, int y) => GuestMemory.Read<byte>(GuestMemory.R32(img + 0x14) + (uint)(y * GuestMemory.Read<ushort>(img + 0x10) + x));

    [Fact]
    public void GlyphImageAtThePen()
    {
        var (m, lib) = NewLib();
        uint f = Open(m, lib);
        uint rect = Buf(4);
        Assert.Equal(0, Call(m, "scePvfGetCharImageRect", f, (ushort)'A', rect));
        Assert.Equal(((ushort)8, (ushort)13), (GuestMemory.Read<ushort>(rect), GuestMemory.Read<ushort>(rect + 2)));

        // pen at (0, 20) on the baseline: the square covers x 1.8..9, y 7.4..20
        uint img = Image(2, 0, 20 * 64, 16, 24, 16);
        Assert.Equal(0, Call(m, "scePvfGetCharGlyphImage", f, (ushort)'A', img));
        Assert.Equal(255, Pixel(img, 5, 12));
        Assert.Equal(255, Pixel(img, 5, 19));
        Assert.Equal(0, Pixel(img, 5, 20));
        Assert.Equal(0, Pixel(img, 0, 12));
        Assert.Equal(0, Pixel(img, 9, 12));
        Assert.InRange(Pixel(img, 1, 12), 30, 80);                                   // 20 % covered
        Assert.InRange(Pixel(img, 5, 7), 130, 175);                                  // 60 % covered

        img = Image(0, 0, 20 * 64, 16, 24, 8);
        Assert.Equal(0, Call(m, "scePvfGetCharGlyphImage", f, (ushort)'A', img));
        Assert.Equal(0xFF, Pixel(img, 2, 12));                                       // x 4 and 5, 4 bpp
        Assert.Equal(Pvf.ERR_ARG, Call(m, "scePvfGetCharGlyphImage", f, (ushort)'A', Image(1, 0, 0, 16, 24, 16)));
        Assert.Equal(Pvf.ERR_ARG, Call(m, "scePvfGetCharGlyphImage", f, (ushort)'A', 0u));

        img = Image(2, 0, 20 * 64, 16, 24, 16);
        Assert.Equal(0, Call(m, "scePvfGetCharGlyphImage_Clip", f, (ushort)'A', img, 0, 0, 4u, 24u));
        Assert.Equal(255, Pixel(img, 3, 12));
        Assert.Equal(0, Pixel(img, 5, 12));
        Assert.Equal(0, Call(m, "scePvfGetCharGlyphImage_Clip", f, (ushort)'A', img, 16, 0, 4u, 24u));
        Assert.Equal(0, Pixel(img, 5, 12));
    }

    [Fact]
    public void SyntheticStyles()
    {
        var (m, lib) = NewLib();
        uint f = Open(m, lib);
        uint rect = Buf(4);
        Assert.Equal(Pvf.ERR_ARG, Call(m, "scePvfSetEmboldenRate", f, 41f));
        Assert.Equal(Pvf.ERR_ARG, Call(m, "scePvfSetSkewValue", f, 0f, 31f));
        Assert.Equal(0, Call(m, "scePvfSetSkewValue", f, 15f, 0f));
        Assert.Equal(0, Call(m, "scePvfGetCharImageRect", f, (ushort)'A', rect));
        Assert.True(GuestMemory.Read<ushort>(rect) > 8);                            // slanted to the right
        Assert.Equal(0, Call(m, "scePvfSetSkewValue", f, 0f, 0f));
        Assert.Equal(0, Call(m, "scePvfSetEmboldenRate", f, 20f));
        Assert.Equal(0, Call(m, "scePvfGetCharImageRect", f, (ushort)'A', rect));
        Assert.True(GuestMemory.Read<ushort>(rect) > 8 && GuestMemory.Read<ushort>(rect + 2) > 13);
        Assert.Equal(0, Call(m, "scePvfSetEmboldenRate", f, -20f));
    }

    [Fact]
    public void KerningAndVerticalForms()
    {
        var (m, lib) = NewLib();
        uint f = Open(m, lib);
        uint k = Buf(0x10);
        Assert.Equal(0, Call(m, "scePvfGetKerningInfo", f, (ushort)'A', (ushort)'V', k));
        Assert.Equal(-64, GuestMemory.Read<int>(k));                                // -50 x 1.152 = -57.6, rounded to a pixel
        Assert.Equal(-1f, GuestMemory.Read<float>(k + 8));
        Assert.Equal(0, Call(m, "scePvfGetKerningInfo", f, (ushort)'V', (ushort)'A', k));
        Assert.Equal(0, GuestMemory.Read<int>(k));

        Assert.Equal(1, Call(m, "scePvfIsVertElement", f, (ushort)0x3001));
        Assert.Equal(0, Call(m, "scePvfIsVertElement", f, (ushort)'A'));
        uint rect = Buf(4);
        Assert.Equal(0, Call(m, "scePvfGetCharImageRect", f, (ushort)0x3001, rect));
        Assert.Equal((ushort)5, GuestMemory.Read<ushort>(rect));                     // x 1.8..5.4
        Assert.Equal(0, Call(m, "scePvfGetVertCharImageRect", f, (ushort)0x3001, rect));
        Assert.Equal((ushort)5, GuestMemory.Read<ushort>(rect));                     // the substituted glyph, x 10.8..14.4
        uint ci = Buf(0x40);
        Assert.Equal(0, Call(m, "scePvfGetVertCharInfo", f, (ushort)0x3001, ci));
        Assert.Equal(320, GuestMemory.Read<int>(ci + 0x14));
        Assert.Equal(640, GuestMemory.Read<int>(ci + 0x24));                        // horizontal bearing of glyph 4, not 3
        uint img = Image(2, 10 * 64, 0, 32, 32, 32);
        Assert.Equal(0, Call(m, "scePvfGetVertCharGlyphImage", f, (ushort)0x3001, img));
        Assert.Equal(0, Call(m, "scePvfGetVertCharGlyphImage_Clip", f, (ushort)0x3001, img, 0, 0, 32u, 32u));
        int ink = 0;
        for (int y = 0; y < 32; y++) for (int x = 0; x < 32; x++) ink += Pixel(img, x, y) != 0 ? 1 : 0;
        Assert.True(ink > 0);
    }

    [Fact]
    public void OutlinesAreAllocatedAndReleased()
    {
        var (m, lib) = NewLib();
        uint f = Open(m, lib);
        uint o = Buf(0x14);
        _allocs = _frees = 0;
        Assert.Equal(0, Call(m, "scePvfGetCharGlyphOutline", f, (ushort)'A', o));
        Assert.Equal(3, _allocs);
        Assert.Equal((short)1, GuestMemory.Read<short>(o));
        Assert.Equal((short)4, GuestMemory.Read<short>(o + 2));
        uint pts = GuestMemory.R32(o + 4), tags = GuestMemory.R32(o + 8), ends = GuestMemory.R32(o + 0xC);
        Assert.Equal((115, 0), (GuestMemory.Read<int>(pts), GuestMemory.Read<int>(pts + 4)));
        Assert.Equal((115, 806), (GuestMemory.Read<int>(pts + 8), GuestMemory.Read<int>(pts + 12)));
        Assert.Equal(1, GuestMemory.Read<byte>(tags));
        Assert.Equal((short)3, GuestMemory.Read<short>(ends));
        Assert.Equal(0, Call(m, "scePvfReleaseCharGlyphOutline", f, o));
        Assert.Equal(3, _frees);
        Assert.Equal(0u, GuestMemory.R32(o + 4));
        Assert.Equal(0, Call(m, "scePvfGetVertCharGlyphOutline", f, (ushort)0x3001, o));
        Assert.Equal((691, 691), (GuestMemory.Read<int>(GuestMemory.R32(o + 4)), GuestMemory.Read<int>(GuestMemory.R32(o + 4) + 4)));
        Assert.Equal(0, Call(m, "scePvfReleaseCharGlyphOutline", f, o));
        Assert.Equal(Pvf.ERR_ARG, Call(m, "scePvfGetCharGlyphOutline", f, (ushort)'A', 0u));
        Assert.Equal(Pvf.ERR_ARG, Call(m, "scePvfReleaseCharGlyphOutline", 0u, o));
    }

    [Fact]
    public void ResolutionEmAndConversions()
    {
        var (m, lib) = NewLib();
        uint e = Err();
        Assert.Equal(128f, CallF(m, "scePvfPointToPixelH", lib, 72f, P(e)));
        Assert.Equal(0, Call(m, "scePvfSetResolution", lib, 72f, 96f));
        Assert.Equal(96f, CallF(m, "scePvfPointToPixelV", lib, 72f, P(e)), 4);
        Assert.Equal(72f, CallF(m, "scePvfPixelToPointH", lib, 72f, P(e)), 4);
        Assert.Equal(54f, CallF(m, "scePvfPixelToPointV", lib, 72f, P(e)), 4);
        Assert.Equal(Pvf.ERR_ARG, Call(m, "scePvfSetResolution", lib, -1f, 96f));
        Assert.Equal(Pvf.ERR_LIBID, Call(m, "scePvfSetResolution", 0u, 72f, 72f));
        Assert.Equal(0, Call(m, "scePvfSetEM", lib, 0f));
        Assert.Equal(Pvf.ERR_ARG, Call(m, "scePvfSetEM", lib, -1f));
        Assert.Equal(0f, CallF(m, "scePvfPointToPixelH", 0u, 72f, P(e)));
        Assert.Equal(Pvf.ERR_LIBID, GuestMemory.Read<int>(e));
        // the char size follows the library resolution: 10.125 pt is 10.125 px wide (72 dpi), 13.5 px high (96 dpi)
        uint f = Open(m, lib);
        uint rect = Buf(4);
        Assert.Equal(0, Call(m, "scePvfGetCharImageRect", f, (ushort)'A', rect));
        Assert.Equal((ushort)5, GuestMemory.Read<ushort>(rect));                    // x 1.01..5.06 at 72 dpi
        Assert.Equal((ushort)10, GuestMemory.Read<ushort>(rect + 2));               // 700 x 13.5 / 1000 = 9.45 at 96 dpi
    }

    /// <summary>Against the firmware fonts when a local Vita3K install has them.</summary>
    [Fact]
    public void FirmwareFonts()
    {
        if (FontTestData.FirmwareFontDir("pvf") is not { } dir) return;
        var (m, lib) = NewLib(files: name => File.Exists(Path.Combine(dir, name)) ? File.ReadAllBytes(Path.Combine(dir, name)) : null);
        uint latin = Open(m, lib, 4), japanese = Open(m, lib, 0);
        uint rect = Buf(4);
        Assert.Equal(0, Call(m, "scePvfGetCharImageRect", latin, (ushort)'A', rect));
        Assert.InRange(GuestMemory.Read<ushort>(rect + 2), 12, 16);                 // cap height at 18 px per em
        Assert.Equal(1, Call(m, "scePvfIsVertElement", japanese, (ushort)0x30FC));  // ー has a vertical form
        Assert.Equal(1, Call(m, "scePvfIsElement", japanese, (ushort)0x3042));
        Assert.Equal(0, Call(m, "scePvfIsElement", latin, (ushort)0x3042));
    }
}
