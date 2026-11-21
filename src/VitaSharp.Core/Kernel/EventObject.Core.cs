using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Kernel;

/// <summary>
/// The event core every object of threadmgr's event class has (FW 3.74, studied in the decompiled module: the class
/// constructor 0x8100078c zeroes it, the set 0x8101dcf8 / pulse 0x8101e370, the wait 0x8101d220, the clear
/// SceThreadmgrForDriver 0x9C335818): a pattern word (+0x24) the set ORs bits into and only a take with the auto reset
/// attribute (+0x20 bit 0x100) or a clear removes, the last set's 64-bit user data (+0x30), and ONE wait queue (+0x10)
/// of single waiters and sceKernelWaitMultipleEvents calls in arrival order. The set walks the queue and hands every
/// waiter whose pattern matches its bits and the user data inside the call (a multiple-events call gets a result
/// {uid, 0, bits, user data} appended there); with the auto reset each taker clears the bits it took, except 0x10000.
/// Message pipes and asynchronous I/O operations use it (simple events and timers keep their own copy of the same
/// logic): their events are edges kept in the word, not a level read from their state.
/// </summary>
public abstract partial class EventObject
{
    /// <summary>The bit an auto-reset take leaves in place (the set and the wait mask it out: 0xfffeffff).</summary>
    protected const uint CoreSticky = 0x10000;

    /// <summary>The event pattern (+0x24).</summary>
    protected uint CorePattern;

    /// <summary>The last set's user data (+0x30).</summary>
    protected ulong CoreUserData;

    /// <summary>The object was deleted: a waiter asking for DELETE takes it, the others end with WAIT_DELETE.</summary>
    protected bool CoreDeleted;

    /// <summary>The object's attribute has the auto reset bit (0x100).</summary>
    protected virtual bool CoreAutoReset => false;

    private sealed class CoreWaiter
    {
        public uint WaitPattern;
        public Ptr<uint> Result;
        public Ptr<ulong> UserData;
        public AddressSpace? Space;
        public GuestThread? Thread;
        public int? Code;
        public MultiEventWait? Multi;
        public int MultiIndex;
    }

    private readonly LinkedList<CoreWaiter> _coreWaiters = new();

    /// <summary>The event pattern, for tests and the object's info.</summary>
    public uint EventPattern
    {
        get
        {
            lock (this)
            {
                return CorePattern | (CoreDeleted ? EventBits.Delete : 0);
            }
        }
    }

    private uint CoreVisible => CorePattern | (CoreDeleted ? EventBits.Delete : 0);

    /// <summary>The bits a take clears with the auto reset (never 0x10000).</summary>
    private void CoreConsume(uint bits)
    {
        if (CoreAutoReset)
        {
            CorePattern &= ~(bits & ~CoreSticky);
        }
    }

    /// <summary>
    /// The event set (0x8101dcf8; the caller holds the object's lock): ORs <paramref name="bits"/> into the pattern,
    /// keeps <paramref name="userData"/>, and hands the queued waiters whose pattern matches their bits in queue order.
    /// With <paramref name="pulse"/> (0x8101e370) the pattern goes back to what it was afterwards.
    /// </summary>
    protected void CoreSet(uint bits, ulong userData, bool pulse = false)
    {
        uint before = CorePattern;
        ulong beforeData = CoreUserData;
        CorePattern |= bits;
        CoreUserData = userData;
        for (LinkedListNode<CoreWaiter>? node = _coreWaiters.First; node != null;)
        {
            LinkedListNode<CoreWaiter>? next = node.Next;
            CoreWaiter waiter = node.Value;
            uint hit = CoreVisible & waiter.WaitPattern;
            if (hit != 0)
            {
                bool took;
                if (waiter.Multi != null)
                {
                    took = waiter.Multi.Record(waiter.MultiIndex, Uid, SceError.OK, hit, CoreUserData);
                }
                else
                {
                    using (AddressSpace.Use(waiter.Space))
                    {
                        waiter.Result.TrySet(hit);
                        waiter.UserData.TrySet(CoreUserData);
                    }

                    waiter.Code = SceError.OK;
                    took = true;
                }

                if (took)
                {
                    CoreConsume(hit);
                }

                _coreWaiters.Remove(node);
            }

            node = next;
        }

        if (pulse)
        {
            CorePattern = before;
            CoreUserData = beforeData;
        }

        Monitor.PulseAll(this);
    }

