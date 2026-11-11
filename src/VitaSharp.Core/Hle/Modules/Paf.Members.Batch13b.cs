using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// libpaf 3.74 exports (unnamed in the community list, or C++ members) whose body is a single statement, read in the
/// decompiled module: member getters and setters, the address of a member, a constant, the clearing of a member
/// block, a call through the object's virtual table (the object's own slot, with the arguments it was given), and a
/// forward to another export the HLE has, with the member it passes.
/// </summary>
public sealed partial class Paf
{
    static uint U32(uint a) => GuestMemory.R32(a);
    static void U32(uint a, uint v) => GuestMemory.W32(a, v);
    static ushort U16(uint a) => GuestMemory.Read<ushort>(a);
    static void U16(uint a, ushort v) => GuestMemory.Write(a, v);
    static byte U8(uint a) => GuestMemory.Read<byte>(a);
    static void U8(uint a, byte v) => GuestMemory.Write(a, v);
    static uint PafCallGuest(uint fn, params ReadOnlySpan<uint> args) => GuestThread.Current!.CallGuest(fn, args);

    /// <summary>Runs the HLE's version of export <paramref name="nid"/> with <paramref name="args"/> in r0..r3 (its
    /// result stays in r0).</summary>
    unsafe void PafFwd(HleContext c, uint nid, params ReadOnlySpan<uint> args)
    {
        for (int i = 0; i < args.Length; i++) c.Cpu->R[i] = args[i];
        if (Emu.Hle.ImplOf(nid) is { } f) f(c);
    }

