using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

// np_commerce2.suprx result parsers, part 3: the product / SKU / content records (a common fixed block, then strings
// and counts) and the remaining items (see NpCommerce2.Parsers.cs). "v3" is the newer server layout some NIDs use
// (other tags for the same records).
public sealed partial class NpCommerce2
{
    // ---- the product fixed block (0x7D bytes, plus a 0x24-byte tail in the longer form)

    /// <summary>
    /// The product block at <paramref name="d"/>. <paramref name="w"/> gives the record words (0 = not wanted) for:
    /// [0] the block pointer, [1..3] values at +0x39/+0x3D/+0x41, [4..5] values at +0x45/+0x49, [6] a flag at +0x4D,
    /// [7..9] values at +0x55/+0x59/+0x5D, [10..12] values at +0x61/+0x65/+0x69, [13..14] times at +0x6D/+0x75; with
    /// the tail, [15..17] times at +0x7D/+0x85/+0x8D, [18] a value at +0x95, [19] a 64-bit value at +0x99. The flags
    /// word gets 1 when [4] is non-zero, 2 when the flag is set, 4 when the value at +0x51 is non-zero. Returns the
    /// block size.
    /// </summary>
    static int ProductBlock(uint rec, uint d, int flagsWord, int[] w, bool tail)
    {
        uint A(int i) => w[i] == 0 ? 0 : At(rec, w[i]);
        GuestMemory.W32(A(0), d);
        Be32To(A(1), d + 0x39);
        Be32To(A(2), d + 0x3D);
        Be32To(A(3), d + 0x41);
        if (A(4) != 0) Be32To(A(4), d + 0x45);
        if (A(5) != 0) Be32To(A(5), d + 0x49);
        GuestMemory.W32(A(6), Be32(d + 0x4D) != 0 ? 1u : 0u);
        uint extra = Be32(d + 0x51);
        uint[] offs = { 0x55, 0x59, 0x5D, 0x61, 0x65, 0x69 };
        for (int i = 0; i < offs.Length; i++) if (A(7 + i) != 0) Be32To(A(7 + i), d + offs[i]);
        if (A(13) != 0) Ticks(A(13), d + 0x6D);
        if (A(14) != 0) Ticks(A(14), d + 0x75);
        uint flags = Rd(rec, flagsWord);
        if (A(4) != 0 && GuestMemory.R32(A(4)) != 0) flags |= 1;
        if (GuestMemory.R32(A(6)) != 0) flags |= 2;
        if (extra != 0) flags |= 4;
        Wr(rec, flagsWord, flags);
        if (!tail) return 0x7D;
        Ticks(A(15), d + 0x7D);
        Ticks(A(16), d + 0x85);
        Ticks(A(17), d + 0x8D);
        Be32To(A(18), d + 0x95);
        Be64To(A(19), d + 0x99);
        return 0xA1;
    }

    static readonly int[] ProductWordsA = { 0x17, 0x18, 0x19, 0x1A, 0, 0, 0x1B, 0, 0x1C, 0x1D, 0x1E, 0x1F, 0x2A, 0x2C, 0x2E, 0x20, 0x22, 0x24, 0x26, 0x28 };
    static readonly int[] ProductWordsB = { 0x17, 0x18, 0x19, 0x1A, 0x1B, 0x1C, 0x1D, 0x1E, 0x1F, 0x20, 0x21, 0x22, 0x2E, 0x30, 0x32, 0x24, 0x26, 0x28, 0x2A, 0x2C };
    static readonly int[] ProductWordsC = { 0x17, 0x18, 0x19, 0x1A, 0, 0, 0x1B, 0, 0, 0, 0x1C, 0x1D, 0x1E, 0x20, 0x22 };

    /// <summary>The strings 0x08 / 0x38 and, for kind 2, 0x12 (the third word stays reply+4 otherwise).</summary>
    static int ProductNames(uint rec, uint p, uint len, uint kind, uint a, uint b, uint c)
    {
        int r;
        if ((r = Str(rec, a, p, len, 0x08)) < 0) return r;
        if ((r = Str(rec, b, p, len, 0x38)) < 0) return r;
        GuestMemory.W32(c, rec == 0 ? 0 : Rd(rec, 1) + 4);
        if (kind == 2 && (r = Str(rec, c, p, len, 0x12)) < 0) return r;
        return 0;
    }

    /// <summary>ProductNames plus the strings 0x07 / 0x10 / 0x04, the optional count of section 0x80 and the optional
    /// string 0xA6.</summary>
    static int ProductTexts(uint rec, uint p, uint len, uint kind, uint a, uint b, uint c, uint s7, uint s10, uint s4, uint count80, uint sA6)
    {
        int r = ProductNames(rec, p, len, kind, a, b, c);
        if (r < 0) return r;
        if ((r = Str(rec, s7, p, len, 0x07)) < 0) return r;
        if ((r = Str(rec, s10, p, len, 0x10)) < 0) return r;
        if ((r = Str(rec, s4, p, len, 0x04)) < 0) return r;
        if (!Opt(r = Count(p, len, 0x80, count80))) return r;
        return Opt(r = Str(rec, sA6, p, len, 0xA6)) ? 0 : r;
    }

    /// <summary>The game product (0x4D item after the parent's cursor; 0x8A in v3): the long product block, the texts,
    /// the strings 0x3C / 0x44, the counts of sections 0x3D / 0x3E / 0x85 and (v2) whether a 0x4B price is there
    /// (word 0x3E: 3 yes, 1 no); 0x100 bytes (0xF8 in v3).</summary>
    static int GameProduct(uint parent, uint info, int variant)
    {
        Rest(parent, out uint p, out uint n);
        GuestMemory.Fill(info, 0, variant == 3 ? 0xF8u : 0x100u);
        int r = TlvFind(p, n, variant == 3 ? 0x8A : 0x4D, 0xC1, out ushort l);
        if (r < 0) return r;
        uint d = p + (uint)r;
        Child(parent, info, 0x5C, d, l);
        r = ProductBlock(info, d, 0x3C, ProductWordsA, tail: true);
        uint c = d + (uint)r, m = (uint)l - (uint)r;
        Wr(info, 5, c);
        r = ProductTexts(info, c, m & 0xFFFF, Rd(info, 0x18), At(info, 0x30), At(info, 0x31), At(info, 0x32), At(info, 0x36),
            At(info, 0x37), At(info, 0x38), At(info, 0x3A), At(info, 0x3D));
        if (r < 0) return r;
        if (!Opt(r = Str(info, At(info, 0x33), c, m, 0x3C))) return r;
        if ((r = Count(c, m, 0x3D, At(info, 0x34))) < 0) return r;
        if ((r = Count(c, m, 0x3E, At(info, 0x35))) < 0) return r;
        if (!Opt(r = Str(info, At(info, 0x39), c, m, 0x44))) return r;
        if (variant == 2) Wr(info, 0x3E, TlvFind(c, m, 0x4B, 8, out _) < 0 ? 1u : 3u);
        return Opt(r = Count(c, m, 0x85, At(info, 0x3B))) ? 0 : r;
    }

    [Hle("sceNpCommerce2_093460F3", Nid = 0x093460F3, Library = C2)]
    int C2GameProduct(uint parent, uint info) => Need(parent, info) ?? GameProduct(parent, info, 2);

