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
