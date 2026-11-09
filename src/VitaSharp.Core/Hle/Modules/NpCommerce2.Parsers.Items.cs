using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

// np_commerce2.suprx result parsers, part 2: more reply records and items (see NpCommerce2.Parsers.cs).
public sealed partial class NpCommerce2
{
    // ---- replies

    [Hle("sceNpCommerce2_3108F734", Nid = 0x3108F734, Library = C2)]
    [Hle("sceNpCommerce2_42A33732", Nid = 0x42A33732, Library = C2)]
    [Hle("sceNpCommerce2_53235A9F", Nid = 0x53235A9F, Library = C2)]
    int C2BareReply2(uint rec, uint buf, uint size) => C2BareReply(rec, buf, size);

    [Hle("sceNpCommerce2_486D6DE3", Nid = 0x486D6DE3, Library = C2)]
    int C2ReplyStoreInfo2(uint rec, uint buf, uint size) => C2ReplyStoreInfo(rec, buf, size);

    /// <summary>Reply with an optional value 0x15 (word 0x17) and an optional 64-bit value 0x03 (word 0x18); 0x68 bytes.</summary>
    [Hle("sceNpCommerce2_2ED2CB51", Nid = 0x2ED2CB51, Library = C2)]
    int C2ReplyValue64(uint rec, uint buf, uint size)
    {
        if (Need(rec, buf, size) is int e) return e;
        int r = Root(rec, 0x68, buf, size, out uint p, out uint n);
        if (r < 0) return r;
        r = TlvFind(p, n, 0x15, 4, out _);
        if (r >= 0) Be32To(At(rec, 0x17), p + (uint)r);
        if (!Opt(r)) return r;
        r = TlvFind(p, n, 0x03, 8, out _);
        if (r >= 0) Be64To(At(rec, 0x18), p + (uint)r);
        return Opt(r) ? 0 : r;
    }

    /// <summary>Reply (lenient flag kept) with the required value 0x2D (word 0x17) and the optional value 0x15 (word
    /// 0x18); 100 bytes.</summary>
    [Hle("sceNpCommerce2_30ABA9CA", Nid = 0x30ABA9CA, Library = C2)]
    int C2ReplyValues2(uint rec, uint buf, uint size)
    {
        if (Need(rec, buf, size) is int e) return e;
        int r = Root(rec, 100, buf, size, out uint p, out uint n, keepLenient: true);
        if (r < 0) return r;
        r = TlvFind(p, n, 0x2D, 4, out _);
        if (r < 0) return r;
        Be32To(At(rec, 0x17), p + (uint)r);
        r = TlvFind(p, n, 0x15, 4, out _);
        if (r >= 0) Be32To(At(rec, 0x18), p + (uint)r);
        return Opt(r) ? 0 : r;
    }

    /// <summary>Reply with the required value 0x31 (word 0x17); 0x60 bytes.</summary>
    [Hle("sceNpCommerce2_37D724FA", Nid = 0x37D724FA, Library = C2)]
    int C2ReplyValue31(uint rec, uint buf, uint size)
    {
        if (Need(rec, buf, size) is int e) return e;
        int r = Root(rec, 0x60, buf, size, out uint p, out uint n);
        if (r < 0) return r;
        r = TlvFind(p, n, 0x31, 4, out _);
        if (r < 0) return r;
        Be32To(At(rec, 0x17), p + (uint)r);
        return 0;
    }

    /// <summary>Reply (lenient) with the optional string 0x94 (word 0x22) and the tag-2 list 0x28 (header at words
    /// 0x17..0x21); 0x8C bytes.</summary>
    [Hle("sceNpCommerce2_39AE7F83", Nid = 0x39AE7F83, Library = C2)]
    int C2ReplyList28Named(uint rec, uint buf, uint size)
    {
        if (Need(rec, buf, size) is int e) return e;
        int r = Root(rec, 0x8C, buf, size, out uint p, out uint n, keepLenient: true);
        if (r < 0) return r;
        if (!Opt(r = Str(rec, At(rec, 0x22), p, n, 0x94))) return r;
        return TrimList8(rec, p, n, 0x28, 4, At(rec, 0x17));
    }

