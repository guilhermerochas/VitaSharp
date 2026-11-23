using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// The event core of the event-class objects that are not simple events or timers (message pipes, asynchronous I/O
/// operations), against FW 3.74 threadmgr (the class constructor 0x8100078c, the set 0x8101dcf8, the wait 0x8101d220,
/// the multiple wait 0x8101ed4c, the clear 0x9C335818, the pipe's send 0x81013dfc and receive 0x8101456c, studied in the
/// decompiled module): a sticky pattern word that starts at 0, edges set by the pipe's transfers (IN, OUT, ERROR, the
/// "can receive" bit kept by the pipe), auto reset by the object's attribute, one queue of single and multiple waits
/// served inside the set, user data { result, bytes } in each result slot, the delete's WAIT_DELETE. The order is
/// forced by the calls (each step waits until the other thread is in its queue), never by the host's timing.
/// </summary>
[Collection("guest")]
public sealed class EventCoreInterleavingTests(GuestMemoryFixture fixture) : IDisposable
{
    private const uint BitA = 0x01000000;

    private readonly HleHarness _h = new(fixture, "event-core");

    private ThreadMgr Tm => _h.Module<ThreadMgr>();

    public void Dispose() => _h.Dispose();

    private int Pipe(string name, uint attr = 0) => Call(Tm, "sceKernelCreateMsgPipe", name, 0x40u, attr, 0x1000u, 0u);

    private int Send(int pipe, uint size) => Call(Tm, "sceKernelSendMsgPipe", pipe, _h.Alloc(size), size, MsgPipe.WaitAll, new Ptr<uint>(0), new Ptr<uint>(0));

    private int Receive(int pipe, uint size) => Call(Tm, "sceKernelReceiveMsgPipe", pipe, _h.Alloc(size), size, MsgPipe.WaitAll, new Ptr<uint>(0), new Ptr<uint>(0));

    private int Poll(int uid, uint pattern, uint result) => Call(Tm, "sceKernelPollEvent", uid, pattern, new Ptr<uint>(result), new Ptr<ulong>(0));

