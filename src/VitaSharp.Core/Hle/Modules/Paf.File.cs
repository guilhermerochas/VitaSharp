using System.Collections.Concurrent;
using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// paf::File and its local and memory files, and paf::Dir. A file object is libpaf's: a virtual table at +0 (the
/// File interface order: GetFileType, GetCapability, GetFileSize, Open, OpenAsync, Close, CloseAsync, IsOpened, IsEof,
/// Abort, Read, ReadAsync, Write, WriteAsync, Seek, SeekAsync, Flush, FlushAsync, WaitAsync, PollAsync, Getstat,
/// SetPriority, the complete and the deleting destructor), LocalFile 0x30 bytes, MemFile 0x48. The HLE keeps each
/// open file's state beside the object. The asynchronous calls run at once and keep their result for
/// WaitAsync/PollAsync, as libpaf's do once the I/O completes; a second request before that wait is busy (0x80AF5041),
/// a wait with nothing pending 0x80AF5042. Errors of the I/O layer come back as SceIo returns them.
/// </summary>
public sealed partial class Paf
{
    const int FileNoImpl = unchecked((int)0x80AF5003), FileBadArg = unchecked((int)0x80AF5002), FileBusy = unchecked((int)0x80AF5041),
        FileNoAsync = unchecked((int)0x80AF5042), FileNotOpen = unchecked((int)0x80AF5029), FileAlreadyOpen = unchecked((int)0x80AF5028),
        DirBadArg = unchecked((int)0x80AF5901), DirEnd = unchecked((int)0x80AF5902);
    const int TypeLocal = 1, TypeMem = 2;

    sealed class FileState
    {
        public int Fd = -1;
        public bool Opened;
        public int AsyncOp; // 0 none, else the pending call's kind
        public long AsyncResult;
        // memory files
        public uint Addr, Size;
        public long Pos;
        public bool Writable;
        public uint BufferSp; // the SharedPtr<Buffer const> kept inside the object
    }

    readonly ConcurrentDictionary<uint, FileState> _files = new();
    IoFileMgr? _io;
    IoFileMgr Io => _io ??= Emu.Hle.Modules.OfType<IoFileMgr>().First();

    uint LocalFileVtable => VTable("LocalFile", 0x7D13C35B, 0x4300E1B0, 0xAF6A9878, 0x499F4FAA, 0x20F863FD, 0x00383A45, 0xF035266A,
        0x74D9B6F4, 0x55E04F37, 0xD184BC69, 0xBD49E2B3, 0x0CD740C5, 0x5A26025A, 0x79277F10, 0x99AAA9F7, 0xCE5848E1, 0x4A447655,
        0x439847D5, 0x01A03F2E, 0x3636127A, 0x5BEF2351, 0x718411BE, 0x932FCF17, 0xF42CA10F);

    uint MemFileVtable => VTable("MemFile", 0x4BB2B084, 0xA6B33A00, 0x6784C895, 0xC4875233, 0xF2A15807, 0x4E981745, 0x807CCDB4,
        0x5C9FFE3E, 0xB456B41B, 0xE3F118A8, 0x871AFA61, 0x44FE3320, 0xA7756292, 0x494E8CF8, 0xEF7C46F1, 0x7BED7D7F, 0x4FBD37E4,
        0x964C67E5, 0x4B8F8811, 0x564A5CA7, HostSlot("MemFile.Getstat", c => c.Return(MemGetstat(c.Gpr(0), c.Gpr(1)))),
        HostSlot("MemFile.SetPriority", c => c.Return(0)), 0x8AF3CC45, 0x71148CB2);

    // ---- LocalFile
    [Hle("_ZN3paf9LocalFileC1Ev")]
    [Hle("_ZN3paf9LocalFileC2Ev")]
    uint LfCtor(uint me) => LfCtorA(me, 0);

    [Hle("_ZN3paf9LocalFileC1EPNS_6memory13HeapAllocatorE")]
    [Hle("_ZN3paf9LocalFileC2EPNS_6memory13HeapAllocatorE")]
    uint LfCtorA(uint me, uint allocator)
    {
        GuestMemory.Fill(me + 4, 0, 0x2C);
        U(me, LocalFileVtable);
        U(me + 0x28, me); // the implementation object libpaf keeps here: the HLE's state is keyed by the file itself
        U(me + 0x2C, allocator);
        _files[me] = new FileState();
        return me;
    }

