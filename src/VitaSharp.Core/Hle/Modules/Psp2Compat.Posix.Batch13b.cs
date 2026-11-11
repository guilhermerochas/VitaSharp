using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// ScePsp2Compat's POSIX layer (WebKit's open/read/write/lseek/unlink/mkdir/rmdir, directories, sockets, pipes over
/// message pipes, pthread keys, mutex lock/trylock, pthread_once, the thread's stack, mmap over memory blocks,
/// gmtime_r/localtime_r, the environment table, the C runtime's heap figures), written from what each export does
/// in the 3.74 module: the flag and type translations, the error convention (-1, or the errno value for the pthread
/// calls: EBUSY 0x10, EINVAL 0x16, EAGAIN 0xB, ENOMEM 0xC, ENOENT 2) and the kernel call each one makes, which here
/// is the HLE's own (the same NIDs the module imports).
/// </summary>
public sealed unsafe partial class Psp2Compat
{
    // the kernel and runtime functions the module calls (its import NIDs)
    const uint IoOpen = 0x6C60AC61, IoClose = 0xC70B8886, IoRead = 0xFDB32293, IoWrite = 0x34EFD876, IoPread = 0x52315AD7,
        IoLseek = 0x99BA173E, IoRemove = 0xE20ED0F3, IoMkdir = 0x9670D39F, IoRmdir = 0xE9F91EC8, IoDopen = 0xA9283DD0,
        IoDread = 0x9C8B6624, IoDclose = 0x422A221A, IoGetstat = 0xBCA5B623, IoChstatByFd = 0x6E903AB2;
    const uint NetSocket = 0xF084FCE3, NetSocketClose = 0x29822B4D, NetSetsockopt = 0x065505CA, NetShutdown = 0x69E50BB5,
        NetAccept = 0x1ADF9BB1, NetBind = 0x1296A94B, NetListen = 0x7A8DA094, NetRecv = 0x023643B7;
    const uint LockLwMutex = 0x46E7BE7B, TryLockLwMutex = 0xA6A2C915, GetTlsAddr = 0xB295EB61, GetThreadInfo = 0x8D9C5461,
        DelayThread = 0x4B675D05, CreateMsgPipe = 0x0A10C1C8, DeleteMsgPipe = 0xE78BCCF7, SendMsgPipe = 0x0CA71EA2,
        ReceiveMsgPipe = 0x4E81DD5C, SetEvent = 0x324218CD, ProcTimeWideNid = 0xB110C123;
    const uint AllocMemBlock = 0xB9D5EBDE, AllocMemBlockForVm = 0xE2D7E137, GetMemBlockBase = 0xB8EF5818,
        FindMemBlockByAddr = 0xA33B99D1, FreeMemBlock = 0xA91E15EE;
    const uint LibcMalloc = 0x775A0CB2, LibcFree = 0x5B9BB802, LibcAbort = 0x20FE0FFF, LibcGmtime = 0xF283CFE3,
        LibcLocaltime = 0xF4A2E0BF, LibcMallocStatsFast = 0xB3D29DE1, CxxNew = 0xF99ED5AC, RtcGetTimeT = 0x8DE6FEB7,
        RtcDateTimeToTime64 = 0xC995DE02;
    const int EBUSY = 0x10, EINVAL = 0x16, EAGAIN = 0xB, ENOMEM = 0xC, ENOENT = 2;

    /// <summary>Calls the HLE's function <paramref name="nid"/> with <paramref name="args"/> (r0..r3, then the stack) and
    /// gives back r0 | r1 &lt;&lt; 32; the caller's registers and stack pointer are put back.</summary>
    static ulong Sys(HleContext c, uint nid, params ReadOnlySpan<uint> args)
    {
        var cpu = c.Cpu;
        uint sp0 = cpu->R[13];
        uint r0 = cpu->R[0], r1 = cpu->R[1], r2 = cpu->R[2], r3 = cpu->R[3];
        if (args.Length > 4)
        {
            uint sp = (sp0 - 0x40 - (uint)(args.Length - 4) * 4) & ~7u;
            for (int i = 4; i < args.Length; i++) GuestMemory.W32(sp + (uint)(i - 4) * 4, args[i]);
            cpu->R[13] = sp;
        }
        for (int i = 0; i < args.Length && i < 4; i++) cpu->R[i] = args[i];
        if (c.Emu.Hle.ImplOf(nid) is { } f) f(c);
        else cpu->R[0] = unchecked((uint)SceError.ERROR);
        ulong r = cpu->R[0] | (ulong)cpu->R[1] << 32;
        cpu->R[13] = sp0;
        cpu->R[0] = r0; cpu->R[1] = r1; cpu->R[2] = r2; cpu->R[3] = r3;
        return r;
    }

