using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

// np_commerce2.suprx result parsers, part 1: top-level reply records and the simple items (see NpCommerce2.Parsers.cs
// for the reply format and the record header).
public sealed partial class NpCommerce2
{
    const string C2 = "SceNpCommerce2";

    // ---- top-level records over a reply buffer: (record, buffer, size)

    /// <summary>A bare reply record (0x5C bytes): only the common header.</summary>
    [Hle("sceNpCommerce2_05002E87", Nid = 0x05002E87, Library = C2)]
    [Hle("sceNpCommerce2_076E2600", Nid = 0x076E2600, Library = C2)]
    [Hle("sceNpCommerce2_2837D9E2", Nid = 0x2837D9E2, Library = C2)]
    int C2BareReply(uint rec, uint buf, uint size)
    {
        if (Need(rec, buf, size) is int e) return e;
        int r = Root(rec, 0x5C, buf, size, out _, out _);
        return r < 0 ? r : 0;
    }

    static readonly int[] StoreStrings = { 0x4F, 0x50, 0x51, 0x52, 0x36, 0x54 };

    /// <summary>Reply with six strings (tags 0x4F, 0x50, 0x51, 0x52, 0x36, 0x54 at words 0x17..0x1C), 0x74 bytes.</summary>
    [Hle("sceNpCommerce2_027A054E", Nid = 0x027A054E, Library = C2)]
    [Hle("sceNpCommerce2_BFC26DA2", Nid = 0xBFC26DA2, Library = C2)]
    int C2ReplyStrings6(uint rec, uint buf, uint size)
    {
        if (Need(rec, buf, size) is int e) return e;
        int r = Root(rec, 0x74, buf, size, out uint p, out uint n);
        if (r < 0) return r;
        for (int i = 0; i < StoreStrings.Length; i++)
            if ((r = Str(rec, At(rec, 0x17 + i), p, n, StoreStrings[i])) < 0) return r;
        return 0;
    }

    /// <summary>Reply with the counts of sections 0x37 / 0x39, the six strings at words 0x19..0x1E and a flag (word
    /// 0x1F) telling whether a 0x30 item is there; 0x80 bytes.</summary>
    [Hle("sceNpCommerce2_23C047F1", Nid = 0x23C047F1, Library = C2)]
    int C2ReplyStoreInfo(uint rec, uint buf, uint size)
    {
        if (Need(rec, buf, size) is int e) return e;
        int r = Root(rec, 0x80, buf, size, out uint p, out uint n);
        if (r < 0) return r;
        if ((r = Count(p, n, 0x37, At(rec, 0x17))) < 0) return r;
        if ((r = Count(p, n, 0x39, At(rec, 0x18))) < 0) return r;
        for (int i = 0; i < StoreStrings.Length; i++)
            if ((r = Str(rec, At(rec, 0x19 + i), p, n, StoreStrings[i])) < 0) return r;
        r = TlvFind(p, n, 0x30, 0x19, out _);
        if (r < 0 && r != C2_NOT_FOUND) return r;
        Wr(rec, 0x1F, r < 0 ? 0u : 1u);
        return 0;
    }

    /// <summary>Reply with the counts of sections 0x16 / 0x64 / 0x58 (words 0x17..0x19, optional) and the string 0x5C
    /// (word 0x1B); 0x70 bytes.</summary>
    [Hle("sceNpCommerce2_115DF6B1", Nid = 0x115DF6B1, Library = C2)]
    int C2ReplyCounts3(uint rec, uint buf, uint size)
    {
        if (Need(rec, buf, size) is int e) return e;
        int r = Root(rec, 0x70, buf, size, out uint p, out uint n);
        if (r < 0) return r;
        if (!Opt(r = Count(p, n, 0x16, At(rec, 0x17)))) return r;
        if (!Opt(r = Count(p, n, 0x64, At(rec, 0x18)))) return r;
        if (!Opt(r = Count(p, n, 0x58, At(rec, 0x19)))) return r;
        return Opt(r = Str(rec, At(rec, 0x1B), p, n, 0x5C)) ? 0 : r;
    }