    /// <summary>Reply (lenient) over the tag-2 list 0x73 of 0x9E-byte items; 0x88 bytes.</summary>
    [Hle("sceNpCommerce2_4DAE27BE", Nid = 0x4DAE27BE, Library = C2)]
    int C2ReplyList73(uint rec, uint buf, uint size)
    {
        if (Need(rec, buf, size) is int e) return e;
        int r = Root(rec, 0x88, buf, size, out uint p, out uint n, keepLenient: true);
        if (r < 0) return r;
        return TrimList8(rec, p, n, 0x73, 0x9E, At(rec, 0x17));
    }

    /// <summary>Reply with the required string 0x76 (word 0x17); 0x60 bytes.</summary>
    [Hle("sceNpCommerce2_5953F48D", Nid = 0x5953F48D, Library = C2)]
    int C2ReplyString76(uint rec, uint buf, uint size)
    {
        if (Need(rec, buf, size) is int e) return e;
        int r = Root(rec, 0x60, buf, size, out uint p, out uint n);
        if (r < 0) return r;
        r = Str(rec, At(rec, 0x17), p, n, 0x76);
        return r < 0 ? r : 0;
    }

    // ---- two-value items (0x32, 0x58, 0x68)

    static int TwoValues(uint parent, uint p, uint n, int tag, uint info)
    {
        int r = TlvFind(p, n, tag, 8, out ushort l);
        if (r < 0) return r;
        uint d = p + (uint)r;
        Child(parent, info, 100, d, l);
        Be32To(At(info, 0x17), d);
        Be32To(At(info, 0x18), d + 4);
        Wr(info, 5, d + 8);
        return r;
    }

    [Hle("sceNpCommerce2_6C65E2E2", Nid = 0x6C65E2E2, Library = C2)]
    int C2Info32b(uint parent, uint info) => Need(parent, info) ?? TwoValues(parent, Rd(parent, 3), Rd(parent, 4), 0x32, info);

    [Hle("sceNpCommerce2_307290E8", Nid = 0x307290E8, Library = C2)]
    int C2Info58(uint parent, uint info) => Need(parent, info) ?? TwoValues(parent, Rd(parent, 3), Rd(parent, 4), 0x58, info);

    [Hle("sceNpCommerce2_447274CD", Nid = 0x447274CD, Library = C2)]
    int C2Info68(uint parent, uint info) => Need(parent, info) ?? TwoValues(parent, Rd(parent, 3), Rd(parent, 4), 0x68, info);

    [Hle("sceNpCommerce2_4D3698A1", Nid = 0x4D3698A1, Library = C2)]
    int C2Info33b(uint parent, uint info) => C2Info33(parent, info);

    [Hle("sceNpCommerce2_5E7B03D8", Nid = 0x5E7B03D8, Library = C2)]
    int C2Item39b(uint parent, uint index, uint info) => C2Item39(parent, index, info);

    [Hle("sceNpCommerce2_3E8B077C", Nid = 0x3E8B077C, Library = C2)]
    [Hle("sceNpCommerce2_42799B8B", Nid = 0x42799B8B, Library = C2)]
    int C2Block91(uint parent, uint info) =>
        Need(parent, info) ?? Block91(Rd(parent, 0), Rd(parent, 1), Rd(parent, 3), Rd(parent, 4), Rd(parent, 5), info);

    // ---- the 0x18 item (a fixed 0x11-byte part, the string 0x10 and the count of section 0x17); 0x88 bytes

    static int Item18(uint id, uint buf, uint d, uint len, uint info)
    {
        ChildOf(id, buf, info, 0x88, d, len);
        Wr(info, 0x17, d);
        uint c = d + 0x11;
        Wr(info, 5, c);
        int r = Str(info, At(info, 0x18), c, len - 0x11, 0x10);
        return r < 0 ? r : Count(c, len - 0x11, 0x17, At(info, 0x19));
    }

    [Hle("sceNpCommerce2_2EB4B433", Nid = 0x2EB4B433, Library = C2)]
    [Hle("sceNpCommerce2_A88EA64B", Nid = 0xA88EA64B, Library = C2)]
    int C2Item18(uint parent, uint index, uint info)
    {
        if (Need(parent, info) is int e) return e;
        Rest(parent, out uint p, out uint n);
        int r = TlvElement(p, n, 0x18, 0x19, index, out ushort l);
        return r < 0 ? r : Item18(Rd(parent, 0), Rd(parent, 1), p + (uint)r, l, info);
    }

