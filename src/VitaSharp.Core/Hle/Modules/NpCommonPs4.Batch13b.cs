using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// np_common_ps4.suprx 3.74: more exports whose body is a single statement, read in the decompiled module: member
/// getters and setters, emptiness and equality tests of a container's two cursors (+0x10/+0x14), clears of a member
/// block (memset), the address of a base class (this - 4), calls through an allocator's or an object's function
/// table, and forwards to another export of the library (or of the kernel) with the member it passes.
/// </summary>
public sealed partial class NpCommonPs4
{
    static uint U32(uint a) => GuestMemory.R32(a);
    static void U32(uint a, uint v) => GuestMemory.W32(a, v);
    static byte U8(uint a) => GuestMemory.Read<byte>(a);
    static void U8(uint a, byte v) => GuestMemory.Write(a, v);
    static uint CallGuestFn(uint fn, params ReadOnlySpan<uint> args) => GuestThread.Current!.CallGuest(fn, args);

    /// <summary>Runs the HLE's version of export <paramref name="nid"/> with <paramref name="args"/> in r0..r3 (its
    /// result stays in r0).</summary>
    unsafe void Fwd(HleContext c, uint nid, params ReadOnlySpan<uint> args)
    {
        for (int i = 0; i < args.Length; i++) c.Cpu->R[i] = args[i];
        if (Emu.Hle.ImplOf(nid) is { } f) f(c);
    }

