using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// Message pipes under adversarial interleavings, against FW 3.74 threadmgr (the blocking send 0x81013DFC and receive
/// 0x8101456C, the Try forms, cancel and the class's wait handlers, studied in the decompiled module): one wait queue
/// of senders or receivers, FIFO or by priority with TH_PRIO_S (0x1) / TH_PRIO_R (0x2); a send hands its bytes to the
/// waiting receivers inside the call and a receive takes the waiting senders' bytes inside the call, in queue order;
/// nobody goes ahead of a waiter of its own kind, nor of a head that moved part of its bytes. Each step is checked
/// while the pipe's lock is held (the woken threads cannot run yet), so the result is decided by the calls, never by
/// the host's timing.
/// </summary>
[Collection("guest")]
public sealed class MsgPipeInterleavingTests(GuestMemoryFixture fixture) : IDisposable
{
    private readonly HleHarness _h = new(fixture, "msgpipe-interleavings");

    private ThreadMgr Tm => _h.Module<ThreadMgr>();

    public void Dispose() => _h.Dispose();

    private MsgPipe Pipe(int uid) => _h.Emu.Kernel.Objects.Get<MsgPipe>(uid)!;

    private const int HighPriority = 100, LowPriority = 150;

    private int Create(string name, uint attr = 0, uint size = 0x1000) => Call(Tm, "sceKernelCreateMsgPipe", name, 0x40u, attr, size, 0u);

    /// <summary>Guest bytes 1, 2, 3... (plus <paramref name="seed"/>) of <paramref name="size"/>.</summary>
    private uint Bytes(int size, int seed = 0)
    {
        uint address = _h.Alloc((uint)Math.Max(size, 4));
        for (int i = 0; i < size; i++)
        {
            GuestMemory.Write(address + (uint)i, (byte)(seed + i + 1));
        }

        return address;
    }

    /// <summary>A thread that waits in a send (or receive) of <paramref name="size"/> bytes at <paramref name="buffer"/>;
    /// its result goes to <paramref name="results"/>[<paramref name="index"/>] and its byte count to the word at
    /// <paramref name="moved"/>. Returns once it is in the queue.</summary>
    private GuestRun Waiter(bool send, int uid, uint buffer, int size, uint mode, int[] results, int index, uint moved,
        int priority = 0x10000100, uint timeout = 0)
    {
        string function = send ? "sceKernelSendMsgPipe" : "sceKernelReceiveMsgPipe";
        GuestRun run = _h.Start(_ => results[index] = Call(Tm, function, uid, buffer, (uint)size, mode, new Ptr<uint>(moved), new Ptr<uint>(timeout)),
            $"mpp-{(send ? "send" : "recv")}-{index}", priority);
        Until(() => run.Thread.Status == ThreadStatus.Waiting || !run.Thread.IsLive, $"waiter {index} in the queue");
        Assert.Equal(ThreadStatus.Waiting, run.Thread.Status);
        return run;
    }

    /// <summary>freeSize, numSendWaitThreads and numReceiveWaitThreads as sceKernelGetMsgPipeInfo reports them.</summary>
    private (int Free, int Senders, int Receivers) Info(GuestThread main, int uid)
    {
        uint info = _h.Alloc(0x3C);
        GuestMemory.W32(info, 0x3C);
        Assert.Equal(0u, main.DispatchForTest("sceKernelGetMsgPipeInfo", (uint)uid, info));
        return ((int)GuestMemory.R32(info + 0x30), (int)GuestMemory.R32(info + 0x34), (int)GuestMemory.R32(info + 0x38));
    }

    /// <summary>A Try send or receive on the calling thread (inside the test's lock: the pipe's lock is reentrant),
    /// the count written to <paramref name="count"/>.</summary>
    private int Try(string function, int uid, uint buffer, uint size, uint mode, uint count)
        => Call(Tm, function, uid, buffer, size, mode, new Ptr<uint>(count));

