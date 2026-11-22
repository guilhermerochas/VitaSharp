using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// The FW 3.74 sysmem.skprx calls that read the running thread's control block and sysmem's own context (studied in
/// the decompiled module): the syscall frame at control block +0x44 and its state word (SceSysrootForDriver), the
/// domain access check sceKernelOpenVMDomain changes, the tool partition and the boot's physical regions of sysmem's
/// context (module private word 3), its word +0x8C and the coarse page table walk of an MMU context
/// (SceSysmemForKernel/ForDriver).
/// </summary>
[Collection("guest")]
public sealed class KernelSysmemContextTests(GuestMemoryFixture fixture) : IDisposable
{
    private const uint InvalidArgument = 0x80020005, NoMemoryRegion = 0x80024300, IllegalContext = 0x80027101;
    private const uint GetSyscallFrame = 0x44EA3197, CurrentControlBlockPair = 0x6C1F092F;
    private const uint SetStateValue = 0x8CD02748;

    private static readonly uint[] s_stateSetters = [0xD86BD6DC, 0x8B8F1D01, 0x6E0BC27C, 0x2A5DBD38, 0x8A760856];

    private static readonly uint[] s_sysmemNids = [0x0B1B4ACA, 0x456ECB54, 0x4E6D8BC3, 0x8FD11E02];

    private readonly HleHarness _harness = new(fixture, "ksysmemctx");

    public void Dispose() => _harness.Dispose();

    private SysMem UserSysMem => _harness.Module<SysMem>();

    private unsafe uint R0(GuestThread thread, uint nid, params uint[] args)
    {
        Action<HleContext> implementation = _harness.Emu.Hle.ImplOf(nid) ?? throw new InvalidOperationException($"0x{nid:X8} not implemented");
        for (int i = 0; i < 4; i++)
        {
            thread.Core.State->R[i] = i < args.Length ? args[i] : 0xA5A5A5A5;
        }

        implementation(thread.Hle);
        return thread.Core.State->R[0];
    }

    private uint ControlBlock(GuestThread thread)
    {
        uint pair = _harness.Alloc(8);
        Assert.Equal(0u, R0(thread, CurrentControlBlockPair, pair));
        uint controlBlock = GuestMemory.R32(pair);
        Assert.NotEqual(0u, controlBlock);
        return controlBlock;
    }

    [Fact]
    public void EveryExportIsARealImplementation()
    {
        var bindings = HleBindings.Modules.SelectMany(m => m.Bindings()).ToList();
        foreach (uint nid in s_stateSetters.Append(GetSyscallFrame).Append(SetStateValue))
        {
            Assert.False(bindings.Single(b => b.Nid == nid && b.Library == "SceSysrootForDriver").Default);
        }

        foreach (uint nid in s_sysmemNids)
        {
            Assert.False(bindings.Single(b => b.Nid == nid && b.Library == "SceSysmemForKernel").Default);
        }

        Assert.False(bindings.Single(b => b.Nid == 0x89475192 && b.Library == "SceSysmemForDriver").Default);
    }

    /// <summary>Outside a syscall the control block has no frame: the getter answers 0 and the setters touch nothing.</summary>
    [Fact]
    public void WithoutASyscallFrameTheStateCallsDoNothing()
    {
        _harness.OnGuest(t =>
        {
            uint controlBlock = ControlBlock(t);
            GuestMemory.W32(controlBlock + 0x44, 0);
            Assert.Equal(0u, R0(t, GetSyscallFrame));
            foreach (uint nid in s_stateSetters)
            {
                R0(t, nid);
            }

            R0(t, SetStateValue, 7);
            Assert.Equal(0u, GuestMemory.R32(controlBlock + 0x44));
        });
    }

