using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// The firmware kernel exports kernel plugins import (SceSysmemForDriver/ForKernel, SceCpuForDriver/ForKernel,
/// SceSysrootForKernel, SceDebugForDriver, SceProcessmgrForDriver, SceProcEventForDriver, SceExcpmgrForKernel,
/// SceThreadmgrForDriver, SceSblACMgrForDriver, ScePowerForDriver, SceSysclibForDriver, SceIofilemgrForDriver,
/// SceCtrlForDriver): one test per export or group of exports, with the firmware's semantics and error codes. The
/// module manager's are in <see cref="KernelModulemgrTests"/>.
/// </summary>
[Collection("guest")]
public sealed class KernelExportsTests(GuestMemoryFixture fx) : IDisposable
{
    const uint KernelRw = 0x1020D006, UserRw = 0x0C20D060, UserRx = 0x0C20D050;

    readonly HleHarness _h = new(fx, "kernexp");
    SysmemKernel Sysmem => _h.Module<SysmemKernel>();
    ProcessmgrKernel Procmgr => _h.Module<ProcessmgrKernel>();

    public void Dispose() => _h.Dispose();

    /// <summary>The firmware imports of the kernel plugins of a HENkaku installation (taihen.skprx, kubridge.skprx and
    /// fd_fix.skprx as installed by the homebrew's own installers, their import tables read with the loader), and the
    /// firmware functions kubridge looks up by module: every one is served by VitaSharp. taiHEN's own libraries
    /// (taihenForKernel, taihenModuleUtils, which kubridge and fd_fix import) are not firmware: only the real
    /// taihen.skprx exports them.</summary>
    static readonly uint[] PluginImports =
    [
        // SceThreadmgrForDriver
        0xFBAA026E, 0xE54FD746, 0x9DCB4B7A, 0x270993A6, 0x1E82E5D0, 0x16AC80C5, 0x0A912340, 0x1D17DECF, 0x21F5419B,
        0x3E20216F, 0xC6674E7D,
        // SceModulemgrForDriver, SceModulemgrForKernel
        0x9D953C22, 0x86D8D634, 0x728E72A6, 0x5182E212, 0x49A3EDC7, 0x189BFBBB, 0x100DAEB9, 0x0675B682, 0x03B30B7E,
        0xFE303863, 0xD269F915, 0x97CF7B4E, 0x7BB4CE54, 0x5972E2CC,
        // SceSblACMgrForDriver, SceSysclibForDriver, SceCtrlForDriver
        0x8612B243, 0xAE7A8981, 0x40C88316, 0x12CEE649, 0x0AB9BF5C, 0xEA1D3A34,
        // SceSysmemForDriver, SceSysmemForKernel
        0xDB3EC244, 0xD6437637, 0xC94850C9, 0xBF209859, 0xBC996A7A, 0xA841EDDA, 0x9328E0E8, 0x85336A1C, 0x84A4AF5E,
        0x7B4CB60A, 0x6D88EF8A, 0x61317102, 0x605275F8, 0x58D21746, 0x45D22597, 0x3EBCE343, 0x2ECF7944, 0x149885C4,
        0x047D32F2, 0x00ED6C14, 0x009E1C61, 0x0F5C84B7, 0xDF0288D7, 0x30931572,
        // SceCpuForKernel, SceCpuForDriver
        0x8C683DEC, 0x6BA2E51C, 0x19F17BD0, 0xF5BAD43B, 0x821FC0EE, 0x4C38CE4D, 0x7BB9D5DF, 0x9EC91017, 0xBF82DEB2,
        0xD32ACE9E, 0xD6ED0C46,
        // SceIofilemgrForDriver, SceSysrootForKernel, SceProcEventForDriver, SceDebugForDriver, SceProcessmgrForDriver
        0xF99DD8A3, 0xE17EFC03, 0x75192972, 0x62090481, 0xEC3124A3, 0x63EBB05B, 0x2A43912D, 0x391B5B74, 0x3801D7D6,
        0xAF80F39C,
        // looked up by module: SceSysmem's caches, ScePower's clock, the exception manager, the copy into user code,
        // the remote heap
        0xF4C7F578, 0xC63DACD5, 0x0E333BEC, 0x03499636, 0x00063675, 0x2995558D, 0x00B1CA0F, 0x9C28EA9A,
    ];

    [Fact]
    public void EveryFirmwareImportOfTheKernelPluginsIsImplemented()
    {
        var missing = PluginImports.Where(n => !_h.Emu.Hle.IsImplemented(n)).Select(n => $"0x{n:X8} {NidDatabase.NameOf(n)}").ToList();
        Assert.True(missing.Count == 0, string.Join("\n", missing));
    }