    /// <summary>A blocking send or receive on the calling thread, without a timeout.</summary>
    private int Blocking(string function, int uid, uint buffer, uint size, uint mode, uint count, uint timeout)
        => Call(Tm, function, uid, buffer, size, mode, new Ptr<uint>(count), new Ptr<uint>(timeout));

    /// <summary>
    /// A send hands its bytes to the waiting receiver inside the call: the buffer stays empty and a Try receive right
    /// after it, before the receiver's thread runs, finds nothing (it used to find the bytes in the buffer and take
    /// them from the receiver).
    /// </summary>
    [Fact]
    public void ASendHandsItsBytesToTheWaitingReceiverInsideTheCall()
    {
        _h.OnGuest(main =>
        {
            int uid = Create("direct");
            MsgPipe pipe = Pipe(uid);
            uint source = Bytes(8);
            uint destination = _h.Alloc(16);
            uint moved = _h.Word(0xDEAD);
            uint sent = _h.Word(0xDEAD);
            uint stolen = _h.Alloc(16);
            int[] results = [1];
            GuestRun receiver = Waiter(false, uid, destination, 16, MsgPipe.WaitAsap, results, 0, moved);
            lock (pipe)
            {
                Assert.Equal(0, Try("sceKernelTrySendMsgPipe", uid, source, 8u, MsgPipe.WaitAll, sent));
                Assert.Equal(8u, GuestMemory.R32(sent));
                Assert.Equal((0x1000, 0, 0), Info(main, uid));
                Assert.Equal(SceError.MPP_EMPTY, Try("sceKernelTryReceiveMsgPipe", uid, stolen, 16u, MsgPipe.WaitAsap, _h.Word(0)));
            }
            _h.Join(receiver);
            Assert.Equal(0, results[0]);
            Assert.Equal(8u, GuestMemory.R32(moved));
            Assert.True(GuestMemory.ReadSpan(destination, 8).SequenceEqual(GuestMemory.ReadSpan(source, 8)));
        });
    }

    /// <summary>
    /// Receivers are served in the order they came (FIFO), or the higher-priority one first with TH_PRIO_R (equal
    /// priorities in arrival order): each send of 4 bytes serves exactly the next one, inside the call.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReceiversAreServedInQueueOrder(bool threadPriority)
    {
        _h.OnGuest(main =>
        {
            int uid = Create(threadPriority ? "recv-prio" : "recv-fifo", threadPriority ? MsgPipe.AttrReceivePriority : 0u);
            MsgPipe pipe = Pipe(uid);
            int[] results = [1, 1, 1];
            uint[] destinations = [_h.Alloc(4), _h.Alloc(4), _h.Alloc(4)];
            uint[] moved = [_h.Word(0), _h.Word(0), _h.Word(0)];
            GuestRun low = Waiter(false, uid, destinations[0], 4, MsgPipe.WaitAll, results, 0, moved[0], LowPriority);
            GuestRun highFirst = Waiter(false, uid, destinations[1], 4, MsgPipe.WaitAll, results, 1, moved[1], HighPriority);
            GuestRun highSecond = Waiter(false, uid, destinations[2], 4, MsgPipe.WaitAll, results, 2, moved[2], HighPriority);
            int[] expected = threadPriority ? [1, 2, 0] : [0, 1, 2];
            GuestRun[] runs = [low, highFirst, highSecond];
            for (int served = 0; served < 3; served++)
            {
                uint source = Bytes(4, served * 16);
                lock (pipe)
                {
                    Assert.Equal(0, Blocking("sceKernelSendMsgPipe", uid, source, 4u, MsgPipe.WaitAll, 0u, 0u));
                    Assert.Equal((0x1000, 0, 2 - served), Info(main, uid));
                }
                _h.Join(runs[expected[served]]);
                Assert.True(GuestMemory.ReadSpan(destinations[expected[served]], 4).SequenceEqual(GuestMemory.ReadSpan(source, 4)));
            }
            Assert.Equal([0, 0, 0], results);
        });
    }

