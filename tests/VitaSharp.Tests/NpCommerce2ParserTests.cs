using System.Reflection;
using System.Text;
using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;

namespace VitaSharp.Tests;

/// <summary>np_commerce2.suprx's result parsers (system NIDs, FW 3.74) fed crafted store replies: the 100-byte header,
/// TLV items (big-endian 12-bit tag + 16-bit length), tag-1 / tag-2 sections, the record layouts and the error codes.</summary>
[Collection("guest")]
public sealed class NpCommerce2ParserTests(GuestMemoryFixture fx)
{
    const int NotInit = unchecked((int)0x80550F01), Null = unchecked((int)0x80550F03), NoIndex = unchecked((int)0x80550F06),
        BadBuffer = unchecked((int)0x80550F14), NotFound = unchecked((int)0x80550F87), TooShort = unchecked((int)0x80550F89),
        Overrun = unchecked((int)0x80550F8A);

    static int Call(object m, string name, params object[] args) =>
        Convert.ToInt32(m.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!
            .Invoke(m, args));

    uint Alloc(uint size) { uint p = fx.Memory.Alloc(size, "c2parse"); GuestMemory.Fill(p, 0, size); return p; }

    static NpCommerce2 Lib()
    {
        var c = new NpCommerce2();
        Call(c, "sceNpCommerce2Init");
        return c;
    }

    static byte[] Be16(int v) => [(byte)(v >> 8), (byte)v];
    static byte[] Be32(uint v) => [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];
    static byte[] Be64(ulong v) => [.. Be32((uint)(v >> 32)), .. Be32((uint)v)];
    static byte[] Z(string s) => [.. Encoding.ASCII.GetBytes(s), 0];
    static byte[] Cat(params byte[][] parts) => parts.SelectMany(x => x).ToArray();
    static byte[] T(int tag, params byte[][] value) { var v = Cat(value); return Cat(Be16(tag), Be16(v.Length), v); }
    static byte[] Sec(int type, int count, params byte[][] items) => T(1, Be16(type), Be16(count), Cat(items));
    static byte[] Sec8(int type, int count, int w4, int w6, params byte[][] items) => T(2, Be16(type), Be16(count), Be16(w4), Be16(w6), Cat(items));

    /// <summary>A reply buffer: native header (id, 4 + body length, lenient flag) and the body at +0x68.</summary>
    uint Reply(byte[] body, out uint size, uint lenient = 0, uint id = 0x1234)
    {
        size = 0x68 + (uint)body.Length;
        uint buf = Alloc(size + 16);
        GuestMemory.W32(buf, id);
        GuestMemory.W32(buf + 8, 4 + (uint)body.Length);
        GuestMemory.W32(buf + 12, lenient);
        body.CopyTo(GuestMemory.Span(buf + 0x68, body.Length));
        return buf;
    }

    /// <summary>A parent record over <paramref name="data"/> with its cursor at the start.</summary>
    uint Parent(uint buf, uint data, uint len)
    {
        uint rec = Alloc(0x100);
        GuestMemory.W32(rec, 0x1234); GuestMemory.W32(rec + 4, buf); GuestMemory.W32(rec + 12, data);
        GuestMemory.W32(rec + 16, len); GuestMemory.W32(rec + 20, data);
        return rec;
    }

    static uint W(uint rec, int word) => GuestMemory.R32(rec + (uint)word * 4);
    static ulong W64(uint rec, int word) => GuestMemory.Read<ulong>(rec + (uint)word * 4);

    [Fact]
    public void AllParserNidsAreRegistered()
    {
        var nids = typeof(NpCommerce2).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Where(m => m.Name.StartsWith("C2", StringComparison.Ordinal))
            .SelectMany(m => m.GetCustomAttributes<HleAttribute>()).Where(h => h.Library == "SceNpCommerce2").Select(h => h.Nid).ToList();
        Assert.Equal(155, nids.Count);
        Assert.Equal(nids.Count, nids.Distinct().Count());
    }