    [Hle("_ZN3paf9LocalFile10CreateImplEv")] void LfCreateImpl(uint me) { _files.TryAdd(me, new FileState()); U(me + 0x28, me); }
    [Hle("_ZN3paf9LocalFile10DeleteImplEv")] void LfDeleteImpl(uint me) { if (_files.TryRemove(me, out var s) && s.Opened) Io.sceIoClose(s.Fd); U(me + 0x28, 0); }

    [Hle("_ZN3paf9LocalFileD1Ev")]
    [Hle("_ZN3paf9LocalFileD2Ev")]
    uint LfDtor(uint me) { LfDeleteImpl(me); return me; }

    [Hle("_ZN3paf9LocalFileD0Ev")]
    void LfDelete(uint me) { LfDtor(me); Free(me); }

    FileState? Lf(uint me) => _files.TryGetValue(me, out var s) ? s : null;

    /// <summary>(OpenArg*): { type, filename, flag, mode }; the type must be the file's (1).</summary>
    [Hle("_ZN3paf9LocalFile4OpenEPKNS_4File7OpenArgE")]
    int LfOpen(uint me, uint arg)
    {
        if (arg == 0 || U(arg) != TypeLocal) return FileBadArg;
        if (Lf(me) is not { } s) return FileNoImpl;
        if (s.AsyncOp != 0) return FileBusy;
        int fd = Io.sceIoOpen(CRead(U(arg + 4)), (int)U(arg + 8), (int)U(arg + 0xC));
        if (fd < 0) return fd;
        s.Fd = fd; s.Opened = true;
        return 0;
    }

    [Hle("_ZN3paf9LocalFile9OpenAsyncEPKNS_4File7OpenArgE")]
    int LfOpenAsync(uint me, uint arg)
    {
        if (arg == 0 || U(arg) != TypeLocal) return FileBadArg;
        if (Lf(me) is not { } s) return FileNoImpl;
        if (s.Opened) return FileAlreadyOpen;
        if (s.AsyncOp != 0) return FileBusy;
        int r = LfOpen(me, arg);
        s.AsyncOp = 1; s.AsyncResult = r;
        return 0;
    }

    [Hle("_ZN3paf9LocalFile5CloseEv")]
    int LfClose(uint me)
    {
        if (Lf(me) is not { } s) return FileNoImpl;
        if (!s.Opened) return FileNotOpen;
        if (s.AsyncOp != 0) return FileBusy;
        int r = Io.sceIoClose(s.Fd);
        if (r < 0) return r;
        s.Fd = -1; s.Opened = false;
        return 0;
    }

    [Hle("_ZN3paf9LocalFile10CloseAsyncEv")] int LfCloseAsync(uint me) => Async(me, 2, () => LfClose(me));

    int Async(uint me, int kind, Func<long> op)
    {
        if (Lf(me) is not { } s) return FileNoImpl;
        if (s.AsyncOp != 0) return FileBusy;
        long r = op();
        s.AsyncOp = kind; s.AsyncResult = r;
        return 0;
    }

    [Hle("_ZNK3paf9LocalFile8IsOpenedEv")] bool LfIsOpened(uint me) => Lf(me) is { Opened: true };

    [Hle("_ZN3paf9LocalFile5IsEofEv")]
    bool LfIsEof(uint me)
    {
        if (Lf(me) is not { Opened: true } s) return false;
        long cur = Io.sceIoLseek(s.Fd, 0, 1), end = Io.sceIoLseek(s.Fd, 0, 2);
        Io.sceIoLseek(s.Fd, cur, 0);
        return cur == end;
    }

    /// <summary>Cancels a pending request; the HLE's requests are already done, so there is nothing left to stop.</summary>
    [Hle("_ZN3paf9LocalFile5AbortEv")] int LfAbort(uint me) => Lf(me) is not { } s ? FileNoImpl : s.AsyncOp == 0 ? FileNoAsync : 0;

    /// <summary>Reads until the count is met or the file ends (several SceIo reads when needed).</summary>
    [Hle("_ZN3paf9LocalFile4ReadEPvj")]
    int LfRead(uint me, uint buf, uint n)
    {
        if (Lf(me) is not { } s) return FileNoImpl;
        if (s.AsyncOp != 0) return FileBusy;
        int total = 0;
        while (n > 0)
        {
            int r = Io.sceIoRead(s.Fd, buf + (uint)total, n);
            if (r < 0) return r;
            if (r == 0) break;
            total += r; n -= (uint)r;
        }
        return total;
    }