    /// <summary>Inside a syscall (the entry stored the frame at control block +0x44) the getter answers the frame and
    /// each setter writes its state into the frame's +0x40, the value form with bit 31 set.</summary>
    [Fact]
    public void TheStateCallsWriteTheRunningThreadsSyscallFrame()
    {
        _harness.OnGuest(t =>
        {
            uint controlBlock = ControlBlock(t);
            uint frame = _harness.Alloc(0x60);
            GuestMemory.W32(controlBlock + 0x44, frame);
            try
            {
                Assert.Equal(frame, R0(t, GetSyscallFrame));
                for (uint state = 0; state < s_stateSetters.Length; state++)
                {
                    GuestMemory.W32(frame + 0x40, 0xDEADBEEF);
                    R0(t, s_stateSetters[state]);
                    Assert.Equal(state, GuestMemory.R32(frame + 0x40));
                }

                R0(t, SetStateValue, 0x1234);
                Assert.Equal(0x80001234u, GuestMemory.R32(frame + 0x40));
                Assert.Equal(0u, GuestMemory.R32(frame + 0x3C)); // only the state word changes
                Assert.Equal(0u, GuestMemory.R32(frame + 0x44));
            }
            finally
            {
                GuestMemory.W32(controlBlock + 0x44, 0);
            }
        });
    }

    /// <summary>The frame belongs to the thread: another thread's control block has its own (none here).</summary>
    [Fact]
    public void AnotherThreadDoesNotSeeTheFrame()
    {
        uint frame = _harness.Alloc(0x60);
        uint firstControlBlock = 0;
        _harness.OnGuest(t =>
        {
            firstControlBlock = ControlBlock(t);
            GuestMemory.W32(firstControlBlock + 0x44, frame);
        }, "frame-owner");
        _harness.OnGuest(t =>
        {
            Assert.NotEqual(firstControlBlock, ControlBlock(t));
            Assert.Equal(0u, R0(t, GetSyscallFrame));
            GuestMemory.W32(frame + 0x40, 0x55);
            R0(t, s_stateSetters[3]);
            Assert.Equal(0x55u, GuestMemory.R32(frame + 0x40));
        }, "frame-other");
    }

    /// <summary>NID_4E6D8BC3 is 2 while the VM domain is open (sceKernelOpenVMDomain gives domain 2 manager access),
    /// 0 once it is closed again; without a current thread 0x80027101.</summary>
    [Fact]
    public void TheDomainCheckFollowsTheVmDomain()
    {
        Assert.Equal(unchecked((int)IllegalContext), Call(_harness.Module<SysmemKernel>(), "NID_4E6D8BC3"));
        _harness.OnGuest(t =>
        {
            Assert.Equal(0u, R0(t, 0x4E6D8BC3));
            Assert.Equal(0, UserSysMem.sceKernelOpenVMDomain());
            try
            {
                Assert.Equal(2u, R0(t, 0x4E6D8BC3));
            }
            finally
            {
                Assert.Equal(0, UserSysMem.sceKernelCloseVMDomain());
            }

            Assert.Equal(0u, R0(t, 0x4E6D8BC3));
        });
    }

    /// <summary>Sysmem's context is module private word 3: its +0x10 the kernel's physical partition
    /// (NID_0164D817), +0x14 the tool partition a retail boot leaves 0, +0x8C a word nothing writes.</summary>
    [Fact]
    public void SysmemsContextIsModulePrivateWordThree()
    {
        _harness.OnGuest(t =>
        {
            Assert.Equal(0u, R0(t, 0x0B1B4ACA));
            uint context = R0(t, 0x37EC12BB, 3); // ksceKernelSysrootGetModulePrivate
            Assert.NotEqual(0u, context);
            Assert.Equal(R0(t, 0x0164D817), GuestMemory.R32(context + 0x10));
            Assert.Equal(0u, R0(t, 0x8FD11E02));

            // the words are read from the context, as the firmware does
            GuestMemory.W32(context + 0x8C, 0x1234);
            GuestMemory.W32(context + 0x14, 0x5678);
            try
            {
                Assert.Equal(0x1234u, R0(t, 0x8FD11E02));
                Assert.Equal(0x5678u, R0(t, 0x0B1B4ACA));
                Assert.Equal(context, R0(t, 0x37EC12BB, 3)); // made once
            }
            finally
            {
                GuestMemory.W32(context + 0x8C, 0);
                GuestMemory.W32(context + 0x14, 0);
            }
        });
    }