    [Fact]
    public void ReplyHeaderChecksAndStrings()
    {
        var c = new NpCommerce2();
        uint rec = Alloc(0x80);
        byte[] body = Cat(T(0x4F, Z("abc")), T(0x50), T(0x51, Z("x")), T(0x52, Z("y")), T(0x36, Z("z")), T(0x54, Z("w")));
        uint buf = Reply(body, out uint size);
        Assert.Equal(NotInit, Call(c, "C2ReplyStrings6", rec, buf, size));
        c = Lib();
        Assert.Equal(Null, Call(c, "C2ReplyStrings6", rec, buf, 0u));
        Assert.Equal(BadBuffer, Call(c, "C2ReplyStrings6", rec, buf, 0x67u));
        Assert.Equal(BadBuffer, Call(c, "C2ReplyStrings6", rec, buf, size - 1)); // shorter than the header says
        Assert.Equal(0, Call(c, "C2ReplyStrings6", rec, buf, size));
        Assert.Equal(0x1234u, W(rec, 0));
        Assert.Equal(buf, W(rec, 1));
        Assert.Equal(0x68u + (uint)body.Length, W(rec, 2));
        Assert.Equal(buf + 0x68, W(rec, 3));
        Assert.Equal((uint)body.Length, W(rec, 4));
        Assert.Equal("abc", GuestMemory.ReadCString(W(rec, 0x17)));
        Assert.Equal(buf + 4, W(rec, 0x18)); // empty string → reply+4
        Assert.Equal("w", GuestMemory.ReadCString(W(rec, 0x1C)));
        uint buf2 = Reply(body[..^6], out uint size2); // no 0x54 item
        Assert.Equal(NotFound, Call(c, "C2ReplyStrings6", rec, buf2, size2));
    }

    [Fact]
    public void SectionCountAndIndexedItems()
    {
        var c = Lib();
        uint rec = Alloc(0x80), info = Alloc(0x80);
        byte[] body = Cat(Sec(0x74, 7), Sec(0x78, 2, T(0x78, Be32(1), Be32(2)), T(0x78, Be32(3), Be32(4))));
        uint buf = Reply(body, out uint size);
        Assert.Equal(0, Call(c, "C2ReplyCount74", rec, buf, size));
        Assert.Equal(7u, W(rec, 0x17));
        Assert.Equal(0, Call(c, "C2BareReply", rec, buf, size));
        Assert.True(Call(c, "C2Item78", rec, 1u, info) > 0);
        Assert.Equal(3u, W(info, 0x17));
        Assert.Equal(4u, W(info, 0x18));
        Assert.Equal(8u, W(info, 4));
        Assert.Equal(W(info, 3) + 8, W(info, 5));
        Assert.Equal(NoIndex, Call(c, "C2Item78", rec, 2u, info));
        Assert.Equal(Null, Call(c, "C2Item78", rec, 0u, 0u));

        uint shortBuf = Reply(Sec(0x78, 1, T(0x78, Be32(1)), new byte[8]), out uint shortSize);
        Assert.Equal(0, Call(c, "C2BareReply", rec, shortBuf, shortSize));
        Assert.Equal(TooShort, Call(c, "C2Item78", rec, 0u, info));

        // the item says 12 bytes but its section holds only 8 of them
        byte[] over = Cat(Be16(1), Be16(16), Be16(0x78), Be16(1), Be16(0x78), Be16(12), new byte[8]);
        uint overBuf = Reply(over, out uint overSize);
        Assert.Equal(0, Call(c, "C2BareReply", rec, overBuf, overSize));
        Assert.Equal(Overrun, Call(c, "C2Item78", rec, 0u, info));
    }