    /// <summary>Reply with the count of section 0x74 (word 0x17); 0x60 bytes.</summary>
    [Hle("sceNpCommerce2_1599647E", Nid = 0x1599647E, Library = C2)]
    int C2ReplyCount74(uint rec, uint buf, uint size)
    {
        if (Need(rec, buf, size) is int e) return e;
        int r = Root(rec, 0x60, buf, size, out uint p, out uint n);
        if (r < 0) return r;
        r = Count(p, n, 0x74, At(rec, 0x17));
        return r < 0 ? r : 0;
    }

    /// <summary>Reply with two optional 32-bit values (items 0x2D / 0x15 at words 0x17 / 0x18); 100 bytes.</summary>
    [Hle("sceNpCommerce2_247E42C6", Nid = 0x247E42C6, Library = C2)]
    int C2ReplyValues(uint rec, uint buf, uint size)
    {
        if (Need(rec, buf, size) is int e) return e;
        int r = Root(rec, 100, buf, size, out uint p, out uint n);
        if (r < 0) return r;
        r = TlvFind(p, n, 0x2D, 4, out _);
        if (r >= 0) Be32To(At(rec, 0x17), p + (uint)r);
        if (!Opt(r)) return r;
        r = TlvFind(p, n, 0x15, 4, out _);
        if (r >= 0) Be32To(At(rec, 0x18), p + (uint)r);
        return Opt(r) ? 0 : r;
    }

    /// <summary>Reply over the tag-2 section 0x28 (its header at words 0x17..0x21): the count (word 0x18) is cut to
    /// the items really there; 0x88 bytes.</summary>
    [Hle("sceNpCommerce2_204066EA", Nid = 0x204066EA, Library = C2)]
    int C2ReplyList28(uint rec, uint buf, uint size)
    {
        if (Need(rec, buf, size) is int e) return e;
        int r = Root(rec, 0x88, buf, size, out uint p, out uint n, keepLenient: true);
        if (r < 0) return r;
        return TrimList8(rec, p, n, 0x28, 4, At(rec, 0x17));
    }

    /// <summary>Reads a tag-2 section's header into <paramref name="info"/> and cuts its count (info word 1) to the
    /// items that can really be read.</summary>
    static int TrimList8(uint rec, uint p, uint n, int tag, int minLen, uint info)
    {
        int r = TlvSection8(p, n, tag, out _, info, Rd(rec, 6) != 0);
        if (r < 0) return r;
        r = 0;
        for (uint i = 0; i < Rd(info, 1); i++)
        {
            r = TlvElement8(Rd(rec, 3), Rd(rec, 4), tag, minLen, i, out _, Rd(rec, 6));
            if (r < 0)
            {
                if (i != 0) { r = 0; Wr(info, 1, i); }
                break;
            }
        }
        return r < 0 ? r : 0;
    }

    // ---- items of a record: (parent, index, info) or (parent, info)

    /// <summary>Item <paramref name="index"/> of section 0x28 (lenient reply): one 32-bit value (word 0x17); 0x60 bytes.</summary>
    [Hle("sceNpCommerce2_0390787C", Nid = 0x0390787C, Library = C2)]
    [Hle("sceNpCommerce2_28B9A03A", Nid = 0x28B9A03A, Library = C2)]
    int C2Item28(uint parent, uint index, uint info)
    {
        if (Need(parent, info) is int e) return e;
        uint p = Rd(parent, 3);
        int r = TlvElement8(p, Rd(parent, 4), 0x28, 4, index, out ushort l, Rd(parent, 6));
        if (r < 0) return r;
        uint d = p + (uint)r;
        Child(parent, info, 0x60, d, l);
        Be32To(At(info, 0x17), d);
        Wr(info, 5, d + 4);
        return r;
    }

    /// <summary>Item of section 0x9C after the parent's cursor: data pointer, three 32-bit values and two 64-bit
    /// values (words 0x17..0x1F); 0x80 bytes.</summary>
    [Hle("sceNpCommerce2_006200AD", Nid = 0x006200AD, Library = C2)]
    [Hle("sceNpCommerce2_5354E2C3", Nid = 0x5354E2C3, Library = C2)]
    [Hle("sceNpCommerce2_57C93D02", Nid = 0x57C93D02, Library = C2)]
    int C2Item9C(uint parent, uint index, uint info)
    {
        if (Need(parent, info) is int e) return e;
        Rest(parent, out uint p, out uint n);
        int r = TlvElement(p, n, 0x9C, 0x4D, index, out ushort l);
        if (r < 0) return r;
        uint d = p + (uint)r;
        Child(parent, info, 0x80, d, l);
        Wr(info, 0x17, d);
        Be32To(At(info, 0x18), d + 0x31);
        Be32To(At(info, 0x19), d + 0x35);
        Be32To(At(info, 0x1A), d + 0x39);
        Be64To(At(info, 0x1C), d + 0x3D);
        Be64To(At(info, 0x1E), d + 0x45);
        Wr(info, 5, d + 0x4D);
        return r;
    }

