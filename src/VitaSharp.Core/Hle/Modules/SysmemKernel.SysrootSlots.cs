using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// The rest of the FW 3.74 sysroot's handler slots (sysmem.skprx, decompiled, study only), in the shapes of
/// <see cref="SysmemKernel"/>'s Sysroot part: the thread manager's handlers (a 0x30-byte structure copied into
/// +0x2D4..+0x358), the module manager's table (+0x344, 0x18 bytes), the process manager's tables (+0x360 entries
/// +0x04..+0x3C, +0x364 0x20 bytes), the boot's start functions (+0x2D8, +0x328), the licence manager (+0x368), the
/// security services (+0x36C, +0x370, +0x38C, +0x390, +0x394), the DRM services' table (+0x3B0, 0x40 bytes), the
/// coredump triggers (+0x37C, +0x380, +0x3E4..+0x3F0), the NID name lookup (+0x3A4) and the table at +0x400.
/// </summary>
public sealed partial class SysmemKernel
{
    const int NotFound = unchecked((int)0x80010058);

    /// <summary>The pids the kernel's heap serves directly (the kernel 0x10005, 0x10015 and 0x10013).</summary>
    static bool IsKernelHeapPid(uint pid) => pid == 0x10013 || (pid & ~0x10u) == 0x10005;

    /// <summary>NID_26458702(handlers): the thread manager's 0x30-byte structure (+0 its size): its words +4..+0x2C
    /// go to +0x330, +0x334, +0x350, +0x358, +0x338, +0x33C, +0x340, +0x348, +0x34C, +0x2D4 and +0x354; another size
    /// 0x80020005.</summary>
    [Hle(Nid = 0x26458702, Library = "SceSysrootForKernel")]
    int NID_26458702(uint handlers)
    {
        if (handlers == 0 || GuestMemory.R32(handlers) != 0x30)
        {
            return SceError.INVALID_ARGUMENT;
        }

        ReadOnlySpan<uint> offsets = [0x330, 0x334, 0x350, 0x358, 0x338, 0x33C, 0x340, 0x348, 0x34C, 0x2D4, 0x354];
        for (int word = 0; word < offsets.Length; word++)
        {
            SetSysrootWord(offsets[word], GuestMemory.R32(handlers + 4 + (uint)word * 4));
        }

        return 0;
    }

    /// <summary>NID_D441DC34: calls the handler at +0x330; without one, 0xffffffff.</summary>
    [Hle(Nid = 0xD441DC34, Library = "SceSysrootForKernel")]
    void NID_D441DC34(HleContext context) => CallSysrootHandler(context, 0x330, -1);

    /// <summary>ksceKernelSysrootIsUserModeThread: calls the handler at +0x334; without one, nothing.</summary>
    [Hle(Nid = 0x7FC7A163, Library = "SceSysrootForKernel")]
    void ksceKernelSysrootIsUserModeThread(HleContext context) => CallSysrootHandler(context, 0x334, null);

    /// <summary>NID_7385CADE: calls the handler at +0x338; without one, 0x10005.</summary>
    [Hle(Nid = 0x7385CADE, Library = "SceSysrootForKernel")]
    void NID_7385CADE(HleContext context) => CallSysrootHandler(context, 0x338, 0x10005);

    /// <summary>NID_9991B1AF: calls the handler at +0x33C; without one, nothing.</summary>
    [Hle(Nid = 0x9991B1AF, Library = "SceSysrootForKernel")]
    void NID_9991B1AF(HleContext context) => CallSysrootHandler(context, 0x33C, null);

    /// <summary>ksceKernelSysrootGetCurrentUIDEntryHeapCB: calls the handler at +0x348; without one, nothing.</summary>
    [Hle(Nid = 0xB4C24588, Library = "SceSysrootForKernel")]
    void ksceKernelSysrootGetCurrentUIDEntryHeapCB(HleContext context) => CallSysrootHandler(context, 0x348, null);

    /// <summary>NID_DADFF828: calls the handler at +0x34C.</summary>
    [Hle(Nid = 0xDADFF828, Library = "SceSysrootForKernel")]
    void NID_DADFF828(HleContext context) => CallSysrootHandler(context, 0x34C, null);