    [Hle("sceNpCommerce2_2EBDD562", Nid = 0x2EBDD562, Library = C2)]
    [Hle("sceNpCommerce2_54BCEFA6", Nid = 0x54BCEFA6, Library = C2)]
    int C2Info18(uint parent, uint info)
    {
        if (Need(parent, info) is int e) return e;
        Rest(parent, out uint p, out uint n);
        int r = TlvFind(p, n, 0x18, 0x19, out ushort l);
        return r < 0 ? r : Item18(Rd(parent, 0), Rd(parent, 1), p + (uint)r, l, info);
    }

    // ---- pointers to items

    /// <summary>Pointer to item <paramref name="index"/> of section 0x3E after the parent's cursor.</summary>
    [Hle("sceNpCommerce2_3C62D93E", Nid = 0x3C62D93E, Library = C2)]
    [Hle("sceNpCommerce2_7FECE927", Nid = 0x7FECE927, Library = C2)]
    [Hle("sceNpCommerce2_D3BCDADD", Nid = 0xD3BCDADD, Library = C2)]
    [Hle("sceNpCommerce2_E5E79E63", Nid = 0xE5E79E63, Library = C2)]
    int C2Item3E(uint parent, uint index, uint outPtr)
    {
        if (Need(parent, outPtr) is int e) return e;
        Rest(parent, out uint p, out uint n);
        int r = TlvElement(p, n, 0x3E, 3, index, out _);
        if (r >= 0) GuestMemory.W32(outPtr, p + (uint)r);
        return r;
    }

    /// <summary>Pointer to item <paramref name="index"/> of section 0x38 of the parent.</summary>
    [Hle("sceNpCommerce2_58587046", Nid = 0x58587046, Library = C2)]
    int C2Item38(uint parent, uint index, uint outPtr)
    {
        if (Need(parent, outPtr) is int e) return e;
        uint p = Rd(parent, 3);
        int r = TlvElement(p, Rd(parent, 4), 0x38, 0, index, out _);
        if (r >= 0) GuestMemory.W32(outPtr, p + (uint)r);
        return r;
    }

    // ---- other items

    /// <summary>The 0x67 item: a value, two inline strings and the strings 0x65 / 0x66; 0x70 bytes.</summary>
    [Hle("sceNpCommerce2_3DF0BC8F", Nid = 0x3DF0BC8F, Library = C2)]
    int C2Info67(uint parent, uint info)
    {
        if (Need(parent, info) is int e) return e;
        uint p = Rd(parent, 3);
        int r = TlvFind(p, Rd(parent, 4), 0x67, 0x46, out ushort l);
        if (r < 0) return r;
        uint d = p + (uint)r;
        Child(parent, info, 0x70, d, l);
        Be32To(At(info, 0x17), d);
        Wr(info, 0x18, d + 4);
        Wr(info, 0x19, d + 0x25);
        uint c = d + 0x46, m = (uint)l - 0x46;
        Wr(info, 5, c);
        if ((r = Str(info, At(info, 0x1A), c, m, 0x65)) < 0) return r;
        return Str(info, At(info, 0x1B), c, m, 0x66);
    }

    /// <summary>The 0x70 item: two 64-bit values, a time, four values, a flag, the strings 0x6A / 0x5D and the count
    /// of section 0x6F; 0x98 bytes.</summary>
    [Hle("sceNpCommerce2_43B04705", Nid = 0x43B04705, Library = C2)]
    int C2Info70(uint parent, uint info)
    {
        if (Need(parent, info) is int e) return e;
        uint p = Rd(parent, 3);
        int r = TlvFind(p, Rd(parent, 4), 0x70, 0x38, out ushort l);
        if (r < 0) return r;
        uint d = p + (uint)r;
        Child(parent, info, 0x98, d, l);
        Be64To(At(info, 0x18), d);
        Be64To(At(info, 0x1A), d + 8);
        Ticks(At(info, 0x1C), d + 0x10);
        for (int i = 0; i < 4; i++) Be32To(At(info, 0x1E + i), d + 0x18 + (uint)i * 4);
        Wr(info, 0x22, Be32(d + 0x28) != 0 ? 1u : 0u);
        uint c = d + 0x2C, m = (uint)l - 0x2C;
        Wr(info, 5, c);
        if ((r = Str(info, At(info, 0x23), c, m, 0x6A)) < 0) return r;
        if ((r = Str(info, At(info, 0x24), c, m, 0x5D)) < 0) return r;
        return Count(c, m, 0x6F, At(info, 0x25));
    }