    [Hle("_ZN3paf9LocalFile5WriteEPKvj")]
    int LfWrite(uint me, uint buf, uint n)
    {
        if (Lf(me) is not { } s) return FileNoImpl;
        if (s.AsyncOp != 0) return FileBusy;
        int total = 0;
        while (n > 0)
        {
            int r = Io.sceIoWrite(s.Fd, buf + (uint)total, n);
            if (r < 0) return r;
            if (r == 0) break;
            total += r; n -= (uint)r;
        }
        return total;
    }

    [Hle("_ZN3paf9LocalFile9ReadAsyncEPvj")] int LfReadAsync(uint me, uint buf, uint n) => Async(me, 3, () => Io.sceIoRead(Lf(me)!.Fd, buf, n));
    [Hle("_ZN3paf9LocalFile10WriteAsyncEPKvj")] int LfWriteAsync(uint me, uint buf, uint n) => Async(me, 4, () => Io.sceIoWrite(Lf(me)!.Fd, buf, n));

    [Hle("_ZN3paf9LocalFile4SeekExi")]
    long LfSeek(uint me, long off, int whence)
    {
        if (Lf(me) is not { } s) return FileNoImpl;
        if (s.AsyncOp != 0) return FileBusy;
        return Io.sceIoLseek(s.Fd, off, whence);
    }

    [Hle("_ZN3paf9LocalFile9SeekAsyncExi")] int LfSeekAsync(uint me, long off, int whence) => Async(me, 5, () => Io.sceIoLseek(Lf(me)!.Fd, off, whence));

    [Hle("_ZN3paf9LocalFile5FlushEv")]
    int LfFlush(uint me) => Lf(me) is not { } s ? FileNoImpl : s.AsyncOp != 0 ? FileBusy : Math.Min(0, Io.sceIoSyncByFd(s.Fd, 0));

    [Hle("_ZN3paf9LocalFile10FlushAsyncEv")] int LfFlushAsync(uint me) => Async(me, 6, () => Io.sceIoSyncByFd(Lf(me)!.Fd, 0));

    /// <summary>(int64 *result): the finished request's result; an open sets the file open, a close closed.</summary>
    [Hle("_ZN3paf9LocalFile9WaitAsyncEPx")]
    [Hle("_ZN3paf9LocalFile9PollAsyncEPx")]
    int LfWaitAsync(uint me, uint result)
    {
        if (Lf(me) is not { } s) return FileNoImpl;
        if (s.AsyncOp == 0) return FileNoAsync;
        if (result != 0) Q(result, (ulong)s.AsyncResult);
        s.AsyncOp = 0;
        return 0;
    }

    [Hle("_ZN3paf9LocalFile11SetPriorityEi")] int LfSetPriority(uint me, int prio) => Lf(me) is null ? FileNoImpl : 0;

    [Hle("_ZNK3paf9LocalFile11GetFileSizeEv")]
    long LfGetSize(uint me)
    {
        if (Lf(me) is not { } s) return FileNoImpl;
        uint st = Scratch(0x60);
        int r = Io.sceIoGetstatByFd(s.Fd, st);
        return r < 0 ? r : (long)Q(st + 8);
    }

    /// <summary>paf::FileStat: mode +0, attribute +4, size +8 and the three dates (as paf DateTime, 0x18 each) from +0x10.</summary>
    void FileStatFromIo(uint dst, uint st)
    {
        GuestMemory.Fill(dst, 0, 0x58);
        H(dst, (ushort)U(st)); U(dst + 4, U(st + 4)); Q(dst + 8, Q(st + 8));
        for (uint i = 0; i < 3; i++) GuestMemory.Copy(dst + 0x10 + i * 0x18, st + 0x10 + i * 0x10, 0x10);
    }

    void FileStatToIo(uint st, uint src)
    {
        GuestMemory.Fill(st, 0, 0x58);
        U(st, H(src)); U(st + 4, U(src + 4)); Q(st + 8, Q(src + 8));
        for (uint i = 0; i < 3; i++) GuestMemory.Copy(st + 0x10 + i * 0x10, src + 0x10 + i * 0x18, 0x10);
    }