    /// <summary>Clears bits from the pattern without waking anyone (the caller holds the lock).</summary>
    protected void CoreDrop(uint bits)
    {
        CorePattern &= ~bits;
    }

    /// <summary>sceKernelClearEvent on the object (SceThreadmgrForDriver 0x9C335818): keeps only
    /// <paramref name="keepPattern"/>; the user syscall accepts only the user bits.</summary>
    public int ClearEvent(uint keepPattern)
    {
        if ((keepPattern & ~EventBits.UserMask) != 0)
        {
            return SceError.ILLEGAL_PATTERN;
        }

        lock (this)
        {
            CorePattern &= keepPattern;
        }

        return SceError.OK;
    }

    /// <summary>The object is gone: every queued waiter asking for DELETE takes it, the others end with WAIT_DELETE
    /// (a multiple-events call gets that as its result); the caller holds the lock.</summary>
    protected void CoreDelete()
    {
        CoreDeleted = true;
        while (_coreWaiters.First is { } node)
        {
            CoreWaiter waiter = node.Value;
            _coreWaiters.RemoveFirst();
            bool deleteBit = (waiter.WaitPattern & EventBits.Delete) != 0;
            uint bits = deleteBit ? CoreVisible & waiter.WaitPattern : EventBits.Error;
            int code = deleteBit ? SceError.OK : SceError.WAIT_DELETE;
            if (waiter.Multi != null)
            {
                waiter.Multi.Record(waiter.MultiIndex, Uid, code, bits, deleteBit ? CoreUserData : 0);
                continue;
            }

            using (AddressSpace.Use(waiter.Space))
            {
                waiter.Result.TrySet(bits);
                waiter.UserData.TrySet(CoreUserData);
            }

            waiter.Code = code;
        }

        Monitor.PulseAll(this);
    }

    /// <summary>sceKernelPollEvent (0x8101d220 without the sleep): the matching bits now, EVENT_COND without any.</summary>
    public int PollCoreEvent(uint waitPattern, Ptr<uint> result, Ptr<ulong> userData)
    {
        if (waitPattern == 0)
        {
            return SceError.INVALID_ARGUMENT;
        }

        lock (this)
        {
            return CoreTake(waitPattern, result, userData);
        }
    }

    private int CoreTake(uint waitPattern, Ptr<uint> result, Ptr<ulong> userData)
    {
        uint hit = CoreVisible & waitPattern;
        if (hit == 0)
        {
            result.TrySet(0);
            return SceError.EVENT_COND;
        }

        result.TrySet(hit);
        userData.TrySet(CoreUserData);
        CoreConsume(waitPattern);
        return SceError.OK;
    }

    /// <summary>
    /// sceKernelWaitEvent(CB) on the object (0x8101d220): the matching bits at once, else the queue until a set hands
    /// them over, the timeout (guest microseconds) or a delete; a CB wait runs its notified callbacks off the queue and
    /// queues again at the end.
    /// </summary>
    public int WaitCoreEvent(GuestThread? self, uint waitPattern, Ptr<uint> result, Ptr<ulong> userData, Ptr<uint> timeout)
    {
        if (waitPattern == 0)
        {
            return SceError.INVALID_ARGUMENT;
        }

        lock (this)
        {
            Wait.CallbackWaitDeadline deadline = Wait.CallbackWaitDeadline.Start(timeout);
            while (true)
            {
                if (Wait.CallbacksPending(self))
                {
                    Wait.RunCallbacks(this, self!);
                }

                int taken = CoreTake(waitPattern, result, userData);
                if (taken != SceError.EVENT_COND)
                {
                    deadline.WriteLeft(timeout);
                    return taken;
                }

                if (CoreDeleted)
                {
                    result.TrySet(EventBits.Error);
                    return SceError.WAIT_DELETE;
                }

                if (!deadline.PrepareSleep(timeout))
                {
                    return SceError.WAIT_TIMEOUT;
                }

                var waiter = new CoreWaiter { WaitPattern = waitPattern, Result = result, UserData = userData, Space = AddressSpace.Current, Thread = self };
                LinkedListNode<CoreWaiter> node = _coreWaiters.AddLast(waiter);
                int code = Wait.Until(this, waiter, static w => w.Code.HasValue || Wait.CallbacksPending(w.Thread), timeout, null, WaitDescription);
                if (waiter.Code is { } ended)
                {
                    code = ended;
                }
                else if (node.List != null)
                {
                    _coreWaiters.Remove(node);
                }

                if (code == SceError.NOTIFY_CALLBACK || (!waiter.Code.HasValue && code == SceError.OK))
                {
                    deadline.Interrupted = true;
                    continue;
                }

                if (code < 0 && code != SceError.WAIT_TIMEOUT && code != SceError.WAIT_DELETE)
                {
                    result.TrySet(EventBits.Error);
                }

                return code;
            }
        }
    }