    [Hle("NID_A8F7D4C1", Nid = 0xA8F7D4C1, Library = "ScePafWidget")] void Paf_A8F7D4C1(uint p1, uint p2) { PafCallGuest(U32(U32(p1) + 0xfc), p1, p2, 0x1, 0x0); }
    [Hle("NID_6E59DBBC", Nid = 0x6E59DBBC, Library = "ScePafWidget")] void Paf_6E59DBBC(uint p1) { PafCallGuest(U32(U32(p1) + 0x1c0), p1); }
    [Hle("NID_8D86C86E", Nid = 0x8D86C86E, Library = "ScePafWidget")] uint Paf_8D86C86E(uint p1) => U32(p1 + 0x29c);
    [Hle("NID_C1B2AEC8", Nid = 0xC1B2AEC8, Library = "ScePafWidget")] uint Paf_C1B2AEC8(uint p1) => U32(p1 + 0x29c);
    [Hle("NID_1D77D6D9", Nid = 0x1D77D6D9, Library = "ScePafWidget")] void Paf_1D77D6D9(HleContext c) { uint p1 = c.Gpr(0); PafFwd(c, 0x399CBF5A, U32(p1 + 0x4)); }
    [Hle("NID_001CA5F6", Nid = 0x001CA5F6, Library = "ScePafWidget")] void Paf_001CA5F6(uint p1) { PafCallGuest(U32(U32(p1) + 0x1c0), p1); }
    [Hle("NID_722CAC54", Nid = 0x722CAC54, Library = "ScePafWidget")] void Paf_722CAC54(uint p1, uint p2) { PafCallGuest(U32(U32(p2) + 0x17c), p2, p1); }
    [Hle("NID_33B0385F", Nid = 0x33B0385F, Library = "ScePafWidget")] void Paf_33B0385F(uint p1) { PafCallGuest(U32(U32(p1) + 0x8c), p1); }
    [Hle("NID_BDEF4D3F", Nid = 0xBDEF4D3F, Library = "ScePafWidget")] void Paf_BDEF4D3F(uint p1) { PafCallGuest(U32(U32(p1) + 0x228), p1); }
    [Hle("NID_A4E53390", Nid = 0xA4E53390, Library = "ScePafWidget")] void Paf_A4E53390(uint p1) { U8(p1 + 0xf8, (byte)0x1); PafCallGuest(U32(U32(p1) + 0x230), p1); }
    [Hle("NID_1BE1C801", Nid = 0x1BE1C801, Library = "ScePafWidget")] uint Paf_1BE1C801(uint p1) => U32(p1 + 0x3c0);
    [Hle("NID_6EF1CB10", Nid = 0x6EF1CB10, Library = "ScePafWidget")] void Paf_6EF1CB10(uint p1, uint p2) { U32(p1 + 0x4, (uint)p2); }
    [Hle("NID_CBB4D949", Nid = 0xCBB4D949, Library = "ScePafWidget")] void Paf_CBB4D949(uint p1, uint p2, uint p3, uint p4) { PafCallGuest(U32(U32(p4) + 0x250), p4, p1, p2, p3); }
    [Hle("NID_37133F0F", Nid = 0x37133F0F, Library = "ScePafWidget")] void Paf_37133F0F(uint p1) { PafCallGuest(U32(U32(p1) + 0x1a0), p1); }
    [Hle("NID_3EFA0B3A", Nid = 0x3EFA0B3A, Library = "ScePafWidget")] void Paf_3EFA0B3A(uint p1) { PafCallGuest(U32(U32(p1) + 0x1e4), p1); }
    [Hle("NID_17BFAECF", Nid = 0x17BFAECF, Library = "ScePafWidget")] void Paf_17BFAECF(uint p1) { PafCallGuest(U32(U32(p1) + 0x1d4), p1); }
    [Hle("NID_EDB8D2BB", Nid = 0xEDB8D2BB, Library = "ScePafWidget")] uint Paf_EDB8D2BB(uint p1) => U32(p1 + 0x3fc);
    [Hle("NID_ECB0651E", Nid = 0xECB0651E, Library = "ScePafWidget")] void Paf_ECB0651E(uint p1) { U32(p1 + 0x14, 0); U32(p1 + 0x18, 0); U32(p1 + 0x1c, 0); U32(p1 + 0x20, 0); U32(p1 + 0x24, 0); U32(p1 + 0x28, 0); U32(p1 + 0x2c, 0); U32(p1 + 0x30, 0); U32(p1 + 0x34, 0); U32(p1 + 0x38, 0); U32(p1 + 0x3c, 0); U32(p1 + 0x40, 0); U32(p1 + 0x44, 0); U32(p1 + 0x48, 0); U32(p1 + 0x4c, 0); U32(p1 + 0x50, 0); U32(p1 + 0x58, 0); U32(p1 + 0x54, 0); }
    [Hle("NID_AB05B0A4", Nid = 0xAB05B0A4, Library = "ScePafWidget")] void Paf_AB05B0A4(uint p1) { U32(p1 + 0x14, 0); U32(p1 + 0x18, 0); U32(p1 + 0x1c, 0); U32(p1 + 0x20, 0); U32(p1 + 0x24, 0); U32(p1 + 0x28, 0); U32(p1 + 0x2c, 0); U32(p1 + 0x30, 0); U32(p1 + 0x34, 0); U32(p1 + 0x38, 0); U32(p1 + 0x3c, 0); U32(p1 + 0x40, 0); U32(p1 + 0x44, 0); U32(p1 + 0x48, 0); U32(p1 + 0x4c, 0); U32(p1 + 0x50, 0); U32(p1 + 0x58, 0); U32(p1 + 0x54, 0); }
    [Hle("NID_BA8234E9", Nid = 0xBA8234E9, Library = "ScePafWidget")] void Paf_BA8234E9(HleContext c) { PafFwd(c, 0xD41FD265); }
    [Hle("NID_0EB744C5", Nid = 0x0EB744C5, Library = "ScePafWidget")] void Paf_0EB744C5(HleContext c) { uint p1 = c.Gpr(0); PafFwd(c, 0xB6D42F51, U32(p1 + 0xc)); }
    [Hle("NID_9A7BE3B0", Nid = 0x9A7BE3B0, Library = "ScePafWidget")] uint Paf_9A7BE3B0(uint p1, uint p2, uint p3) { U8(p1 + 0xcd, (byte)p3); U32(p1 + 0xb8, (uint)p2); return 0x0; }
    [Hle("NID_C3D53C98", Nid = 0xC3D53C98, Library = "ScePafWidget")] void Paf_C3D53C98(uint p1) { PafCallGuest(U32(U32(p1) + 0x110), p1); }
    [Hle("NID_6E7B77AC", Nid = 0x6E7B77AC, Library = "ScePafGraphics")] void Paf_6E7B77AC(HleContext c) { uint p1 = c.Gpr(0); PafFwd(c, 0xD096336E, U32(p1 + 0x68c)); }
    [Hle("NID_E4B3B1D2", Nid = 0xE4B3B1D2, Library = "ScePafGraphics")] void Paf_E4B3B1D2(HleContext c) { uint p1 = c.Gpr(0); PafFwd(c, 0x0733D8AE, U32(p1 + 0x68c)); }
    [Hle("NID_A5727A5A", Nid = 0xA5727A5A, Library = "ScePafGraphics")] void Paf_A5727A5A(uint p1) { U32(p1 + 0x60, 0); U32(p1 + 0x74, 0); U32(p1 + 0x7c, 0); U32(p1 + 0x78, 0); }
    [Hle("NID_13DC10AE", Nid = 0x13DC10AE, Library = "ScePafGraphics")] void Paf_13DC10AE(uint p1) { U32(p1 + 0x60, 0); U32(p1 + 0x74, 0); U32(p1 + 0x7c, 0); U32(p1 + 0x78, 0); }
    [Hle("NID_93A32E5C", Nid = 0x93A32E5C, Library = "ScePafGraphics")] void Paf_93A32E5C(uint p1) { U32(p1 + 0xc, (uint)0x1); U32(p1 + 0x4, 0); U32(p1 + 0x10, 0); U32(p1 + 0x14, 0); U8(p1 + 0x8, (byte)0xf); U8(p1 + 0x9, 0); U8(p1 + 0xa, (byte)0x1); U8(p1 + 0xb, (byte)0x1); }
    [Hle("NID_22D7B1C0", Nid = 0x22D7B1C0, Library = "ScePafGraphics")] void Paf_22D7B1C0(uint p1, uint p2) { U32(p1 + 0x30, (uint)p2); }
    [Hle("NID_C398E016", Nid = 0xC398E016, Library = "ScePafGraphics")] void Paf_C398E016(uint p1, uint p2) { U32(p1 + 0x28, (uint)p2); }
    [Hle("NID_29A49BD5", Nid = 0x29A49BD5, Library = "ScePafGraphics")] uint Paf_29A49BD5(uint p1, uint p2) => p2;
    [Hle("NID_8B344DFA", Nid = 0x8B344DFA, Library = "ScePafGraphics")] uint Paf_8B344DFA(uint p1, uint p2) => p2;
    [Hle("NID_879B135D", Nid = 0x879B135D, Library = "ScePafGraphics")] uint Paf_879B135D(uint p1) => U32(p1 + 0x5c);
    [Hle("NID_D1978190", Nid = 0xD1978190, Library = "ScePafGraphics")] void Paf_D1978190(HleContext c) { PafFwd(c, 0x347AFDD9); }
    [Hle("NID_D1ED3E25", Nid = 0xD1ED3E25, Library = "ScePafGraphics")] void Paf_D1ED3E25(HleContext c) { PafFwd(c, 0x347AFDD9); }
    [Hle("NID_6664B7D2", Nid = 0x6664B7D2, Library = "ScePafGraphics")] void Paf_6664B7D2(uint p1) { U16(p1 + 0x72, 0); }
    [Hle("NID_D5DCD40C", Nid = 0xD5DCD40C, Library = "ScePafMisc")] uint Paf_D5DCD40C(uint p1) => U32(p1 + 0x60);
    [Hle("NID_7270DC05", Nid = 0x7270DC05, Library = "ScePafMisc")] uint Paf_7270DC05(uint p1) => p1 + 0xc;
    [Hle("NID_3A9F5294", Nid = 0x3A9F5294, Library = "ScePafMisc")] uint Paf_3A9F5294(uint p1) => U32(p1 + 0x2c);
    [Hle("NID_BC1135F2", Nid = 0xBC1135F2, Library = "ScePafMisc")] uint Paf_BC1135F2(uint p1) => U32(p1 + 0x60);
    [Hle("NID_E904B4E0", Nid = 0xE904B4E0, Library = "ScePafMisc")] uint Paf_E904B4E0(uint p1) => p1 + 0x10;
    [Hle("NID_F58AF47A", Nid = 0xF58AF47A, Library = "ScePafMisc")] uint Paf_F58AF47A(uint p1) => U32(p1 + 0x4c);
    [Hle("NID_7EA133E0", Nid = 0x7EA133E0, Library = "ScePafMisc")] void Paf_7EA133E0(HleContext c) { uint p1 = c.Gpr(0); PafFwd(c, 0xEA61662F, U32(p1 + 0xc)); }
    [Hle("NID_8830A864", Nid = 0x8830A864, Library = "ScePafMisc")] uint Paf_8830A864(uint p1) => U8(p1 + 0x11);
    [Hle("NID_0E846E5C", Nid = 0x0E846E5C, Library = "ScePafMisc")] uint Paf_0E846E5C(uint p1) => U8(p1 + 0x34);
    [Hle("NID_824D4AD7", Nid = 0x824D4AD7, Library = "ScePafResource")] void Paf_824D4AD7(uint p1, uint p2) { U32(p1 + 0x4, (uint)p2); }
    [Hle("NID_DD4EB2C7", Nid = 0xDD4EB2C7, Library = "ScePafResource")] void Paf_DD4EB2C7(uint p1, uint p2) { U32(p1 + 0x8, (uint)p2); }
    [Hle("NID_D58CCBBB", Nid = 0xD58CCBBB, Library = "ScePafResource")] uint Paf_D58CCBBB(uint p1) => U8(p1 + 0xc);
    [Hle("NID_120CECA8", Nid = 0x120CECA8, Library = "ScePafResource")] void Paf_120CECA8(uint p1, uint p2) { U8(p1 + 0xc, (byte)p2); }
    [Hle("NID_FA00F835", Nid = 0xFA00F835, Library = "ScePafResource")] uint Paf_FA00F835(uint p1) => U32(p1 + 0x8);
    [Hle("NID_04FC462E", Nid = 0x04FC462E, Library = "ScePafResource")] uint Paf_04FC462E(uint p1) => U32(p1 + 0x48);
    [Hle("NID_E3349545", Nid = 0xE3349545, Library = "ScePafResource")] uint Paf_E3349545(uint p1) => U32(p1 + 0x4c);
    [Hle("NID_80B84E6A", Nid = 0x80B84E6A, Library = "ScePafResource")] uint Paf_80B84E6A(uint p1) => U32(p1 + 0x14);
    [Hle("NID_1172424E", Nid = 0x1172424E, Library = "ScePafResource")] uint Paf_1172424E(uint p1) => U32(p1 + 0x18);
    [Hle("NID_50C5EB72", Nid = 0x50C5EB72, Library = "ScePafCommon")] uint Paf_50C5EB72(uint p1) => U32(p1 + 0x4);

