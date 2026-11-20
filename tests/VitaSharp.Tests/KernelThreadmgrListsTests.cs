using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;
using Mutex = VitaSharp.Core.Kernel.Mutex;
using Semaphore = VitaSharp.Core.Kernel.Semaphore;

namespace VitaSharp.Tests;

/// <summary>
/// The thread manager's object lists for the debugger as FW 3.74 threadmgr has them (decompiled, study only): the
/// buffer check, the walk that copies while the entries fit and counts the rest, *count left alone for a class with no
/// object, the process lists (the kernel's pid refused), the class membership (fast and lightweight mutexes out of the
/// mutex class, the event class holding simple events, pipes and timers) and the info calls by UID (size check first,
/// the class's unknown-ID error, the caller's size copied).
/// </summary>
[Collection("guest")]
public sealed class KernelThreadmgrListsTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int InvalidArgument = unchecked((int)0x80020005), IllegalSize = unchecked((int)0x8002000B),
        InvalidPid = unchecked((int)0x80029001), UnknownAlarmId = unchecked((int)0x800280C1),
        UnknownEventId = unchecked((int)0x80028201), UnknownRwLockId = unchecked((int)0x800281E1);

    private const int KernelPid = 0x10005;

    private readonly HleHarness _harness = new(fixture, "kthrlists");

    public void Dispose() => _harness.Dispose();

    private ThreadmgrKernel Threadmgr => _harness.Module<ThreadmgrKernel>();

    private ThreadMgr UserThreadmgr => _harness.Module<ThreadMgr>();

    private int Count<T>(Func<T, bool>? filter = null) where T : KernelObject
        => _harness.Emu.Kernel.Objects.All<T>().Count(o => filter == null || filter(o));

    private List<int> UidsOf(uint buffer, uint stride, int entries)
    {
        var uids = new List<int>();
        for (int i = 0; i < entries; i++)
        {
            Assert.Equal(stride, GuestMemory.R32(buffer + (uint)i * stride));
            uids.Add((int)GuestMemory.R32(buffer + (uint)i * stride + 4));
        }

        return uids;
    }

    [Fact]
    public void TheSemaphoreListCopiesTheEntriesThatFitAndCountsTheRest()
    {
        _harness.OnGuest(_ =>
        {
            int first = Call(UserThreadmgr, "sceKernelCreateSema", "list-a", 0u, 1, 5, 0u);
            int second = Call(UserThreadmgr, "sceKernelCreateSema", "list-b", 0u, 2, 5, 0u);
            int third = Call(UserThreadmgr, "sceKernelCreateSema", "list-c", 0u, 3, 5, 0u);
            int total = Count<Semaphore>();
            uint buffer = _harness.Alloc(0x3C * 64);
            uint count = _harness.Word(0xCCCC);
            Assert.Equal(total, Call(Threadmgr, "NID_C6741986", 0u, buffer, 0x3Cu * 64, count));
            Assert.Equal((uint)total, GuestMemory.R32(count));
            List<int> uids = UidsOf(buffer, 0x3C, total);
            Assert.Contains(first, uids);
            Assert.Contains(second, uids);
            Assert.Contains(third, uids);
            Assert.Equal(uids.OrderBy(u => u), uids);
            int at = uids.IndexOf(second);
            Assert.Equal(2u, GuestMemory.R32(buffer + (uint)at * 0x3C + 0x30)); // the current count of the sema info
            Assert.Equal("list-b", GuestMemory.ReadCString(buffer + (uint)at * 0x3C + 8));

            // two entries fit: two copied, every one counted
            GuestMemory.Fill(buffer, 0xEE, 0x3C * 3);
            Assert.Equal(total, Call(Threadmgr, "NID_C6741986", 0u, buffer, 0x3Cu * 2 + 0x10, count));
            Assert.Equal(2u, GuestMemory.R32(count));
            Assert.Equal(0xEEEEEEEEu, GuestMemory.R32(buffer + 0x3C * 2));

            // no buffer and less than one entry: only the count
            Assert.Equal(total, Call(Threadmgr, "NID_C6741986", 0u, 0u, 0x10u, count));
            Assert.Equal(0u, GuestMemory.R32(count));
            // no buffer for a whole entry
            Assert.Equal(InvalidArgument, Call(Threadmgr, "NID_C6741986", 0u, 0u, 0x3Cu, count));
        });
    }

    [Fact]
    public void AClassWithNoObjectAnswersZeroAndLeavesTheCountAlone()
    {
        uint buffer = _harness.Alloc(0x100);
        uint count = _harness.Word(0xCCCC);
        Assert.Equal(0, Call(Threadmgr, "NID_2828F884", 0u, buffer, 0x100u, count)); // alarms
        Assert.Equal(0xCCCCu, GuestMemory.R32(count));
        Assert.Equal(0, Call(Threadmgr, "NID_920EA1CA", 0u, buffer, 0x100u, count)); // work queues
        Assert.Equal(0, Call(Threadmgr, "NID_E605ED7A", 0u, buffer, 0x100u, count)); // work tasks
        Assert.Equal(0xCCCCu, GuestMemory.R32(count));
        Assert.Equal(InvalidArgument, Call(Threadmgr, "NID_E605ED7A", 0u, 0u, 0x58u, count));
    }

    [Fact]
    public void TheProcessListsRefuseTheKernelAndAnUnknownPidAndListTheProcesssObjects()
    {
        _harness.OnGuest(_ =>
        {
            int flag = Call(UserThreadmgr, "sceKernelCreateEventFlag", "list-evf", 0u, 0x5u, 0u);
            uint buffer = _harness.Alloc(0x38 * 64);
            uint count = _harness.Word(0);
            Assert.Equal(InvalidArgument, Call(Threadmgr, "NID_0EB0ABF0", KernelPid, 0u, buffer, 0x38u * 64, count));
            Assert.Equal(InvalidPid, Call(Threadmgr, "NID_0EB0ABF0", 0x7FFF0001, 0u, buffer, 0x38u * 64, count));
            Assert.Equal(InvalidArgument, Call(Threadmgr, "NID_0EB0ABF0", 0x7FFF0001, 0u, 0u, 0x38u, count));
            int pid = _harness.Emu.Pid;
            int mine = Count<EventFlag>(e => e.OwnerPid == pid);
            Assert.Equal(mine, Call(Threadmgr, "NID_0EB0ABF0", pid, 0u, buffer, 0x38u * 64, count));
            Assert.Contains(flag, UidsOf(buffer, 0x38, (int)GuestMemory.R32(count)));
        });
    }

    [Fact]
    public void TheMutexListHasNeitherLightweightNorFastMutexesAndTheKernelInfoIs0x40()
    {
        _harness.OnGuest(_ =>
        {
            int plain = Call(UserThreadmgr, "sceKernelCreateMutex", "list-mutex", 0u, 0, 0u);
            uint lwWork = _harness.Alloc(0x20);
            Assert.Equal(0, Call(UserThreadmgr, "sceKernelCreateLwMutex", lwWork, "list-lw", 0u, 0, 0u));
            int light = (int)GuestMemory.R32(lwWork + Mutex.LwUidOffset);
            uint fastWork = _harness.Alloc(0x40);
            Assert.Equal(0, Call(Threadmgr, "ksceKernelInitializeFastMutex", fastWork, "list-fast", 0u, 0u));
            int fast = (int)GuestMemory.R32(fastWork + 8);

            uint buffer = _harness.Alloc(0x40 * 64);
            GuestMemory.Fill(buffer, 0xEE, 0x40 * 64);
            uint count = _harness.Word(0);
            int listed = Call(Threadmgr, "NID_B6DA4669", 0u, buffer, 0x40u * 64, count);
            List<int> uids = UidsOf(buffer, 0x40, listed);
            Assert.Contains(plain, uids);
            Assert.DoesNotContain(light, uids);
            Assert.DoesNotContain(fast, uids);
            Assert.Equal(0u, GuestMemory.R32(buffer + (uint)uids.IndexOf(plain) * 0x40 + 0x3C));

            int lwListed = Call(Threadmgr, "NID_C2DA6286", 0u, buffer, 0x40u * 64, count);
            List<int> lwUids = UidsOf(buffer, 0x40, lwListed);
            Assert.Contains(light, lwUids);
            Assert.DoesNotContain(plain, lwUids);
        });
    }

    [Fact]
    public void TheEventClassHoldsSimpleEventsPipesAndTimersAndItsInfoReadsAnyOfThem()
    {
        _harness.OnGuest(_ =>
        {
            int simple = Call(UserThreadmgr, "sceKernelCreateSimpleEvent", _harness.Str("list-simple"), 0u, 0x03000000u, 0u);
            Assert.True(simple > 0);
            int pipe = Call(UserThreadmgr, "sceKernelCreateMsgPipe", "list-pipe", 0x40u, 0u, 0x100u, 0u);
            int timer = Call(UserThreadmgr, "sceKernelCreateTimer", "list-timer", 0u, 0u);
            uint buffer = _harness.Alloc(0x40 * 64);
            uint count = _harness.Word(0);
            int listed = Call(Threadmgr, "NID_995F32E1", 0u, buffer, 0x40u * 64, count);
            List<int> uids = UidsOf(buffer, 0x40, listed);
            Assert.Contains(simple, uids);
            Assert.Contains(pipe, uids);
            Assert.Contains(timer, uids);
            List<int> simpleUids = UidsOf(buffer, 0x40, Call(Threadmgr, "NID_4DE77569", 0u, buffer, 0x40u * 64, count));
            Assert.Contains(simple, simpleUids);
            Assert.DoesNotContain(pipe, simpleUids);

            uint info = _harness.Alloc(0x48);
            GuestMemory.W32(info, 0x40);
            Assert.Equal(0, Call(Threadmgr, "NID_CBA7FAAA", pipe, info));
            Assert.Equal((uint)pipe, GuestMemory.R32(info + 4));
            Assert.Equal("list-pipe", GuestMemory.ReadCString(info + 8));
            Assert.Equal(0, Call(Threadmgr, "NID_CBA7FAAA", simple, info));
            Assert.Equal(0x03000000u, GuestMemory.R32(info + 0x2C) & 0xFF000000u);
            Assert.Equal(UnknownEventId, Call(Threadmgr, "NID_CBA7FAAA", Call(UserThreadmgr, "sceKernelCreateSema", "not-event", 0u, 0, 1, 0u), info));
        });
    }

    [Fact]
    public void InfoCallsCheckTheSizeFirstAndCopyTheCallersSize()
    {
        _harness.OnGuest(_ =>
        {
            int rwlock = Call(UserThreadmgr, "sceKernelCreateRWLock", "list-rw", 0u, 0u);
            uint info = _harness.Alloc(0x48);
            GuestMemory.W32(info, 0x3D);
            Assert.Equal(IllegalSize, Call(Threadmgr, "NID_374C3267", 0x7FFF0001, info));
            GuestMemory.Fill(info, 0xEE, 0x48);
            GuestMemory.W32(info, 0x10);
            Assert.Equal(0, Call(Threadmgr, "NID_374C3267", rwlock, info));
            Assert.Equal(0x3Cu, GuestMemory.R32(info));
            Assert.Equal((uint)rwlock, GuestMemory.R32(info + 4));
            Assert.Equal(0xEEEEEEEEu, GuestMemory.R32(info + 0x10));
            GuestMemory.W32(info, 0x3C);
            Assert.Equal(UnknownRwLockId, Call(Threadmgr, "NID_374C3267", 0x7FFF0001, info));
            GuestMemory.W32(info, 0x38);
            Assert.Equal(UnknownAlarmId, Call(Threadmgr, "NID_4A038803", 1, info));
            GuestMemory.W32(info, 0x39);
            Assert.Equal(IllegalSize, Call(Threadmgr, "NID_4A038803", 1, info));
        });
    }

    [Fact]
    public void EveryClassListAnswersItsOwnObjects()
    {
        _harness.OnGuest(_ =>
        {
            int mutex = Call(UserThreadmgr, "sceKernelCreateMutex", "cls-mutex", 0u, 0, 0u);
            int cond = Call(UserThreadmgr, "sceKernelCreateCond", "cls-cond", 0u, mutex, 0u);
            int rwlock = Call(UserThreadmgr, "sceKernelCreateRWLock", "cls-rw", 0u, 0u);
            int pipe = Call(UserThreadmgr, "sceKernelCreateMsgPipe", "cls-pipe", 0x40u, 0u, 0x100u, 0u);
            int callback = Call(UserThreadmgr, "sceKernelCreateCallback", "cls-cb", 0u, 0x81000001u, 0u);
            int timer = Call(UserThreadmgr, "sceKernelCreateTimer", "cls-timer", 0u, 0u);
            uint lwMutexWork = _harness.Alloc(0x20);
            Call(UserThreadmgr, "sceKernelCreateLwMutex", lwMutexWork, "cls-lwm", 0u, 0, 0u);
            uint lwCondWork = _harness.Alloc(0x20);
            Assert.Equal(0, Call(UserThreadmgr, "sceKernelCreateLwCond", lwCondWork, "cls-lwc", 0u, lwMutexWork, 0u));
            int lwCond = _harness.Emu.Kernel.Objects.All<CondVar>().Single(c => c.WorkArea == lwCondWork).Uid;
            int pid = _harness.Emu.Pid;
            (string All, string Process, uint Stride, int Uid)[] classes =
            [
                ("NID_67022B18", "NID_CA704239", 0x34, cond),
                ("NID_E2B57231", "NID_6624E612", 0x3C, rwlock),
                ("NID_C7F5FFE0", "NID_E9E50096", 0x3C, pipe),
                ("NID_767CA30C", "NID_FF8DA217", 0x44, callback),
                ("NID_3C297724", "NID_C327CA7C", 0x60, timer),
                ("NID_738FF63D", "NID_09346D1A", 0x38, lwCond),
            ];
            uint buffer = _harness.Alloc(0x60 * 64);
            uint count = _harness.Word(0);
            foreach ((string all, string process, uint stride, int uid) in classes)
            {
                int listed = Call(Threadmgr, all, 0u, buffer, stride * 64, count);
                Assert.True(UidsOf(buffer, stride, listed).Contains(uid), all);
                listed = Call(Threadmgr, process, pid, 0u, buffer, stride * 64, count);
                Assert.True(UidsOf(buffer, stride, listed).Contains(uid), process);
            }
        });
    }
}
