using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// The debugger's thread control of FW 3.74 threadmgr (decompiled, study only): suspend reasons added and taken away
/// (the first stops the thread, the last lets it run), their checks, the status word, and the CPU and VFP registers of
/// a suspended thread read and written by mask. The thread is one that never ran, so nothing depends on when a running
/// thread reaches its next block.
/// </summary>
[Collection("guest")]
public sealed class KernelThreadDebuggerTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int InvalidArgument = unchecked((int)0x80020005), IllegalAddr = unchecked((int)0x80020006),
        IllegalThreadId = unchecked((int)0x80028022), UnknownThreadId = unchecked((int)0x80028021),
        AlreadySuspended = unchecked((int)0x8002802F), NotSuspended = unchecked((int)0x80028030);

    private readonly HleHarness _harness = new(fixture, "kthrdebug");

    public void Dispose() => _harness.Dispose();

    private ThreadmgrKernel Threadmgr => _harness.Module<ThreadmgrKernel>();

    private GuestThread Idle() => _harness.Emu.Kernel.CreateThread("debuggee", 0, 0x10000100, 0x4000, 0, 0x70000);

    [Fact]
    public void SuspendReasonsAddUpAndTheLastOneTakenAwayResumes()
    {
        GuestThread thread = Idle();
        Assert.Equal(InvalidArgument, Call(Threadmgr, "ksceKernelDebugSuspendThread", thread.Uid, 0x4u));
        Assert.Equal(IllegalThreadId, Call(Threadmgr, "ksceKernelDebugSuspendThread", 0, 0x100u));
        Assert.Equal(UnknownThreadId, Call(Threadmgr, "ksceKernelDebugSuspendThread", 0x7FFF0001, 0x100u));
        _harness.OnGuest(t => Assert.Equal(IllegalThreadId, Call(Threadmgr, "ksceKernelDebugSuspendThread", t.Uid, 0x100u)));
        Assert.Equal(NotSuspended, Call(Threadmgr, "ksceKernelDebugResumeThread", thread.Uid, 0x100u));

        Assert.Equal(0, Call(Threadmgr, "ksceKernelDebugSuspendThread", thread.Uid, 0x100u));
        Assert.Equal(1, thread.SuspendCount);
        Assert.Equal(0x100, Call(Threadmgr, "ksceKernelIsThreadDebugSuspended", thread.Uid));
        Assert.Equal(AlreadySuspended, Call(Threadmgr, "ksceKernelDebugSuspendThread", thread.Uid, 0x100u));
        Assert.Equal(0, Call(Threadmgr, "ksceKernelDebugSuspendThread", thread.Uid, 0x200u));
        Assert.Equal(0x300, Call(Threadmgr, "ksceKernelIsThreadDebugSuspended", thread.Uid));
        Assert.Equal(1, thread.SuspendCount);

        Assert.Equal(0, Call(Threadmgr, "ksceKernelDebugResumeThread", thread.Uid, 0x100u));
        Assert.Equal(1, thread.SuspendCount);
        Assert.Equal(0, Call(Threadmgr, "ksceKernelDebugResumeThread", thread.Uid, 0x200u));
        Assert.Equal(0, thread.SuspendCount);
        Assert.Equal(0, Call(Threadmgr, "ksceKernelIsThreadDebugSuspended", thread.Uid));
        Assert.Equal(NotSuspended, Call(Threadmgr, "ksceKernelDebugResumeThread", thread.Uid, 0x200u));
        Assert.Equal(IllegalThreadId, Call(Threadmgr, "ksceKernelIsThreadDebugSuspended", 0));
    }

    [Fact]
    public void TheStatusWordChangesOnlyOnASuspendedThread()
    {
        GuestThread thread = Idle();
        Assert.Equal(NotSuspended, Call(Threadmgr, "ksceKernelChangeThreadSuspendStatus", thread.Uid, 0x100u));
        Assert.Equal(InvalidArgument, Call(Threadmgr, "ksceKernelChangeThreadSuspendStatus", thread.Uid, 0x4u));
        Assert.Equal(0, Call(Threadmgr, "ksceKernelDebugSuspendThread", thread.Uid, 0x100u));
        Assert.Equal(0x100, Call(Threadmgr, "ksceKernelChangeThreadSuspendStatus", thread.Uid, 0x2u));
        Assert.Equal(0x2, Call(Threadmgr, "ksceKernelIsThreadDebugSuspended", thread.Uid));
        Assert.Equal(0, Call(Threadmgr, "ksceKernelDebugResumeThread", thread.Uid, 0x2u));
        Assert.Equal(0, thread.SuspendCount);
    }

    [Fact]
    public unsafe void TheRegistersOfASuspendedThreadAreReadAndWrittenByMask()
    {
        GuestThread thread = Idle();
        uint registers = _harness.Alloc(0x90);
        uint vfp = _harness.Alloc(0x100);
        Assert.Equal(NotSuspended, Call(Threadmgr, "ksceKernelGetThreadCpuRegisters", thread.Uid, registers));
        Assert.Equal(0, Call(Threadmgr, "ksceKernelDebugSuspendThread", thread.Uid, 0x100u));
        Assert.Equal(IllegalAddr, Call(Threadmgr, "ksceKernelGetThreadCpuRegisters", thread.Uid, 0u));
        for (int i = 0; i < 16; i++)
        {
            thread.Core.State->R[i] = 0x1000u + (uint)i;
        }

        thread.Core.State->TpidrUrw = 0xABCD;
        Assert.Equal(0, Call(Threadmgr, "ksceKernelGetThreadCpuRegisters", thread.Uid, registers));
        Assert.Equal(0x1003u, GuestMemory.R32(registers + 3 * 4));
        Assert.Equal(0x100Fu, GuestMemory.R32(registers + 0x3C));
        Assert.Equal(0xABCDu, GuestMemory.R32(registers + 0x44));
        Assert.Equal(0, Call(Threadmgr, "NID_C4735A7E", thread.Uid, registers));
        Assert.Equal(0x1003u, GuestMemory.R32(registers + 3 * 4));

        // r2 and pc change, r3 does not
        GuestMemory.W32(registers + 2 * 4, 0x2222);
        GuestMemory.W32(registers + 3 * 4, 0x3333);
        GuestMemory.W32(registers + 0x3C, 0x81000100);
        Assert.Equal(0, Call(Threadmgr, "NID_64E89DE9", thread.Uid, registers, (1u << 2) | (1u << 15)));
        Assert.Equal(0x2222u, thread.Core.State->R[2]);
        Assert.Equal(0x1003u, thread.Core.State->R[3]);
        Assert.Equal(0x81000100u, thread.Core.State->R[15]);

        GuestMemory.Fill(vfp, 0x5A, 0x100);
        Assert.Equal(0, Call(Threadmgr, "NID_49A0B679", thread.Uid, vfp, 1u << 3));
        byte* d = (byte*)thread.Core.State->D;
        Assert.Equal(0x5A, d[3 * 16]);
        Assert.NotEqual(0x5A, d[4 * 16]);
        GuestMemory.Fill(vfp, 0, 0x100);
        Assert.Equal(0, Call(Threadmgr, "ksceKernelGetVfpRegisterForDebugger", thread.Uid, vfp));
        Assert.Equal(0x5A, GuestMemory.Read<byte>(vfp + 3 * 16 + 15));
        Assert.Equal(0, Call(Threadmgr, "NID_444CE648", thread.Uid, vfp));
        Assert.Equal(0, Call(Threadmgr, "NID_AC726660", thread.Uid, vfp, 0u));
        Assert.Equal(0, Call(Threadmgr, "NID_5075AE5E", thread.Uid, registers, 0u));
        Assert.Equal(0, Call(Threadmgr, "ksceKernelDebugResumeThread", thread.Uid, 0x100u));
    }
}