    /// <summary>
    /// Senders wait in order: with a full buffer, a FULL sender of 8 bytes at the head and one of 4 behind it, a
    /// receive that frees 4 bytes does not let the second one in ahead of the first (FIFO), and a Try send of 1 byte
    /// fails too, room or not. Freeing 4 more serves the head inside the receive; the second waits for the next room.
    /// </summary>
    [Fact]
    public void SendersDoNotGoAheadOfTheHead()
    {
        _h.OnGuest(main =>
        {
            int uid = Create("send-fifo");
            MsgPipe pipe = Pipe(uid);
            Assert.Equal(0, Call(Tm, "sceKernelSendMsgPipe", uid, Bytes(0x1000), 0x1000u, MsgPipe.WaitAll, new Ptr<uint>(0), new Ptr<uint>(0)));
            int[] results = [1, 1];
            uint[] moved = [_h.Word(0xDEAD), _h.Word(0xDEAD)];
            GuestRun head = Waiter(true, uid, Bytes(8, 0x40), 8, MsgPipe.WaitAll, results, 0, moved[0]);
            GuestRun behind = Waiter(true, uid, Bytes(4, 0x80), 4, MsgPipe.WaitAll, results, 1, moved[1]);
            Assert.Equal(0u, GuestMemory.R32(moved[0])); // the count is written before the wait
            uint sink = _h.Alloc(0x1000);
            lock (pipe)
            {
                Assert.Equal(0, Blocking("sceKernelReceiveMsgPipe", uid, sink, 4u, MsgPipe.WaitAll, 0u, 0u));
                Assert.Equal((4, 2, 0), Info(main, uid));
                Assert.Equal(SceError.MPP_FULL, Try("sceKernelTrySendMsgPipe", uid, Bytes(1), 1u, MsgPipe.WaitAsap, _h.Word(0)));
                Assert.Equal(0, Blocking("sceKernelReceiveMsgPipe", uid, sink, 4u, MsgPipe.WaitAll, 0u, 0u));
                Assert.Equal((0, 1, 0), Info(main, uid));
            }
            _h.Join(head);
            Assert.Equal(ThreadStatus.Waiting, behind.Thread.Status);
            Assert.Equal(0, Call(Tm, "sceKernelReceiveMsgPipe", uid, sink, 4u, MsgPipe.WaitAll, new Ptr<uint>(0), new Ptr<uint>(0)));
            _h.Join(behind);
            Assert.Equal([0, 0], results);
            Assert.Equal(8u, GuestMemory.R32(moved[0]));
            Assert.Equal(4u, GuestMemory.R32(moved[1]));
            // the buffer now ends with the head's 8 bytes, then the second sender's 4
            Assert.Equal(0, Call(Tm, "sceKernelReceiveMsgPipe", uid, sink, 0x1000u, MsgPipe.WaitAll, new Ptr<uint>(0), new Ptr<uint>(0)));
            Assert.Equal((byte)0x41, GuestMemory.Read<byte>(sink + 0xFF4));
            Assert.Equal((byte)0x81, GuestMemory.Read<byte>(sink + 0xFFC));
        });
    }

    /// <summary>
    /// With TH_PRIO_S a higher-priority Try send goes ahead of a waiting lower-priority sender when its bytes fit (the
    /// waiting FULL sender of 16 bytes does not fit in the 8 free ones); without the attribute it may not go ahead of
    /// any waiting sender and fails, room or not.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AHigherPrioritySenderGoesAheadOnlyWithThPrioS(bool threadPriority)
    {
        _h.OnGuest(main =>
        {
            int uid = Create(threadPriority ? "send-prio" : "send-fifo2", threadPriority ? MsgPipe.AttrSendPriority : 0u);
            Assert.Equal(0, Blocking("sceKernelSendMsgPipe", uid, Bytes(0xFF8), 0xFF8u, MsgPipe.WaitAll, 0u, 0u));
            int[] results = [1];
            GuestRun low = Waiter(true, uid, Bytes(16, 0x40), 16, MsgPipe.WaitAll, results, 0, _h.Word(0), LowPriority);
            int tried = 1;
            uint count = _h.Word(0xDEAD);
            GuestRun high = _h.Start(_ => tried = Try("sceKernelTrySendMsgPipe", uid, Bytes(4, 0x80), 4u, MsgPipe.WaitAsap, count), "mpp-high-send", HighPriority);
            _h.Join(high);
            Assert.Equal(threadPriority ? 0 : SceError.MPP_FULL, tried);
            Assert.Equal(threadPriority ? 4u : 0xDEADu, GuestMemory.R32(count));
            Assert.Equal((threadPriority ? 4 : 8, 1, 0), Info(main, uid));
            Assert.Equal(0, Call(Tm, "sceKernelCancelMsgPipe", uid, new Ptr<int>(0), new Ptr<int>(0)));
            _h.Join(low);
            Assert.Equal(SceError.WAIT_CANCEL, results[0]);
        });
    }