    uint Opt(uint attr, uint vbase = 0, uint align = 0, int pid = 0)
    {
        uint opt = _h.Alloc(0x58);
        GuestMemory.W32(opt, 0x58);
        GuestMemory.W32(opt + 8, attr);
        GuestMemory.W32(opt + 0xC, vbase);
        GuestMemory.W32(opt + 0x14, align);
        GuestMemory.W32(opt + 0x24, (uint)pid);
        return opt;
    }

    [Fact]
    public void AllocMemBlockPlacesKernelAndUserTypes()
    {
        using var scope = ProcessScope.Enter(_h.Emu);
        uint name = _h.Str("kblock"), basePtr = _h.Alloc(4);
        Assert.Equal(SceError.ILLEGAL_ADDR, Call(Sysmem, "ksceKernelAllocMemBlock", 0u, KernelRw, 0x1000u, 0u));
        Assert.Equal(SceError.ILLEGAL_SIZE, Call(Sysmem, "ksceKernelAllocMemBlock", name, KernelRw, 0u, 0u));

        int k = Call(Sysmem, "ksceKernelAllocMemBlock", name, KernelRw, 0x1800u, 0u);
        Assert.True(k > 0);
        Assert.Equal(0, Call(Sysmem, "ksceKernelGetMemBlockBase", k, basePtr));
        uint kbase = GuestMemory.R32(basePtr);
        Assert.InRange(kbase, GuestMemory.KernelStart, GuestMemory.KernelEnd - 1);
        Assert.True(KernelSpace.IsKernelOwned(kbase));
        Assert.Equal(KernelSpace.KernelPid, _h.Emu.Kernel.Objects.Get<MemBlock>(k)!.OwnerPid);
        Assert.Equal(0x2000u, _h.Emu.Kernel.Objects.Get<MemBlock>(k)!.Size);

        // a user block at a fixed address (HAS_VBASE), as kubridge's loaders ask for
        const uint probe = 0xA1000000;
        int u = Call(Sysmem, "ksceKernelAllocMemBlock", name, UserRx, 0x3000u, Opt(0x1, probe));
        Assert.True(u > 0, $"0x{u:X8}");
        Assert.Equal(0, Call(Sysmem, "ksceKernelGetMemBlockBase", u, basePtr));
        Assert.Equal(probe, GuestMemory.R32(basePtr));
        Assert.True(KernelSpace.IsUserRange(probe, 0x3000));
        Assert.Equal(SceError.NO_MEMORY, Call(Sysmem, "ksceKernelAllocMemBlock", name, UserRx, 0x1000u, Opt(0x1, probe)));
        Assert.Equal(SceError.INVALID_ARGUMENT, Call(Sysmem, "ksceKernelAllocMemBlock", name, UserRw, 0x1000u, Opt(0x1, probe + 4)));
        // alignment, and HAS_PID with no such process
        int aligned = Call(Sysmem, "ksceKernelAllocMemBlock", name, UserRw, 0x1000u, Opt(0x4, align: 0x100000));
        Assert.Equal(0, Call(Sysmem, "ksceKernelGetMemBlockBase", aligned, basePtr));
        Assert.Equal(0u, GuestMemory.R32(basePtr) & 0xFFFFF);
        Assert.Equal(SceError.INVALID_PID, Call(Sysmem, "ksceKernelAllocMemBlock", name, UserRw, 0x1000u, Opt(0x80, pid: 0x7FFFFF01)));

        Assert.Equal(SceError.ILLEGAL_ADDR, Call(Sysmem, "ksceKernelGetMemBlockBase", k, 0u));
        foreach (int uid in new[] { k, u, aligned })
        {
            Assert.Equal(0, Call(Sysmem, "ksceKernelFreeMemBlock", uid));
            Assert.Equal(SceError.INVALID_UID, Call(Sysmem, "ksceKernelFreeMemBlock", uid));
        }
    }

