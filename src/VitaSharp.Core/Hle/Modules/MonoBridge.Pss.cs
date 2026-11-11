using System.Buffers.Binary;
using System.Security.Cryptography;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// The <c>pss_*</c> platform calls of SceLibMonoBridge. Inferred contracts (no copy of the module to read): thin
/// wrappers over the kernel, sceIo* (with the PSM path roots), sceNet* (sockets get the name "pss_socket"), the Mono
/// thread-control calls of SceKernelForMono, executable memory from sceKernelAllocMemBlockForVM, and time in the units
/// Mono's platform layer uses (milliseconds for the 32-bit tick, 100 ns for the 64-bit ones and FILETIMEs). The
/// encrypted-file reader reads the files as they are (PSM's edata decryption needs the console's keys). The USB
/// transport is the link to the PSM SDK's debugger on a PC, which the emulator does not have (default).
/// </summary>
public sealed unsafe partial class MonoBridge
{
    SysMem? _sysmem;
    SysMem Sm => _sysmem ??= Emu.Hle.Modules.OfType<SysMem>().First();
    Clib? _clib;
    Clib Cl => _clib ??= Emu.Hle.Modules.OfType<Clib>().First();

    readonly Dictionary<string, uint> _nids = new();

    /// <summary>Runs another library's HLE function on the current registers (same arguments).</summary>
    void Fwd(HleContext c, string name, string lib)
    {
        uint nid;
        lock (_nids)
            if (!_nids.TryGetValue(name, out nid))
                _nids[name] = nid = NidDatabase.LookupName(name).FirstOrDefault(i => i.Library == lib)?.Nid ?? 0;
        if (nid != 0 && Emu.Hle.ImplOf(nid) is { } f) f(c);
        else c.Return(SceError.ERROR);
    }

    /// <summary>Shifts the arguments one register up and puts <paramref name="first"/> in r0 (at most 3 arguments).</summary>
    static void Prepend(HleContext c, uint first)
    {
        c.Cpu->R[3] = c.Cpu->R[2]; c.Cpu->R[2] = c.Cpu->R[1]; c.Cpu->R[1] = c.Cpu->R[0]; c.Cpu->R[0] = first;
    }

    // ------------------------------------------------------------------ memory

    [Hle("pss_alloc_mem", Library = Lib)] [Hle("pss_alloc_raw", Library = Lib)] uint PssAlloc(uint size) => Malloc(Math.Max(size, 1));
    [Hle("pss_free_mem", Library = Lib)] [Hle("pss_free_raw", Library = Lib)] void PssFree(uint p) => Free(p);
    [Hle("pss_getpagesize", Library = Lib)] int PssGetpagesize() => 4096;

    // JIT code memory: one VM block (sceKernelAllocMemBlockForVM, 1 MiB granules, 16 MiB budget) run as a heap.
    int _codeBlock = -1;
    uint _codeBase, _codeSpace;

    /// <summary>pss_code_mem_initialize(size): reserves the JIT block (size rounded up to 1 MiB; 0 = 8 MiB). 0 or an error.</summary>
    [Hle("pss_code_mem_initialize", Library = Lib)]
    int PssCodeMemInitialize(uint size)
    {
        if (_codeBlock >= 0) return 0;
        size = size == 0 ? 8u << 20 : GuestMemory.AlignUp(Math.Min(size, 16u << 20), 1u << 20);
        int uid = Sm.sceKernelAllocMemBlockForVM("SceMonoBridgeJit", size);
        if (uid < 0) return uid;
        uint cell = Malloc0(4);
        Sm.sceKernelGetMemBlockBase(uid, new Ptr<uint>(cell));
        _codeBase = R32(cell);
        Free(cell);
        _codeSpace = Cl.sceClibMspaceCreate(_codeBase, size);
        _codeBlock = uid;
        return 0;
    }

    [Hle("pss_code_mem_terminate", Library = Lib)]
    int PssCodeMemTerminate()
    {
        if (_codeBlock < 0) return 0;
        Cl.sceClibMspaceDestroy(_codeSpace);
        Sm.sceKernelFreeMemBlockForVM(_codeBlock);
        _codeBlock = -1; _codeBase = _codeSpace = 0;
        return 0;
    }

