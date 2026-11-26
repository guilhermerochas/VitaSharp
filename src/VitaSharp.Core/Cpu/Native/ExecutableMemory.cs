using System.Runtime.InteropServices;

namespace VitaSharp.Core.Cpu.Native;

/// <summary>
/// One growable arena for generated machine code, W^X on every platform:
/// <list type="bullet">
/// <item>Windows and Linux: the arena is a shared memory object mapped twice, a read/write view that the compiler writes
/// through and a read/execute view that runs. No page is ever writable and executable in the same mapping, and no
/// protection is flipped on pages other threads may be executing (flipping a shared page to RW in place would fault
/// a thread running an older block on that page).</item>
/// <item>macOS: one MAP_JIT mapping; writes are enabled per thread with pthread_jit_write_protect_np (Apple silicon),
/// so other threads keep executing while the compiling thread writes. On Apple silicon this needs a Native AOT build
/// (see <see cref="UnsupportedReason"/>).</item>
/// <item>Android: memfd (API 30+) or an ashmem region (ASharedMemory, API 26+) mapped twice, like Linux.</item>
/// </list>
/// After every write the instruction cache is synchronized: FlushInstructionCache (Windows, also on ARM64),
/// sys_icache_invalidate (macOS), a generated DC CVAU / IC IVAU helper (Linux and Android ARM64); x64 needs nothing.
/// Code is placed with a bump allocator in a single reservation, so every block is within a rel32 jump of the others
/// and of the entry trampoline. Blocks are never freed individually (invalidated code stays mapped until
/// <see cref="Reset"/>, which the owner only calls when no guest thread runs).
/// </summary>
public sealed unsafe partial class ExecutableMemory : IDisposable
{
    public const int ReserveSize = 256 << 20;    // 256 MiB of address space
    private const int CommitGranule = 1 << 20;            // committed (made usable) in 1 MiB steps

    private readonly object _lock = new();
    private byte* _exec;       // execute view
    private byte* _write;      // write view (== _exec on macOS)
    private int _used, _committed;
    private nint _section;     // Windows section handle / Linux memfd
    private readonly Mode _mode;

    private enum Mode
    {
        DualMapped, AppleJit, Rwx
    }

    public byte* ExecBase => _exec;
    public int Used => _used;
    public string Description => _mode switch
    {
        Mode.DualMapped => "dual-mapped W^X",
        Mode.AppleJit => "MAP_JIT W^X",
        _ => "RWX (no W^X: dual mapping unavailable)",
    };

    /// <summary>
    /// Why generated code cannot be written on this host, or null when it can.
    /// <para>
    /// macOS on Apple silicon with a JIT-compiled .NET runtime (dotnet run, dotnet test, framework-dependent and
    /// non-AOT self-contained builds): CoreCLR puts the managed code it compiles in MAP_JIT memory too (it never dual
    /// maps on Apple ARM64), and pthread_jit_write_protect_np switches every MAP_JIT region of the thread at once. Once
    /// the write window is open, returning from the P/Invoke into the JIT-compiled caller faults (SIGBUS), so the window
    /// can only be driven from ahead-of-time compiled code: Native AOT builds, where no managed code lives in MAP_JIT
    /// memory. Without Native AOT the native backend is unavailable there and the IL backend runs instead.
    /// </para>
    /// </summary>
    public static string? UnsupportedReason { get; } =
        OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64 && System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeCompiled
            ? "macOS on Apple silicon needs a Native AOT build for native code (the .NET JIT's own code is MAP_JIT memory, which faults while a thread's JIT write protection is off)"
            : null;

    public ExecutableMemory()
    {
        if (UnsupportedReason != null)
        {
            throw new PlatformNotSupportedException(UnsupportedReason);
        }

        if (OperatingSystem.IsWindows())
        {
            CreateWindows();
            _mode = Mode.DualMapped;
        }
        else if (OperatingSystem.IsMacOS())
        {
            CreateApple();
            _mode = Mode.AppleJit;
        }
        else if (TryCreateLinux())
        {
            _mode = Mode.DualMapped;
        }
        else
        {
            CreateRwx();
            _mode = Mode.Rwx;
        }
        _committed = 0;
    }