    [Fact]
    public void CopiesCheckTheUserSide()
    {
        using var scope = ProcessScope.Enter(_h.Emu);
        int k = Call(Sysmem, "ksceKernelAllocMemBlock", _h.Str("kcopy"), KernelRw, 0x1000u, 0u);
        uint basePtr = _h.Alloc(4);
        Call(Sysmem, "ksceKernelGetMemBlockBase", k, basePtr);
        uint kmem = GuestMemory.R32(basePtr), user = _h.Alloc(64);
        GuestMemory.WriteCString(user, "hello");

        Assert.Equal(0, Call(Sysmem, "ksceKernelMemcpyUserToKernel", kmem, user, 6u));
        Assert.Equal("hello", GuestMemory.ReadCString(kmem));
        // kernel memory is not user memory: as a user source or destination the copy faults
        Assert.Equal(KernelSpace.UserCopyFault, Call(Sysmem, "ksceKernelMemcpyUserToKernel", kmem + 0x100, kmem, 6u));
        Assert.Equal(KernelSpace.UserCopyFault, Call(Sysmem, "ksceKernelMemcpyKernelToUser", kmem + 0x100, kmem, 6u));
        Assert.Equal(KernelSpace.UserCopyFault, Call(Sysmem, "ksceKernelMemcpyUserToKernel", kmem, 0x10u, 4u)); // unmapped
        Assert.Equal(0, Call(Sysmem, "ksceKernelMemcpyKernelToUser", user + 32, kmem, 6u));
        Assert.Equal("hello", GuestMemory.ReadCString(user + 32));
        Assert.Equal(0, Call(Sysmem, "ksceKernelMemcpyUserToKernel", kmem, 0x10u, 0u)); // nothing to copy

        // strncpy: the length without the NUL, the rest of the destination zeroed; no NUL within len: len
        GuestMemory.Fill(kmem + 0x200, 0xEE, 16);
        Assert.Equal(5, Call(Sysmem, "ksceKernelStrncpyUserToKernel", kmem + 0x200, user, 16u));
        Assert.Equal(new byte[16 - 5], GuestMemory.ReadSpan(kmem + 0x205, 11).ToArray());
        Assert.Equal(3, Call(Sysmem, "ksceKernelStrncpyUserToKernel", kmem + 0x300, user, 3u));
        Assert.Equal(KernelSpace.UserCopyFault, Call(Sysmem, "ksceKernelStrncpyUserToKernel", kmem + 0x300, kmem, 3u));

        // mapped user-visible, the kernel block is a valid user range
        Assert.Equal(0, Call(Sysmem, "ksceKernelMapBlockUserVisible", k));
        Assert.Equal(0, Call(Sysmem, "ksceKernelMemcpyKernelToUser", kmem + 0x100, user, 6u));
        Assert.Equal(SceError.INVALID_UID, Call(Sysmem, "ksceKernelMapBlockUserVisible", 0x7FFFFF01));

        // the ForPid copy reads the process's memory; an unknown process is refused
        Assert.Equal(0, Call(Sysmem, "ksceKernelMemcpyUserToKernelForPid", _h.Emu.Pid, kmem + 0x400, user, 6u));
        Assert.Equal("hello", GuestMemory.ReadCString(kmem + 0x400));
        Assert.Equal(SceError.INVALID_PID, Call(Sysmem, "ksceKernelMemcpyUserToKernelForPid", 0x7FFFFF01, kmem, user, 6u));

        // into executable user memory of a process (code the kernel installs there)
        uint code = _h.Alloc(16);
        GuestMemory.W32(kmem + 0x500, 0xE12FFF1E);
        Assert.Equal(0, Call(Sysmem, "ksceKernelProcCopyToUserRx", 0, code, kmem + 0x500, 4u));
        Assert.Equal(0xE12FFF1Eu, GuestMemory.R32(code));
        int k2 = Call(Sysmem, "ksceKernelAllocMemBlock", _h.Str("kcopy2"), KernelRw, 0x1000u, 0u);
        Call(Sysmem, "ksceKernelGetMemBlockBase", k2, basePtr);
        Assert.Equal(KernelSpace.UserCopyFault, Call(Sysmem, "ksceKernelProcCopyToUserRx", 0, GuestMemory.R32(basePtr), kmem + 0x500, 4u));
        Call(Sysmem, "ksceKernelFreeMemBlock", k2);

        // the unrestricted copy writes anywhere mapped
        Assert.Equal(0, Call(Sysmem, "ksceKernelCpuUnrestrictedMemcpy", kmem + 0x600, user, 6u));
        Assert.Equal("hello", GuestMemory.ReadCString(kmem + 0x600));
        Assert.Equal(SceError.INVALID_ARGUMENT, Call(Sysmem, "ksceKernelCpuUnrestrictedMemcpy", 0x10u, user, 6u));
        Call(Sysmem, "ksceKernelFreeMemBlock", k);
    }