    [Fact]
    public void TimesBecomeRtcTicks()
    {
        var c = Lib();
        uint rec = Alloc(0x80), info = Alloc(0x80);
        uint buf = Reply(T(0x2C, Be32(7), Be64(1000)), out uint size);
        Assert.Equal(0, Call(c, "C2BareReply", rec, buf, size));
        Assert.True(Call(c, "C2Info2C", rec, info) > 0);
        Assert.Equal(7u, W(info, 0x17));
        Assert.Equal(1000UL * 1000 + 62135596800000000UL, W64(info, 0x18));
        uint buf0 = Reply(T(0x2C, Be32(7), Be64(0)), out uint size0);
        Call(c, "C2BareReply", rec, buf0, size0);
        Call(c, "C2Info2C", rec, info);
        Assert.Equal(0UL, W64(info, 0x18));
    }

    [Fact]
    public void Tag2ListCountIsTrimmed()
    {
        var c = Lib();
        uint rec = Alloc(0x90), info = Alloc(0x80);
        uint buf = Reply(Sec8(0x28, 3, 0xAA, 0xBB, T(0x28, Be32(5)), T(0x28, Be32(6))), out uint size, lenient: 1);
        Assert.Equal(0, Call(c, "C2ReplyList28", rec, buf, size));
        Assert.Equal(1u, W(rec, 6));
        Assert.Equal(0xAAu, W(rec, 0x17));
        Assert.Equal(2u, W(rec, 0x18)); // three announced, two there
        Assert.Equal(0xBBu, W(rec, 0x19));
        Assert.True(Call(c, "C2Item28", rec, 1u, info) > 0);
        Assert.Equal(6u, W(info, 0x17));
        Assert.Equal(NoIndex, Call(c, "C2Item28", rec, 3u, info));
    }

    [Fact]
    public void GameProductRecord()
    {
        var c = Lib();
        var block = new byte[0xA1];
        void Put(int off, byte[] v) => v.CopyTo(block, off);
        Put(0x39, Be32(2)); Put(0x3D, Be32(11)); Put(0x41, Be32(12));
        Put(0x4D, Be32(1)); Put(0x51, Be32(5)); Put(0x59, Be32(13)); Put(0x7D, Be64(2000));
        byte[] value = Cat(block, T(0x08, Z("name")), T(0x38, Z("desc")), T(0x12, Z("x")), T(0x07, Z("a")), T(0x10, Z("b")),
            T(0x04, Z("c")), Sec(0x3D, 1), Sec(0x3E, 2), Sec(0x85, 9));
        uint buf = Reply(T(0x4D, value), out _);
        uint parent = Parent(buf, buf + 0x68, 4 + (uint)value.Length), info = Alloc(0x100);
        GuestMemory.Fill(info, 0xCC, 0x100);
        Assert.Equal(0, Call(c, "C2GameProduct", parent, info));
        uint d = buf + 0x68 + 4;
        Assert.Equal(d, W(info, 0x17));
        Assert.Equal(2u, W(info, 0x18));
        Assert.Equal(11u, W(info, 0x19));
        Assert.Equal(12u, W(info, 0x1A));
        Assert.Equal(1u, W(info, 0x1B));
        Assert.Equal(13u, W(info, 0x1C));
        Assert.Equal(6u, W(info, 0x3C)); // flag set (2) + the +0x51 value (4)
        Assert.Equal(2000UL * 1000 + 62135596800000000UL, W64(info, 0x20));
        Assert.Equal(d + 0xA1, W(info, 5));
        Assert.Equal("name", GuestMemory.ReadCString(W(info, 0x30)));
        Assert.Equal("desc", GuestMemory.ReadCString(W(info, 0x31)));
        Assert.Equal("x", GuestMemory.ReadCString(W(info, 0x32))); // kind 2 has the 0x12 string
        Assert.Equal("c", GuestMemory.ReadCString(W(info, 0x38)));
        Assert.Equal(buf + 4, W(info, 0x33)); // no 0x3C string
        Assert.Equal(1u, W(info, 0x34));
        Assert.Equal(2u, W(info, 0x35));
        Assert.Equal(9u, W(info, 0x3B));
        Assert.Equal(1u, W(info, 0x3E)); // no 0x4B price item
        Assert.Equal(0u, W(info, 0x3F)); // the record is cleared first
    }

