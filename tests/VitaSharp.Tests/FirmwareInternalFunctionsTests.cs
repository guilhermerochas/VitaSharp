using VitaSharp.Core.Firmware;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Loader;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// The FW 3.74 SceSysmem's internal functions as kernel code reaches them: a kernel module finds them by offset in
/// the firmware module's code segment (module_get_offset on the module's id, as plugins do for functions the firmware
/// does not export) and changes the access of a range of a user block the way the firmware's own protection code does:
/// the current address space, the block that holds the address (its FW 3.74 object: paging flags, base, size,
/// partition, page list), a new page object, unmap, map with the new access, back the page, free the page object.
/// Everything is synthetic and generic: a firmware SceSysmem with the FW 3.74 module NID and segment sizes, a block,
/// a function written into it.
/// </summary>
[Collection("guest")]
public sealed class FirmwareInternalFunctionsTests : IDisposable
{
    private const uint Sysmem374ModuleNid = 0xDCF937EF;
    private const uint FindMemBlockByAddr = 0x10CE1, AllocVirPageObject = 0xA115, FreeVirPageObject = 0x15DAD,
        MapVirPageCore = 0x149E5, UnmapVirPageWithOpt = 0x148D9, VirPageAllocPhyPage = 0x15B1D, PhyMemPartFreePhyPage = 0xE52D;
    private const uint UserRw = 0x0C20D060;
    private const uint MovR0 = 0xE3A00000, BxLr = 0xE12FFF1E;

    private readonly HleHarness _harness;

    public FirmwareInternalFunctionsTests(GuestMemoryFixture fixture)
    {
        _harness = new HleHarness(fixture, "fwinternal", kernelPlugins: true);
        SceModuleBuilder sysmem = new SceModuleBuilder("SceSysmem").ModuleNid(Sysmem374ModuleNid).SegmentSizes(0x2F280, 0x7604);
        sysmem.Export("SceCpuForDriver", 0x40ECDB0E, 0x0001, (0x5F6A8743, sysmem.ReturnConstant(0)));
        string kd = Path.Combine(_harness.Root, "os0", "kd");
        Directory.CreateDirectory(kd);
        File.WriteAllBytes(Path.Combine(kd, "sysmem.skprx"), sysmem.Build());
        FirmwareKernelImage.Forget(_harness.Root);
        string tai = Path.Combine(_harness.Root, "ur0", "tai");
        Directory.CreateDirectory(tai);
        File.WriteAllBytes(Path.Combine(tai, "empty.skprx"), new SceModuleBuilder("EmptyPlugin").Start(0).Build());
        File.WriteAllText(Path.Combine(tai, "config.txt"), "*KERNEL\nur0:tai/empty.skprx\n");
        Assert.Single(_harness.Emu.LoadPlugins());
    }

    public void Dispose()
    {
        FirmwareKernelImage.Forget(_harness.Root);
        _harness.Dispose();
    }

    private SysmemKernel Sysmem => _harness.Module<SysmemKernel>();

    /// <summary>Where module_get_offset puts <paramref name="offset"/> of the firmware SceSysmem's code segment.</summary>
    private uint Internal(uint offset)
    {
        LoadedModule shell = _harness.Emu.FirmwareKernel.Modules.Single(m => m.Name == "SceSysmem");
        uint address = _harness.Alloc(4);
        Assert.Equal(0, Call(_harness.Module<PluginFramework>(), "ModuleGetOffset", KernelSpace.KernelPid, shell.Uid, 0, offset, address));
        return GuestMemory.R32(address);
    }

    /// <summary>Calls the function at <paramref name="function"/> as kernel code of the harness's process.</summary>
    private uint CallKernel(uint function, params uint[] args)
    {
        uint result = 0;
        _harness.OnGuest(t =>
        {
            t.KernelMode = true;
            result = t.CallGuest(function, args);
        });
        return result;
    }