    /// <summary>ksceKernelSysrootGetThreadAccessLevel: calls the handler at +0x350; without one, nothing.</summary>
    [Hle(Nid = 0x20009397, Library = "SceSysrootForKernel")]
    void ksceKernelSysrootGetThreadAccessLevel(HleContext context) => CallSysrootHandler(context, 0x350, null);

    /// <summary>NID_9139E22B: calls the handler at +0x354.</summary>
    [Hle(Nid = 0x9139E22B, Library = "SceSysrootForKernel")]
    void NID_9139E22B(HleContext context) => CallSysrootHandler(context, 0x354, null);

    /// <summary>NID_73601453: calls the handler at +0x358.</summary>
    [Hle(Nid = 0x73601453, Library = "SceSysrootForKernel")]
    void NID_73601453(HleContext context) => CallSysrootHandler(context, 0x358, null);

    /// <summary>NID_0EB5D7CD: calls the handler at +0x2D4 (the firmware panics on a negative result).</summary>
    [Hle(Nid = 0x0EB5D7CD, Library = "SceSysrootForKernel")]
    void NID_0EB5D7CD(HleContext context) => CallSysrootHandler(context, 0x2D4, null);

    /// <summary>ksceKernelSysrootIofilemgrStart: calls the I/O file manager's start function (+0x2D8).</summary>
    [Hle(Nid = 0xF6A6D205, Library = "SceSysrootForKernel")]
    void ksceKernelSysrootIofilemgrStart(HleContext context) => CallSysrootHandler(context, 0x2D8, null);

    /// <summary>NID_21F5790B: sets the handler word at +0x3D8.</summary>
    [Hle(Nid = 0x21F5790B, Library = "SceSysrootForKernel")]
    void NID_21F5790B(HleContext context) => SetSysrootHandler(context, 0x3D8);

    /// <summary>NID_0DF574A9: calls the handler at +0x3D8; without one, nothing.</summary>
    [Hle(Nid = 0x0DF574A9, Library = "SceSysrootForKernel")]
    void NID_0DF574A9(HleContext context) => CallSysrootHandler(context, 0x3D8, null);

    /// <summary>ksceKernelSysrootAppMgrSpawnProcess: calls the handler at +0x328.</summary>
    [Hle(Nid = 0x3ACACD22, Library = "SceSysrootForKernel")]
    [Hle(Nid = 0x3ACACD22, Library = "SceSysrootForDriver")]
    void ksceKernelSysrootAppMgrSpawnProcess(HleContext context) => CallSysrootHandler(context, 0x328, null);

    /// <summary>ksceKernelSysrootCheckRemapCodeForUser: calls entry +0 of the table at +0x324; without it, 0.</summary>
    [Hle(Nid = 0xF8769E86, Library = "SceSysrootForKernel")]
    void ksceKernelSysrootCheckRemapCodeForUser(HleContext context) => CallSysrootTableEntry(context, 0x324, 0, 0);

    // ------------------------------------------------------------------ the module manager's table (+0x344)

    /// <summary>NID_B171CC2D: registers the module manager's table (+0 its size, 0x18) at +0x344.</summary>
    [Hle(Nid = 0xB171CC2D, Library = "SceSysrootForKernel")]
    void NID_B171CC2D(HleContext context) => RegisterSysrootTable(context, 0x344, 0x18);

    /// <summary>ksceSysrootGetModuleInfoForPid: entry +0x4 of the table at +0x344; without it, 0x80010058.</summary>
    [Hle(Nid = 0xFF9F80FF, Library = "SceSysrootForKernel")]
    void ksceSysrootGetModuleInfoForPid(HleContext context) => CallSysrootTableEntry(context, 0x344, 0x4, NotFound);

    /// <summary>NID_0A12227B: entry +0x8 of the table at +0x344; without it, nothing.</summary>
    [Hle(Nid = 0x0A12227B, Library = "SceSysrootForKernel")]
    void NID_0A12227B(HleContext context) => CallSysrootTableEntry(context, 0x344, 0x8, null);