    /// <summary>Item of section 0x80 after the parent's cursor (0xF8-byte fixed part: id, values, three inline
    /// strings, a time) plus the strings 0x08 / 0x84; 0x98 bytes.</summary>
    [Hle("sceNpCommerce2_0507F960", Nid = 0x0507F960, Library = C2)]
    [Hle("sceNpCommerce2_12A3F4BA", Nid = 0x12A3F4BA, Library = C2)]
    [Hle("sceNpCommerce2_17690AAD", Nid = 0x17690AAD, Library = C2)]
    [Hle("sceNpCommerce2_29154ADF", Nid = 0x29154ADF, Library = C2)]
    [Hle("sceNpCommerce2_3625C8E7", Nid = 0x3625C8E7, Library = C2)]
    [Hle("sceNpCommerce2_429EA2AF", Nid = 0x429EA2AF, Library = C2)]
    [Hle("sceNpCommerce2_580C74F2", Nid = 0x580C74F2, Library = C2)]
    [Hle("sceNpCommerce2_BB8069E3", Nid = 0xBB8069E3, Library = C2)]
    [Hle("sceNpCommerce2_D0831EFE", Nid = 0xD0831EFE, Library = C2)]
    int C2Item80(uint parent, uint index, uint info)
    {
        if (Need(parent, info) is int e) return e;
        Rest(parent, out uint p, out uint n);
        int r = TlvElement(p, n, 0x80, 0xF4, index, out ushort l);
        if (r < 0) return r;
        uint d = p + (uint)r;
        Child(parent, info, 0x98, d, l);
        Wr(info, 0x17, d);
        Be32To(At(info, 0x18), d + 0x31);
        Wr(info, 0x19, d + 0x35);
        Wr(info, 0x1A, d + 0x6E);
        Be32To(At(info, 0x1B), d + 0x9F);
        Be32To(At(info, 0x1C), d + 0xA3);
        Be32To(At(info, 0x1D), d + 0xA7);
        Wr(info, 0x1E, d + 0xAB);
        Be32To(At(info, 0x1F), d + 0xE4);
        Ticks(At(info, 0x20), d + 0xE8);
        Be32To(At(info, 0x22), d + 0xF0);
        uint c = d + 0xF8, m = (uint)l - 0xF8;
        Wr(info, 5, c);
        if ((r = Str(info, At(info, 0x23), c, m, 0x08)) < 0) return r;
        return Opt(r = Str(info, At(info, 0x24), c, m, 0x84)) ? 0 : r;
    }

    /// <summary>Item of section 0x95 after the parent's cursor (0xF8-byte fixed part) with its 0x83 price block, the
    /// string 0x97 and the count of section 0x96; 0xB0 bytes.</summary>
    [Hle("sceNpCommerce2_1CCF4344", Nid = 0x1CCF4344, Library = C2)]
    [Hle("sceNpCommerce2_7F6077C9", Nid = 0x7F6077C9, Library = C2)]
    [Hle("sceNpCommerce2_A3A7C1FC", Nid = 0xA3A7C1FC, Library = C2)]
    [Hle("sceNpCommerce2_B1BB4660", Nid = 0xB1BB4660, Library = C2)]
    [Hle("sceNpCommerce2_F708CC37", Nid = 0xF708CC37, Library = C2)]
    int C2Item95(uint parent, uint index, uint info)
    {
        if (Need(parent, info) is int e) return e;
        Rest(parent, out uint p, out uint n);
        int r = TlvElement(p, n, 0x95, 0xF8, index, out ushort l);
        if (r < 0) return r;
        uint d = p + (uint)r;
        Child(parent, info, 0xB0, d, l);
        Wr(info, 0x17, d);
        Be32To(At(info, 0x18), d + 0x31);
        Wr(info, 0x19, d + 0x6E);
        Be32To(At(info, 0x1A), d + 0x9F);
        Be32To(At(info, 0x1B), d + 0xA3);
        Be32To(At(info, 0x1C), d + 0xA7);
        Wr(info, 0x1D, d + 0xAB);
        Be32To(At(info, 0x1E), d + 0xE4);
        Ticks(At(info, 0x20), d + 0xE8);
        Be32To(At(info, 0x22), d + 0xF0);
        Be32To(At(info, 0x23), d + 0xF4);
        uint c = d + 0xF8, m = (uint)l - 0xF8;
        Wr(info, 5, c);
        r = TlvFind(c, m, 0x83, 0x14, out _);
        if (r < 0) return r;
        uint q = c + (uint)r;
        Be32To(At(info, 0x26), q);
        Be64To(At(info, 0x28), q + 4);
        Be32To(At(info, 0x2A), q + 0xC);
        Be32To(At(info, 0x2B), q + 0x10);
        if (!Opt(r = Str(info, At(info, 0x24), c, m, 0x97))) return r;
        return Opt(r = Count(c, m, 0x96, At(info, 0x25))) ? 0 : r;
    }

