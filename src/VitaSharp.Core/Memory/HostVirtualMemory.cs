using System.Runtime.InteropServices;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Memory;

/// <summary>OS virtual memory primitives (Windows + POSIX so the core stays portable to Android).</summary>
internal static unsafe partial class HostVirtualMemory
{
    private const uint MEM_COMMIT = 0x1000, MEM_RESERVE = 0x2000, MEM_RELEASE = 0x8000, MEM_DECOMMIT = 0x4000;
    private const uint PAGE_NOACCESS = 1, PAGE_READWRITE = 4, PAGE_EXECUTE_READWRITE = 0x40;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial void* VirtualAlloc(void* addr, nuint size, uint type, uint protect);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool VirtualFree(void* addr, nuint size, uint type);

    [LibraryImport("kernel32.dll")]
    private static partial nint AddVectoredExceptionHandler(uint first, void* handler);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FlushInstructionCache(nint process, void* addr, nuint size);

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint GetModuleHandleW(string name);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint GetProcAddress(nint module, string name);

    // POSIX
    [LibraryImport("libc", SetLastError = true)]
    private static partial void* mmap(void* addr, nuint len, int prot, int flags, int fd, nint off);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int munmap(void* addr, nuint len);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int mprotect(void* addr, nuint len, int prot);

    private const int PROT_NONE = 0, PROT_READ = 1, PROT_WRITE = 2;
    private const int MAP_PRIVATE = 2, MAP_ANONYMOUS_LINUX = 0x20, MAP_NORESERVE_LINUX = 0x4000, MAP_ANON_MAC = 0x1000;

    /// <summary>Reserve address space. On POSIX it is mapped read/write with NORESERVE (lazy backing).</summary>
    public static byte* Reserve(nuint size, out bool lazyCommitted)
    {
        if (OperatingSystem.IsWindows())
        {
            lazyCommitted = false;
            void* p = VirtualAlloc(null, size, MEM_RESERVE, PAGE_NOACCESS);
            if (p == null)
            {
                throw new OutOfMemoryException($"VirtualAlloc reserve failed ({Marshal.GetLastPInvokeError()})");
            }

            return (byte*)p;
        }
        lazyCommitted = true;
        int flags = MAP_PRIVATE | (OperatingSystem.IsMacOS() ? MAP_ANON_MAC : MAP_ANONYMOUS_LINUX | MAP_NORESERVE_LINUX);
        void* m = mmap(null, size, PROT_READ | PROT_WRITE, flags, -1, 0);
        if (m == (void*)-1)
        {
            throw new OutOfMemoryException("mmap reserve failed");
        }

        return (byte*)m;
    }