    /// <summary>The product with SKU list (0x98 bytes, 0xB8 record): the short product block (word 0x2B flags), the
    /// texts and the count of section 0x9C.</summary>
    [Hle("sceNpCommerce2_98E244BA", Nid = 0x98E244BA, Library = C2)]
    int C2Product4C(uint parent, uint info)
    {
        if (Need(parent, info) is int e) return e;
        Rest(parent, out uint p, out uint n);
        int r = TlvFind(p, n, 0x4C, 0xB5, out ushort l);
        if (r < 0) return r;
        uint d = p + (uint)r;
        Child(parent, info, 0xB8, d, l);
        r = ProductBlock(info, d, 0x2B, ProductWordsC, tail: false) + 0x24;
        uint c = d + (uint)r, m = (uint)l - (uint)r;
        Wr(info, 5, c);
        r = ProductTexts(info, c, m & 0xFFFF, Rd(info, 0x18), At(info, 0x24), At(info, 0x25), At(info, 0x26), At(info, 0x27),
            At(info, 0x28), At(info, 0x29), At(info, 0x2A), At(info, 0x2D));
        if (r < 0) return r;
        return Opt(r = Count(c, m, 0x9C, At(info, 0x2C))) ? 0 : r;
    }

    /// <summary>The SKU item <paramref name="index"/> (section 0x1F after the parent's cursor; 0x89 in v3): the long
    /// product block (flags word 0x3D), the names, the string 0x3C / 0xA6 and the counts 0x3D / 0x3E / 0x80 / 0x85 /
    /// 0x95; 0x100 bytes.</summary>
    static int SkuItem(uint parent, uint index, uint info, int variant)
    {
        Rest(parent, out uint p, out uint n);
        GuestMemory.Fill(info, 0, 0x100);
        int r = TlvElement(p, n, variant == 3 ? 0x89 : 0x1F, 0xA5, index, out ushort l);
        if (r < 0) return r;
        uint d = p + (uint)r;
        Child(parent, info, 0x5C, d, l);
        r = ProductBlock(info, d, 0x3D, ProductWordsB, tail: true);
        uint c = d + (uint)r, m = (uint)l - (uint)r;
        Wr(info, 5, c);
        if ((r = ProductNames(info, c, m & 0xFFFF, Rd(info, 0x18), At(info, 0x34), At(info, 0x35), At(info, 0x36))) < 0) return r;
        if (!Opt(r = Str(info, At(info, 0x37), c, m, 0x3C))) return r;
        if ((r = Count(c, m, 0x3D, At(info, 0x38))) < 0) return r;
        if ((r = Count(c, m, 0x3E, At(info, 0x39))) < 0) return r;
        if (!Opt(r = Count(c, m, 0x80, At(info, 0x3A)))) return r;
        if (!Opt(r = Count(c, m, 0x85, At(info, 0x3B)))) return r;
        if (!Opt(r = Count(c, m, 0x95, At(info, 0x3C)))) return r;
        return Opt(r = Str(info, At(info, 0x3E), c, m, 0xA6)) ? 0 : r;
    }

    [Hle("sceNpCommerce2_71B86720", Nid = 0x71B86720, Library = C2)]
    int C2SkuItem(uint parent, uint index, uint info) => Need(parent, info) ?? SkuItem(parent, index, info, 2);

    [Hle("sceNpCommerce2_1D9934A3", Nid = 0x1D9934A3, Library = C2)]
    int C2SkuItemV3(uint parent, uint index, uint info) => Need(parent, info) ?? SkuItem(parent, index, info, 3);

    /// <summary>The single SKU (0x1E item after the parent's cursor; 0x87 in v3): the long product block (flags word
    /// 0x37), the string 0x08, the counts 0x80 / 0x95 and the string 0xA6; 0xE8 bytes.</summary>
    static int SkuInfo(uint parent, uint info, int variant)
    {
        Rest(parent, out uint p, out uint n);
        GuestMemory.Fill(info, 0, 0xE8);
        int r = TlvFind(p, n, variant == 3 ? 0x87 : 0x1E, 0xA5, out ushort l);
        if (r < 0) return r;
        uint d = p + (uint)r;
        Child(parent, info, 0x5C, d, l);
        r = ProductBlock(info, d, 0x37, ProductWordsB, tail: true);
        uint c = d + (uint)r, m = (uint)l - (uint)r;
        Wr(info, 5, c);
        if ((r = Str(info, At(info, 0x34), c, m, 0x08)) < 0) return r;
        if (!Opt(r = Count(c, m, 0x80, At(info, 0x35)))) return r;
        if (!Opt(r = Count(c, m, 0x95, At(info, 0x36)))) return r;
        return Opt(r = Str(info, At(info, 0x38), c, m, 0xA6)) ? 0 : r;
    }

    [Hle("sceNpCommerce2_85C62297", Nid = 0x85C62297, Library = C2)]
    int C2SkuInfo(uint parent, uint info) => Need(parent, info) ?? SkuInfo(parent, info, 2);

    [Hle("sceNpCommerce2_EFEA25A3", Nid = 0xEFEA25A3, Library = C2)]
    int C2SkuInfoV3(uint parent, uint info) => Need(parent, info) ?? SkuInfo(parent, info, 3);

    // ---- the category / content block (0x3D bytes + 0x14)

    /// <summary>The 0x51-byte category block: pointer, a value at +0x31, a time at +0x35, values at +0x3D / +0x41, the
    /// raw flags at +0x45 (and "purchasable" = flags &amp; 0xA), the high bits of +0x49 and +0x4D != 0 into a bit set.</summary>
    static int CategoryBlock(uint d, uint ptr, uint val, uint ticks, uint v1, uint v2, uint purchasable, uint rawFlags, uint bits)
    {
        GuestMemory.W32(ptr, d);
        Be32To(val, d + 0x31);
        Ticks(ticks, d + 0x35);
        uint q = d + 0x3D;
        Be32To(v1, q);
        Be32To(v2, q + 4);
        uint f = Be32(q + 8);
        GuestMemory.W32(rawFlags, f);
        GuestMemory.W32(purchasable, (f & 2) != 0 || (f & 8) != 0 ? 1u : 0u);
        HighBits(bits, Be32(q + 0xC), Be32(q + 0x10));
        return 0x51;
    }

    static void HighBits(uint dst, uint f, uint g)
    {
        uint b = GuestMemory.R32(dst);
        if ((f & 0x80000000) != 0) b |= 1;
        if ((f & 0x40000000) != 0) b |= 2;
        if ((f & 0x20000000) != 0) b |= 4;
        if (g != 0) b |= 8;
        GuestMemory.W32(dst, b);
    }

    /// <summary>The strings 0x07 / 0x0C / 0x10 / 0x04 (all required).</summary>
    static int CategoryTexts(uint rec, uint p, uint len, uint a, uint b, uint c, uint d)
    {
        int r;
        if ((r = Str(rec, a, p, len, 0x07)) < 0) return r;
        if ((r = Str(rec, b, p, len, 0x0C)) < 0) return r;
        if ((r = Str(rec, c, p, len, 0x10)) < 0) return r;
        return Str(rec, d, p, len, 0x04);
    }