    /// <summary>pss_code_mem_alloc(size): 16-byte aligned executable memory (the block is set up on first use).</summary>
    [Hle("pss_code_mem_alloc", Library = Lib)]
    uint PssCodeMemAlloc(uint size)
    {
        if (_codeBlock < 0 && PssCodeMemInitialize(0) < 0) return 0;
        return Cl.MspaceMemalign(_codeSpace, 16, Math.Max(size, 1));
    }

    [Hle("pss_code_mem_free", Library = Lib)]
    void PssCodeMemFree(uint p) { if (p != 0 && _codeSpace != 0) Cl.sceClibMspaceFree(_codeSpace, p); }

    /// <summary>pss_code_mem_lock / unlock: the VM domain opened for writing code and closed again (always 0: a nested
    /// open is not an error for the caller).</summary>
    [Hle("pss_code_mem_lock", Library = Lib)] int PssCodeMemLock() { Sm.sceKernelOpenVMDomain(); return 0; }
    [Hle("pss_code_mem_unlock", Library = Lib)] int PssCodeMemUnlock() { Sm.sceKernelCloseVMDomain(); return 0; }

    /// <summary>pss_code_mem_flush_icache(addr, size): drops the translations of the range (sceKernelSyncVMDomain).</summary>
    [Hle("pss_code_mem_flush_icache", Library = Lib)]
    int PssCodeMemFlushIcache(uint addr, uint size)
    {
        if (_codeBlock >= 0 && addr >= _codeBase) return Sm.sceKernelSyncVMDomain(_codeBlock, addr, size) < 0 ? -1 : 0;
        Emu.Code.Invalidate(addr, size);
        return 0;
    }

    // ------------------------------------------------------------------ threads and semaphores

    [Hle("pss_threads_initialize", Library = Lib)] int PssThreadsInitialize() => 0;
    /// <summary>pss_supports_fast_tls: TRUE (the pthread_vita_tls_* calls below).</summary>
    [Hle("pss_supports_fast_tls", Library = Lib)] bool PssSupportsFastTls() => true;

    [Hle("pss_get_thread_context", Library = Lib)] void PssGetThreadContext(HleContext c) => Fwd(c, "sceKernelGetThreadContextForMono", "SceKernelForMono");
    [Hle("pss_set_thread_context", Library = Lib)] void PssSetThreadContext(HleContext c) => Fwd(c, "sceKernelSetThreadContextForMono", "SceKernelForMono");
    [Hle("pss_suspend_thread", Library = Lib)] void PssSuspendThread(HleContext c) => Fwd(c, "sceKernelSuspendThreadForMono", "SceKernelForMono");
    [Hle("pss_resume_thread", Library = Lib)] void PssResumeThread(HleContext c) => Fwd(c, "sceKernelResumeThreadForMono", "SceKernelForMono");

    /// <summary>pss_delay_thread(microseconds).</summary>
    [Hle("pss_delay_thread", Library = Lib)] int PssDelayThread(uint us) { Tm.sceKernelDelayThread(us); return 0; }

    /// <summary>pss_nanosleep(const timespec *req, timespec *rem): 0 (rem = 0), -1 with EINVAL for a bad request.</summary>
    [Hle("pss_nanosleep", Library = Lib)]
    int PssNanosleep(uint req, uint rem)
    {
        if (req == 0 || R32(req + 4) >= 1_000_000_000) { Libc.SetErrno(Libc.EINVAL); return -1; }
        ulong us = (ulong)R32(req) * 1_000_000 + (R32(req + 4) + 999) / 1000;
        Tm.sceKernelDelayThread((uint)Math.Min(us, uint.MaxValue));
        if (rem != 0) { W32(rem, 0); W32(rem + 4, 0); }
        return 0;
    }

    /// <summary>pss_create_semaphore(initial, max): a kernel semaphore (max &lt;= 0 = no limit), its UID.</summary>
    [Hle("pss_create_semaphore", Library = Lib)]
    int PssCreateSemaphore(int initial, int max) => Tm.sceKernelCreateSema("SceMonoBridgeSema", 0, Math.Max(initial, 0), max <= 0 ? 0x7FFFFFFF : max, 0);

    [Hle("pss_delete_semaphore", Library = Lib)] int PssDeleteSemaphore(int uid) => Tm.sceKernelDeleteSema(uid) < 0 ? -1 : 0;

