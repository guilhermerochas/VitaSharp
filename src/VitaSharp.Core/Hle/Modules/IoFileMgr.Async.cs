using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// One asynchronous I/O request (sceIoXxxAsync). The opid returned to the guest is this object's UID. It is an event
/// object: <see cref="Pattern"/> gets <see cref="CompletionBit"/> when the request finishes and waiters on the object
/// (sceKernelWaitEvent-style, Monitor on the instance) are pulsed; sceIoComplete then returns <see cref="Result"/>.
/// </summary>
public sealed class IoAsyncOp : KernelObject
{
    /// <summary>Event bit set on completion (inferred; to confirm with a title using async I/O).</summary>
    public const uint CompletionBit = 0x1;
    public int Fd = -1;
    public long Result;
    public bool Done, Cancelled;
    public uint Pattern;
    public ulong UserData;

    public void Finish(long result)
    {
        lock (this)
        {
            Result = result;
            Done = true;
            Pattern |= CompletionBit;
            Monitor.PulseAll(this);
        }
    }
}

/// <summary>
/// Asynchronous I/O and I/O priorities. Phase 1 of the spec (docs/hle/B-io.md section 4): every request runs to
/// completion inside the submitting call, on the caller's thread, so the observable result (read only through
/// sceIoComplete) is the synchronous one and no new concurrency is introduced. Arguments that are invalid without
/// doing any I/O (unknown fd) fail immediately; every other error is the request's result.
/// </summary>
public sealed partial class IoFileMgr
{
    const int IO_ECANCELED = unchecked((int)0x8001008C);

    readonly ConcurrentDictionary<int, IoAsyncOp> _ops = new();
    int _nextLocalOp = 0x7000_0001; // UIDs when no kernel is attached (unit tests)

    bool IsKnownFd(int fd) => _files.ContainsKey(fd) || _dirs.ContainsKey(fd) || IsSpecialFd(fd);

    /// <summary>Runs <paramref name="op"/> now and returns the opid holding its result.</summary>
    int Submit(int fd, Func<long> op)
    {
        if (fd >= 0 && !IsKnownFd(fd)) return SceError.IO_EBADF;
        var o = new IoAsyncOp { Fd = fd, Name = "SceIoAsync" };
        int uid = Emu != null ? K.Objects.Register(o) : (o.Uid = Interlocked.Add(ref _nextLocalOp, 2));
        _ops[uid] = o;
        long r;
        try { r = op(); }
        catch (IOException) { r = SceError.IO_EINVAL; }
        catch (UnauthorizedAccessException) { r = SceError.IO_EACCES; }
        o.Finish(r);
        return uid;
    }

    [Hle] int sceIoOpenAsync(string path, int flags, int mode) => Submit(-1, () => sceIoOpen(path, flags, mode));
    [Hle] int sceIoCloseAsync(int fd) => Submit(fd, () => _dirs.ContainsKey(fd) ? sceIoDclose(fd) : sceIoClose(fd));
    [Hle] int sceIoReadAsync(int fd, uint buf, uint size) => Submit(fd, () => sceIoRead(fd, buf, size));
    [Hle] int sceIoWriteAsync(int fd, uint buf, uint size) => Submit(fd, () => sceIoWrite(fd, buf, size));
    [Hle] int sceIoPreadAsync(int fd, uint buf, uint size, long offset) => Submit(fd, () => sceIoPread(fd, buf, size, offset));
    [Hle] int sceIoPwriteAsync(int fd, uint buf, uint size, long offset) => Submit(fd, () => sceIoPwrite(fd, buf, size, offset));
    [Hle] int sceIoLseekAsync(int fd, long offset, int whence) => Submit(fd, () => sceIoLseek(fd, offset, whence));
    [Hle] int sceIoSyncAsync(uint device, int flags) => Submit(-1, () => sceIoSync(device, flags));
    [Hle] int sceIoSyncByFdAsync(int fd, int flag) => Submit(fd, () => sceIoSyncByFd(fd, flag));
    [Hle] int sceIoGetstatAsync(string path, uint stat) => Submit(-1, () => sceIoGetstat(path, stat));
    [Hle] int sceIoGetstatByFdAsync(int fd, uint stat) => Submit(fd, () => sceIoGetstatByFd(fd, stat));
    [Hle] int sceIoChstatAsync(string path, uint stat, int bits) => Submit(-1, () => sceIoChstat(path, stat, bits));
    [Hle] int sceIoChstatByFdAsync(int fd, uint stat, int bits) => Submit(fd, () => sceIoChstatByFd(fd, stat, bits));
    [Hle] int sceIoMkdirAsync(string path, int mode) => Submit(-1, () => sceIoMkdir(path, mode));
    [Hle] int sceIoRmdirAsync(string path) => Submit(-1, () => sceIoRmdir(path));
    [Hle] int sceIoRemoveAsync(string path) => Submit(-1, () => sceIoRemove(path));
    [Hle] int sceIoRenameAsync(string oldPath, string newPath) => Submit(-1, () => sceIoRename(oldPath, newPath));
    [Hle] int sceIoDopenAsync(string path) => Submit(-1, () => sceIoDopen(path));
    [Hle] int sceIoDreadAsync(int fd, uint dirent) => Submit(fd, () => sceIoDread(fd, dirent));
    [Hle] int sceIoDcloseAsync(int fd) => Submit(fd, () => sceIoDclose(fd));