    /// <summary>The category (0x26 item after the parent's cursor; 0x8B in v3): the block, the texts, the string 0x11
    /// (a count in v3), the count of section 0x18 and the string 0x44; 0xA0 bytes.</summary>
    static int CategoryInfo(uint parent, uint info, int variant)
    {
        Rest(parent, out uint p, out uint n);
        GuestMemory.Fill(info, 0, 0xA0);
        int r = TlvFind(p, n, variant == 3 ? 0x8B : 0x26, 0x61, out ushort l);
        if (r < 0) return r;
        uint d = p + (uint)r;
        Child(parent, info, 0x5C, d, l);
        r = CategoryBlock(d, At(info, 0x17), At(info, 0x18), At(info, 0x1A), At(info, 0x1C), At(info, 0x1D), At(info, 0x1E),
            At(info, 0x1F), At(info, 0x20));
        uint c = d + (uint)r, m = (uint)l - (uint)r;
        Wr(info, 5, c);
        if ((r = CategoryTexts(info, c, m & 0xFFFF, At(info, 0x21), At(info, 0x22), At(info, 0x23), At(info, 0x24))) < 0) return r;
        if (variant == 3) { if (!Opt(r = Count(c, m, 0x11, At(info, 0x27)))) return r; }
        else if ((r = Str(info, At(info, 0x27), c, m, 0x11)) < 0) return r;
        if ((r = Count(c, m, 0x18, At(info, 0x25))) < 0) return r;
        return Opt(r = Str(info, At(info, 0x26), c, m, 0x44)) ? 0 : r;
    }

    [Hle("sceNpCommerce2_A66EF528", Nid = 0xA66EF528, Library = C2)]
    int C2CategoryInfo(uint parent, uint info) => Need(parent, info) ?? CategoryInfo(parent, info, 2);

    [Hle("sceNpCommerce2_22490B90", Nid = 0x22490B90, Library = C2)]
    int C2CategoryInfoV3(uint parent, uint info) => Need(parent, info) ?? CategoryInfo(parent, info, 3);

    /// <summary>The content category (0x27 item after the parent's cursor; 0x8C in v3): the block, the texts, the
    /// string 0x11 (a count in v3), the count 0x18, the string 0x0D, the counts 0x23 / 0x24 / 0x25 and the strings 0x44
    /// / 0x45 / 0x77; 0xB8 bytes.</summary>
    static int ContentCategory(uint parent, uint info, int variant)
    {
        Rest(parent, out uint p, out uint n);
        GuestMemory.Fill(info, 0, 0xB8);
        int r = TlvFind(p, n, variant == 3 ? 0x8C : 0x27, 0x6D, out ushort l);
        if (r < 0) return r;
        uint d = p + (uint)r;
        Child(parent, info, 0x5C, d, l);
        r = CategoryBlock(d, At(info, 0x17), At(info, 0x19), At(info, 0x1A), At(info, 0x1C), At(info, 0x18), At(info, 0x1D),
            At(info, 0x1E), At(info, 0x1F));
        uint c = d + (uint)r, m = (uint)l - (uint)r;
        Wr(info, 5, c);
        if ((r = CategoryTexts(info, c, m & 0xFFFF, At(info, 0x20), At(info, 0x21), At(info, 0x22), At(info, 0x23))) < 0) return r;
        if (variant == 3) { if (!Opt(r = Count(c, m, 0x11, At(info, 0x2C)))) return r; }
        else if ((r = Str(info, At(info, 0x2C), c, m, 0x11)) < 0) return r;
        if ((r = Count(c, m, 0x18, At(info, 0x24))) < 0) return r;
        if ((r = Str(info, At(info, 0x25), c, m, 0x0D)) < 0) return r;
        if ((r = Count(c, m, 0x23, At(info, 0x26))) < 0) return r;
        if ((r = Count(c, m, 0x24, At(info, 0x27))) < 0) return r;
        if ((r = Count(c, m, 0x25, At(info, 0x28))) < 0) return r;
        if (!Opt(r = Str(info, At(info, 0x29), c, m, 0x44))) return r;
        if (!Opt(r = Str(info, At(info, 0x2A), c, m, 0x45))) return r;
        return Opt(r = Str(info, At(info, 0x2B), c, m, 0x77)) ? 0 : r;
    }

    [Hle("sceNpCommerce2_7471D20F", Nid = 0x7471D20F, Library = C2)]
    int C2ContentCategory(uint parent, uint info) => Need(parent, info) ?? ContentCategory(parent, info, 2);

    [Hle("sceNpCommerce2_CAA91CCE", Nid = 0xCAA91CCE, Library = C2)]
    int C2ContentCategoryV3(uint parent, uint info) => Need(parent, info) ?? ContentCategory(parent, info, 3);

    /// <summary>A sub-category (0x21 / 0x22 item after the parent's cursor): pointer, value, time, the high-bit set
    /// (word 0x22), the texts and, for the 0x22 kind (word 0x17 = 1), the strings 0x0D / 0x77; 0xE0 bytes.</summary>
    static int SubCategory(uint parent, uint info, int tag, int minLen, uint kind)
    {
        Rest(parent, out uint p, out uint n);
        int r = TlvFind(p, n, tag, minLen, out ushort l);
        if (r < 0) return r;
        uint d = p + (uint)r;
        GuestMemory.Fill(info, 0, 0xE0);
        Wr(info, 0, Rd(parent, 0)); Wr(info, 1, Rd(parent, 1)); Wr(info, 3, d); Wr(info, 4, l);
        Wr(info, 0x17, kind);
        GuestMemory.W32(At(info, 0x18), d);
        Be32To(At(info, 0x19), d + 0x31);
        Ticks(At(info, 0x1A), d + 0x35);
        uint q = d + 0x3D;
        HighBits(At(info, 0x22), Be32(q + 0xC), Be32(q + 0x10));
        uint c = q + 0x14, m = (uint)l - (c - d);
        Wr(info, 5, c);
        r = CategoryTexts(info, c, m & 0xFFFF, At(info, 0x1C), At(info, 0x1D), At(info, 0x1E), At(info, 0x1F));
        if (r < 0 || kind != 1) return r;
        if ((r = Str(info, At(info, 0x20), c, m, 0x0D)) < 0) return r;
        return Opt(r = Str(info, At(info, 0x21), c, m, 0x77)) ? 0 : r;
    }

    [Hle("sceNpCommerce2_EC09FF89", Nid = 0xEC09FF89, Library = C2)]
    int C2SubCategory21(uint parent, uint info) => Need(parent, info) ?? SubCategory(parent, info, 0x21, 0x61, 0);

    [Hle("sceNpCommerce2_F988A255", Nid = 0xF988A255, Library = C2)]
    int C2SubCategory22(uint parent, uint info) => Need(parent, info) ?? SubCategory(parent, info, 0x22, 0x65, 1);

    // ---- the store product (0x7F after the parent's cursor; 0x8F in v3)