    // ------------------------------------------------------------------ TextObj over its TextLayout (+0x50)

    /// <summary>TextObj's queries forward to its TextLayout (+0x50) with the same arguments; 0x80AF0022 (not ready)
    /// while it has none.</summary>
    unsafe void TextObjForward(HleContext c, uint nid)
    {
        uint layout = U32(c.Gpr(0) + 0x50);
        if (layout == 0) { c.Return(unchecked((int)0x80AF0022)); return; }
        c.Cpu->R[0] = layout;
        Emu.Hle.ImplOf(nid)!(c);
    }

    [Hle("_ZN3paf5graph7TextObj12GetLineCountERj")] void TextObjLineCount(HleContext c) => TextObjForward(c, 0xBFDF8AC1);
    [Hle("_ZN3paf5graph7TextObj9GetBoundsERNS_4math2v2E")] void TextObjBounds(HleContext c) => TextObjForward(c, 0xD08F1491);
    [Hle("_ZN3paf5graph7TextObj18GetLayoutAttributeENS0_19TextLayoutAttributeERNS_4math2v2E")]
    void TextObjLayoutV2(HleContext c) => TextObjForward(c, 0xE48BBEAB);

    // ------------------------------------------------------------------ TextLayout's layout attributes
    // flags +0x10 (attributes 9..0x12 = bits 0..9), ints +0xC/+0xD/+0xE (signed bytes, attributes 0..2) and +0x12
    // (attribute 3), floats +0x14/+0x18 (attributes 4, 5; 0x13 reads +0x18); every change is made under the layout's
    // optional RMutex (+0x8C), marks the dirty bits +0x70 and returns them (0 when nothing changed); 0x80AF2302 for
    // an attribute of another type.