    /// <summary>pss_wait_semaphore(uid, timeout ms; -1 = forever): 0, 1 on timeout, -1 on an error.</summary>
    [Hle("pss_wait_semaphore", Library = Lib)]
    int PssWaitSemaphore(int uid, int timeoutMs)
    {
        uint cell = 0;
        if (timeoutMs >= 0) { cell = Malloc0(4); W32(cell, (uint)Math.Min((long)timeoutMs * 1000, uint.MaxValue)); }
        int r = Tm.sceKernelWaitSema(uid, 1, new Ptr<uint>(cell));
        Free(cell);
        return r == 0 ? 0 : r == unchecked((int)0x80028005) ? 1 : -1;
    }

    /// <summary>pss_signal_semaphore(uid, count): 0 or -1.</summary>
    [Hle("pss_signal_semaphore", Library = Lib)]
    int PssSignalSemaphore(int uid, int count) => Tm.sceKernelSignalSema(uid, Math.Max(count, 1)) < 0 ? -1 : 0;

    // ------------------------------------------------------------------ process, time, errno

    [Hle("pss_getpid", Library = Lib)] int PssGetpid() => Emu.Pid;

    const uint ErrnoTlsSlot = 0x88; // SceLibc's errno word (_sceLibcErrnoLoc)
    [Hle("pss_get_errnoloc", Library = Lib)] uint PssGetErrnoloc() => GuestThread.Current is { } t ? t.TlsBase + ErrnoTlsSlot * 4 : 0;

    /// <summary>pss_disable_ftz: clears flush-to-zero (FPSCR bit 24) on the calling thread.</summary>
    [Hle("pss_disable_ftz", Library = Lib)] void PssDisableFtz(HleContext c) => c.Cpu->Fpscr &= ~(1u << 24);

    [Hle("pss_gettimeofday", Library = Lib)]
    int PssGettimeofday(uint tv, uint tz)
    {
        var (s, u) = Now();
        if (tv != 0) { W32(tv, s); W32(tv + 4, u); }
        if (tz != 0) { W32(tz, 0); W32(tz + 4, 0); }
        return 0;
    }

    /// <summary>pss_get_ticks_32: milliseconds of process time.</summary>
    [Hle("pss_get_ticks_32", Library = Lib)] uint PssGetTicks32() => (uint)(K.ProcessTimeUs / 1000);
    /// <summary>pss_get_ticks_64: 100 ns units of process time.</summary>
    [Hle("pss_get_ticks_64", Library = Lib)] ulong PssGetTicks64() => K.ProcessTimeUs * 10;
    /// <summary>pss_get_ticks_since_111: 100 ns units since 1 January 0001 UTC (.NET DateTime ticks).</summary>
    [Hle("pss_get_ticks_since_111", Library = Lib)] ulong PssGetTicksSince111() => (ulong)DateTime.UtcNow.Ticks;

    static ulong FileTimeAt(uint p)
    {
        var dt = new DateTime(R16(p), Math.Max((int)R16(p + 2), 1), Math.Max((int)R16(p + 4), 1), R16(p + 6) % 24, R16(p + 8) % 60, R16(p + 10) % 60, DateTimeKind.Utc);
        return (ulong)(dt.ToFileTimeUtc() + R32(p + 12) * 10L);
    }

    static void WriteFileTime(uint p, ulong ft)
    {
        var dt = DateTime.FromFileTimeUtc((long)ft);
        GuestMemory.Write(p, (ushort)dt.Year); GuestMemory.Write(p + 2, (ushort)dt.Month); GuestMemory.Write(p + 4, (ushort)dt.Day);
        GuestMemory.Write(p + 6, (ushort)dt.Hour); GuestMemory.Write(p + 8, (ushort)dt.Minute); GuestMemory.Write(p + 10, (ushort)dt.Second);
        W32(p + 12, (uint)(dt.Ticks % TimeSpan.TicksPerSecond / 10));
    }

