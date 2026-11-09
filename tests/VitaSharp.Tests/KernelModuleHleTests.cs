using VitaSharp.Core.Kernel;
using VitaSharp.Core.Loader;
using VitaSharp.Core.Memory;
using Mutex = VitaSharp.Core.Kernel.Mutex;

namespace VitaSharp.Tests;

/// <summary>Package A, second pass: sceKernelCancelMutex and module unloading (linker unbinding).</summary>
[Collection("guest")]
public sealed class KernelModuleHleTests(GuestMemoryFixture fx)
{
    uint Alloc(uint size, string tag = "km") { uint p = fx.Memory.Alloc(size, tag); GuestMemory.Fill(p, 0, size); return p; }

    static GuestThread FakeThread() =>
        (GuestThread)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(GuestThread));

    [Fact]
    public void CancelMutexWakesWaitersAndResetsCount()
    {
        var owner = FakeThread();
        var waiter = FakeThread();
        var canceller = FakeThread();
        var m = new Mutex { Name = "m", InitCount = 0 };
        Assert.Equal(0, m.TryLock(owner, 1));
        int r = 0;
        var t = new Thread(() => r = m.Lock(waiter, 1, default));
        t.Start();
        while (Volatile.Read(ref m.Waiters) == 0) Thread.Sleep(1);
        uint n = Alloc(4);
        Assert.Equal(0, m.Cancel(canceller, 1, new Ptr<int>(n)));
        t.Join();
        Assert.Equal(SceError.WAIT_CANCEL, r);
        Assert.Equal(1u, GuestMemory.R32(n));
        Assert.Same(canceller, m.Owner);
        Assert.Equal(1, m.LockCount);

        // -1 restores initCount (0): no owner
        Assert.Equal(0, m.Cancel(canceller, -1, default));
        Assert.Null(m.Owner);
        Assert.Equal(0, m.LockCount);
        // counts above 1 need the recursive attribute
        Assert.Equal(SceError.ILLEGAL_COUNT, m.Cancel(canceller, 2, default));
        Assert.Equal(SceError.ILLEGAL_COUNT, m.Cancel(canceller, -2, default));
        var rec = new Mutex { Attr = Mutex.AttrRecursive };
        Assert.Equal(0, rec.Cancel(canceller, 3, default));
        Assert.Equal(3, rec.LockCount);
        // a later wait is not affected by an earlier cancel
        Assert.Equal(0, m.Lock(waiter, 1, default));
    }

    /// <summary>A lightweight mutex keeps its state only in the guest work area (owner UID, count), as the guest-code
    /// fast paths (Hle/HleGuestCode) read and write it: a sleeper flags the owner word (bit 31) so the owner's unlock comes
    /// to the kernel and wakes it, a lock taken by guest code in between sends the woken thread back to sleep, a dead
    /// owner's lock can be taken, and a deleted mutex's UID word is cleared.</summary>
    [Fact]
    public void LightMutexStateLivesInTheWorkArea()
    {
        var table = new ObjectTable();
        GuestThread owner = FakeThread(), waiter = FakeThread(), other = FakeThread();
        table.Register(owner); table.Register(waiter); table.Register(other);
        uint work = Alloc(32);
        var m = new Mutex { Name = "lw", WorkArea = work, Threads = table };
        GuestMemory.W32(work + Mutex.LwUidOffset, (uint)table.Register(m));
        uint Word() => GuestMemory.R32(work);

        Assert.Equal(0, m.TryLock(owner, 1));
        Assert.Equal(((uint)owner.Uid, 1u), (Word(), GuestMemory.R32(work + 8)));
        Assert.Same(owner, m.Owner);
        Assert.Equal(SceError.LW_MUTEX_FAILED_TO_OWN, m.TryLock(waiter, 1));
        Assert.Equal(SceError.LW_MUTEX_RECURSIVE, m.TryLock(owner, 1));
        Assert.Equal(SceError.LW_MUTEX_NOT_OWNED, m.Unlock(waiter, 1));
        Assert.Equal(SceError.LW_MUTEX_UNLOCK_UDF, m.Unlock(owner, 2));

        int r = -1;
        var t = new Thread(() => r = m.Lock(waiter, 2, default));
        t.Start();
        while (Word() != ((uint)owner.Uid | Mutex.Contended)) Thread.Sleep(1); // the sleeper flagged the word
        Assert.Equal(0, m.Unlock(owner, 1));
        t.Join();
        Assert.Equal(0, r);
        Assert.Equal(((uint)waiter.Uid, 2u), (Word(), GuestMemory.R32(work + 8))); // no other sleeper: no flag
        Assert.Equal(0, m.Unlock(waiter, 2));
        Assert.Equal((0u, 0u), (Word(), GuestMemory.R32(work + 8)));

        // guest code takes the free mutex; a sleeper flags it; guest code taking it back between the kernel's wake-up and
        // the woken thread's retry sends that thread back to sleep
        GuestMemory.W32(work, (uint)other.Uid); GuestMemory.W32(work + 8, 1);
        r = -1;
        t = new Thread(() => r = m.Lock(waiter, 1, default));
        t.Start();
        while (Word() != ((uint)other.Uid | Mutex.Contended)) Thread.Sleep(1);
        lock (m)
        {
            Assert.Equal(0, m.Unlock(other, 1)); // the flagged word: the guest unlock's slow path
            GuestMemory.W32(work, (uint)owner.Uid); GuestMemory.W32(work + 8, 1); // guest code again, before the waiter runs
        }
        while (Word() != ((uint)owner.Uid | Mutex.Contended)) Thread.Sleep(1); // back to sleep, flagged again
        Assert.True(t.IsAlive);
        Assert.Equal(0, m.Unlock(owner, 1));
        t.Join();
        Assert.Equal((0, (uint)waiter.Uid), (r, Word()));
        Assert.Equal(0, m.Unlock(waiter, 1));

        // an owner that has ended does not hold the mutex
        GuestMemory.W32(work, (uint)other.Uid); GuestMemory.W32(work + 8, 1);
        other.Status = ThreadStatus.Dormant;
        Assert.Equal(0, m.Lock(owner, 1, default));
        Assert.Equal((uint)owner.Uid, Word());

        m.Delete();
        Assert.Equal((0u, 0u, 0u), (Word(), GuestMemory.R32(work + 8), GuestMemory.R32(work + Mutex.LwUidOffset)));
    }

    /// <summary>Every [Hle] registration must resolve to a NID ("HLE: no NID known" otherwise: dead code).</summary>
    [Fact]
    public void EveryHleRegistrationHasANid()
    {
        var missing = new List<string>();
        foreach (var t in typeof(VitaSharp.Core.Hle.HleModule).Assembly.GetTypes()
                     .Where(t => t.IsSubclassOf(typeof(VitaSharp.Core.Hle.HleModule)) && !t.IsAbstract))
            foreach (var mi in t.GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public |
                                            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly))
                foreach (var a in System.Reflection.CustomAttributeExtensions.GetCustomAttributes<VitaSharp.Core.Hle.HleAttribute>(mi))
                {
                    string name = a.Name ?? mi.Name;
                    if (a.Nid == 0 && !VitaSharp.Core.Hle.NidDatabase.LookupName(name).Any(n => a.Library == null || n.Library == a.Library))
                        missing.Add($"{t.Name}.{name}");
                }
        Assert.Empty(missing);
    }

    sealed class FakeHle : IImportResolver
    {
        public uint GetHleFunctionIndex(uint nid, uint libNid, string libName) => nid & 0xFFFF;
        public uint GetHleVariable(uint nid) => 0;
    }

    sealed class GuestCodeHle(uint nid, uint code) : IImportResolver
    {
        public uint GetHleFunctionIndex(uint n, uint libNid, string libName) => n & 0xFFFF;
        public uint GetHleVariable(uint n) => 0;
        public uint GetHleGuestCode(uint n) => n == nid ? code : 0;
    }

    /// <summary>After a save state load the HLE import stubs are bound again to this build's routines: an SVC stub of a
    /// state saved before a routine existed, and a jump into the saved session's guest code, both take the current
    /// binding; a stub bound to a module export and a stub something else patched (a hook) stay as they are.</summary>
    [Fact]
    public void LoadedStateStubsTakeTheCurrentRoutines()
    {
        uint libNid = 0xAABBCCDD, nidCode = 0x11112222, nidSvc = 0x33334444, nidExport = 0x55556666, nidHooked = 0x77778888;
        uint seg = Alloc(0x100), oldCode = Alloc(0x40), newCode = Alloc(0x40), hook = Alloc(0x40), prov = Alloc(0x40);
        var linker = new Linker(new GuestCodeHle(nidCode, newCode));
        var importer = new LoadedModule { Name = "importer", Segments = { [0] = new SegmentInfo(seg, 0, 0x100) } };
        uint sCode = seg, sCode2 = seg + 0x10, sSvc = seg + 0x20, sExport = seg + 0x30, sHooked = seg + 0x40;
        foreach (var (n, s) in new[] { (nidCode, sCode), (nidCode, sCode2), (nidSvc, sSvc), (nidExport, sExport), (nidHooked, sHooked) })
            linker.BindFunctionImport(n, libNid, "Lib", s, importer.Segments);
        var provider = new LoadedModule { Name = "p", Segments = { [0] = new SegmentInfo(prov, 0, 0x40) } };
        linker.RegisterExports(provider, libNid, [nidExport], [prov + 1], 1);
        Assert.Equal(newCode, StubTarget(sCode));

        // the restored memory: the saving session's stubs
        Linker.WriteHleStub(sCode, 0x999, nidCode);   // saved before nidCode had guest code
        Linker.WriteJumpStub(sCode2, oldCode + 8);     // the saved session's routine
        Linker.WriteJumpStub(sHooked, hook);           // a hook
        Assert.Equal(3, linker.RebindHleStubs(a => a - oldCode < 0x40)); // the two above and the plain SVC stub
        Assert.Equal(newCode, StubTarget(sCode));
        Assert.Equal(newCode, StubTarget(sCode2));
        Assert.Equal(0xEF000000u | (nidSvc & 0xFFFF), GuestMemory.R32(sSvc));
        Assert.Equal(prov + 1, StubTarget(sExport));
        Assert.Equal(hook, StubTarget(sHooked));
    }

    static uint StubTarget(uint stub)
    {
        uint w0 = GuestMemory.R32(stub), w1 = GuestMemory.R32(stub + 4);
        return ((w0 >> 4) & 0xF000) | (w0 & 0xFFF) | ((((w1 >> 4) & 0xF000) | (w1 & 0xFFF)) << 16);
    }

    [Fact]
    public void UnregisterModuleRebindsImports()
    {
        var linker = new Linker(new FakeHle());
        uint libNid = 0xAABBCCDD, nidA = 0x11112222, nidB = 0x33334444;

        // importer: two stubs, bound before any provider exists -> HLE
        uint impSeg = Alloc(0x100);
        var importer = new LoadedModule { Name = "importer", Segments = { [0] = new SegmentInfo(impSeg, 0, 0x100) } };
        uint stubA = impSeg, stubB = impSeg + 0x10;
        linker.BindFunctionImport(nidA, libNid, "Lib", stubA, importer.Segments);
        linker.BindFunctionImport(nidB, libNid, "Lib", stubB, importer.Segments);
        Assert.Equal(0xEF000000u | (nidA & 0xFFFF), GuestMemory.R32(stubA));

        // provider 1 exports A and B; provider 2 (loaded later) exports A only
        uint p1 = Alloc(0x100), p2 = Alloc(0x100);
        var prov1 = new LoadedModule { Name = "p1", Segments = { [0] = new SegmentInfo(p1, 0, 0x100) } };
        var prov2 = new LoadedModule { Name = "p2", Segments = { [0] = new SegmentInfo(p2, 0, 0x100) } };
        linker.RegisterExports(prov1, libNid, [nidA, nidB], [p1 + 1, p1 + 0x21], 2);
        Assert.Equal(p1 + 1, StubTarget(stubA));
        Assert.Equal(p1 + 0x21, StubTarget(stubB));
        linker.RegisterExports(prov2, 0x12345678, [nidA], [p2 + 1], 1); // other library: does not steal lib-bound stubs
        Assert.Equal(p1 + 1, StubTarget(stubA));

        // unloading provider 1: A falls back to the remaining (any-library) export, B back to HLE
        Assert.Equal(2, linker.UnregisterModule(prov1));
        Assert.Equal(p2 + 1, StubTarget(stubA));
        Assert.Equal(0xEF000000u | (nidB & 0xFFFF), GuestMemory.R32(stubB));
        Assert.Equal(nidB, GuestMemory.R32(stubB + 8));
        Assert.True(linker.TryGetFunctionExport(nidA, out uint a) && a == p2 + 1);
        Assert.False(linker.TryGetFunctionExport(nidB, out _));

        // unloading the importer drops its bindings: a new provider no longer writes into freed memory
        linker.UnregisterModule(importer);
        GuestMemory.W32(stubB, 0xDEADBEEF);
        linker.RegisterExports(prov1, libNid, [nidB], [p1 + 0x41], 1);
        Assert.Equal(0xDEADBEEFu, GuestMemory.R32(stubB));
    }
}