    /// <summary>
    /// Reserves <paramref name="size"/> bytes (16-byte aligned) and returns the execute address. The bytes are written
    /// through <see cref="Write"/>; the caller assembles its code for this address before writing it.
    /// </summary>
    public byte* Allocate(int size)
    {
        lock (_lock)
        {
            int start = (_used + 15) & ~15;
            if (start + size > ReserveSize)
            {
                throw new OutOfMemoryException("JIT code arena full");
            }

            while (start + size > _committed)
            {
                CommitMore();
            }

            _used = start + size;
            return _exec + start;
        }
    }

    /// <summary>Bytes reserved beyond an encoding's length when it has to be redone at another address (an x64
    /// encoding may change length with its address: short branches to stubs within reach).</summary>
    public const int PlaceSlack = 64;

    /// <summary>Encodings that had to be redone because another thread placed code first (statistics).</summary>
    public long PlaceRetries;

    /// <summary>
    /// Places code whose encoding depends on its address: <paramref name="assemble"/> is called with the execute
    /// address the code will occupy, outside the arena lock, so threads compiling at the same time (the JIT workers
    /// and the precompilation) never wait for each other's encoding. The encoding is first made for the address the
    /// next block would get; if another thread placed code meanwhile, a range (its length plus
    /// <see cref="PlaceSlack"/>) is reserved and the code is encoded again for it, so the lock only ever covers the
    /// bump of the allocation pointer. <paramref name="assemble"/> may therefore run more than once.
    /// </summary>
    public byte* Place(Func<ulong, byte[]> assemble) => Place(new CodeEncoder(rip => assemble(rip)), reuseFreed: false);

    /// <summary>Encodes code for execution at the given address into a buffer of the encoder's that stays valid until
    /// its next call (the JIT's emitters reuse one per thread: nothing is allocated per block).</summary>
    public delegate ReadOnlySpan<byte> CodeEncoder(ulong rip);

    /// <summary><see cref="Place(Func{ulong, byte[]})"/> with an encoder that returns its own buffer.</summary>
    public byte* Place(CodeEncoder assemble)
    {
        int start = (Volatile.Read(ref _used) + 15) & ~15;
        int reserved = 0; // 0: nothing reserved yet, the encoding is for a guessed address
        while (true)
        {
            ReadOnlySpan<byte> code = assemble((ulong)(_exec + start));
            if (reserved != 0 && code.Length <= reserved)
            {
                Write(_exec + start, code);
                return _exec + start;
            }

            bool placed = false;
            lock (_lock)
            {
                int next = (_used + 15) & ~15;
                if (reserved == 0 && next == start)
                {
                    Claim(start, code.Length);
                    placed = true;
                }
                else
                {
                    // another thread placed code first (or the encoding at the reserved address grew): reserve a
                    // range at the current end and encode again for it, outside the lock
                    start = next;
                    reserved = code.Length + PlaceSlack;
                    Claim(start, reserved);
                    Interlocked.Increment(ref PlaceRetries);
                }
            }

            if (placed)
            {
                Write(_exec + start, code);
                return _exec + start;
            }
        }
    }

    /// <summary>Places code that does not depend on its address (copied in, no encoding under the lock).</summary>
    public byte* Place(ReadOnlySpan<byte> code) => Place(code, reuseFreed: false);

    /// <summary>
    /// <see cref="Place(ReadOnlySpan{byte})"/>; with <paramref name="reuseFreed"/> the code first goes into a range
    /// given back with <see cref="Free"/> (the code translated ahead that never ran), so the arena only grows when no
    /// freed range fits.
    /// </summary>
    public byte* Place(ReadOnlySpan<byte> code, bool reuseFreed)
    {
        int start;
        lock (_lock)
        {
            start = reuseFreed ? TakeFreed(code.Length) : -1;
            if (start < 0)
            {
                start = (_used + 15) & ~15;
                Claim(start, code.Length);
            }
        }

        Write(_exec + start, code);
        return _exec + start;
    }

