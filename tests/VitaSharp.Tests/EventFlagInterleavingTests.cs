using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// Event flags under adversarial interleavings, against FW 3.74 threadmgr (sceKernelWaitEventFlag, Poll, Set, Cancel,
/// Delete and the waiter's wake handler, studied in the decompiled module): who a set satisfies and in what order,
/// what is left in the pattern, what a cancelled, deleted or timed-out waiter reports. A thread's host execution is
/// stopped where the race happens, with the <see cref="GuestThread.HleReturned"/> seam (a waker stopped right after
/// its call) or by holding the flag's lock (a woken thread whose host thread is late to run); the order is forced,
/// never left to the host's timing.
/// </summary>
[Collection("guest")]
public sealed class EventFlagInterleavingTests(GuestMemoryFixture fixture) : IDisposable
{
    private readonly HleHarness _h = new(fixture, "evf-interleavings");

    private ThreadMgr Tm => _h.Module<ThreadMgr>();

    public void Dispose() => _h.Dispose();

    private EventFlag Flag(int uid) => _h.Emu.Kernel.Objects.Get<EventFlag>(uid)!;

    /// <summary>
    /// threadmgr refuses a mode with bits other than OR, CLEAR and CLEAR_PAT, or with both clears, as ILLEGAL_MODE, in
    /// the wait and in the poll, without touching the pattern. The wait checks the mode before the bits and both before
    /// the UID; the poll checks the bits first.
    /// </summary>
    [Theory]
    [InlineData(EventFlag.WaitClear | EventFlag.WaitClearPat)]
    [InlineData(EventFlag.WaitOr | EventFlag.WaitClear | EventFlag.WaitClearPat)]
    [InlineData(8u)]
    [InlineData(0x10u)]
    [InlineData(0x80000000u)]
    public void WaitAndPollRefuseAnIllegalMode(uint mode)
    {
        _h.OnGuest(_ =>
        {
            int uid = Call(Tm, "sceKernelCreateEventFlag", "illegal-mode", 0u, 0x3u, 0u);
            uint outBits = _h.Word(0xDEADBEEF);
            Assert.Equal(SceError.ILLEGAL_MODE, Call(Tm, "sceKernelPollEventFlag", uid, 0x1u, mode, new Ptr<uint>(outBits)));
            Assert.Equal(SceError.ILLEGAL_MODE, Call(Tm, "sceKernelWaitEventFlag", uid, 0x1u, mode, new Ptr<uint>(outBits), new Ptr<uint>(0)));
            Assert.Equal(0xDEADBEEFu, GuestMemory.R32(outBits));
            Assert.Equal(0x3u, Flag(uid).Pattern);
            Assert.Equal(SceError.ILLEGAL_MODE, Call(Tm, "sceKernelWaitEventFlag", uid, 0u, mode, new Ptr<uint>(0), new Ptr<uint>(0)));
            Assert.Equal(SceError.INVALID_ARGUMENT, Call(Tm, "sceKernelPollEventFlag", uid, 0u, mode, new Ptr<uint>(0)));
            Assert.Equal(SceError.ILLEGAL_MODE, Call(Tm, "sceKernelWaitEventFlag", 0x7FFFFFF1, 0x1u, mode, new Ptr<uint>(0), new Ptr<uint>(0)));
            Assert.Equal(SceError.ILLEGAL_MODE, Call(Tm, "sceKernelPollEventFlag", 0x7FFFFFF1, 0x1u, mode, new Ptr<uint>(0)));
        });
    }

