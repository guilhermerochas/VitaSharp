using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// The I/O scheduler of a process's asynchronous requests (the firmware's "SceIoScheduler" in iofilemgr: sceIoXxxAsync
/// queues the request to it and returns; its thread does the I/O and sets the request's event). One host thread runs
/// the queued requests in submission order (one device queue, so the requests of an fd also end in order) with the
/// storage model deferred (<see cref="IoTiming.BeginDeferred"/>): the host work is done at once, and the request
/// completes when the modelled device time ends, in guest time, through the kernel timer
/// (<see cref="TimerDispatcher"/>). Nobody blocks for the device time: not the submitting thread, not this one. The
/// thread exits when the queue is empty and is made again for the next request.
/// <para>Save states: a request still queued or running here makes the state retry (it holds host work not yet done);
/// one in the device is saved with its result and completion time and resumes from there (<see cref="Resume"/>).</para>
/// </summary>
internal sealed class IoScheduler : IGuestTimed
{
    private readonly object _gate = new();

    /// <summary>Serialises completions, so the requests end in their order (the worker and the timer both complete).</summary>
    private readonly object _completing = new();

    private readonly Queue<IoAsyncOp> _queue = new();

    /// <summary>Requests whose host work is done, by completion time (the order they were submitted in).</summary>
    private readonly List<IoAsyncOp> _inDevice = new();

    private bool _workerRunning;
    private IoAsyncOp? _running;

    /// <summary>The completion time of the last request into the device: a later request never ends before it.</summary>
    private long _lastDue;

    /// <summary>Bumped by a load: the request the worker was running belongs to the replaced session.</summary>
    private int _generation;

    public bool Armed { get; set; }

    public bool HasPending
    {
        get
        {
            lock (_gate)
            {
                return _inDevice.Count > 0;
            }
        }
    }

    /// <summary>Queues a submitted request (<see cref="IoAsyncOp.Request"/> set) and starts the worker if it is not running.</summary>
    public void Enqueue(IoAsyncOp op)
    {
        lock (_gate)
        {
            op.State = IoAsyncState.Queued;
            _queue.Enqueue(op);
            if (_workerRunning)
            {
                return;
            }

            _workerRunning = true;
        }

        new Thread(Work) { IsBackground = true, Name = "SceIoScheduler" }.Start();
    }

    /// <summary>A loaded state's request that was in the device: it completes at its saved time.</summary>
    public void Resume(IoAsyncOp op)
    {
        lock (_gate)
        {
            op.State = IoAsyncState.InDevice;
            _lastDue = Math.Max(_lastDue, op.DueTicks);
            int at = _inDevice.Count;
            while (at > 0 && _inDevice[at - 1].DueTicks > op.DueTicks)
            {
                at--;
            }

            _inDevice.Insert(at, op);
        }

        TimerDispatcher.Arm(this);
    }

    /// <summary>A load: this session's requests are dropped (the loaded state has its own).</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _generation++;
            _queue.Clear();
            _inDevice.Clear();
            _lastDue = 0;
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>sceIoCancel: a request not started yet ends with <paramref name="code"/>; any other is left to finish.</summary>
    public void Cancel(IoAsyncOp op, int code)
    {
        lock (_gate)
        {
            if (op.State != IoAsyncState.Queued)
            {
                return;
            }

            var rest = new List<IoAsyncOp>(_queue.Count);
            foreach (IoAsyncOp queued in _queue)
            {
                if (queued != op)
                {
                    rest.Add(queued);
                }
            }

            _queue.Clear();
            foreach (IoAsyncOp queued in rest)
            {
                _queue.Enqueue(queued);
            }

            op.Request = null;
            op.Cancelled = true;
            op.State = IoAsyncState.Done;
        }

        lock (_completing)
        {
            Complete(op, code);
        }
    }

    /// <summary>A request is queued or running here (its host work is not done).</summary>
    public bool HostBusy
    {
        get
        {
            lock (_gate)
            {
                return _queue.Count > 0 || _running != null;
            }
        }
    }

    /// <summary>Tests: waits until no request is queued or running here.</summary>
    public void WaitHostIdle()
    {
        lock (_gate)
        {
            while (_queue.Count > 0 || _running != null)
            {
                Monitor.Wait(_gate);
            }
        }
    }

    /// <summary>Runs a request's work, mapping the host's errors to the firmware's codes; the modelled device time it
    /// reserved goes to <paramref name="deviceDone"/> (guest ticks, at least <paramref name="from"/>).</summary>
    public static long Execute(Func<long> work, long from, out long deviceDone)
    {
        IoTiming.BeginDeferred(from);
        try
        {
            return Execute(work);
        }
        finally
        {
            deviceDone = IoTiming.EndDeferred();
        }
    }

    /// <summary>Runs a request's work on the calling thread (a request that needs a guest thread: its device time, if
    /// any, is spent here as a synchronous call would).</summary>
    public static long Execute(Func<long> work)
    {
        IoTiming.InAsyncRequest = true;
        try
        {
            return work();
        }
        catch (IOException)
        {
            return SceError.IO_EINVAL;
        }
        catch (UnauthorizedAccessException)
        {
            return SceError.IO_EACCES;
        }
        finally
        {
            IoTiming.InAsyncRequest = false;
        }
    }

    /// <summary>The request's end: the result into its param (in the submitter's address space), then the completion
    /// bit, which wakes sceIoComplete and the event waits.</summary>
    public static void Complete(IoAsyncOp op, long result)
    {
        if (op.Param != 0)
        {
            using (AddressSpace.Use(op.Space))
            {
                if (GuestMemory.IsMapped(op.Param))
                {
                    new Ptr<long>(op.Param).Value = result;
                }
            }
        }

        op.Finish(result);
    }

    private void Work()
    {
        while (true)
        {
            IoAsyncOp op;
            int generation;
            lock (_gate)
            {
                if (_queue.Count == 0)
                {
                    _workerRunning = false;
                    Monitor.PulseAll(_gate);
                    return;
                }

                op = _queue.Dequeue();
                op.State = IoAsyncState.Running;
                _running = op;
                generation = _generation;
            }

            Func<long> work = op.Request!;
            op.Request = null;
            long result;
            long deviceDone;
            using (AddressSpace.Use(op.Space))
            {
                result = Execute(work, op.SubmitTicks, out deviceDone);
            }

            bool current;
            lock (_gate)
            {
                current = generation == _generation;
                if (current)
                {
                    long due = Math.Max(Math.Max(deviceDone, op.SubmitTicks), _lastDue);
                    _lastDue = due;
                    op.Result = result;
                    op.DueTicks = due;
                    op.State = IoAsyncState.InDevice;
                    _inDevice.Add(op);
                }
            }

            // a request with no device time left completes now, before the worker counts as idle
            if (current && FireDueAndRemaining() != null)
            {
                TimerDispatcher.Arm(this);
            }

            lock (_gate)
            {
                _running = null;
                Monitor.PulseAll(_gate);
            }
        }
    }

    /// <summary>Completes, in order, the requests whose device time has ended; the microseconds to the next one.</summary>
    public ulong? FireDueAndRemaining()
    {
        lock (_completing)
        {
            while (true)
            {
                IoAsyncOp op;
                long now = GuestClock.Ticks;
                lock (_gate)
                {
                    if (_inDevice.Count == 0)
                    {
                        return null;
                    }

                    op = _inDevice[0];
                    if (op.DueTicks > now)
                    {
                        return (ulong)GuestClock.ToMicroseconds(op.DueTicks - now) + 1;
                    }

                    _inDevice.RemoveAt(0);
                }

                Complete(op, op.Result);
            }
        }
    }
}