    [Hle("_ZN3paf9LocalFile7GetstatEPNS_8FileStatE")]
    int LfGetstatFd(uint me, uint stat)
    {
        if (stat == 0) return FileBadArg;
        if (Lf(me) is not { } s) return FileNoImpl;
        uint st = Scratch(0x60);
        int r = Io.sceIoGetstatByFd(s.Fd, st);
        if (r == 0) FileStatFromIo(stat, st);
        return r;
    }

    [Hle("_ZN3paf9LocalFile7GetstatEPKcPNS_13LocalFileStatE")]
    int LfGetstat(uint path, uint stat)
    {
        if (path == 0 || stat == 0) return FileBadArg;
        uint st = Scratch(0x60);
        int r = Io.sceIoGetstat(CRead(path), st);
        if (r == 0) FileStatFromIo(stat, st);
        return r;
    }

    [Hle("_ZN3paf9LocalFile6ChstatEPKcPNS_13LocalFileStatEj")]
    int LfChstat(uint path, uint stat, uint bits)
    {
        if (path == 0 || stat == 0) return FileBadArg;
        uint st = Scratch(0x60);
        FileStatToIo(st, stat);
        return Io.sceIoChstat(CRead(path), st, (int)bits);
    }

    [Hle("_ZN3paf9LocalFile10RemoveFileEPKc")] int LfRemove(uint path) => Io.sceIoRemove(CRead(path));
    [Hle("_ZN3paf9LocalFile10RenameFileEPKcS2_")] int LfRename(uint from, uint to) => Io.sceIoRename(CRead(from), CRead(to));

    [Hle("_ZN3paf9LocalFile11GetFileSizeEPKc")]
    long LfFileSize(uint path)
    {
        uint st = Scratch(0x60);
        int r = Io.sceIoGetstat(CRead(path), st);
        return r < 0 ? r : (long)Q(st + 8);
    }

    bool StatMode(uint path, uint mask)
    {
        uint st = Scratch(0x60);
        return path != 0 && Io.sceIoGetstat(CRead(path), st) == 0 && (U(st) & mask) != 0;
    }

    [Hle("_ZN3paf9LocalFile6ExistsEPKc")] bool LfExists(uint path) => StatMode(path, 0x3000);
    [Hle("_ZN3paf9LocalFile11IsDirectoryEPKc")] bool LfIsDir(uint path) => StatMode(path, 0x1000);
    [Hle("_ZN3paf9LocalFile6IsFileEPKc")] bool LfIsFile(uint path) => StatMode(path, 0x2000);

    /// <summary>(SharedPtr&lt;LocalFile&gt; &ret, path, flag, mode, int *error): a new open file, or an empty pointer and
    /// the error.</summary>
    [Hle("_ZN3paf9LocalFile4OpenEPKcjjPi")]
    void LfOpenStatic(uint ret, uint path, uint flag, uint mode, uint err)
    {
        uint obj = Alloc(0x30);
        int r = FileNoImpl;
        if (obj != 0)
        {
            LfCtor(obj);
            uint arg = Scratch(0) + 0xD0;
            U(arg, TypeLocal); U(arg + 4, path); U(arg + 8, flag); U(arg + 0xC, mode);
            r = LfOpen(obj, arg);
            if (r < 0) { LfDtor(obj); Free(obj); obj = 0; }
        }
        if (err != 0) U(err, (uint)Math.Min(r, 0));
        SharedPtrNew(ret, obj, 23);
    }

    /// <summary>The local form of a URL open: only when the caller's "ignored" flag is given and clear.</summary>
    [Hle("_ZN3paf9LocalFile7OpenUrlEPKcjjPiPb")]
    void LfOpenUrl(uint ret, uint path, uint flag, uint mode, uint err, uint ignored)
    {
        if (ignored == 0 || B(ignored) != 0) { U(ret, 0); U(ret + 4, 0); return; }
        LfOpenStatic(ret, path, flag, mode, err);
    }

    // ---- MemFile: reads (and writes, when writable) a block of memory
    [Hle("_ZN3paf7MemFileC1Ev")]
    [Hle("_ZN3paf7MemFileC2Ev")]
    uint MfCtor(uint me)
    {
        GuestMemory.Fill(me + 4, 0, 0x44);
        U(me, MemFileVtable);
        _files[me] = new FileState { BufferSp = me + 8 };
        return me;
    }

    [Hle("_ZN3paf7MemFileD1Ev")]
    [Hle("_ZN3paf7MemFileD2Ev")]
    uint MfDtor(uint me) { if (Lf(me) is { } s) { SharedPtrRelease(s.BufferSp); _files.TryRemove(me, out _); } return me; }