    /// <summary>pss_get_win32_filetime(path, creation*, access*, write*): the file's times as FILETIMEs; 0 or -1.</summary>
    [Hle("pss_get_win32_filetime", Library = Lib)]
    int PssGetWin32Filetime(uint path, uint creation, uint access, uint write)
    {
        uint st = Malloc0(0x60);
        int r = Io.sceIoGetstat(PsmPath(Utf8(path)), st);
        if (r >= 0)
        {
            if (creation != 0) GuestMemory.Write(creation, FileTimeAt(st + 16));
            if (access != 0) GuestMemory.Write(access, FileTimeAt(st + 32));
            if (write != 0) GuestMemory.Write(write, FileTimeAt(st + 48));
        }
        Free(st);
        if (r < 0) { Libc.SetErrno(IoErrno(r)); return -1; }
        return 0;
    }

    /// <summary>pss_set_win32_filetime(path, creation*, access*, write*): sets the times given (NULL = keep); 0 or -1.</summary>
    [Hle("pss_set_win32_filetime", Library = Lib)]
    int PssSetWin32Filetime(uint path, uint creation, uint access, uint write)
    {
        uint st = Malloc0(0x60);
        int bits = 0;
        if (creation != 0) { WriteFileTime(st + 16, GuestMemory.Read<ulong>(creation)); bits |= 0x08; }
        if (access != 0) { WriteFileTime(st + 32, GuestMemory.Read<ulong>(access)); bits |= 0x10; }
        if (write != 0) { WriteFileTime(st + 48, GuestMemory.Read<ulong>(write)); bits |= 0x20; }
        int r = bits == 0 ? 0 : Io.sceIoChstat(PsmPath(Utf8(path)), st, bits);
        Free(st);
        if (r < 0) { Libc.SetErrno(IoErrno(r)); return -1; }
        return 0;
    }

    /// <summary>pss_get_account_hash_id(uint64 *out): the PSN account hash the runtime binds licences to; no account is
    /// signed in on the emulator (0, the hash zeroed).</summary>
    [Hle("pss_get_account_hash_id", Library = Lib)]
    int PssGetAccountHashId(uint outp) { if (outp != 0) GuestMemory.Write(outp, 0UL); return 0; }

    // ------------------------------------------------------------------ random numbers

    uint _prng;
    [Hle("pss_get_prng_provider", Library = Lib)] uint PssGetPrngProvider() => _prng != 0 ? _prng : _prng = Malloc0(4);
    [Hle("pss_free_prng_provider", Library = Lib)] int PssFreePrngProvider(uint p) => 0;

    /// <summary>pss_prng_fill(provider, buf, size): cryptographic random bytes, 0.</summary>
    [Hle("pss_prng_fill", Library = Lib)]
    int PssPrngFill(uint provider, uint buf, uint size)
    {
        if (buf == 0) return -1;
        RandomNumberGenerator.Fill(GuestMemory.Span(buf, (int)size));
        return 0;
    }

    // ------------------------------------------------------------------ files

    [Hle("pss_io_open", Library = Lib)] int PssIoOpen(uint path, int flags, int mode) => Io.sceIoOpen(PsmPath(Utf8(path)), flags, mode);
    [Hle("pss_io_close", Library = Lib)] int PssIoClose(int fd) => Io.sceIoClose(fd);
    [Hle("pss_io_read", Library = Lib)] int PssIoRead(int fd, uint buf, uint size) => Io.sceIoRead(fd, buf, size);
    [Hle("pss_io_write", Library = Lib)] int PssIoWrite(int fd, uint buf, uint size) => Io.sceIoWrite(fd, buf, size);
    [Hle("pss_io_lseek", Library = Lib)] long PssIoLseek(int fd, long offset, int whence) => Io.sceIoLseek(fd, offset, whence);
    [Hle("pss_io_dopen", Library = Lib)] int PssIoDopen(uint path) => Io.sceIoDopen(PsmPath(Utf8(path)));
    [Hle("pss_io_dclose", Library = Lib)] int PssIoDclose(int fd) => Io.sceIoDclose(fd);
    [Hle("pss_io_mkdir", Library = Lib)] int PssIoMkdir(uint path, int mode) => Io.sceIoMkdir(PsmPath(Utf8(path)), mode);
    [Hle("pss_io_rmdir", Library = Lib)] int PssIoRmdir(uint path) => Io.sceIoRmdir(PsmPath(Utf8(path)));
    [Hle("pss_io_remove", Library = Lib)] int PssIoRemove(uint path) => Io.sceIoRemove(PsmPath(Utf8(path)));
    [Hle("pss_io_rename", Library = Lib)] int PssIoRename(uint from, uint to) => Io.sceIoRename(PsmPath(Utf8(from)), PsmPath(Utf8(to)));
    [Hle("pss_io_getstat", Library = Lib)] int PssIoGetstat(uint path, uint stat) => Io.sceIoGetstat(PsmPath(Utf8(path)), stat);
    [Hle("pss_io_chstat", Library = Lib)] int PssIoChstat(uint path, uint stat, int bits) => Io.sceIoChstat(PsmPath(Utf8(path)), stat, bits);