    [Fact]
    public void HeapsAllocateZeroedKernelMemory()
    {
        uint name = _h.Str("kheap");
        Assert.Equal(SceError.ILLEGAL_SIZE, Call(Sysmem, "ksceKernelCreateHeap", name, 0u, 0u));
        int heap = Call(Sysmem, "ksceKernelCreateHeap", name, 0x10000u, 0u);
        Assert.True(heap > 0);
        uint a = Convert.ToUInt32(Invoke(Sysmem, "ksceKernelAllocHeapMemory", heap, 0x40u));
        uint b = Convert.ToUInt32(Invoke(Sysmem, "ksceKernelAllocHeapMemory", heap, 0x40u));
        Assert.NotEqual(0u, a);
        Assert.NotEqual(a, b);
        Assert.True(KernelSpace.IsKernelOwned(a));
        Assert.Equal(new byte[0x40], GuestMemory.ReadSpan(a, 0x40).ToArray());
        Assert.Equal(0u, Convert.ToUInt32(Invoke(Sysmem, "ksceKernelAllocHeapMemory", 0x7FFFFF01, 0x40u)));
        Assert.Equal(0, Call(Sysmem, "ksceKernelFreeHeapMemory", heap, a));
        Assert.Equal(SceError.INVALID_ARGUMENT, Call(Sysmem, "ksceKernelFreeHeapMemory", heap, a));
        Assert.Equal(0, Call(Sysmem, "ksceKernelDeleteHeap", heap)); // b goes with it
        Assert.Equal(SceError.ILLEGAL_HEAP_ID, Call(Sysmem, "ksceKernelDeleteHeap", heap));
        Assert.Equal(SceError.ILLEGAL_HEAP_ID, Call(Sysmem, "ksceKernelFreeHeapMemory", heap, b));
    }

    [Fact]
    public void ClassesAndObjects()
    {
        uint cls = _h.Alloc(0x2C), name = _h.Str("TestClass");
        // create callback: obj->data[0] (+8) = 0x55
        uint create = _h.Alloc(8);
        GuestMemory.Write(create, (ushort)0x2155);
        GuestMemory.Write(create + 2, (ushort)0x6081);
        GuestMemory.Write(create + 4, (ushort)0x4770);
        Assert.Equal(SceError.ILLEGAL_SIZE, Call(Sysmem, "ksceKernelCreateClass", cls, name, 0u, 4u, 0u, 0u));
        Assert.Equal(0, Call(Sysmem, "ksceKernelCreateClass", cls, name, 0u, 0x20u, create | 1, 0u));
        uint root = Convert.ToUInt32(Invoke(Sysmem, "ksceKernelGetUidClass"));
        Assert.Equal(root, GuestMemory.R32(cls + 0x10)); // the parent defaults to the root class
        Assert.Equal(0xABCE9DA5u, GuestMemory.R32(cls + 0x28));
        Assert.Equal(0x20, GuestMemory.Read<ushort>(cls + 0x18));
        Assert.Equal("SceUIDClass", GuestMemory.ReadCString(GuestMemory.R32(root + 0x0C)));

        _h.OnGuest(_ =>
        {
            uint objOut = _h.Alloc(4);
            int guid = Call(Sysmem, "ksceGUIDKernelCreateWithOpt", cls, _h.Str("obj"), 0u, objOut);
            Assert.True(guid > 0);
            uint obj = GuestMemory.R32(objOut);
            Assert.Equal(cls, GuestMemory.R32(obj + 4));
            Assert.Equal(0x55u, GuestMemory.R32(obj + 8));
            Assert.Equal(SceError.ILLEGAL_TYPE, Call(Sysmem, "ksceGUIDKernelCreateWithOpt", _h.Alloc(0x2C), 0u, 0u, objOut));

            uint got = _h.Alloc(4);
            Assert.Equal(0, Call(Sysmem, "ksceGUIDReferObjectWithClass", guid, cls, got));
            Assert.Equal(obj, GuestMemory.R32(got));
            Assert.Equal(0, Call(Sysmem, "ksceGUIDReferObjectWithClass", guid, root, got)); // a base class matches
            Assert.Equal(SceError.INVALID_UID_CLASS, Call(Sysmem, "ksceGUIDReferObjectWithClass", guid, _h.Alloc(0x2C), got));
            Assert.Equal(0, Call(Sysmem, "ksceGUIDReferObject", guid, got));
            Assert.Equal(obj, GuestMemory.R32(got));
            Assert.Equal(3, _h.Emu.Kernel.Objects.Get<KernelData>(guid)!.References);
            for (int i = 0; i < 3; i++)
            {
                Assert.Equal(0, Call(Sysmem, "ksceGUIDReleaseObject", guid));
            }

            Assert.Equal(0, _h.Emu.Kernel.Objects.Get<KernelData>(guid)!.References);

            // process handles: the same UID, now the process's
            Assert.Equal(guid, Call(Sysmem, "kscePUIDOpenByGUID", _h.Emu.Pid, guid));
            Assert.Equal(_h.Emu.Pid, _h.Emu.Kernel.Objects.Get<KernelData>(guid)!.OwnerPid);
            Assert.Equal(guid, Call(Sysmem, "kscePUIDtoGUID", _h.Emu.Pid, guid));
            Assert.Equal(0, Call(Sysmem, "kscePUIDClose", _h.Emu.Pid, guid));
            Assert.Equal(SceError.INVALID_PID, Call(Sysmem, "kscePUIDOpenByGUID", 0x7FFFFF01, guid));

            Assert.Equal(0, Call(Sysmem, "ksceGUIDClose", guid));
            Assert.Equal(SceError.INVALID_UID, Call(Sysmem, "ksceGUIDClose", guid));
            Assert.Equal(SceError.INVALID_UID, Call(Sysmem, "ksceGUIDReferObject", guid, got));
            Assert.Equal(SceError.INVALID_UID, Call(Sysmem, "ksceGUIDReleaseObject", guid));
            Assert.Equal(SceError.INVALID_UID, Call(Sysmem, "kscePUIDtoGUID", 0, guid));
            Assert.Equal(SceError.INVALID_UID, Call(Sysmem, "kscePUIDClose", 0, guid));
        });

        // an HLE object (a memory block) has a body kernel code can refer to, stable across calls
        int block = Call(Sysmem, "ksceKernelAllocMemBlock", _h.Str("b"), KernelRw, 0x1000u, 0u);
        uint body1 = _h.Alloc(4), body2 = _h.Alloc(4);
        Assert.Equal(0, Call(Sysmem, "ksceGUIDReferObject", block, body1));
        Assert.Equal(0, Call(Sysmem, "ksceGUIDReferObject", block, body2));
        Assert.Equal(GuestMemory.R32(body1), GuestMemory.R32(body2));
        Assert.Equal(0, Call(Sysmem, "ksceGUIDClose", block)); // frees the block
        Assert.Null(_h.Emu.Kernel.Objects.Get<MemBlock>(block));
    }