    [Hle("_ZN3paf7MemFileD0Ev")] void MfDelete(uint me) { MfDtor(me); Free(me); }

    /// <summary>(OpenArg*): { type 2, SharedPtr&lt;Buffer const&gt; }: the buffer's address (+4) and size (+8).</summary>
    [Hle("_ZN3paf7MemFile4OpenEPKNS_4File7OpenArgE")]
    int MfOpen(uint me, uint arg)
    {
        if (arg == 0 || U(arg) != TypeMem) return FileBadArg;
        if (Lf(me) is not { } s) return FileNoImpl;
        uint buf = U(arg + 4);
        if (buf == 0) return FileBadArg;
        // keep a reference to the buffer
        U(s.BufferSp, buf); U(s.BufferSp + 4, U(arg + 8));
        if (U(arg + 8) != 0) U(U(arg + 8) + 4, U(U(arg + 8) + 4) + 1);
        s.Addr = U(buf + 4); s.Size = U(buf + 8); s.Pos = 0; s.Opened = true; s.Writable = false;
        return 0;
    }

    [Hle("_ZN3paf7MemFile9OpenAsyncEPKNS_4File7OpenArgE")] int MfOpenAsync(uint me, uint arg) => MfAsync(me, 1, () => MfOpen(me, arg));

    int MfAsync(uint me, int kind, Func<long> op)
    {
        if (Lf(me) is not { } s) return FileNoImpl;
        if (s.AsyncOp != 0) return FileBusy;
        s.AsyncResult = op(); s.AsyncOp = kind;
        return 0;
    }

    /// <summary>(SharedPtr&lt;MemFile&gt; &ret, const void *addr, size, int *error): a file over memory the caller owns.</summary>
    [Hle("_ZN3paf7MemFile4OpenEPKvjPi")]
    void MfOpenMem(uint ret, uint addr, uint size, uint err)
    {
        uint obj = Alloc(0x48);
        if (obj == 0) { if (err != 0) U(err, unchecked((uint)FileNoImpl)); U(ret, 0); U(ret + 4, 0); return; }
        MfCtor(obj);
        var s = Lf(obj)!;
        s.Addr = addr; s.Size = size; s.Opened = true;
        if (err != 0) U(err, 0);
        SharedPtrNew(ret, obj, 23);
    }

    /// <summary>(SharedPtr&lt;MemFile&gt; &ret, SharedPtr&lt;Buffer const&gt; buffer, int *error).</summary>
    [Hle("_ZN3paf7MemFile4OpenENS_6common9SharedPtrIKNS_6BufferEEEPi")]
    void MfOpenBuffer(uint ret, uint bufSp, uint err)
    {
        uint obj = Alloc(0x48);
        if (obj == 0) { if (err != 0) U(err, unchecked((uint)FileNoImpl)); U(ret, 0); U(ret + 4, 0); return; }
        MfCtor(obj);
        uint arg = Scratch(0) + 0xD0;
        U(arg, TypeMem); U(arg + 4, U(bufSp)); U(arg + 8, U(bufSp + 4));
        int r = MfOpen(obj, arg);
        if (r < 0) { MfDelete(obj); obj = 0; }
        if (err != 0) U(err, (uint)Math.Min(r, 0));
        SharedPtrNew(ret, obj, 23);
    }

    [Hle("_ZN3paf7MemFile5CloseEv")]
    int MfClose(uint me)
    {
        if (Lf(me) is not { } s) return FileNoImpl;
        if (!s.Opened) return FileNotOpen;
        SharedPtrRelease(s.BufferSp);
        s.Opened = false; s.Addr = s.Size = 0; s.Pos = 0;
        return 0;
    }

    [Hle("_ZN3paf7MemFile10CloseAsyncEv")] int MfCloseAsync(uint me) => MfAsync(me, 2, () => MfClose(me));
    [Hle("_ZNK3paf7MemFile8IsOpenedEv")] bool MfIsOpened(uint me) => Lf(me) is { Opened: true };
    [Hle("_ZN3paf7MemFile5IsEofEv")] bool MfIsEof(uint me) => Lf(me) is { Opened: true } s && s.Pos >= s.Size;
    [Hle("_ZN3paf7MemFile5AbortEv")] int MfAbort(uint me) => Lf(me) is not { } s ? FileNoImpl : s.AsyncOp == 0 ? FileNoAsync : 0;
    [Hle("_ZNK3paf7MemFile11GetFileSizeEv")] long MfSize(uint me) => Lf(me) is { Opened: true } s ? s.Size : FileNotOpen;