    static int StoreProduct(uint parent, uint info, int variant)
    {
        Rest(parent, out uint p, out uint n);
        GuestMemory.Fill(info, 0, 0xD0);
        int r = TlvFind(p, n, variant == 3 ? 0x8F : 0x7F, 100, out ushort l);
        if (r < 0) return r;
        uint d = p + (uint)r;
        Child(parent, info, 0x5C, d, l);
        Be32To(At(info, 0x17), d);
        Be32To(At(info, 0x18), d + 4);
        for (int i = 0; i < 3; i++) Be32To(At(info, 0x1A + i), d + 0xC + (uint)i * 4);
        uint f = Be32(d + 0x18);
        Wr(info, 0x1D, f);
        Wr(info, 0x19, (f & 2) != 0 || (f & 8) != 0 ? 1u : 0u);
        Be32To(At(info, 0x1E), d + 0x1C);
        Be32To(At(info, 0x1F), d + 0x20);
        Ticks(At(info, 0x20), d + 0x24);
        Ticks(At(info, 0x22), d + 0x2C);
        Ticks(At(info, 0x24), d + 0x34);
        Be32To(At(info, 0x26), d + 0x3C);
        Be64To(At(info, 0x28), d + 0x40);
        uint c = d + 0x48, m = (uint)l - 0x48;
        Wr(info, 5, c);
        if (variant == 3) { if (!Opt(r = Count(c, m, 0x11, At(info, 0x33)))) return r; }
        else if ((r = Str(info, At(info, 0x33), c, m, 0x11)) < 0) return r;
        if ((r = Count(c, m, 0x23, At(info, 0x2A))) < 0) return r;
        if ((r = Count(c, m, 0x24, At(info, 0x2B))) < 0) return r;
        if ((r = Count(c, m, 0x25, At(info, 0x2C))) < 0) return r;
        if (!Opt(r = Str(info, At(info, 0x2D), c, m, 0x44))) return r;
        if (!Opt(r = Str(info, At(info, 0x2E), c, m, 0x45))) return r;
        if (!Opt(r = Str(info, At(info, 0x2F), c, m, 0x3C))) return r;
        if ((r = Count(c, m, 0x3D, At(info, 0x30))) < 0) return r;
        if ((r = Count(c, m, 0x3E, At(info, 0x31))) < 0) return r;
        return Opt(r = Count(c, m, variant == 3 ? 0x90 : 0x85, At(info, 0x32))) ? 0 : r;
    }

    [Hle("sceNpCommerce2_165CA716", Nid = 0x165CA716, Library = C2)]
    int C2StoreProduct(uint parent, uint info) => Need(parent, info) ?? StoreProduct(parent, info, 2);

    [Hle("sceNpCommerce2_AC59EBB3", Nid = 0xAC59EBB3, Library = C2)]
    int C2StoreProductV3(uint parent, uint info) => Need(parent, info) ?? StoreProduct(parent, info, 3);

    // ---- the content item of the tag-2 list 0x2B (0x95 bytes and a kind-dependent price block)

    [Hle("sceNpCommerce2_D5E21C19", Nid = 0xD5E21C19, Library = C2)]
    int C2Content2B(uint parent, uint index, uint info)
    {
        if (Need(parent, info) is int e) return e;
        uint p = Rd(parent, 3);
        int r = TlvElement8(p, Rd(parent, 4), 0x2B, 0x95, index, out ushort l, Rd(parent, 6));
        if (r < 0) return r;
        uint d = p + (uint)r;
        Child(parent, info, 0x100, d, l);
        Wr(info, 0x17, d);
        Be32To(At(info, 0x18), d + 0x31);
        Be32To(At(info, 0x19), d + 0x35);
        Be32To(At(info, 0x1A), d + 0x39);
        Be64To(At(info, 0x1C), d + 0x3D);
        Ticks(At(info, 0x1E), d + 0x45);
        Ticks(At(info, 0x20), d + 0x4D);
        Wr(info, 0x22, Be32(d + 0x55) != 0 ? 1u : 0u);
        Ticks(At(info, 0x24), d + 0x59);
        Be64To(At(info, 0x3C), d + 0x61);
        Be32To(At(info, 0x3F), d + 0x69);
        Ticks(At(info, 0x26), d + 0x6D);
        Be32To(At(info, 0x28), d + 0x75);
        Wr(info, 0x29, Be32(d + 0x79) != 0 ? 1u : 0u);
        uint c = d + 0x7D;
        Wr(info, 5, c);
        int w = 0x2A;
        foreach (int tag in new[] { 0x09, 0x0A, 0x04, 0x05, 0x10, 0x14 })
            if ((r = Str(info, At(info, w++), c, (uint)l - 0x7D, tag)) < 0) return r;
        uint kind = Rd(info, 0x19), off = 0x7D;
        if (kind == 1)
        {
            r = TlvFind(c, (uint)l - 0x7D, 0x98, 0x14, out _);
            if (r < 0) return r;
            uint q = c + (uint)r;
            Be32To(At(info, 0x30), q);
            Be32To(At(info, 0x31), q + 4);
            Be64To(At(info, 0x32), q + 8);
        }
        else if (kind == 2 || kind == 4)
        {
            r = TlvFind(c, (uint)l - 0x7D, kind == 4 ? 0x8D : 0x56, 0x14, out _);
            if (r < 0) return r;
            uint q = c + (uint)r;
            Be32To(At(info, 0x30), q);
            Be32To(At(info, 0x31), q + 4);
            c = q + 0x14;
            off = c - d;
            uint m = (uint)l - off;
            if ((r = Str(info, At(info, 0x35), c, m, 0x59)) < 0) return r;
            if (!Opt(r = Str(info, At(info, 0x36), c, m, 0x55))) return r;
            if ((r = Str(info, At(info, 0x37), c, m, 0x07)) < 0) return r;
            if (!Opt(r = Str(info, At(info, 0x39), c, m, 0x44))) return r;
            Wr(info, 0x38, TlvFind(c, m, 0x4B, 8, out _) < 0 ? 1u : 3u);
            if (!Opt(r = Count(c, m, 0x9D, At(info, 0x34)))) return r;
        }
        if (!Opt(r = Str(info, At(info, 0x3A), c, (uint)l - off, 0x38))) return r;
        return Opt(r = Count(c, (uint)l - off, 0x80, At(info, 0x3E))) ? 0 : r;
    }

    // ---- remaining reply records

    /// <summary>A reply whose only field is the count of one section (word 0x17); 0x60 bytes.</summary>
    int RootCount(uint rec, uint buf, uint size, int type)
    {
        int r = Root(rec, 0x60, buf, size, out uint p, out uint n);
        if (r < 0) return r;
        r = Count(p, n, type, At(rec, 0x17));
        return r < 0 ? r : 0;
    }

    [Hle("sceNpCommerce2_A5ADC2A6", Nid = 0xA5ADC2A6, Library = C2)]
    int C2ReplyCount6B(uint rec, uint buf, uint size) => Need(rec, buf, size) ?? RootCount(rec, buf, size, 0x6B);

    [Hle("sceNpCommerce2_AA0D88AE", Nid = 0xAA0D88AE, Library = C2)]
    int C2ReplyCount38(uint rec, uint buf, uint size) => Need(rec, buf, size) ?? RootCount(rec, buf, size, 0x38);

    [Hle("sceNpCommerce2_B70BF176", Nid = 0xB70BF176, Library = C2)]
    int C2ReplyCount37(uint rec, uint buf, uint size) => Need(rec, buf, size) ?? RootCount(rec, buf, size, 0x37);

    [Hle("sceNpCommerce2_C28139BD", Nid = 0xC28139BD, Library = C2)]
    int C2ReplyCount61(uint rec, uint buf, uint size) => Need(rec, buf, size) ?? RootCount(rec, buf, size, 0x61);

    /// <summary>The count of section 0x78 (the record pointer itself is not checked).</summary>
    [Hle("sceNpCommerce2_8F24F115", Nid = 0x8F24F115, Library = C2)]
    int C2ReplyCount78(uint rec, uint buf, uint size) => Need(buf, size) ?? RootCount(rec, buf, size, 0x78);

    [Hle("sceNpCommerce2_E9C58817", Nid = 0xE9C58817, Library = C2)]
    [Hle("sceNpCommerce2_F762E758", Nid = 0xF762E758, Library = C2)]
    int C2BareReply3(uint rec, uint buf, uint size) => C2BareReply(rec, buf, size);