    /// <summary>Item of section 0x96 after the parent's cursor: two inline data pointers; 100 bytes.</summary>
    [Hle("sceNpCommerce2_2EAFF40C", Nid = 0x2EAFF40C, Library = C2)]
    int C2Item96(uint parent, uint index, uint info)
    {
        if (Need(parent, info) is int e) return e;
        Rest(parent, out uint p, out uint n);
        int r = TlvElement(p, n, 0x96, 0x6A, index, out ushort l);
        if (r < 0) return r;
        uint d = p + (uint)r;
        Child(parent, info, 100, d, l);
        Wr(info, 0x17, d);
        Wr(info, 0x18, d + 0x39);
        return r;
    }

    /// <summary>Pointer to item <paramref name="index"/> of section 0x23 after the parent's cursor.</summary>
    [Hle("sceNpCommerce2_0D9666D8", Nid = 0x0D9666D8, Library = C2)]
    [Hle("sceNpCommerce2_838F1DAB", Nid = 0x838F1DAB, Library = C2)]
    int C2Item23(uint parent, uint index, uint outPtr)
    {
        if (Need(parent, outPtr) is int e) return e;
        Rest(parent, out uint p, out uint n);
        int r = TlvElement(p, n, 0x23, 0, index, out _);
        if (r >= 0) GuestMemory.W32(outPtr, p + (uint)r);
        return r;
    }

    /// <summary>Pointer to item <paramref name="index"/> of section 0x3D after the parent's cursor and, when the item
    /// holds one (7+ bytes), the 32-bit value after its 3-byte code (0 otherwise; the value pointer is optional).</summary>
    [Hle("sceNpCommerce2_0DDBF063", Nid = 0x0DDBF063, Library = C2)]
    [Hle("sceNpCommerce2_5730F4A4", Nid = 0x5730F4A4, Library = C2)]
    int C2Item3D(uint parent, uint index, uint outPtr, uint outValue)
    {
        if (Need(parent, outPtr) is int e) return e;
        Rest(parent, out uint p, out uint n);
        int r = TlvElement(p, n, 0x3D, 3, index, out ushort l);
        if (r < 0) return r;
        uint d = p + (uint)r;
        GuestMemory.W32(outPtr, d);
        if (outValue != 0) GuestMemory.W32(outValue, l < 7 ? 0 : Be32(d + 3));
        return r;
    }

    /// <summary>Item <paramref name="index"/> of section 0x58: its 32-bit value.</summary>
    [Hle("sceNpCommerce2_26602DA1", Nid = 0x26602DA1, Library = C2)]
    int C2Item58(uint parent, uint index, uint outValue)
    {
        if (Need(parent, outValue) is int e) return e;
        uint p = Rd(parent, 3);
        int r = TlvElement(p, Rd(parent, 4), 0x58, 8, index, out _);
        if (r >= 0) Be32To(outValue, p + (uint)r);
        return r;
    }

    /// <summary>Item <paramref name="index"/> of section 0x78: two 32-bit values; 100 bytes.</summary>
    [Hle("sceNpCommerce2_1B0B5798", Nid = 0x1B0B5798, Library = C2)]
    int C2Item78(uint parent, uint index, uint info)
    {
        if (Need(parent, info) is int e) return e;
        uint p = Rd(parent, 3);
        int r = TlvElement(p, Rd(parent, 4), 0x78, 8, index, out ushort l);
        if (r < 0) return r;
        uint d = p + (uint)r;
        Child(parent, info, 100, d, l);
        Be32To(At(info, 0x17), d);
        Be32To(At(info, 0x18), d + 4);
        Wr(info, 5, d + 8);
        return r;
    }