    const int PafBadLayoutAttr = unchecked((int)0x80AF2302);

    unsafe void LayoutLocked(HleContext c, uint me, Action change)
    {
        uint m = U32(me + 0x8C);
        if (m != 0) CallPafExport(c, 0xF9FFA0BE, m);
        change();
        if (m != 0) CallPafExport(c, 0x95F9C45F, m);
    }

    unsafe void CallPafExport(HleContext c, uint nid, uint arg)
    {
        uint r0 = c.Cpu->R[0];
        c.Cpu->R[0] = arg;
        Emu.Hle.ImplOf(nid)?.Invoke(c);
        c.Cpu->R[0] = r0;
    }

    static readonly ushort[] LayoutBoolDirty = [6, 6, 4, 4, 7, 7, 7, 7, 4, 7];

    [Hle("_ZN3paf5graph10TextLayout18GetLayoutAttributeENS0_19TextLayoutAttributeERb")]
    int TextLayoutGetBool(uint me, int attr, uint outp)
    {
        if ((uint)(attr - 9) >= 10) return PafBadLayoutAttr;
        U8(outp, (byte)((U16(me + 0x10) >> (attr - 9)) & 1));
        return 0;
    }

    [Hle("_ZN3paf5graph10TextLayout18SetLayoutAttributeENS0_19TextLayoutAttributeEb")]
    int TextLayoutSetBool(HleContext c, uint me, int attr, bool on)
    {
        if ((uint)(attr - 9) >= 10) return 0;
        ushort bit = (ushort)(1 << (attr - 9));
        if (on == ((U16(me + 0x10) & bit) != 0)) return 0;
        // the last-but-one flag (attribute 0x11) only dirties the layout when it is cleared
        ushort dirty = attr == 0x11 ? (ushort)(on ? 0 : 4) : LayoutBoolDirty[attr - 9];
        LayoutLocked(c, me, () =>
        {
            U16(me + 0x70, (ushort)(U16(me + 0x70) | dirty));
            ushort v = U16(me + 0x10);
            U16(me + 0x10, (ushort)(on ? v | bit : v & ~bit));
        });
        return dirty;
    }