    /// <summary>Waits for a step the calls make happen at once; the host time only bounds how long a broken build hangs.</summary>
    private static void Soon(Func<bool> done, string what)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!done())
        {
            if (watch.ElapsedMilliseconds > 10_000)
            {
                throw new Xunit.Sdk.XunitException($"never reached: {what}");
            }

            Thread.Sleep(1);
        }
    }

    private sealed class MultiCall
    {
        public GuestRun Run = null!;
        public uint Results;
        public int Returned = 1;
    }

    /// <summary>A multiple-events call on its own thread, returned once it sleeps (its first look at every event,
    /// which queues it there, is done before the sleep).</summary>
    private MultiCall StartMulti((int Uid, uint Pattern)[] events, bool or)
    {
        uint array = _h.Alloc((uint)events.Length * 8);
        for (int i = 0; i < events.Length; i++)
        {
            GuestMemory.W32(array + (uint)i * 8, (uint)events[i].Uid);
            GuestMemory.W32(array + (uint)i * 8 + 4, events[i].Pattern);
        }

        var call = new MultiCall { Results = _h.Alloc((uint)events.Length * 0x18) };
        call.Run = _h.Start(_ => call.Returned = Call(Tm, "sceKernelWaitMultipleEvents", array, (uint)events.Length, or ? 1u : 2u, call.Results, new Ptr<uint>(0)), "multi-wait");
        Soon(() => call.Run.Thread.Status == ThreadStatus.Waiting, "the multiple-events wait in the queue");
        return call;
    }

    private static (int Uid, int Code, uint Pattern, ulong UserData) Entry(uint results, int index)
    {
        uint entry = results + (uint)index * 0x18;
        return ((int)GuestMemory.R32(entry), (int)GuestMemory.R32(entry + 4), GuestMemory.R32(entry + 8), GuestMemory.Read<ulong>(entry + 16));
    }

    /// <summary>The pipe's pattern starts at 0 (no OUT on a fresh pipe), a send sets IN and "can receive", a receive
    /// that empties it clears "can receive" and sets OUT; IN stays (sticky) until sceKernelClearEvent, which works on a
    /// pipe as on any event-class object and accepts only user bits to keep.</summary>
    [Fact]
    public void PipeBitsAreStickyEdgesClearedByClearEvent()
    {
        _h.OnGuest(_ =>
        {
            int pipe = Pipe("core-sticky");
            uint result = _h.Word(0xFFFFFFFF);
            Assert.Equal(SceError.EVENT_COND, Poll(pipe, MsgPipe.EventIn | MsgPipe.EventOut, result));
            Assert.Equal(0, Send(pipe, 8));
            Assert.Equal(0, Poll(pipe, 0xFFFF_FFFFu & ~EventBits.Delete, result));
            Assert.Equal(MsgPipe.EventIn | MsgPipe.EventReceivable, GuestMemory.R32(result));
            Assert.Equal(0, Receive(pipe, 8));
            Assert.Equal(0, Poll(pipe, 0xFFFF_FFFFu & ~EventBits.Delete, result));
            Assert.Equal(MsgPipe.EventIn | MsgPipe.EventOut, GuestMemory.R32(result));
            Assert.Equal(SceError.ILLEGAL_PATTERN, Call(Tm, "sceKernelClearEvent", pipe, MsgPipe.EventIn));
            Assert.Equal(0, Call(Tm, "sceKernelClearEvent", pipe, 0u));
            Assert.Equal(SceError.EVENT_COND, Poll(pipe, MsgPipe.EventIn | MsgPipe.EventOut, result));
        });
    }

    /// <summary>With the AUTO_RESET attribute a take clears the bits it asked for, except the pipe's "can receive"
    /// bit (0x10000), which only the receive that empties the buffer clears.</summary>
    [Fact]
    public void AutoResetPipeTakesClearTheEdgesButNotTheReceivableBit()
    {
        _h.OnGuest(_ =>
        {
            int pipe = Pipe("core-auto", EventBits.AttrAutoReset);
            uint result = _h.Word(0);
            Assert.Equal(0, Send(pipe, 4));
            Assert.Equal(0, Poll(pipe, MsgPipe.EventIn | MsgPipe.EventReceivable, result));
            Assert.Equal(MsgPipe.EventIn | MsgPipe.EventReceivable, GuestMemory.R32(result));
            Assert.Equal(SceError.EVENT_COND, Poll(pipe, MsgPipe.EventIn, result));
            Assert.Equal(0, Poll(pipe, MsgPipe.EventReceivable, result));
            Assert.Equal(MsgPipe.EventReceivable, GuestMemory.R32(result));
        });
    }

    /// <summary>The adverse case of the old level reading: a receiver already waiting takes the bytes of the send at
    /// once (nothing is left in the buffer), and the multiple-events call queued on the pipe still gets IN, inside the
    /// send, with the user data { result 0, 4 bytes }.</summary>
    [Fact]
    public void APipeInTakenByAWaitingReceiverStillEndsTheCall()
    {
        _h.OnGuest(_ =>
        {
            int pipe = Pipe("core-drained");
            int received = 1;
            GuestRun receiver = _h.Start(_ => received = Receive(pipe, 4), "receiver");
            Soon(() => receiver.Thread.Status == ThreadStatus.Waiting, "the receiver in the pipe's queue");
            MultiCall call = StartMulti([(pipe, MsgPipe.EventIn)], or: true);
            Assert.Equal(0, Send(pipe, 4));
            _h.Join(receiver);
            _h.Join(call.Run);
            Assert.Equal(0, received);
            Assert.Equal(1, call.Returned);
            Assert.Equal((pipe, 0, MsgPipe.EventIn, 4UL << 32), Entry(call.Results, 0));
        });
    }

    /// <summary>A single sceKernelWaitEvent on the pipe sleeps in the same queue and gets the bits and the user data
    /// inside the receive that sets OUT.</summary>
    [Fact]
    public void ASingleWaitOnThePipeGetsTheReceiveEdgeAndItsUserData()
    {
        _h.OnGuest(_ =>
        {
            int pipe = Pipe("core-single");
            Assert.Equal(0, Send(pipe, 12));
            uint result = _h.Word(0);
            uint userData = _h.Alloc(8);
            int waited = 1;
            GuestRun waiter = _h.Start(_ => waited = Call(Tm, "sceKernelWaitEvent", pipe, MsgPipe.EventOut, new Ptr<uint>(result), new Ptr<ulong>(userData), new Ptr<uint>(0)), "single-wait");
            Soon(() => waiter.Thread.Status == ThreadStatus.Waiting, "the single wait in the pipe's queue");
            Assert.Equal(0, Receive(pipe, 12));
            _h.Join(waiter);
            Assert.Equal(0, waited);
            Assert.Equal(MsgPipe.EventOut, GuestMemory.R32(result));
            Assert.Equal(12UL << 32, GuestMemory.Read<ulong>(userData));
        });
    }

    /// <summary>A cancel ends a waiting receiver with WAIT_CANCEL: the receive sets ERROR (an error other than
    /// 0x8002802C / 0x8002802D), with the error and all-ones bytes as the user data.</summary>
    [Fact]
    public void ACancelledReceiveSetsTheErrorBit()
    {
        _h.OnGuest(_ =>
        {
            int pipe = Pipe("core-cancel");
            int received = 1;
            GuestRun receiver = _h.Start(_ => received = Receive(pipe, 4), "receiver");
            Soon(() => receiver.Thread.Status == ThreadStatus.Waiting, "the receiver in the pipe's queue");
            Assert.Equal(0, Call(Tm, "sceKernelCancelMsgPipe", pipe, new Ptr<int>(0), new Ptr<int>(0)));
            _h.Join(receiver);
            Assert.Equal(SceError.WAIT_CANCEL, received);
            uint result = _h.Word(0);
            uint userData = _h.Alloc(8);
            Assert.Equal(0, Call(Tm, "sceKernelPollEvent", pipe, EventBits.Error, new Ptr<uint>(result), new Ptr<ulong>(userData)));
            Assert.Equal(EventBits.Error, GuestMemory.R32(result));
            Assert.Equal(0xFFFFFFFF_00000000UL | unchecked((uint)SceError.WAIT_CANCEL), GuestMemory.Read<ulong>(userData));
        });
    }

    /// <summary>Deleting the pipe ends a queued multiple-events call with WAIT_DELETE in its slot, and one whose pattern
    /// asks for DELETE with OK and the DELETE bit.</summary>
    [Fact]
    public void DeletingThePipeEndsTheQueuedCalls()
    {
        _h.OnGuest(_ =>
        {
            int plain = Pipe("core-delete-plain");
            MultiCall plainCall = StartMulti([(plain, MsgPipe.EventIn)], or: true);
            Assert.Equal(0, Call(Tm, "sceKernelDeleteMsgPipe", plain));
            _h.Join(plainCall.Run);
            Assert.Equal(1, plainCall.Returned);
            Assert.Equal((plain, SceError.WAIT_DELETE, EventBits.Error, 0UL), Entry(plainCall.Results, 0));

            int asking = Pipe("core-delete-asking");
            MultiCall askingCall = StartMulti([(asking, MsgPipe.EventIn | EventBits.Delete)], or: true);
            Assert.Equal(0, Call(Tm, "sceKernelDeleteMsgPipe", asking));
            _h.Join(askingCall.Run);
            Assert.Equal((asking, 0, EventBits.Delete, 0UL), Entry(askingCall.Results, 0));
        });
    }

    /// <summary>The user bits of sceKernelSetEventWithNotifyCallback go into the pipe's pattern and reach the call
    /// queued there, with the set's user data; a pulse is seen by the queued call only.</summary>
    [Fact]
    public void NotifyCallbackSetsUserBitsOnThePipe()
    {
        _h.OnGuest(_ =>
        {
            int pipe = Pipe("core-notify");
            MultiCall call = StartMulti([(pipe, BitA)], or: true);
            Assert.Equal(0, Call(Tm, "sceKernelPulseEventWithNotifyCallback", pipe, BitA, 0x55UL, 0));
            _h.Join(call.Run);
            Assert.Equal((pipe, 0, BitA, 0x55UL), Entry(call.Results, 0));
            uint result = _h.Word(0);
            Assert.Equal(SceError.EVENT_COND, Poll(pipe, BitA, result));
            Assert.Equal(0, Call(Tm, "sceKernelSetEventWithNotifyCallback", pipe, BitA, 0x66UL, 0));
            Assert.Equal(0, Poll(pipe, BitA, result));
            Assert.Equal(BitA, GuestMemory.R32(result));
        });
    }

    /// <summary>An asynchronous I/O operation still running queues the call; its end sets the completion bit inside
    /// the call, with the operation's user data. Its user bits come from the notify set as on any event-class object.</summary>
    [Fact]
    public void AnIoOperationEndHandsTheCallItsCompletion()
    {
        _h.OnGuest(_ =>
        {
            var operation = new IoAsyncOp { Name = "core-io", UserData = 0x77 };
            int uid = _h.Emu.Kernel.Objects.Register(operation);
            MultiCall call = StartMulti([(uid, IoAsyncOp.CompletionBit)], or: true);
            operation.Finish(0);
            _h.Join(call.Run);
            Assert.Equal(1, call.Returned);
            Assert.Equal((uid, 0, IoAsyncOp.CompletionBit, 0x77UL), Entry(call.Results, 0));
            Assert.Equal(0, Call(Tm, "sceKernelSetEventWithNotifyCallback", uid, BitA, 0x88UL, 0));
            uint result = _h.Word(0);
            Assert.Equal(0, Poll(uid, BitA | IoAsyncOp.CompletionBit, result));
            Assert.Equal(BitA | IoAsyncOp.CompletionBit, GuestMemory.R32(result));
        });
    }

    /// <summary>An AND call over a pipe, an I/O operation and an object outside the event class: the foreign UID is
    /// UNKNOWN_EVENT_ID at once, the pipe's sticky IN is taken at once, the call ends at the operation's end.</summary>
    [Fact]
    public void AnAndCallMixesTheTypes()
    {
        _h.OnGuest(_ =>
        {
            int pipe = Pipe("core-and");
            Assert.Equal(0, Send(pipe, 4));
            Assert.Equal(0, Receive(pipe, 4)); // the buffer is empty again: IN stays
            int sema = Call(Tm, "sceKernelCreateSema", "core-sema", 0u, 0, 1, 0u);
            var operation = new IoAsyncOp { Name = "core-and-io" };
            int uid = _h.Emu.Kernel.Objects.Register(operation);
            MultiCall call = StartMulti([(sema, 1u), (pipe, MsgPipe.EventIn), (uid, IoAsyncOp.CompletionBit)], or: false);
            operation.Finish(0);
            _h.Join(call.Run);
            Assert.Equal(3, call.Returned);
            Assert.Equal((sema, SceError.UNKNOWN_EVENT_ID, EventBits.Error, 0UL), Entry(call.Results, 0));
            Assert.Equal((pipe, 0, MsgPipe.EventIn, 4UL << 32), Entry(call.Results, 1));
            Assert.Equal((uid, 0, IoAsyncOp.CompletionBit, 0UL), Entry(call.Results, 2));
        });
    }
}