    /// <summary>Item <paramref name="index"/> of section 0x39: data pointer, a value at +0x11 and the strings 0x0F /
    /// 0x10; 0x6C bytes.</summary>
    [Hle("sceNpCommerce2_16FE6A6E", Nid = 0x16FE6A6E, Library = C2)]
    int C2Item39(uint parent, uint index, uint info)
    {
        if (Need(parent, info) is int e) return e;
        uint p = Rd(parent, 3);
        int r = TlvElement(p, Rd(parent, 4), 0x39, 0x1D, index, out ushort l);
        if (r < 0) return r;
        uint d = p + (uint)r;
        Child(parent, info, 0x6C, d, l);
        Wr(info, 0x17, d);
        Be32To(At(info, 0x1A), d + 0x11);
        uint c = d + 0x15, m = (uint)l - 0x15;
        Wr(info, 5, c);
        if ((r = Str(info, At(info, 0x18), c, m, 0x0F)) < 0) return r;
        return Str(info, At(info, 0x19), c, m, 0x10);
    }

    /// <summary>Item <paramref name="index"/> of section 0x37: data pointer, the string 0x36 and the count of section
    /// 0x3B; 0x68 bytes.</summary>
    [Hle("sceNpCommerce2_17DF6D62", Nid = 0x17DF6D62, Library = C2)]
    int C2Item37(uint parent, uint index, uint info)
    {
        if (Need(parent, info) is int e) return e;
        uint p = Rd(parent, 3);
        int r = TlvElement(p, Rd(parent, 4), 0x37, 0x49, index, out ushort l);
        if (r < 0) return r;
        uint d = p + (uint)r;
        Child(parent, info, 0x68, d, l);
        Wr(info, 0x17, d);
        uint c = d + 0x41, m = (uint)l - 0x41;
        Wr(info, 5, c);
        if ((r = Str(info, At(info, 0x18), c, m, 0x36)) < 0) return r;
        return Count(c, m, 0x3B, At(info, 0x19));
    }

    /// <summary>Item <paramref name="index"/> of section 0x61: a value and the string 0x60; 100 bytes.</summary>
    [Hle("sceNpCommerce2_1A37B2BA", Nid = 0x1A37B2BA, Library = C2)]
    int C2Item61(uint parent, uint index, uint info)
    {
        if (Need(parent, info) is int e) return e;
        uint p = Rd(parent, 3);
        int r = TlvElement(p, Rd(parent, 4), 0x61, 8, index, out ushort l);
        if (r < 0) return r;
        uint d = p + (uint)r;
        return ValueAndString(parent, d, d, l, info, 0x60);
    }

    /// <summary>A 100-byte record: a 32-bit value then a string item (<paramref name="tag"/>).</summary>
    static int ValueAndString(uint parent, uint area, uint d, uint len, uint info, int tag)
    {
        len &= 0xFFFF;
        Child(parent, info, 100, d, len);
        Be32To(At(info, 0x17), d);
        uint c = d + 4;
        Wr(info, 5, c);
        return Str(info, At(info, 0x18), c, len - (c - area), tag);
    }

    /// <summary>The 0x5F item of the parent: fifteen 32-bit values, word 0x1B = the kind of word 0x1A (8 → 1,
    /// 4 → 2, 1 → 3, 0 → 0); 0x9C bytes.</summary>
    [Hle("sceNpCommerce2_0DE0C8B1", Nid = 0x0DE0C8B1, Library = C2)]
    [Hle("sceNpCommerce2_C46EB548", Nid = 0xC46EB548, Library = C2)]
    int C2Info5F(uint parent, uint info)
    {
        if (Need(parent, info) is int e) return e;
        uint p = Rd(parent, 3);
        int r = TlvFind(p, Rd(parent, 4), 0x5F, 0x3C, out ushort l);
        if (r < 0) return r;
        uint d = p + (uint)r;
        Child(parent, info, 0x9C, d, l);
        Be32To(At(info, 0x17), d);
        Be32To(At(info, 0x18), d + 4);
        Be32To(At(info, 0x19), d + 8);
        uint kind = Be32(d + 0xC);
        Wr(info, 0x1A, kind);
        switch (kind)
        {
            case 8: Wr(info, 0x1B, 1); break;
            case 4: Wr(info, 0x1B, 2); break;
            case 1: Wr(info, 0x1B, 3); break;
            case 0: Wr(info, 0x1B, 0); break;
        }
        for (int i = 0; i < 11; i++) Be32To(At(info, 0x1C + i), d + 0x10 + (uint)i * 4);
        Wr(info, 5, d + 0x3C);
        return r;
    }