    static int SysI(HleContext c, uint nid, params ReadOnlySpan<uint> args) => (int)(uint)Sys(c, nid, args);

    /// <summary>Scratch words on the guest stack below the caller's frame (for out-parameters of the calls).</summary>
    static uint Scratch(HleContext c, uint size)
    {
        uint p = (c.Cpu->R[13] - 0x200 - size) & ~15u;
        GuestMemory.Fill(p, 0, size);
        return p;
    }

    static int MinusOneIfNonZero(int r) => r != 0 ? -1 : 0;
    static int MinusOneIfNegative(int r) => r < 0 ? -1 : r;

    // ------------------------------------------------------------------ files

    /// <summary>open(path, flags): O_WRONLY/O_RDWR/O_APPEND/O_CREAT/O_TRUNC/O_EXCL (1, 2, 0x400, 0x40, 0x200, 0x80) to the
    /// SCE_O flags, mode 0600; -1 on error.</summary>
    [Hle("NID_5BCFDB76", Nid = 0x5BCFDB76, Library = Lib)]
    int PosixOpen(HleContext c, uint path, uint flags)
    {
        uint f = (flags & 3) switch { 0 => 1u, 1 => 2u, 2 => 3u, _ => flags };
        if ((flags & 0x400) != 0) f |= 0x100;
        if ((flags & 0x40) != 0) f |= 0x200;
        if ((flags & 0x200) != 0) f |= 0x400;
        if ((flags & 0x80) != 0) f |= 0x800;
        return MinusOneIfNegative(SysI(c, IoOpen, path, f, 0x180));
    }

    /// <summary>close(fd): a file, else a socket, else a pipe (message pipe); -1 when none of them closes it.</summary>
    [Hle("NID_49D31125", Nid = 0x49D31125, Library = Lib)]
    int PosixClose(HleContext c, int fd)
    {
        if (SysI(c, IoClose, (uint)fd) == 0) return 0;
        if (SysI(c, NetSocketClose, (uint)fd) >= 0) return 0;
        return MinusOneIfNonZero(SysI(c, DeleteMsgPipe, (uint)fd));
    }

    /// <summary>read(fd, buf, n): a file, else the pipe's message pipe (waiting for data); -1 on error.</summary>
    [Hle("NID_8F67A7FF", Nid = 0x8F67A7FF, Library = Lib)]
    int PosixRead(HleContext c, int fd, uint buf, uint n) => FileOrPipe(c, IoRead, ReceiveMsgPipe, fd, buf, n);

    /// <summary>write(fd, buf, n): a file, else the pipe's message pipe; -1 on error.</summary>
    [Hle("NID_FB129366", Nid = 0xFB129366, Library = Lib)]
    int PosixWrite(HleContext c, int fd, uint buf, uint n) => FileOrPipe(c, IoWrite, SendMsgPipe, fd, buf, n);

    static int FileOrPipe(HleContext c, uint io, uint pipe, int fd, uint buf, uint n)
    {
        int r = SysI(c, io, (uint)fd, buf, n);
        if (r >= 0) return r;
        uint result = Scratch(c, 8);
        GuestMemory.W32(result, (uint)r);
        if (SysI(c, pipe, (uint)fd, buf, n, 0, result, 0) != 0) return -1;
        return (int)GuestMemory.R32(result);
    }

    /// <summary>lseek(fd, offset, whence): sceIoLseek; -1 (64-bit) on error.</summary>
    [Hle("NID_684DBF22", Nid = 0x684DBF22, Library = Lib)]
    long PosixLseek(HleContext c)
    {
        long r = (long)Sys(c, IoLseek, c.Gpr(0), c.Gpr(1), c.Gpr(2), c.Gpr(3), c.Gpr(4));
        return r < 0 ? -1 : r;
    }