    [Hle]
    int sceIoDevctlAsync(string dev, int cmd, uint arg, uint argLen, uint buf, uint bufLen)
        => Submit(-1, () => sceIoDevctl(dev, cmd, arg, argLen, buf, bufLen));

    [Hle]
    int sceIoIoctlAsync(int fd, int cmd, uint arg, uint argLen, uint buf, uint bufLen)
        => Submit(fd, () => sceIoIoctl(fd, cmd, arg, argLen, buf, bufLen));

    /// <summary>Waits for the request if needed, returns its result (low 32 bits for Lseek) and frees the opid.</summary>
    [Hle]
    int sceIoComplete(int opid)
    {
        if (!_ops.TryRemove(opid, out var op)) return SceError.UNKNOWN_EVENT_ID;
        lock (op)
        {
            if (!op.Done) Wait.Until(op, op, static o => o.Done, default, null, op.WaitDescription);
        }
        if (Emu != null) K.Objects.Remove(opid);
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
    [Hle("_sceIoCompleteMultiple")]
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
            if (Emu != null) K.Objects.Remove(opid);
            done++;
        }
        numCompleted.TrySet(done);
        return r;
    }

    /// <summary>Cancels pending requests by opid or by fd. Phase 1 requests are already complete when the opid is
    /// returned, so there is never anything left to cancel: a known opid/fd returns 0, anything else EBADF.</summary>
    [Hle]
    int sceIoCancel(int id)
    {
        if (_ops.TryGetValue(id, out var op))
        {
            lock (op) if (!op.Done) { op.Cancelled = true; op.Finish(IO_ECANCELED); }
            return 0;
        }
        return IsKnownFd(id) ? 0 : SceError.IO_EBADF;
    }

    // ------------------------------------------------------------------ I/O priority
    // VitaSharp has no I/O queue (requests complete synchronously), so priorities are only stored and reported.
    // The valid range and the initial process default are not documented; values are kept as given (negative ones
    // are rejected) and the process default starts at 0 until a title shows otherwise.

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

    [Hle]
    [Hle("sceIoGetPriorityForSystem")]
    int sceIoGetPriority(int fd)
    {
        if (!_files.TryGetValue(fd, out var f)) return IsSpecialFd(fd) ? ThreadDefaultPriority : SceError.IO_EBADF;
        return f.Priority >= 0 ? f.Priority : ThreadDefaultPriority;
    }

    [Hle]
    [Hle("sceIoSetPriorityForSystem")]
    int sceIoSetPriority(int fd, int priority)
    {
        if (!_files.TryGetValue(fd, out var f)) return SceError.IO_EBADF;
        if (priority < 0) return SceError.ILLEGAL_PRIORITY;
        f.Priority = priority;
        return 0;
    }

    [Hle]
    [Hle("sceIoGetThreadDefaultPriorityForSystem")]
    int sceIoGetThreadDefaultPriority() => ThreadDefaultPriority;

    [Hle]
    [Hle("sceIoSetThreadDefaultPriorityForSystem")]
    int sceIoSetThreadDefaultPriority(int priority)
    {
        if (priority < 0) return SceError.ILLEGAL_PRIORITY;
        var t = GuestThread.Current;
        if (t == null) _noThreadPriority = priority;
        else _threadPriority.GetOrCreateValue(t).Value = priority;
        return 0;
    }

    [Hle] int sceIoGetProcessDefaultPriority() => _processDefaultPriority;

    [Hle]
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