    /// <summary>A callback notification reached a CB waiter in the core queue: it leaves it (a set meanwhile goes to
    /// the waiters behind). True when the thread was found there.</summary>
    protected bool CoreInterruptForCallbacks(GuestThread thread)
    {
        for (LinkedListNode<CoreWaiter>? node = _coreWaiters.First; node != null; node = node.Next)
        {
            if (node.Value.Thread != thread || node.Value.Code.HasValue || node.Value.Multi != null)
            {
                continue;
            }

            node.Value.Code = SceError.NOTIFY_CALLBACK;
            _coreWaiters.Remove(node);
            Monitor.PulseAll(this);
            return true;
        }

        return false;
    }

    /// <summary>
    /// A sceKernelWaitMultipleEvents call's first look at the object (0x8101ed4c): a pattern that matches now is taken
    /// (the auto reset applied); otherwise the call joins the queue at the end, unless it is an OR call that already
    /// has a result. True when it joined the queue.
    /// </summary>
    public bool AttachCoreMulti(MultiEventWait multi, int index, uint waitPattern)
    {
        lock (this)
        {
            uint hit = CoreVisible & waitPattern;
            if (hit != 0)
            {
                if (multi.Record(index, Uid, SceError.OK, hit, CoreUserData))
                {
                    CoreConsume(waitPattern);
                }

                return false;
            }

            if (CoreDeleted)
            {
                multi.Record(index, Uid, SceError.WAIT_DELETE, EventBits.Error, 0);
                return false;
            }

            if (multi.Complete)
            {
                return false;
            }

            _coreWaiters.AddLast(new CoreWaiter { WaitPattern = waitPattern, Thread = multi.Thread, Multi = multi, MultiIndex = index });
            return true;
        }
    }

    /// <summary>The multiple-events call ended: its place in the queue goes away.</summary>
    public void DetachCoreMulti(MultiEventWait multi)
    {
        lock (this)
        {
            for (LinkedListNode<CoreWaiter>? node = _coreWaiters.First; node != null;)
            {
                LinkedListNode<CoreWaiter>? next = node.Next;
                if (node.Value.Multi == multi)
                {
                    _coreWaiters.Remove(node);
                }

                node = next;
            }
        }
    }

    /// <summary>The kernel's raw write of the pattern and user data (SceThreadmgrForDriver 0x25AB8F6B): nobody is
    /// woken.</summary>
    public void WriteEventCore(uint pattern, ulong userData)
    {
        lock (this)
        {
            CorePattern = pattern;
            CoreUserData = userData;
        }
    }

    /// <summary>The kernel's set (SceThreadmgrForDriver 0xB7DF3EDF, 0x8101dcf8 under the object's lock): any bits, the
    /// queued waits they satisfy, then the registered callbacks notified with <paramref name="notifyArg"/>.</summary>
    public void KernelSetEvent(uint bits, ulong userData, int notifyArg)
    {
        lock (this)
        {
            CoreSet(bits, userData);
            NotifyRegisteredCallbacks(notifyArg);
        }
    }
}