    /// <summary>ftruncate(fd, length): the size through sceIoChstatByFd (SCE_CST_SIZE); -1 for a negative length or an
    /// error.</summary>
    [Hle("NID_0C46FC96", Nid = 0x0C46FC96, Library = Lib)]
    int PosixFtruncate(HleContext c, int fd, uint unused, uint lenLo, int lenHi)
    {
        if (lenHi < 0) return -1;
        uint st = Scratch(c, 0x58);
        GuestMemory.W32(st + 8, lenLo);
        GuestMemory.W32(st + 12, (uint)lenHi);
        return MinusOneIfNonZero(SysI(c, IoChstatByFd, (uint)fd, st, 4));
    }

    [Hle("NID_3358CDD4", Nid = 0x3358CDD4, Library = Lib)] int PosixUnlink(HleContext c, uint path) => MinusOneIfNonZero(SysI(c, IoRemove, path));
    [Hle("NID_7DC5F0A1", Nid = 0x7DC5F0A1, Library = Lib)] int PosixRmdir(HleContext c, uint path) => MinusOneIfNonZero(SysI(c, IoRmdir, path));
    /// <summary>mkdir(path): mode 0700.</summary>
    [Hle("NID_C8DE4EA7", Nid = 0xC8DE4EA7, Library = Lib)] int PosixMkdir(HleContext c, uint path) => MinusOneIfNonZero(SysI(c, IoMkdir, path, 0x1C0));

    /// <summary>(path, { int64 size; time_t mtime } *out): the size and modification time of a file; -1 on error.</summary>
    [Hle("NID_9A44F009", Nid = 0x9A44F009, Library = Lib)]
    int PosixStat(HleContext c, uint path, uint outp)
    {
        uint st = Scratch(c, 0x60);
        if (SysI(c, IoGetstat, path, st) != 0) return -1;
        GuestMemory.W32(outp, GuestMemory.R32(st + 8));
        GuestMemory.W32(outp + 4, GuestMemory.R32(st + 12));
        Sys(c, RtcGetTimeT, st + 0x30, outp + 8);
        return 0;
    }

    // the module's FILE-like object { SceUID fd; uint32 offset; uint32 size }

    /// <summary>(F *f): closes the descriptor and resets the object; always 0.</summary>
    [Hle("NID_A8590908", Nid = 0xA8590908, Library = Lib)]
    int FileClose(HleContext c, uint f)
    {
        if (f == 0) return 0;
        if (GuestMemory.R32(f) != uint.MaxValue) Sys(c, IoClose, GuestMemory.R32(f));
        GuestMemory.W32(f, uint.MaxValue);
        GuestMemory.W32(f + 4, 0);
        GuestMemory.W32(f + 8, 0);
        return 0;
    }

    /// <summary>(buf, size, F *f): sceIoPread at the object's offset; 0 without an open descriptor.</summary>
    [Hle("NID_EE2FE924", Nid = 0xEE2FE924, Library = Lib)]
    int FilePread(HleContext c, uint buf, uint size, uint f)
    {
        if (f == 0 || GuestMemory.R32(f) == uint.MaxValue) return 0;
        return SysI(c, IoPread, GuestMemory.R32(f), buf, size, size, GuestMemory.R32(f + 4), 0);
    }

    /// <summary>A new, closed object (operator new(12) = { -1, 0, 0 }).</summary>
    [Hle("NID_0A221CBB", Nid = 0x0A221CBB, Library = Lib)]
    uint FileNew(HleContext c)
    {
        uint f = (uint)Sys(c, CxxNew, 0xC);
        if (f != 0) { GuestMemory.W32(f, uint.MaxValue); GuestMemory.W32(f + 4, 0); GuestMemory.W32(f + 8, 0); }
        return f;
    }

    // ------------------------------------------------------------------ directories

    /// <summary>opendir(path): the directory descriptor, 0 on error.</summary>
    [Hle("NID_5524F04F", Nid = 0x5524F04F, Library = Lib)]
    int PosixOpendir(HleContext c, uint path) { int r = SysI(c, IoDopen, path); return r < 0 ? 0 : r; }