    /// <summary>A user block of the harness's process with "mov r0, #value; bx lr" at its second page.</summary>
    private (int Uid, uint Base) CodeBlock(byte value)
    {
        using ProcessScope.Scope scope = ProcessScope.Enter(_harness.Emu);
        uint basePtr = _harness.Alloc(4);
        int uid = Call(Sysmem, "ksceKernelAllocMemBlock", _harness.Str("code"), UserRw, 0x4000u, 0u);
        Assert.True(uid > 0, $"0x{uid:X8}");
        Assert.Equal(0, Call(Sysmem, "ksceKernelGetMemBlockBase", uid, basePtr));
        uint blockBase = GuestMemory.R32(basePtr);
        GuestMemory.W32(blockBase + 0x1000, MovR0 | value);
        GuestMemory.W32(blockBase + 0x1004, BxLr);
        return (uid, blockBase);
    }

    private uint CurrentAddressSpace()
    {
        using ProcessScope.Scope scope = ProcessScope.Enter(_harness.Emu);
        return (uint)Call(Sysmem, "ksceKernelSysrootGetCurrentAddressSpaceCB");
    }

    [Fact]
    public void TheBlockLookupGivesTheBlocksObjectInTheFirmwareLayout()
    {
        (int uid, uint blockBase) = CodeBlock(1);
        uint space = CurrentAddressSpace();
        Assert.Equal(0x4D95AEECu, GuestMemory.R32(space + 0x16C));
        uint blockOut = _harness.Alloc(4);

        Assert.Equal(0u, CallKernel(Internal(FindMemBlockByAddr), space, blockBase + 0x1000, 0, blockOut));
        uint block = GuestMemory.R32(blockOut);
        Assert.Equal(0x40000u, GuestMemory.R32(block + 0x0C) & 0xF0000);
        Assert.NotEqual(0u, GuestMemory.R32(block + 0x10));
        Assert.Equal(blockBase, GuestMemory.R32(block + 0x18));
        Assert.Equal(0x4000u, GuestMemory.R32(block + 0x1C));
        Assert.Equal((uint)uid, GuestMemory.R32(block + 0x3C));
        // its partition reaches the MMU context and the page-object heap, as the firmware's page code reads them
        uint partition = GuestMemory.R32(block + 0x24);
        uint mmuContext = GuestMemory.R32(partition + 0x1C);
        Assert.NotEqual(0u, mmuContext);
        Assert.NotEqual(0u, GuestMemory.R32(mmuContext + 0x1C));
        // one page holds the whole block, backed
        uint page = GuestMemory.R32(block + 0x30);
        Assert.Equal(0u, GuestMemory.R32(page));
        Assert.Equal(0x4u, GuestMemory.R32(page + 0x08) & 0x3F);
        Assert.Equal(blockBase, GuestMemory.R32(page + 0x0C));
        Assert.Equal(0x4000u, GuestMemory.R32(page + 0x10));
        // ksceGUIDReferObject gives the same object
        uint objectOut = _harness.Alloc(4);
        Assert.Equal(0, Call(Sysmem, "ksceGUIDReferObject", uid, objectOut));
        Assert.Equal(block, GuestMemory.R32(objectOut));

        // an exact range must match the block; an address no block holds clears the output
        Assert.Equal(0u, CallKernel(Internal(FindMemBlockByAddr), space, blockBase, 0x4000, blockOut));
        Assert.Equal(0x80020001u, CallKernel(Internal(FindMemBlockByAddr), space, blockBase, 0x1000, blockOut));
        Assert.Equal(0u, GuestMemory.R32(blockOut));
        Assert.Equal(0x80020001u, CallKernel(Internal(FindMemBlockByAddr), space, 0xDF000000, 0, blockOut));
        // outside every partition of the address space
        Assert.Equal(0x80024A03u, CallKernel(Internal(FindMemBlockByAddr), space, 0x00001000, 0, blockOut));
    }

