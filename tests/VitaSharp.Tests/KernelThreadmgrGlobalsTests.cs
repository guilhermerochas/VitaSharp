using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// The thread manager entries of FW 3.74 that keep its own data words (the handler slots, the process event handler,
/// the flags and the scheduler probe hooks), the entries that only run another export (spinlocks, message pipe calls on
/// one buffer) and the current-thread accessors, as threadmgr.skprx has them (decompiled, study only).
/// </summary>
[Collection("guest")]
public sealed class KernelThreadmgrGlobalsTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int InvalidArgument = unchecked((int)0x80020005), SlotBusy = unchecked((int)0x80028003),
        IllegalContext = unchecked((int)0x80027101), NotImplemented = unchecked((int)0x80020002),
        UnknownThreadId = unchecked((int)0x80028021);

    private readonly HleHarness _harness = new(fixture, "kthrglobals");

    public void Dispose() => _harness.Dispose();

    private ThreadmgrKernel Threadmgr => _harness.Module<ThreadmgrKernel>();

    private ThreadMgr UserThreadmgr => _harness.Module<ThreadMgr>();

    /// <summary>Runs the HLE of <paramref name="nid"/> on <paramref name="thread"/> with r0-r3 and the stack words
    /// after them; r0.</summary>
    private uint RunNid(GuestThread thread, uint nid, params uint[] args)
    {
        Action<HleContext> implementation = _harness.Emu.Hle.ImplOf(nid) ?? throw new InvalidOperationException($"0x{nid:X8} not bound");
        unsafe
        {
            for (int i = 0; i < 4; i++)
            {
                thread.Core.State->R[i] = i < args.Length ? args[i] : 0;
            }

            uint sp = thread.Core.State->R[13];
            for (int i = 4; i < args.Length; i++)
            {
                GuestMemory.W32(sp + (uint)(i - 4) * 4, args[i]);
            }

            implementation(thread.Hle);
            return thread.Core.State->R[0];
        }
    }

    private uint Globals() => (uint)Invoke(Threadmgr, "NID_19440C83")!;

    [Fact]
    public void DebuggerHandlerSlotsTakeOneHandlerEachAndCheckTheirRange()
    {
        uint globals = Globals();
        Assert.NotEqual(0u, globals);
        Assert.Equal(globals, (uint)Invoke(Threadmgr, "NID_19440C83")!);
        Assert.Equal(0, Call(Threadmgr, "NID_6C2E3A49", 1u, 7u, 0x81001235u));
        Assert.Equal(0x81001235u, GuestMemory.R32(globals + 0x239C + (8 + 7) * 4));
        // a taken slot keeps its handler
        Assert.Equal(SlotBusy, Call(Threadmgr, "NID_6C2E3A49", 1u, 7u, 0x81009999u));
        Assert.Equal(0x81001235u, GuestMemory.R32(globals + 0x239C + (8 + 7) * 4));
        Assert.Equal(InvalidArgument, Call(Threadmgr, "NID_6C2E3A49", 2u, 0u, 1u));
        Assert.Equal(InvalidArgument, Call(Threadmgr, "NID_6C2E3A49", 0u, 8u, 1u));
        Assert.Equal(InvalidArgument, Call(Threadmgr, "NID_47F26712", 0u, 8u));
        Assert.Equal(0, Call(Threadmgr, "NID_47F26712", 1u, 7u));
        Assert.Equal(0u, GuestMemory.R32(globals + 0x239C + (8 + 7) * 4));
        Assert.Equal(0, Call(Threadmgr, "NID_6C2E3A49", 1u, 7u, 0x81009999u));
    }

    [Fact]
    public void EventHandlerSlotsAreThreeCompareAndSetWords()
    {
        uint globals = Globals();
        Assert.Equal(0, Call(Threadmgr, "NID_1378F6EF", 2u, 0x81002001u));
        Assert.Equal(SlotBusy, Call(Threadmgr, "NID_1378F6EF", 2u, 0x81002003u));
        Assert.Equal(0x81002001u, GuestMemory.R32(globals + 0x23DC + 8));
        Assert.Equal(InvalidArgument, Call(Threadmgr, "NID_1378F6EF", 3u, 1u));
        Assert.Equal(InvalidArgument, Call(Threadmgr, "NID_0D0644DA", 3u));
        Assert.Equal(0, Call(Threadmgr, "NID_0D0644DA", 2u));
        Assert.Equal(0u, GuestMemory.R32(globals + 0x23DC + 8));
    }

    [Fact]
    public void DataWordsLandAtTheFirmwareOffsetsAndReadBack()
    {
        uint globals = Globals();
        Assert.Equal(0, Call(Threadmgr, "NID_5311CFB5", 0x81003001u, 0x1234u));
        Assert.Equal(0x81003001u, GuestMemory.R32(globals + 0x23E8));
        Assert.Equal(0x1234u, GuestMemory.R32(globals + 0x23EC));
        Assert.Equal(0, Call(Threadmgr, "NID_59D1D5B0", 0x81004000u));
        Assert.Equal(0x81004000u, GuestMemory.R32(globals + 0x2390));
        Assert.Equal(0, Call(Threadmgr, "NID_1394E856"));
        Assert.Equal(1u, GuestMemory.R32(globals + 0x2434));
        Assert.Equal(0, Call(Threadmgr, "NID_5D270A94"));
        Assert.Equal(0u, GuestMemory.R32(globals + 0x2434));
        GuestMemory.W32(globals + 0x28A8, 0xAAAA);
        GuestMemory.W32(globals + 0x28AC, 0xBBBB);
        uint output = _harness.Alloc(4);
        Assert.Equal(0, Call(Threadmgr, "NID_60798313", output));
        Assert.Equal(0xAAAAu, GuestMemory.R32(output));
        Assert.Equal(0, Call(Threadmgr, "NID_A7E5C83D", output));
        Assert.Equal(0xBBBBu, GuestMemory.R32(output));
    }

    [Fact]
    public void ProbeHookSettersKeepEachHookInItsOwnWord()
    {
        string[] setters = ["NID_B4A05763", "NID_F885ECCA", "NID_F55FDEFB", "NID_A3975A5A", "NID_9605DF7D", "NID_8C07C8EC", "NID_E2DACBC2", "NID_ABBE9BA5", "NID_10853CCD"];
        for (int i = 0; i < setters.Length; i++)
        {
            Invoke(Threadmgr, setters[i], 0x81005000u + (uint)i * 0x10);
        }

        for (int i = 0; i < setters.Length; i++)
        {
            Assert.Equal(0x81005000u + (uint)i * 0x10, (uint)Invoke(Threadmgr, "ProbeHook", i)!);
        }
    }

    [Fact]
    public void TheTwoStubsAnswerNotImplemented()
    {
        Assert.Equal(NotImplemented, Call(Threadmgr, "NID_6DC1BE7A"));
        Assert.Equal(NotImplemented, Call(Threadmgr, "NID_F46783E2"));
    }

    [Fact]
    public void SpinlockEntriesRunTheCpuSpinlocks()
    {
        uint spinlock = _harness.Alloc(8);
        _harness.OnGuest(t =>
        {
            Assert.Equal(0u, RunNid(t, 0x0A4AD37A, spinlock)); // trylock: taken
            Assert.NotEqual(0u, GuestMemory.R32(spinlock));
            Assert.NotEqual(0u, RunNid(t, 0x0A4AD37A, spinlock)); // already held
            RunNid(t, 0xC03799E2, spinlock); // unlock
            Assert.Equal(0u, GuestMemory.R32(spinlock));
            RunNid(t, 0x032E8F73, spinlock); // lock
            Assert.NotEqual(0u, GuestMemory.R32(spinlock));
            RunNid(t, 0xC03799E2, spinlock);
            uint state = RunNid(t, 0xA90B1E01, spinlock); // lock, interrupts suspended
            Assert.NotEqual(0u, GuestMemory.R32(spinlock));
            RunNid(t, 0xF96FE0D5, spinlock, state);
            Assert.Equal(0u, GuestMemory.R32(spinlock));
            state = RunNid(t, 0xF8A6013B, spinlock); // trylock, interrupts suspended
            Assert.NotEqual(0u, GuestMemory.R32(spinlock));
            RunNid(t, 0xF96FE0D5, spinlock, state);
        });
    }

    [Fact]
    public void MessagePipeCallsOnOneBufferMoveTheBytesLikeTheUserCalls()
    {
        _harness.OnGuest(t =>
        {
            int pipe = Call(UserThreadmgr, "sceKernelCreateMsgPipe", "kpipe", 0x40u, 0u, 0x100u, 0u);
            Assert.True(pipe > 0);
            uint source = _harness.Alloc(8);
            GuestMemory.W32(source, 0x11223344);
            GuestMemory.W32(source + 4, 0x55667788);
            uint destination = _harness.Alloc(8);
            uint moved = _harness.Alloc(4);
            Assert.Equal(0u, RunNid(t, 0x85F4DC8C, (uint)pipe, source, 4, 1, moved)); // try send, all
            Assert.Equal(4u, GuestMemory.R32(moved));
            Assert.Equal(0u, RunNid(t, 0xF5074173, (uint)pipe, source + 4, 4, 1, moved, 0)); // send, no timeout
            Assert.Equal(0u, RunNid(t, 0x2E95B628, (uint)pipe, destination, 4, 1, moved)); // try receive
            Assert.Equal(0x11223344u, GuestMemory.R32(destination));
            Assert.Equal(0u, RunNid(t, 0x2F9F75AA, (uint)pipe, destination + 4, 4, 1, moved, 0)); // receive
            Assert.Equal(0x55667788u, GuestMemory.R32(destination + 4));
            Assert.Equal(0u, RunNid(t, 0xE6D80698, (uint)pipe, source, 8, 1, moved, 0)); // send CB
            Assert.Equal(0u, RunNid(t, 0xD0262C55, (uint)pipe, destination, 8, 1, moved, 0)); // receive CB
            Assert.Equal(8u, GuestMemory.R32(moved));
            // the vector send with callbacks: one entry {buffer, size}
            uint vector = _harness.Alloc(8);
            GuestMemory.W32(vector, source);
            GuestMemory.W32(vector + 4, 8);
            Assert.Equal(0u, RunNid(t, 0x2FC0FFF6, (uint)pipe, vector, 1, 1, moved, 0));
            Assert.Equal(8u, GuestMemory.R32(moved));
            Assert.Equal(unchecked((uint)SceError.UNKNOWN_MSG_PIPE_ID), RunNid(t, 0x2E95B628, 0x7FFF0001u, destination, 4, 1, moved));
        });
    }

    [Fact]
    public void KernelChangeThreadPriorityAnswersTheEarlierPriorityAndNidDA1544D1Zero()
    {
        _harness.OnGuest(t =>
        {
            GuestThread other = _harness.Emu.Kernel.CreateThread("prio", 0, 0x10000100, 0x4000, 0, 0x70000);
            int earlier = other.Priority;
            Assert.Equal(earlier, Call(Threadmgr, "ksceKernelChangeThreadPriority", other.Uid, 0x10000102));
            int raised = other.Priority;
            Assert.NotEqual(earlier, raised);
            Assert.Equal(0, Call(Threadmgr, "NID_DA1544D1", other.Uid, 0x10000100));
            Assert.Equal(earlier, other.Priority);
            Assert.Equal(UnknownThreadId, Call(Threadmgr, "NID_DA1544D1", 0x7FFF0001, 0x10000100));
            Assert.Equal(t.Priority, Call(Threadmgr, "ksceKernelChangeThreadPriority", 0, 0));
        });
    }

    [Fact]
    public void Word100IsSetByNid84EB1EA4AndReadByNid56C18B11()
    {
        Assert.Equal(IllegalContext, Call(Threadmgr, "NID_84EB1EA4", 5));
        _harness.OnGuest(_ =>
        {
            Assert.Equal(0, Call(Threadmgr, "NID_56C18B11"));
            Assert.Equal(InvalidArgument, Call(Threadmgr, "NID_84EB1EA4", -1));
            Assert.Equal(0, Call(Threadmgr, "NID_84EB1EA4", 0x77));
            Assert.Equal(0x77, Call(Threadmgr, "NID_56C18B11"));
            Assert.Equal(0x77, Call(Threadmgr, "NID_84EB1EA4", 3));
        });
        // another thread starts from 0
        _harness.OnGuest(_ => Assert.Equal(0, Call(Threadmgr, "NID_56C18B11")));
    }

    [Fact]
    public void TheForKernelPermissionSetterIsKsceKernelSetPermission()
    {
        _harness.OnGuest(_ =>
        {
            int initial = Call(Threadmgr, "NID_332E127C");
            Assert.Equal(InvalidArgument, Call(Threadmgr, "NID_9461FC73", -2));
            Assert.Equal(initial, Call(Threadmgr, "NID_9461FC73", 0x40));
            Assert.Equal(0x40, Call(Threadmgr, "NID_332E127C"));
        });
    }

    [Fact]
    public void StackFreeBytesAreSpMinusTheBaseInsideTheStack()
    {
        _harness.OnGuest(t =>
        {
            unsafe
            {
                uint saved = t.Core.State->R[13];
                t.Core.State->R[13] = t.StackAddr + 0x300;
                Assert.Equal(0x300u, RunNid(t, 0xD74F66BD));
                t.Core.State->R[13] = t.StackAddr;
                Assert.Equal(0u, RunNid(t, 0xD74F66BD));
                t.Core.State->R[13] = t.StackAddr + t.StackSize;
                Assert.Equal(0u, RunNid(t, 0xD74F66BD));
                t.Core.State->R[13] = saved;
            }
        });
    }

    [Fact]
    public void CurrentThreadVfpMaskAndThreadClassComeFromTheirExports()
    {
        _harness.OnGuest(t =>
        {
            Call(UserThreadmgr, "sceKernelChangeThreadVfpException", 0x0800009Fu, 0u);
            Assert.Equal(0u, RunNid(t, 0x14F8167C));
            Call(UserThreadmgr, "sceKernelChangeThreadVfpException", 0u, 0x1u);
            Assert.Equal(1u, RunNid(t, 0x14F8167C));
            Assert.Equal(0u, RunNid(t, 0x20C228E4));
            Assert.Equal(RunNid(t, 0x85336A1C), RunNid(t, 0x565BD2DA));
            Assert.NotEqual(0u, RunNid(t, 0x565BD2DA));
        });
    }
}
