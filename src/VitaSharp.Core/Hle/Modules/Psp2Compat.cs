using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// ScePsp2Compat (vs0:data/external/webcore/ScePsp2Compat.suprx 3.74, the POSIX layer and support libraries WebKit
/// runs on): the exports that are thin enough to state without the module's own state, read in the module: member
/// accessors of its objects, constants (the default view size 960x544 and its 512 variant as float bits), pthread
/// helpers that are lightweight kernel objects (mutexes and condition variables named "SceWebKitPosixPthread"),
/// usleep(1000), thread/process ids, the safe-memory calls and the allocator entry points it re-exports. The exports
/// have no names (NIDs only). Firmware fallback only: the browser loads the real module.
/// </summary>
public sealed unsafe partial class Psp2Compat : HleModule
{
    const string Lib = "ScePsp2Compat";
    public override bool FirmwareFallbackOnly => true;

    static uint R32(uint a) => GuestMemory.R32(a);
    static void W32(uint a, uint v) => GuestMemory.W32(a, v);
    static byte R8(uint a) => GuestMemory.Read<byte>(a);
    static void W8(uint a, byte v) => GuestMemory.Write(a, v);

    const string PthreadName = "SceWebKitPosixPthread";
    ThreadMgr? _threads;
    ThreadMgr T => _threads ??= Emu.Hle.Modules.OfType<ThreadMgr>().First();

    /// <summary>Runs the HLE's version of another library's function on the current registers.</summary>
    void Fwd(HleContext c, uint nid)
    {
        if (Emu.Hle.ImplOf(nid) is { } f) f(c);
        else c.Return(SceError.ERROR);
    }

    // ------------------------------------------------------------------ member accessors

    [Hle("NID_E0CAF974", Nid = 0xE0CAF974, Library = Lib)] [Hle("NID_E0A5B0F6", Nid = 0xE0A5B0F6, Library = Lib)]
    [Hle("NID_7EB9A7A1", Nid = 0x7EB9A7A1, Library = Lib)] [Hle("NID_C4D5D767", Nid = 0xC4D5D767, Library = Lib)]
    [Hle("NID_F4B43968", Nid = 0xF4B43968, Library = Lib)] [Hle("NID_54B2CA5B", Nid = 0x54B2CA5B, Library = Lib)]
    [Hle("NID_001E09AF", Nid = 0x001E09AF, Library = Lib)]
    uint Get04(uint o) => R32(o + 4);

    [Hle("NID_9D865FA3", Nid = 0x9D865FA3, Library = Lib)] [Hle("NID_3962D944", Nid = 0x3962D944, Library = Lib)]
    uint Get08(uint o) => R32(o + 8);
    [Hle("NID_D4A8FF9E", Nid = 0xD4A8FF9E, Library = Lib)] uint Get0C(uint o) => R32(o + 0x0C);
    [Hle("NID_210B7D23", Nid = 0x210B7D23, Library = Lib)] uint Get10(uint o) => R32(o + 0x10);
    [Hle("NID_08BB7554", Nid = 0x08BB7554, Library = Lib)] uint Get14(uint o) => R32(o + 0x14);
    [Hle("NID_4FF054B1", Nid = 0x4FF054B1, Library = Lib)] uint Get20(uint o) => R32(o + 0x20);
    [Hle("NID_783317C7", Nid = 0x783317C7, Library = Lib)] uint Get44(uint o) => R32(o + 0x44);
    [Hle("NID_0833BFAF", Nid = 0x0833BFAF, Library = Lib)] uint Get48(uint o) => R32(o + 0x48);
    [Hle("NID_E459E09D", Nid = 0xE459E09D, Library = Lib)] uint Get64(uint o) => R32(o + 0x64);
    [Hle("NID_29B2E2F1", Nid = 0x29B2E2F1, Library = Lib)] uint GetE4(uint o) => R32(o + 0xE4);
    [Hle("NID_E67CE5C3", Nid = 0xE67CE5C3, Library = Lib)] uint Get320(uint o) => R32(o + 0x320);
    [Hle("NID_86159AA8", Nid = 0x86159AA8, Library = Lib)] ulong Get20Wide(uint o) => GuestMemory.Read<ulong>(o + 0x20);

    [Hle("NID_B505E2B8", Nid = 0xB505E2B8, Library = Lib)] uint GetByte08(uint o) => R8(o + 8);
    [Hle("NID_D1C4289B", Nid = 0xD1C4289B, Library = Lib)] uint GetByte09(uint o) => R8(o + 9);
    [Hle("NID_E11BB380", Nid = 0xE11BB380, Library = Lib)] uint GetByte1E(uint o) => R8(o + 0x1E);
    [Hle("NID_624A1907", Nid = 0x624A1907, Library = Lib)] uint GetByte34(uint o) => R8(o + 0x34);