    [Hle("NID_614BC98D", Nid = 0x614BC98D, Library = Lib)]
    int PosixClosedir(HleContext c, int fd) => fd == 0 ? -1 : MinusOneIfNonZero(SysI(c, IoDclose, (uint)fd));

    /// <summary>readdir_r(dir, char name[256], char **result): the next entry's name (SceIoDirent.d_name); -1 at the
    /// end or on error (*result stays NULL).</summary>
    [Hle("NID_9DB5A146", Nid = 0x9DB5A146, Library = Lib)]
    int PosixReaddir(HleContext c, int dir, uint entry, uint result)
    {
        if (result != 0) GuestMemory.W32(result, 0);
        if (dir == 0 || entry == 0) return -1;
        uint d = Scratch(c, 0x160);
        int r = SysI(c, IoDread, (uint)dir, d);
        if (r > 0)
        {
            GuestMemory.Copy(entry, d + 0x58, 0x100);
            GuestMemory.W32(result, entry);
        }
        return r < 1 ? -1 : 0;
    }

    // ------------------------------------------------------------------ sockets

    /// <summary>socket(domain, type, protocol): AF_INET only; SOCK_STREAM/DGRAM/RAW (1..3), protocol kept only for a raw
    /// ICMP socket; -1 otherwise or on error.</summary>
    [Hle("NID_FD31AE18", Nid = 0xFD31AE18, Library = Lib)]
    int PosixSocket(HleContext c, int domain, int type, int protocol)
    {
        if (domain != 2 || type < 1 || type > 3) return -1;
        uint proto = type == 3 && protocol == 3 ? 3u : 0u;
        uint name = Scratch(c, 0x20);
        "SceWebKitPosixSocket\0"u8.CopyTo(GuestMemory.Span(name, 21));
        return MinusOneIfNegative(SysI(c, NetSocket, name, 2, (uint)type, proto));
    }

    [Hle("NID_7842B5FD", Nid = 0x7842B5FD, Library = Lib)] int PosixSocketClose(HleContext c) => PassZero(c, NetSocketClose);
    [Hle("NID_6E5E8340", Nid = 0x6E5E8340, Library = Lib)] int PosixSetsockopt(HleContext c) => PassZero(c, NetSetsockopt);
    [Hle("NID_D2108D91", Nid = 0xD2108D91, Library = Lib)] int PosixShutdown(HleContext c) => PassZero(c, NetShutdown);
    [Hle("NID_504EFE25", Nid = 0x504EFE25, Library = Lib)] int PosixBind(HleContext c) => PassZero(c, NetBind);
    [Hle("NID_3104E01A", Nid = 0x3104E01A, Library = Lib)] int PosixListen(HleContext c) => PassZero(c, NetListen);
    [Hle("NID_016AA482", Nid = 0x016AA482, Library = Lib)] int PosixAccept(HleContext c) => MinusOneIfNegative(Pass(c, NetAccept));
    [Hle("NID_72212B83", Nid = 0x72212B83, Library = Lib)] int PosixRecv(HleContext c) => MinusOneIfNegative(Pass(c, NetRecv));

    /// <summary>The call with the caller's own arguments (r0..r3 and the first stack words).</summary>
    static int Pass(HleContext c, uint nid) => SysI(c, nid, c.Gpr(0), c.Gpr(1), c.Gpr(2), c.Gpr(3), c.Gpr(4), c.Gpr(5));
    static int PassZero(HleContext c, uint nid) => Pass(c, nid) < 0 ? -1 : 0;

    // ------------------------------------------------------------------ pipes

    /// <summary>pipe(int fds[2]): one message pipe "psp2-compat-pipe" (4 KiB, thread-ordered) for both ends.</summary>
    [Hle("NID_88AFBD1A", Nid = 0x88AFBD1A, Library = Lib)]
    int PosixPipe(HleContext c, uint fds)
    {
        uint name = Scratch(c, 0x20);
        "psp2-compat-pipe\0"u8.CopyTo(GuestMemory.Span(name, 17));
        int uid = SysI(c, CreateMsgPipe, name, 0x40, 0x10C, 0x1000, 0);
        if (uid < 0) return -1;
        GuestMemory.W32(fds, (uint)uid);
        GuestMemory.W32(fds + 4, (uint)uid);
        return 0;
    }