    /// <summary>
    /// <see cref="Place(Func{ulong, byte[]})"/>, first trying a freed range (<see cref="Free"/>) with
    /// <paramref name="reuseFreed"/>: the code is encoded once for the next address to learn its length, and again for
    /// a freed range that holds that length (freed ranges are the sizes of evicted blocks, so a slack would rarely
    /// fit); an x64 encoding that grows at the new address (a short branch out of reach) gives the range back and is
    /// placed at the end. Meant for work that is not waited for (the precompilation's background threads): the second
    /// encoding costs time, and only when a range was freed.
    /// </summary>
    public byte* Place(Func<ulong, byte[]> assemble, bool reuseFreed) => Place(new CodeEncoder(rip => assemble(rip)), reuseFreed);

    /// <summary><see cref="Place(Func{ulong, byte[]}, bool)"/> with an encoder that returns its own buffer.</summary>
    public byte* Place(CodeEncoder assemble, bool reuseFreed)
    {
        if (!reuseFreed || Volatile.Read(ref _freeCount) == 0)
        {
            return Place(assemble);
        }

        int guessLength = assemble((ulong)(_exec + ((Volatile.Read(ref _used) + 15) & ~15))).Length;
        int reserved = (guessLength + 15) & ~15; // most encodings keep their length at another address
        int start;
        lock (_lock)
        {
            start = TakeFreed(reserved);
        }

        if (start < 0)
        {
            return Place(assemble);
        }

        ReadOnlySpan<byte> code = assemble((ulong)(_exec + start));
        if (code.Length > reserved)
        {
            Free(_exec + start, reserved);
            return Place(assemble);
        }

        Write(_exec + start, code);
        int used = (code.Length + 15) & ~15;
        if (reserved - used >= MinFreeRange)
        {
            Free(_exec + start + used, reserved - used);
        }

        return _exec + start;
    }

    /// <summary>Freed ranges smaller than this are not kept (no block is that small).</summary>
    public const int MinFreeRange = 32;

    /// <summary>Freed ranges of up to this many bytes are kept by size, in 16-byte classes; larger ones in one list.</summary>
    private const int SmallFreeLimit = 4096;

    private readonly Stack<int>[] _freeBySize = new Stack<int>[SmallFreeLimit / 16 + 1];
    private readonly List<(int Start, int Size)> _freeLarge = [];
    private int _freeCount;
    private long _freeBytes;

    /// <summary>Bytes of the arena given back with <see cref="Free"/> and not reused yet.</summary>
    public long FreeBytes => Interlocked.Read(ref _freeBytes);

    /// <summary>
    /// Gives back [<paramref name="execAddr"/>, + <paramref name="size"/>), code that nothing can run any more and no
    /// thread is inside (a block translated ahead that was never published): later placements with reuse may take it.
    /// The range stays committed (its pages are reused, not returned).
    /// </summary>
    public void Free(byte* execAddr, int size)
    {
        long offset = execAddr - _exec;
        if (offset < 0 || offset + size > _used || (offset & 15) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(execAddr));
        }

        if (size < MinFreeRange)
        {
            return;
        }