    /// <summary>Item of section 0x6D after the parent's cursor: two 64-bit values, three negated values, two values
    /// and the strings 0x6C / 0x04 / 0x07 / 0x08; 0x98 bytes.</summary>
    [Hle("sceNpCommerce2_4ABFA125", Nid = 0x4ABFA125, Library = C2)]
    int C2Item6D(uint parent, uint index, uint info)
    {
        if (Need(parent, info) is int e) return e;
        Rest(parent, out uint p, out uint n);
        int r = TlvElement(p, n, 0x6D, 0x61, index, out ushort l);
        if (r < 0) return r;
        uint d = p + (uint)r;
        Child(parent, info, 0x98, d, l);
        Wr(info, 0x17, d);
        Be64To(At(info, 0x18), d + 0x39);
        Be64To(At(info, 0x1A), d + 0x41);
        Wr(info, 0x1C, (uint)-(int)Be32(d + 0x49));
        Wr(info, 0x1D, (uint)-(int)Be32(d + 0x4D));
        Wr(info, 0x1E, (uint)-(int)Be32(d + 0x51));
        Be32To(At(info, 0x20), d + 0x55);
        Be32To(At(info, 0x21), d + 0x59);
        uint c = d + 0x5D, m = (uint)l - 0x5D;
        Wr(info, 5, c);
        if ((r = Str(info, At(info, 0x1F), c, m, 0x6C)) < 0) return r;
        if ((r = Str(info, At(info, 0x22), c, m, 0x04)) < 0) return r;
        if ((r = Str(info, At(info, 0x23), c, m, 0x07)) < 0) return r;
        return Str(info, At(info, 0x24), c, m, 0x08);
    }

    /// <summary>Item <paramref name="index"/> of section 0x5B: a 3-byte code, two values and the counts of sections
    /// 0x5C / 0x9B; 0x70 bytes.</summary>
    [Hle("sceNpCommerce2_4BE534B6", Nid = 0x4BE534B6, Library = C2)]
    int C2Item5B(uint parent, uint index, uint info)
    {
        if (Need(parent, info) is int e) return e;
        uint p = Rd(parent, 3);
        int r = TlvElement(p, Rd(parent, 4), 0x5B, 0xF, index, out ushort l);
        if (r < 0) return r;
        uint d = p + (uint)r;
        Child(parent, info, 0x70, d, l);
        Wr(info, 0x17, d);
        Be32To(At(info, 0x18), d + 3);
        Be32To(At(info, 0x1A), d + 7);
        uint c = d + 0xB, m = (uint)l - 0xB;
        Wr(info, 5, c);
        if ((r = Count(c, m, 0x5C, At(info, 0x19))) < 0) return r;
        return Count(c, m, 0x9B, At(info, 0x1B));
    }

    /// <summary>Item <paramref name="index"/> of the tag-2 list 0x71: a time, two values and the strings 0x04 / 0x05 /
    /// 0x10; 0x80 bytes.</summary>
    [Hle("sceNpCommerce2_50F7E2CD", Nid = 0x50F7E2CD, Library = C2)]
    int C2Item71(uint parent, uint index, uint info)
    {
        if (Need(parent, info) is int e) return e;
        uint p = Rd(parent, 3);
        int r = TlvElement8(p, Rd(parent, 4), 0x71, 0x31, index, out ushort l, Rd(parent, 6));
        if (r < 0) return r;
        uint d = p + (uint)r;
        Child(parent, info, 0x80, d, l);
        Wr(info, 0x17, d);
        Ticks(At(info, 0x18), d + 0x21);
        Be32To(At(info, 0x1A), d + 0x29);
        Be32To(At(info, 0x1B), d + 0x2D);
        uint c = d + 0x31, m = (uint)l - 0x31;
        Wr(info, 5, c);
        if ((r = Str(info, At(info, 0x1C), c, m, 0x04)) < 0) return r;
        if ((r = Str(info, At(info, 0x1D), c, m, 0x05)) < 0) return r;
        return Str(info, At(info, 0x1E), c, m, 0x10);
    }