    // ------------------------------------------------------------------ threads

    /// <summary>(SceKernelLwMutexWork *m): pthread_mutex_lock; -1 on error.</summary>
    [Hle("NID_0AD3644A", Nid = 0x0AD3644A, Library = Lib)]
    int MutexLock(HleContext c, uint m) => SysI(c, LockLwMutex, m, 1, 0) < 0 ? -1 : 0;

    /// <summary>pthread_mutex_trylock: EBUSY when it is held.</summary>
    [Hle("NID_43644E37", Nid = 0x43644E37, Library = Lib)]
    int MutexTryLock(HleContext c, uint m) => SysI(c, TryLockLwMutex, m, 1) < 0 ? EBUSY : 0;

    /// <summary>(us): sceKernelDelayThread, at least 1 us.</summary>
    [Hle("NID_383C18A6", Nid = 0x383C18A6, Library = Lib)]
    void DelayUs(HleContext c, uint us) => Sys(c, DelayThread, us == 0 ? 1 : us, 0);

    /// <summary>(void **stackaddr, size_t *size): the calling thread's stack from sceKernelGetThreadInfo.</summary>
    [Hle("NID_91CFA4F0", Nid = 0x91CFA4F0, Library = Lib)]
    int ThreadStack(HleContext c, uint addr, uint size)
    {
        uint info = Scratch(c, 0x80);
        GuestMemory.W32(info, 0x80);
        int r = SysI(c, GetThreadInfo, 0, info);
        if (r < 0) return r;
        GuestMemory.W32(addr, GuestMemory.R32(info + 0x34));
        GuestMemory.W32(size, GuestMemory.R32(info + 0x38));
        return 0;
    }

    /// <summary>The process time in seconds (a double).</summary>
    [Hle("NID_AFEE1BCF", Nid = 0xAFEE1BCF, Library = Lib)]
    double ProcessSeconds(HleContext c) => Sys(c, ProcTimeWideNid) / 1000000.0;

    // pthread keys: 64 process-wide slots { used, destructor } under the module's lock; the values live in a 64-word
    // table per thread, kept in the thread's TLS slot 0x42 (allocated with the C runtime's malloc on first use)
    readonly (bool Used, uint Dtor)[] _keys = new (bool, uint)[0x40];
    readonly object _keyLock = new();

    [Hle("NID_266EBD4C", Nid = 0x266EBD4C, Library = Lib)]
    int KeyCreate(uint key, uint dtor)
    {
        lock (_keyLock)
            for (int i = 0; i < _keys.Length; i++)
                if (!_keys[i].Used)
                {
                    _keys[i] = (true, dtor);
                    GuestMemory.W32(key, (uint)i);
                    return 0;
                }
        return EAGAIN;
    }

    [Hle("NID_D13BA225", Nid = 0xD13BA225, Library = Lib)]
    int KeyDelete(uint key)
    {
        if (key >= 0x40) return EINVAL;
        lock (_keyLock)
        {
            if (!_keys[key].Used) return ENOENT;
            _keys[key].Used = false;
        }
        return 0;
    }

    [Hle("NID_88FBFB01", Nid = 0x88FBFB01, Library = Lib)]
    uint KeyGet(HleContext c, uint key)
    {
        if (key >= 0x40) return 0;
        uint slot = (uint)Sys(c, GetTlsAddr, 0x42);
        uint table = GuestMemory.R32(slot);
        return table == 0 ? 0 : GuestMemory.R32(table + key * 4);
    }

    [Hle("NID_408295E9", Nid = 0x408295E9, Library = Lib)]
    int KeySet(HleContext c, uint key, uint value)
    {
        if (key >= 0x40) return EINVAL;
        uint slot = (uint)Sys(c, GetTlsAddr, 0x42);
        uint table = GuestMemory.R32(slot);
        if (table == 0)
        {
            table = (uint)Sys(c, LibcMalloc, 0x100);
            if (table == 0) return ENOMEM;
            GuestMemory.Fill(table, 0, 0x100);
            GuestMemory.W32(slot, table);
        }
        GuestMemory.W32(table + key * 4, value);
        return 0;
    }

