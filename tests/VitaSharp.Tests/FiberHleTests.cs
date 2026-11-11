using System.Reflection;
using System.Runtime.CompilerServices;
using VitaSharp.Core;
using VitaSharp.Core.Cpu;
using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;

namespace VitaSharp.Tests;

/// <summary>
/// SceFiber context switching, driven on a fake current guest thread (the HLE calls rewrite its register state the
/// way the dispatcher would see it). Covers the rule libult's scheduler depends on: a fiber initialised without a
/// context area keeps no state, so every run or switch into it restarts it at its entry on the thread's stack.
/// </summary>
[Collection("guest")]
public sealed unsafe class FiberHleTests(GuestMemoryFixture fixture)
{
    private const uint ThreadSp = 0x8100_8000, ThreadLr = 0x8100_1235;
    private const uint EntryA = 0x8200_0001, EntryB = 0x8300_0001;

    private static int Call(Fiber f, string name, params object[] args) =>
        Convert.ToInt32(typeof(Fiber).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.Invoke(f, args));

    private uint Alloc(uint size)
    {
        uint p = fixture.Memory.Alloc(size, "fiber");
        GuestMemory.Fill(p, 0, size);
        return p;
    }

    private static void SetBacking(object o, string prop, object? value) =>
        o.GetType().GetField($"<{prop}>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(o, value);

    /// <summary>An emulator with just what SceFiber uses: the kernel, guest memory, the HLE table (the stub fiber entries
    /// return to) and the code cache.</summary>
    private Emulator NewEmulator()
    {
        var emu = (Emulator)RuntimeHelpers.GetUninitializedObject(typeof(Emulator));
        SetBacking(emu, "Kernel", new VitaSharp.Core.Kernel.Kernel(emu));
        SetBacking(emu, "Memory", fixture.Memory);
        SetBacking(emu, "Code", fixture.Cache);
        SetBacking(emu, "Hle", new VitaSharp.Core.Hle.HleRegistry(emu));
        return emu;
    }

    /// <summary>Makes the test thread a guest thread with its own register file and returns that file.</summary>
    private static CpuState* EnterFakeThread(Emulator emu)
    {
        var core = (CpuCore)RuntimeHelpers.GetUninitializedObject(typeof(CpuCore));
        CpuState* s = CpuState.Allocate();
        SetBacking(core, "State", Pointer.Box(s, typeof(CpuState*)));
        var t = (GuestThread)RuntimeHelpers.GetUninitializedObject(typeof(GuestThread));
        SetBacking(t, "Core", core);
        typeof(GuestThread).GetField("_current", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, t);
        return s;
    }

    private static void LeaveFakeThread() => typeof(GuestThread).GetField("_current", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, null);

    /// <summary>Simulates the guest calling an HLE function from <paramref name="pc"/> (returning to pc+4 in Thumb).</summary>
    private static void CallFrom(CpuState* s, uint pc, uint sp)
    {
        s->R[14] = (pc + 4) | 1;
        s->R[13] = sp;
    }

    [Fact]
    public void ContextlessFiberRestartsAtEntryOnEverySwitch()
    {
        Emulator emu = NewEmulator();
        var f = new Fiber { Emu = emu };
        uint a = Alloc(128), b = Alloc(128), name = Alloc(32), ctxA = Alloc(0x1000);
        GuestMemory.WriteCString(name, "fib");
        Assert.Equal(0, Call(f, "_sceFiberInitializeImpl", a, name, EntryA, 0x11u, ctxA, 0x1000u));
        Assert.Equal(0, Call(f, "_sceFiberInitializeImpl", b, name, EntryB, 0x22u, 0u, 0u)); // no context area

        CpuState* s = EnterFakeThread(emu);
        try
        {
            // the thread runs the scheduler fiber B: it starts on the thread's stack
            s->R[14] = ThreadLr;
            s->R[13] = ThreadSp;
            Call(f, "sceFiberRun", b, 5u, 0u);
            Assert.Equal(EntryB & ~1u, s->R[15]);
            Assert.Equal(0x22u, s->R[0]);
            Assert.Equal(5u, s->R[1]);
            Assert.Equal(ThreadSp, s->R[13]);

            // B switches to A (own context): A starts at its entry on its context stack
            CallFrom(s, 0x8300_0100, ThreadSp - 0x40);
            Call(f, "sceFiberSwitch", a, 6u, 0u);
            Assert.Equal(EntryA & ~1u, s->R[15]);
            Assert.Equal(ctxA + 0xFF8, s->R[13]); // (addr + size - 1) & ~7

            // A switches back to B: B does not resume after its sceFiberSwitch call but starts over
            CallFrom(s, 0x8200_0200, ctxA + 0xF00);
            Call(f, "sceFiberSwitch", b, 7u, 0u);
            Assert.Equal(EntryB & ~1u, s->R[15]);
            Assert.Equal(0x22u, s->R[0]);
            Assert.Equal(7u, s->R[1]);
            Assert.Equal(ThreadSp, s->R[13]);

            // B switches to A again: A (with a context) resumes right after its own sceFiberSwitch with SCE_OK
            CallFrom(s, 0x8300_0100, ThreadSp - 0x40);
            Call(f, "sceFiberSwitch", a, 8u, 0u);
            Assert.Equal(0x8200_0204u, s->R[15]);
            Assert.Equal(0u, s->R[0]);
            Assert.Equal(ctxA + 0xF00, s->R[13]);

            // A returns to the thread, which resumes after its sceFiberRun
            CallFrom(s, 0x8200_0300, ctxA + 0xF00);
            Call(f, "sceFiberReturnToThread", 0u, 0u);
            Assert.Equal(ThreadLr & ~1u, s->R[15]);
            Assert.Equal(ThreadSp, s->R[13]);
        }
        finally
        {
            LeaveFakeThread();
            CpuState.Free(s);
        }
    }

    [Fact]
    public void AttachContextAcceptsAnyIdleFiberWithoutContextArea()
    {
        Emulator emu = NewEmulator();
        var f = new Fiber { Emu = emu };
        uint a = Alloc(128), b = Alloc(128), c = Alloc(128), name = Alloc(32), ctxA = Alloc(0x1000), ctxB = Alloc(0x800);
        GuestMemory.WriteCString(name, "fib");
        Assert.Equal(0, Call(f, "_sceFiberInitializeImpl", a, name, EntryA, 0x11u, ctxA, 0x1000u));
        Assert.Equal(0, Call(f, "_sceFiberInitializeImpl", b, name, EntryB, 0x22u, 0u, 0u));
        Assert.Equal(0, Call(f, "_sceFiberInitializeImpl", c, name, EntryB, 0x33u, 0u, 0u));
        const int ERR_ALIGNMENT = unchecked((int)0x80590002), ERR_RANGE = unchecked((int)0x80590003),
            ERR_INVALID = unchecked((int)0x80590004), ERR_PERMISSION = unchecked((int)0x80590005), ERR_STATE = unchecked((int)0x80590006);

        CpuState* s = EnterFakeThread(emu);
        try
        {
            s->R[14] = ThreadLr;
            s->R[13] = ThreadSp;
            Assert.Equal(ERR_PERMISSION, Call(f, "_sceFiberAttachContextAndSwitch", b, ctxB, 0x800u, 0u, 0u)); // not in a fiber
            // B runs on the thread stack (no context), then switches to A: B is now idle (suspended) without a context area
            Call(f, "sceFiberRun", b, 0u, 0u);
            Assert.Equal(ERR_PERMISSION, Call(f, "_sceFiberAttachContextAndRun", c, ctxB, 0x800u, 0u, 0u)); // already in one
            CallFrom(s, 0x8300_0100, ThreadSp - 0x40);
            Call(f, "sceFiberSwitch", a, 0u, 0u);
            Assert.Equal(ERR_STATE, Call(f, "_sceFiberAttachContextAndSwitch", a, 0u, 0u, 0u, 0u)); // A is running
            Assert.Equal(ERR_RANGE, Call(f, "_sceFiberAttachContextAndSwitch", b, ctxB, 0x100u, 0u, 0u));
            Assert.Equal(ERR_ALIGNMENT, Call(f, "_sceFiberAttachContextAndSwitch", b, ctxB + 4, 0x800u, 0u, 0u));
            Assert.Equal(ERR_INVALID, Call(f, "_sceFiberAttachContextAndSwitch", b, ctxB, 0x804u, 0u, 0u));
            Assert.Equal(ERR_INVALID, Call(f, "_sceFiberAttachContextAndSwitch", b, 0u, 0x800u, 0u, 0u));

            // A attaches a context to the suspended B: B starts at its entry on the new stack
            CallFrom(s, 0x8200_0200, ctxA + 0xF00);
            Assert.Equal(0x22, Call(f, "_sceFiberAttachContextAndSwitch", b, ctxB, 0x800u, 9u, 0u));
            Assert.Equal(EntryB & ~1u, s->R[15]);
            Assert.Equal(9u, s->R[1]);
            Assert.Equal(ctxB + 0x7F8, s->R[13]);

            // B (now with a context) switches to A with 0/0: a plain switch, A resumes after its call
            CallFrom(s, 0x8300_0400, ctxB + 0x700);
            Assert.Equal(0, Call(f, "_sceFiberAttachContextAndSwitch", a, 0u, 0u, 0u, 0u));
            Assert.Equal(0x8200_0204u, s->R[15]);
            Assert.Equal(ERR_INVALID, Call(f, "_sceFiberAttachContextAndSwitch", b, ctxB, 0x800u, 0u, 0u)); // B has one now
            // B keeps its state now: switching back resumes it
            Call(f, "sceFiberSwitch", b, 0u, 0u);
            Assert.Equal(0x8300_0404u, s->R[15]);
            Assert.Equal(ctxB + 0x700, s->R[13]);
        }
        finally
        {
            LeaveFakeThread();
            CpuState.Free(s);
        }
    }

    [Fact]
    public void EntryReturnResumesTheThreadWithErrState()
    {
        Emulator emu = NewEmulator();
        var f = new Fiber { Emu = emu };
        uint a = Alloc(128), name = Alloc(32), ctxA = Alloc(0x1000), argOnReturn = Alloc(4);
        GuestMemory.WriteCString(name, "fib");
        Assert.Equal(0, Call(f, "_sceFiberInitializeImpl", a, name, EntryA, 0x11u, ctxA, 0x1000u));
        CpuState* s = EnterFakeThread(emu);
        try
        {
            s->R[14] = ThreadLr;
            s->R[13] = ThreadSp;
            Call(f, "sceFiberRun", a, 0u, argOnReturn);
            uint lr = s->R[14];
            Assert.NotEqual(emu.Kernel.ThreadExitTrap, lr);
            // the entry returns: the stub's host function puts the thread back after its sceFiberRun with ERR_STATE
            uint svc = GuestMemory.R32(lr) & 0xFFFFFF;
            HleFunction fn = emu.Hle.GetFunction(svc);
            Assert.Equal("SceFiber:entryReturned", fn.Name);
            var ctx = new VitaSharp.Core.Hle.HleContext { Cpu = s, Thread = GuestThread.Current!, Emu = emu };
            fn.Invoke!(ctx);
            Assert.Equal(0x80590006u, s->R[0]);
            Assert.Equal(ThreadLr & ~1u, s->R[15]);
            Assert.Equal(ThreadSp, s->R[13]);
            Assert.Equal(a, GuestMemory.R32(argOnReturn));
            // the fiber is idle and starts over at its entry
            Call(f, "sceFiberRun", a, 3u, 0u);
            Assert.Equal(EntryA & ~1u, s->R[15]);
            Assert.Equal(3u, s->R[1]);
        }
        finally
        {
            LeaveFakeThread();
            CpuState.Free(s);
        }
    }
}