    /// <summary>NID_1D84C4D4: entry +0xC of the table at +0x344; without it, 0x80010058.</summary>
    [Hle(Nid = 0x1D84C4D4, Library = "SceSysrootForKernel")]
    void NID_1D84C4D4(HleContext context) => CallSysrootTableEntry(context, 0x344, 0xC, NotFound);

    /// <summary>NID_CC893F37: entry +0x10 of the table at +0x344; without it, 0.</summary>
    [Hle(Nid = 0xCC893F37, Library = "SceSysrootForKernel")]
    void NID_CC893F37(HleContext context) => CallSysrootTableEntry(context, 0x344, 0x10, 0);

    /// <summary>NID_491CE8DF: entry +0x14 of the table at +0x344; without it, 0.</summary>
    [Hle(Nid = 0x491CE8DF, Library = "SceSysrootForKernel")]
    void NID_491CE8DF(HleContext context) => CallSysrootTableEntry(context, 0x344, 0x14, 0);

    // ------------------------------------------------------------------ the process manager's tables (+0x360, +0x364)

    /// <summary>NID_5F2CE289: entry +0x4 of the table at +0x360.</summary>
    [Hle(Nid = 0x5F2CE289, Library = "SceSysrootForKernel")]
    void NID_5F2CE289(HleContext context) => CallSysrootTableEntry(context, 0x360, 0x4, null);

    /// <summary>NID_BBFD2E3C: entry +0x8 of the table at +0x360.</summary>
    [Hle(Nid = 0xBBFD2E3C, Library = "SceSysrootForKernel")]
    void NID_BBFD2E3C(HleContext context) => CallSysrootTableEntry(context, 0x360, 0x8, null);

    /// <summary>NID_AD74BEB3: entry +0xC of the table at +0x360.</summary>
    [Hle(Nid = 0xAD74BEB3, Library = "SceSysrootForKernel")]
    void NID_AD74BEB3(HleContext context) => CallSysrootTableEntry(context, 0x360, 0xC, null);

    /// <summary>NID_2F75C1DC: entry +0x10 of the table at +0x360; without it, 0.</summary>
    [Hle(Nid = 0x2F75C1DC, Library = "SceSysrootForKernel")]
    void NID_2F75C1DC(HleContext context) => CallSysrootTableEntry(context, 0x360, 0x10, 0);

    /// <summary>NID_AE2132FD: entry +0x14 of the table at +0x360; without it, 0.</summary>
    [Hle(Nid = 0xAE2132FD, Library = "SceSysrootForKernel")]
    void NID_AE2132FD(HleContext context) => CallSysrootTableEntry(context, 0x360, 0x14, 0);

    /// <summary>ksceKernelSysrootProcessmgrStart2: entry +0x20 of the table at +0x360 (the firmware panics on a
    /// negative result).</summary>
    [Hle(Nid = 0x62E8F511, Library = "SceSysrootForKernel")]
    void ksceKernelSysrootProcessmgrStart2(HleContext context) => CallSysrootTableEntry(context, 0x360, 0x20, null);

    /// <summary>NID_1247A825: entry +0x3C of the table at +0x360.</summary>
    [Hle(Nid = 0x1247A825, Library = "SceSysrootForKernel")]
    void NID_1247A825(HleContext context) => CallSysrootTableEntry(context, 0x360, 0x3C, null);

    /// <summary>ksceKernelAllocHeapMemory2 (0xD351EBC8)(pid, size, opt): the kernel's pids allocate from the kernel's
    /// heap (ksceKernelAllocHeapMemoryWithOpt on heap 0x1000B, given back by NID_7968F091); any other pid asks entry
    /// +0x18 of the process manager's table (+0x360); without it, 0.</summary>
    [Hle(Nid = 0xD351EBC8, Library = "SceSysrootForKernel", ReturnsValue = true)]
    void ksceKernelAllocHeapMemory2(HleContext context)
    {
        if (IsKernelHeapPid(context.Gpr(0)))
        {
            context.Return(ksceKernelAllocThroughEntry(context.Gpr(1)));
            return;
        }

        CallSysrootTableEntry(context, 0x360, 0x18, 0);
    }

