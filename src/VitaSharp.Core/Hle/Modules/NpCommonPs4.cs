using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// np_common_ps4.suprx (FW 3.74, the PS4-style NP layer of the shell and np_webapi: "npext" heaps, locks, atomics,
/// time, strings, allocators and the PSN token helpers), the exports that stand on their own, written from the module's
/// behaviour (NIDs without public names). The shell and np_webapi load the real module, so these only serve a process
/// that does not: the library never takes the module over from the firmware.
/// An npext allocator is {void *(*alloc)(size, ctx), ..., void (*free)(ptr, ctx) at +8, ctx at +0xC}; a heap is a
/// pointer to a SceClib mspace.
/// </summary>
public sealed partial class NpCommonPs4 : HleModule
{
    public override bool FirmwareFallbackOnly => true;

    const int ERR_ALREADY_SET = unchecked((int)0x80559E03), ERR_TOKEN_TOO_LONG = unchecked((int)0x80559E0D);

    Clib? _clib;
    ThreadMgr? _tm;
    Rtc? _rtc;
    Clib C => _clib ??= Emu.Hle.Modules.OfType<Clib>().First();
    ThreadMgr Tm => _tm ??= Emu.Hle.Modules.OfType<ThreadMgr>().First();
    Rtc R => _rtc ??= Emu.Hle.Modules.OfType<Rtc>().First();

    static uint CallGuest(uint fn, params ReadOnlySpan<uint> args) => GuestThread.Current!.CallGuest(fn, args);

    // ---- the library's random numbers (an LCG over a module-wide seed, initially 1)

    uint _seed = 1;

    /// <summary>rand(): seed = seed * 1103515245 + 12345; (seed &amp; 0x7FFFFFFF) &gt;&gt; 16.</summary>
    [Hle("NID_00F74689", Nid = 0x00F74689, Library = "SceNpCommonPs4")]
    uint NpRand() { _seed = _seed * 0x41C64E6D + 0x3039; return (_seed & 0x7FFFFFFF) >> 16; }

    /// <summary>srand(seed).</summary>
    [Hle("NID_61DAB7E5", Nid = 0x61DAB7E5, Library = "SceNpCommonPs4")]
    void NpSrand(uint seed) => _seed = seed;

    // ---- atomics: fetch-and-add / fetch-and-sub of 1, returning the old value

    [Hle("NID_741F88FE", Nid = 0x741F88FE, Library = "SceNpCommonPs4")]
    int NpAtomicIncrement(uint p) { lock (_seedLock) { int v = (int)GuestMemory.R32(p); GuestMemory.W32(p, (uint)(v + 1)); return v; } }

    [Hle("NID_7B512A98", Nid = 0x7B512A98, Library = "SceNpCommonPs4")]
    int NpAtomicDecrement(uint p) { lock (_seedLock) { int v = (int)GuestMemory.R32(p); GuestMemory.W32(p, (uint)(v - 1)); return v; } }

    readonly object _seedLock = new();

    /// <summary>(RefCounted *obj): drops a reference (+4); the last one calls the object's virtual destructor (slot 1).</summary>
    [Hle("NID_93C97239", Nid = 0x93C97239, Library = "SceNpCommonPs4")]
    int NpRelease(uint obj)
    {
        int n = NpAtomicDecrement(obj + 4) - 1;
        if (n == 0 && obj != 0) CallGuest(GuestMemory.R32(GuestMemory.R32(obj) + 4), obj);
        return n;
    }

    // ---- small accessors of npext objects

    /// <summary>A buffer view {.., +0x10 begin, +0x14 end}: its length.</summary>
    [Hle("NID_2A607C9A", Nid = 0x2A607C9A, Library = "SceNpCommonPs4")] int NpBufferLength(uint o) => (int)(GuestMemory.R32(o + 0x14) - GuestMemory.R32(o + 0x10));
    [Hle("NID_2CE76BE5", Nid = 0x2CE76BE5, Library = "SceNpCommonPs4")] uint NpGetField4(uint o) => GuestMemory.R32(o + 4);
    [Hle("NID_80D16974", Nid = 0x80D16974, Library = "SceNpCommonPs4")] uint NpFieldAddress8(uint o) => o + 8;
    /// <summary>A queue {.., +0x10 count}: empty / not empty.</summary>
    [Hle("NID_B599A560", Nid = 0xB599A560, Library = "SceNpCommonPs4")] int NpIsEmpty(uint o) => GuestMemory.R32(o + 0x10) == 0 ? 1 : 0;
    [Hle("NID_DEAEDDB3", Nid = 0xDEAEDDB3, Library = "SceNpCommonPs4")] int NpIsNotEmpty(uint o) => GuestMemory.R32(o + 0x10) != 0 ? 1 : 0;
    /// <summary>The same test on the member at +4.</summary>
    [Hle("NID_DC7BB7F1", Nid = 0xDC7BB7F1, Library = "SceNpCommonPs4")] int NpMemberIsEmpty(uint o) => NpIsEmpty(o + 4);
    [Hle("NID_C78F5DF5", Nid = 0xC78F5DF5, Library = "SceNpCommonPs4")] int NpGetFlag8(uint o) => GuestMemory.Read<byte>(o + 8);