    /// <summary>
    /// On a flag without SCE_EVENT_WAITMULTIPLE the waiting thread owns the flag: another thread's poll is refused like
    /// a second wait (EVF_MULTI) and leaves the pattern alone, even when it would match. With WAITMULTIPLE the poll
    /// takes the bits.
    /// </summary>
    [Fact]
    public void PollFromAnotherThreadOnASingleWaiterFlagIsRefused()
    {
        _h.OnGuest(_ =>
        {
            foreach (bool multi in new[] { false, true })
            {
                int uid = Call(Tm, "sceKernelCreateEventFlag", multi ? "poll-multi" : "poll-single", multi ? EventFlag.AttrMulti : 0u, 0x2u, 0u);
                int waitResult = 1;
                GuestRun waiter = _h.Start(_ => waitResult = Call(Tm, "sceKernelWaitEventFlag", uid, 0x1u, EventFlag.WaitAnd | EventFlag.WaitClearPat,
                    new Ptr<uint>(0), new Ptr<uint>(0)), "evf-owner");
                Until(() => waiter.Thread.Status == ThreadStatus.Waiting, "the wait");
                uint outBits = _h.Word(0xDEADBEEF);
                int poll = Call(Tm, "sceKernelPollEventFlag", uid, 0x2u, EventFlag.WaitAnd | EventFlag.WaitClearPat, new Ptr<uint>(outBits));
                Assert.Equal(multi ? SceError.OK : SceError.EVF_MULTI, poll);
                Assert.Equal(multi ? 0x2u : 0xDEADBEEFu, GuestMemory.R32(outBits));
                Assert.Equal(multi ? 0u : 0x2u, Flag(uid).Pattern);
                Assert.Equal(0, Call(Tm, "sceKernelSetEventFlag", uid, 0x1u));
                _h.Join(waiter);
                Assert.Equal(0, waitResult);
            }
        });
    }

    /// <summary>
    /// A set walks the queue in order and applies each satisfied waiter's clear mode before it looks at the next one,
    /// so the setter's single call decides who gets the bits. Here the setter is stopped right after its set (the
    /// seam): the first CLEAR_PAT waiter took bit 0, the OR waiter behind it saw the pattern without that bit, the
    /// second CLEAR_PAT waiter for bit 0 is still waiting, and nothing the stopped setter does not do can change it. The
    /// woken threads are held at their next call behind the stopped setter.
    /// </summary>
    [Fact]
    public void ASetterStoppedAfterItsSetHasDecidedWhoGotTheBits()
    {
        _h.OnGuest(main =>
        {
            int uid = Call(Tm, "sceKernelCreateEventFlag", "walk", EventFlag.AttrMulti, 0u, 0u);
            EventFlag flag = Flag(uid);
            uint firstBits = _h.Word(0);
            uint orBits = _h.Word(0);
            uint secondBits = _h.Word(0);
            int secondResult = 1;
            GuestRun first = _h.Start(th =>
            {
                Assert.Equal(0, Call(Tm, "sceKernelWaitEventFlag", uid, 0x1u, EventFlag.WaitAnd | EventFlag.WaitClearPat, new Ptr<uint>(firstBits), new Ptr<uint>(0)));
                th.Core.ChargeInstructions(200_000); // its work: it would run ahead of the stopped setter
                th.DispatchForTest("sceKernelGetThreadId");
            }, "evf-first");
            Until(() => first.Thread.Status == ThreadStatus.Waiting, "the first wait");
            GuestRun or = _h.Start(th =>
            {
                Assert.Equal(0, Call(Tm, "sceKernelWaitEventFlag", uid, 0x3u, EventFlag.WaitOr, new Ptr<uint>(orBits), new Ptr<uint>(0)));
                th.Core.ChargeInstructions(200_000);
                th.DispatchForTest("sceKernelGetThreadId");
            }, "evf-or");
            Until(() => or.Thread.Status == ThreadStatus.Waiting, "the OR wait");
            GuestRun second = _h.Start(_ => secondResult = Call(Tm, "sceKernelWaitEventFlag", uid, 0x1u, EventFlag.WaitAnd | EventFlag.WaitClearPat,
                new Ptr<uint>(secondBits), new Ptr<uint>(0)), "evf-second");
            Until(() => second.Thread.Status == ThreadStatus.Waiting, "the second wait");
            first.Thread.WakerWaitLimitTicks = long.MaxValue; // the outcome may not depend on how long the host takes
            or.Thread.WakerWaitLimitTicks = long.MaxValue;
            (uint FirstBits, uint OrBits, uint Pattern, ThreadStatus Second, int Waiters)? whileStopped = null;
            GuestThread.HleReturned = (thread, function) =>
            {
                if (thread == main && function.Name == "sceKernelSetEventFlag")
                {
                    Until(() => first.Thread.IsWaitingForWaker && or.Thread.IsWaitingForWaker, "the woken threads' next calls");
                    whileStopped = (GuestMemory.R32(firstBits), GuestMemory.R32(orBits), flag.Pattern, second.Thread.Status, flag.Waiters);
                }
            };
            try
            {
                Assert.Equal(0u, main.DispatchForTest("sceKernelSetEventFlag", (uint)uid, 0x3u));
            }
            finally
            {
                GuestThread.HleReturned = null;
            }
            // the first waiter got the pattern the set reached it with, the OR waiter the one after the first's clear
            Assert.Equal((0x3u, 0x2u, 0x2u, ThreadStatus.Waiting, 1), whileStopped);
            Assert.Equal(0, Call(Tm, "sceKernelSetEventFlag", uid, 0x1u));
            Assert.Equal(0x2u, flag.Pattern); // the second waiter took bit 0 inside this set
            Assert.Equal(0, Call(Tm, "sceKernelDelayThread", 1000u)); // main blocks: the held threads go on
            _h.Join(first);
            _h.Join(or);
            _h.Join(second);
            Assert.Equal(0, secondResult);
            Assert.Equal(0x3u, GuestMemory.R32(secondBits));
        });
    }