    /// <summary>NID_7968F091(pid, ptr): the kernel's pids give the block back to the kernel's heap (ksceKernelFree);
    /// any other pid asks entry +0x1C of the process manager's table; without it, 0.</summary>
    [Hle(Nid = 0x7968F091, Library = "SceSysrootForKernel")]
    void NID_7968F091(HleContext context)
    {
        if (IsKernelHeapPid(context.Gpr(0)))
        {
            context.Return(ksceKernelFreeThroughEntry(context.Gpr(1)));
            return;
        }

        CallSysrootTableEntry(context, 0x360, 0x1C, 0);
    }

    /// <summary>NID_733C243E: registers the process manager's second table (+0 its size, 0x20) at +0x364.</summary>
    [Hle(Nid = 0x733C243E, Library = "SceSysrootForKernel")]
    void NID_733C243E(HleContext context) => RegisterSysrootTable(context, 0x364, 0x20);

    /// <summary>The table at +0x364 when one is registered (the firmware's own fallbacks answer otherwise: VitaSharp's
    /// process manager runs in HLE).</summary>
    uint ProcessTable364 => SysrootWord(0x364);

    /// <summary>ksceKernelSysrootGetPUIDEntryHeap (pid, *heap): entry +0x4 of the table at +0x364; without it, *heap
    /// = word +0x2E0 and 0.</summary>
    [Hle(Nid = 0x88DE85EF, Library = "SceSysrootForKernel")]
    void ksceKernelSysrootGetPUIDEntryHeap(HleContext context)
    {
        if (ProcessTable364 != 0)
        {
            CallSysrootTableEntry(context, 0x364, 0x4, null);
            return;
        }

        GuestMemory.W32(context.Gpr(1), SysrootData(0x2E0));
        context.Return(0);
    }

    /// <summary>NID_7334F1E8: entry +0x8 of the table at +0x364; without it, 0x10007.</summary>
    [Hle(Nid = 0x7334F1E8, Library = "SceSysrootForKernel")]
    void NID_7334F1E8(HleContext context)
    {
        if (ProcessTable364 != 0)
        {
            CallSysrootTableEntry(context, 0x364, 0x8, null);
            return;
        }

        context.Return(0x10007);
    }

    /// <summary>ksceSysrootGetSelfAuthInfo (pid, SceSelfAuthInfo *info): entry +0x10 of the table at +0x364; without
    /// it, the kernel's own auth info (0x90 bytes) and 0.</summary>
    [Hle(Nid = 0x4F0A4066, Library = "SceSysrootForKernel")]
    void ksceSysrootGetSelfAuthInfo(HleContext context)
    {
        if (ProcessTable364 != 0)
        {
            CallSysrootTableEntry(context, 0x364, 0x10, null);
            return;
        }

        KernelSelfAuthInfo.CopyTo(GuestMemory.Span(context.Gpr(1), KernelSelfAuthInfo.Length));
        context.Return(0);
    }

    // ------------------------------------------------------------------ the licence manager and the security services

    /// <summary>ksceSysrootRegisterLicMgrGetLicenseStatus: sets the handler word at +0x368.</summary>
    [Hle(Nid = 0x71DB83A2, Library = "SceSysrootForKernel")]
    [Hle(Nid = 0x71DB83A2, Library = "SceSysrootForDriver")]
    void ksceSysrootRegisterLicMgrGetLicenseStatus(HleContext context) => SetSysrootHandler(context, 0x368);

    /// <summary>NID_377895EB (the licence status): calls the handler at +0x368; without one, 0xffffffff.</summary>
    [Hle(Nid = 0x377895EB, Library = "SceSysrootForKernel")]
    [Hle(Nid = 0x377895EB, Library = "SceSysrootForDriver")]
    void NID_377895EB(HleContext context) => CallSysrootHandler(context, 0x368, -1);

