using System.Runtime.InteropServices;

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

    static void Clear(nuint from, nuint to)
    {
        if (to > from) NativeMemory.Clear((void*)from, to - from);
    }

    public static void Release(byte* addr, nuint size)
    {
        if (OperatingSystem.IsWindows()) VirtualFree(addr, 0, MEM_RELEASE);
        else munmap(addr, size);
    }

    public static byte* AllocExecutable(int size)
    {
        if (OperatingSystem.IsWindows())
            return (byte*)VirtualAlloc(null, (nuint)size, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
        throw new PlatformNotSupportedException();
    }

    /// <summary>
    /// Installs a native (hand-assembled x64) vectored exception handler that commits zeroed pages on
    /// access violations inside [base, base+size). Pure machine code so it never re-enters the CLR
    /// while the faulting thread is in cooperative mode.
    /// </summary>
    public static bool InstallLazyCommitHandler(byte* @base, ulong size)
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
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