    readonly object _onceLock = new();

    /// <summary>pthread_once(int *once, void (*fn)(void)).</summary>
    [Hle("NID_7E5D799F", Nid = 0x7E5D799F, Library = Lib)]
    int Once(uint once, uint fn)
    {
        lock (_onceLock)
        {
            if (GuestMemory.R32(once) == 0)
            {
                GuestMemory.W32(once, 1);
                GuestThread.Current!.CallGuest(fn);
            }
        }
        return 0;
    }

    // ------------------------------------------------------------------ memory blocks (mmap)

    /// <summary>The JIT's executable block (the module's exported variable NID_77EC4625 holds its uid).</summary>
    public int JitBlock { get; private set; } = -1;

    /// <summary>(alignment, size, executable): a read-write block "ManxMemblockRW" (user RW, the alignment or 4 KiB) or
    /// the executable "ManxMemblockRX" for the JIT (sceKernelAllocMemBlockForVM); its base, or NULL.</summary>
    [Hle("NID_925BD038", Nid = 0x925BD038, Library = Lib)]
    uint MapBlock(HleContext c, uint align, uint size, bool exec)
    {
        uint name = Scratch(c, 0x40), opt = name + 0x20, baseOut = name + 0x38;
        int uid;
        if (!exec)
        {
            "ManxMemblockRW\0"u8.CopyTo(GuestMemory.Span(name, 15));
            GuestMemory.W32(opt, 0x14);
            GuestMemory.W32(opt + 4, 4);
            GuestMemory.W32(opt + 8, align != 0 ? align : 0x1000);
            uid = SysI(c, AllocMemBlock, name, 0x0C20D060, size, opt);
        }
        else
        {
            "ManxMemblockRX\0"u8.CopyTo(GuestMemory.Span(name, 15));
            uid = SysI(c, AllocMemBlockForVm, name, size);
            JitBlock = uid;
        }
        if (uid < 0) return 0;
        GuestMemory.W32(baseOut, 0);
        return SysI(c, GetMemBlockBase, (uint)uid, baseOut) < 0 ? 0 : GuestMemory.R32(baseOut);
    }

    /// <summary>(addr): frees the block holding the address; the module aborts when there is none.</summary>
    [Hle("NID_5B6231F9", Nid = 0x5B6231F9, Library = Lib)]
    void UnmapBlock(HleContext c, uint addr)
    {
        int uid = SysI(c, FindMemBlockByAddr, addr, 0);
        if (uid >= 0 && SysI(c, FreeMemBlock, (uint)uid) == 0) return;
        Sys(c, LibcAbort);
    }

    /// <summary>(context): the fatal-error path: the context's two cleanup callbacks (+0xC4, then +0xC0 with 1), then
    /// abort().</summary>
    [Hle("NID_83B22ED9", Nid = 0x83B22ED9, Library = Lib)]
    void FatalAbort(HleContext c, uint ctx)
    {
        if (ctx != 0)
        {
            if (GuestMemory.R32(ctx + 0xC4) is var a and not 0) GuestThread.Current!.CallGuest(a, ctx);
            if (GuestMemory.R32(ctx + 0xC0) is var b and not 0) GuestThread.Current!.CallGuest(b, ctx, 1);
        }
        Sys(c, LibcAbort);
    }

    // ------------------------------------------------------------------ time

    /// <summary>gmtime_r(const time_t *t, struct tm *out) / localtime_r: the C runtime's result copied (9 words).</summary>
    [Hle("NID_5B3E1BF2", Nid = 0x5B3E1BF2, Library = Lib)] uint GmtimeR(HleContext c, uint t, uint tm) => CopyTm(c, LibcGmtime, t, tm);
    [Hle("NID_78050A6E", Nid = 0x78050A6E, Library = Lib)] uint LocaltimeR(HleContext c, uint t, uint tm) => CopyTm(c, LibcLocaltime, t, tm);

    static uint CopyTm(HleContext c, uint fn, uint t, uint tm)
    {
        uint src = (uint)Sys(c, fn, t);
        GuestMemory.Copy(tm, src, 36);
        return tm;
    }