    /// <summary>
    /// A FULL receiver keeps the first place once it has part of its bytes: a send of 3 goes to it and it goes on
    /// waiting (no other receive goes ahead of it, a Try one fails), and the next send of 5 completes it, the bytes
    /// in order.
    /// </summary>
    [Fact]
    public void AFullReceiverKeepsItsPlaceWhileItFills()
    {
        _h.OnGuest(main =>
        {
            int uid = Create("recv-partial", MsgPipe.AttrReceivePriority);
            MsgPipe pipe = Pipe(uid);
            int[] results = [1];
            uint destination = _h.Alloc(8);
            uint moved = _h.Word(0);
            GuestRun receiver = Waiter(false, uid, destination, 8, MsgPipe.WaitAll, results, 0, moved, LowPriority);
            uint first = Bytes(3);
            uint second = Bytes(5, 3);
            lock (pipe)
            {
                Assert.Equal(0, Blocking("sceKernelSendMsgPipe", uid, first, 3u, MsgPipe.WaitAll, 0u, 0u));
                Assert.Equal((0x1000, 0, 1), Info(main, uid));
                Assert.Equal(3u, GuestMemory.R32(moved));
            }
            // a higher-priority receive queues behind the partly served head instead of ahead of it
            int[] highResult = [1];
            GuestRun high = Waiter(false, uid, _h.Alloc(4), 4, MsgPipe.WaitAsap, highResult, 0, _h.Word(0), HighPriority);
            Assert.Equal(SceError.MPP_EMPTY, Try("sceKernelTryReceiveMsgPipe", uid, _h.Alloc(4), 1u, MsgPipe.WaitAsap, _h.Word(0)));
            lock (pipe)
            {
                Assert.Equal(0, Blocking("sceKernelSendMsgPipe", uid, second, 5u, MsgPipe.WaitAll, 0u, 0u));
                Assert.Equal((0x1000, 0, 1), Info(main, uid));
            }
            _h.Join(receiver);
            Assert.Equal(ThreadStatus.Waiting, high.Thread.Status);
            Assert.Equal(0, Call(Tm, "sceKernelCancelMsgPipe", uid, new Ptr<int>(0), new Ptr<int>(0)));
            _h.Join(high);
            Assert.Equal(SceError.WAIT_CANCEL, highResult[0]);
            Assert.Equal(0, results[0]);
            Assert.Equal(8u, GuestMemory.R32(moved));
            Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, GuestMemory.ReadSpan(destination, 8).ToArray());
        });
    }

    /// <summary>A receive takes the waiting sender's bytes inside the call: with 8 bytes free and a FULL sender of 16
    /// waiting, a receive of the whole buffer gets the buffer's bytes and the sender's first 8 straight from it, the
    /// sender's other 8 go into the buffer and it is served before its thread runs.</summary>
    [Fact]
    public void AReceiveTakesTheWaitingSendersBytesInsideTheCall()
    {
        _h.OnGuest(main =>
        {
            int uid = Create("from-sender");
            MsgPipe pipe = Pipe(uid);
            Assert.Equal(0, Blocking("sceKernelSendMsgPipe", uid, Bytes(0xFF8), 0xFF8u, MsgPipe.WaitAll, 0u, 0u));
            int[] results = [1];
            uint moved = _h.Word(0);
            uint source = Bytes(16, 0x40);
            GuestRun sender = Waiter(true, uid, source, 16, MsgPipe.WaitAll, results, 0, moved);
            uint sink = _h.Alloc(0x1000);
            lock (pipe)
            {
                Assert.Equal(0, Blocking("sceKernelReceiveMsgPipe", uid, sink, 0x1000u, MsgPipe.WaitAll, 0u, 0u));
                Assert.Equal((0x1000 - 8, 0, 0), Info(main, uid));
                Assert.Equal(16u, GuestMemory.R32(moved));
            }
            _h.Join(sender);
            Assert.Equal(0, results[0]);
            Assert.True(GuestMemory.ReadSpan(sink + 0xFF8, 8).SequenceEqual(GuestMemory.ReadSpan(source, 8)));
            uint rest = _h.Alloc(8);
            Assert.Equal(0, Try("sceKernelTryReceiveMsgPipe", uid, rest, 8u, MsgPipe.WaitAll, _h.Word(0)));
            Assert.True(GuestMemory.ReadSpan(rest, 8).SequenceEqual(GuestMemory.ReadSpan(source + 8, 8)));
        });
    }

    /// <summary>
    /// A cancel empties the buffer and releases every waiter with WAIT_CANCEL and what it moved; numSend / numRecv
    /// count the senders. Before, the buffer's bytes stayed.
    /// </summary>
    [Fact]
    public void ACancelEmptiesTheBufferAndReleasesTheQueue()
    {
        _h.OnGuest(main =>
        {
            int uid = Create("cancel");
            MsgPipe pipe = Pipe(uid);
            Assert.Equal(0, Call(Tm, "sceKernelSendMsgPipe", uid, Bytes(0xFFE), 0xFFEu, MsgPipe.WaitAll, new Ptr<uint>(0), new Ptr<uint>(0)));
            int[] results = [1, 1];
            uint[] moved = [_h.Word(0xDEAD), _h.Word(0xDEAD)];
            GuestRun first = Waiter(true, uid, Bytes(4), 4, MsgPipe.WaitAll, results, 0, moved[0]);
            GuestRun second = Waiter(true, uid, Bytes(4), 4, MsgPipe.WaitAsap, results, 1, moved[1]);
            uint numSend = _h.Word(9);
            uint numRecv = _h.Word(9);
            lock (pipe)
            {
                Assert.Equal(0u, main.DispatchForTest("sceKernelCancelMsgPipe", (uint)uid, numSend, numRecv));
                Assert.Equal((0x1000, 0, 0), Info(main, uid));
                Assert.Equal(SceError.MPP_EMPTY, Try("sceKernelTryReceiveMsgPipe", uid, _h.Alloc(4), 1u, MsgPipe.WaitAsap, _h.Word(0)));
            }
            _h.Join(first);
            _h.Join(second);
            Assert.Equal(2u, GuestMemory.R32(numSend));
            Assert.Equal(0u, GuestMemory.R32(numRecv));
            Assert.Equal([SceError.WAIT_CANCEL, SceError.WAIT_CANCEL], results);
            Assert.Equal(0u, GuestMemory.R32(moved[0]));
            Assert.Equal(0u, GuestMemory.R32(moved[1]));
        });
    }

    /// <summary>A deleted pipe's waiters return WAIT_DELETE, a FULL receiver with the bytes it got; the UID is gone.</summary>
    [Fact]
    public void ADeleteReleasesTheQueueWithWhatEachMoved()
    {
        _h.OnGuest(main =>
        {
            int uid = Create("delete");
            MsgPipe pipe = Pipe(uid);
            int[] results = [1, 1];
            uint[] moved = [_h.Word(0), _h.Word(0)];
            GuestRun first = Waiter(false, uid, _h.Alloc(8), 8, MsgPipe.WaitAll, results, 0, moved[0]);
            GuestRun second = Waiter(false, uid, _h.Alloc(8), 8, MsgPipe.WaitAll, results, 1, moved[1]);
            Assert.Equal(0, Call(Tm, "sceKernelSendMsgPipe", uid, Bytes(3), 3u, MsgPipe.WaitAll, new Ptr<uint>(0), new Ptr<uint>(0)));
            lock (pipe)
            {
                Assert.Equal(0u, main.DispatchForTest("sceKernelDeleteMsgPipe", (uint)uid));
                Assert.Equal(SceError.UNKNOWN_MSG_PIPE_ID, Try("sceKernelTrySendMsgPipe", uid, Bytes(1), 1u, 0u, 0u));
            }
            _h.Join(first);
            _h.Join(second);
            Assert.Equal([SceError.WAIT_DELETE, SceError.WAIT_DELETE], results);
            Assert.Equal(3u, GuestMemory.R32(moved[0]));
            Assert.Equal(0u, GuestMemory.R32(moved[1]));
        });
    }

    /// <summary>
    /// A timeout is the kernel's timer at a guest-time deadline (the test moves guest time): a microsecond before it
    /// the FULL head sender still waits; at it, the head is off the queue (WAIT_TIMEOUT, its count written) and the
    /// sender behind it, whose 4 bytes fit, is served by that alone, without another call.
    /// </summary>
    [Fact]
    public void ATimedOutHeadLetsTheSenderBehindItIn()
    {
        using var clock = ControlledGuestClock.Stop();
        _h.OnGuest(main =>
        {
            const uint LimitUs = 50_000;
            int uid = Create("deadline");
            MsgPipe pipe = Pipe(uid);
            Assert.Equal(0, Call(Tm, "sceKernelSendMsgPipe", uid, Bytes(0xFFC), 0xFFCu, MsgPipe.WaitAll, new Ptr<uint>(0), new Ptr<uint>(0)));
            int[] results = [1, 1];
            uint[] moved = [_h.Word(0xDEAD), _h.Word(0xDEAD)];
            uint timeout = _h.Word(LimitUs);
            GuestRun head = Waiter(true, uid, Bytes(8), 8, MsgPipe.WaitAll, results, 0, moved[0], timeout: timeout);
            GuestRun behind = Waiter(true, uid, Bytes(4), 4, MsgPipe.WaitAll, results, 1, moved[1]);
            lock (pipe)
            {
                clock.Advance(LimitUs - 1);
                Assert.Equal((4, 2, 0), Info(main, uid));
                clock.Advance(1);
                Assert.Equal((0, 0, 0), Info(main, uid));
            }
            _h.Join(head);
            _h.Join(behind);
            Assert.Equal([SceError.WAIT_TIMEOUT, 0], results);
            Assert.Equal(0u, GuestMemory.R32(moved[0]));
            Assert.Equal(4u, GuestMemory.R32(moved[1]));
            Assert.Equal(0u, GuestMemory.R32(timeout));
        });
    }

    /// <summary>A FULL receiver whose timeout passes after it got part of its bytes returns WAIT_TIMEOUT with that
    /// count (it used to report 0 and the bytes looked lost).</summary>
    [Fact]
    public void ATimedOutReceiverReportsWhatItGot()
    {
        using var clock = ControlledGuestClock.Stop();
        _h.OnGuest(main =>
        {
            const uint LimitUs = 20_000;
            int uid = Create("partial-timeout");
            int[] results = [1];
            uint moved = _h.Word(0);
            GuestRun receiver = Waiter(false, uid, _h.Alloc(8), 8, MsgPipe.WaitAll, results, 0, moved, timeout: _h.Word(LimitUs));
            Assert.Equal(0, Call(Tm, "sceKernelSendMsgPipe", uid, Bytes(3), 3u, MsgPipe.WaitAll, new Ptr<uint>(0), new Ptr<uint>(0)));
            clock.Advance(LimitUs);
            Assert.Equal((0x1000, 0, 0), Info(main, uid));
            _h.Join(receiver);
            Assert.Equal(SceError.WAIT_TIMEOUT, results[0]);
            Assert.Equal(3u, GuestMemory.R32(moved));
        });
    }

    /// <summary>DONT_REMOVE (0x100) on a Try receive copies the bytes and leaves them in the pipe; the next receive gets
    /// them again. The Try forms refuse other mode bits with ILLEGAL_MODE (send: anything but FULL; receive: anything
    /// but FULL | DONT_REMOVE), and a FULL Try send that does not fit writes no count.</summary>
    [Fact]
    public void APeekLeavesTheBytesAndTheTryFormsCheckTheMode()
    {
        _h.OnGuest(main =>
        {
            int uid = Create("peek");
            uint source = Bytes(4);
            Assert.Equal(0, Call(Tm, "sceKernelSendMsgPipe", uid, source, 4u, MsgPipe.WaitAll, new Ptr<uint>(0), new Ptr<uint>(0)));
            uint peeked = _h.Alloc(4);
            uint count = _h.Word(0);
            Assert.Equal(0, Call(Tm, "sceKernelTryReceiveMsgPipe", uid, peeked, 4u, MsgPipe.WaitAll | MsgPipe.DontRemove, new Ptr<uint>(count)));
            Assert.Equal(4u, GuestMemory.R32(count));
            Assert.Equal((0x1000 - 4, 0, 0), Info(main, uid));
            uint taken = _h.Alloc(4);
            Assert.Equal(0, Call(Tm, "sceKernelTryReceiveMsgPipe", uid, taken, 4u, MsgPipe.WaitAll, new Ptr<uint>(count)));
            Assert.True(GuestMemory.ReadSpan(peeked, 4).SequenceEqual(GuestMemory.ReadSpan(source, 4)));
            Assert.True(GuestMemory.ReadSpan(taken, 4).SequenceEqual(GuestMemory.ReadSpan(source, 4)));
            Assert.Equal(SceError.ILLEGAL_MODE, Call(Tm, "sceKernelTrySendMsgPipe", uid, source, 4u, MsgPipe.DontRemove, new Ptr<uint>(0)));
            Assert.Equal(SceError.ILLEGAL_MODE, Call(Tm, "sceKernelTryReceiveMsgPipe", uid, taken, 4u, 2u, new Ptr<uint>(0)));
            Assert.Equal(0, Call(Tm, "sceKernelSendMsgPipe", uid, Bytes(0xFFE), 0xFFEu, MsgPipe.WaitAll, new Ptr<uint>(0), new Ptr<uint>(0)));
            uint untouched = _h.Word(0xDEAD);
            Assert.Equal(SceError.MPP_FULL, Call(Tm, "sceKernelTrySendMsgPipe", uid, source, 4u, MsgPipe.WaitAll, new Ptr<uint>(untouched)));
            Assert.Equal(0xDEADu, GuestMemory.R32(untouched));
        });
    }

    /// <summary>
    /// A save state keeps the buffer and the queue: a receiver a send served that had not returned when the state was
    /// saved returns OK with its bytes when its call is reissued, and the receivers still queued keep their saved
    /// order however the restored threads happen to reissue their calls (here in reverse).
    /// </summary>
    [Fact]
    public void ASaveStateKeepsTheQueueOrderAndTheServedWaiters()
    {
        _h.OnGuest(main =>
        {
            int uid = Create("saved");
            MsgPipe pipe = Pipe(uid);
            MsgPipe restored = new() { Name = "saved" };
            int[] first = [1, 1, 1];
            int[] second = [1, 1, 1];
            int[] gates = new int[3];
            uint[] destinations = [_h.Alloc(4), _h.Alloc(4), _h.Alloc(4)];
            uint[] moved = [_h.Word(0), _h.Word(0), _h.Word(0)];
            var runs = new GuestRun[3];
            for (int i = 0; i < 3; i++)
            {
                int index = i;
                gates[i] = Call(Tm, "sceKernelCreateSema", $"gate-{i}", 0u, 0, 1, 0u);
                runs[i] = _h.Start(_ =>
                {
                    first[index] = Call(Tm, "sceKernelReceiveMsgPipe", uid, destinations[index], 4u, MsgPipe.WaitAll, new Ptr<uint>(moved[index]), new Ptr<uint>(0));
                    Assert.Equal(0, Call(Tm, "sceKernelWaitSema", gates[index], 1, new Ptr<uint>(0)));
                    second[index] = restored.Receive(destinations[index], 4, MsgPipe.WaitAll, new Ptr<uint>(moved[index]), new Ptr<uint>(0), false);
                }, $"mpp-saved-{i}");
                Until(() => runs[index].Thread.Status == ThreadStatus.Waiting, "the receive");
            }

            var writer = new SnapshotWriter();
            uint served = Bytes(4, 0x10);
            lock (pipe)
            {
                Assert.Equal(0, Blocking("sceKernelSendMsgPipe", uid, served, 4u, MsgPipe.WaitAll, 0u, 0u)); // serves receiver 0
                pipe.Save(writer);
            }
            Assert.Equal(0, Call(Tm, "sceKernelCancelMsgPipe", uid, new Ptr<int>(0), new Ptr<int>(0)));
            restored.Load(new SnapshotReader(writer.Data.ToArray()));
            Assert.Equal(2, restored.RecvWaiters);
            for (int i = 2; i >= 0; i--)
            {
                int index = i;
                Until(() => runs[index].Thread.Status == ThreadStatus.Waiting && runs[index].Thread.CurrentWaitSync == _h.Emu.Kernel.Objects.Get<Core.Kernel.Semaphore>(gates[index]), "the gate");
                Assert.Equal(0, Call(Tm, "sceKernelSignalSema", gates[index], 1));
                Until(() => (runs[index].Thread.Status == ThreadStatus.Waiting && runs[index].Thread.CurrentWaitSync == restored) || !runs[index].Thread.IsLive,
                    "the reissued receive");
            }

            _h.Join(runs[0]); // served before the save
            Assert.Equal(0, second[0]);
            uint next = Bytes(4, 0x20);
            lock (restored)
            {
                Assert.Equal(SceError.OK, restored.Send(next, 4, MsgPipe.WaitAll, default, default, true));
                Assert.Equal(1, restored.RecvWaiters);
            }
            _h.Join(runs[1]);
            Assert.Equal(ThreadStatus.Waiting, runs[2].Thread.Status);
            Assert.Equal(SceError.OK, restored.Send(Bytes(4, 0x30), 4, MsgPipe.WaitAll, default, default, true));
            _h.Join(runs[2]);
            Assert.Equal([0, SceError.WAIT_CANCEL, SceError.WAIT_CANCEL], first);
            Assert.Equal([0, 0, 0], second);
            Assert.True(GuestMemory.ReadSpan(destinations[0], 4).SequenceEqual(GuestMemory.ReadSpan(served, 4)));
            Assert.True(GuestMemory.ReadSpan(destinations[1], 4).SequenceEqual(GuestMemory.ReadSpan(next, 4)));
        });
    }

    /// <summary>A message pipe record of a build before the saved queue loads with its buffer's bytes and an empty queue.</summary>
    [Fact]
    public void AMsgPipeRecordWithoutAQueueLoads()
    {
        _h.OnGuest(_ =>
        {
            var writer = new SnapshotWriter();
            writer.Write(MsgPipe.AttrSendPriority);
            writer.Write(0x1000);
            writer.Write(7);
            writer.Write(false);
            writer.Write(new byte[] { 9, 8, 7 });
            var restored = new MsgPipe { Name = "legacy" };
            restored.Load(new SnapshotReader(writer.Data.ToArray()));
            Assert.Equal((MsgPipe.AttrSendPriority, 0x1000, 7), (restored.Attr, restored.BufferSize, restored.CreatorThreadUid));
            Assert.Equal((0x1000 - 3, 0, 0), (restored.FreeSize, restored.SendWaiters, restored.RecvWaiters));
            uint destination = _h.Alloc(4);
            Assert.Equal(SceError.OK, restored.Receive(destination, 3, MsgPipe.WaitAll, default, default, true));
            Assert.Equal(new byte[] { 9, 8, 7 }, GuestMemory.ReadSpan(destination, 3).ToArray());
        });
    }
}