    /// <summary>Item <paramref name="index"/> of the tag-2 list 0x72: an inline string, a flag, two times, two values,
    /// the string 0x79 and the count of section 0x80; 0x88 bytes.</summary>
    [Hle("sceNpCommerce2_57A54D3C", Nid = 0x57A54D3C, Library = C2)]
    int C2Item72(uint parent, uint index, uint info)
    {
        if (Need(parent, info) is int e) return e;
        uint p = Rd(parent, 3);
        int r = TlvElement8(p, Rd(parent, 4), 0x72, 0x8A, index, out ushort l, Rd(parent, 6));
        if (r < 0) return r;
        uint d = p + (uint)r;
        Child(parent, info, 0x88, d, l);
        Wr(info, 0x17, d);
        Wr(info, 0x18, d + 0x39);
        Wr(info, 0x19, Be32(d + 0x6A) != 0 ? 1u : 0u);
        Ticks(At(info, 0x1A), d + 0x6E);
        Ticks(At(info, 0x1C), d + 0x76);
        Be32To(At(info, 0x1E), d + 0x7E);
        Be32To(At(info, 0x1F), d + 0x82);
        uint c = d + 0x86, m = (uint)l - 0x86;
        Wr(info, 5, c);
        if ((r = Str(info, At(info, 0x20), c, m, 0x79)) < 0) return r;
        return Opt(r = Count(c, m, 0x80, At(info, 0x21))) ? 0 : r;
    }

    /// <summary>Item of section 0x85 (0x90 in the v3 layout) after the parent's cursor: five values, a 64-bit value,
    /// the string 0x11 (a count in v3), the string 0x3C and the counts of sections 0x3D / 0x3E; 0x90 bytes.</summary>
    static int Item85(uint parent, uint index, uint info, int variant)
    {
        Rest(parent, out uint p, out uint n);
        GuestMemory.Fill(info, 0, 0x90);
        int r = TlvElement(p, n, variant == 3 ? 0x90 : 0x85, 0x55, index, out ushort l);
        if (r < 0) return r;
        uint d = p + (uint)r;
        Child(parent, info, 0x5C, d, l);
        Wr(info, 0x17, d);
        for (int i = 0; i < 5; i++) Be32To(At(info, 0x18 + i), d + 0x31 + (uint)i * 4);
        Be64To(At(info, 0x1E), d + 0x45);
        uint c = d + 0x4D, m = (uint)l - 0x4D;
        Wr(info, 5, c);
        if (variant == 3) { if (!Opt(r = Count(c, m, 0x11, At(info, 0x23)))) return r; }
        else if ((r = Str(info, At(info, 0x23), c, m, 0x11)) < 0) return r;
        if (!Opt(r = Str(info, At(info, 0x20), c, m, 0x3C))) return r;
        if ((r = Count(c, m, 0x3D, At(info, 0x21))) < 0) return r;
        if ((r = Count(c, m, 0x3E, At(info, 0x22))) < 0) return r;
        return 0;
    }

    [Hle("sceNpCommerce2_523F9F84", Nid = 0x523F9F84, Library = C2)]
    [Hle("sceNpCommerce2_D42FB74B", Nid = 0xD42FB74B, Library = C2)]
    int C2Item85(uint parent, uint index, uint info) => Need(parent, info) ?? Item85(parent, index, info, 2);

    [Hle("sceNpCommerce2_C51EA536", Nid = 0xC51EA536, Library = C2)]
    int C2Item90(uint parent, uint index, uint info) => Need(parent, info) ?? Item85(parent, index, info, 3);