    /// <summary>The 0x33 item of the parent: the count of its section 0x7A; 0x60 bytes.</summary>
    [Hle("sceNpCommerce2_0666D252", Nid = 0x0666D252, Library = C2)]
    int C2Info33(uint parent, uint info)
    {
        if (Need(parent, info) is int e) return e;
        uint p = Rd(parent, 3);
        int r = TlvFind(p, Rd(parent, 4), 0x33, 4, out ushort l);
        if (r < 0) return r;
        uint d = p + (uint)r;
        Child(parent, info, 0x60, d, l);
        Wr(info, 5, d);
        return Count(d, l, 0x7A, At(info, 0x17));
    }

    /// <summary>The 0x32 item of the parent: two 32-bit values; 100 bytes.</summary>
    [Hle("sceNpCommerce2_2463E793", Nid = 0x2463E793, Library = C2)]
    int C2Info32(uint parent, uint info)
    {
        if (Need(parent, info) is int e) return e;
        uint p = Rd(parent, 3);
        int r = TlvFind(p, Rd(parent, 4), 0x32, 8, out ushort l);
        if (r < 0) return r;
        uint d = p + (uint)r;
        Child(parent, info, 100, d, l);
        Be32To(At(info, 0x17), d);
        Be32To(At(info, 0x18), d + 4);
        Wr(info, 5, d + 8);
        return r;
    }

    /// <summary>The 0x2C item of the parent: a value and a time; 0x68 bytes.</summary>
    [Hle("sceNpCommerce2_2A9DB98C", Nid = 0x2A9DB98C, Library = C2)]
    int C2Info2C(uint parent, uint info)
    {
        if (Need(parent, info) is int e) return e;
        uint p = Rd(parent, 3);
        int r = TlvFind(p, Rd(parent, 4), 0x2C, 0xC, out ushort l);
        if (r < 0) return r;
        uint d = p + (uint)r;
        Child(parent, info, 0x68, d, l);
        Be32To(At(info, 0x17), d);
        Ticks(At(info, 0x18), d + 4);
        Wr(info, 5, d + 0xC);
        return r;
    }

    /// <summary>The 0x30 item of the parent: data pointer, the string 0x0F and the optional 0x63 block (four values
    /// and the strings 0x62 / 0xB7 / 0xB8); 0x80 bytes.</summary>
    [Hle("sceNpCommerce2_0FFC4396", Nid = 0x0FFC4396, Library = C2)]
    int C2Info30(uint parent, uint info)
    {
        if (Need(parent, info) is int e) return e;
        uint p = Rd(parent, 3);
        int r = TlvFind(p, Rd(parent, 4), 0x30, 0x19, out ushort l);
        if (r < 0) return r;
        uint d = p + (uint)r;
        Child(parent, info, 0x80, d, l);
        Wr(info, 0x17, d);
        uint c = d + 0x15;
        Wr(info, 5, c);
        if ((r = Str(info, At(info, 0x18), c, (uint)l - 0x15, 0x0F)) < 0) return r;
        r = TlvFind(c, (uint)l - 0x15, 0x63, 0x14, out _);
        if (r == C2_NOT_FOUND) return 0;
        if (r < 0) return r;
        uint q = c + (uint)r;
        for (int i = 0; i < 4; i++) Be32To(At(info, 0x19 + i), q + (uint)i * 4);
        q += 0x10;
        uint m = (uint)l - (q - d);
        if ((r = Str(info, At(info, 0x1D), q, m, 0x62)) < 0) return r;
        if (!Opt(r = Str(info, At(info, 0x1E), q, m, 0xB7))) return r;
        return Opt(r = Str(info, At(info, 0x1F), q, m, 0xB8)) ? 0 : r;
    }

