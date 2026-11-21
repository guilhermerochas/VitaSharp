namespace VitaSharp.Core.Kernel;

/// <summary>
/// One sceKernelWaitMultipleEvents(CB) call, as threadmgr 3.74 does it (the syscalls 0x8102c950 / 0x8102cc4c, the wait 0x8101ed4c,
/// and the event class's handler for such a waiter, reached from the event set 0x8101dcf8, the pulse 0x8101e370 and the
/// cancels, studied in the decompiled module): ONE wait in several event objects at once. The waiter is in the wait queue of each simple event
/// it names, in its arrival order (or by priority with TH_PRIO), as a single wait would be; the set (or pulse) that
/// reaches it there hands it that event's bits and user data inside the set, applying the auto reset as it does for a
/// single waiter, and appends a result in the order the events fired; a cancel or a delete appends that event's error.
/// The call ends at the first result in OR mode and once every event has one in AND mode; an event reached after an OR
/// call already ended is not taken. Timers, message pipes and asynchronous I/O operations queue it the same way (the
/// timer's fire, the pipe's send and receive, the operation's end take it inside their set). In the CB form a callback
/// notification ends the sleep (NOTIFY_CALLBACK): from then on a set that reaches the call's place in a queue takes
/// nothing and drops that place (0x8101dcf8 hands a multiple wait the event only while its thread still sleeps), the
/// call leaves every queue (0x8101d7a4), runs the callbacks and makes its first pass again over the events still
/// without a result, joining their queues anew at the end, under the same deadline.
/// </summary>
public sealed class MultiEventWait : ICallbackInterruptedWait
{
    /// <summary>One SceKernelResultEvent: the event's UID, its result, the bits it reported and its user data.</summary>
    public readonly record struct Result(int Uid, int Code, uint Pattern, ulong UserData);

    private readonly bool _or;
    private readonly ulong[] _done;
    private int _remaining;
    private readonly List<Result> _results = new();
    private int _changes;
    private bool _interrupted;

    public MultiEventWait(int count, bool or, GuestThread? thread)
    {
        _or = or;
        _remaining = count;
        _done = new ulong[(count + 63) / 64];
        Thread = thread;
    }

    /// <summary>The waiting thread (null: a host caller).</summary>
    public GuestThread? Thread { get; }

    /// <summary>The call has what it waits for: a result in OR mode, every event in AND mode (or nothing to wait for).</summary>
    public bool Complete
    {
        get
        {
            lock (this)
            {
                return CompleteLocked;
            }
        }
    }

    private bool CompleteLocked => _remaining == 0 || (_or && _results.Count > 0);

    /// <summary>Event <paramref name="index"/> of the call is done: its result is appended in the order the events
    /// fired. False when the call is already complete (an OR call another event ended) or the event is done: then it
    /// takes nothing (the set leaves the event's bits for the next waiter). Called by the event, under its lock.</summary>
    public bool Record(int index, int uid, int code, uint pattern, ulong userData)
    {
        lock (this)
        {
            ulong bit = 1UL << (index & 63);
            if (CompleteLocked || _interrupted || (_done[index >> 6] & bit) != 0)
            {
                return false;
            }

            _done[index >> 6] |= bit;
            _remaining--;
            _results.Add(new Result(uid, code, pattern, userData));
            _changes++;
            Monitor.PulseAll(this);
            return true;
        }
    }

    /// <summary>A callback notification ended the CB call's sleep (threadmgr 3.74's notify): the call takes nothing
    /// until its next pass (<see cref="BeginPass"/>). Called holding this object's lock.</summary>
    public void InterruptForCallbacks(GuestThread thread)
    {
        if (thread != Thread)
        {
            return;
        }

        _interrupted = true;
        _changes++;
    }

    /// <summary>A callback notification ended the sleep: the call leaves its queues and runs its callbacks.</summary>
    public bool Interrupted
    {
        get
        {
            lock (this)
            {
                return _interrupted;
            }
        }
    }

    /// <summary>A pass over the events starts (the first, or the one after the callbacks): the call takes results
    /// again.</summary>
    public void BeginPass()
    {
        lock (this)
        {
            _interrupted = false;
        }
    }

    /// <summary>Event <paramref name="index"/> already has its result.</summary>
    public bool IsDone(int index)
    {
        lock (this)
        {
            return (_done[index >> 6] & (1UL << (index & 63))) != 0;
        }
    }

    /// <summary>A level-triggered event the call reads (a pipe, a timer, an I/O operation) changed: the waiter looks at
    /// them again.</summary>
    public void Poke()
    {
        lock (this)
        {
            _changes++;
            Monitor.PulseAll(this);
        }
    }

    /// <summary>A count that grows with every result and every poke: a wait that saw it unchanged has missed nothing.</summary>
    public int Changes
    {
        get
        {
            lock (this)
            {
                return _changes;
            }
        }
    }

    /// <summary>The results so far, in the order the events fired.</summary>
    public IReadOnlyList<Result> Results
    {
        get
        {
            lock (this)
            {
                return _results.ToArray();
            }
        }
    }

    /// <summary>The wait-loop predicate: complete, or something changed since <paramref name="seen"/> (the caller holds
    /// this object's lock).</summary>
    internal bool ChangedSince(int seen) => CompleteLocked || _interrupted || _changes != seen;
}