    /// <summary>pss_crypto_open(path): the program's (edata) file opened for reading; its descriptor or an error.</summary>
    [Hle("pss_crypto_open", Library = Lib)] int PssCryptoOpen(uint path) => Io.sceIoOpen(PsmPath(Utf8(path)), SCE_O_RDONLY, 0);
    [Hle("pss_crypto_close", Library = Lib)] int PssCryptoClose(int fd) => Io.sceIoClose(fd);
    /// <summary>pss_crypto_read(fd, buf, size): bytes read or an error.</summary>
    [Hle("pss_crypto_read", Library = Lib)] int PssCryptoRead(int fd, uint buf, uint size) => Io.sceIoRead(fd, buf, size);

    /// <summary>pss_crypto_fread(buf, size, count, fd): fread's shape, the number of whole items read.</summary>
    [Hle("pss_crypto_fread", Library = Lib)]
    uint PssCryptoFread(uint buf, uint size, uint count, int fd)
    {
        if (size == 0 || count == 0) return 0;
        ulong total = (ulong)size * count;
        if (total > int.MaxValue) return 0;
        uint got = 0;
        while (got < total)
        {
            int r = Io.sceIoRead(fd, buf + got, (uint)total - got);
            if (r <= 0) break;
            got += (uint)r;
        }
        return got / size;
    }

    // ------------------------------------------------------------------ sockets (SceNet)

    /// <summary>pss_net_init(): sceNetInit with a 128 KiB pool of the bridge's own; "already initialized" is success.</summary>
    [Hle("pss_net_init", Library = Lib)]
    void PssNetInit(HleContext c)
    {
        uint param = Malloc0(12), pool = Malloc(0x20000);
        W32(param, pool); W32(param + 4, 0x20000); W32(param + 8, 0);
        c.Cpu->R[0] = param;
        Fwd(c, "sceNetInit", "SceNet");
        Free(param);
        if (c.Cpu->R[0] == 0x80410110) { c.Cpu->R[0] = 0; Free(pool); }
        else if ((int)c.Cpu->R[0] < 0) Free(pool);
    }

