using System.Reflection;
using System.Runtime.CompilerServices;
using VitaSharp.Core;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;

namespace VitaSharp.Tests;

/// <summary>
/// SceUlt fallback (package G2) on the non-blocking paths. The expected codes are the ones the FW 3.60 libult.suprx
/// returned for the same calls in a scratch harness (blocking acquire/release, ulthread create/join and the mutex owner
/// checks were run there on guest threads, HLE and LLE side by side).
/// </summary>
[Collection("guest")]
public sealed class UltHleTests(GuestMemoryFixture fx)
{
    const int NULL = unchecked((int)0x80810001), ALIGNMENT = unchecked((int)0x80810002), RANGE = unchecked((int)0x80810003),
        INVALID = unchecked((int)0x80810004), PERMISSION = unchecked((int)0x80810005), STATE = unchecked((int)0x80810006),
        AGAIN = unchecked((int)0x80810008);
    const uint Version = 0x03600011;

    static int Call(Ult u, string name, params object[] args) =>
        Convert.ToInt32(typeof(Ult).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.Invoke(u, args));

    uint Alloc(uint size) { uint p = fx.Memory.Alloc(size, "ult"); GuestMemory.Fill(p, 0, size); return p; }

    uint Name(string s) { uint p = Alloc(32); GuestMemory.WriteCString(p, s); return p; }