    [Theory]
    [InlineData(0x28, "abc", 0x6u)]
    [InlineData(0x28, "aaaa1234", 0x10u)]
    [InlineData(0x28, "Ab1Ab1Ab", 0u)]
    [InlineData(0x28, "abababab", 0x24u)]
    [InlineData(0x27, "a@b.c", 0u)]
    [InlineData(0x27, "a@bc", 0x80u)]
    [InlineData(0x27, "@a.b", 0x80u)]
    [InlineData(0x10, "ab", 0x2u)]
    [InlineData(0x10, "1abc", 0x40u)]
    [InlineData(0x10, "ab c", 0x80u)]
    [InlineData(0x10, "Ab_c-1", 0u)]
    [InlineData(0xFF00, "", 0x1u)]
    [InlineData(0xFF00, "tab\there", 0x80u)]
    public void FieldChecks(int kind, string text, uint flags)
    {
        object[] args = [kind, Encoding.ASCII.GetBytes(text), 0u];
        Assert.Equal(0, (int)typeof(NpCommerce2).GetMethod("CheckField", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, args)!);
        Assert.Equal(flags, (uint)args[2]);
    }

    [Fact]
    public void FieldCheckExportNarrowsUtf16()
    {
        var c = Lib();
        uint text = Alloc(0x400), flags = Alloc(4);
        void Set(string s) { GuestMemory.Fill(text, 0, 0x400); for (int i = 0; i < s.Length; i++) GuestMemory.Write(text + (uint)i * 2, (ushort)s[i]); }
        Set("Abc");
        Assert.Equal(0, Call(c, "C2CheckField", 0x10, text, flags));
        Assert.Equal(0u, GuestMemory.R32(flags));
        Set("Abé");
        Assert.Equal(0, Call(c, "C2CheckField", 0x10, text, flags));
        Assert.Equal(0x80u, GuestMemory.R32(flags));
        Assert.Equal(Null, Call(c, "C2CheckField", 0x99, text, flags));
        Set(new string('a', 256));
        Assert.Equal(Null, Call(c, "C2CheckField", 0xFF00, text, flags));
    }

    [Fact]
    public void IdHelpers()
    {
        var c = new NpCommerce2(); // no library state needed
        uint id = Alloc(0x40), outp = Alloc(0x40);
        GuestMemory.WriteCString(id, "UP0001-NPXX00001_00-ABCDEFGHIJKLMNOP-E001");
        Assert.Equal(0, Call(c, "C2IdPrefix", id, outp, 0x40u));
        Assert.Equal("UP0001-NPXX00001_00-ABCDEFGHIJKLMNOP", GuestMemory.ReadCString(outp));
        Assert.Equal(unchecked((int)0x80550F84), Call(c, "C2IdPrefix", id, outp, 36u));
        GuestMemory.WriteCString(id, "abc");
        Assert.Equal(unchecked((int)0x80550F07), Call(c, "C2IdPrefix", id, outp, 0x40u));

        GuestMemory.WriteCString(id, "IP9101-NPIA90005_00");
        Assert.Equal(0, Call(c, "C2IsLinkServiceId", id, outp));
        Assert.Equal(1u, GuestMemory.R32(outp));
        GuestMemory.WriteCString(id, "IP9106-NPIA90005_00");
        Assert.Equal(0, Call(c, "C2IsLinkServiceId", id, outp));
        Assert.Equal(0u, GuestMemory.R32(outp));
        Assert.Equal(Null, Call(c, "C2IsLinkServiceId", 0u, outp));
    }
}