    [Hle("pss_net_socket", Library = Lib)] void PssNetSocket(HleContext c) { Prepend(c, C.ConstString("pss_socket")); Fwd(c, "sceNetSocket", "SceNet"); }
    [Hle("pss_net_socket_close", Library = Lib)] void PssNetSocketClose(HleContext c) => Fwd(c, "sceNetSocketClose", "SceNet");
    [Hle("pss_net_accept", Library = Lib)] void PssNetAccept(HleContext c) => Fwd(c, "sceNetAccept", "SceNet");
    [Hle("pss_net_bind", Library = Lib)] void PssNetBind(HleContext c) => Fwd(c, "sceNetBind", "SceNet");
    [Hle("pss_net_connect", Library = Lib)] void PssNetConnect(HleContext c) => Fwd(c, "sceNetConnect", "SceNet");
    [Hle("pss_net_listen", Library = Lib)] void PssNetListen(HleContext c) => Fwd(c, "sceNetListen", "SceNet");
    [Hle("pss_net_recv", Library = Lib)] void PssNetRecv(HleContext c) => Fwd(c, "sceNetRecv", "SceNet");
    [Hle("pss_net_recvfrom", Library = Lib)] void PssNetRecvfrom(HleContext c) => Fwd(c, "sceNetRecvfrom", "SceNet");
    [Hle("pss_net_send", Library = Lib)] void PssNetSend(HleContext c) => Fwd(c, "sceNetSend", "SceNet");
    [Hle("pss_net_sendto", Library = Lib)] void PssNetSendto(HleContext c) => Fwd(c, "sceNetSendto", "SceNet");
    [Hle("pss_net_shutdown", Library = Lib)] void PssNetShutdown(HleContext c) => Fwd(c, "sceNetShutdown", "SceNet");
    [Hle("pss_net_getsockopt", Library = Lib)] void PssNetGetsockopt(HleContext c) => Fwd(c, "sceNetGetsockopt", "SceNet");
    [Hle("pss_net_setsockopt", Library = Lib)] void PssNetSetsockopt(HleContext c) => Fwd(c, "sceNetSetsockopt", "SceNet");
    [Hle("pss_net_getpeername", Library = Lib)] void PssNetGetpeername(HleContext c) => Fwd(c, "sceNetGetpeername", "SceNet");
    [Hle("pss_net_getsockname", Library = Lib)] void PssNetGetsockname(HleContext c) => Fwd(c, "sceNetGetsockname", "SceNet");
    [Hle("pss_net_epoll_create", Library = Lib)] void PssNetEpollCreate(HleContext c) { Prepend(c, C.ConstString("pss_epoll")); Fwd(c, "sceNetEpollCreate", "SceNet"); }
    [Hle("pss_net_epoll_ctl", Library = Lib)] void PssNetEpollCtl(HleContext c) => Fwd(c, "sceNetEpollControl", "SceNet");
    [Hle("pss_net_epoll_wait", Library = Lib)] void PssNetEpollWait(HleContext c) => Fwd(c, "sceNetEpollWait", "SceNet");
    [Hle("pss_net_epoll_destroy", Library = Lib)] void PssNetEpollDestroy(HleContext c) => Fwd(c, "sceNetEpollDestroy", "SceNet");

    /// <summary>pss_net_resolver_create(): sceNetResolverCreate("pss_resolver", NULL, 0).</summary>
    [Hle("pss_net_resolver_create", Library = Lib)]
    void PssNetResolverCreate(HleContext c)
    {
        c.Cpu->R[0] = C.ConstString("pss_resolver"); c.Cpu->R[1] = 0; c.Cpu->R[2] = 0;
        Fwd(c, "sceNetResolverCreate", "SceNet");
    }

    [Hle("pss_net_resolver_start_ntoa", Library = Lib)] void PssNetResolverStartNtoa(HleContext c) => Fwd(c, "sceNetResolverStartNtoa", "SceNet");
    [Hle("pss_net_resolver_start_aton", Library = Lib)] void PssNetResolverStartAton(HleContext c) => Fwd(c, "sceNetResolverStartAton", "SceNet");

    /// <summary>pss_net_gethostname(buf, len): the console's name ("PS Vita"), 0; -1 when it does not fit.</summary>
    [Hle("pss_net_gethostname", Library = Lib)]
    int PssNetGethostname(uint buf, uint len)
    {
        const string name = "PS Vita";
        if (buf == 0 || len < name.Length + 1) return -1;
        GuestMemory.WriteCString(buf, name, (int)len);
        return 0;
    }

    [Hle("pss_net_htonl", Library = Lib)] [Hle("pss_net_ntohl", Library = Lib)] uint PssNetHtonl(uint v) => BinaryPrimitives.ReverseEndianness(v);
    [Hle("pss_net_htons", Library = Lib)] [Hle("pss_net_ntohs", Library = Lib)] uint PssNetHtons(uint v) => BinaryPrimitives.ReverseEndianness((ushort)v);

    // ------------------------------------------------------------------ the PSM SDK's USB debugger link (no PC host)

    const int UsbNotConnected = -1;
    [Hle("pss_usb_transport_connect", Library = Lib, Default = true)] int PssUsbConnect() => UsbNotConnected;
    [Hle("pss_usb_transport_send", Library = Lib, Default = true)] int PssUsbSend(uint buf, uint len) => UsbNotConnected;
    [Hle("pss_usb_transport_recv", Library = Lib, Default = true)] int PssUsbRecv(uint buf, uint len) => UsbNotConnected;
    [Hle("pss_usb_transport_close1", Library = Lib, Default = true)] int PssUsbClose1() => 0;
    [Hle("pss_usb_transport_close2", Library = Lib, Default = true)] int PssUsbClose2() => 0;
}