    [Hle("_ZN3paf7MemFile4ReadEPvj")]
    int MfRead(uint me, uint buf, uint n)
    {
        if (Lf(me) is not { Opened: true } s) return FileNotOpen;
        long left = Math.Max(0, s.Size - s.Pos);
        uint k = (uint)Math.Min(left, n);
        if (k > 0) GuestMemory.Copy(buf, s.Addr + (uint)s.Pos, k);
        s.Pos += k;
        return (int)k;
    }

    /// <summary>A memory file opened over a const buffer is read-only (0x80AF5004 like libpaf's capability check).</summary>
    [Hle("_ZN3paf7MemFile5WriteEPKvj")]
    int MfWrite(uint me, uint buf, uint n)
    {
        if (Lf(me) is not { Opened: true } s) return FileNotOpen;
        if (!s.Writable) return unchecked((int)0x80AF5004);
        long left = Math.Max(0, s.Size - s.Pos);
        uint k = (uint)Math.Min(left, n);
        if (k > 0) GuestMemory.Copy(s.Addr + (uint)s.Pos, buf, k);
        s.Pos += k;
        return (int)k;
    }

    [Hle("_ZN3paf7MemFile9ReadAsyncEPvj")] int MfReadAsync(uint me, uint buf, uint n) => MfAsync(me, 3, () => MfRead(me, buf, n));
    [Hle("_ZN3paf7MemFile10WriteAsyncEPKvj")] int MfWriteAsync(uint me, uint buf, uint n) => MfAsync(me, 4, () => MfWrite(me, buf, n));

    /// <summary>Seeks inside the block (0 set, 1 current, 2 end); a position outside it is refused (0x80AF5002).</summary>
    [Hle("_ZN3paf7MemFile4SeekExi")]
    long MfSeek(uint me, long off, int whence)
    {
        if (Lf(me) is not { Opened: true } s) return FileNotOpen;
        long p = whence switch { 0 => off, 1 => s.Pos + off, 2 => s.Size + off, _ => -1 };
        if (p < 0 || p > s.Size) return FileBadArg;
        return s.Pos = p;
    }

    [Hle("_ZN3paf7MemFile9SeekAsyncExi")] int MfSeekAsync(uint me, long off, int whence) => MfAsync(me, 5, () => MfSeek(me, off, whence));
    [Hle("_ZN3paf7MemFile5FlushEv")] int MfFlush(uint me) => Lf(me) is null ? FileNoImpl : 0;
    [Hle("_ZN3paf7MemFile10FlushAsyncEv")] int MfFlushAsync(uint me) => MfAsync(me, 6, () => 0);
    [Hle("_ZN3paf7MemFile9WaitAsyncEPx")]
    [Hle("_ZN3paf7MemFile9PollAsyncEPx")]
    int MfWaitAsync(uint me, uint result) => LfWaitAsync(me, result);

    /// <summary>A memory file's status: a regular file of the block's size, no dates.</summary>
    int MemGetstat(uint me, uint stat)
    {
        if (stat == 0) return FileBadArg;
        if (Lf(me) is not { Opened: true } s) return FileNotOpen;
        GuestMemory.Fill(stat, 0, 0x58);
        H(stat, 0x2000 | 0x124);
        Q(stat + 8, s.Size);
        return 0;
    }

    // ---- Dir: { SceUID handle } (-1 when closed)
    [Hle("_ZN3paf3DirC1Ev")]
    [Hle("_ZN3paf3DirC2Ev")]
    uint DirCtor(uint me) { U(me, 0xFFFFFFFF); return me; }

    [Hle("_ZN3paf3Dir4OpenEPKc")]
    int DirOpen(uint me, uint path)
    {
        int fd = Io.sceIoDopen(CRead(path));
        if (fd < 0) return fd;
        U(me, (uint)fd);
        return 0;
    }

    [Hle("_ZN3paf3Dir5CloseEv")]
    int DirClose(uint me)
    {
        if ((int)U(me) != -1) Io.sceIoDclose((int)U(me));
        U(me, 0xFFFFFFFF);
        return 0;
    }