    [Fact]
    public void PidContextAndAddressSpaceRecords()
    {
        uint out1 = _h.Alloc(4), out2 = _h.Alloc(4);
        Assert.Equal(SceError.ILLEGAL_ADDR, Call(Sysmem, "ksceKernelGetPidContext", 0, 0u));
        Assert.Equal(0, Call(Sysmem, "ksceKernelGetPidContext", 0, out1));
        Assert.Equal(0, Call(Sysmem, "ksceKernelGetPidContext", _h.Emu.Pid, out2));
        Assert.Equal(GuestMemory.R32(out1), GuestMemory.R32(out2));
        Assert.Equal(0, Call(Sysmem, "ksceKernelGetPidContext", KernelSpace.KernelPid, out2));
        Assert.NotEqual(GuestMemory.R32(out1), GuestMemory.R32(out2));
        Assert.Equal(SceError.INVALID_PID, Call(Sysmem, "ksceKernelGetPidContext", 0x7FFFFF01, out2));
        uint space = Convert.ToUInt32(Invoke(Sysmem, "ksceKernelSysrootGetCurrentAddressSpaceCB"));
        Assert.NotEqual(0u, space);
        Assert.Equal(space, Convert.ToUInt32(Invoke(Sysmem, "ksceKernelSysrootGetCurrentAddressSpaceCB")));
    }

    [Fact]
    public void ProcessTitleId()
    {
        uint buf = _h.Alloc(16);
        Assert.Equal(SceError.ILLEGAL_ADDR, Call(Sysmem, "ksceKernelSysrootGetProcessTitleId", 0, 0u, 16u));
        Assert.Equal(0, Call(Sysmem, "ksceKernelSysrootGetProcessTitleId", 0, buf, 16u));
        Assert.Equal(_h.Emu.TitleId, GuestMemory.ReadCString(buf));
        Assert.Equal(0, Call(Sysmem, "ksceKernelSysrootGetProcessTitleId", KernelSpace.KernelPid, buf, 16u));
        Assert.Equal("", GuestMemory.ReadCString(buf));
        Assert.Equal(SceError.INVALID_PID, Call(Sysmem, "ksceKernelSysrootGetProcessTitleId", 0x7FFFFF01, buf, 16u));
    }