    [Hle("sceNpCommerce2_A8459628", Nid = 0xA8459628, Library = C2)]
    int C2ReplyList72(uint rec, uint buf, uint size) => Need(rec, buf, size) ?? RootList8(rec, buf, size, 0x72, 0x8A);

    [Hle("sceNpCommerce2_D44AAECA", Nid = 0xD44AAECA, Library = C2)]
    int C2ReplyList2B(uint rec, uint buf, uint size) => Need(rec, buf, size) ?? RootList8(rec, buf, size, 0x2B, 0x95);

    [Hle("sceNpCommerce2_91979E62", Nid = 0x91979E62, Library = C2)]
    int C2ReplyList71(uint rec, uint buf, uint size) => Need(rec, buf, size) ?? RootList8(rec, buf, size, 0x71, 0x31);

    static int RootList8(uint rec, uint buf, uint size, int tag, int minLen)
    {
        int r = Root(rec, 0x88, buf, size, out uint p, out uint n, keepLenient: true);
        return r < 0 ? r : TrimList8(rec, p, n, tag, minLen, At(rec, 0x17));
    }

    /// <summary>Reply with the strings 0x4F / 0x52 / 0x36 (words 0x17..0x19); 0x68 bytes.</summary>
    [Hle("sceNpCommerce2_9FB0D052", Nid = 0x9FB0D052, Library = C2)]
    int C2ReplyStrings3(uint rec, uint buf, uint size)
    {
        if (Need(rec, buf, size) is int e) return e;
        int r = Root(rec, 0x68, buf, size, out uint p, out uint n);
        if (r < 0) return r;
        if ((r = Str(rec, At(rec, 0x17), p, n, 0x4F)) < 0) return r;
        if ((r = Str(rec, At(rec, 0x18), p, n, 0x52)) < 0) return r;
        return (r = Str(rec, At(rec, 0x19), p, n, 0x36)) < 0 ? r : 0;
    }

    /// <summary>Reply with the required value 0x31 and 64-bit value 0x03 (words 0x17 / 0x18); 0x68 bytes.</summary>
    [Hle("sceNpCommerce2_BA03D443", Nid = 0xBA03D443, Library = C2)]
    int C2ReplyValue31And64(uint rec, uint buf, uint size)
    {
        if (Need(rec, buf, size) is int e) return e;
        int r = Root(rec, 0x68, buf, size, out uint p, out uint n);
        if (r < 0) return r;
        if ((r = TlvFind(p, n, 0x31, 4, out _)) < 0) return r;
        Be32To(At(rec, 0x17), p + (uint)r);
        if ((r = TlvFind(p, n, 0x03, 8, out _)) < 0) return r;
        Be64To(At(rec, 0x18), p + (uint)r);
        return 0;
    }

    /// <summary>Reply with the required values 0x15 / 0x2D and 64-bit value 0x03 (words 0x17 / 0x18 / 0x1A); 0x70 bytes.</summary>
    [Hle("sceNpCommerce2_CF1FA4E5", Nid = 0xCF1FA4E5, Library = C2)]
    int C2ReplyValues3(uint rec, uint buf, uint size)
    {
        if (Need(rec, buf, size) is int e) return e;
        int r = Root(rec, 0x70, buf, size, out uint p, out uint n);
        if (r < 0) return r;
        if ((r = TlvFind(p, n, 0x15, 4, out _)) < 0) return r;
        Be32To(At(rec, 0x17), p + (uint)r);
        if ((r = TlvFind(p, n, 0x2D, 4, out _)) < 0) return r;
        Be32To(At(rec, 0x18), p + (uint)r);
        if ((r = TlvFind(p, n, 0x03, 8, out _)) < 0) return r;
        Be64To(At(rec, 0x1A), p + (uint)r);
        return 0;
    }

    // ---- remaining items

    /// <summary>A record with one 32-bit value (word 0x17); 0x60 bytes.</summary>
    static int OneValue(uint parent, uint p, uint n, int tag, uint info)
    {
        int r = TlvFind(p, n, tag, 4, out ushort l);
        if (r < 0) return r;
        uint d = p + (uint)r;
        Child(parent, info, 0x60, d, l);
        Be32To(At(info, 0x17), d);
        Wr(info, 5, d + 4);
        return r;
    }

    [Hle("sceNpCommerce2_9E082DB2", Nid = 0x9E082DB2, Library = C2)]
    int C2Info29(uint parent, uint info) => Need(parent, info) ?? OneValue(parent, Rd(parent, 3), Rd(parent, 4), 0x29, info);

    [Hle("sceNpCommerce2_F6A4FC50", Nid = 0xF6A4FC50, Library = C2)]
    int C2Info28(uint parent, uint info)
    {
        if (Need(parent, info) is int e) return e;
        Rest(parent, out uint p, out uint n);
        return OneValue(parent, p, n, 0x28, info);
    }

    /// <summary>The 0x4E item after the parent's cursor: one value; 0x60 bytes. Called with kind 0 (a single item).</summary>
    [Hle("sceNpCommerce2_8672B0EB", Nid = 0x8672B0EB, Library = C2)]
    int C2Info4E(uint parent, uint info)
    {
        if (Need(parent, info) is int e) return e;
        Rest(parent, out uint p, out uint n);
        int r = TlvFind(p, n, 0x4E, 0xB9, out ushort l);
        if (r < 0) return r;
        uint d = p + (uint)r;
        Child(parent, info, 0x60, d, l);
        Be32To(At(info, 0x17), d);
        Wr(info, 5, d + 4);
        return r;
    }

    [Hle("sceNpCommerce2_7377AEC9", Nid = 0x7377AEC9, Library = C2)]
    [Hle("sceNpCommerce2_C995829A", Nid = 0xC995829A, Library = C2)]
    int C2Info2Cb(uint parent, uint info) => C2Info2C(parent, info);

    [Hle("sceNpCommerce2_AEB340A6", Nid = 0xAEB340A6, Library = C2)]
    int C2Info30b(uint parent, uint info) => C2Info30(parent, info);

    [Hle("sceNpCommerce2_C3B1179E", Nid = 0xC3B1179E, Library = C2)]
    int C2Item37b(uint parent, uint index, uint info) => C2Item37(parent, index, info);

    [Hle("sceNpCommerce2_9B1AE84E", Nid = 0x9B1AE84E, Library = C2)]
    [Hle("sceNpCommerce2_F520899E", Nid = 0xF520899E, Library = C2)]
    int C2Item18b(uint parent, uint index, uint info) => C2Item18(parent, index, info);

    /// <summary>The 0x2F item: three values and the count of its section 0x2E (lenient reply); 0x6C bytes.</summary>
    [Hle("sceNpCommerce2_A1E8C739", Nid = 0xA1E8C739, Library = C2)]
    [Hle("sceNpCommerce2_DBC8B377", Nid = 0xDBC8B377, Library = C2)]
    int C2Info2F(uint parent, uint info)
    {
        if (Need(parent, info) is int e) return e;
        uint p = Rd(parent, 3);
        int r = TlvFind(p, Rd(parent, 4), 0x2F, 0x10, out ushort l);
        if (r < 0) return r;
        uint d = p + (uint)r;
        Child(parent, info, 0x6C, d, l);
        for (int i = 0; i < 3; i++) Be32To(At(info, 0x17 + i), d + (uint)i * 4);
        uint c = d + 0xC;
        Wr(info, 5, c);
        r = TlvSection(c, (uint)l - 0xC, 0x2E, out _, out uint count, Rd(parent, 6) != 0);
        if (r >= 0) Wr(info, 0x1A, count);
        return r;
    }