    [Hle("NID_8BE94B17", Nid = 0x8BE94B17, Library = Lib)] [Hle("NID_DFD64AE2", Nid = 0xDFD64AE2, Library = Lib)]
    void Set00(uint o, uint v) => W32(o, v);
    /// <summary>The setter that answers 0.</summary>
    [Hle("NID_DEE2F929", Nid = 0xDEE2F929, Library = Lib)] int Set08(uint o, uint v) { W32(o + 8, v); return 0; }
    [Hle("NID_E9BE7976", Nid = 0xE9BE7976, Library = Lib)] void Set9C(uint o, uint v) => W32(o + 0x9C, v);
    [Hle("NID_971019CC", Nid = 0x971019CC, Library = Lib)] void SetF4(uint o, uint v) => W32(o + 0xF4, v);
    [Hle("NID_3484035A", Nid = 0x3484035A, Library = Lib)] void SetByte23(uint o, uint v) => W8(o + 0x23, (byte)v);

    [Hle("NID_3DEE3222", Nid = 0x3DEE3222, Library = Lib)] void Flag09(uint o) => W8(o + 9, 1);
    [Hle("NID_F18C2B60", Nid = 0xF18C2B60, Library = Lib)] void Flag2C(uint o) => W8(o + 0x2C, 1);
    [Hle("NID_D2D56C5E", Nid = 0xD2D56C5E, Library = Lib)] void FlagE0(uint o) => W32(o + 0xE0, 1);

    /// <summary>(T *o): ++o-&gt;+0x80-&gt;+0x3C (a reference count), 0.</summary>
    [Hle("NID_EA651C7A", Nid = 0xEA651C7A, Library = Lib)]
    int AddRef80(uint o) { uint p = R32(o + 0x80); W32(p + 0x3C, R32(p + 0x3C) + 1); return 0; }

    // ------------------------------------------------------------------ constants and comparisons

    [Hle("NID_6163181E", Nid = 0x6163181E, Library = Lib)] [Hle("NID_C88EF069", Nid = 0xC88EF069, Library = Lib)]
    uint Zero() => 0;
    /// <summary>960.0f, the screen width.</summary>
    [Hle("NID_56AEA165", Nid = 0x56AEA165, Library = Lib)] [Hle("NID_A150300C", Nid = 0xA150300C, Library = Lib)]
    uint Width() => 0x44700000;
    /// <summary>544.0f, the screen height.</summary>
    [Hle("NID_E0D800D1", Nid = 0xE0D800D1, Library = Lib)] uint Height() => 0x44080000;
    /// <summary>512.0f.</summary>
    [Hle("NID_7D92D628", Nid = 0x7D92D628, Library = Lib)] uint Size512() => 0x44000000;

    /// <summary>pthread_equal.</summary>
    [Hle("NID_C74F2F8E", Nid = 0xC74F2F8E, Library = Lib)] bool Equal(uint a, uint b) => a == b;

    /// <summary>(T *o, const double *v): *v &gt;= 0.</summary>
    [Hle("NID_CF6DA2BF", Nid = 0xCF6DA2BF, Library = Lib)] bool NonNegative(uint o, uint v) => GuestMemory.Read<double>(v) >= 0;

    /// <summary>(int id, char *out): the "SceManxMemoryPool%x" name of a pool (32 bytes), answers out.</summary>
    [Hle("NID_F0D7581A", Nid = 0xF0D7581A, Library = Lib)]
    uint PoolName(uint id, uint outp) { GuestMemory.WriteCString(outp, $"SceManxMemoryPool{id:x}", 0x20); return outp; }

    // ------------------------------------------------------------------ pthread over lightweight kernel objects

    /// <summary>(SceKernelLwMutexWork *m): sceKernelCreateLwMutex(m, "SceWebKitPosixPthread", recursive, 0, NULL).</summary>
    [Hle("NID_5B55E8F8", Nid = 0x5B55E8F8, Library = Lib)]
    void MutexInit(uint m) => T.sceKernelCreateLwMutex(m, PthreadName, 0x2000, 0, 0);

    [Hle("NID_9BAEB8BC", Nid = 0x9BAEB8BC, Library = Lib)]
    int MutexUnlock(uint m) { T.sceKernelUnlockLwMutex(m, 1); return 0; }

    [Hle("NID_51DD290C", Nid = 0x51DD290C, Library = Lib)] int MutexDestroy(uint m) { T.sceKernelDeleteLwMutex(m); return 0; }