    [Hle("_ZN3paf5graph10TextLayout18GetLayoutAttributeENS0_19TextLayoutAttributeERi")]
    int TextLayoutGetInt(uint me, int attr, uint outp)
    {
        switch (attr)
        {
            case 0: case 1: case 2: U32(outp, (uint)(sbyte)U8(me + 0xC + (uint)attr)); return 0;
            case 3: U32(outp, U16(me + 0x12)); return 0;
            default: return PafBadLayoutAttr;
        }
    }

    [Hle("_ZN3paf5graph10TextLayout18SetLayoutAttributeENS0_19TextLayoutAttributeEi")]
    int TextLayoutSetInt(HleContext c, uint me, int attr, int value)
    {
        if (attr is >= 0 and <= 2)
        {
            uint a = me + 0xC + (uint)attr;
            if (value == (sbyte)U8(a)) return 0;
            LayoutLocked(c, me, () => { U8(a, (byte)value); U16(me + 0x70, (ushort)(U16(me + 0x70) | 6)); });
            return 6;
        }
        if (attr == 3)
        {
            if ((uint)value == U16(me + 0x12)) return 0;
            LayoutLocked(c, me, () => { U16(me + 0x12, (ushort)value); U16(me + 0x70, (ushort)(U16(me + 0x70) | 7)); });
            return 7;
        }
        return PafBadLayoutAttr;
    }

    [Hle("_ZN3paf5graph10TextLayout18GetLayoutAttributeENS0_19TextLayoutAttributeERf")]
    int TextLayoutGetFloat(uint me, int attr, uint outp)
    {
        // attribute 0x13 is the line height (+0x18), scaled by 1.35 in the module's large-text mode, which the HLE
        // does not have
        switch (attr)
        {
            case 4: F(outp, F(me + 0x14)); return 0;
            case 5: case 0x13: F(outp, F(me + 0x18)); return 0;
            default: return PafBadLayoutAttr;
        }
    }

    [Hle("_ZN3paf5graph10TextLayout18SetLayoutAttributeENS0_19TextLayoutAttributeEf")]
    int TextLayoutSetFloat(HleContext c, float value, uint me, int attr)
    {
        if (attr != 4 && attr != 5) return PafBadLayoutAttr;
        uint a = me + (attr == 4 ? 0x14u : 0x18u);
        if (value == F(a)) return 0;
        LayoutLocked(c, me, () => { F(a, value); U16(me + 0x70, (ushort)(U16(me + 0x70) | 7)); });
        return 7;
    }

    [Hle("_ZN3paf5graph7TextObj18GetLayoutAttributeENS0_19TextLayoutAttributeERb")] void TextObjLayoutBool(HleContext c) => TextObjForward(c, 0xE9CB81D3);
    [Hle("_ZN3paf5graph7TextObj18GetLayoutAttributeENS0_19TextLayoutAttributeERf")] void TextObjLayoutFloat(HleContext c) => TextObjForward(c, 0x4B2C6994);
    [Hle("_ZN3paf5graph7TextObj18GetLayoutAttributeENS0_19TextLayoutAttributeERi")] void TextObjLayoutInt(HleContext c) => TextObjForward(c, 0x5D9D3B40);
}
