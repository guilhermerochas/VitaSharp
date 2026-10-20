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
/// so other threads keep executing while the compiling thread writes.</item>
/// </list>
/// Code is placed with a bump allocator in a single reservation, so every block is within a rel32 jump of the others
/// and of the entry trampoline. Blocks are never freed individually (invalidated code stays mapped until
/// <see cref="Reset"/>, which the owner only calls when no guest thread runs).
/// </summary>
public sealed unsafe partial class ExecutableMemory : IDisposable
{
    public const int ReserveSize = 256 << 20;    // 256 MiB of address space
    const int CommitGranule = 1 << 20;            // committed (made usable) in 1 MiB steps

    readonly object _lock = new();
    byte* _exec;       // execute view
    byte* _write;      // write view (== _exec on macOS)
    int _used, _committed;
    nint _section;     // Windows section handle / Linux memfd
    readonly Mode _mode;

    enum Mode { DualMapped, AppleJit, Rwx }

    public byte* ExecBase => _exec;
    public int Used => _used;
    public string Description => _mode switch
    {
        Mode.DualMapped => "dual-mapped W^X",
        Mode.AppleJit => "MAP_JIT W^X",
        _ => "RWX (no W^X: dual mapping unavailable)",
    };

    public ExecutableMemory()
    {
        if (OperatingSystem.IsWindows()) { CreateWindows(); _mode = Mode.DualMapped; }
        else if (OperatingSystem.IsMacOS()) { CreateApple(); _mode = Mode.AppleJit; }
        else if (TryCreateLinux()) _mode = Mode.DualMapped;
        else { CreateRwx(); _mode = Mode.Rwx; }
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
            if (start + size > ReserveSize) throw new OutOfMemoryException("JIT code arena full");
            while (start + size > _committed) CommitMore();
            _used = start + size;
            return _exec + start;
        }
    }

    /// <summary>
    /// Places code whose encoding depends on its address: <paramref name="assemble"/> is called with the execute
    /// address the code will occupy (under the arena lock, so keep it to the final encoding step).
    /// </summary>
    public byte* Place(Func<ulong, byte[]> assemble)
    {
        lock (_lock)
        {
            int start = (_used + 15) & ~15;
            var code = assemble((ulong)(_exec + start));
            if (start + code.Length > ReserveSize) throw new OutOfMemoryException("JIT code arena full");
            while (start + code.Length > _committed) CommitMore();
            _used = start + code.Length;
            Write(_exec + start, code);
            return _exec + start;
        }
    }

    /// <summary>Copies code to a range obtained from <see cref="Allocate"/> and makes it visible to instruction fetch.</summary>
    public void Write(byte* execAddr, ReadOnlySpan<byte> code)
    {
        long off = execAddr - _exec;
        if (off < 0 || off + code.Length > _used) throw new ArgumentOutOfRangeException(nameof(execAddr));
        if (_mode == Mode.AppleJit) AppleWriteProtect(false);
        try { code.CopyTo(new Span<byte>(_write + off, code.Length)); }
        finally { if (_mode == Mode.AppleJit) AppleWriteProtect(true); }
        FlushInstructionCache(execAddr, code.Length);
    }

    /// <summary>Forgets every block (process exec): only valid when no thread can run generated code.</summary>
    public void Reset(int keepBytes)
    {
        lock (_lock) _used = keepBytes;
    }

    void CommitMore()
    {
        int next = Math.Min(_committed + CommitGranule, ReserveSize);
        if (OperatingSystem.IsWindows())
        {
            // views of a SEC_RESERVE section: commit through one view commits the shared pages for both
            if (VirtualAlloc(_write + _committed, (nuint)(next - _committed), MEM_COMMIT, PAGE_READWRITE) == null)
                throw new OutOfMemoryException($"JIT arena commit failed ({Marshal.GetLastPInvokeError()})");
        }
        _committed = next;
    }

    void FlushInstructionCache(byte* addr, int len)
    {
        if (OperatingSystem.IsWindows()) FlushInstructionCache(GetCurrentProcess(), addr, (nuint)len);
        else if (OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64) sys_icache_invalidate(addr, (nuint)len);
        // x64 keeps instruction fetch coherent with stores. A Linux ARM64 emitter would need __clear_cache here
        // (dc cvau + ic ivau per cache line), which has no libc export: emit it as a small generated helper.
    }

    // ------------------------------------------------------------------ Windows

    const uint MEM_COMMIT = 0x1000, MEM_RESERVE = 0x2000, PAGE_READWRITE = 4, PAGE_EXECUTE_READWRITE = 0x40, PAGE_EXECUTE_READ = 0x20;
    const uint SEC_RESERVE = 0x4000000, FILE_MAP_WRITE = 2, FILE_MAP_READ = 4, FILE_MAP_EXECUTE = 0x20;

    void CreateWindows()
    {
        _section = CreateFileMappingW(-1, null, PAGE_EXECUTE_READWRITE | SEC_RESERVE, 0, ReserveSize, null);
        if (_section == 0) throw new InvalidOperationException($"CreateFileMapping failed ({Marshal.GetLastPInvokeError()})");
        _write = (byte*)MapViewOfFile(_section, FILE_MAP_WRITE, 0, 0, ReserveSize);
        _exec = (byte*)MapViewOfFile(_section, FILE_MAP_READ | FILE_MAP_EXECUTE, 0, 0, ReserveSize);
        if (_write == null || _exec == null) throw new InvalidOperationException($"MapViewOfFile failed ({Marshal.GetLastPInvokeError()})");
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

    const int PROT_NONE = 0, PROT_READ = 1, PROT_WRITE = 2, PROT_EXEC = 4;
    const int MAP_SHARED = 1, MAP_PRIVATE = 2, MAP_ANON_LINUX = 0x20, MAP_ANON_MAC = 0x1000, MAP_JIT = 0x800;

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

    bool TryCreateLinux()
    {
        int fd;
        try { fd = memfd_create("vitasharp-jit", 1 /*MFD_CLOEXEC*/); }
        catch (EntryPointNotFoundException) { return false; }
        if (fd < 0) return false;
        if (ftruncate(fd, ReserveSize) != 0) { close(fd); return false; }
        var w = mmap(null, ReserveSize, PROT_READ | PROT_WRITE, MAP_SHARED, fd, 0);
        var x = mmap(null, ReserveSize, PROT_READ | PROT_EXEC, MAP_SHARED, fd, 0);
        if (w == (void*)-1 || x == (void*)-1)
        {
            if (w != (void*)-1) munmap(w, ReserveSize);
            if (x != (void*)-1) munmap(x, ReserveSize);
            close(fd);
            return false;
        }
        _write = (byte*)w; _exec = (byte*)x; _section = fd;
        return true;
    }

    void CreateApple()
    {
        var p = mmap(null, ReserveSize, PROT_READ | PROT_WRITE | PROT_EXEC, MAP_PRIVATE | MAP_ANON_MAC | MAP_JIT, -1, 0);
        if (p == (void*)-1) throw new InvalidOperationException("mmap(MAP_JIT) failed");
        _exec = _write = (byte*)p;
    }

    void CreateRwx()
    {
        var p = mmap(null, ReserveSize, PROT_READ | PROT_WRITE | PROT_EXEC, MAP_PRIVATE | MAP_ANON_LINUX, -1, 0);
        if (p == (void*)-1) throw new InvalidOperationException("mmap(RWX) failed");
        _exec = _write = (byte*)p;
    }

    static void AppleWriteProtect(bool enable)
    {
        // only Apple silicon enforces per-thread JIT write protection; Intel Macs map MAP_JIT memory RWX
        if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64) pthread_jit_write_protect_np(enable ? 1 : 0);
    }

    public void Dispose()
    {
        if (_exec == null) return;
        if (OperatingSystem.IsWindows())
        {
            UnmapViewOfFile(_exec);
            UnmapViewOfFile(_write);
            CloseHandle(_section);
        }
        else
        {
            munmap(_exec, ReserveSize);
            if (_write != _exec) munmap(_write, ReserveSize);
            if (_mode == Mode.DualMapped) close((int)_section);
        }
        _exec = _write = null;
    }
}