    /// <summary>(SceKernelLwCondWork *c, SceKernelLwMutexWork *m): sceKernelCreateLwCond(c, "SceWebKitPosixPthread", 0, m, NULL).</summary>
    [Hle("NID_CE37A53C", Nid = 0xCE37A53C, Library = Lib)]
    void CondInit(uint cond, uint m) => T.sceKernelCreateLwCond(cond, PthreadName, 0, m, 0);

    [Hle("NID_CED4C61B", Nid = 0xCED4C61B, Library = Lib)] void CondDestroy(HleContext c) => Fwd(c, 0x721F6CB3);
    [Hle("NID_54F5F245", Nid = 0x54F5F245, Library = Lib)] void CondSignal(HleContext c) => Fwd(c, 0x3AC63B9A);
    [Hle("NID_B99DDCE0", Nid = 0xB99DDCE0, Library = Lib)] void CondBroadcast(HleContext c) => Fwd(c, 0xE5241A0C);
    [Hle("NID_49784A13", Nid = 0x49784A13, Library = Lib)] void CondWait(uint cond) => T.sceKernelWaitLwCond(cond, new Ptr<uint>(0));

    /// <summary>(SceUID *lock): sceKernelDeleteRWLock(*lock).</summary>
    [Hle("NID_1AC7666C", Nid = 0x1AC7666C, Library = Lib)] void RwLockDestroy(HleContext c) { c.Cpu->R[0] = R32(c.Cpu->R[0]); Fwd(c, 0xE73649CA); }

    /// <summary>(SceUID thid, int *status): sceKernelWaitThreadEnd(thid, status, NULL).</summary>
    [Hle("NID_70619FAB", Nid = 0x70619FAB, Library = Lib)] void Join(HleContext c) { c.Cpu->R[2] = 0; Fwd(c, 0xDDB395A9); }

    /// <summary>(T *o): sceKernelSetEvent(o-&gt;+0x38, 0x1000000, 0).</summary>
    [Hle("NID_5B3DCA2C", Nid = 0x5B3DCA2C, Library = Lib)]
    void SetEvent38(HleContext c) { c.Cpu->R[0] = R32(c.Cpu->R[0] + 0x38); c.Cpu->R[1] = 0x1000000; c.Cpu->R[2] = 0; c.Cpu->R[3] = 0; Fwd(c, 0x324218CD); }

    /// <summary>usleep(1000).</summary>
    [Hle("NID_A2D8700D", Nid = 0xA2D8700D, Library = Lib)] void Sleep1ms(HleContext c) { c.Cpu->R[0] = 1000; Fwd(c, 0x4B675D05); }

    [Hle("NID_928ECCD7", Nid = 0x928ECCD7, Library = Lib)] void ProcessId(HleContext c) => Fwd(c, 0x9DCB4B7A);
    [Hle("NID_1DB7E7CD", Nid = 0x1DB7E7CD, Library = Lib)] void ThreadId(HleContext c) => Fwd(c, 0x0FB972F9);
    [Hle("NID_046C84C1", Nid = 0x046C84C1, Library = Lib)] void ExitDeleteThread(HleContext c) => Fwd(c, 0x1D17DECF);
    [Hle("NID_4A90C3A8", Nid = 0x4A90C3A8, Library = Lib)] void ProcessTimeWide(HleContext c) => Fwd(c, 0xB110C123);

    // ------------------------------------------------------------------ other libraries' entry points it re-exports

    [Hle("NID_B6795643", Nid = 0xB6795643, Library = Lib)] void SaveSafeMemory(HleContext c) => Fwd(c, 0xD366AA44);
    [Hle("NID_44888F5E", Nid = 0x44888F5E, Library = Lib)] void LoadSafeMemory(HleContext c) => Fwd(c, 0xFAF3DAAA);
    [Hle("NID_A98DBFDA", Nid = 0xA98DBFDA, Library = Lib)] void Malloc(HleContext c) => Fwd(c, 0x775A0CB2);
    [Hle("NID_73DDFB32", Nid = 0x73DDFB32, Library = Lib)] void Free(HleContext c) => Fwd(c, 0x5B9BB802);
    [Hle("NID_B5C06BFA", Nid = 0xB5C06BFA, Library = Lib)] void Delete(HleContext c) => Fwd(c, 0x72293931);
    [Hle("NID_ADB1FDA4", Nid = 0xADB1FDA4, Library = Lib)] void NetCtlCheckCallback(HleContext c) => Fwd(c, 0xDFFC3ED4);
}