    public static void Commit(byte* addr, nuint size)
    {
        if (OperatingSystem.IsWindows())
        {
            if (VirtualAlloc(addr, size, MEM_COMMIT, PAGE_READWRITE) == null)
            {
                throw new OutOfMemoryException($"VirtualAlloc commit failed ({Marshal.GetLastPInvokeError()})");
            }
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool VirtualProtect(void* addr, nuint size, uint protect, uint* old);

    /// <summary>
    /// Windows: the committed pages of [<paramref name="addr"/>, + <paramref name="size"/>) (a view of shared memory, which
    /// cannot be decommitted through one of its views) become inaccessible (<paramref name="accessible"/> = false) or
    /// read/write again; pages not committed are left alone (they fault already, and commit read/write). Other hosts:
    /// nothing (their reservations are never made inaccessible).
    /// </summary>
    public static void ProtectCommitted(byte* addr, nuint size, bool accessible)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        nuint at = (nuint)addr, end = at + size;
        while (at < end)
        {
            MemoryBasicInformation info;
            if (VirtualQuery((void*)at, &info, (nuint)sizeof(MemoryBasicInformation)) == 0)
            {
                return;
            }

            nuint regionEnd = Math.Min(end, (nuint)info.BaseAddress + info.RegionSize);
            if (info.State == MEM_COMMIT)
            {
                uint old;
                VirtualProtect((void*)at, regionEnd - at, accessible ? PAGE_READWRITE : PAGE_NOACCESS, &old);
            }

            at = regionEnd;
        }
    }

    public static void Decommit(byte* addr, nuint size)
    {
        if (OperatingSystem.IsWindows())
        {
            VirtualFree(addr, size, MEM_DECOMMIT);
        }
        else
        {
            // Drop the whole host pages inside the range by remapping fresh zero pages over them, and clear the
            // partial pages at the edges by hand: guest pages are 4 KB but host pages can be 16 KB (Apple Silicon,
            // some arm64 Linux kernels), and remapping a whole host page would wipe neighbouring live blocks.
            nuint page = (nuint)Environment.SystemPageSize;
            nuint start = (nuint)addr, end = start + size;
            nuint innerStart = (start + page - 1) & ~(page - 1), innerEnd = end & ~(page - 1);
            if (innerEnd > innerStart)
            {
                int flags = MAP_PRIVATE | 0x10 /*MAP_FIXED*/ | (OperatingSystem.IsMacOS() ? MAP_ANON_MAC : MAP_ANONYMOUS_LINUX | MAP_NORESERVE_LINUX);
                if (mmap((void*)innerStart, innerEnd - innerStart, PROT_READ | PROT_WRITE, flags, -1, 0) == (void*)-1)
                {
                    Clear(innerStart, innerEnd);
                }

                Clear(start, innerStart);
                Clear(innerEnd, end);
            }
            else
            {
                Clear(start, end);
            }
        }
    }

    /// <summary>
    /// Freed pages of shared memory (a view several reservations map, which cannot be decommitted through one of them):
    /// their content is no longer needed. Linux and Android punch them out of the memory file (the RAM goes back to the
    /// system and every view reads zeros); Windows marks them MEM_RESET (the OS may drop them instead of paging them
    /// out; they read back as garbage or zeros). <see cref="ZeroSharedPages"/> makes the next block there read zero.
    /// </summary>
    public static void DiscardShared(nint memory, ulong offset, byte* addr, nuint size)
    {
        const uint MEM_RESET = 0x80000;
        if (OperatingSystem.IsWindows())
        {
            VirtualAlloc(addr, size, MEM_RESET, PAGE_NOACCESS);
        }
        else if (OperatingSystem.IsLinux() || OperatingSystem.IsAndroid())
        {
            PunchHole(memory, offset, size);
        }
    }

    /// <summary>
    /// Makes [<paramref name="offset"/>, + <paramref name="size"/>) of shared memory <paramref name="memory"/> (mapped at
    /// <paramref name="addr"/> in the calling reservation) read zero in every view, without making pages that already
    /// read zero resident: Windows clears by hand only the pages of the section that were ever committed (by a block or
    /// by a stray access through the lazy commit handler); a page of a SEC_RESERVE section never committed is still the
    /// demand-zero page. Linux and Android punch the range out of the memory file (FALLOC_FL_PUNCH_HOLE: holes read
    /// zero and take no RAM; byte exact, so neighbouring blocks in the same host page keep their data). False when the
    /// host has no way to do it (macOS shm objects have no hole punching): the caller clears what may hold data.
    /// </summary>
    public static bool ZeroSharedPages(nint memory, ulong offset, byte* addr, nuint size)
    {
        if (OperatingSystem.IsWindows())
        {
            ClearCommittedPages(addr, size);
            return true;
        }

        if (OperatingSystem.IsLinux() || OperatingSystem.IsAndroid())
        {
            return PunchHole(memory, offset, size);
        }

        return false;
    }

    /// <summary>Windows: clears the committed parts of [<paramref name="addr"/>, + <paramref name="size"/>).</summary>
    private static void ClearCommittedPages(byte* addr, nuint size)
    {
        nuint at = (nuint)addr, end = at + size;
        while (at < end)
        {
            MemoryBasicInformation info;
            if (VirtualQuery((void*)at, &info, (nuint)sizeof(MemoryBasicInformation)) == 0)
            {
                // no answer: clear the rest by hand (commit first, a clear of reserved pages would fault)
                Commit((byte*)at, end - at);
                Clear(at, end);
                return;
            }

            nuint regionEnd = Math.Min(end, (nuint)info.BaseAddress + info.RegionSize);
            if (info.State == MEM_COMMIT)
            {
                Clear(at, regionEnd);
            }

            at = regionEnd;
        }
    }

    /// <summary>Linux, Android: punches [offset, + size) out of the memory file; false when the kernel refuses.</summary>
    private static bool PunchHole(nint memory, ulong offset, nuint size)
    {
        const int FALLOC_FL_KEEP_SIZE = 1, FALLOC_FL_PUNCH_HOLE = 2;
        try
        {
            return fallocate((int)memory - 1, FALLOC_FL_KEEP_SIZE | FALLOC_FL_PUNCH_HOLE, (long)offset, (long)size) == 0;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>
    /// macOS (and Linux without hole punching): true when the host page holding <paramref name="addr"/> is in memory, so it
    /// may hold data although no block was ever there (a stray access wrote it). False also when the query fails.
    /// </summary>
    public static bool IsResident(byte* addr)
    {
        if (OperatingSystem.IsWindows())
        {
            return true;
        }

        nuint page = (nuint)Environment.SystemPageSize;
        byte vector = 0;
        void* start = (void*)((nuint)addr & ~(page - 1));
        try
        {
            return mincore(start, page, &vector) == 0 && (vector & 1) != 0;
        }
        catch (EntryPointNotFoundException)
        {
            return true;
        }
    }

    [LibraryImport("kernel32.dll")]
    private static partial nuint VirtualQuery(void* addr, MemoryBasicInformation* info, nuint length);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryBasicInformation
    {
        public nint BaseAddress;
        public nint AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public nuint RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    [LibraryImport("libc", SetLastError = true)]
    private static partial int fallocate(int fd, int mode, long offset, long length);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int mincore(void* addr, nuint length, byte* vector);

    private static void Clear(nuint from, nuint to)
    {
        if (to > from)
        {
            NativeMemory.Clear((void*)from, to - from);
        }
    }

    public static void Release(byte* addr, nuint size)
    {
        if (OperatingSystem.IsWindows())
        {
            VirtualFree(addr, 0, MEM_RELEASE);
        }
        else
        {
            munmap(addr, size);
        }
    }

    // ------------------------------------------------------------------ shared regions (several views of one memory)

    private const uint MEM_RESERVE_PLACEHOLDER = 0x00040000, MEM_REPLACE_PLACEHOLDER = 0x00004000, MEM_PRESERVE_PLACEHOLDER = 0x2;
    private const uint SEC_RESERVE = 0x04000000;

    [LibraryImport("kernelbase.dll", SetLastError = true)]
    private static partial void* VirtualAlloc2(nint process, void* baseAddress, nuint size, uint allocationType, uint pageProtection, void* extendedParameters, uint parameterCount);

    [LibraryImport("kernelbase.dll", SetLastError = true)]
    private static partial void* MapViewOfFile3(nint fileMapping, nint process, void* baseAddress, ulong offset, nuint viewSize, uint allocationType, uint pageProtection, void* extendedParameters, uint parameterCount);

    [LibraryImport("kernelbase.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnmapViewOfFile2(nint process, void* baseAddress, uint unmapFlags);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateFileMappingW(nint file, void* attributes, uint protect, uint maximumSizeHigh, uint maximumSizeLow, string? name);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [LibraryImport("libc", SetLastError = true, EntryPoint = "memfd_create", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int memfd_create(string name, uint flags);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int ftruncate(int fd, long length);

    // shm_open(name, oflag, ...) is variadic: on Apple arm64 the variadic mode goes on the stack, where a ninth
    // integer argument also goes (x0-x7 hold the first eight); elsewhere (x86-64) it is the third register
    [LibraryImport("libc", SetLastError = true, EntryPoint = "shm_open", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int shm_open_arm64(string name, int oflag, nint x2, nint x3, nint x4, nint x5, nint x6, nint x7, nint mode);

    [LibraryImport("libc", SetLastError = true, EntryPoint = "shm_open", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int shm_open_x64(string name, int oflag, nint mode);

    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int shm_unlink(string name);

    private const int O_RDWR_MAC = 2, O_CREAT_MAC = 0x200, O_EXCL_MAC = 0x800;

    /// <summary>
    /// macOS: an anonymous POSIX shared memory object (shm_open with a unique name, unlinked at once so only the
    /// descriptor holds it). Its pages are ordinary anonymous memory (swap backed, never written to a file), what memfd
    /// is on Linux. -1 when the host refuses it.
    /// </summary>
    private static int CreateMacSharedMemory(string tag)
    {
        // names are at most 31 bytes (PSHMNAMLEN)
        string name = $"/vs{Environment.ProcessId:x}{tag[..Math.Min(tag.Length, 6)]}{Random.Shared.Next():x8}";
        int oflag = O_RDWR_MAC | O_CREAT_MAC | O_EXCL_MAC;
        const nint OwnerReadWrite = 0x180; // 0600
        int fd = RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? shm_open_arm64(name, oflag, 0, 0, 0, 0, 0, 0, OwnerReadWrite)
            : shm_open_x64(name, oflag, OwnerReadWrite);
        if (fd >= 0)
        {
            shm_unlink(name);
        }
        return fd;
    }

    [LibraryImport("libc", SetLastError = true)]
    private static partial int close(int fd);

    private const int MAP_SHARED = 1, MAP_FIXED = 0x10;

    /// <summary>
    /// Memory that several reservations map at once (<see cref="ReserveWithViews"/>): a pagefile-backed section whose pages
    /// are committed on demand (Windows), an anonymous memory file (Linux and Android memfd) or an unlinked POSIX shared
    /// memory object (macOS). Returns 0 when the host cannot provide it.
    /// </summary>
    public static nint CreateSharedMemory(ulong size, string tag)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                nint h = CreateFileMappingW(-1, null, PAGE_READWRITE | SEC_RESERVE, (uint)(size >> 32), (uint)size, null);
                return h;
            }
            // anonymous shared memory: memfd (Linux, Android), an unlinked shm object (macOS; not a temporary file, whose
            // dirty pages would be written back to disk, which CDRAM traffic must not cost). Elsewhere all processes
            // share one space
            int fd = -1;
            if (OperatingSystem.IsLinux() || OperatingSystem.IsAndroid())
            {
                try
                {
                    fd = memfd_create("vitasharp-" + tag, 1 /*MFD_CLOEXEC*/);
                }
                catch (EntryPointNotFoundException)
                {
                    fd = -1;
                }
            }
            else if (OperatingSystem.IsMacOS())
            {
                fd = CreateMacSharedMemory(tag);
            }
            if (fd < 0)
            {
                return 0;
            }

            if (ftruncate(fd, (long)size) != 0)
            {
                close(fd);
                return 0;
            }
            return fd + 1; // 0 means "none"
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return 0;
        }
    }

    /// <summary>One shared memory mapped at <paramref name="Offset"/> of a reservation.</summary>
    public readonly record struct View(nuint Offset, nuint Size, nint Memory);

    /// <summary>
    /// Reserves <paramref name="size"/> bytes like <see cref="Reserve"/>, with the shared memories of
    /// <paramref name="views"/> (sorted, 64 KB aligned, not overlapping) mapped at their offsets. On Windows the
    /// reservation is split with placeholders: the views replace their placeholders and the gaps become ordinary reserved
    /// memory (<paramref name="parts"/> lists every piece for <see cref="ReleaseWithViews"/>). Returns null when the host
    /// cannot do it (the caller then falls back to a single reservation).
    /// </summary>
    public static byte* ReserveWithViews(nuint size, ReadOnlySpan<View> views, out bool lazyCommitted, out (nint Address, nuint Size, bool View)[] parts)
    {
        parts = [];
        lazyCommitted = !OperatingSystem.IsWindows();
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return ReserveWithViewsWindows(size, views, out parts);
            }

            byte* p = Reserve(size, out lazyCommitted);
            foreach (View v in views)
            {
                void* m = mmap(p + v.Offset, v.Size, PROT_READ | PROT_WRITE, MAP_SHARED | MAP_FIXED, (int)v.Memory - 1, 0);
                if (m == (void*)-1)
                {
                    munmap(p, size);
                    return null;
                }
            }
            parts = [((nint)p, size, false)];
            return p;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    private static byte* ReserveWithViewsWindows(nuint size, ReadOnlySpan<View> views, out (nint Address, nuint Size, bool View)[] parts)
    {
        parts = [];
        nint proc = GetCurrentProcess();
        byte* p = (byte*)VirtualAlloc2(proc, null, size, MEM_RESERVE | MEM_RESERVE_PLACEHOLDER, PAGE_NOACCESS, null, 0);
        if (p == null)
        {
            return null;
        }

        var list = new List<(nint, nuint, bool)>();
        nuint at = 0;
        bool ok = true;
        foreach (View v in views)
        {
            // split the placeholder that holds the view's range off, and map the shared memory in its place
            if (!VirtualFree(p + v.Offset, v.Size, MEM_RELEASE | MEM_PRESERVE_PLACEHOLDER)
                || MapViewOfFile3(v.Memory, proc, p + v.Offset, 0, v.Size, MEM_REPLACE_PLACEHOLDER, PAGE_READWRITE, null, 0) == null)
            {
                ok = false;
                break;
            }
            if (v.Offset > at)
            {
                list.Add(((nint)(p + at), v.Offset - at, false));
            }

            list.Add(((nint)(p + v.Offset), v.Size, true));
            at = v.Offset + v.Size;
        }
        if (ok && at < size)
        {
            list.Add(((nint)(p + at), size - at, false));
        }
        // the remaining placeholders become ordinary reserved memory (committed by VirtualAlloc and the lazy handler)
        foreach ((nint a, nuint len, bool view) in list)
        {
            if (ok && !view && VirtualAlloc2(proc, (void*)a, len, MEM_RESERVE | MEM_REPLACE_PLACEHOLDER, PAGE_NOACCESS, null, 0) == null)
            {
                ok = false;
            }
        }

        parts = list.ToArray();
        if (ok)
        {
            return p;
        }

        Log.Warn($"Address space with shared views: Windows placeholder mapping failed ({Marshal.GetLastPInvokeError()})");
        ReleaseWithViews(parts);
        return null;
    }

    /// <summary>Releases a reservation made by <see cref="ReserveWithViews"/>.</summary>
    public static void ReleaseWithViews((nint Address, nuint Size, bool View)[] parts)
    {
        foreach ((nint a, nuint len, bool view) in parts)
        {
            if (OperatingSystem.IsWindows())
            {
                if (view)
                {
                    UnmapViewOfFile2(GetCurrentProcess(), (void*)a, 0);
                }
                else
                {
                    VirtualFree((void*)a, 0, MEM_RELEASE);
                }
            }
            else
            {
                munmap((void*)a, len);
            }
        }
    }

    public static void ReleaseSharedMemory(nint memory)
    {
        if (memory == 0)
        {
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            CloseHandle(memory);
        }
        else
        {
            close((int)memory - 1);
        }
    }

    public static byte* AllocExecutable(int size)
    {
        if (OperatingSystem.IsWindows())
        {
            return (byte*)VirtualAlloc(null, (nuint)size, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
        }

        throw new PlatformNotSupportedException();
    }

    /// <summary>
    /// Installs a native (hand-assembled x64, or generated ARM64 on Windows on ARM) vectored exception handler that commits zeroed pages on
    /// access violations inside [base, base+size). Pure machine code so it never re-enters the CLR
    /// while the faulting thread is in cooperative mode.
    /// </summary>
    public static bool InstallLazyCommitHandler(byte* @base, ulong size)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
        {
            nint vaArm = GetProcAddress(GetModuleHandleW("kernel32.dll"), "VirtualAlloc");
            if (vaArm == 0)
            {
                return false;
            }

            byte[] handler = Cpu.Native.Arm64Emitter.EmitLazyCommitHandler((ulong)@base, size, (ulong)vaArm);
            byte* execArm = AllocExecutable(handler.Length);
            if (execArm == null)
            {
                return false;
            }

            handler.AsSpan().CopyTo(new Span<byte>(execArm, handler.Length));
            FlushInstructionCache(GetCurrentProcess(), execArm, (nuint)handler.Length);
            return AddVectoredExceptionHandler(1, execArm) != 0;
        }
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            return false;
        }

        nint va = GetProcAddress(GetModuleHandleW("kernel32.dll"), "VirtualAlloc");
        if (va == 0)
        {
            return false;
        }

        nint vp = GetProcAddress(GetModuleHandleW("kernel32.dll"), "VirtualProtect");
        if (vp == 0)
        {
            return false;
        }

        byte[] arr = Cpu.Native.X64Emitter.EmitLazyCommitHandler((ulong)@base, size, (ulong)va, (ulong)vp);
        byte* exec = AllocExecutable(arr.Length);
        if (exec == null)
        {
            return false;
        }

        arr.AsSpan().CopyTo(new Span<byte>(exec, arr.Length));
        return AddVectoredExceptionHandler(1, exec) != 0;
    }
}