    [Hle("NID_67BBE5F9", Nid = 0x67BBE5F9, Library = "SceNpCommonPs4")] void Ps4_67BBE5F9(uint p1, uint p2, uint p3) { CallGuestFn(U32(p1 + 0x4), p2, p3, U32(p1 + 0xc)); }
    [Hle("NID_A37CE123", Nid = 0xA37CE123, Library = "SceNpCommonPs4")] void Ps4_A37CE123(HleContext c) { Fwd(c, 0x9DCB4B7A); }
    [Hle("NID_E47C64E8", Nid = 0xE47C64E8, Library = "SceNpCommonPs4")] void Ps4_E47C64E8(HleContext c) { uint p1 = c.Gpr(0); Fwd(c, 0xC88D44AD, U32(p1), 0x0); }
    [Hle("NID_3D73D77E", Nid = 0x3D73D77E, Library = "SceNpCommonPs4")] void Ps4_3D73D77E(HleContext c) { uint p1 = c.Gpr(0); Fwd(c, 0x6ED2E2DC, U32(p1)); }
    [Hle("NID_62A77FA7", Nid = 0x62A77FA7, Library = "SceNpCommonPs4")] void Ps4_62A77FA7(HleContext c) { uint p1 = c.Gpr(0); Fwd(c, 0xC2E7AC22, U32(p1)); }
    [Hle("NID_2D74359F", Nid = 0x2D74359F, Library = "SceNpCommonPs4")] void Ps4_2D74359F(HleContext c) { uint p1 = c.Gpr(0); Fwd(c, 0xCB78710D, U32(p1)); }
    [Hle("NID_3AAA7DF7", Nid = 0x3AAA7DF7, Library = "SceNpCommonPs4")] void Ps4_3AAA7DF7(HleContext c) { uint p1 = c.Gpr(0); Fwd(c, 0xC78F5DF5, p1 + 0x4); }
    [Hle("NID_A486D855", Nid = 0xA486D855, Library = "SceNpCommonPs4")] uint Ps4_A486D855(uint p1) => p1 - 0x4;
    [Hle("NID_53E8E212", Nid = 0x53E8E212, Library = "SceNpCommonPs4")] bool Ps4_53E8E212(uint p1) => U32(p1 + 0x14) != 0x0;
    [Hle("NID_C6A17ECD", Nid = 0xC6A17ECD, Library = "SceNpCommonPs4")] uint Ps4_C6A17ECD(uint p1, uint p2) { U8(p1 + 0x4, (byte)p2); return 0x0; }
    [Hle("NID_C6BD1713", Nid = 0xC6BD1713, Library = "SceNpCommonPs4")] uint Ps4_C6BD1713(uint p1) => U8(p1 + 0x4);
    [Hle("NID_34DAA7F1", Nid = 0x34DAA7F1, Library = "SceNpCommonPs4")] void Ps4_34DAA7F1(uint p1) { U32(p1 + 0x4, 0); U32(p1 + 0x8, 0); U32(p1 + 0xc, 0); U32(p1 + 0x10, 0); U32(p1 + 0x14, 0); }
    [Hle("NID_2221BD7C", Nid = 0x2221BD7C, Library = "SceNpCommonPs4")] void Ps4_2221BD7C(uint p1) { U32(p1 + 0x4, 0); U32(p1 + 0x8, 0); U32(p1 + 0xc, 0); U32(p1 + 0x10, 0); U32(p1 + 0x14, 0); }
    [Hle("NID_BE6CB06B", Nid = 0xBE6CB06B, Library = "SceNpCommonPs4")] void Ps4_BE6CB06B(uint p1) { U32(p1 + 0x8, (uint)U32(p1 + 0x4)); U32(p1 + 0xc, (uint)U32(p1 + 0x4)); U32(p1 + 0x10, 0); }
    [Hle("NID_E67CA80C", Nid = 0xE67CA80C, Library = "SceNpCommonPs4")] bool Ps4_E67CA80C(uint p1) => U32(p1 + 0x10) == U32(p1 + 0x14);
    [Hle("NID_B85224E9", Nid = 0xB85224E9, Library = "SceNpCommonPs4")] uint Ps4_B85224E9(uint p1) => U32(p1 + 0x10);
    [Hle("NID_7F252C09", Nid = 0x7F252C09, Library = "SceNpCommonPs4")] void Ps4_7F252C09(HleContext c) { uint p1 = c.Gpr(0); Fwd(c, 0x38CC9AF5, U32(p1 + 0x18), U32(p1 + 0x4)); }
    [Hle("NID_E1F6AE0F", Nid = 0xE1F6AE0F, Library = "SceNpCommonPs4")] bool Ps4_E1F6AE0F(uint p1) => U32(p1 + 0x4) != 0x0;
    [Hle("NID_EBC4EF55", Nid = 0xEBC4EF55, Library = "SceNpCommonPs4")] void Ps4_EBC4EF55(uint p1) { GuestMemory.Fill(p1 + 0x4, (byte)0x0, 0xc); }
    [Hle("NID_DA48D36D", Nid = 0xDA48D36D, Library = "SceNpCommonPs4")] void Ps4_DA48D36D(uint p1) { GuestMemory.Fill(p1 + 0x4, (byte)0x0, 0x10); }
    [Hle("NID_71763AED", Nid = 0x71763AED, Library = "SceNpCommonPs4")] bool Ps4_71763AED(uint p1) => U32(p1 + 0x28) != 0x0;
    [Hle("NID_09998227", Nid = 0x09998227, Library = "SceNpCommonPs4")] bool Ps4_09998227(uint p1) => U32(p1 + 0x28) != 0x0;
    [Hle("NID_3C9F833F", Nid = 0x3C9F833F, Library = "SceNpCommonPs4")] bool Ps4_3C9F833F(uint p1) => U32(p1 + 0x8) != 0x0;
    [Hle("NID_82F5AC22", Nid = 0x82F5AC22, Library = "SceNpCommonPs4")] void Ps4_82F5AC22(uint p1) { U32(p1 + 0x8, 0); }
    [Hle("NID_6EA40010", Nid = 0x6EA40010, Library = "SceNpCommonPs4")] void Ps4_6EA40010(uint p1) { U8(p1 + 0x8, 0); }
    [Hle("NID_6BE664A1", Nid = 0x6BE664A1, Library = "SceNpCommonPs4")] bool Ps4_6BE664A1(uint p1) => U32(p1 + 0x8) != 0x0;
    [Hle("NID_144B99F3", Nid = 0x144B99F3, Library = "SceNpCommonPs4")] void Ps4_144B99F3(uint p1) { U32(p1 + 0x8, 0); }
    [Hle("NID_EF60709C", Nid = 0xEF60709C, Library = "SceNpCommonPs4")] void Ps4_EF60709C(uint p1) { GuestMemory.Fill(p1 + 0x8, (byte)0x0, 0x108); }
}