    [Fact]
    public void CachesInterruptsAndSpinlocks()
    {
        // a Thumb function returning 1, run, then rewritten to return 2: after the icache flush the new code runs
        uint code = _h.Alloc(16);
        GuestMemory.Write(code, (ushort)0x2001);
        GuestMemory.Write(code + 2, (ushort)0x4770);
        _h.OnGuest(t =>
        {
            Assert.Equal(1u, t.CallGuest(code | 1));
            GuestMemory.Write(code, (ushort)0x2002);
            Invoke(Sysmem, "ksceKernelCpuDcacheWritebackInvalidateRange", code, 4u);
            Invoke(Sysmem, "ksceKernelCpuIcacheInvalidateRange", code, 4u);
            Assert.Equal(2u, t.CallGuest(code | 1));
            GuestMemory.Write(code, (ushort)0x2003);
            Invoke(Sysmem, "ksceKernelCpuIcacheAndL2WritebackInvalidateRange", code, 4u);
            Assert.Equal(3u, t.CallGuest(code | 1));

            // interrupt masking nests: suspend returns the previous state, resume puts it back
            Assert.Equal(0, Call(Sysmem, "ksceKernelCpuSuspendIntr"));
            Assert.Equal(0x80, Call(Sysmem, "ksceKernelCpuDisableInterrupts"));
            Invoke(Sysmem, "ksceKernelCpuEnableInterrupts", 0x80);
            Assert.Equal(0x80, Call(Sysmem, "ksceKernelCpuSuspendIntr"));
            Call(Sysmem, "ksceKernelCpuResumeIntr", 0);
            Assert.Equal(0, Call(Sysmem, "ksceKernelCpuSuspendIntr"));
            Call(Sysmem, "ksceKernelCpuResumeIntr", 0);

            // spinlocks: the word holds the holder, 0 when free
            uint lockWord = _h.Alloc(4);
            Assert.Equal(0, Call(Sysmem, "ksceKernelCpuLockSuspendIntrStoreFlag", lockWord));
            Assert.Equal(1u, GuestMemory.R32(lockWord));
            Invoke(Sysmem, "ksceKernelCpuUnlockResumeIntrStoreFlag", lockWord, 0);
            Assert.Equal(0u, GuestMemory.R32(lockWord));
            Assert.Equal(SceError.ILLEGAL_ADDR, Call(Sysmem, "ksceKernelCpuLockSuspendIntrStoreFlag", 0u));
        });
    }

    [Fact]
    public void LockStoreLRKeepsTheHolder()
    {
        // the lock takes its address in r0 and keeps the caller's return address in the word
        uint lockWord = _h.Alloc(4);
        _h.OnGuest(t =>
        {
            unsafe
            {
                t.Core.State->R[0] = lockWord;
                t.Core.State->R[14] = 0x81001235;
            }

            Invoke(Sysmem, "ksceKernelCpuLockStoreLR", t.Hle);
            Assert.Equal(0x81001235u, GuestMemory.R32(lockWord));
            Invoke(Sysmem, "ksceKernelCpuUnlockStoreLR", lockWord);
            Assert.Equal(0u, GuestMemory.R32(lockWord));
        });
    }

    [Fact]
    public void KernelPanicStopsTheCaller()
    {
        Assert.Throws<InvalidOperationException>(() => Invoke(Sysmem, "ksceDebugPrintKernelPanic", 0u, 0x81000001u));
    }

    [Fact]
    public void ProcessLocalStorage()
    {
        uint addr = _h.Alloc(4);
        Assert.Equal(SceError.ILLEGAL_ADDR, Call(Procmgr, "ksceKernelCreateProcessLocalStorage", 0u, 0x20u));
        Assert.Equal(SceError.ILLEGAL_SIZE, Call(Procmgr, "ksceKernelCreateProcessLocalStorage", _h.Str("pls"), 0u));
        int key = Call(Procmgr, "ksceKernelCreateProcessLocalStorage", _h.Str("pls"), 0x20u);
        Assert.True(key >= 0);
        Assert.Equal(SceError.NO_PROCESS_DATA, Call(Procmgr, "ksceKernelGetProcessLocalStorageAddrForPid", 0, key, addr, 0));
        Assert.Equal(0u, GuestMemory.R32(addr));
        Assert.Equal(0, Call(Procmgr, "ksceKernelGetProcessLocalStorageAddrForPid", 0, key, addr, 1));
        uint block = GuestMemory.R32(addr);
        Assert.True(KernelSpace.IsKernelOwned(block));
        Assert.Equal(new byte[0x20], GuestMemory.ReadSpan(block, 0x20).ToArray());
        Assert.Equal(0, Call(Procmgr, "ksceKernelGetProcessLocalStorageAddrForPid", _h.Emu.Pid, key, addr, 0));
        Assert.Equal(block, GuestMemory.R32(addr));
        Assert.Equal(SceError.INVALID_ARGUMENT, Call(Procmgr, "ksceKernelGetProcessLocalStorageAddrForPid", 0, key + 100, addr, 1));
        Assert.Equal(SceError.INVALID_PID, Call(Procmgr, "ksceKernelGetProcessLocalStorageAddrForPid", 0x7FFFFF01, key, addr, 1));
        Assert.Equal(SceError.ILLEGAL_ADDR, Call(Procmgr, "ksceKernelGetProcessLocalStorageAddrForPid", 0, key, 0u, 1));
    }