    // ---- the 0x4B / 0x91 blocks (two values, strings and an optional 0x4A triple)

    /// <summary>The optional 0x4A item after <paramref name="rec"/>'s cursor: three values, and a 0/1 flag.</summary>
    static int Triple4A(uint rec, uint flag, uint a, uint b, uint c)
    {
        Rest(rec, out uint p, out uint n);
        int r = TlvFind(p, n, 0x4A, 0xC, out _);
        uint has = 0;
        if (r != C2_NOT_FOUND)
        {
            if (r < 0) return r;
            uint d = p + (uint)r;
            Be32To(a, d); Be32To(b, d + 4); Be32To(c, d + 8);
            has = 1;
        }
        GuestMemory.W32(flag, has);
        return 0;
    }

    static int Block4B(uint id, uint buf, uint area, uint areaLen, uint p, uint info)
    {
        int r = TlvFind(p, areaLen - (p - area), 0x4B, 8, out ushort l);
        if (r < 0) return r;
        uint d = p + (uint)r;
        ChildOf(id, buf, info, 0x80, d, l);
        Be32To(At(info, 0x17), d);
        Be32To(At(info, 0x18), d + 4);
        uint c = d + 8, m = (uint)l - 8;
        Wr(info, 5, c);
        if (!Opt(r = Str(info, At(info, 0x19), c, m, 0x5A))) return r;
        if (!Opt(r = Str(info, At(info, 0x1A), c, m, 0x46))) return r;
        if (!Opt(r = Str(info, At(info, 0x1B), c, m, 0x47))) return r;
        r = Triple4A(info, At(info, 0x1C), At(info, 0x1D), At(info, 0x1E), At(info, 0x1F));
        return r < 0 ? r : 0;
    }

    static int Block91(uint id, uint buf, uint area, uint areaLen, uint p, uint info)
    {
        int r = TlvFind(p, areaLen - (p - area), 0x91, 8, out ushort l);
        if (r < 0) return r;
        uint d = p + (uint)r;
        ChildOf(id, buf, info, 0x78, d, l);
        Be32To(At(info, 0x17), d);
        Be32To(At(info, 0x18), d + 4);
        uint c = d + 8;
        Wr(info, 5, c);
        if (!Opt(r = Str(info, At(info, 0x19), c, (uint)l - 8, 0x92))) return r;
        r = Triple4A(info, At(info, 0x1A), At(info, 0x1B), At(info, 0x1C), At(info, 0x1D));
        return r < 0 ? r : 0;
    }

    /// <summary>The 0x4B block after the parent's cursor; 0x80 bytes.</summary>
    [Hle("sceNpCommerce2_0A121766", Nid = 0x0A121766, Library = C2)]
    [Hle("sceNpCommerce2_2447BED0", Nid = 0x2447BED0, Library = C2)]
    [Hle("sceNpCommerce2_4A315A31", Nid = 0x4A315A31, Library = C2)]
    [Hle("sceNpCommerce2_5EA4E81E", Nid = 0x5EA4E81E, Library = C2)]
    [Hle("sceNpCommerce2_677FE58A", Nid = 0x677FE58A, Library = C2)]
    int C2Block4B(uint parent, uint info) =>
        Need(parent, info) ?? Block4B(Rd(parent, 0), Rd(parent, 1), Rd(parent, 3), Rd(parent, 4), Rd(parent, 5), info);

    /// <summary>The 0x56 item after the parent's cursor (0x8D in the v3 layout) holding a 0x4B block (0x91 in v3).</summary>
    static int Wrapped4B(uint parent, uint info, int variant)
    {
        Rest(parent, out uint p, out uint n);
        int r = TlvFind(p, n, variant == 3 ? 0x8D : 0x56, 0x14, out ushort l);
        if (r < 0) return r;
        uint d = p + (uint)r;
        return variant == 3 ? Block91(Rd(parent, 0), Rd(parent, 1), d, l, d + 0x14, info)
                            : Block4B(Rd(parent, 0), Rd(parent, 1), d, l, d + 0x14, info);
    }

    [Hle("sceNpCommerce2_1E26E2D1", Nid = 0x1E26E2D1, Library = C2)]
    int C2Wrapped4B(uint parent, uint info) => Need(parent, info) ?? Wrapped4B(parent, info, 2);
}