    static Ult Make()
    {
        var emu = (Emulator)RuntimeHelpers.GetUninitializedObject(typeof(Emulator));
        typeof(Emulator).GetField("<Kernel>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(emu, new VitaSharp.Core.Kernel.Kernel(emu));
        return new Ult { Emu = emu };
    }

    uint Pool(Ult u)
    {
        uint pool = Alloc(0x100);
        Assert.Equal(0, Call(u, "_sceUltWaitingQueueResourcePoolCreate", pool, Name("wq"), 4u, 4u, Alloc(0x200), 0u));
        return pool;
    }

    [Fact]
    public void OptParamsAndWorkAreaSizes()
    {
        var u = Make();
        uint opt = Alloc(0x80);
        GuestMemory.Fill(opt, 0xEE, 0x80);
        Assert.Equal(0, Call(u, "_sceUltMutexOptParamInitialize", opt, Version));
        Assert.Equal(0x6C696275u, GuestMemory.R32(opt));
        Assert.Equal(Version, GuestMemory.R32(opt + 0xC));
        Assert.Equal(0u, GuestMemory.R32(opt + 0x7C));
        Assert.Equal(NULL, Call(u, "_sceUltQueueOptParamInitialize", 0u, Version));
        Assert.Equal(ALIGNMENT, Call(u, "_sceUltQueueOptParamInitialize", opt + 4, Version));
        Assert.Equal((4u + 4) * 32 + 0x40, (uint)Call(u, "sceUltWaitingQueueResourcePoolGetWorkAreaSize", 4u, 4u));
        Assert.Equal(8u * 16 + (8u + 2) * 32 + 0x40, (uint)Call(u, "sceUltQueueDataResourcePoolGetWorkAreaSize", 8u, 16u, 2u));
        Assert.True(Call(u, "sceUltUlthreadRuntimeGetWorkAreaSize", 4u, 2u) > 0);

        // an uninitialised opt is INVALID; a pool's opt must keep its reserved bytes zero
        uint bad = Alloc(0x80);
        Assert.Equal(INVALID, Call(u, "_sceUltWaitingQueueResourcePoolCreate", Alloc(0x100), Name("p"), 4u, 4u, Alloc(0x200), bad));
        Assert.Equal(0, Call(u, "_sceUltWaitingQueueResourcePoolOptParamInitialize", bad, Version));
        GuestMemory.W32(bad + 0x10, 1);
        Assert.Equal(INVALID, Call(u, "_sceUltWaitingQueueResourcePoolCreate", Alloc(0x100), Name("p"), 4u, 4u, Alloc(0x200), bad));
        Assert.Equal(INVALID, Call(u, "_sceUltWaitingQueueResourcePoolCreate", Alloc(0x100), Name("p"), 0u, 4u, Alloc(0x200), 0u));
        Assert.Equal(ALIGNMENT, Call(u, "_sceUltWaitingQueueResourcePoolCreate", Alloc(0x100) + 4, Name("p"), 4u, 4u, Alloc(0x200), 0u));
    }

    [Fact]
    public void QueueSharesThePoolElements()
    {
        var u = Make();
        uint wq = Pool(u), dp = Alloc(0x100), q = Alloc(0x100), data = Alloc(16);
        Assert.Equal(0, Call(u, "_sceUltQueueDataResourcePoolCreate", dp, Name("dp"), 1u, 4u, 1u, wq, Alloc(0x200), 0u));
        Assert.Equal(0, Call(u, "_sceUltQueueCreate", q, Name("q"), 4u, wq, dp, 0u));
        Assert.Equal(AGAIN, Call(u, "sceUltQueueTryPop", q, data));
        GuestMemory.W32(data, 0x12345678);
        Assert.Equal(0, Call(u, "sceUltQueueTryPush", q, data));
        Assert.Equal(AGAIN, Call(u, "sceUltQueueTryPush", q, data)); // the pool has one element
        GuestMemory.W32(data, 0);
        Assert.Equal(0, Call(u, "sceUltQueueTryPop", q, data));
        Assert.Equal(0x12345678u, GuestMemory.R32(data));
        Assert.Equal(STATE, Call(u, "sceUltQueueDataResourcePoolDestroy", dp)); // the queue still uses it
        Assert.Equal(0, Call(u, "sceUltQueueDestroy", q));
        Assert.Equal(0, Call(u, "sceUltQueueDataResourcePoolDestroy", dp));
        Assert.Equal(0, Call(u, "sceUltWaitingQueueResourcePoolDestroy", wq));
        Assert.Equal(STATE, Call(u, "sceUltQueueTryPop", q, data)); // destroyed
    }

    [Fact]
    public void SemaphoreInfoAndDestroyedObjects()
    {
        var u = Make();
        uint wq = Pool(u), s = Alloc(0x100), info = Alloc(0x80);
        Assert.Equal(0, Call(u, "_sceUltSemaphoreCreate", s, Name("sema"), 1, wq, 0u));
        Assert.Equal(0, Call(u, "sceUltSemaphoreTryAcquire", s, 1));
        Assert.Equal(AGAIN, Call(u, "sceUltSemaphoreTryAcquire", s, 1));
        Assert.Equal(RANGE, Call(u, "sceUltSemaphoreRelease", s, 0));
        Assert.Equal(0, Call(u, "sceUltSemaphoreRelease", s, 2));
        Assert.Equal(0, Call(u, "sceUltSemaphoreTryAcquire", s, 2));
        GuestMemory.W32(info, 0x40);
        Assert.Equal(INVALID, Call(u, "sceUltGetSemaphoreInfo", s, info));
        GuestMemory.W32(info, 0x80);
        Assert.Equal(0, Call(u, "sceUltGetSemaphoreInfo", s, info));
        Assert.Equal(s, GuestMemory.R32(info + 4));
        Assert.Equal("sema", GuestMemory.ReadCString(info + 8, 32));
        Assert.Equal("sema", GuestMemory.ReadCString(s, 32)); // the object keeps its name at +0 like libult
        Assert.Equal(STATE, Call(u, "sceUltWaitingQueueResourcePoolDestroy", wq));
        Assert.Equal(0, Call(u, "sceUltSemaphoreDestroy", s));
        Assert.Equal(STATE, Call(u, "sceUltSemaphoreRelease", s, 1));
        Assert.Equal(INVALID, Call(u, "sceUltSemaphoreRelease", Alloc(0x100), 1)); // never created
        Assert.Equal(0, Call(u, "sceUltWaitingQueueResourcePoolDestroy", wq));
    }

    [Fact]
    public void ConditionVariableBindsItsMutexAndUlthreadCallsOutsideUlthreads()
    {
        var u = Make();
        uint wq = Pool(u), m = Alloc(0x100), cv = Alloc(0x100), self = Alloc(4);
        Assert.Equal(0, Call(u, "_sceUltMutexCreate", m, Name("m"), wq, 0u));
        Assert.Equal(NULL, Call(u, "_sceUltConditionVariableCreate", cv, Name("cv"), 0u, 0u));
        Assert.Equal(0, Call(u, "_sceUltConditionVariableCreate", cv, Name("cv"), m, 0u));
        Assert.Equal(PERMISSION, Call(u, "sceUltConditionVariableWait", cv)); // caller does not own the mutex
        Assert.Equal(0, Call(u, "sceUltConditionVariableSignalAll", cv));

        GuestMemory.W32(self, 0xFFFFFFFF);
        Assert.Equal(0, Call(u, "sceUltUlthreadGetSelf", new Ptr<uint>(self)));
        Assert.Equal(0u, GuestMemory.R32(self));
        Assert.Equal(PERMISSION, Call(u, "sceUltUlthreadYield"));
        Assert.Equal(PERMISSION, Call(u, "sceUltUlthreadExit", 0));
    }
}
