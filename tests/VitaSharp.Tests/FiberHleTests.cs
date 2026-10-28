using System.Reflection;
using System.Runtime.CompilerServices;
using VitaSharp.Core;
using VitaSharp.Core.Cpu;
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
public sealed unsafe class FiberHleTests(GuestMemoryFixture fx)
{
    const uint ThreadSp = 0x8100_8000, ThreadLr = 0x8100_1235;
    const uint EntryA = 0x8200_0001, EntryB = 0x8300_0001;

    static int Call(Fiber f, string name, params object[] args) =>
        Convert.ToInt32(typeof(Fiber).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.Invoke(f, args));

    uint Alloc(uint size) { uint p = fx.Memory.Alloc(size, "fiber"); GuestMemory.Fill(p, 0, size); return p; }

    static void SetBacking(object o, string prop, object? value) =>
        o.GetType().GetField($"<{prop}>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(o, value);

    /// <summary>Makes the test thread a guest thread with its own register file and returns that file.</summary>
    static CpuState* EnterFakeThread(Emulator emu)
    {
        var core = (CpuCore)RuntimeHelpers.GetUninitializedObject(typeof(CpuCore));
        var s = CpuState.Allocate();
        SetBacking(core, "State", Pointer.Box(s, typeof(CpuState*)));
        var t = (GuestThread)RuntimeHelpers.GetUninitializedObject(typeof(GuestThread));
        SetBacking(t, "Core", core);
        typeof(GuestThread).GetField("_current", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, t);
        return s;
    }

    static void LeaveFakeThread() => typeof(GuestThread).GetField("_current", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, null);

    /// <summary>Simulates the guest calling an HLE function from <paramref name="pc"/> (returning to pc+4 in Thumb).</summary>
    static void CallFrom(CpuState* s, uint pc, uint sp) { s->R[14] = (pc + 4) | 1; s->R[13] = sp; }

    [Fact]
    public void ContextlessFiberRestartsAtEntryOnEverySwitch()
    {
        var emu = (Emulator)RuntimeHelpers.GetUninitializedObject(typeof(Emulator));
        SetBacking(emu, "Kernel", new VitaSharp.Core.Kernel.Kernel(emu));
        var f = new Fiber { Emu = emu };
        uint a = Alloc(128), b = Alloc(128), name = Alloc(32), ctxA = Alloc(0x1000);
        GuestMemory.WriteCString(name, "fib");
        Assert.Equal(0, Call(f, "_sceFiberInitializeImpl", a, name, EntryA, 0x11u, ctxA, 0x1000u));
        Assert.Equal(0, Call(f, "_sceFiberInitializeImpl", b, name, EntryB, 0x22u, 0u, 0u)); // no context area

        var s = EnterFakeThread(emu);
        try
        {
            // the thread runs the scheduler fiber B: it starts on the thread's stack
            s->R[14] = ThreadLr; s->R[13] = ThreadSp;
            Call(f, "sceFiberRun", b, 5u, 0u);
            Assert.Equal(EntryB & ~1u, s->R[15]);
            Assert.Equal(0x22u, s->R[0]);
            Assert.Equal(5u, s->R[1]);
            Assert.Equal(ThreadSp, s->R[13]);

            // B switches to A (own context): A starts at its entry on its context stack
            CallFrom(s, 0x8300_0100, ThreadSp - 0x40);
            Call(f, "sceFiberSwitch", a, 6u, 0u);
            Assert.Equal(EntryA & ~1u, s->R[15]);
            Assert.Equal(ctxA + 0x1000, s->R[13]);

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
}