    /// <summary>The 0x99 item after the parent's cursor: two values at +0x1C / +0x20 and the count of section 0x9C;
    /// 0x68 bytes.</summary>
    [Hle("sceNpCommerce2_A793AF75", Nid = 0xA793AF75, Library = C2)]
    int C2Info99(uint parent, uint info)
    {
        if (Need(parent, info) is int e) return e;
        Rest(parent, out uint p, out uint n);
        GuestMemory.Fill(info, 0, 0x68);
        int r = TlvFind(p, n, 0x99, 0x48, out ushort l);
        if (r < 0) return r;
        uint d = p + (uint)r;
        Child(parent, info, 0x5C, d, l);
        Be32To(At(info, 0x17), d + 0x1C);
        Be32To(At(info, 0x18), d + 0x20);
        uint c = d + 0x48;
        Wr(info, 5, c);
        return Opt(r = Count(c, (uint)l - 0x48, 0x9C, At(info, 0x19))) ? 0 : r;
    }

    /// <summary>The 0x9B item (after the parent's cursor): two inline pointers; 100 bytes.</summary>
    static void Info9B(uint parent, uint d, uint l, uint info)
    {
        Child(parent, info, 100, d, l);
        Wr(info, 0x17, d);
        Wr(info, 0x18, d + 6);
        Wr(info, 5, d + 0x47);
    }

    [Hle("sceNpCommerce2_9DB36783", Nid = 0x9DB36783, Library = C2)]
    int C2Info9B(uint parent, uint info)
    {
        if (Need(parent, info) is int e) return e;
        Rest(parent, out uint p, out uint n);
        int r = TlvFind(p, n, 0x9B, 0x47, out ushort l);
        if (r < 0) return r;
        Info9B(parent, p + (uint)r, l, info);
        return 0;
    }

    [Hle("sceNpCommerce2_AE7334BA", Nid = 0xAE7334BA, Library = C2)]
    int C2Item9B(uint parent, uint index, uint info)
    {
        if (Need(parent, info) is int e) return e;
        Rest(parent, out uint p, out uint n);
        int r = TlvElement(p, n, 0x9B, 0x47, index, out ushort l);
        if (r < 0) return r;
        Info9B(parent, p + (uint)r, l, info);
        return 0;
    }

    /// <summary>Item <paramref name="index"/> of section 0x16: two pointers, a 16-bit value, five values, the count of
    /// section 0x15 and the strings 0x7D / 0x7E; 0x88 bytes.</summary>
    [Hle("sceNpCommerce2_AED6006D", Nid = 0xAED6006D, Library = C2)]
    int C2Item16(uint parent, uint index, uint info)
    {
        if (Need(parent, info) is int e) return e;
        uint p = Rd(parent, 3);
        int r = TlvElement(p, Rd(parent, 4), 0x16, 0x2A, index, out ushort l);
        if (r < 0) return r;
        uint d = p + (uint)r;
        Child(parent, info, 0x88, d, l);
        Wr(info, 0x17, d);
        Wr(info, 0x18, d + 4);
        GuestMemory.Write(At(info, 0x19), Be16(d + 8));
        Be32To(At(info, 0x1A), d + 0xA);
        Be32To(At(info, 0x1B), d + 0xE);
        Be32To(At(info, 0x1D), d + 0x12);
        Be32To(At(info, 0x1E), d + 0x16);
        Be32To(At(info, 0x1F), d + 0x1A);
        uint c = d + 0x1E, m = (uint)l - 0x1E;
        Wr(info, 5, c);
        if ((r = Count(c, m, 0x15, At(info, 0x1C))) < 0) return r;
        if ((r = Str(info, At(info, 0x20), c, m, 0x7D)) < 0) return r;
        return Str(info, At(info, 0x21), c, m, 0x7E);
    }

    /// <summary>The 0x5E item: a 64-bit value, three inline pointers, values, a flag, a 64-bit value, a 16-bit value
    /// and the string 0x5D; 0x98 bytes.</summary>
    [Hle("sceNpCommerce2_B37493FA", Nid = 0xB37493FA, Library = C2)]
    [Hle("sceNpCommerce2_E829D968", Nid = 0xE829D968, Library = C2)]
    int C2Info5E(uint parent, uint info)
    {
        if (Need(parent, info) is int e) return e;
        uint p = Rd(parent, 3);
        int r = TlvFind(p, Rd(parent, 4), 0x5E, 0x3C, out ushort l);
        if (r < 0) return r;
        uint d = p + (uint)r;
        Child(parent, info, 0x98, d, l);
        Be64To(At(info, 0x18), d);
        Wr(info, 0x1A, d + 8);
        Wr(info, 0x1B, d + 0x19);
        Wr(info, 0x1C, d + 0x1C);
        Be32To(At(info, 0x1D), d + 0x22);
        Wr(info, 0x1F, Be32(d + 0x26) != 0 ? 1u : 0u);
        Be32To(At(info, 0x20), d + 0x2A);
        Be64To(At(info, 0x22), d + 0x2E);
        GuestMemory.Write(At(info, 0x24), Be16(d + 0x36));
        uint c = d + 0x38;
        Wr(info, 5, c);
        return Str(info, At(info, 0x1E), c, (uint)l - 0x38, 0x5D);
    }

    /// <summary>Item <paramref name="index"/> of section 0x6B: like the 0x70 item without its count (strings 0x6A /
    /// 0x5D); 0x98 bytes.</summary>
    [Hle("sceNpCommerce2_B882FC31", Nid = 0xB882FC31, Library = C2)]
    int C2Item6B(uint parent, uint index, uint info)
    {
        if (Need(parent, info) is int e) return e;
        uint p = Rd(parent, 3);
        int r = TlvElement(p, Rd(parent, 4), 0x6B, 0x34, index, out ushort l);
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
        return Str(info, At(info, 0x24), c, m, 0x5D);
    }

    /// <summary>Pointer to item <paramref name="index"/> of section 0x5C after the parent's cursor.</summary>
    [Hle("sceNpCommerce2_BDECB406", Nid = 0xBDECB406, Library = C2)]
    int C2Item5C(uint parent, uint index, uint outPtr)
    {
        if (Need(parent, outPtr) is int e) return e;
        Rest(parent, out uint p, out uint n);
        int r = TlvElement(p, n, 0x5C, 0, index, out _);
        if (r >= 0) GuestMemory.W32(outPtr, p + (uint)r);
        return r;
    }

    /// <summary>Item of section 0x17 after the parent's cursor: a kind (word 0x17) and, for kind 1, the string 0x10
    /// (word 0x18) or, for kind 2, the string 0x0E (word 0x19); 0x88 bytes.</summary>
    [Hle("sceNpCommerce2_C4CC8E22", Nid = 0xC4CC8E22, Library = C2)]
    int C2Item17(uint parent, uint index, uint info)
    {
        if (Need(parent, info) is int e) return e;
        Rest(parent, out uint p, out uint n);
        int r = TlvElement(p, n, 0x17, 8, index, out ushort l);
        if (r < 0) return r;
        uint d = p + (uint)r;
        Child(parent, info, 0x88, d, l);
        Wr(info, 0x18, Rd(parent, 1) + 4);
        Wr(info, 0x19, Rd(parent, 1) + 4);
        uint kind = Be32(d);
        Wr(info, 0x17, kind);
        uint c = d + 4;
        Wr(info, 5, c);
        if (kind == 1) r = Str(info, At(info, 0x18), c, (uint)l - 4, 0x10);
        else if (kind == 2) r = Str(info, At(info, 0x19), c, (uint)l - 4, 0x0E);
        return r;
    }