    [Fact]
    public void RemoteProcessHeap()
    {
        uint p = Convert.ToUInt32(Invoke(Procmgr, "ksceKernelAllocRemoteProcessHeap", _h.Emu.Pid, 0x100u, 0u));
        Assert.NotEqual(0u, p);
        Assert.True(KernelSpace.IsUserRange(p, 0x100));
        Assert.Equal(0u, Convert.ToUInt32(Invoke(Procmgr, "ksceKernelAllocRemoteProcessHeap", 0x7FFFFF01, 0x100u, 0u)));
        Invoke(Procmgr, "ksceKernelFreeRemoteProcessHeap", _h.Emu.Pid, p);
        Assert.False(GuestMemory.IsMapped(p));
    }

    [Fact]
    public void ProcEventHandlersAreCopied()
    {
        uint handler = _h.Alloc(0x1C);
        GuestMemory.W32(handler, 0x1C);
        GuestMemory.W32(handler + 8, 0x81000101);
        Assert.Equal(SceError.ILLEGAL_ADDR, Call(Procmgr, "ksceKernelRegisterProcEventHandler", 0u, handler, 0));
        GuestMemory.W32(handler, 0x40);
        Assert.Equal(SceError.ILLEGAL_SIZE, Call(Procmgr, "ksceKernelRegisterProcEventHandler", _h.Str("h"), handler, 0));
        GuestMemory.W32(handler, 0x1C);
        int uid = Call(Procmgr, "ksceKernelRegisterProcEventHandler", _h.Str("h"), handler, 0);
        Assert.True(uid > 0);
        GuestMemory.W32(handler + 8, 0); // the caller's copy (on its stack) goes away
        var h = _h.Emu.Kernel.Objects.Get<KernelData>(uid)!;
        Assert.Equal(0x81000101u, GuestMemory.R32(h.Address + 8));
        Assert.Equal(0, Call(Procmgr, "ksceKernelUnregisterProcEventHandler", uid));
        Assert.Equal(SceError.INVALID_UID, Call(Procmgr, "ksceKernelUnregisterProcEventHandler", uid));
    }

    [Fact]
    public void ExceptionHandlersChainPerKind()
    {
        uint a = _h.Alloc(16), b = _h.Alloc(16);
        Assert.Equal(SceError.INVALID_ARGUMENT, Call(Procmgr, "sceKernelRegisterExceptionHandler", 8u, 0u, a));
        Assert.Equal(SceError.ILLEGAL_ADDR, Call(Procmgr, "sceKernelRegisterExceptionHandler", 1u, 0u, 0u));
        Assert.Equal(0, Call(Procmgr, "sceKernelRegisterExceptionHandler", 4u, 0u, a));
        Assert.Equal(0, Call(Procmgr, "sceKernelRegisterExceptionHandler", 4u, 0u, b));
        Assert.Equal(a, GuestMemory.R32(b)); // the new head links to the previous one
        Assert.Equal(0u, GuestMemory.R32(a));
        Assert.Equal([b, a], Procmgr.ExceptionHandlers(4));
        Assert.Empty(Procmgr.ExceptionHandlers(3));
    }

    [Fact]
    public void KernelThreadsWorkForTheKernel()
    {
        uint code = _h.Alloc(16);
        GuestMemory.Write(code, (ushort)0x2007); // movs r0, #7; bx lr
        GuestMemory.Write(code + 2, (ushort)0x4770);
        _h.OnGuest(t =>
        {
            int uid = Call(Procmgr, "ksceKernelCreateThread", "kthread", code | 1, 0x40, 0x4000, 0u, 0x10000, 0u);
            Assert.True(uid > 0);
            var kt = _h.Emu.Kernel.GetThread(uid)!;
            Assert.True(kt.KernelMode);
            Assert.Equal(0, Call(_h.Module<ThreadMgr>(), "ksceKernelStartThread", uid, 0u, 0u));
            uint stat = _h.Alloc(4);
            Assert.Equal(7, Call(_h.Module<ThreadMgr>(), "ksceKernelWaitThreadEnd", uid, new Ptr<int>(stat), new Ptr<uint>(0)));
            Assert.Equal(7u, GuestMemory.R32(stat));
            Assert.Equal(SceError.INVALID_ARGUMENT, Call(Procmgr, "ksceKernelCreateThread", "bad", 0u, 0x40, 0x4000, 0u, 0, 0u));
            // run with stack: func(arg) on the caller
            Assert.Equal(7, Call(Procmgr, "ksceKernelRunWithStack", 0x4000u, code | 1, 0u));
            Assert.Equal(SceError.ILLEGAL_ADDR, Call(Procmgr, "ksceKernelRunWithStack", 0x4000u, 0u, 0u));
        });
    }