    /// <summary>
    /// threadmgr writes a cancelled waiter's outBits in its wake handler, under the flag's lock, with the pattern the
    /// cancel set. A waiter whose host thread runs only after the canceller's next set (the lock is held from the
    /// cancel to the set) still reports the cancel's pattern, and the set's bits stay in the flag.
    /// </summary>
    [Fact]
    public void ACancelledWaiterReportsThePatternOfTheCancel()
    {
        _h.OnGuest(main =>
        {
            int uid = Call(Tm, "sceKernelCreateEventFlag", "cancel-bits", EventFlag.AttrMulti, 0x4u, 0u);
            EventFlag flag = Flag(uid);
            uint[] outBits = [_h.Word(0), _h.Word(0)];
            int[] results = [1, 1];
            var waiters = new GuestRun[2];
            for (int i = 0; i < 2; i++)
            {
                int index = i;
                waiters[i] = _h.Start(_ => results[index] = Call(Tm, "sceKernelWaitEventFlag", uid, 0x1u, EventFlag.WaitAnd | EventFlag.WaitClearPat,
                    new Ptr<uint>(outBits[index]), new Ptr<uint>(0)), $"evf-cancelled-{i}");
                Until(() => waiters[index].Thread.Status == ThreadStatus.Waiting, "the wait");
            }

            uint numWait = _h.Word(0);
            lock (flag)
            {
                Assert.Equal(0u, main.DispatchForTest("sceKernelCancelEventFlag", (uint)uid, 0x10u, numWait));
                Assert.Equal(0u, main.DispatchForTest("sceKernelSetEventFlag", (uint)uid, 0x1u));
            }
            foreach (GuestRun waiter in waiters)
            {
                _h.Join(waiter);
            }

            Assert.Equal(2u, GuestMemory.R32(numWait));
            Assert.Equal([SceError.WAIT_CANCEL, SceError.WAIT_CANCEL], results);
            Assert.Equal(0x10u, GuestMemory.R32(outBits[0]));
            Assert.Equal(0x10u, GuestMemory.R32(outBits[1]));
            Assert.Equal(0x11u, flag.Pattern);
        });
    }

    /// <summary>A deleted flag's waiters return WAIT_DELETE with the pattern of the delete, whenever their host threads
    /// run; the UID is gone for every call after it.</summary>
    [Fact]
    public void ADeletedWaiterReportsThePatternOfTheDelete()
    {
        _h.OnGuest(main =>
        {
            int uid = Call(Tm, "sceKernelCreateEventFlag", "delete-bits", 0u, 0x6u, 0u);
            EventFlag flag = Flag(uid);
            uint outBits = _h.Word(0);
            int result = 1;
            GuestRun waiter = _h.Start(_ => result = Call(Tm, "sceKernelWaitEventFlag", uid, 0x1u, EventFlag.WaitAnd,
                new Ptr<uint>(outBits), new Ptr<uint>(0)), "evf-deleted");
            Until(() => waiter.Thread.Status == ThreadStatus.Waiting, "the wait");
            lock (flag)
            {
                Assert.Equal(0u, main.DispatchForTest("sceKernelDeleteEventFlag", (uint)uid));
                Assert.Equal(unchecked((uint)SceError.UNKNOWN_EVF_ID), main.DispatchForTest("sceKernelSetEventFlag", (uint)uid, 0x1u));
            }
            _h.Join(waiter);
            Assert.Equal(SceError.WAIT_DELETE, result);
            Assert.Equal(0x6u, GuestMemory.R32(outBits));
            Assert.Equal(SceError.UNKNOWN_EVF_ID, Call(Tm, "sceKernelPollEventFlag", uid, 0x1u, 0u, new Ptr<uint>(0)));
        });
    }