    /// <summary>Item <paramref name="index"/> of section 0x64: a value, four flags, the first value of its 0x58 item
    /// (word 0x1C) and the string 0x5D; 0x78 bytes.</summary>
    [Hle("sceNpCommerce2_5EE39E3F", Nid = 0x5EE39E3F, Library = C2)]
    int C2Item64(uint parent, uint index, uint info)
    {
        if (Need(parent, info) is int e) return e;
        uint p = Rd(parent, 3);
        int r = TlvElement(p, Rd(parent, 4), 0x64, 0x18, index, out ushort l);
        if (r < 0) return r;
        uint d = p + (uint)r;
        Child(parent, info, 0x78, d, l);
        Be32To(At(info, 0x17), d);
        for (int i = 0; i < 4; i++) Wr(info, 0x18 + i, Be32(d + 4 + (uint)i * 4) != 0 ? 1u : 0u);
        uint c = d + 0x14, m = (uint)l - 0x14;
        Wr(info, 5, c);
        r = TlvFind(c, m, 0x58, 8, out _);
        if (r < 0) return r;
        Be32To(At(info, 0x1C), c + (uint)r);
        return Str(info, At(info, 0x1D), c, m, 0x5D);
    }

    /// <summary>Item of section 0x25 after the parent's cursor: a value and the strings 0x3F / 0x40 / 0x41; 0x6C bytes.</summary>
    [Hle("sceNpCommerce2_60137ABB", Nid = 0x60137ABB, Library = C2)]
    [Hle("sceNpCommerce2_938D7819", Nid = 0x938D7819, Library = C2)]
    [Hle("sceNpCommerce2_9F845F18", Nid = 0x9F845F18, Library = C2)]
    [Hle("sceNpCommerce2_E793945B", Nid = 0xE793945B, Library = C2)]
    int C2Item25(uint parent, uint index, uint info)
    {
        if (Need(parent, info) is int e) return e;
        Rest(parent, out uint p, out uint n);
        int r = TlvElement(p, n, 0x25, 4, index, out ushort l);
        if (r < 0) return r;
        uint d = p + (uint)r;
        Child(parent, info, 0x6C, d, l);
        Be32To(At(info, 0x17), d);
        uint c = d + 4, m = (uint)l - 4;
        Wr(info, 5, c);
        if ((r = Str(info, At(info, 0x18), c, m, 0x3F)) < 0) return r;
        if (!Opt(r = Str(info, At(info, 0x19), c, m, 0x40))) return r;
        return Opt(r = Str(info, At(info, 0x1A), c, m, 0x41)) ? 0 : r;
    }

    /// <summary>Item of section 0x24 after the parent's cursor: the strings 0x42 / 0x43; 100 bytes.</summary>
    [Hle("sceNpCommerce2_6308701B", Nid = 0x6308701B, Library = C2)]
    [Hle("sceNpCommerce2_A200696D", Nid = 0xA200696D, Library = C2)]
    int C2Item24(uint parent, uint index, uint info)
    {
        if (Need(parent, info) is int e) return e;
        Rest(parent, out uint p, out uint n);
        int r = TlvElement(p, n, 0x24, 0, index, out ushort l);
        if (r < 0) return r;
        uint d = p + (uint)r;
        Child(parent, info, 100, d, l);
        Wr(info, 5, d);
        if ((r = Str(info, At(info, 0x17), d, l, 0x42)) < 0) return r;
        return Opt(r = Str(info, At(info, 0x18), d, l, 0x43)) ? 0 : r;
    }

    /// <summary>The 0x7C item after the parent's cursor (0x8E in v3): three values and the string 0x44; 0x6C bytes.</summary>
    static int Info7C(uint parent, uint info, int variant)
    {
        Rest(parent, out uint p, out uint n);
        int r = TlvFind(p, n, variant == 3 ? 0x8E : 0x7C, 0xC, out ushort l);
        if (r < 0) return r;
        uint d = p + (uint)r;
        Child(parent, info, 0x6C, d, l);
        Be32To(At(info, 0x1A), d);
        Be32To(At(info, 0x19), d + 4);
        Be32To(At(info, 0x17), d + 8);
        uint c = d + 0xC;
        Wr(info, 5, c);
        return Opt(r = Str(info, At(info, 0x18), c, (uint)l - 0xC, 0x44)) ? 0 : r;
    }