        size &= ~15;
        lock (_lock)
        {
            if (size <= SmallFreeLimit)
            {
                (_freeBySize[size / 16] ??= new Stack<int>()).Push((int)offset);
            }
            else
            {
                _freeLarge.Add(((int)offset, size));
            }

            _freeCount++;
            _freeBytes += size;
        }
    }

    /// <summary>The start of a freed range that holds <paramref name="size"/> bytes (the smallest such class first),
    /// its rest freed again; -1 when none (under the lock).</summary>
    private int TakeFreed(int size)
    {
        if (_freeCount == 0)
        {
            return -1;
        }

        int need = (size + 15) & ~15;
        int start = -1;
        int found = 0;
        if (need <= SmallFreeLimit)
        {
            for (int sizeClass = need / 16; sizeClass < _freeBySize.Length; sizeClass++)
            {
                if (_freeBySize[sizeClass] is { Count: > 0 } ranges)
                {
                    start = ranges.Pop();
                    found = sizeClass * 16;
                    break;
                }
            }
        }

        if (start < 0)
        {
            for (int i = 0; i < _freeLarge.Count; i++)
            {
                if (_freeLarge[i].Size >= need)
                {
                    (start, found) = _freeLarge[i];
                    _freeLarge[i] = _freeLarge[^1];
                    _freeLarge.RemoveAt(_freeLarge.Count - 1);
                    break;
                }
            }
        }

        if (start < 0)
        {
            return -1;
        }

        _freeCount--;
        _freeBytes -= found;
        int rest = found - need;
        if (rest >= MinFreeRange)
        {
            if (rest <= SmallFreeLimit)
            {
                (_freeBySize[rest / 16] ??= new Stack<int>()).Push(start + need);
            }
            else
            {
                _freeLarge.Add((start + need, rest));
            }

            _freeCount++;
            _freeBytes += rest;
        }

        return start;
    }

    /// <summary>Moves the end of the used range past [start, start + size), committing what it needs (under the lock).</summary>
    private void Claim(int start, int size)
    {
        if (start + size > ReserveSize)
        {
            throw new OutOfMemoryException("JIT code arena full");
        }

        while (start + size > _committed)
        {
            CommitMore();
        }

        Volatile.Write(ref _used, start + size);
    }

    /// <summary>Copies code to a range obtained from <see cref="Allocate"/> and makes it visible to instruction fetch.</summary>
    public void Write(byte* execAddr, ReadOnlySpan<byte> code)
    {
        long off = execAddr - _exec;
        if (off < 0 || off + code.Length > _used)
        {
            throw new ArgumentOutOfRangeException(nameof(execAddr));
        }

        if (_mode == Mode.AppleJit)
        {
            AppleWriteProtect(false);
        }

        try
        {
            code.CopyTo(new Span<byte>(_write + off, code.Length));
        }
        finally
        {
            if (_mode == Mode.AppleJit)
            {
                AppleWriteProtect(true);
            }
        }
        FlushInstructionCache(execAddr, code.Length);
    }

    /// <summary>Replaces one aligned instruction word of placed code (link patching) atomically and makes it visible to
    /// instruction fetch.</summary>
    public void PatchWord(byte* execAddr, uint word)
    {
        long off = execAddr - _exec;
        if (off < 0 || off + 4 > _used || (off & 3) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(execAddr));
        }

        if (_mode == Mode.AppleJit)
        {
            AppleWriteProtect(false);
        }

        try
        {
            Volatile.Write(ref *(uint*)(_write + off), word);
        }
        finally
        {
            if (_mode == Mode.AppleJit)
            {
                AppleWriteProtect(true);
            }
        }
        FlushInstructionCache(execAddr, 4);
    }

    /// <summary>Forgets every block (process exec): only valid when no thread can run generated code.</summary>
    public void Reset(int keepBytes)
    {
        lock (_lock)
        {
            _used = keepBytes;
            foreach (Stack<int>? ranges in _freeBySize)
            {
                ranges?.Clear();
            }

            _freeLarge.Clear();
            _freeCount = 0;
            _freeBytes = 0;
        }

        HostFaultAborts.ForgetFrom((ulong)(_exec + keepBytes)); // the code there is gone with its fault sites
    }

    private void CommitMore()
    {
        int next = Math.Min(_committed + CommitGranule, ReserveSize);
        if (OperatingSystem.IsWindows())
        {
            // views of a SEC_RESERVE section: commit through one view commits the shared pages for both
            if (VirtualAlloc(_write + _committed, (nuint)(next - _committed), MEM_COMMIT, PAGE_READWRITE) == null)
            {
                throw new OutOfMemoryException($"JIT arena commit failed ({Marshal.GetLastPInvokeError()})");
            }
        }
        _committed = next;
    }

    private void FlushInstructionCache(byte* addr, int len)
    {
        if (OperatingSystem.IsWindows())
        {
            FlushInstructionCache(GetCurrentProcess(), addr, (nuint)len);
        }
        else if (OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
        {
            sys_icache_invalidate(addr, (nuint)len);
        }
        else if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
        {
            ClearCache(addr, len);
        }
        // x64 keeps instruction fetch coherent with stores
    }

    private static delegate* unmanaged<void*, nuint, void> s_clearCache;
    private static readonly object s_clearCacheLock = new();

    /// <summary>
    /// Linux/Android ARM64: __clear_cache has no libc export, so the backend generates it
    /// (<see cref="Arm64Emitter.EmitCacheFlush"/>) into a page of its own, made executable with mprotect before its
    /// first use (the kernel synchronizes the caches when a page becomes executable).
    /// </summary>
    private static void ClearCache(byte* addr, int len)
    {
        delegate* unmanaged<void*, nuint, void> f = s_clearCache;
        if (f == null)
        {
            lock (s_clearCacheLock)
            {
                if (s_clearCache == null)
                {
                    byte[] code = Arm64Emitter.EmitCacheFlush();
                    nuint page = (nuint)Environment.SystemPageSize;
                    void* p = mmap(null, page, PROT_READ | PROT_WRITE, MAP_PRIVATE | MAP_ANON_LINUX, -1, 0);
                    if (p == (void*)-1)
                    {
                        throw new InvalidOperationException("mmap for the cache flush helper failed");
                    }

                    code.CopyTo(new Span<byte>(p, code.Length));
                    if (mprotect(p, page, PROT_READ | PROT_EXEC) != 0)
                    {
                        throw new InvalidOperationException("mprotect for the cache flush helper failed");
                    }

                    s_clearCache = (delegate* unmanaged<void*, nuint, void>)p;
                }
                f = s_clearCache;
            }
        }
        f(addr, (nuint)len);
    }

    // ------------------------------------------------------------------ Windows

    private const uint MEM_COMMIT = 0x1000, MEM_RESERVE = 0x2000, PAGE_READWRITE = 4, PAGE_EXECUTE_READWRITE = 0x40, PAGE_EXECUTE_READ = 0x20;
    private const uint SEC_RESERVE = 0x4000000, FILE_MAP_WRITE = 2, FILE_MAP_READ = 4, FILE_MAP_EXECUTE = 0x20;

    private void CreateWindows()
    {
        _section = CreateFileMappingW(-1, null, PAGE_EXECUTE_READWRITE | SEC_RESERVE, 0, ReserveSize, null);
        if (_section == 0)
        {
            throw new InvalidOperationException($"CreateFileMapping failed ({Marshal.GetLastPInvokeError()})");
        }

        _write = (byte*)MapViewOfFile(_section, FILE_MAP_WRITE, 0, 0, ReserveSize);
        _exec = (byte*)MapViewOfFile(_section, FILE_MAP_READ | FILE_MAP_EXECUTE, 0, 0, ReserveSize);
        if (_write == null || _exec == null)
        {
            throw new InvalidOperationException($"MapViewOfFile failed ({Marshal.GetLastPInvokeError()})");
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint CreateFileMappingW(nint file, void* attributes, uint protect, uint sizeHigh, uint sizeLow, char* name);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial void* MapViewOfFile(nint mapping, uint access, uint offHigh, uint offLow, nuint size);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnmapViewOfFile(void* addr);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint h);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial void* VirtualAlloc(void* addr, nuint size, uint type, uint protect);
    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FlushInstructionCache(nint process, void* addr, nuint size);
    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

    // ------------------------------------------------------------------ POSIX

    private const int PROT_NONE = 0, PROT_READ = 1, PROT_WRITE = 2, PROT_EXEC = 4;
    private const int MAP_SHARED = 1, MAP_PRIVATE = 2, MAP_ANON_LINUX = 0x20, MAP_ANON_MAC = 0x1000, MAP_JIT = 0x800;

    [LibraryImport("libc", SetLastError = true)]
    private static partial void* mmap(void* addr, nuint len, int prot, int flags, int fd, nint off);
    [LibraryImport("libc", SetLastError = true)]
    private static partial int munmap(void* addr, nuint len);
    [LibraryImport("libc", SetLastError = true)]
    private static partial int ftruncate(int fd, nint length);
    [LibraryImport("libc", SetLastError = true)]
    private static partial int close(int fd);
    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int memfd_create(string name, uint flags);
    [LibraryImport("libc")]
    private static partial void pthread_jit_write_protect_np(int enabled);
    [LibraryImport("libc")]
    private static partial void sys_icache_invalidate(void* start, nuint len);
    [LibraryImport("libc", SetLastError = true)]
    private static partial int mprotect(void* addr, nuint len, int prot);
    [LibraryImport("libandroid", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int ASharedMemory_create(string name, nuint size);

    private bool TryCreateLinux()
    {
        int fd = -1;
        try
        {
            fd = memfd_create("vitasharp-jit", 1 /*MFD_CLOEXEC*/);
        }
        catch (EntryPointNotFoundException) { }
        if (fd >= 0 && ftruncate(fd, ReserveSize) != 0)
        {
            close(fd);
            fd = -1;
        }
        // Android before API 30 has no memfd_create in bionic: an ashmem region (API 26+) is mappable twice too
        if (fd < 0 && OperatingSystem.IsAndroid())
        {
            try
            {
                fd = ASharedMemory_create("vitasharp-jit", ReserveSize);
            }
            catch (Exception e) when (e is EntryPointNotFoundException or DllNotFoundException) { }
        }
        if (fd < 0)
        {
            return false;
        }

        void* w = mmap(null, ReserveSize, PROT_READ | PROT_WRITE, MAP_SHARED, fd, 0);
        void* x = mmap(null, ReserveSize, PROT_READ | PROT_EXEC, MAP_SHARED, fd, 0);
        if (w == (void*)-1 || x == (void*)-1)
        {
            if (w != (void*)-1)
            {
                munmap(w, ReserveSize);
            }

            if (x != (void*)-1)
            {
                munmap(x, ReserveSize);
            }

            close(fd);
            return false;
        }
        _write = (byte*)w;
        _exec = (byte*)x;
        _section = fd;
        return true;
    }

    private void CreateApple()
    {
        void* p = mmap(null, ReserveSize, PROT_READ | PROT_WRITE | PROT_EXEC, MAP_PRIVATE | MAP_ANON_MAC | MAP_JIT, -1, 0);
        if (p == (void*)-1)
        {
            throw new InvalidOperationException("mmap(MAP_JIT) failed");
        }

        _exec = _write = (byte*)p;
    }

    private void CreateRwx()
    {
        void* p = mmap(null, ReserveSize, PROT_READ | PROT_WRITE | PROT_EXEC, MAP_PRIVATE | MAP_ANON_LINUX, -1, 0);
        if (p == (void*)-1)
        {
            throw new InvalidOperationException("mmap(RWX) failed");
        }

        _exec = _write = (byte*)p;
    }

    private static void AppleWriteProtect(bool enable)
    {
        // only Apple silicon enforces per-thread JIT write protection; Intel Macs map MAP_JIT memory RWX
        if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
        {
            pthread_jit_write_protect_np(enable ? 1 : 0);
        }
    }

    public void Dispose()
    {
        if (_exec == null)
        {
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            UnmapViewOfFile(_exec);
            UnmapViewOfFile(_write);
            CloseHandle(_section);
        }
        else
        {
            munmap(_exec, ReserveSize);
            if (_write != _exec)
            {
                munmap(_write, ReserveSize);
            }

            if (_mode == Mode.DualMapped)
            {
                close((int)_section);
            }
        }
        _exec = _write = null;
    }
}
