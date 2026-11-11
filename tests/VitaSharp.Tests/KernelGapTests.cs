using System.Reflection;
using System.Runtime.CompilerServices;
using VitaSharp.Core;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;

namespace VitaSharp.Tests;

/// <summary>Kernel functions the comparison with Vita3K found missing: sceKernelWaitMultipleEvents(CB) and the
/// _sceKernelWaitMultipleEvents syscall form against the 3.74 threadmgr contract (argument checks, OR/AND modes,
/// results in firing order, auto-reset taken per event, unknown uids reported in the results, timeout), and
/// _sceKernelGetTimer5Reg.</summary>
[Collection("guest")]
public sealed class KernelGapTests(GuestMemoryFixture fx)
{
    static int Call(object module, string name, params object?[] args) =>
        Convert.ToInt32(module.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.Invoke(module, args));

    uint Alloc(uint size)
    {
        uint p = fx.Memory.Alloc(size, "wme");
        GuestMemory.Fill(p, 0, size);
        return p;
    }

    Emulator Shell()
    {
        var emu = (Emulator)RuntimeHelpers.GetUninitializedObject(typeof(Emulator));
        emu.GetType().GetField("<Kernel>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(emu, new VitaSharp.Core.Kernel.Kernel(emu));
        emu.GetType().GetField("<Memory>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(emu, fx.Memory);
        return emu;
    }

    ThreadMgr Module() => new() { Emu = Shell() };

    [Fact]
    public void Timer5RegIsAPageWithTheProcessTime()
    {
        var pm = new ProcessMgr { Emu = Shell() }; // not running: the refresher thread ends at once
        uint reg = Alloc(4);
        Assert.Equal(SceError.ILLEGAL_ADDR, Call(pm, "_sceKernelGetTimer5Reg", new Ptr<uint>(0)));
        Assert.Equal(0, Call(pm, "_sceKernelGetTimer5Reg", new Ptr<uint>(reg)));
        uint page = GuestMemory.R32(reg);
        Assert.NotEqual(0u, page);
        ulong t = GuestMemory.R32(page) | (ulong)GuestMemory.R32(page + 4) << 32;
        Assert.InRange(t, 0UL, pm.Emu.Kernel.ProcessTimeUs);
        GuestMemory.W32(reg, 0);
        Assert.Equal(0, Call(pm, "_sceKernelGetTimer5Reg", new Ptr<uint>(reg)));
        Assert.Equal(page, GuestMemory.R32(reg)); // one page per process
    }

    int Event(ThreadMgr tm, uint attr, uint pattern)
    {
        uint name = Alloc(16);
        GuestMemory.WriteCString(name, "ev");
        return Call(tm, "sceKernelCreateSimpleEvent", name, attr, pattern, 0u);
    }

    [Fact]
    public void OrAndModesResultsAndErrors()
    {
        const uint A = 0x01000000, B = 0x02000000; // the user bits of an event pattern
        var tm = Module();
        int a = Event(tm, EventBits.AttrAutoReset, 0), b = Event(tm, 0, 0);
        uint events = Alloc(3 * 8), results = Alloc(3 * 0x18), timeout = Alloc(4);
        GuestMemory.W32(events, (uint)a);
        GuestMemory.W32(events + 4, A);
        GuestMemory.W32(events + 8, (uint)b);
        GuestMemory.W32(events + 12, B);

        Assert.Equal(SceError.ILLEGAL_ADDR, Call(tm, "sceKernelWaitMultipleEvents", 0u, 2u, 1u, results, new Ptr<uint>(0)));
        Assert.Equal(SceError.ILLEGAL_SIZE, Call(tm, "sceKernelWaitMultipleEvents", events, 65u, 1u, results, new Ptr<uint>(0)));
        Assert.Equal(SceError.ILLEGAL_MODE, Call(tm, "sceKernelWaitMultipleEvents", events, 2u, 4u, results, new Ptr<uint>(0)));
        Assert.Equal(0, Call(tm, "sceKernelWaitMultipleEvents", events, 0u, 1u, results, new Ptr<uint>(0)));

        // OR: only b is set; one result, b's pattern and user data
        Assert.Equal(0, Call(tm, "sceKernelSetEvent", b, B, 0x1122334455667788UL));
        Assert.Equal(1, Call(tm, "sceKernelWaitMultipleEvents", events, 2u, 1u, results, new Ptr<uint>(0)));
        Assert.Equal((uint)b, GuestMemory.R32(results));
        Assert.Equal(0u, GuestMemory.R32(results + 4));
        Assert.Equal(B, GuestMemory.R32(results + 8));
        Assert.Equal(0x1122334455667788UL, new Ptr<ulong>(results + 16).Value);

        // AND with a timeout: a never fires, b (manual reset) is taken; the wait times out with b's result in
        GuestMemory.W32(timeout, 2000);
        Assert.Equal(SceError.WAIT_TIMEOUT, Call(tm, "_sceKernelWaitMultipleEvents", events, 2u, 2u, OptOf(results, timeout)));
        Assert.Equal(0u, GuestMemory.R32(timeout));
        Assert.Equal((uint)b, GuestMemory.R32(results));

        // AND: both set; a is auto-reset and is cleared by the wait, b is not
        Assert.Equal(0, Call(tm, "sceKernelSetEvent", a, A, 5UL));
        Assert.Equal(2, Call(tm, "sceKernelWaitMultipleEvents", events, 2u, 2u, results, new Ptr<uint>(0)));
        Assert.Equal((uint)a, GuestMemory.R32(results));
        Assert.Equal((uint)b, GuestMemory.R32(results + 0x18));
        uint pat = Alloc(4);
        Assert.Equal(0, Call(tm, "sceKernelGetEventPattern", a, new Ptr<uint>(pat)));
        Assert.Equal(0u, GuestMemory.R32(pat) & A);
        Assert.Equal(0, Call(tm, "sceKernelGetEventPattern", b, new Ptr<uint>(pat)));
        Assert.Equal(B, GuestMemory.R32(pat) & B);

        // an unknown uid is a result of its own (UNKNOWN_EVENT_ID, pattern 0x10) and satisfies OR
        GuestMemory.W32(events + 16, 0x7FFF0001);
        GuestMemory.W32(events + 20, A);
        Assert.Equal(1, Call(tm, "sceKernelWaitMultipleEvents", events + 16, 1u, 1u, results, new Ptr<uint>(0)));
        Assert.Equal(0x7FFF0001u, GuestMemory.R32(results));
        Assert.Equal(SceError.UNKNOWN_EVENT_ID, (int)GuestMemory.R32(results + 4));
        Assert.Equal(EventBits.Error, GuestMemory.R32(results + 8));
        // without a results array nothing is counted
        Assert.Equal(0, Call(tm, "sceKernelWaitMultipleEvents", events, 2u, 1u, 0u, new Ptr<uint>(0)));
    }

    uint OptOf(uint results, uint timeout)
    {
        uint opt = Alloc(16);
        GuestMemory.W32(opt, results);
        GuestMemory.W32(opt + 4, timeout);
        return opt;
    }
}