    /// <summary>(obj, value): sets +4 once (flags +0x20/+0x21); a second time 0x80559E03.</summary>
    [Hle("NID_70073BD1", Nid = 0x70073BD1, Library = "SceNpCommonPs4")]
    int NpSetOnce(uint o, uint v)
    {
        if (GuestMemory.Read<byte>(o + 0x20) != 0) return ERR_ALREADY_SET;
        GuestMemory.W32(o + 4, v);
        GuestMemory.Write<byte>(o + 0x21, 1);
        GuestMemory.Write<byte>(o + 0x20, 1);
        return 0;
    }

    // ---- time

    /// <summary>(SceRtcTick *out): the current tick; zeros when it cannot be read.</summary>
    [Hle("NID_477BD360", Nid = 0x477BD360, Library = "SceNpCommonPs4")]
    int NpGetCurrentTick(uint outp) => R.sceRtcGetCurrentTick(new Ptr<ulong>(outp)) is var r and < 0 ? Zero8(outp, r) : 0;

    /// <summary>(SceRtcTick *out): the current network tick; zeros when it cannot be read.</summary>
    [Hle("NID_5B904477", Nid = 0x5B904477, Library = "SceNpCommonPs4")]
    int NpGetCurrentNetworkTick(uint outp) => R.sceRtcGetCurrentNetworkTick(new Ptr<ulong>(outp)) is var r and < 0 ? Zero8(outp, r) : 0;

    static int Zero8(uint p, int r) { GuestMemory.Write(p, 0ul); return r; }

    /// <summary>(uint64 *out): sceKernelGetSystemTimeWide.</summary>
    [Hle("NID_F56B0EAB", Nid = 0xF56B0EAB, Library = "SceNpCommonPs4")]
    int NpGetSystemTimeWide(uint outp) { GuestMemory.Write(outp, Emu.Kernel.ProcessTimeUs); return 0; }

    /// <summary>(uint32 *out): the low word of the system time.</summary>
    [Hle("NID_A25DA29A", Nid = 0xA25DA29A, Library = "SceNpCommonPs4")]
    int NpGetSystemTimeLow(uint outp) { GuestMemory.W32(outp, (uint)Emu.Kernel.ProcessTimeUs); return 0; }

    // ---- locks over SceKernelLwMutex

    [Hle("NID_F5840D6E", Nid = 0xF5840D6E, Library = "SceNpCommonPs4")]
    int NpLock(uint m) => Tm.sceKernelLockLwMutex(m, 1, new Ptr<uint>(0)) is var r and < 0 ? r : 0;

    [Hle("NID_4A1C1C3C", Nid = 0x4A1C1C3C, Library = "SceNpCommonPs4")]
    int NpUnlock(uint m) => Tm.sceKernelUnlockLwMutex(m, 1) is var r and < 0 ? r : 0;

    [Hle("NID_29895EBB", Nid = 0x29895EBB, Library = "SceNpCommonPs4")]
    int NpDeleteLock(uint m) => Tm.sceKernelDeleteLwMutex(m);

    // ---- heaps (a pointer to a SceClib mspace) and allocators

    [Hle("NID_EB6E37A8", Nid = 0xEB6E37A8, Library = "SceNpCommonPs4")]
    uint NpHeapAlloc(uint heap, uint size) => C.sceClibMspaceMalloc(GuestMemory.R32(heap), size);

    [Hle("NID_1514F370", Nid = 0x1514F370, Library = "SceNpCommonPs4")]
    void NpHeapFree(uint heap, uint p) { if (p != 0) C.sceClibMspaceFree(GuestMemory.R32(heap), p); }

    [Hle("NID_4117508A", Nid = 0x4117508A, Library = "SceNpCommonPs4")]
    uint NpHeapRealloc(uint heap, uint p, uint size) => C.sceClibMspaceRealloc(GuestMemory.R32(heap), p, size);

    /// <summary>(heap, const char *s): a copy of the string in the heap (NULL when it is full).</summary>
    [Hle("NID_EBF8CB79", Nid = 0xEBF8CB79, Library = "SceNpCommonPs4")]
    uint NpHeapStrdup(uint heap, uint s) => HeapCopy(heap, s, uint.MaxValue);

    /// <summary>(heap, const char *s, uint max): a copy of at most max characters.</summary>
    [Hle("NID_8CBF4614", Nid = 0x8CBF4614, Library = "SceNpCommonPs4")]
    uint NpHeapStrndup(uint heap, uint s, uint max) => HeapCopy(heap, s, max);