    /// <summary>NID_E25D2FD5: sets the handler word at +0x36C.</summary>
    [Hle(Nid = 0xE25D2FD5, Library = "SceSysrootForDriver")]
    void NID_E25D2FD5(HleContext context) => SetSysrootHandler(context, 0x36C);

    /// <summary>NID_26AA237C: calls the handler at +0x36C; without one, nothing.</summary>
    [Hle(Nid = 0x26AA237C, Library = "SceSysrootForDriver")]
    void NID_26AA237C(HleContext context) => CallSysrootHandler(context, 0x36C, null);

    /// <summary>NID_E2E88E3E: sets the handler word at +0x370.</summary>
    [Hle(Nid = 0xE2E88E3E, Library = "SceSysrootForDriver")]
    void NID_E2E88E3E(HleContext context) => SetSysrootHandler(context, 0x370);

    /// <summary>NID_E2515A08: calls the handler at +0x370; without one, nothing.</summary>
    [Hle(Nid = 0xE2515A08, Library = "SceSysrootForDriver")]
    void NID_E2515A08(HleContext context) => CallSysrootHandler(context, 0x370, null);

    /// <summary>NID_A12C9950: sets the handler word at +0x38C.</summary>
    [Hle(Nid = 0xA12C9950, Library = "SceSysrootForDriver")]
    void NID_A12C9950(HleContext context) => SetSysrootHandler(context, 0x38C);

    /// <summary>NID_56D85EB0: calls the handler at +0x38C; without one, 0.</summary>
    [Hle(Nid = 0x56D85EB0, Library = "SceSysrootForDriver")]
    void NID_56D85EB0(HleContext context) => CallSysrootHandler(context, 0x38C, 0);

    /// <summary>NID_35875119: sets the handler word at +0x390.</summary>
    [Hle(Nid = 0x35875119, Library = "SceSysrootForDriver")]
    void NID_35875119(HleContext context) => SetSysrootHandler(context, 0x390);

    /// <summary>NID_D75D4F37: calls the handler at +0x390; without one, 0x80010002.</summary>
    [Hle(Nid = 0xD75D4F37, Library = "SceSysrootForDriver")]
    void NID_D75D4F37(HleContext context) => CallSysrootHandler(context, 0x390, unchecked((int)0x80010002));

    /// <summary>ksceSysrootUtMgrSetNpTestFlag: sets the handler word at +0x394.</summary>
    [Hle(Nid = 0xEE5D6CE9, Library = "SceSysrootForDriver")]
    void ksceSysrootUtMgrSetNpTestFlag(HleContext context) => SetSysrootHandler(context, 0x394);

    /// <summary>ksceSysrootUtMgrHasNpTestFlag: calls the handler at +0x394; without one, 0x80010002.</summary>
    [Hle(Nid = 0xA43599E9, Library = "SceSysrootForDriver")]
    void ksceSysrootUtMgrHasNpTestFlag(HleContext context) => CallSysrootHandler(context, 0x394, unchecked((int)0x80010002));

    // ------------------------------------------------------------------ the DRM services' table (+0x3B0)

    /// <summary>NID_579CC758: registers the DRM services' table (+0 its size, 0x40) at +0x3B0.</summary>
    [Hle(Nid = 0x579CC758, Library = "SceSysrootForDriver")]
    void NID_579CC758(HleContext context) => RegisterSysrootTable(context, 0x3B0, 0x40);

    /// <summary>NID_9A486846: entry +0x4 of the table at +0x3B0; without it, 0.</summary>
    [Hle(Nid = 0x9A486846, Library = "SceSysrootForDriver")]
    void NID_9A486846(HleContext context) => CallSysrootTableEntry(context, 0x3B0, 0x4, 0);

    /// <summary>NID_9EC02A41: entry +0x8 of the table at +0x3B0; without it, 0.</summary>
    [Hle(Nid = 0x9EC02A41, Library = "SceSysrootForDriver")]
    void NID_9EC02A41(HleContext context) => CallSysrootTableEntry(context, 0x3B0, 0x8, 0);

    /// <summary>NID_217B2871: entry +0xC of the table at +0x3B0; without it, 0.</summary>
    [Hle(Nid = 0x217B2871, Library = "SceSysrootForDriver")]
    void NID_217B2871(HleContext context) => CallSysrootTableEntry(context, 0x3B0, 0xC, 0);

