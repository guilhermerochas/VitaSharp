using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// dbutil.suprx 3.74 (SceDbutil, the system software's database helper over the vsh SQLite), the exports whose body
/// is one statement, read in the decompiled module: two member getters and forwards of the connection's sqlite3
/// handle (+0xC) or of a path to the SQLite/AppMgr export that does the work. The system programs load the real
/// module; these serve a process that does not.
/// </summary>
public sealed partial class Dbutil : HleModule
{
    public override bool FirmwareFallbackOnly => true;
    const string Lib = "SceDbutil";
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

    [Hle("NID_64649195", Nid = 0x64649195, Library = Lib)] void Dbu_64649195(HleContext c) { uint p1 = c.Gpr(0); uint p2 = c.Gpr(1); uint p3 = c.Gpr(2); uint p4 = c.Gpr(3); Fwd(c, 0x0E8D27E8, p2, p3, p4); }
    [Hle("NID_7C8652D9", Nid = 0x7C8652D9, Library = Lib)] uint Dbu_7C8652D9(uint p1) => U32(p1 + 0x4);
    [Hle("NID_9B5DC5DE", Nid = 0x9B5DC5DE, Library = Lib)] bool Dbu_9B5DC5DE(uint p1) => U32(p1 + 0x48) != 0x0;
    [Hle("NID_FDA9BC8A", Nid = 0xFDA9BC8A, Library = Lib)] void Dbu_FDA9BC8A(HleContext c) { uint p1 = c.Gpr(0); Fwd(c, 0x26C40C45, U32(p1 + 0xc)); }
    [Hle("NID_1E311E98", Nid = 0x1E311E98, Library = Lib)] void Dbu_1E311E98(HleContext c) { uint p1 = c.Gpr(0); Fwd(c, 0xD666D46D, U32(p1 + 0xc)); }
    [Hle("NID_9725DEF8", Nid = 0x9725DEF8, Library = Lib)] void Dbu_9725DEF8(HleContext c) { uint p1 = c.Gpr(0); Fwd(c, 0xDF4DFF2D, U32(p1 + 0xc)); }
}
