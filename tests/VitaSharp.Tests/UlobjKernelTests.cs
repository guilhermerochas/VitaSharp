using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// SceUlobjMgr (3.74): the user side's revision and start checks and the process record the kernel entries find
/// (none before the start: 0x80010002); the object queries read libult's objects from the process's memory as
/// SceUlobjMgr 3.74 does (the header's size and kind, the waiter lists, the pool's limit, the tables of entries; a
/// range the process cannot read is the copy's fault, another kind or a smaller size 0x8002000C).
/// </summary>
[Collection("guest")]
public sealed class UlobjKernelTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int Argument = unchecked((int)0x80020005), NoRecord = unchecked((int)0x80010002);
    private const int NoObject = unchecked((int)0x8002000C), Fault = VitaSharp.Core.Kernel.KernelSpace.UserCopyFault;

    private readonly HleHarness _harness = new(fixture, "ulobjk");

    public void Dispose() => _harness.Dispose();

    private DebugPerf Debug => _harness.Module<DebugPerf>();

    [Fact]
    public void EveryExportHasAnEntry()
    {
        var bindings = HleBindings.Modules.SelectMany(m => m.Bindings()).Where(b => b.Library == "SceUlobjMgrForDriver").ToList();
        Assert.Equal(19, bindings.Count);
        Assert.Equal(19, bindings.Select(b => b.Nid).Distinct().Count());
    }

    [Fact]
    public void TheUserSideChecksRevisionAndOrder()
    {
        Assert.Equal(Argument, Call(Debug, "_sceUlobjMgrRegisterLibultProtocolRevision", 0x1000001u));
        Assert.Equal(Argument, Call(Debug, "_sceUlobjMgrRegisterLibultProtocolRevision", 0x3600000u));
        Assert.Equal(0, Call(Debug, "_sceUlobjMgrRegisterLibultProtocolRevision", 0x3600011u));
        Assert.Equal(unchecked((int)0x80029001), Call(Debug, "_sceUlobjMgrStopSupportingUserlevelObject"));
        Assert.Equal(unchecked((int)0x80020004), Call(Debug, "_sceUlobjMgrStartSupportingUserlevelObject", 0x1000u, 0x3740001u, 0u));
        Assert.Equal(unchecked((int)0x8002710B), Call(Debug, "_sceUlobjMgrStartSupportingUserlevelObject", 0x2000001u, 0x3600011u, 0u));
    }

    [Fact]
    public void TheRecordExistsOnlyAfterTheStart()
    {
        uint words = _harness.Alloc(12);
        Assert.Equal(Argument, Call(Debug, "ksceUlobjMgr_A90A564C", 0, 0u, words + 4, words + 8));
        Assert.Equal(NoRecord, Call(Debug, "ksceUlobjMgr_A90A564C", 0, words, words + 4, words + 8));
        Assert.Equal(unchecked((int)0x80020004), Call(Debug, "ksceUlobjMgr_6F868221"));
        Assert.Equal(0, Call(Debug, "_sceUlobjMgrStartSupportingUserlevelObject", 0x1000u, 0x3600011u, words + 8));
        uint table = GuestMemory.R32(words + 8);
        Assert.Equal(unchecked((int)0x80028003), Call(Debug, "_sceUlobjMgrStartSupportingUserlevelObject", 0x1000u, 0x3600011u, words + 8));
        Assert.Equal(8u, GuestMemory.R32(table + 0x10));
        Assert.Equal(0xFFFFFFF8u, GuestMemory.R32(table + 0x10 + 0xFFF * 4));
        Assert.Equal(0, Call(Debug, "ksceUlobjMgr_A90A564C", 0, words, words + 4, words + 8));
        Assert.Equal(table, GuestMemory.R32(words));
        Assert.Equal(GuestMemory.R32(words + 8) + 0xC, GuestMemory.R32(words + 4));
        Assert.Equal(0x3600011u, GuestMemory.R32(GuestMemory.R32(words + 4)));
        Assert.Equal(0x1000u, GuestMemory.R32(GuestMemory.R32(words + 8) + 0x10));
        Assert.Equal(0, Call(Debug, "ksceUlobjMgr_A48A5B3C", GuestMemory.R32(words + 8)));
        Assert.Equal(Argument, Call(Debug, "ksceUlobjMgr_A48A5B3C", 0u));
        Assert.Equal(0, Call(Debug, "_sceUlobjMgrStopSupportingUserlevelObject"));
        Assert.Equal(NoRecord, Call(Debug, "ksceUlobjMgr_A90A564C", 0, words, words + 4, words + 8));
    }

    [Fact]
    public void ObjectLookupsFindNoKernelObject()
    {
        uint info = _harness.Alloc(0x100);
        uint count = _harness.Alloc(4);
        GuestMemory.W32(count, 0);
        Assert.Equal(Argument, Call(Debug, "ksceUlobjMgr_525A9BDB", 0, 0x81000000u, 0u));
        Assert.Equal(Fault, Call(Debug, "ksceUlobjMgr_525A9BDB", 0, 0x10u, info));
        Assert.Equal(Argument, Call(Debug, "ksceUlobjMgr_6D2C8928", 0, 0x81000000u, info, 0u, count));
        Assert.Equal(Fault, Call(Debug, "ksceUlobjMgr_6D2C8928", 0, 0x10u, info, 4u, count));
        Assert.Equal(NoObject, Call(Debug, "ksceUlobjMgr_6D2C8928", 0, info, info, 4u, count));
        GuestMemory.W32(count, 0xFFFFFFFF);
        Assert.Equal(Argument, Call(Debug, "ksceUlobjMgr_74B9199C", 0, 0x81000000u, info, 4u, count));
        Assert.Equal(NoObject, Call(Debug, "ksceUlobjMgr_CDAD91AD", 0, 0x81000000u, 1u));
        Assert.Equal(0, Call(Debug, "ksceUlobjMgr_BB11EB2F", 0, info, 4u, 0u, 1u));
        Assert.Equal(Argument, Call(Debug, "ksceUlobjMgr_BB11EB2F", 0, info, 4u, 0u, 9u));
    }

    // ---- feat/kern9's object queries (e2b7d4a9), run against feat/kernO's implementation

    private int Pid => _harness.Emu.Pid;

    /// <summary>A libult object header: the name, the attributes at +0x1C, the size at +0x20 and the kind at +0x22.</summary>
    private uint Object(uint size, byte kind, string name, uint attributes)
    {
        uint address = _harness.Alloc(size);
        GuestMemory.WriteCString(address, name);
        GuestMemory.W32(address + 0x1C, attributes);
        GuestMemory.Write(address + 0x20, (ushort)size);
        GuestMemory.Write(address + 0x22, kind);
        return address;
    }

    /// <summary>A 12-byte list node: flags, the next node, the waiter.</summary>
    private uint Node(uint flags, uint next, uint waiter)
    {
        uint node = _harness.Alloc(0x10);
        GuestMemory.W32(node, flags);
        GuestMemory.W32(node + 4, next);
        GuestMemory.W32(node + 8, waiter);
        return node;
    }

    /// <summary>A waiter whose first word is the thread id doubled plus one.</summary>
    private uint Waiter(uint threadId)
    {
        uint waiter = _harness.Alloc(0x10);
        GuestMemory.W32(waiter, threadId << 1 | 1);
        return waiter;
    }

    /// <summary>A waiting list: the head, then a waiter with thread id 5 and one whose first word points at the id 9.</summary>
    private uint TwoWaiters()
    {
        uint indirect = _harness.Alloc(0x10);
        GuestMemory.W32(indirect, _harness.Word(9));
        uint second = Node(0, 0, indirect);
        uint first = Node(0, second, Waiter(5));
        return Node(0, first, 0);
    }

    /// <summary>A waiting queue pool admitting <paramref name="limit"/> waiters.</summary>
    private uint Pool(uint limit)
    {
        uint pool = Object(0x50, 6, "pool", 0);
        GuestMemory.W32(pool + 0x24, limit);
        return pool;
    }

    private static void AssertHeader(uint info, uint record, uint address, string name, uint attributes)
    {
        Assert.Equal(record, GuestMemory.R32(info));
        Assert.Equal(address, GuestMemory.R32(info + 4));
        Assert.Equal(name, GuestMemory.ReadCString(info + 8, 0x1C));
        Assert.Equal(attributes, GuestMemory.R32(info + 0x24));
    }

    [Fact]
    public void TheQueriesCheckTheirArgumentsAndTheObjectsKind()
    {
        uint info = _harness.Alloc(0x100);
        uint list = _harness.Alloc(0x40);
        uint count = _harness.Word(0);
        Assert.Equal(Argument, Call(Debug, "ksceUlobjMgr_525A9BDB", Pid, 0x81000000u, 0u));
        Assert.Equal(Argument, Call(Debug, "ksceUlobjMgr_FFC8C811", Pid, 0x81000000u, 0u, 0u));
        Assert.Equal(Argument, Call(Debug, "ksceUlobjMgr_6D2C8928", Pid, 0x81000000u, info, 0u, count));
        Assert.Equal(Argument, Call(Debug, "ksceUlobjMgr_362C9488", Pid, 0x81000000u, 0u, list, count));
        GuestMemory.W32(count, 0xFFFFFFFF);
        Assert.Equal(Argument, Call(Debug, "ksceUlobjMgr_74B9199C", Pid, 0x81000000u, info, list, count));
        Assert.Equal(Argument, Call(Debug, "ksceUlobjMgr_6BCB4568", Pid, 0x81000000u, 0u));
        Assert.Equal(Argument, Call(Debug, "ksceUlobjMgr_ADAAA961", Pid, 0x81000000u, 0u));
        GuestMemory.W32(count, 4);
        // a semaphore is no mutex, condition, pool or lock; a mutex too small is none either
        uint semaphore = Object(0x6C, 3, "sema", 0);
        Assert.Equal(NoObject, Call(Debug, "ksceUlobjMgr_6D2C8928", Pid, semaphore, info, list, count));
        Assert.Equal(NoObject, Call(Debug, "ksceUlobjMgr_362C9488", Pid, semaphore, info, list, count));
        Assert.Equal(NoObject, Call(Debug, "ksceUlobjMgr_6BCB4568", Pid, semaphore, info));
        Assert.Equal(NoObject, Call(Debug, "ksceUlobjMgr_ADAAA961", Pid, semaphore, info));
        uint small = Object(100, 1, "mutex", 0);
        GuestMemory.Write(small + 0x20, (ushort)99);
        Assert.Equal(NoObject, Call(Debug, "ksceUlobjMgr_6D2C8928", Pid, small, info, list, count));
        // the semaphore's pool must be a pool
        GuestMemory.W32(semaphore + 0x68, Object(0x50, 3, "notpool", 0));
        Assert.Equal(NoObject, Call(Debug, "ksceUlobjMgr_74B9199C", Pid, semaphore, info, list, count));
        Assert.NotEqual(0, Call(Debug, "ksceUlobjMgr_6D2C8928", 0x7FFF0001, semaphore, info, list, count));
    }

    /// <summary>The mutex query (SceUlobjMgr 3.74 0x81001868): its waiters' thread ids (direct and through a word), the
    /// count lowered to what fits, the owner of a held mutex; more waiters than the pool admits 0x80022005.</summary>
    [Fact]
    public void AMutexReportsItsOwnerAndWaiters()
    {
        uint mutex = Object(100, 1, "mutex", 0xAB123456);
        GuestMemory.W32(mutex + 0x24, 3);
        GuestMemory.W32(mutex + 0x28, 7u << 1 | 1);
        GuestMemory.W32(mutex + 0x2C, 2);
        GuestMemory.W32(mutex + 0x30, 1);
        GuestMemory.W32(mutex + 0x34, TwoWaiters());
        GuestMemory.W32(mutex + 0x60, Pool(8));
        uint info = _harness.Alloc(0x40);
        uint list = _harness.Alloc(0x10);
        uint count = _harness.Word(1);
        Assert.Equal(0, Call(Debug, "ksceUlobjMgr_6D2C8928", Pid, mutex, info, list, count));
        AssertHeader(info, 0x32, mutex, "mutex", 0x123456);
        Assert.Equal(1u, GuestMemory.R32(count));
        Assert.Equal(5u, GuestMemory.R32(list));
        Assert.Equal(0u, GuestMemory.R32(list + 4));
        Assert.Equal(1u, GuestMemory.R32(info + 0x28));
        Assert.Equal(GuestMemory.R32(mutex + 0x60), GuestMemory.R32(info + 0x2C));
        Assert.Equal((1u, 2u, 7u, 2u), (GuestMemory.R32(info + 0x30), GuestMemory.R32(info + 0x34), GuestMemory.R32(info + 0x38), GuestMemory.R32(info + 0x3C)));

        GuestMemory.W32(count, 4);
        Assert.Equal(0, Call(Debug, "ksceUlobjMgr_6D2C8928", Pid, mutex, info, list, count));
        Assert.Equal((2u, 5u, 9u), (GuestMemory.R32(count), GuestMemory.R32(list), GuestMemory.R32(list + 4)));

        // not held: no owner
        GuestMemory.W32(mutex + 0x30, 0);
        Assert.Equal(0, Call(Debug, "ksceUlobjMgr_6D2C8928", Pid, mutex, info, list, count));
        Assert.Equal((0u, 0u), (GuestMemory.R32(info + 0x30), GuestMemory.R32(info + 0x38)));

        GuestMemory.W32(GuestMemory.R32(mutex + 0x60) + 0x24, 1);
        Assert.Equal(unchecked((int)0x80022005), Call(Debug, "ksceUlobjMgr_6D2C8928", Pid, mutex, info, list, count));
    }

    /// <summary>The condition variable's waiters go through its mutex's pool; the semaphore reports its count.</summary>
    [Fact]
    public void AConditionAndASemaphoreReportTheirWaiters()
    {
        uint mutex = Object(100, 1, "mutex", 0);
        GuestMemory.W32(mutex + 0x60, Pool(8));
        uint condition = Object(0x44, 2, "cond", 0x01000002);
        GuestMemory.W32(condition + 0x2C, TwoWaiters());
        GuestMemory.W32(condition + 0x40, mutex);
        uint info = _harness.Alloc(0x40);
        uint list = _harness.Alloc(0x10);
        uint count = _harness.Word(4);
        Assert.Equal(0, Call(Debug, "ksceUlobjMgr_362C9488", Pid, condition, info, list, count));
        AssertHeader(info, 0xA2, condition, "cond", 2);
        Assert.Equal((0u, mutex, 2u), (GuestMemory.R32(info + 0x28), GuestMemory.R32(info + 0x2C), GuestMemory.R32(info + 0x30)));
        Assert.Equal((2u, 5u, 9u), (GuestMemory.R32(count), GuestMemory.R32(list), GuestMemory.R32(list + 4)));
        GuestMemory.Write(mutex + 0x22, (byte)3);
        Assert.Equal(NoObject, Call(Debug, "ksceUlobjMgr_362C9488", Pid, condition, info, list, count));

        uint semaphore = Object(0x6C, 3, "sema", 0);
        GuestMemory.W32(semaphore + 0x28, 4);
        GuestMemory.W32(semaphore + 0x2C, TwoWaiters());
        GuestMemory.W32(semaphore + 0x68, Pool(8));
        Assert.Equal(0, Call(Debug, "ksceUlobjMgr_74B9199C", Pid, semaphore, info, list, count));
        AssertHeader(info, 0x12, semaphore, "sema", 0);
        Assert.Equal((0u, GuestMemory.R32(semaphore + 0x68), 4u, 2u),
            (GuestMemory.R32(info + 0x28), GuestMemory.R32(info + 0x2C), GuestMemory.R32(info + 0x30), GuestMemory.R32(info + 0x34)));
    }

    /// <summary>The pool and the reader/writer lock count their waiting list and the free entries of their free list
    /// (all ones when the list is longer than the size).</summary>
    [Fact]
    public void APoolAndALockCountTheirLists()
    {
        uint free = Node(0, Node(0, 0, 0), 0);
        uint pool = Pool(8);
        GuestMemory.W32(pool + 0x28, 4);
        GuestMemory.W32(pool + 0x34, TwoWaiters());
        GuestMemory.W32(pool + 0x4C, free);
        uint info = _harness.Alloc(0x40);
        Assert.Equal(0, Call(Debug, "ksceUlobjMgr_6BCB4568", Pid, pool, info));
        AssertHeader(info, 0x43, pool, "pool", 0);
        Assert.Equal((8u, 2u, 4u, 3u), (GuestMemory.R32(info + 0x28), GuestMemory.R32(info + 0x2C), GuestMemory.R32(info + 0x30), GuestMemory.R32(info + 0x34)));
        GuestMemory.W32(pool + 0x24, 1);
        Assert.Equal(0, Call(Debug, "ksceUlobjMgr_6BCB4568", Pid, pool, info));
        Assert.Equal(0xFFFFFFFFu, GuestMemory.R32(info + 0x2C));
        GuestMemory.W32(pool + 0x28, 0);
        Assert.Equal(0, Call(Debug, "ksceUlobjMgr_6BCB4568", Pid, pool, info));
        Assert.Equal(0xFFFFFFFFu, GuestMemory.R32(info + 0x34));

        uint rwlock = Object(0xAC, 7, "rwlock", 0);
        GuestMemory.W32(rwlock + 0x24, 5);
        GuestMemory.W32(rwlock + 0x28, 4);
        GuestMemory.W32(rwlock + 0x34, TwoWaiters());
        GuestMemory.W32(rwlock + 0x4C, free);
        GuestMemory.W32(rwlock + 0xA4, 0x77);
        Assert.Equal(0, Call(Debug, "ksceUlobjMgr_ADAAA961", Pid, rwlock, info));
        AssertHeader(info, 0x42, rwlock, "rwlock", 0);
        Assert.Equal((0u, 5u, 2u, 0x77u), (GuestMemory.R32(info + 0x28), GuestMemory.R32(info + 0x2C), GuestMemory.R32(info + 0x30), GuestMemory.R32(info + 0x34)));
        Assert.Equal((4u, 3u), (GuestMemory.R32(info + 0x38), GuestMemory.R32(info + 0x3C)));
    }

    /// <summary>An object with a table of entries: the information counts the set ones; the listing skips and stops at
    /// its maximum.</summary>
    [Fact]
    public void ATablesSetEntriesAreCountedAndListed()
    {
        uint table = _harness.Alloc(0x10);
        GuestMemory.W32(table + 4, 11);
        GuestMemory.W32(table + 0xC, 12);
        uint obj = Object(0x34, 0, "table", 0xFF000010);
        GuestMemory.W32(obj + 0x2C, 4);
        GuestMemory.W32(obj + 0x30, table);
        uint info = _harness.Alloc(0x40);
        Assert.Equal(0, Call(Debug, "ksceUlobjMgr_525A9BDB", Pid, obj, info));
        Assert.Equal("table", GuestMemory.ReadCString(info, 0x1C));
        Assert.Equal((0x10u, obj, 0x34u, 4u, 2u),
            (GuestMemory.R32(info + 0x1C), GuestMemory.R32(info + 0x20), GuestMemory.R32(info + 0x24), GuestMemory.R32(info + 0x28), GuestMemory.R32(info + 0x2C)));

        uint list = _harness.Alloc(0x10);
        Assert.Equal(2, Call(Debug, "ksceUlobjMgr_3D6E2EF3", Pid, obj, 0u, 0u, 0u));
        Assert.Equal(0, Call(Debug, "ksceUlobjMgr_3D6E2EF3", Pid, obj, list, 0u, 0u));
        Assert.Equal(1, Call(Debug, "ksceUlobjMgr_3D6E2EF3", Pid, obj, list, 1u, 1u));
        Assert.Equal(12u, GuestMemory.R32(list));
        Assert.Equal(2, Call(Debug, "ksceUlobjMgr_3D6E2EF3", Pid, obj, list, 4u, 0u));
        Assert.Equal((11u, 12u), (GuestMemory.R32(list), GuestMemory.R32(list + 4)));
    }

    /// <summary>A thread's information maps libult's state (0-5 to 0, 1, 4, 5, 3, 2; others 6) and reports +0x5C only
    /// with the 0x5AA55AA5 mark at +0x24.</summary>
    [Fact]
    public void AThreadMapsItsState()
    {
        uint thread = Object(0xB8, 0, "thread", 0x00000042);
        GuestMemory.Write(thread + 0xA0, (ushort)2);
        GuestMemory.W32(thread + 0x5C, 0x99);
        GuestMemory.W32(thread + 0xA8, 0xA8);
        GuestMemory.W32(thread + 0xAC, 0xAC);
        GuestMemory.W32(thread + 0xB0, 0xB0);
        uint info = _harness.Alloc(0x40);
        Assert.Equal(0, Call(Debug, "ksceUlobjMgr_FFC8C811", Pid, thread, 0x1234u, info));
        Assert.Equal("thread", GuestMemory.ReadCString(info, 0x1C));
        Assert.Equal((0x42u, thread, 0x1234u, thread + 0x20, 4u),
            (GuestMemory.R32(info + 0x1C), GuestMemory.R32(info + 0x20), GuestMemory.R32(info + 0x24), GuestMemory.R32(info + 0x28), GuestMemory.R32(info + 0x2C)));
        Assert.Equal((0xA8u, 0u, 0xACu, 0xB0u), (GuestMemory.R32(info + 0x30), GuestMemory.R32(info + 0x34), GuestMemory.R32(info + 0x38), GuestMemory.R32(info + 0x3C)));
        GuestMemory.W32(thread + 0x24, 0x5AA55AA5);
        GuestMemory.Write(thread + 0xA0, (ushort)9);
        Assert.Equal(0, Call(Debug, "ksceUlobjMgr_FFC8C811", Pid, thread, 0u, info));
        Assert.Equal((6u, 0x99u), (GuestMemory.R32(info + 0x2C), GuestMemory.R32(info + 0x34)));
    }
}
