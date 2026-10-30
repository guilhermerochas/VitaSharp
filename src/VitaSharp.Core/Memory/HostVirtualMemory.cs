using System.Runtime.InteropServices;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Memory;

/// <summary>OS virtual memory primitives (Windows + POSIX so the core stays portable to Android).</summary>
internal static unsafe partial class HostVirtualMemory
{
    const uint MEM_COMMIT = 0x1000, MEM_RESERVE = 0x2000, MEM_RELEASE = 0x8000, MEM_DECOMMIT = 0x4000;
    const uint PAGE_NOACCESS = 1, PAGE_READWRITE = 4, PAGE_EXECUTE_READWRITE = 0x40;

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

    const int PROT_NONE = 0, PROT_READ = 1, PROT_WRITE = 2;
    const int MAP_PRIVATE = 2, MAP_ANONYMOUS_LINUX = 0x20, MAP_NORESERVE_LINUX = 0x4000, MAP_ANON_MAC = 0x1000;

    /// <summary>Reserve address space. On POSIX it is mapped read/write with NORESERVE (lazy backing).</summary>
    public static byte* Reserve(nuint size, out bool lazyCommitted)
    {
        if (OperatingSystem.IsWindows())
        {
            lazyCommitted = false;
            var p = VirtualAlloc(null, size, MEM_RESERVE, PAGE_NOACCESS);
            if (p == null) throw new OutOfMemoryException($"VirtualAlloc reserve failed ({Marshal.GetLastPInvokeError()})");
            return (byte*)p;
        }
        lazyCommitted = true;
        int flags = MAP_PRIVATE | (OperatingSystem.IsMacOS() ? MAP_ANON_MAC : MAP_ANONYMOUS_LINUX | MAP_NORESERVE_LINUX);
        var m = mmap(null, size, PROT_READ | PROT_WRITE, flags, -1, 0);
        if (m == (void*)-1) throw new OutOfMemoryException("mmap reserve failed");
        return (byte*)m;
    }

    public static void Commit(byte* addr, nuint size)
    {
        if (OperatingSystem.IsWindows())
        {
            if (VirtualAlloc(addr, size, MEM_COMMIT, PAGE_READWRITE) == null)
                throw new OutOfMemoryException($"VirtualAlloc commit failed ({Marshal.GetLastPInvokeError()})");
        }
    }