    /// <summary>NID_70A60BBA: entry +0x10 of the table at +0x3B0; without it, 0.</summary>
    [Hle(Nid = 0x70A60BBA, Library = "SceSysrootForDriver")]
    void NID_70A60BBA(HleContext context) => CallSysrootTableEntry(context, 0x3B0, 0x10, 0);

    /// <summary>NID_79458ACB: entry +0x14 of the table at +0x3B0; without it, 0.</summary>
    [Hle(Nid = 0x79458ACB, Library = "SceSysrootForDriver")]
    void NID_79458ACB(HleContext context) => CallSysrootTableEntry(context, 0x3B0, 0x14, 0);

    /// <summary>NID_D03741B8: entry +0x18 of the table at +0x3B0; without it, 0.</summary>
    [Hle(Nid = 0xD03741B8, Library = "SceSysrootForDriver")]
    void NID_D03741B8(HleContext context) => CallSysrootTableEntry(context, 0x3B0, 0x18, 0);

    /// <summary>NID_56C4C949: entry +0x1C of the table at +0x3B0; without it, 0.</summary>
    [Hle(Nid = 0x56C4C949, Library = "SceSysrootForDriver")]
    void NID_56C4C949(HleContext context) => CallSysrootTableEntry(context, 0x3B0, 0x1C, 0);

    /// <summary>NID_7998F5B5: entry +0x20 of the table at +0x3B0; without it, 0.</summary>
    [Hle(Nid = 0x7998F5B5, Library = "SceSysrootForDriver")]
    void NID_7998F5B5(HleContext context) => CallSysrootTableEntry(context, 0x3B0, 0x20, 0);

    /// <summary>NID_0F59B369: entry +0x24 of the table at +0x3B0; without it, 0.</summary>
    [Hle(Nid = 0x0F59B369, Library = "SceSysrootForDriver")]
    void NID_0F59B369(HleContext context) => CallSysrootTableEntry(context, 0x3B0, 0x24, 0);

    /// <summary>NID_97E2FB77: entry +0x28 of the table at +0x3B0; without it, 0.</summary>
    [Hle(Nid = 0x97E2FB77, Library = "SceSysrootForDriver")]
    void NID_97E2FB77(HleContext context) => CallSysrootTableEntry(context, 0x3B0, 0x28, 0);

    /// <summary>NID_F0C91CD3: entry +0x2C of the table at +0x3B0; without it, 0.</summary>
    [Hle(Nid = 0xF0C91CD3, Library = "SceSysrootForDriver")]
    void NID_F0C91CD3(HleContext context) => CallSysrootTableEntry(context, 0x3B0, 0x2C, 0);

    /// <summary>NID_E384DD93: entry +0x30 of the table at +0x3B0; without it, 0.</summary>
    [Hle(Nid = 0xE384DD93, Library = "SceSysrootForDriver")]
    void NID_E384DD93(HleContext context) => CallSysrootTableEntry(context, 0x3B0, 0x30, 0);

    /// <summary>NID_E8EBF179: entry +0x34 of the table at +0x3B0; without it, 0.</summary>
    [Hle(Nid = 0xE8EBF179, Library = "SceSysrootForDriver")]
    void NID_E8EBF179(HleContext context) => CallSysrootTableEntry(context, 0x3B0, 0x34, 0);

    /// <summary>NID_1BA0C6C1: entry +0x38 of the table at +0x3B0; without it, 0.</summary>
    [Hle(Nid = 0x1BA0C6C1, Library = "SceSysrootForDriver")]
    void NID_1BA0C6C1(HleContext context) => CallSysrootTableEntry(context, 0x3B0, 0x38, 0);

    /// <summary>NID_D04C7723: entry +0x3C of the table at +0x3B0; without it, 0.</summary>
    [Hle(Nid = 0xD04C7723, Library = "SceSysrootForDriver")]
    void NID_D04C7723(HleContext context) => CallSysrootTableEntry(context, 0x3B0, 0x3C, 0);