    uint HeapCopy(uint heap, uint s, uint max)
    {
        uint len = Strnlen(s, max);
        uint p = NpHeapAlloc(heap, len + 1);
        if (p == 0) return 0;
        GuestMemory.Copy(p, s, len);
        GuestMemory.Write<byte>(p + len, 0);
        return p;
    }

    static uint Strnlen(uint s, uint max)
    {
        uint n = 0;
        while (n < max && GuestMemory.Read<byte>(s + n) != 0) n++;
        return n;
    }

    /// <summary>(allocator, const char *s, uint max): a copy through the allocator (NULL when it fails).</summary>
    [Hle("NID_83496021", Nid = 0x83496021, Library = "SceNpCommonPs4")]
    uint NpAllocStrndup(uint alloc, uint s, uint max)
    {
        uint len = Strnlen(s, max);
        uint p = CallGuest(GuestMemory.R32(alloc), len + 1, GuestMemory.R32(alloc + 0xC));
        if (p == 0) return 0;
        GuestMemory.Copy(p, s, len);
        GuestMemory.Write<byte>(p + len, 0);
        return p;
    }

    /// <summary>(allocator, ptr): the allocator's first function with the pointer and its context.</summary>
    [Hle("NID_299ADFC3", Nid = 0x299ADFC3, Library = "SceNpCommonPs4")]
    [Hle("NID_AA980113", Nid = 0xAA980113, Library = "SceNpCommonPs4")]
    void NpAllocCall0(uint alloc, uint p) => CallGuest(GuestMemory.R32(alloc), p, GuestMemory.R32(alloc + 0xC));

    /// <summary>(allocator, ptr): the allocator's free (+8) with the pointer and its context.</summary>
    [Hle("NID_38CC9AF5", Nid = 0x38CC9AF5, Library = "SceNpCommonPs4")]
    [Hle("NID_6B428829", Nid = 0x6B428829, Library = "SceNpCommonPs4")]
    void NpAllocFree(uint alloc, uint p) => CallGuest(GuestMemory.R32(alloc + 8), p, GuestMemory.R32(alloc + 0xC));

    /// <summary>(uint size, allocator): a block with a 16-byte header that remembers the allocator; returns the data.</summary>
    [Hle("NID_0C922423", Nid = 0x0C922423, Library = "SceNpCommonPs4")]
    uint NpAllocWithHeader(uint size, uint alloc)
    {
        uint p = CallGuest(GuestMemory.R32(alloc), size + 0x10, GuestMemory.R32(alloc + 0xC));
        if (p == 0) return 0;
        GuestMemory.W32(p, alloc);
        return p + 0x10;
    }

    /// <summary>(data): frees a block of NID_0C922423 through the allocator in its header.</summary>
    [Hle("NID_BB5282C8", Nid = 0xBB5282C8, Library = "SceNpCommonPs4")]
    void NpFreeWithHeader(uint data)
    {
        if (data == 0) return;
        uint alloc = GuestMemory.R32(data - 0x10);
        CallGuest(GuestMemory.R32(alloc + 8), data - 0x10, GuestMemory.R32(alloc + 0xC));
    }

    // ---- PSN helpers

    /// <summary>(const token *t, char *buf, uint size): "Bearer " + the access token (t + 8); 0x80559E0D when it does
    /// not fit.</summary>
    [Hle("NID_D95658C6", Nid = 0xD95658C6, Library = "SceNpCommonPs4")]
    int NpBearerHeader(uint token, uint buf, uint size)
    {
        if (size == 0) return ERR_TOKEN_TOO_LONG;
        GuestMemory.Fill(buf, 0, size);
        string v = "Bearer " + GuestMemory.ReadCString(token + 8, 0x1000);
        if ("Bearer ".Length >= size || v.Length >= size) { GuestMemory.WriteCString(buf, v[..(int)Math.Min(v.Length, size - 1)], (int)size); return ERR_TOKEN_TOO_LONG; }
        GuestMemory.WriteCString(buf, v, (int)size);
        return 0;
    }

    /// <summary>(const SceNpOnlineId-holder *a, *b): their online ids (+4) are equal (SceNpCommon's comparison).</summary>
    [Hle("NID_21B2C0B7", Nid = 0x21B2C0B7, Library = "SceNpCommonPs4")]
    int NpSameOnlineId(uint a, uint b) => SameId(a + 4, b + 4);

    /// <summary>(const holder *a, const SceNpOnlineId *id): the holder's online id (+4) equals id.</summary>
    [Hle("NID_21F316D6", Nid = 0x21F316D6, Library = "SceNpCommonPs4")]
    int NpHasOnlineId(uint a, uint id) => SameId(a + 4, id);

    static int SameId(uint x, uint y)
    {
        if (x == 0 || y == 0) return 0;
        for (uint i = 0; i < 0x10; i++)
        {
            byte p = GuestMemory.Read<byte>(x + i), q = GuestMemory.Read<byte>(y + i);
            if (p != q) return 0;
            if (p == 0) break;
        }
        return 1;
    }
}