    /// <summary>The 0x69 item: two yes/no values (1 = no, 2 = yes); 100 bytes.</summary>
    [Hle("sceNpCommerce2_C9183FC5", Nid = 0xC9183FC5, Library = C2)]
    int C2Info69(uint parent, uint info)
    {
        if (Need(parent, info) is int e) return e;
        uint p = Rd(parent, 3);
        int r = TlvFind(p, Rd(parent, 4), 0x69, 8, out ushort l);
        if (r < 0) return r;
        uint d = p + (uint)r;
        Child(parent, info, 100, d, l);
        Wr(info, 0x17, Be32(d) == 0 ? 1u : 2u);
        Wr(info, 0x18, Be32(d + 4) == 0 ? 1u : 2u);
        Wr(info, 5, d + 8);
        return r;
    }

    /// <summary>Item of section 0x7A after the parent's cursor: a time, three values and seven strings plus the count of
    /// section 0x80; 0x98 bytes.</summary>
    [Hle("sceNpCommerce2_95C0850D", Nid = 0x95C0850D, Library = C2)]
    int C2Item7A(uint parent, uint index, uint info)
    {
        if (Need(parent, info) is int e) return e;
        Rest(parent, out uint p, out uint n);
        int r = TlvElement(p, n, 0x7A, 0x65, index, out ushort l);
        if (r < 0) return r;
        uint d = p + (uint)r;
        Child(parent, info, 0x98, d, l);
        Wr(info, 0x17, d);
        Ticks(At(info, 0x18), d + 0x39);
        Be32To(At(info, 0x1A), d + 0x41);
        Be32To(At(info, 0x22), d + 0x45);
        Be32To(At(info, 0x23), d + 0x49);
        uint c = d + 0x4D, m = (uint)l - 0x4D;
        Wr(info, 5, c);
        if (!Opt(r = Str(info, At(info, 0x1B), c, m, 0x04))) return r;
        int w = 0x1C;
        foreach (int tag in new[] { 0x07, 0x08, 0x10, 0x0C, 0x0D, 0x77 })
            if ((r = Str(info, At(info, w++), c, m, tag)) < 0) return r;
        return Opt(r = Count(c, m, 0x80, At(info, 0x24))) ? 0 : r;
    }

    /// <summary>Item <paramref name="index"/> of the tag-2 list 0x73: an inline string, a value, times, a flag and the
    /// strings 0x07 / 0x82 / 0x08 with the counts 0x7B / 0x80; 0xA0 bytes.</summary>
    [Hle("sceNpCommerce2_D8E5E2DA", Nid = 0xD8E5E2DA, Library = C2)]
    int C2Item73(uint parent, uint index, uint info)
    {
        if (Need(parent, info) is int e) return e;
        uint p = Rd(parent, 3);
        int r = TlvElement8(p, Rd(parent, 4), 0x73, 0x9E, index, out ushort l, Rd(parent, 6));
        if (r < 0) return r;
        uint d = p + (uint)r;
        Child(parent, info, 0xA0, d, l);
        Wr(info, 0x17, d);
        Wr(info, 0x18, d + 0x39);
        Be32To(At(info, 0x19), d + 0x72);
        Ticks(At(info, 0x1A), d + 0x76);
        Be32To(At(info, 0x1C), d + 0x7E);
        Wr(info, 0x1D, Be32(d + 0x82) != 0 ? 1u : 0u);
        Ticks(At(info, 0x1E), d + 0x86);
        Ticks(At(info, 0x20), d + 0x8E);
        uint c = d + 0x96, m = (uint)l - 0x96;
        Wr(info, 5, c);
        if ((r = Str(info, At(info, 0x22), c, m, 0x07)) < 0) return r;
        if (!Opt(r = Str(info, At(info, 0x23), c, m, 0x82))) return r;
        if ((r = Str(info, At(info, 0x24), c, m, 0x08)) < 0) return r;
        if ((r = Count(c, m, 0x7B, At(info, 0x25))) < 0) return r;
        return Opt(r = Count(c, m, 0x80, At(info, 0x26))) ? 0 : r;
    }

    /// <summary>Item <paramref name="index"/> of section 0x28 (lenient section lookup): one value; 0x60 bytes.</summary>
    [Hle("sceNpCommerce2_DAE82F31", Nid = 0xDAE82F31, Library = C2)]
    int C2Item28L(uint parent, uint index, uint info)
    {
        if (Need(parent, info) is int e) return e;
        uint p = Rd(parent, 3);
        int r = TlvElementLenient(p, Rd(parent, 4), 0x28, 4, index, out ushort l, Rd(parent, 6) != 0);
        if (r < 0) return r;
        uint d = p + (uint)r;
        Child(parent, info, 0x60, d, l);
        Be32To(At(info, 0x17), d);
        Wr(info, 5, d + 4);
        return r;
    }

    /// <summary>Item of section 0x2E after the parent's cursor (lenient lookup): seven values and whether a 0x18 item
    /// follows (word 0x1D); 0x7C bytes.</summary>
    [Hle("sceNpCommerce2_FFED270E", Nid = 0xFFED270E, Library = C2)]
    int C2Item2E(uint parent, uint index, uint info)
    {
        if (Need(parent, info) is int e) return e;
        Rest(parent, out uint p, out uint n);
        int r = TlvElementLenient(p, n, 0x2E, 0x1C, index, out ushort l, Rd(parent, 6) != 0);
        if (r < 0) return r;
        uint d = p + (uint)r;
        Child(parent, info, 0x7C, d, l);
        for (int i = 0; i < 6; i++) Be32To(At(info, 0x17 + i), d + (uint)i * 4);
        Be32To(At(info, 0x1E), d + 0x18);
        uint c = d + 0x1C;
        Wr(info, 5, c);
        r = TlvFind(c, (uint)l - 0x1C, 0x18, 0x19, out _);
        if (r < 0 && r != C2_NOT_FOUND) return r;
        Wr(info, 0x1D, r < 0 ? 0u : 1u);
        return 0;
    }

    /// <summary>Like <see cref="TlvElement"/>, but the section lookup may be lenient and items are searched to the end
    /// of the area.</summary>
    internal static int TlvElementLenient(uint p, uint len, int tag, int minLen, uint index, out ushort elemLen, bool lenient)
    {
        elemLen = 0;
        int r = TlvSection(p, len, tag, out _, out uint count, lenient);
        if (r < 0) return r;
        if (index >= count) return C2_NO_INDEX;
        uint cur = p + (uint)r;
        ushort last = 0;
        for (uint i = 0; i <= index; i++)
        {
            if (i != 0) cur += last;
            r = TlvFind(cur, len - (cur - p), tag, minLen, out last);
            if (r < 0) return r;
            cur += (uint)r;
        }
        elemLen = last;
        return (int)(cur - p);
    }

    /// <summary>Item of section 0x15 after the parent's cursor: one value; 0x60 bytes.</summary>
    [Hle("sceNpCommerce2_E2415631", Nid = 0xE2415631, Library = C2)]
    int C2Item15(uint parent, uint index, uint info)
    {
        if (Need(parent, info) is int e) return e;
        Rest(parent, out uint p, out uint n);
        int r = TlvElement(p, n, 0x15, 4, index, out ushort l);
        if (r < 0) return r;
        uint d = p + (uint)r;
        Child(parent, info, 0x60, d, l);
        Be32To(At(info, 0x17), d);
        return r;
    }

