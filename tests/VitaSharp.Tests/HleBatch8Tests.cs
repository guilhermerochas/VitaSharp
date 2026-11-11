using System.Reflection;
using System.Text;
using VitaSharp.Core;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using Xunit;

namespace VitaSharp.Tests;

/// <summary>HLE batch 8: libpaf's classes (rect, rtc/DateTime, strings and paths, files, SHA-1, zlib, heaps, buffers).</summary>
[Collection("guest")]
public sealed class HleBatch8Tests(GuestMemoryFixture fx) : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "vitasharp-hle8-" + Guid.NewGuid().ToString("N"));
    Emulator? _emu;

    Emulator Emu()
    {
        if (_emu != null)
        {
            return _emu;
        }

        Directory.CreateDirectory(_root);
        return _emu = new Emulator(new EmulatorOptions { FsRoot = _root, Memory = fx.Memory, EnableJit = false, CpuBackend = "interp" });
    }

    Paf P => Emu().Hle.Modules.OfType<Paf>().Single();
    Libc L => Emu().Hle.Modules.OfType<Libc>().Single();

    static object? Invoke(object module, string name, params object?[] args) =>
        module.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!.Invoke(module, args);

    uint Alloc(uint size)
    {
        uint p = fx.Memory.Alloc(size, "hle8");
        GuestMemory.Fill(p, 0, size);
        return p;
    }
    uint Str(string s)
    {
        var b = Encoding.UTF8.GetBytes(s);
        uint p = Alloc((uint)b.Length + 1);
        b.CopyTo(GuestMemory.Span(p, b.Length));
        return p;
    }
    static float F(uint a) => GuestMemory.Read<float>(a);
    static string PStr(uint s) => GuestMemory.R32(s + 4) == 0 ? "" : Encoding.UTF8.GetString(GuestMemory.ReadSpan(GuestMemory.R32(s), (int)GuestMemory.R32(s + 4)));

    public void Dispose()
    {
        _emu?.Dispose();
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException) { }
    }

    [Fact]
    public void RectKeepsTheOppositeEdgeAndClampsTheSize()
    {
        uint r = Alloc(16), o = Alloc(16), v = Alloc(8);
        Invoke(P, "RectSet", r, 10f, 20f, 4f, -2f);
        Assert.Equal(0f, F(r + 0xC)); // negative height stored as 0
        Invoke(P, "RectSetHeight", r, 6f);
        Assert.Equal(8f, (float)Invoke(P, "RectLeftEdge", r)!);
        Assert.Equal(23f, (float)Invoke(P, "RectTopEdge", r)!);
        Invoke(P, "RectSetLeft", r, 6f); // right edge (12) stays
        Assert.Equal(9f, F(r));
        Assert.Equal(6f, F(r + 8));
        Invoke(P, "RectSetLeft", r, 20f); // past the right edge: stops there
        Assert.Equal(12f, F(r));
        Assert.Equal(0f, F(r + 8));
        Invoke(P, "RectSet", r, 0f, 0f, 2f, 2f);
        Invoke(P, "RectSet", o, 1f, 1f, 2f, 2f);
        Assert.True((bool)Invoke(P, "RectHit", r, o)!);
        Invoke(P, "RectSet", o, 2f, 0f, 2f, 2f); // edges touch
        Assert.False((bool)Invoke(P, "RectHit", r, o)!);
        Assert.True((bool)Invoke(P, "RectHasPoint", r, 1f, -1f)!);
        Invoke(P, "RectLtrb", v = Alloc(16), r);
        Assert.Equal([-1f, 1f, 1f, -1f], [F(v), F(v + 4), F(v + 8), F(v + 12)]);
    }

    [Fact]
    public void SqliteDatesParseFieldByField()
    {
        Assert.Equal(0, Paf.ParseSqliteDate("2013-05-07 10:20:30.25", out var d, out int f, false));
        Assert.Equal((2013, 5, 7, 10, 20, 30, 250000u), ((int)d.Y, (int)d.Mo, (int)d.D, (int)d.H, (int)d.Mi, (int)d.S, d.Us));
        Assert.Equal(7, f);
        Assert.Equal(0, Paf.ParseSqliteDate("2013-05", out d, out f, true));
        Assert.Equal((2, 1), (f, (int)d.D));
        Assert.Equal(unchecked((int)0x80251083), Paf.ParseSqliteDate("2013-05-x7", out _, out _, false));
        Assert.Equal(unchecked((int)0x80251080), Paf.ParseSqliteDate("2013/05/07", out _, out _, false));
        Assert.Equal(unchecked((int)0x80251002), Paf.ParseSqliteDate("2013-02-30", out _, out _, false));
    }

    [Fact]
    public void DateTimeTicksAndWeekdays()
    {
        Assert.Equal(3, (int)Invoke(P, "DtDayOfWeek", 2013u, 5u, 1u)!); // a Wednesday
        Assert.Equal(0, (int)Invoke(P, "DtDayOfWeek", 2000u, 1u, 2u)!); // a Sunday
        uint dt = Alloc(0x18), tick = Alloc(8), back = Alloc(0x10);
        Invoke(P, "DtCtor", dt, 2012u, 2u, 29u, 23u, 59u, 58u, 5u);
        ulong t = (ulong)Invoke(P, "DtToTick", dt)!;
        GuestMemory.Write(tick, t);
        Assert.Equal(0, (int)Invoke(P, "RtcAddYears", tick, tick, 1)!); // 29 Feb + 1 year = 28 Feb
        Assert.Equal(0, (int)Invoke(P, "RtcTickToDate", tick, back, 0u)!);
        Assert.Equal((2013, 2, 28), (GuestMemory.Read<ushort>(back), GuestMemory.Read<ushort>(back + 2), GuestMemory.Read<ushort>(back + 4)));
    }

    [Fact]
    public void StringsPathsAndNumbers()
    {
        uint w = Alloc(12), s = Alloc(12), a = Alloc(12), b = Alloc(12), ret = Alloc(12);
        Assert.Equal(5, (int)Invoke(P, "U8ToU16", Str("héllo"), w)!);
        Assert.Equal(6, (int)Invoke(P, "U16sToU8", w, s)!);
        Assert.Equal("héllo", PStr(s));
        Assert.Equal(-1234, (int)Invoke(P, "FromStringInt", Str("-1234"), 5u)!);
        Assert.Equal(102, (int)Invoke(P, "FromStringInt", Str("1x2"), 3u)!); // other characters count as 0
        typeof(Paf).GetMethod("StrAssign", BindingFlags.Instance | BindingFlags.NonPublic, [typeof(uint), typeof(string)])!.Invoke(P, [a, "ux0:data/a/b.txt"]);
        Invoke(P, "PathSplit", a, s, b);
        Assert.Equal(("ux0:data/a/", "b.txt"), (PStr(s), PStr(b)));
        Invoke(P, "PathSplitExt", a, s, b);
        Assert.Equal(("ux0:data/a/b", ".txt"), (PStr(s), PStr(b)));
        Assert.Equal("ux0:data/c.txt", Paf.JoinPath("ux0:data/a/", "../c.txt"));
        Assert.Equal("ux0:data/a/c", Paf.JoinPath("ux0:data/a", "./c"));
    }

    [Fact]
    public void Sha1MatchesTheRfcVectors()
    {
        uint ctx = Alloc(0x68), dig = Alloc(20), data = Str("abc");
        Assert.Equal(0, (int)Invoke(P, "Sha1Reset", ctx)!);
        Assert.Equal(0, (int)Invoke(P, "Sha1Input", ctx, data, 3u)!);
        Assert.Equal(0, (int)Invoke(P, "Sha1Result", ctx, dig)!);
        Assert.Equal("A9993E364706816ABA3E25717850C26C9CD0D89D", Convert.ToHexString(GuestMemory.ReadSpan(dig, 20)));
        Assert.Equal(3, (int)Invoke(P, "Sha1Input", ctx, data, 3u)!); // after the result: state error
        string longText = "abcdbcdecdefdefgefghfghighijhijkijkljklmklmnlmnomnopnopq";
        Invoke(P, "Sha1Reset", ctx);
        Invoke(P, "Sha1Input", ctx, Str(longText), (uint)longText.Length);
        Invoke(P, "Sha1Result", ctx, dig);
        Assert.Equal("84983E441C3BD26EBAAE4AA1F95129E5E54670F1", Convert.ToHexString(GuestMemory.ReadSpan(dig, 20)));
    }

    [Fact]
    public void ZlibDecompressesIntoTheCallersBuffer()
    {
        var plain = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("libpaf zlib ", 40)));
        var ms = new MemoryStream();
        using (var z = new System.IO.Compression.ZLibStream(ms, System.IO.Compression.CompressionLevel.Optimal, true))
        {
            z.Write(plain);
        }

        var packed = ms.ToArray();
        uint src = Alloc((uint)packed.Length), dst = Alloc(0x400);
        packed.CopyTo(GuestMemory.Span(src, packed.Length));
        Assert.Equal(plain.Length, (int)Invoke(P, "Decompress", dst, 0x400u, src, (uint)packed.Length, 1)!);
        Assert.True(GuestMemory.ReadSpan(dst, plain.Length).SequenceEqual(plain));
        Assert.Equal(unchecked((int)0x80AF5700), (int)Invoke(P, "Decompress", dst, 0x400u, src, (uint)packed.Length, 2)!);
        Assert.Equal(unchecked((int)0x80AF5704), (int)Invoke(P, "Decompress", dst, 0x10u, src, (uint)packed.Length, 1)!);
    }

    [Fact]
    public void HeapAllocatorServesFromItsOwnMemory()
    {
        uint heap = Alloc(0x5C), mem = Alloc(0x10000), name = Str("test heap");
        Invoke(P, "HeapCtor", heap, mem, 0x10000u, name, 0u);
        uint p = (uint)Invoke(P, "HeapMalloc", heap, 0x100u)!;
        Assert.InRange(p, mem, mem + 0x10000 - 0x100);
        Assert.True((uint)Invoke(P, "HeapUsable", heap, p)! >= 0x100);
        uint before = (uint)Invoke(P, "HeapFreeSize", heap)!;
        Invoke(P, "HeapFree", heap, p);
        Assert.True((uint)Invoke(P, "HeapFreeSize", heap)! > before);
        Assert.Equal("test heap", GuestMemory.ReadCString((uint)Invoke(P, "HeapName", heap)!));
    }

    [Fact]
    public void LocalFileStaticHelpersAndDirectories()
    {
        Directory.CreateDirectory(Path.Combine(_root, "ux0", "data"));
        File.WriteAllText(Path.Combine(_root, "ux0", "data", "paf.txt"), "hello paf");
        Assert.True((bool)Invoke(P, "LfExists", Str("ux0:data/paf.txt"))!);
        Assert.True((bool)Invoke(P, "LfIsFile", Str("ux0:data/paf.txt"))!);
        Assert.False((bool)Invoke(P, "LfIsDir", Str("ux0:data/paf.txt"))!);
        Assert.Equal(9L, (long)Invoke(P, "LfFileSize", Str("ux0:data/paf.txt"))!);
        Assert.Equal(0, (int)Invoke(P, "DirCreateRecursive", Str("ux0:data/x/y/z"))!);
        Assert.True(Directory.Exists(Path.Combine(_root, "ux0", "data", "x", "y", "z")));
        Assert.Equal(0, (int)Invoke(P, "DirRemoveRecursive", Str("ux0:data/x"))!);
        Assert.False(Directory.Exists(Path.Combine(_root, "ux0", "data", "x")));
        Assert.Equal(0, (int)Invoke(P, "DirRemove", Str("ux0:data/none"))!); // nothing to remove
    }

    [Fact]
    public void PafCLibraryNames()
    {
        uint a = Str("abc"), w = Alloc(16);
        Assert.Equal(-1, (int)Invoke(L, "sce_paf_bcmp", a, Str("abd"), 3)!);
        Invoke(L, "sce_paf_wmemset", w, 0x41, 4);
        Assert.Equal(0x41, GuestMemory.Read<ushort>(w + 6));
        Assert.Equal(-1, (int)Invoke(L, "sce_paf_wmemcmp", w, Str("BB"), 1)!);
        Assert.Equal(3f, (float)Invoke(L, "sce_paf_floorf", 3.7f)!);
        Assert.Equal(0.0, (double)Invoke(L, "sce_paf_fmod", 5.0, 0.0)!);
    }

    [Fact]
    public void CxmlDocumentWalksElementsAndAttributes()
    {
        var buf = new byte[0xC0];
        void W(int at, int v) => BitConverter.GetBytes(v).CopyTo(buf, at);
        "CXML"u8.ToArray().CopyTo(buf, 0);
        W(4, 0x110);
        int[] tables = [0x58, 0x58, 0xBE, 0, 0xBE, 0, 0xB0, 14, 0xBE, 0, 0xBE, 0, 0xBE, 0, 0xBE, 0, 0xBE, 0];
        for (int i = 0; i < tables.Length; i++)
        {
            W(8 + i * 4, tables[i]);
        }

        int[] tree = [0, 2, -1, -1, -1, 0x3C, 0x3C, 5, 1, 5, 0, 7, 3, 11, 2, 9, 0, 0, -1, -1, -1, -1];
        for (int i = 0; i < tree.Length; i++)
        {
            W(0x58 + i * 4, tree[i]);
        }

        "root\0a\0s\0c\0hi\0"u8.ToArray().CopyTo(buf, 0xB0);
        uint src = Alloc((uint)buf.Length), doc = Alloc(0xAC), root = Alloc(8), child = Alloc(8), attr = Alloc(12), v = Alloc(4), len = Alloc(4);
        buf.CopyTo(GuestMemory.Span(src, buf.Length));
        Invoke(P, "CxDocCtor", doc);
        Assert.Equal(0, (int)Invoke(P, "CxFromBuffer", doc, src, (uint)buf.Length, true)!);
        Assert.Equal(unchecked((int)0x80AF050D), (int)Invoke(P, "CxFromBuffer", doc, src, (uint)buf.Length, true)!);
        Invoke(P, "CxRoot", root, doc);
        Assert.Equal("root", GuestMemory.ReadCString((uint)Invoke(P, "CxName", root)!));
        Assert.Equal(0, (int)Invoke(P, "CxUtilInt", root, Str("a"), v)!);
        Assert.Equal(5u, GuestMemory.R32(v));
        Assert.Equal(0, (int)Invoke(P, "CxUtilString", root, Str("s"), v, len)!);
        Assert.Equal(("hi", 2u), (GuestMemory.ReadCString(GuestMemory.R32(v)), GuestMemory.R32(len)));
        Assert.Equal(unchecked((int)0x80AF050B), (int)Invoke(P, "CxUtilInt", root, Str("s"), v)!);
        Assert.Equal(unchecked((int)0x80AF050A), (int)Invoke(P, "CxUtilInt", root, Str("zz"), v)!);
        Invoke(P, "CxFindChild", child, root, Str("c"));
        Assert.Equal(0x3Cu, GuestMemory.R32(child + 4));
        Assert.Equal(1, (int)Invoke(P, "CxNumChild", root)!);
        Invoke(P, "CxNext", child, child);
        Assert.Equal(-1, (int)GuestMemory.R32(child + 4));
        "XXXX"u8.ToArray().CopyTo(GuestMemory.Span(src, 4));
        uint doc2 = Alloc(0xAC);
        Assert.Equal(unchecked((int)0x80AF0505), (int)Invoke(P, "CxFromBuffer", doc2, src, (uint)buf.Length, false)!);
    }

    [Fact]
    public void UrlPartsAndEasing()
    {
        uint url = Alloc(0x50), s = Alloc(12), ret = Alloc(12);
        typeof(Paf).GetMethod("StrAssign", BindingFlags.Instance | BindingFlags.NonPublic, [typeof(uint), typeof(string)])!.Invoke(P, [s, "https://user@example.com:8443/a/b.html?x=1#top"]);
        Invoke(P, "UrlFromString", url, s);
        Invoke(P, "UrlGetHost", ret, url);
        Assert.Equal("example.com", PStr(ret));
        Invoke(P, "UrlGetPath", ret, url);
        Assert.Equal("/a/b.html", PStr(ret));
        Invoke(P, "UrlGetQuery", ret, url);
        Assert.Equal("x=1", PStr(ret));
        Invoke(P, "UrlGetUser", ret, url);
        Assert.Equal("user", PStr(ret));
        Invoke(P, "UrlGetBase", ret, url);
        Assert.Equal("https://example.com:8443/a/", PStr(ret));
        Assert.Equal(8443, (int)Invoke(P, "UrlGetPort", url)!);
        Assert.Equal(1f, (float)Invoke(P, "EaseBounceOut", 2f)!);
        Assert.Equal(0f, (float)Invoke(P, "EaseQuadIn", -1f)!);
        Assert.Equal(0.75f, (float)Invoke(P, "EaseQuadOut", 0.5f)!);
        Assert.Equal(0.5f, (float)Invoke(P, "EaseCubicInOut", 0.5f)!, 5);
    }
}