    /// <summary>
    /// A wait's timeout is the kernel's timer at a guest-time deadline: once it has passed, the waiter is off the queue
    /// and a later set does not satisfy it, however late its host thread is to notice (the lock is held here while the
    /// test moves guest time past the deadline, then the set comes). The waiter returns WAIT_TIMEOUT with no time left, the
    /// pattern from before the set, and the set's bit stays for the next caller.
    /// </summary>
    [Fact]
    public void ASetAfterTheDeadlineDoesNotSatisfyALateWaiter()
    {
        using var clock = ControlledGuestClock.Stop();
        _h.OnGuest(_ =>
        {
            const uint LimitUs = 50_000;
            int uid = Call(Tm, "sceKernelCreateEventFlag", "deadline", 0u, 0x8u, 0u);
            EventFlag flag = Flag(uid);
            uint outBits = _h.Word(0);
            uint timeout = _h.Word(LimitUs);
            int result = 1;
            GuestRun waiter = _h.Start(_ => result = Call(Tm, "sceKernelWaitEventFlag", uid, 0x1u, EventFlag.WaitAnd | EventFlag.WaitClearPat,
                new Ptr<uint>(outBits), new Ptr<uint>(timeout)), "evf-late");
            Until(() => waiter.Thread.Status == ThreadStatus.Waiting, "the wait");
            lock (flag)
            {
                clock.Advance(LimitUs);
                Assert.Equal(0, Call(Tm, "sceKernelSetEventFlag", uid, 0x1u));
            }
            _h.Join(waiter);
            Assert.Equal(SceError.WAIT_TIMEOUT, result);
            Assert.Equal(0u, GuestMemory.R32(timeout));
            Assert.Equal(0x8u, GuestMemory.R32(outBits));
            Assert.Equal(0x9u, flag.Pattern);
            Assert.Equal(0, flag.Waiters);
        });
    }

    /// <summary>The other side of the deadline: a set inside the timeout satisfies the waiter even when its host thread
    /// only runs after the deadline (the kernel's timer finds it already woken), with the time left written back.</summary>
    [Fact]
    public void ASetBeforeTheDeadlineSatisfiesALateWaiter()
    {
        using var clock = ControlledGuestClock.Stop();
        _h.OnGuest(_ =>
        {
            const uint LimitUs = 50_000, BeforeSetUs = 20_000;
            int uid = Call(Tm, "sceKernelCreateEventFlag", "in-time", 0u, 0u, 0u);
            EventFlag flag = Flag(uid);
            uint outBits = _h.Word(0);
            uint timeout = _h.Word(LimitUs);
            int result = 1;
            GuestRun waiter = _h.Start(_ => result = Call(Tm, "sceKernelWaitEventFlag", uid, 0x1u, EventFlag.WaitAnd | EventFlag.WaitClearPat,
                new Ptr<uint>(outBits), new Ptr<uint>(timeout)), "evf-in-time");
            Until(() => waiter.Thread.Status == ThreadStatus.Waiting, "the wait");
            lock (flag)
            {
                clock.Advance(BeforeSetUs);
                Assert.Equal(0, Call(Tm, "sceKernelSetEventFlag", uid, 0x3u));
                clock.Advance(LimitUs); // the waiter's thread runs past its deadline
            }
            _h.Join(waiter);
            Assert.Equal(0, result);
            Assert.Equal(LimitUs - BeforeSetUs, GuestMemory.R32(timeout)); // the time left when the set woke it
            Assert.Equal(0x3u, GuestMemory.R32(outBits));
            Assert.Equal(0x2u, flag.Pattern);
        });
    }
}