    /// <summary>Item of section 0x6F after the parent's cursor: a 64-bit value, a time, two values, the string 0x6E and
    /// the count of section 0x6D; 0x80 bytes.</summary>
    [Hle("sceNpCommerce2_E3E40B5E", Nid = 0xE3E40B5E, Library = C2)]
    int C2Item6F(uint parent, uint index, uint info)
    {
        if (Need(parent, info) is int e) return e;
        Rest(parent, out uint p, out uint n);
        int r = TlvElement(p, n, 0x6F, 0x1C, index, out ushort l);
        if (r < 0) return r;
        uint d = p + (uint)r;
        Child(parent, info, 0x80, d, l);
        Be64To(At(info, 0x18), d);
        Ticks(At(info, 0x1A), d + 8);
        Be32To(At(info, 0x1C), d + 0x10);
        Be32To(At(info, 0x1D), d + 0x14);
        uint c = d + 0x18, m = (uint)l - 0x18;
        Wr(info, 5, c);
        if ((r = Str(info, At(info, 0x1E), c, m, 0x6E)) < 0) return r;
        return Opt(r = Count(c, m, 0x6D, At(info, 0x1F))) ? 0 : r;
    }

    /// <summary>Item <paramref name="index"/> of section 0x74: a kind (word 0x17, only 1 or 2 kept; raw in 0x18),
    /// values and a flag; 0x74 bytes.</summary>
    [Hle("sceNpCommerce2_E77A9E8B", Nid = 0xE77A9E8B, Library = C2)]
    int C2Item74(uint parent, uint index, uint info)
    {
        if (Need(parent, info) is int e) return e;
        uint p = Rd(parent, 3);
        int r = TlvElement(p, Rd(parent, 4), 0x74, 0x14, index, out ushort l);
        if (r < 0) return r;
        uint d = p + (uint)r;
        Child(parent, info, 0x74, d, l);
        uint kind = Be32(d);
        Wr(info, 0x17, kind is 1 or 2 ? kind : 0);
        Wr(info, 0x18, kind);
        Be32To(At(info, 0x19), d + 4);
        Wr(info, 0x1A, Be32(d + 8) != 0 ? 1u : 0u);
        Be32To(At(info, 0x1B), d + 0xC);
        Be32To(At(info, 0x1C), d + 0x10);
        Wr(info, 5, d + 0x14);
        return r;
    }

    /// <summary>Item of section 0x3B after the parent's cursor: data pointer and the string 0x3A; 100 bytes.</summary>
    [Hle("sceNpCommerce2_F682B145", Nid = 0xF682B145, Library = C2)]
    int C2Item3B(uint parent, uint index, uint info)
    {
        if (Need(parent, info) is int e) return e;
        Rest(parent, out uint p, out uint n);
        int r = TlvElement(p, n, 0x3B, 0x45, index, out ushort l);
        if (r < 0) return r;
        uint d = p + (uint)r;
        Child(parent, info, 100, d, l);
        Wr(info, 0x17, d);
        uint c = d + 0x41;
        Wr(info, 5, c);
        return Str(info, At(info, 0x18), c, (uint)l - 0x41, 0x3A);
    }

    // ---- id helpers (no library state)

    /// <summary>(const char *id, char *out, SceSize size): the id up to its third '-' (or its first 0x38 characters);
    /// 0x80550F07 without two '-', 0x80550F84 when it does not fit.</summary>
    [Hle("sceNpCommerce2_9FBF9536", Nid = 0x9FBF9536, Library = C2)]
    int C2IdPrefix(uint id, uint outp, uint size)
    {
        if (id == 0) return unchecked((int)0x80550F07);
        uint q = id;
        for (int i = 0; i < 2; i++)
        {
            q = StrChr(q, (byte)'-');
            if (q == 0) return unchecked((int)0x80550F07);
            q++;
        }
        uint end = StrChr(q, (byte)'-');
        if (end == 0) end = id + StrNLen(id, 0x38);
        uint len = end - id;
        if (len >= size) return unchecked((int)0x80550F84);
        GuestMemory.Copy(outp, id, len);
        GuestMemory.Write<byte>(outp + len, 0);
        return 0;
    }

    /// <summary>(const char *id, int *out): 1 when the id is a PS4 Link / Remote Play service id ("IP910" + '1'..'5' +
    /// "-NPIA9000" + '5'..'9' + '_'), else 0. NULL 0x80550F03.</summary>
    [Hle("sceNpCommerce2_BC44F7F9", Nid = 0xBC44F7F9, Library = C2)]
    int C2IsLinkServiceId(uint id, uint outp)
    {
        if (id == 0 || outp == 0) return C2_NULL;
        const string a = "IP910", b = "-NPIA9000";
        uint len = StrNLen(id, 0x38);
        uint ok = 0;
        if (len >= a.Length + b.Length + 4 && Matches(id, a))
        {
            byte c = GuestMemory.Read<byte>(id + (uint)a.Length);
            uint q = id + (uint)a.Length + 1;
            if (c is >= (byte)'1' and <= (byte)'5' && Matches(q, b))
            {
                byte c2 = GuestMemory.Read<byte>(q + (uint)b.Length);
                if (c2 is >= (byte)'5' and <= (byte)'9' && GuestMemory.Read<byte>(q + (uint)b.Length + 1) == '_') ok = 1;
            }
        }
        GuestMemory.W32(outp, ok);
        return 0;
    }

    static bool Matches(uint p, string s)
    {
        for (int i = 0; i < s.Length; i++) if (GuestMemory.Read<byte>(p + (uint)i) != s[i]) return false;
        return true;
    }

    static uint StrChr(uint p, byte c)
    {
        for (; ; p++)
        {
            byte b = GuestMemory.Read<byte>(p);
            if (b == c) return p;
            if (b == 0) return 0;
        }
    }

    static uint StrNLen(uint p, uint max)
    {
        uint n = 0;
        while (n < max && GuestMemory.Read<byte>(p + n) != 0) n++;
        return n;
    }

    // ---- the NP Manager hand-off

    uint _c2OwnerName;

    /// <summary>(a, const char **id, c): hands the id to the NP Manager (SceNpManager NID_9554802B, a tail call: its
    /// result is the return value) with the owner name "NpCommerce2"; nothing when an argument is NULL.</summary>
    [Hle("sceNpCommerce2_FF13E2C3", Nid = 0xFF13E2C3, Library = C2)]
    unsafe void C2RegisterId(HleContext ctx, uint a, uint idp, uint c)
    {
        const uint target = 0x9554802B;
        if (a == 0 || idp == 0 || c == 0 || !Emu.Hle.Implements(target)) { ctx.Return(0); return; }
        if (_c2OwnerName == 0)
        {
            _c2OwnerName = Emu.Memory.Alloc(0x20, "NpCommerce2 owner name");
            GuestMemory.Fill(_c2OwnerName, 0, 0x20);
            GuestMemory.WriteCString(_c2OwnerName, "NpCommerce2");
        }
        ctx.Cpu->R[0] = a;
        ctx.Cpu->R[1] = GuestMemory.R32(idp);
        ctx.Cpu->R[2] = _c2OwnerName;
        var f = Emu.Hle.Functions[(int)Emu.Hle.GetHleFunctionIndex(target, 0, "SceNpManager")];
        if (f.Invoke != null) f.Invoke(ctx); else ctx.Return(0);
    }
}
