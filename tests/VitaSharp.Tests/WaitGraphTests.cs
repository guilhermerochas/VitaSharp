using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using static VitaSharp.Tests.HleHarness;
using Mutex = VitaSharp.Core.Kernel.Mutex;
using Semaphore = VitaSharp.Core.Kernel.Semaphore;

namespace VitaSharp.Tests;

/// <summary>
/// The wait graph of the thread dumps (watchdog stall, F8 capture, end of run): a waiting thread is followed through the
/// object it waits on to the thread that holds or ends it (a mutex's owner, the thread a thread-end wait waits for), to
/// the end of the chain; a chain that comes back to a thread already in it is reported as a deadlock. A boot hang
/// (Dead Space: 'main' in sceKernelWaitThreadEndCB on a 'pthread' stuck in sceKernelLoadStartModule) is read from it
/// without a debugger.
/// </summary>
[Collection("guest")]
public sealed class WaitGraphTests(GuestMemoryFixture fixture) : IDisposable
{
    private readonly HleHarness _h = new(fixture, "wait-graph");

    private ThreadMgr Tm => _h.Module<ThreadMgr>();

    public void Dispose() => _h.Dispose();

    private List<string> Graph() => WaitGraph.Lines(_h.Emu.Kernel.Threads);

    private static string N(GuestRun run) => WaitGraph.Named(run.Thread);

    /// <summary>A thread-end wait leads to the thread it waits for, and that thread's mutex wait to the mutex's owner.</summary>
    [Fact]
    public void AThreadEndWaitIsFollowedToTheMutexOwner()
    {
        _h.OnGuest(main =>
        {
            int mutex = Call(Tm, "sceKernelCreateMutex", "wg-mutex", 0u, 0, 0u);
            Assert.Equal(0, Call(Tm, "sceKernelLockMutex", mutex, 1, new Ptr<uint>(0)));
            GuestRun locker = _h.Start(_ =>
            {
                Assert.Equal(0, Call(Tm, "sceKernelLockMutex", mutex, 1, new Ptr<uint>(0)));
                Assert.Equal(0, Call(Tm, "sceKernelUnlockMutex", mutex, 1));
            }, "wg-locker");
            Until(() => locker.Thread.Status == ThreadStatus.Waiting && locker.Thread.CurrentWaitSync is Mutex, "the mutex wait");
            GuestRun joiner = _h.Start(_ => Call(Tm, "sceKernelWaitThreadEnd", locker.Thread.Uid, new Ptr<int>(0), new Ptr<uint>(0)), "wg-joiner");
            Until(() => joiner.Thread.Status == ThreadStatus.Waiting && ReferenceEquals(joiner.Thread.CurrentWaitSync, locker.Thread.WaitLock), "the thread-end wait");

            List<string> graph = Graph();
            Assert.Contains($"wait {N(joiner)} -> end of {N(locker)} (Waiting) -> mutex 'wg-mutex' held by {WaitGraph.Named(main)} (Running)", graph);
            Assert.Contains($"wait {N(locker)} -> mutex 'wg-mutex' held by {WaitGraph.Named(main)} (Running)", graph);
            Assert.DoesNotContain(graph, line => line.Contains("DEADLOCK", StringComparison.Ordinal));

            Assert.Equal(0, Call(Tm, "sceKernelUnlockMutex", mutex, 1));
            _h.Join(locker);
            _h.Join(joiner);
            Assert.Empty(Graph());
        });
    }

    /// <summary>Two threads that each hold the mutex the other waits for: the chain comes back, a deadlock.</summary>
    [Fact]
    public void TwoThreadsWaitingForEachOthersMutexAreADeadlock()
    {
        using var clock = ControlledGuestClock.Stop();
        _h.OnGuest(main =>
        {
            const uint LimitUs = 1_000_000;
            int first = Call(Tm, "sceKernelCreateMutex", "wg-first", 0u, 0, 0u);
            int second = Call(Tm, "sceKernelCreateMutex", "wg-second", 0u, 0, 0u);
            int[] results = [1, 1];
            using var bothHold = new CountdownEvent(2);
            GuestRun a = _h.Start(_ =>
            {
                Assert.Equal(0, Call(Tm, "sceKernelLockMutex", first, 1, new Ptr<uint>(0)));
                bothHold.Signal();
                bothHold.Wait();
                results[0] = Call(Tm, "sceKernelLockMutex", second, 1, new Ptr<uint>(_h.Word(LimitUs)));
                Assert.Equal(0, Call(Tm, "sceKernelUnlockMutex", first, 1));
            }, "wg-a");
            GuestRun b = _h.Start(_ =>
            {
                Assert.Equal(0, Call(Tm, "sceKernelLockMutex", second, 1, new Ptr<uint>(0)));
                bothHold.Signal();
                bothHold.Wait();
                results[1] = Call(Tm, "sceKernelLockMutex", first, 1, new Ptr<uint>(_h.Word(LimitUs)));
                Assert.Equal(0, Call(Tm, "sceKernelUnlockMutex", second, 1));
            }, "wg-b");
            Until(() => a.Thread.Status == ThreadStatus.Waiting && a.Thread.CurrentWaitSync is Mutex
                && b.Thread.Status == ThreadStatus.Waiting && b.Thread.CurrentWaitSync is Mutex, "both mutex waits");

            List<string> graph = Graph();
            Assert.Contains($"wait {N(a)} -> mutex 'wg-second' held by {N(b)} (Waiting) -> mutex 'wg-first' held by {N(a)} (Waiting) (DEADLOCK: cycle)", graph);
            Assert.Contains($"wait {N(b)} -> mutex 'wg-first' held by {N(a)} (Waiting) -> mutex 'wg-second' held by {N(b)} (Waiting) (DEADLOCK: cycle)", graph);

            clock.Advance(LimitUs + 1);
            _h.Join(a);
            _h.Join(b);
            Assert.Equal([SceError.WAIT_TIMEOUT, SceError.WAIT_TIMEOUT], results);
        });
    }

    /// <summary>A semaphore has no owner: the line names its count and the thread that last signalled it.</summary>
    [Fact]
    public void ASemaphoreWaitNamesItsLastSignaller()
    {
        _h.OnGuest(main =>
        {
            int sema = Call(Tm, "sceKernelCreateSema", "wg-sema", 0u, 1, 1, 0u);
            Assert.Equal(0, Call(Tm, "sceKernelWaitSema", sema, 1, new Ptr<uint>(0)));
            Assert.Equal(0, Call(Tm, "sceKernelSignalSema", sema, 1));
            Assert.Equal(0, Call(Tm, "sceKernelWaitSema", sema, 1, new Ptr<uint>(0)));
            GuestRun waiter = _h.Start(_ => Assert.Equal(0, Call(Tm, "sceKernelWaitSema", sema, 1, new Ptr<uint>(0))), "wg-sema-waiter");
            Until(() => waiter.Thread.Status == ThreadStatus.Waiting && waiter.Thread.CurrentWaitSync is Semaphore, "the semaphore wait");

            Assert.Contains($"wait {N(waiter)} -> sema 'wg-sema' count 0, last signalled by {WaitGraph.Named(main)}", Graph());

            Assert.Equal(0, Call(Tm, "sceKernelSignalSema", sema, 1));
            _h.Join(waiter);
        });
    }
}
