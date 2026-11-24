using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>Where an asynchronous request is (guarded by the I/O scheduler's lock).</summary>
public enum IoAsyncState
{
    /// <summary>Submitted, waiting for the I/O scheduler.</summary>
    Queued,

    /// <summary>The I/O scheduler is doing its host work (reading or writing the host file).</summary>
    Running,

    /// <summary>The host work is done; the request ends when the modelled device time does (<see cref="IoAsyncOp.DueTicks"/>).</summary>
    InDevice,

    /// <summary>Completed: the result is in the param and the completion bit is set.</summary>
    Done,
}

/// <summary>
/// One asynchronous I/O request (sceIoXxxAsync). The opid returned to the guest is this object's UID. It is an object of
/// an event class iofilemgr derives from threadmgr's (SceThreadmgrForDriver 0xDBB77032, "SceUIDIoAsyncEventClass"), so
/// it has the event core every such object has (<see cref="EventObject"/>): the request's end sets
/// <see cref="CompletionBit"/> in its pattern through the class's set, handing it to the waits queued there
/// (sceKernelWaitEvent, sceKernelWaitMultipleEvents); sceIoComplete then returns <see cref="Result"/>.
/// </summary>
public sealed class IoAsyncOp : EventObject
{
    /// <summary>Event bit set on completion (inferred; to confirm with a title using async I/O).</summary>
    public const uint CompletionBit = 0x1;
    public int Fd = -1;
    public long Result;
    public bool Done, Cancelled;

    /// <summary>Where the request is (see <see cref="IoAsyncState"/>; the I/O scheduler's lock guards it).</summary>
    internal IoAsyncState State = IoAsyncState.Done;

    /// <summary>The request's work (the synchronous call), run by the I/O scheduler; null once it ran.</summary>
    internal Func<long>? Request;

    /// <summary>Where the I/O scheduler runs <see cref="Request"/> (null: on its own host thread).</summary>
    internal IoScheduler.RequestRunner? Runner;

    /// <summary>The SceIoAsyncParam the result goes to on completion (0: none).</summary>
    internal uint Param;

    /// <summary>The address space of the process that submitted it (its buffers and its param).</summary>
    internal AddressSpace? Space;

    /// <summary>Guest clock ticks: when it was submitted, and when the device finishes it (<see cref="IoAsyncState.InDevice"/>).</summary>
    internal long SubmitTicks, DueTicks;

    /// <summary>The event pattern (the core's word; a write does not wake anyone, as the kernel's raw setter).</summary>
    public uint Pattern
    {
        get => CorePattern;
        set => CorePattern = value;
    }

    /// <summary>The last set's user data.</summary>
    public ulong UserData
    {
        get => CoreUserData;
        set => CoreUserData = value;
    }

    public void Finish(long result)
    {
        lock (this)
        {
            Result = result;
            Done = true;
            State = IoAsyncState.Done;
            CoreSet(CompletionBit, CoreUserData);
        }
    }

    /// <summary>sceKernelPollEvent on the opid: the completion bit once the request finished, EVENT_COND before.</summary>
    public int PollEvent(uint waitPattern, Ptr<uint> resultPattern, Ptr<ulong> userData) => PollCoreEvent(waitPattern, resultPattern, userData);

    /// <summary>
    /// sceKernelWaitEvent on the opid, as libpaf's file reader waits for each sceIoReadAsync before sceIoComplete (the
    /// shell copying a pic0.png into ur0:temp/appmeta for a game's LiveArea): waits in the queue for <see cref="Finish"/>
    /// or the timeout (guest microseconds).
    /// </summary>
    public int WaitEvent(uint waitPattern, Ptr<uint> resultPattern, Ptr<ulong> userData, Ptr<uint> timeout)
        => WaitCoreEvent(GuestThread.Current, waitPattern, resultPattern, userData, timeout);

    /// <summary>sceKernelSet/PulseEventWithNotifyCallback on the opid: the user bits go into its event pattern.</summary>
    protected override void SetForNotify(uint setPattern, ulong userData, bool pulse) => CoreSet(setPattern, userData, pulse);
}

/// <summary>
/// Asynchronous I/O and I/O priorities. As in the firmware (3.74 iofilemgr: each sceIoXxxAsync makes its
/// "SceIoAsyncEvent" object, queues the request to the "SceIoScheduler" and returns the opid; the scheduler's thread
/// does the I/O and sets the event), a request returns at once and runs on the I/O scheduler (<see cref="IoScheduler"/>),
/// off the guest thread: the device time of the storage model (<see cref="IoTiming"/>) is spent by the request, not by
/// the thread that submitted it. Arguments that are invalid without doing any I/O (unknown fd) fail immediately; every
/// other error is the request's result. A request on a guest driver's mount runs the driver's guest code, which needs
/// a guest thread: it still runs in the submitting call.
/// </summary>
public sealed partial class IoFileMgr
{
    const int IO_ECANCELED = unchecked((int)0x8001008C);

    readonly ConcurrentDictionary<int, IoAsyncOp> _ops = new();
    int _nextLocalOp = 0x7000_0001; // UIDs when no kernel is attached (unit tests)

    IoScheduler? _scheduler;

    IoScheduler AsyncScheduler => LazyInitializer.EnsureInitialized(ref _scheduler, static () => new IoScheduler());

    bool IsKnownFd(int fd) => _files.ContainsKey(fd) || _dirs.ContainsKey(fd) || IsSpecialFd(fd);

    /// <summary>The request has to run guest code (a guest driver's file system): it cannot leave the guest thread.</summary>
    bool NeedsGuestThread(int fd, string? path, string? otherPath)
    {
        if (t_inHostVop)
        {
            return true;
        }

        if (fd >= 0 && _files.TryGetValue(fd, out OpenFile? file) && file.GuestFile != 0)
        {
            return true;
        }

        if (fd >= 0 && _dirs.TryGetValue(fd, out OpenDir? dir) && dir.GuestFile != 0)
        {
            return true;
        }

        return (path != null && ResolveGuestMount(path, out _) != null) || (otherPath != null && ResolveGuestMount(otherPath, out _) != null);
    }

    /// <summary>
    /// Queues <paramref name="op"/> to the I/O scheduler and returns the opid that will hold its result. Every async
    /// call takes a trailing <c>SceIoAsyncParam *</c> (<paramref name="param"/>) whose first 8 bytes receive the result
    /// (sign-extended to 64 bits: the fd of an open, the bytes of a read, the offset of a seek, or an error) when the
    /// request completes: libpaf's file object passes its +0x10, waits with sceKernelWaitEvent(opid, 1) and reads the
    /// fd there before sceIoComplete, so without it the Camera wrote its picture to fd 0 and failed (0x80AF5021, "Could
    /// not save the image"). <paramref name="path"/>/<paramref name="otherPath"/>: the paths the request names (one on
    /// a guest driver's mount keeps the request on the calling thread).
    /// </summary>
    int Submit(int fd, Func<long> op, uint param = 0, string? path = null, string? otherPath = null)
    {
        if (fd >= 0 && !IsKnownFd(fd))
        {
            return SceError.IO_EBADF;
        }

        var request = new IoAsyncOp { Fd = fd, Name = "SceIoAsyncEvent", Param = param, Space = AddressSpace.Current, SubmitTicks = GuestClock.Ticks };
        int uid = Emu?.Kernel != null ? K.Objects.Register(request) : (request.Uid = Interlocked.Add(ref _nextLocalOp, 2));
        _ops[uid] = request;
        if (NeedsGuestThread(fd, path, otherPath))
        {
            IoScheduler.Complete(request, IoScheduler.Execute(op));
            return uid;
        }

        request.Request = op;
        if (path != null || otherPath != null)
        {
            request.Runner = _runPathRequest ??= RunPathRequest;
        }

        AsyncScheduler.Enqueue(request);
        return uid;
    }

    /// <summary>The cached <see cref="RunPathRequest"/> delegate (no allocation per request).</summary>
    [SnapshotIgnore]
    IoScheduler.RequestRunner? _runPathRequest;

    /// <summary>
    /// A request that names a path, as the I/O scheduler runs it. On the Vita the request runs on iofilemgr's own
    /// kernel thread (the "SceIoScheduler" of 3.74, study only), which walks the path through the vfs as the
    /// synchronous call does, one ksceVopLookup per element, for the process that submitted it. The emulator's devices
    /// answer without the walk, so the request runs on the scheduler's host thread; only when a hook took ksceVopLookup
    /// (<see cref="HostWalk"/>), checked when the request runs, it runs on a kernel thread named as the firmware's, in
    /// the submitting process's context (its devices, its host vnodes and their ops, its address space; VitaSharp runs
    /// kernel code on the threads of every process), so the hook is called for it as for a synchronous call and its
    /// answer is the request's. The device time is the request's either way.
    /// </summary>
    long RunPathRequest(Func<long> work, long from, out long deviceDone)
    {
        if (!HleEntryCalls.IsHooked(this, s_hleEntry_ksceVopLookup) || Emu is not { } emu)
        {
            return IoScheduler.Execute(work, from, out deviceDone);
        }

        AddressSpace? space = AddressSpace.Current;
        long result = SceError.IO_EINVAL;
        long done = from;
        // a kernel thread in the submitting process's context: its devices, its host vnodes and their ops, its memory
        emu.RunOnKernelThread(emu, "SceIoScheduler", _ =>
        {
            using (AddressSpace.Use(space))
            {
                result = IoScheduler.Execute(work, from, out done);
            }
        }, "an asynchronous I/O request");
        deviceDone = done;
        return result;
    }

    /// <summary>Tests: waits until the I/O scheduler has done the host work of every queued request (their completion
    /// still waits for the device time, in guest time).</summary>
    internal void WaitForAsyncHostWork() => _scheduler?.WaitHostIdle();

    /// <summary>A load waits for the host work of this session's queued requests: the I/O scheduler would write a
    /// read's data into the loaded memory.</summary>
    public string? CheckLoad() => _scheduler is { HostBusy: true } ? "an asynchronous I/O request is being served" : null;

    /// <summary>A loaded state replaces this session's requests: the ones the state has in the device go on to their
    /// saved completion time (their host work was done before the save; the state holds the result).</summary>
    void AfterAsyncLoad()
    {
        _scheduler?.Reset();
        foreach (IoAsyncOp op in _ops.Values)
        {
            if (!op.Done)
            {
                op.Space = Emu?.Space;
                AsyncScheduler.Resume(op);
            }
        }
    }

    [Hle] int sceIoOpenAsync(string path, int flags, int mode, uint param) => Submit(-1, () => sceIoOpen(path, flags, mode), param, path);
    [Hle] int sceIoCloseAsync(int fd, uint param) => Submit(fd, () => _dirs.ContainsKey(fd) ? sceIoDcloseThroughEntry(fd) : sceIoCloseThroughEntry(fd), param);
    [Hle] int sceIoReadAsync(int fd, uint buf, uint size, uint param) => Submit(fd, () => sceIoReadThroughEntry(fd, buf, size), param);
    [Hle] int sceIoWriteAsync(int fd, uint buf, uint size, uint param) => Submit(fd, () => sceIoWriteThroughEntry(fd, buf, size), param);
    [Hle] int sceIoPreadAsync(int fd, uint buf, uint size, long offset, uint param) => Submit(fd, () => sceIoPreadThroughEntry(fd, buf, size, offset), param);
    [Hle] int sceIoPwriteAsync(int fd, uint buf, uint size, long offset, uint param) => Submit(fd, () => sceIoPwriteThroughEntry(fd, buf, size, offset), param);
    [Hle] int sceIoLseekAsync(int fd, long offset, int whence, uint param) => Submit(fd, () => sceIoLseekThroughEntry(fd, offset, whence), param);
    [Hle] int sceIoSyncAsync(uint device, int flags, uint param) => Submit(-1, () => sceIoSyncThroughEntry(device, flags), param);
    [Hle] int sceIoSyncByFdAsync(int fd, int flag, uint param) => Submit(fd, () => sceIoSyncByFdThroughEntry(fd, flag), param);
    [Hle] int sceIoGetstatAsync(string path, uint stat, uint param) => Submit(-1, () => sceIoGetstat(path, stat), param, path);
    [Hle] int sceIoGetstatByFdAsync(int fd, uint stat, uint param) => Submit(fd, () => sceIoGetstatByFdThroughEntry(fd, stat), param);
    [Hle] int sceIoChstatAsync(string path, uint stat, int bits, uint param) => Submit(-1, () => sceIoChstat(path, stat, bits), param, path);
    [Hle] int sceIoChstatByFdAsync(int fd, uint stat, int bits, uint param) => Submit(fd, () => sceIoChstatByFdThroughEntry(fd, stat, bits), param);
    [Hle] int sceIoMkdirAsync(string path, int mode, uint param) => Submit(-1, () => sceIoMkdir(path, mode), param, path);
    [Hle] int sceIoRmdirAsync(string path, uint param) => Submit(-1, () => sceIoRmdir(path), param, path);
    [Hle] int sceIoRemoveAsync(string path, uint param) => Submit(-1, () => sceIoRemove(path), param, path);
    [Hle] int sceIoRenameAsync(string oldPath, string newPath, uint param) => Submit(-1, () => sceIoRename(oldPath, newPath), param, oldPath, newPath);
    [Hle] int sceIoDopenAsync(string path, uint param) => Submit(-1, () => sceIoDopen(path), param, path);
    [Hle] int sceIoDreadAsync(int fd, uint dirent, uint param) => Submit(fd, () => sceIoDreadThroughEntry(fd, dirent), param);
    [Hle] int sceIoDcloseAsync(int fd, uint param) => Submit(fd, () => sceIoDcloseThroughEntry(fd), param);

    [Hle]
    int sceIoDevctlAsync(string dev, int cmd, uint arg, uint argLen, uint buf, uint bufLen, uint param)
        => Submit(-1, () => sceIoDevctl(dev, cmd, arg, argLen, buf, bufLen), param, dev);

    [Hle]
    int sceIoIoctlAsync(int fd, int cmd, uint arg, uint argLen, uint buf, uint bufLen, uint param)
        => Submit(fd, () => sceIoIoctlThroughEntry(fd, cmd, arg, argLen, buf, bufLen), param);

    /// <summary>Waits for the request if needed, returns its result (low 32 bits for Lseek) and frees the opid.</summary>
    [Hle]
    int sceIoComplete(int opid)
    {
        if (!_ops.TryRemove(opid, out var op)) return SceError.UNKNOWN_EVENT_ID;
        lock (op)
        {
            if (!op.Done) Wait.Until(op, op, static o => o.Done, default, null, op.WaitDescription);
        }
        if (Emu?.Kernel != null) K.Objects.Remove(opid);
        return op.Cancelled ? IO_ECANCELED : (int)op.Result;
    }

    /// <summary>
    /// <c>int (const SceIoAsyncParam *params, int count, int *numCompleted)</c> (the SceLibKernel export forwards to the
    /// syscall; layout from the firmware's iofilemgr): <paramref name="count"/> entries of 0x18 bytes whose first word
    /// is an opid. Each request is finished and its opid released, in order, stopping at the first one still running
    /// (EBUSY 0x80010010) or unknown; <c>*numCompleted</c> gets how many were finished. Only that count is written:
    /// the firmware returns no per-request results here (they are read with sceIoComplete beforehand or not at all).
    /// The firmware caps <paramref name="count"/> at a per-process limit of at least 0x5C (EINVAL above it).
    /// </summary>
    [Hle]
    [Hle("_sceIoCompleteMultiple", Cost = HleCost.KernelEntry)]
    int sceIoCompleteMultiple(uint parameters, int count, Ptr<int> numCompleted)
    {
        const int EINVAL = unchecked((int)0x80010016), EBUSY = unchecked((int)0x80010010);
        if (count > 0x5C) return EINVAL;
        int done = 0, r = 0;
        for (int i = 0; i < count; i++)
        {
            int opid = (int)GuestMemory.R32(parameters + (uint)i * 0x18);
            if (!_ops.TryGetValue(opid, out var op)) { r = SceError.UNKNOWN_EVENT_ID; break; }
            lock (op) if (!op.Done) { r = EBUSY; break; }
            _ops.TryRemove(opid, out _);
            if (Emu?.Kernel != null) K.Objects.Remove(opid);
            done++;
        }
        numCompleted.TrySet(done);
        return r;
    }

    /// <summary>Cancels requests by opid or by fd: a request the I/O scheduler has not started ends with ECANCELED
    /// (sceIoComplete returns it); one already done or in the device is left to finish. A known opid/fd returns 0,
    /// anything else EBADF.</summary>
    [Hle]
    int sceIoCancel(int id)
    {
        if (_ops.TryGetValue(id, out IoAsyncOp? op))
        {
            _scheduler?.Cancel(op, IO_ECANCELED);
            return 0;
        }

        if (!IsKnownFd(id))
        {
            return SceError.IO_EBADF;
        }

        foreach (IoAsyncOp pending in _ops.Values)
        {
            if (pending.Fd == id)
            {
                _scheduler?.Cancel(pending, IO_ECANCELED);
            }
        }

        return 0;
    }

    // ------------------------------------------------------------------ I/O priority
    // The I/O scheduler serves requests in submission order (one device queue), so priorities are only stored and
    // reported.
    // The valid range and the initial process default are not documented; values are kept as given (negative ones
    // are rejected) and the process default starts at 0 until a title shows otherwise.
    // The getters and setters only read or store a value: a kernel entry, not the module's blocking-I/O wait.

    int _processDefaultPriority;
    readonly ConditionalWeakTable<GuestThread, StrongBox<int>> _threadPriority = new();
    int _noThreadPriority = -1;

    int ThreadDefaultPriority
    {
        get
        {
            var t = GuestThread.Current;
            int v = t == null ? _noThreadPriority : _threadPriority.TryGetValue(t, out var b) ? b.Value : -1;
            return v >= 0 ? v : _processDefaultPriority;
        }
    }

    [Hle(Cost = HleCost.KernelEntry)]
    [Hle("sceIoGetPriorityForSystem", Cost = HleCost.KernelEntry)]
    int sceIoGetPriority(int fd)
    {
        if (!_files.TryGetValue(fd, out var f)) return IsSpecialFd(fd) ? ThreadDefaultPriority : SceError.IO_EBADF;
        return f.Priority >= 0 ? f.Priority : ThreadDefaultPriority;
    }

    [Hle(Cost = HleCost.KernelEntry)]
    [Hle("sceIoSetPriorityForSystem", Cost = HleCost.KernelEntry)]
    int sceIoSetPriority(int fd, int priority)
    {
        if (!_files.TryGetValue(fd, out var f)) return SceError.IO_EBADF;
        if (priority < 0) return SceError.ILLEGAL_PRIORITY;
        f.Priority = priority;
        return 0;
    }

    [Hle(Cost = HleCost.KernelEntry)]
    [Hle("sceIoGetThreadDefaultPriorityForSystem", Cost = HleCost.KernelEntry)]
    int sceIoGetThreadDefaultPriority() => ThreadDefaultPriority;

    [Hle(Cost = HleCost.KernelEntry)]
    [Hle("sceIoSetThreadDefaultPriorityForSystem", Cost = HleCost.KernelEntry)]
    int sceIoSetThreadDefaultPriority(int priority)
    {
        if (priority < 0) return SceError.ILLEGAL_PRIORITY;
        var t = GuestThread.Current;
        if (t == null) _noThreadPriority = priority;
        else _threadPriority.GetOrCreateValue(t).Value = priority;
        return 0;
    }

    [Hle(Cost = HleCost.KernelEntry)] int sceIoGetProcessDefaultPriority() => _processDefaultPriority;

    [Hle(Cost = HleCost.KernelEntry)]
    int sceIoSetProcessDefaultPriority(int priority)
    {
        if (priority < 0) return SceError.ILLEGAL_PRIORITY;
        _processDefaultPriority = priority;
        return 0;
    }

    // The *ForSystem aliases above are the privileged entries used by system modules (they also accept system
    // priorities, which the plain range check here does not distinguish).

    /// <summary>File lock for system processes, no public prototype (inferred <c>int (SceUID fd, int op)</c>). Files are
    /// never shared between processes here, so only the descriptor is checked.</summary>
    [Hle] int sceIoFlockForSystem(int fd, int op) => _files.ContainsKey(fd) ? 0 : SceError.IO_EBADF;
}