    /// <summary>(const int fields[6] = year, month, day, hour, minute, second): the time64 of that date, 0 when it is not
    /// valid.</summary>
    [Hle("NID_DFBC6A44", Nid = 0xDFBC6A44, Library = Lib)]
    long DateToTime64(HleContext c, uint fields)
    {
        uint dt = Scratch(c, 0x20), outp = dt + 0x10;
        for (uint i = 0; i < 6; i++) GuestMemory.Write(dt + i * 2, (ushort)GuestMemory.R32(fields + i * 4));
        GuestMemory.W32(dt + 12, 0);
        if (SysI(c, RtcDateTimeToTime64, dt, outp) != 0) return 0;
        return GuestMemory.Read<long>(outp);
    }

    // ------------------------------------------------------------------ environment: 128 entries { name[64], value[128] }

    uint _env;
    uint Env => _env != 0 ? _env : _env = AllocEnv();
    uint AllocEnv() { uint p = Emu.Memory.Alloc(0x80 * 0xC0, "psp2compat environment"); GuestMemory.Fill(p, 0, 0x80 * 0xC0); return p; }

    /// <summary>getenv(name): the value, or NULL.</summary>
    [Hle("NID_4F5A3726", Nid = 0x4F5A3726, Library = Lib)]
    uint Getenv(uint name)
    {
        string n = GuestMemory.ReadCString(name);
        for (uint i = 0; i < 0x80; i++)
            if (GuestMemory.ReadCString(Env + i * 0xC0, 0x40) == n) return Env + i * 0xC0 + 0x40;
        return 0;
    }

    /// <summary>setenv(name, value, overwrite): names up to 64 characters and values up to 128; an existing name is
    /// rewritten when overwrite is set, otherwise the first free entry takes it; -1 when it does not fit.</summary>
    [Hle("NID_606C68D1", Nid = 0x606C68D1, Library = Lib)]
    int Setenv(uint name, uint value, int overwrite)
    {
        if (name == 0 || value == 0) return -1;
        string n = GuestMemory.ReadCString(name), v = GuestMemory.ReadCString(value);
        if (n.Length > 0x40 || v.Length > 0x80) return -1;
        for (uint i = 0; i < 0x80; i++)
        {
            uint e = Env + i * 0xC0;
            string cur = GuestMemory.ReadCString(e, 0x40);
            if (cur == n && overwrite != 0) { WriteField(e + 0x40, v, 0x80); return 0; }
            if (cur.Length == 0) { WriteField(e, n, 0x40); WriteField(e + 0x40, v, 0x80); return 0; }
        }
        return -1;
    }

    static void WriteField(uint dst, string s, int size)
    {
        var b = System.Text.Encoding.Latin1.GetBytes(s);
        int n = Math.Min(b.Length, size - 1);
        GuestMemory.Fill(dst, 0, (uint)size);
        b.AsSpan(0, n).CopyTo(GuestMemory.Span(dst, n));
    }

    /// <summary>(path): whether it ends in ".pad".</summary>
    [Hle("NID_E86136A0", Nid = 0xE86136A0, Library = Lib)]
    bool EndsWithPad(uint path)
    {
        if (path == 0) return false;
        string s = GuestMemory.ReadCString(path);
        return s.Length >= 3 && s.Length >= 4 && s[^4] == '.' && s[^3..] == "pad";
    }

    // ------------------------------------------------------------------ the C runtime's heap figures

    /// <summary>malloc_stats_fast: the current system size (+8) / the in-use size (+0xC).</summary>
    [Hle("NID_8EA8CEE6", Nid = 0x8EA8CEE6, Library = Lib)] uint HeapStat08(HleContext c) => HeapStat(c, 8);
    [Hle("NID_54E92110", Nid = 0x54E92110, Library = Lib)] uint HeapStat0C(HleContext c) => HeapStat(c, 12);

    static uint HeapStat(HleContext c, uint offset)
    {
        uint st = Scratch(c, 0x20);
        Sys(c, LibcMallocStatsFast, st);
        return GuestMemory.R32(st + offset);
    }

    /// <summary>(void *p): free unless NULL (the module also skips its own static empty object).</summary>
    [Hle("NID_A404DED6", Nid = 0xA404DED6, Library = Lib)]
    void FreeObject(HleContext c, uint p) { if (p != 0) Sys(c, LibcFree, p); }

