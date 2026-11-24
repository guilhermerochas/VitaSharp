using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// The process object of FW 3.74 processmgr (decompiled, study only) and the SceProcessmgrForDriver/ForKernel calls on
/// its words: the block at the firmware's layout (state, UID, main thread, SELF auth info), the process lookup (0 the
/// current thread's, 0x10005 the kernel's, no process INVALID_PID), the plain words read back, the checks of the
/// setters, the debugging-console words behind the dipsw bit, the operations table and the process heaps.
/// </summary>
[Collection("guest")]
public sealed class KernelProcessObjectTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int InvalidArgument = unchecked((int)0x80020005), IllegalAddr = unchecked((int)0x80020006),
        InvalidPid = unchecked((int)0x80029001), NotAllowed = unchecked((int)0x80029008),
        NotImplemented = unchecked((int)0x80020002), Unsupported = unchecked((int)0x80020004);

    private const int KernelPid = 0x10005;

    private readonly HleHarness _harness = new(fixture, "kprocobj");

    public void Dispose() => _harness.Dispose();

    private ProcessmgrKernel Processmgr => _harness.Module<ProcessmgrKernel>();

    private int Pid => _harness.Emu.Pid;

    [Fact]
    public void TheProcessObjectHasTheFirmwareLayoutAndStaysTheSame()
    {
        uint block = (uint)Invoke(Processmgr, "ksceKernelGetProcessKernelBuf", Pid)!;
        Assert.NotEqual(0u, block);
        Assert.Equal(block, (uint)Invoke(Processmgr, "ksceKernelGetProcessKernelBuf", Pid)!);
        Assert.Equal(2u, GuestMemory.R32(block + 0x3C) & 0xFF);
        Assert.Equal((uint)Pid, GuestMemory.R32(block + 0x64));
        Assert.Equal(_harness.Emu.Access.AuthorityId, GuestMemory.Read<ulong>(block + 0xB8));
        Assert.NotEqual(block, (uint)Invoke(Processmgr, "ksceKernelGetProcessKernelBuf", KernelPid)!);
        Assert.Equal(0u, (uint)Invoke(Processmgr, "ksceKernelGetProcessKernelBuf", 0x7FFF0001)!);
        // pid 0 is the current thread's process: none on a host thread
        Assert.Equal(0u, (uint)Invoke(Processmgr, "ksceKernelGetProcessKernelBuf", 0)!);
        _harness.OnGuest(_ => Assert.Equal(block, (uint)Invoke(Processmgr, "ksceKernelGetProcessKernelBuf", 0)!));

        uint info = _harness.Alloc(0x90);
        Assert.Equal(0, Call(Processmgr, "ksceKernelGetProcessSelfAuthInfo", Pid, info));
        Assert.Equal(_harness.Emu.Access.AuthorityId, GuestMemory.Read<ulong>(info));
        Assert.Equal(InvalidPid, Call(Processmgr, "ksceKernelGetProcessSelfAuthInfo", 0x7FFF0001, info));
    }

    /// <summary>The kernel's process object carries the kernel's SELF auth info (processmgr 3.74 copies its 0x90
    /// bytes at the start: program authority 0x2808000000000001, the kernel's capability) and the access level 0x80:
    /// ksceKernelGetProcessSelfAuthInfo for the kernel, and for pid 0 from a kernel thread, gives them. It gave zeros
    /// (no authority).</summary>
    [Fact]
    public void TheKernelsProcessObjectHasTheKernelsAuthInfo()
    {
        uint block = (uint)Invoke(Processmgr, "ksceKernelGetProcessKernelBuf", KernelPid)!;
        Assert.Equal(0x5000000u, GuestMemory.R32(block + 0x40));
        Assert.Equal(0x80u, GuestMemory.R32(block + 0xA0));
        uint info = _harness.Alloc(0x90);
        Assert.Equal(0, Call(Processmgr, "ksceKernelGetProcessSelfAuthInfo", KernelPid, info));
        Assert.Equal(SysmemKernel.KernelSelfAuthInfo.ToArray(), GuestMemory.Span(info, 0x90).ToArray());
        Assert.Equal(0x2808000000000001UL, GuestMemory.Read<ulong>(info));
        _harness.OnGuest(t =>
        {
            GuestMemory.Fill(info, 0, 0x90);
            t.KernelMode = true;
            try
            {
                Assert.Equal(0, Call(Processmgr, "ksceKernelGetProcessSelfAuthInfo", 0, info));
            }
            finally
            {
                t.KernelMode = false;
            }

            Assert.Equal(0x2808000000000001UL, GuestMemory.Read<ulong>(info));
        });
    }

    [Fact]
    public void TheStateByteIsSetOnlyWithBit4AndBit20ReadsApart()
    {
        uint status = _harness.Alloc(4);
        Assert.Equal(0, Call(Processmgr, "ksceKernelGetProcessStatus", Pid, status));
        Assert.Equal(2u, GuestMemory.R32(status));
        Assert.Equal(InvalidArgument, Call(Processmgr, "NID_1D0F3185", Pid, 0x0Fu));
        Assert.Equal(0, Call(Processmgr, "NID_1D0F3185", Pid, 0x13u));
        Assert.Equal(0, Call(Processmgr, "ksceKernelGetProcessStatus", Pid, status));
        Assert.Equal(0x13u, GuestMemory.R32(status));
        Assert.Equal(InvalidPid, Call(Processmgr, "ksceKernelGetProcessStatus", 0x7FFF0001, status));
        Assert.Equal(0, Call(Processmgr, "NID_0CC5B30C", Pid));
        uint block = (uint)Invoke(Processmgr, "ksceKernelGetProcessKernelBuf", Pid)!;
        GuestMemory.W32(block + 0x3C, GuestMemory.R32(block + 0x3C) | 0x100000);
        Assert.Equal(1, Call(Processmgr, "NID_0CC5B30C", Pid));
    }

    [Fact]
    public void PlainWordsAreReadBackAndTheSettersCheckTheirValues()
    {
        Assert.Equal(0, Call(Processmgr, "NID_72F25C14", Pid, 0x81001000u));
        Assert.Equal(0x81001000u, (uint)Invoke(Processmgr, "ksceKernelGetProcessModuleInfo", Pid)!);
        Assert.Equal(0u, (uint)Invoke(Processmgr, "ksceKernelGetProcessModuleInfo", 0x7FFF0001)!);
        Assert.Equal(0, Call(Processmgr, "NID_00DBA872", Pid, 0x55u));
        Assert.Equal(0x55, Call(Processmgr, "NID_E5097B4F", Pid));
        Assert.Equal(unchecked((int)0x80022004), Call(Processmgr, "NID_F013B7F1", Pid, 0x10u));
        Assert.Equal(unchecked((int)0x80022004), Call(Processmgr, "NID_F013B7F1", Pid, 0x30003u));
        Assert.Equal(0, Call(Processmgr, "NID_F013B7F1", Pid, 0x30000u));
        uint block = (uint)Invoke(Processmgr, "ksceKernelGetProcessKernelBuf", Pid)!;
        Assert.Equal(3u, GuestMemory.R32(block + 0x54));
        Assert.Equal(0, Call(Processmgr, "NID_F013B7F1", Pid, 0x7u));
        Assert.Equal(7u, GuestMemory.R32(block + 0x54));
        GuestMemory.W32(block + 0x5C, 0x5C5C);
        GuestMemory.W32(block + 0x68, 0x6868);
        GuestMemory.W32(block + 0x40, 0x4040);
        GuestMemory.W32(block + 0x148, 0x1480);
        GuestMemory.Write<ushort>(block + 0x408, 0x1234);
        GuestMemory.Write<ushort>(block + 0x40A, 0x2);
        Assert.Equal(0x5C5C, Call(Processmgr, "NID_9BC44974", Pid));
        Assert.Equal(0x6868, Call(Processmgr, "NID_A5E55217", Pid));
        Assert.Equal(0x4040, Call(Processmgr, "NID_B631C93B", Pid));
        uint output = _harness.Alloc(4);
        Assert.Equal(0, Call(Processmgr, "NID_D141C076", Pid, output));
        Assert.Equal(0x1480u, GuestMemory.R32(output));
        Assert.Equal(InvalidArgument, Call(Processmgr, "NID_E1A67C86", Pid, 0u));
        Assert.Equal(0, Call(Processmgr, "NID_E1A67C86", Pid, output));
        Assert.Equal(0x1234u, GuestMemory.R32(output));
        Assert.Equal(0, Call(Processmgr, "NID_879153D9", Pid, 0x10000u));
        Assert.Equal(1, Call(Processmgr, "NID_879153D9", Pid, 0x20000u));
        Assert.Equal(InvalidArgument, Call(Processmgr, "NID_879153D9", Pid, 0x30000u));
        Assert.Equal(0, Call(Processmgr, "NID_A1CF74FA", Pid));
        GuestMemory.W32(block + 0x3F0, 9);
        Assert.Equal(1, Call(Processmgr, "NID_A1CF74FA", Pid));
        _harness.OnGuest(_ =>
        {
            Assert.Equal(0, Call(Processmgr, "NID_24FA97A5", 0, 0xABCDu));
            Assert.Equal(0, Call(Processmgr, "NID_54D469E2", output));
            Assert.Equal(0xABCDu, GuestMemory.R32(output));
        });
    }

    [Fact]
    public void TheMainThreadComesFromTheProcess()
    {
        GuestThread main = _harness.Emu.Kernel.CreateThread("main-like", 0, 0x10000100, 0x4000, 0, 0x70000);
        GuestThread? earlier = _harness.Emu.Kernel.MainThread;
        _harness.Emu.Kernel.MainThread = main;
        try
        {
            Assert.Equal(main.Uid, Call(Processmgr, "ksceKernelGetProcessMainThread", Pid));
        }
        finally
        {
            _harness.Emu.Kernel.MainThread = earlier;
        }

        Assert.Equal(InvalidPid, Call(Processmgr, "ksceKernelGetProcessMainThread", 0x7FFF0001));
    }

    [Fact]
    public void DebuggingConsoleWordsNeedTheDipswBit()
    {
        uint first = _harness.Alloc(4);
        uint second = _harness.Alloc(4);
        Assert.Equal(NotAllowed, Call(Processmgr, "NID_B1C3EFCA", Pid, 1u));
        Assert.Equal(NotAllowed, Call(Processmgr, "NID_1C70501E", Pid));
        Assert.Equal(NotAllowed, Call(Processmgr, "NID_1197DB8A", 0u, 1u, 2u));
        Assert.Equal(NotAllowed, Call(Processmgr, "NID_9FA66773", 0u, first, second));
        Invoke(_harness.Module<SystemServicesVsh>(), "ksceKernelSetDipsw", 0xE4u);
        Assert.Equal(0, Call(Processmgr, "NID_B1C3EFCA", Pid, 0x28Cu));
        Assert.Equal(0x28C, Call(Processmgr, "NID_1C70501E", Pid));
        Assert.Equal(0, Call(Processmgr, "NID_61B9B6FA", Pid, 0x290u));
        Assert.Equal(0x290, Call(Processmgr, "NID_594319C7", Pid));
        Assert.Equal(0, Call(Processmgr, "NID_1197DB8A", 4u, 0x11u, 0x22u));
        Assert.Equal(InvalidArgument, Call(Processmgr, "NID_1197DB8A", 5u, 0x11u, 0x22u));
        Assert.Equal(IllegalAddr, Call(Processmgr, "NID_6DEC45C3", 4u, 0u, second));
        Assert.Equal(0, Call(Processmgr, "NID_6DEC45C3", 4u, first, second));
        Assert.Equal(0x11u, GuestMemory.R32(first));
        Assert.Equal(0x22u, GuestMemory.R32(second));
        Assert.Equal(0, Call(Processmgr, "NID_DDE282CF", 3u, 0x33u, 0x44u));
        Assert.Equal(InvalidArgument, Call(Processmgr, "NID_DDE282CF", 4u, 0x33u, 0x44u));
        Assert.Equal(0, Call(Processmgr, "NID_9FA66773", 3u, first, second));
        Assert.Equal(0x33u, GuestMemory.R32(first));
        Assert.Equal(0x44u, GuestMemory.R32(second));
    }

    [Fact]
    public void TheOperationsTableIsRegisteredOnceAndAnEmptySlotIsNotImplemented()
    {
        Assert.Equal(NotImplemented, Call(Processmgr, "NID_D231AE5B"));
        uint table = _harness.Alloc(0x40);
        uint other = _harness.Alloc(0x40);
        GuestMemory.W32(other + 0x1C, 0x81000001);
        Assert.Equal(0, Call(Processmgr, "NID_4140D633", table));
        Assert.Equal(0, Call(Processmgr, "NID_4140D633", other)); // the first stays
        _harness.OnGuest(_ =>
        {
            Assert.Equal(NotImplemented, Call(Processmgr, "NID_D231AE5B"));
            Assert.Equal(NotImplemented, Call(Processmgr, "NID_3EBC93BB"));
        });
        Assert.Equal(InvalidArgument, Call(Processmgr, "NID_5E882B60", 0u));
        Assert.Equal(0, Call(Processmgr, "NID_5E882B60", 0x1234u));
        Assert.Equal(0x1234u, (uint)Invoke(Processmgr, "get_Word64C")!);
    }

    [Fact]
    public void TheProcessHeapsGiveAndTakeBlocks()
    {
        _harness.OnGuest(_ =>
        {
            uint user = (uint)Invoke(Processmgr, "NID_5468892A", 0x40u, 0u)!;
            Assert.NotEqual(0u, user);
            Assert.Equal(0, Call(Processmgr, "NID_FC2A424E", user));
            uint kernel = (uint)Invoke(Processmgr, "NID_3B33FFBA", 0x40u, 0u)!;
            Assert.NotEqual(0u, kernel);
            Assert.Equal(0, Call(Processmgr, "NID_11BEFBBE", kernel));
            Assert.Equal(IllegalAddr, Call(Processmgr, "NID_11BEFBBE", kernel));
        });
        uint forPid = (uint)Invoke(Processmgr, "NID_8A85FA28", Pid, 0x20u, 0u)!;
        Assert.NotEqual(0u, forPid);
        Assert.Equal(0u, (uint)Invoke(Processmgr, "NID_8A85FA28", 0x7FFF0001, 0x20u, 0u)!);
        Assert.Equal(0, Call(Processmgr, "NID_DD0A58D1", Pid, forPid));
        Assert.Equal(0u, (uint)Invoke(Processmgr, "NID_51AF6FE6", Pid, 0x20u)!);
        Assert.Equal(Unsupported, Call(Processmgr, "NID_2755CEDD", Pid, 0u));
    }
}