    /// <summary>
    /// The protection change kernel code makes on a page of a user block (split off a page object, unmap, map with the
    /// new access, back it): every call succeeds, and code written into the range while it was writable runs as
    /// written afterwards, not the translation of what was there before.
    /// </summary>
    [Fact]
    public void ChangingAPagesAccessRunsTheCodeWrittenThere()
    {
        (_, uint blockBase) = CodeBlock(1);
        Assert.Equal(1u, CallKernel(blockBase + 0x1000));
        GuestMemory.W32(blockBase + 0x1000, MovR0 | 2);

        uint space = CurrentAddressSpace();
        uint blockOut = _harness.Alloc(4);
        Assert.Equal(0u, CallKernel(Internal(FindMemBlockByAddr), space, blockBase + 0x1000, 0, blockOut));
        uint block = GuestMemory.R32(blockOut);
        uint partition = GuestMemory.R32(block + 0x24);
        uint mmuContext = GuestMemory.R32(partition + 0x1C);
        uint page = GuestMemory.R32(block + 0x30);

        uint newPageOut = _harness.Alloc(4);
        Assert.Equal(0u, CallKernel(Internal(AllocVirPageObject), partition, newPageOut));
        uint newPage = GuestMemory.R32(newPageOut);
        Assert.InRange(newPage, GuestMemory.KernelStart, GuestMemory.KernelEnd - 1);
        Assert.Equal(0u, GuestMemory.R32(newPage + 0x10));
        Assert.Equal(0u, CallKernel(Internal(UnmapVirPageWithOpt), partition, block, page, 0));
        uint code = GuestMemory.R32(block + 0x10);
        GuestMemory.W32(block + 0x10, (code & 0xFFFFFF0F) | 0x50);
        Assert.Equal(0u, CallKernel(Internal(MapVirPageCore), 0, partition, mmuContext + 0x0C, block, blockBase + 0x1000, 0x1000, blockBase + 0x1000));
        GuestMemory.W32(block + 0x10, code);
        uint lastPageOut = _harness.Alloc(4);
        Assert.Equal(0u, CallKernel(Internal(VirPageAllocPhyPage), page, partition, GuestMemory.R32(block + 0x28), lastPageOut));
        Assert.Equal(page, GuestMemory.R32(lastPageOut));
        Assert.Equal(0u, CallKernel(Internal(FreeVirPageObject), partition, GuestMemory.R32(mmuContext + 0x1C), newPage));
        Assert.Equal(0u, CallKernel(Internal(PhyMemPartFreePhyPage), 0, 0));

        Assert.Equal(2u, CallKernel(blockBase + 0x1000));
    }

    /// <summary>
    /// A hook by offset on an internal function of a firmware module VitaSharp runs in HLE (Adrenaline 8 hooks the FW
    /// 3.74 SceCompat at +0x2AE8, its secure-RAM access setup, 24/11/2026): the code segment holds trap fill, not
    /// code to move, and the framework refused it with TAI_ERROR_HOOK_ERROR. The hook now installs: a call at the
    /// offset runs the hook, the hook's continue runs the function's HLE, and the release puts the function back. Thumb
    /// and ARM entries.
    /// </summary>
    [Theory]
    [InlineData(0x200u, 1)]
    [InlineData(0x300u, 0)]
    public void AnOffsetHookOnAnHleFirmwareFunctionRunsTheHookAndItsContinue(uint offset, int thumb)
    {
        uint entry = offset | (uint)thumb;
        _harness.Emu.FirmwareKernel.RegisterInternal(Sysmem374ModuleNid, entry, (_, arguments) => arguments[0] + 0x100);
        (_, uint blockBase) = CodeBlock(0x55);
        uint hookFunction = blockBase + 0x1000;
        LoadedModule shell = _harness.Emu.FirmwareKernel.Modules.Single(m => m.Name == "SceSysmem");
        uint hookRef = _harness.Word(0);
        PluginFramework framework = _harness.Module<PluginFramework>();
        Assert.Equal(0x105u, CallKernel(Internal(entry), 5));

        int uid = Call(framework, "HookFunctionOffsetForKernel", KernelSpace.KernelPid, hookRef, shell.Uid, 0, offset, thumb, hookFunction);

        Assert.True(uid > 0, $"offset hook returned 0x{uid:X8}");
        Assert.Equal(0x55u, CallKernel(Internal(entry), 5));
        Assert.Equal(0x106u, CallKernel(GuestMemory.R32(GuestMemory.R32(hookRef) + 8), 6)); // the hook's continue
        Assert.Equal(0, Call(framework, "HookReleaseForKernel", uid, GuestMemory.R32(hookRef)));
        Assert.Equal(0x107u, CallKernel(Internal(entry), 7));
    }
}