    public static void Decommit(byte* addr, nuint size)
    {
        if (OperatingSystem.IsWindows()) VirtualFree(addr, size, MEM_DECOMMIT);
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
                    Clear(innerStart, innerEnd);
                Clear(start, innerStart);
                Clear(innerEnd, end);
            }
            else Clear(start, end);
        }
    }

    /// <summary>
    /// Freed pages of shared memory (a view several reservations map, which cannot be decommitted through one of them):
    /// their content is no longer needed, so the OS may drop it instead of paging it out. They read back as garbage or
    /// zeros; allocations clear what they commit.
    /// </summary>
    public static void Discard(byte* addr, nuint size)
    {
        const uint MEM_RESET = 0x80000;
        if (OperatingSystem.IsWindows()) VirtualAlloc(addr, size, MEM_RESET, PAGE_NOACCESS);
    }

    static void Clear(nuint from, nuint to)
    {
        if (to > from) NativeMemory.Clear((void*)from, to - from);
    }

    public static void Release(byte* addr, nuint size)
    {
        if (OperatingSystem.IsWindows()) VirtualFree(addr, 0, MEM_RELEASE);
        else munmap(addr, size);
    }

    // ------------------------------------------------------------------ shared regions (several views of one memory)

    const uint MEM_RESERVE_PLACEHOLDER = 0x00040000, MEM_REPLACE_PLACEHOLDER = 0x00004000, MEM_PRESERVE_PLACEHOLDER = 0x2;
    const uint SEC_RESERVE = 0x04000000;

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

    [LibraryImport("libc", SetLastError = true)]
    private static partial int close(int fd);

    const int MAP_SHARED = 1, MAP_FIXED = 0x10;

    /// <summary>
    /// Memory that several reservations map at once (<see cref="ReserveWithViews"/>): a pagefile-backed section whose pages
    /// are committed on demand (Windows), or an anonymous memory file (Linux and Android memfd). Returns 0 when the host
    /// cannot provide it.
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
            // anonymous shared memory: memfd (Linux, Android). Elsewhere (macOS) a temporary file would do, but its dirty
            // pages are written back to disk, which CDRAM traffic must not cost: there all processes share one space
            int fd = -1;
            if (OperatingSystem.IsLinux() || OperatingSystem.IsAndroid())
            {
                try { fd = memfd_create("vitasharp-" + tag, 1 /*MFD_CLOEXEC*/); } catch (EntryPointNotFoundException) { fd = -1; }
            }
            if (fd < 0) return 0;
            if (ftruncate(fd, (long)size) != 0) { close(fd); return 0; }
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
            if (OperatingSystem.IsWindows()) return ReserveWithViewsWindows(size, views, out parts);
            byte* p = Reserve(size, out lazyCommitted);
            foreach (var v in views)
            {
                var m = mmap(p + v.Offset, v.Size, PROT_READ | PROT_WRITE, MAP_SHARED | MAP_FIXED, (int)v.Memory - 1, 0);
                if (m == (void*)-1) { munmap(p, size); return null; }
            }
            parts = [((nint)p, size, false)];
            return p;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    static byte* ReserveWithViewsWindows(nuint size, ReadOnlySpan<View> views, out (nint Address, nuint Size, bool View)[] parts)
    {
        parts = [];
        nint proc = GetCurrentProcess();
        byte* p = (byte*)VirtualAlloc2(proc, null, size, MEM_RESERVE | MEM_RESERVE_PLACEHOLDER, PAGE_NOACCESS, null, 0);
        if (p == null) return null;
        var list = new List<(nint, nuint, bool)>();
        nuint at = 0;
        bool ok = true;
        foreach (var v in views)
        {
            // split the placeholder that holds the view's range off, and map the shared memory in its place
            if (!VirtualFree(p + v.Offset, v.Size, MEM_RELEASE | MEM_PRESERVE_PLACEHOLDER)
                || MapViewOfFile3(v.Memory, proc, p + v.Offset, 0, v.Size, MEM_REPLACE_PLACEHOLDER, PAGE_READWRITE, null, 0) == null)
            {
                ok = false;
                break;
            }
            if (v.Offset > at) list.Add(((nint)(p + at), v.Offset - at, false));
            list.Add(((nint)(p + v.Offset), v.Size, true));
            at = v.Offset + v.Size;
        }
        if (ok && at < size) list.Add(((nint)(p + at), size - at, false));
        // the remaining placeholders become ordinary reserved memory (committed by VirtualAlloc and the lazy handler)
        foreach (var (a, len, view) in list)
            if (ok && !view && VirtualAlloc2(proc, (void*)a, len, MEM_RESERVE | MEM_REPLACE_PLACEHOLDER, PAGE_NOACCESS, null, 0) == null) ok = false;
        parts = list.ToArray();
        if (ok) return p;
        Log.Warn($"Address space with shared views: Windows placeholder mapping failed ({Marshal.GetLastPInvokeError()})");
        ReleaseWithViews(parts);
        return null;
    }

    /// <summary>Releases a reservation made by <see cref="ReserveWithViews"/>.</summary>
    public static void ReleaseWithViews((nint Address, nuint Size, bool View)[] parts)
    {
        foreach (var (a, len, view) in parts)
        {
            if (OperatingSystem.IsWindows())
            {
                if (view) UnmapViewOfFile2(GetCurrentProcess(), (void*)a, 0);
                else VirtualFree((void*)a, 0, MEM_RELEASE);
            }
            else munmap((void*)a, len);
        }
    }

    public static void ReleaseSharedMemory(nint memory)
    {
        if (memory == 0) return;
        if (OperatingSystem.IsWindows()) CloseHandle(memory);
        else close((int)memory - 1);
    }

    public static byte* AllocExecutable(int size)
    {
        if (OperatingSystem.IsWindows())
            return (byte*)VirtualAlloc(null, (nuint)size, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
        throw new PlatformNotSupportedException();
    }

    /// <summary>
    /// Installs a native (hand-assembled x64, or generated ARM64 on Windows on ARM) vectored exception handler that commits zeroed pages on
    /// access violations inside [base, base+size). Pure machine code so it never re-enters the CLR
    /// while the faulting thread is in cooperative mode.
    /// </summary>
    public static bool InstallLazyCommitHandler(byte* @base, ulong size)
    {
        if (!OperatingSystem.IsWindows()) return false;
        if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
        {
            nint vaArm = GetProcAddress(GetModuleHandleW("kernel32.dll"), "VirtualAlloc");
            if (vaArm == 0) return false;
            var handler = Cpu.Native.Arm64Emitter.EmitLazyCommitHandler((ulong)@base, size, (ulong)vaArm);
            byte* execArm = AllocExecutable(handler.Length);
            if (execArm == null) return false;
            handler.AsSpan().CopyTo(new Span<byte>(execArm, handler.Length));
            FlushInstructionCache(GetCurrentProcess(), execArm, (nuint)handler.Length);
            return AddVectoredExceptionHandler(1, execArm) != 0;
        }
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
            return false;

        nint va = GetProcAddress(GetModuleHandleW("kernel32.dll"), "VirtualAlloc");
        if (va == 0) return false;

        var code = new List<byte>();
        void B(params byte[] b) => code.AddRange(b);
        void Q(ulong v) => code.AddRange(BitConverter.GetBytes(v));

        B(0x48, 0x83, 0xEC, 0x28);                         // sub rsp, 40
        B(0x48, 0x8B, 0x01);                               // mov rax, [rcx]          ; ExceptionRecord
        B(0x81, 0x38, 0x05, 0x00, 0x00, 0xC0);             // cmp dword [rax], 0xC0000005
        int jne1 = code.Count; B(0x0F, 0x85, 0, 0, 0, 0);  // jne notours
        B(0x48, 0x8B, 0x50, 0x28);                         // mov rdx, [rax+0x28]     ; faulting address
        B(0x49, 0xB8); Q((ulong)@base);                    // mov r8, base
        B(0x4C, 0x29, 0xC2);                               // sub rdx, r8
        B(0x49, 0xB9); Q(size);                            // mov r9, size
        B(0x4C, 0x39, 0xCA);                               // cmp rdx, r9
        int jae = code.Count; B(0x0F, 0x83, 0, 0, 0, 0);   // jae notours
        B(0x48, 0x8B, 0x48, 0x28);                         // mov rcx, [rax+0x28]
        B(0x48, 0x81, 0xE1, 0x00, 0xF0, 0xFF, 0xFF);       // and rcx, -4096
        B(0xBA, 0x00, 0x10, 0x00, 0x00);                   // mov edx, 0x1000
        B(0x41, 0xB8, 0x00, 0x10, 0x00, 0x00);             // mov r8d, MEM_COMMIT
        B(0x41, 0xB9, 0x04, 0x00, 0x00, 0x00);             // mov r9d, PAGE_READWRITE
        B(0x48, 0xB8); Q((ulong)va);                       // mov rax, VirtualAlloc
        B(0xFF, 0xD0);                                     // call rax
        B(0x48, 0x85, 0xC0);                               // test rax, rax
        int jz = code.Count; B(0x0F, 0x84, 0, 0, 0, 0);    // jz notours
        B(0xB8, 0xFF, 0xFF, 0xFF, 0xFF);                   // mov eax, -1 (CONTINUE_EXECUTION)
        B(0x48, 0x83, 0xC4, 0x28);                         // add rsp, 40
        B(0xC3);                                           // ret
        int notours = code.Count;
        B(0x31, 0xC0);                                     // xor eax, eax (CONTINUE_SEARCH)
        B(0x48, 0x83, 0xC4, 0x28);                         // add rsp, 40
        B(0xC3);                                           // ret

        var arr = code.ToArray();
        foreach (int at in new[] { jne1, jae, jz })
            BitConverter.GetBytes(notours - (at + 6)).CopyTo(arr, at + 2);

        byte* exec = AllocExecutable(arr.Length);
        if (exec == null) return false;
        arr.AsSpan().CopyTo(new Span<byte>(exec, arr.Length));
        return AddVectoredExceptionHandler(1, exec) != 0;
    }
}