    [Hle("sceNpCommerce2_6ABA5313", Nid = 0x6ABA5313, Library = C2)]
    int C2Info7C(uint parent, uint info) => Need(parent, info) ?? Info7C(parent, info, 2);

    // ---- account field checks

    /// <summary>
    /// (kind, const SceWChar16 *text, uint *flags): checks a Sign Up field. The text (under 256 characters, else
    /// 0x80550F03) is narrowed to bytes (flag 0x80 if any character is not ASCII), then by kind: 0xFF00 free text
    /// (1024), 0x28 password (8..30 printable with a digit, a letter, no three equal in a row, three distinct), 0x27
    /// e-mail (64, an '@' inside followed by a '.'), 0x10 online ID (3..16, a letter then letters, digits, '_' or
    /// '-'). Flags: 1 empty, 2 length, 4 no digit, 8 no letter, 0x10 a run of three, 0x20 too few distinct
    /// characters, 0x40 bad first character, 0x80 bad character / format. Unknown kind 0x80550F03.
    /// </summary>
    [Hle("sceNpCommerce2_34CDA223", Nid = 0x34CDA223, Library = C2)]
    int C2CheckField(int kind, uint text, uint flags)
    {
        if (!_inited) return C2_NOT_INIT;
        if (text == 0 || flags == 0) return C2_NULL;
        GuestMemory.W32(flags, 0);
        var s = new List<byte>();
        for (uint i = 0; ; i++)
        {
            if (i == 0x100) return C2_NULL;
            ushort ch = GuestMemory.Read<ushort>(text + i * 2);
            if (ch == 0) break;
            if (ch > 0x7F) GuestMemory.W32(flags, 0x80);
            s.Add((byte)ch);
        }
        uint f = GuestMemory.R32(flags);
        int r = CheckField(kind, s.ToArray(), ref f);
        GuestMemory.W32(flags, f);
        return r;
    }

    internal static int CheckField(int kind, byte[] text, ref uint flags)
    {
        static bool Printable(byte c) => c >= 0x20 && c < 0x7F;
        static bool Letter(byte c) => c is >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z';
        static bool Digit(byte c) => c is >= (byte)'0' and <= (byte)'9';
        static byte Lower(byte c) => c is >= (byte)'A' and <= (byte)'Z' ? (byte)(c + 32) : c;
        int max = kind switch { 0xFF00 => 0x400, 0x28 => 0x1E, 0x27 => 0x40, 0x10 => 0x10, _ => -1 };
        if (max < 0) return C2_NULL;
        int len = Math.Min(text.Length, max);
        var t = text.AsSpan(0, len);
        if (len == 0) { flags |= 1; return 0; }
        bool allPrintable = true;
        foreach (byte c in t) if (!Printable(c)) { allPrintable = false; break; }
        switch (kind)
        {
            case 0xFF00:
                if (!allPrintable) flags |= 0x80;
                break;
            case 0x28:
                if (!allPrintable) flags |= 0x80;
                if (len < 8) flags |= 2;
                bool digit = false, letter = false;
                foreach (byte c in t) { digit |= Digit(c); letter |= Letter(c); }
                if (!digit) flags |= 4;
                if (!letter) flags |= 8;
                int run = 0; byte prev = 0;
                foreach (byte c in t)
                {
                    run = Lower(prev) == Lower(c) ? run + 1 : 1;
                    if (run > 2) { flags |= 0x10; break; }
                    prev = c;
                }
                var distinct = new HashSet<byte>();
                bool three = false;
                foreach (byte c in t) { distinct.Add(c); if (distinct.Count > 2) { three = true; break; } }
                if (!three) flags |= 0x20;
                break;
            case 0x27:
                if (!allPrintable) flags |= 0x80;
                int at = t.IndexOf((byte)'@');
                if (t[0] == '@' || t[len - 1] == '@' || at < 0 || t[at..].IndexOf((byte)'.') < 0) flags |= 0x80;
                break;
            case 0x10:
                if (len < 3) flags |= 2;
                if (!Letter(t[0])) flags |= 0x40;
                int i = 1;
                while (i < len && (Letter(t[i]) || Digit(t[i]) || t[i] == '_' || t[i] == '-')) i++;
                if (i != len) flags |= 0x80;
                break;
        }
        return 0;
    }
}