    /// <summary>Region kind 1 is the first chunk of the kernel's physical partition; 8 and 9 the boot's {page, size}
    /// regions of the context (none: 0x80024300); other kinds 0x80024300; a NULL or wrongly sized info 0x80020005.</summary>
    [Fact]
    public void PhysicalRegionsComeFromTheContext()
    {
        _harness.OnGuest(t =>
        {
            uint info = _harness.Alloc(0xC);
            Assert.Equal(InvalidArgument, R0(t, 0x89475192, 1, 0));
            GuestMemory.W32(info, 0x10);
            Assert.Equal(InvalidArgument, R0(t, 0x89475192, 1, info));
            GuestMemory.W32(info, 0xC);

            Assert.Equal(0u, R0(t, 0x89475192, 1, info));
            uint kernelPartition = R0(t, 0x0164D817);
            uint chunk = GuestMemory.R32(kernelPartition + 0x7C);
            Assert.Equal(GuestMemory.R32(chunk + 4), GuestMemory.R32(info + 4));
            Assert.Equal(GuestMemory.R32(chunk + 8), GuestMemory.R32(info + 8));
            Assert.NotEqual(0u, GuestMemory.R32(info + 8));

            Assert.Equal(NoMemoryRegion, R0(t, 0x89475192, 8, info));
            Assert.Equal(NoMemoryRegion, R0(t, 0x89475192, 9, info));
            Assert.Equal(NoMemoryRegion, R0(t, 0x89475192, 2, info));

            uint context = R0(t, 0x37EC12BB, 3);
            uint region = _harness.Alloc(8);
            GuestMemory.W32(region, 0x40123);
            GuestMemory.W32(region + 4, 0x2000);
            GuestMemory.W32(context + 0x94, region);
            try
            {
                Assert.Equal(0u, R0(t, 0x89475192, 9, info));
                Assert.Equal(0x40123000u, GuestMemory.R32(info + 4));
                Assert.Equal(0x2000u, GuestMemory.R32(info + 8));
                Assert.Equal(NoMemoryRegion, R0(t, 0x89475192, 8, info));
            }
            finally
            {
                GuestMemory.W32(context + 0x94, 0);
            }
        });
    }

    /// <summary>The coarse page table of a section: the first-level descriptor of the MB when it is a page table
    /// (type 1), without the type bits; a section or fault descriptor 0; a context without a table 0.</summary>
    [Fact]
    public void TheCoarseTableWalkReadsTheFirstLevelDescriptor()
    {
        _harness.OnGuest(t =>
        {
            uint context = _harness.Alloc(0x28);
            uint record = _harness.Alloc(0x40);
            GuestMemory.W32(context + 0x1C, record);
            Assert.Equal(0u, R0(t, 0x456ECB54, context, 0x81000000)); // no first-level table yet

            uint table = _harness.Alloc(0x4000);
            GuestMemory.W32(record + 0x10, table);
            GuestMemory.W32(table + 0x810 * 4, 0x40567C01); // a page table descriptor (type 1)
            GuestMemory.W32(table + 0x811 * 4, 0x40600C02); // a section (type 2)
            Assert.Equal(0x40567C00u, R0(t, 0x456ECB54, context, 0x81012345));
            Assert.Equal(0u, R0(t, 0x456ECB54, context, 0x81100000));
            Assert.Equal(0u, R0(t, 0x456ECB54, context, 0x81200000)); // a fault entry
        });
    }
}