    /// <summary>(DirEnt*): { type (0 dir, 1 regular, 2 link mode, 3 other), string name +4, int size +0x10, int
    /// creation year +0x14 }; the end of the listing is 0x80AF5902.</summary>
    [Hle("_ZN3paf3Dir4ReadEPNS_6DirEntE")]
    int DirRead(uint me, uint ent)
    {
        if (ent == 0 || (int)U(me) == -1) return DirBadArg;
        uint d = Alloc(0x168);
        try
        {
            GuestMemory.Fill(d, 0, 0x168);
            int r = Io.sceIoDread((int)U(me), d);
            if (r < 0) return r;
            if (r == 0) return DirEnd;
            uint mode = U(d);
            U(ent, (mode & 0x1000) != 0 ? 0u : (mode & 0x2000) != 0 ? 1u : (mode & 0x4000) != 0 ? 2u : 3u);
            StrAssign(ent + 4, GuestMemory.ReadCString(d + 0x58, 256));
            U(ent + 0x10, (uint)Q(d + 8));
            U(ent + 0x14, H(d + 0x10));
            return 0;
        }
        finally { Free(d); }
    }

    [Hle("_ZN3paf3Dir6CreateEPKcj")] int DirCreateMode(uint path, uint mode) => Io.sceIoMkdir(CRead(path), (int)mode);
    /// <summary>The default mode is 6 (read and write for the system).</summary>
    [Hle("_ZN3paf3Dir6CreateEPKc")] int DirCreate(uint path) => Io.sceIoMkdir(CRead(path), 6);

    /// <summary>Makes each parent of the path (each prefix ending before a '/', after the first character), then the
    /// path; the result is the last mkdir's.</summary>
    [Hle("_ZN3paf3Dir15CreateRecursiveEPKcj")]
    int DirCreateRecursiveMode(uint path, uint mode)
    {
        if (path == 0 || B(path) == 0) return 0;
        string p = CRead(path);
        for (int i = p.IndexOf('/', 1); i > 0; i = p.IndexOf('/', i + 1)) Io.sceIoMkdir(p[..i], (int)mode);
        return Io.sceIoMkdir(p, (int)mode);
    }

    [Hle("_ZN3paf3Dir15CreateRecursiveEPKc")] int DirCreateRecursive(uint path) => DirCreateRecursiveMode(path, 6);
    [Hle("_ZN3paf3Dir11IsDirectoryEPKc")] bool DirIsDir(uint path) => StatMode(path, 0x1000);
    [Hle("_ZN3paf3Dir7IsExistEPKc", Nid = 0x95F14046)] bool DirIsExist(uint path) { uint st = Scratch(0x60); return Io.sceIoGetstat(CRead(path), st) == 0; }

    /// <summary>Removes a file or an empty directory; a path that does not exist is already removed (0).</summary>
    [Hle("_ZN3paf3Dir6RemoveEPKc")]
    int DirRemove(uint path)
    {
        if (path == 0) return DirBadArg;
        string p = CRead(path);
        uint st = Scratch(0x60);
        if (Io.sceIoGetstat(p, st) != 0) return 0;
        return (U(st) & 0x1000) != 0 ? Io.sceIoRmdir(p) : Io.sceIoRemove(p);
    }

    /// <summary>Removes the path and, for a directory, everything under it.</summary>
    [Hle("_ZN3paf3Dir15RemoveRecursiveEPKc")]
    int DirRemoveRecursive(uint path) => path == 0 ? DirBadArg : RemoveTree(CRead(path));

    int RemoveTree(string p)
    {
        uint st = Scratch(0x60);
        if (Io.sceIoGetstat(p, st) != 0) return 0;
        if ((U(st) & 0x1000) == 0) return Io.sceIoRemove(p);
        int fd = Io.sceIoDopen(p);
        if (fd < 0) return fd;
        var names = new List<string>();
        uint d = Alloc(0x168);
        try
        {
            while (true)
            {
                GuestMemory.Fill(d, 0, 0x168);
                if (Io.sceIoDread(fd, d) <= 0) break;
                string n = GuestMemory.ReadCString(d + 0x58, 256);
                if (n is not "." and not "..") names.Add(n);
            }
        }
        finally { Io.sceIoDclose(fd); Free(d); }
        foreach (var n in names) { int r = RemoveTree(p.TrimEnd('/') + "/" + n); if (r < 0) return r; }
        return Io.sceIoRmdir(p);
    }
}