    // ------------------------------------------------------------------ coredump triggers, NID names, +0x400

    /// <summary>ksceKernelSysrootCoredumpTrigger: calls the coredump trigger at +0x37C.</summary>
    [Hle(Nid = 0xCD8CD242, Library = "SceSysrootForDriver")]
    void ksceKernelSysrootCoredumpTrigger(HleContext context) => CallSysrootHandler(context, 0x37C, null);

    /// <summary>NID_80FBC69D: calls the handler at +0x380.</summary>
    [Hle(Nid = 0x80FBC69D, Library = "SceSysrootForDriver")]
    void NID_80FBC69D(HleContext context) => CallSysrootHandler(context, 0x380, null);

    /// <summary>NID_BF82931F: calls the handler at +0x3E4; without one, 0x80010086.</summary>
    [Hle(Nid = 0xBF82931F, Library = "SceSysrootForKernel")]
    void NID_BF82931F(HleContext context) => CallSysrootHandler(context, 0x3E4, unchecked((int)0x80010086));

    /// <summary>NID_6D111FA7: calls the handler at +0x3E8; without one, 0x80010086.</summary>
    [Hle(Nid = 0x6D111FA7, Library = "SceSysrootForKernel")]
    void NID_6D111FA7(HleContext context) => CallSysrootHandler(context, 0x3E8, unchecked((int)0x80010086));

    /// <summary>NID_2A03DFA1: calls the handler at +0x3EC; without one, 0x80010086.</summary>
    [Hle(Nid = 0x2A03DFA1, Library = "SceSysrootForKernel")]
    void NID_2A03DFA1(HleContext context) => CallSysrootHandler(context, 0x3EC, unchecked((int)0x80010086));

    /// <summary>NID_CC119171: sets the handler word at +0x3F0.</summary>
    [Hle(Nid = 0xCC119171, Library = "SceSysrootForKernel")]
    void NID_CC119171(HleContext context) => SetSysrootHandler(context, 0x3F0);

    /// <summary>NID_4F57A13F: calls the handler at +0x3F0; without one, 0x80010086.</summary>
    [Hle(Nid = 0x4F57A13F, Library = "SceSysrootForKernel")]
    void NID_4F57A13F(HleContext context) => CallSysrootHandler(context, 0x3F0, unchecked((int)0x80010086));

    /// <summary>NID_B39CD708: sets the NID name lookup (+0x3A4).</summary>
    [Hle(Nid = 0xB39CD708, Library = "SceSysrootForKernel")]
    void NID_B39CD708(HleContext context) => SetSysrootHandler(context, 0x3A4);

    /// <summary>ksceSysrootGetNidName: calls the NID name lookup (+0x3A4); without one, 0x80010058.</summary>
    [Hle(Nid = 0x0B79E220, Library = "SceSysrootForKernel")]
    void ksceSysrootGetNidName(HleContext context) => CallSysrootHandler(context, 0x3A4, NotFound);

    /// <summary>NID_CD8B5D37: entry +0 of the table at +0x400; without it (or the entry), 0x80020001.</summary>
    [Hle(Nid = 0xCD8B5D37, Library = "SceSysrootForKernel")]
    void NID_CD8B5D37(HleContext context) => CallSysrootTableEntry(context, 0x400, 0x0, SceError.ERROR);

    /// <summary>NID_CA8D1A2B: entry +0x4 of the table at +0x400; without it (or the entry), 0x80020001.</summary>
    [Hle(Nid = 0xCA8D1A2B, Library = "SceSysrootForKernel")]
    void NID_CA8D1A2B(HleContext context) => CallSysrootTableEntry(context, 0x400, 0x4, SceError.ERROR);

    /// <summary>NID_FF44A7BA: entry +0x8 of the table at +0x400; without it (or the entry), 0x80020001.</summary>
    [Hle(Nid = 0xFF44A7BA, Library = "SceSysrootForKernel")]
    void NID_FF44A7BA(HleContext context) => CallSysrootTableEntry(context, 0x400, 0x8, SceError.ERROR);
}