    /// <summary>(T *p): frees p-&gt;+4 and p.</summary>
    [Hle("NID_B31C3F2C", Nid = 0xB31C3F2C, Library = Lib)]
    void FreePair(HleContext c, uint p)
    {
        if (p == 0) return;
        Sys(c, LibcFree, GuestMemory.R32(p + 4));
        Sys(c, LibcFree, p);
    }

    // ------------------------------------------------------------------ the module's worker objects
    // { +8 lightweight mutex, +0x38 event flag uid, +0x3C value, +0x40 owned object, +0x44 stop flag }

    /// <summary>Asks the worker to stop: flag +0x44 under its lock, then the 0x1000000 event.</summary>
    [Hle("NID_04E9D29E", Nid = 0x04E9D29E, Library = Lib)]
    [Hle("NID_CF3FCF07", Nid = 0xCF3FCF07, Library = Lib)]
    void WorkerStop(HleContext c, uint w)
    {
        MutexLock(c, w + 8);
        W8(w + 0x44, 1);
        T.sceKernelUnlockLwMutex(w + 8, 1);
        Sys(c, SetEvent, R32(w + 0x38), 0x1000000, 0, 0);
    }

    /// <summary>(worker, value, object): stores the value and the object (releasing the one it replaces, vtable +4).</summary>
    [Hle("NID_26F1BCC0", Nid = 0x26F1BCC0, Library = Lib)]
    void WorkerSet(HleContext c, uint w, uint value, uint obj)
    {
        MutexLock(c, w + 8);
        uint old = R32(w + 0x40);
        W32(w + 0x3C, value);
        if (obj != old && old != 0) GuestThread.Current!.CallGuest(R32(R32(old) + 4), old);
        W32(w + 0x40, obj);
        T.sceKernelUnlockLwMutex(w + 8, 1);
        Sys(c, SetEvent, R32(w + 0x38), 0x1000000, 0, 0);
    }

    /// <summary>Clears the worker's value (-1) and object (released).</summary>
    [Hle("NID_5281B074", Nid = 0x5281B074, Library = Lib)]
    void WorkerClear(HleContext c, uint w)
    {
        MutexLock(c, w + 8);
        W32(w + 0x3C, uint.MaxValue);
        if (R32(w + 0x40) is var old and not 0) GuestThread.Current!.CallGuest(R32(R32(old) + 4), old);
        W32(w + 0x40, 0);
        T.sceKernelUnlockLwMutex(w + 8, 1);
        Sys(c, SetEvent, R32(w + 0x38), 0x1000000, 0, 0);
    }

    // ------------------------------------------------------------------ small helpers of the module

    /// <summary>(F *f): the object's size word (+8), 0 for NULL.</summary>
    [Hle("NID_3685EDC7", Nid = 0x3685EDC7, Library = Lib)] uint FileSize(uint f) => f == 0 ? 0 : R32(f + 8);

    /// <summary>(T **pp): the byte at +0x32 of **pp, 0 when either pointer is NULL.</summary>
    [Hle("NID_7CDAD06D", Nid = 0x7CDAD06D, Library = Lib)]
    uint InnerFlag32(uint pp)
    {
        uint p = R32(pp);
        if (p == 0 || R32(p) == 0) return 0;
        return R8(R32(p) + 0x32);
    }

    /// <summary>(const char *url): whether the resource-request logger takes the URL: a development unit with the
    /// system parameter 0x8C3860 set (not 0 or -1) and a "manx://netlog" URL.</summary>
    [Hle("NID_3CCDEB72", Nid = 0x3CCDEB72, Library = Lib)]
    bool IsNetlogUrl(HleContext c, uint url)
    {
        if (SysI(c, 0x7D4F8F83) == 0) return false;
        uint v = Scratch(c, 4);
        if (SysI(c, 0x347C1BDB, 0x8C3860, v) != 0) return false;
        uint flag = GuestMemory.R32(v);
        if (flag == 0 || flag == uint.MaxValue) return false;
        return GuestMemory.ReadCString(url, 13) == "manx://netlog";
    }
}
