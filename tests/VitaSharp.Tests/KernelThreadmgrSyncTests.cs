using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;
using Mutex = VitaSharp.Core.Kernel.Mutex;
using Semaphore = VitaSharp.Core.Kernel.Semaphore;

namespace VitaSharp.Tests;

/// <summary>
/// The SceThreadmgrForDriver/ForKernel entries of FW 3.74 threadmgr (decompiled, study only) that wrap the user
/// synchronisation calls with their own checks: the deletes that check the class, the waits and creates on the
/// caller's registers, the cancels, the timers, the multi-event wait's limits and the event class's pattern words.
/// </summary>
[Collection("guest")]
public sealed unsafe class KernelThreadmgrSyncTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int IllegalContext = unchecked((int)0x80027101), DifferentClass = unchecked((int)0x80028002),
        InvalidArgument = unchecked((int)0x80020005), IllegalAddr = unchecked((int)0x80020006),
        IllegalSize = unchecked((int)0x8002000B), IllegalMode = unchecked((int)0x80020010),
        IllegalPattern = unchecked((int)0x8002000D), UnknownSimpleEvent = unchecked((int)0x80028261),
        IllegalType = unchecked((int)0x8002000C);

    private readonly HleHarness _harness = new(fixture, "kthrsync");

    public void Dispose() => _harness.Dispose();

    private ThreadmgrKernel Threadmgr => _harness.Module<ThreadmgrKernel>();

    private ThreadMgr User => _harness.Module<ThreadMgr>();

    /// <summary>Runs an entry taking <see cref="HleContext"/> with <paramref name="args"/> in r0-r3 and answers r0.</summary>
    private static int Run(GuestThread thread, object module, string name, params uint[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            thread.Hle.Cpu->R[i] = args[i];
        }

        Invoke(module, name, thread.Hle);
        return (int)thread.Hle.Cpu->R[0];
    }

    [Fact]
    public void TheDeletesCheckTheClassFirst()
    {
        int sema = Call(User, "sceKernelCreateSema", "s", 0u, 1, 1, 0u);
        int mutex = Call(User, "sceKernelCreateMutex", "m", 0u, 0, 0u);
        int rwlock = Call(User, "sceKernelCreateRWLock", "r", 0u, 0u);
        _harness.OnGuest(t =>
        {
            Assert.Equal(DifferentClass, Run(t, Threadmgr, "NID_A5ACE750", (uint)mutex));
            Assert.Equal(DifferentClass, Run(t, Threadmgr, "NID_CA760728", (uint)sema));
            Assert.Equal(DifferentClass, Run(t, Threadmgr, "NID_32452017", (uint)sema));
            Assert.Equal(0, Run(t, Threadmgr, "NID_A5ACE750", (uint)sema));
            Assert.Equal(0, Run(t, Threadmgr, "NID_CA760728", (uint)mutex));
            Assert.Equal(0, Run(t, Threadmgr, "NID_32452017", (uint)rwlock));
        });
        Assert.Null(_harness.Emu.Kernel.Objects.Get<KernelObject>(sema));
        Assert.Null(_harness.Emu.Kernel.Objects.Get<KernelObject>(mutex));
        Assert.Null(_harness.Emu.Kernel.Objects.Get<KernelObject>(rwlock));
    }

    [Fact]
    public void ARwLockDeleteNeedsACurrentThread()
    {
        int rwlock = Call(User, "sceKernelCreateRWLock", "r", 0u, 0u);
        GuestThread idle = _harness.Emu.Kernel.CreateThread("idle", 0, 0x10000100, 0x4000, 0, 0x70000);
        Assert.Equal(IllegalContext, Run(idle, Threadmgr, "NID_32452017", (uint)rwlock));
    }

    [Fact]
    public void TheSemaphoreEntriesCreateWaitSignalAndCancel()
    {
        uint name = _harness.Str("ksema");
        uint waiting = _harness.Alloc(4);
        _harness.OnGuest(t =>
        {
            int uid = Run(t, Threadmgr, "NID_F9364623", name, 0u, 2, 2);
            Assert.True(uid > 0);
            Assert.Equal(0, Run(t, Threadmgr, "NID_EFD7439D", (uint)uid, 2u, 0u));
            Assert.Equal(0, Run(t, Threadmgr, "NID_21B25367", (uint)uid, 1u));
            Assert.Equal(0, Run(t, Threadmgr, "NID_8760C8ED", (uint)uid, 2u, waiting));
            Assert.Equal(0u, GuestMemory.R32(waiting));
            Assert.Equal(2, _harness.Emu.Kernel.Objects.Get<Semaphore>(uid)!.Count);
        });
    }

    [Fact]
    public void TheMutexEntriesLockAndUnlock()
    {
        uint name = _harness.Str("kmutex");
        _harness.OnGuest(t =>
        {
            int uid = Run(t, Threadmgr, "NID_1E4FF787", name, 0u, 0u, 0u);
            Assert.True(uid > 0);
            Assert.Equal(0, Run(t, Threadmgr, "NID_1059CCDD", (uint)uid, 1u, 0u));
            Assert.Same(t, _harness.Emu.Kernel.Objects.Get<Mutex>(uid)!.Owner);
            Assert.Equal(0, Run(t, Threadmgr, "NID_59D924C1", (uint)uid, 1u));
            Assert.Null(_harness.Emu.Kernel.Objects.Get<Mutex>(uid)!.Owner);
        });
    }

    [Fact]
    public void TheRwLockEntriesTakeTheLockForReadingAndWriting()
    {
        int uid = Call(User, "sceKernelCreateRWLock", "r", 0u, 0u);
        _harness.OnGuest(t =>
        {
            Assert.Equal(0, Run(t, Threadmgr, "NID_3F5E1D56", (uint)uid, 0u));
            Assert.Equal(1, _harness.Emu.Kernel.Objects.Get<RwLock>(uid)!.LockCount);
            Assert.Equal(0, Call(User, "sceKernelUnlockReadRWLock", uid));
            Assert.Equal(0, Run(t, Threadmgr, "NID_A64B00F6", (uint)uid, 0u));
            Assert.Same(t, _harness.Emu.Kernel.Objects.Get<RwLock>(uid)!.Writer);
        });
    }

    [Fact]
    public void TheTimerEntriesCheckTheirArguments()
    {
        uint name = _harness.Str("ktimer");
        uint time = _harness.Alloc(8);
        _harness.OnGuest(t =>
        {
            int uid = Run(t, Threadmgr, "NID_90656C98", name, 0u, 0u);
            Assert.True(uid > 0);
            Assert.Equal(IllegalAddr, Run(t, Threadmgr, "NID_CB795E13", (uint)uid, 0u));
            GuestMemory.Write(time, 1234UL);
            Assert.Equal(0, Run(t, Threadmgr, "NID_D978D16F", (uint)uid, time));
            Assert.Equal(0, Run(t, Threadmgr, "NID_CB795E13", (uint)uid, time));
            Assert.Equal(1234UL, GuestMemory.Read<ulong>(time));
            Assert.Equal(IllegalType, Run(t, Threadmgr, "NID_92A3DCDD", (uint)uid, 2u, time, 0u));
            Assert.Equal(IllegalAddr, Run(t, Threadmgr, "NID_92A3DCDD", (uint)uid, 1u, 0u, 0u));
        });
    }

    [Fact]
    public void TheMultipleEventWaitChecksCountAndMode()
    {
        uint events = _harness.Alloc(0x10);
        _harness.OnGuest(t =>
        {
            Assert.Equal(IllegalAddr, Run(t, Threadmgr, "NID_36F8E58A", 0u, 1u, 1u, 0u));
            Assert.Equal(IllegalSize, Run(t, Threadmgr, "NID_36F8E58A", events, 0x41u, 1u, 0u));
            Assert.Equal(IllegalMode, Run(t, Threadmgr, "NID_8C48E53B", events, 1u, 4u, 0u));
        });
    }

    [Fact]
    public void TheEventPatternIsReadSetQuietlyAndSet()
    {
        uint name = _harness.Str("kevent");
        int uid = Call(User, "sceKernelCreateSimpleEvent", name, 0u, 0x01000000u, 0u);
        Assert.True(uid > 0);
        uint pattern = _harness.Alloc(4);
        uint result = _harness.Alloc(4);
        uint userData = _harness.Alloc(8);
        Assert.Equal(0, Call(Threadmgr, "NID_0C6BCB8C", uid, pattern));
        Assert.Equal(0x01000004u, GuestMemory.R32(pattern)); // the user bits and the created state
        Assert.Equal(IllegalPattern, Call(Threadmgr, "NID_25AB8F6B", uid, 0u, 0u, 0u, 0x10000u));
        Assert.Equal(0, Call(Threadmgr, "NID_25AB8F6B", uid, 0u, 0x55u, 0x66u, 0x06000000u));
        Assert.Equal(0, Call(Threadmgr, "NID_0C6BCB8C", uid, pattern));
        Assert.Equal(0x06000000u, GuestMemory.R32(pattern));
        Assert.Equal(0, Call(Threadmgr, "NID_B7DF3EDF", uid, 0x08000000u, 0x77u, 0u, 0u));
        _harness.OnGuest(t =>
        {
            Assert.Equal(InvalidArgument, Run(t, Threadmgr, "NID_D08C71C6", (uint)uid, 0u, result, userData));
            Assert.Equal(0, Run(t, Threadmgr, "NID_D08C71C6", (uint)uid, 0x08000000u, result, userData));
            Assert.Equal(0x08000000u, GuestMemory.R32(result));
            Assert.Equal(0x77UL, GuestMemory.Read<ulong>(userData));
            Assert.Equal(UnknownSimpleEvent, Run(t, Threadmgr, "NID_2BDE3B40", 0x7FFF0001u));
            Assert.Equal(0, Run(t, Threadmgr, "NID_2BDE3B40", (uint)uid));
        });
        Assert.Null(_harness.Emu.Kernel.Objects.Get<KernelObject>(uid));
    }
}