    [Fact]
    public void KernelMutexes()
    {
        var tm = _h.Module<ThreadMgr>();
        _h.OnGuest(_ =>
        {
            int m = Call(tm, "ksceKernelCreateMutex", "kmutex", 0u, 0, 0u);
            Assert.True(m > 0);
            Assert.Equal(0, Call(tm, "ksceKernelLockMutex", m, 1, new Ptr<uint>(0)));
            Assert.Equal(SceError.MUTEX_RECURSIVE, Call(tm, "ksceKernelTryLockMutex", m, 1)); // not recursive
            Assert.Equal(0, Call(tm, "ksceKernelUnlockMutex", m, 1));
            Assert.Equal(0, Call(tm, "ksceKernelTryLockMutex", m, 1));
            Assert.Equal(0, Call(tm, "ksceKernelUnlockMutex", m, 1));
            Assert.Equal(0, Call(tm, "ksceKernelDeleteMutex", m));
            Assert.Equal(SceError.UNKNOWN_MUTEX_ID, Call(tm, "ksceKernelDeleteMutex", m));
        });
    }

    [Fact]
    public void AccessControlAndClock()
    {
        Assert.Equal(0, Call(Procmgr, "ksceSblACMgrIsSystemProgram", 0));
        Assert.Equal(1, Call(Procmgr, "ksceSblACMgrIsSystemProgram", KernelSpace.KernelPid));
        Assert.True(Call(Procmgr, "kscePowerGetSysClockFrequency") > 0);
        Assert.Equal(SystemMisc.POWER_ERROR_INVALID_VALUE, Call(Procmgr, "kscePowerSetSysClockFrequency", 501));
        long before = VitaSharp.Core.Cpu.CpuCore.InstructionsPerSecond;
        try
        {
            Assert.Equal(0, Call(Procmgr, "kscePowerSetSysClockFrequency", 500));
            if (before > 0)
            {
                Assert.Equal(500, Call(Procmgr, "kscePowerGetSysClockFrequency"));
            }
        }
        finally
        {
            VitaSharp.Core.Cpu.CpuCore.InstructionsPerSecond = before;
        }
    }

    [Fact]
    public void KernelLibraryAliasesShareTheUserImplementations()
    {
        // the kernel's C library, I/O and controller calls take the same arguments as the user ones
        _h.OnGuest(_ =>
        {
            var pm = _h.Module<ProcessMgr>();
            uint a = _h.Str("abcd"), b = _h.Str("abcf"), dst = _h.Alloc(8);
            Assert.Equal(dst, Convert.ToUInt32(Invoke(pm, "kmemcpy", dst, a, 5u)));
            Assert.Equal("abcd", GuestMemory.ReadCString(dst));
            Assert.Equal(dst, Convert.ToUInt32(Invoke(pm, "kmemset", dst, 0x41, 3u)));
            Assert.Equal("AAAd", GuestMemory.ReadCString(dst));
            Assert.True(Call(pm, "kstrncmp", a, b, 4u) < 0);
            Assert.Equal(0, Call(pm, "kstrncmp", a, b, 3u));
        });
        foreach (string name in new[] { "ksnprintf", "ksceIoOpen", "ksceIoRead", "ksceIoLseek", "ksceIoClose", "ksceCtrlPeekBufferPositive" })
        {
            var nids = NidDatabase.LookupName(name);
            Assert.NotEmpty(nids);
            Assert.All(nids, n => Assert.True(_h.Emu.Hle.IsImplemented(n.Nid), name));
        }
    }

    [Fact]
    public void ProcessIdOfKernelAndUserThreads()
    {
        var tm = _h.Module<ThreadMgr>();
        _h.OnGuest(t =>
        {
            Assert.Equal(_h.Emu.Pid, Call(tm, "sceKernelGetProcessId"));
            t.KernelMode = true;
            Assert.Equal(KernelSpace.KernelPid, Call(tm, "sceKernelGetProcessId"));
            t.KernelMode = false;
        });
    }
}
