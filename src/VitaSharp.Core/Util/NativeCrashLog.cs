using Iced.Intel;
using System.Runtime.InteropServices;
using static Iced.Intel.AssemblerRegisters;

namespace VitaSharp.Core.Util;

/// <summary>
/// Windows x64: native heap corruption (0xC0000374, raised by ntdll where it detects the corruption) ends the process
/// without any managed handler running, so it vanished with nothing in the log and only an Application Error event
/// (25/11/2026: headless runs from a state "died in silence", the Intel driver's thread-exit callback freeing a bad
/// block). This installs a vectored exception handler written in machine code (it sees every exception of the
/// process, the native code's lazily committed pages included, and runs on any thread, also one being torn down with
/// the loader lock held, so it must not enter the runtime): for 0xC0000374 only, it records the code, address and
/// thread, wakes the reporter thread and waits (at most <see cref="ReportWaitMs"/>) for it to write a FATAL line to
/// the log and flush it; then the default handling goes on (Windows Error Reporting, exit code 0xC0000374).
/// Not covered: an access violation nothing handles goes to the top-level filter, and Windows refuses a top-level
/// filter outside a loaded image (it reads back null), so it is not logged (exit code 0xC0000005); a fast fail
/// (0xC0000409) runs no handler at all. Linux and macOS: not installed (the runtime owns the fault signals there).
/// A log without the "Process exit" line and a non-zero exit code tell those cases.
/// </summary>
public static unsafe partial class NativeCrashLog
{
    /// <summary>How long a crashing thread waits for the reporter thread's log line.</summary>
    public const int ReportWaitMs = 5000;

    private const uint HeapCorruption = 0xC0000374;

    // the page: [0] claimed (int), [4] exception code (uint), [8] address (ulong), [16] thread id (uint); the handler's
    // code follows at CodeOffset
    private const int CodeOffset = 64;

    private static byte* s_page;
    private static AutoResetEvent? s_crashed;
    private static ManualResetEvent? s_reported;
    private static Thread? s_reporter;

    /// <summary>The handler is installed (false off Windows x64, or when its page could not be made).</summary>
    public static bool Installed { get; private set; }

    /// <summary>Installs the handler and the reporter thread once per process (later calls do nothing).</summary>
    public static bool Install()
    {
        if (Installed)
        {
            return true;
        }

        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            return false;
        }

        try
        {
            nint kernel32 = NativeLibrary.Load("kernel32.dll");
            s_crashed = new AutoResetEvent(false);
            s_reported = new ManualResetEvent(false);
            var handles = new HandlerCalls(
                (ulong)s_crashed.SafeWaitHandle.DangerousGetHandle(),
                (ulong)s_reported.SafeWaitHandle.DangerousGetHandle(),
                (ulong)NativeLibrary.GetExport(kernel32, "SetEvent"),
                (ulong)NativeLibrary.GetExport(kernel32, "WaitForSingleObject"),
                (ulong)NativeLibrary.GetExport(kernel32, "GetCurrentThreadId"));
            byte* page = (byte*)VirtualAlloc(null, 4096, MemCommit | MemReserve, PageReadWrite);
            if (page == null)
            {
                return false;
            }

            ulong entry = (ulong)(page + CodeOffset);
            byte[] code = EmitHandler(entry, (ulong)page, handles);
            code.CopyTo(new Span<byte>((void*)entry, code.Length));
            // the data words stay writable for the handler; the page is executable too (one page for both)
            if (!VirtualProtect(page, 4096, PageExecuteReadWrite, out _))
            {
                return false;
            }

            s_page = page;
            WarmUp();
            s_reporter = new Thread(ReportLoop)
            {
                IsBackground = true,
                Name = "native crash reporter",
            };
            s_reporter.Start();
            // 0: called after the handlers already there (the runtime's, the lazily committed memory's)
            Installed = AddVectoredExceptionHandler(0, (void*)entry) != 0;
            return Installed;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            Log.Warn($"native crash log not installed: {e.Message}");
            return false;
        }
    }

    private readonly record struct HandlerCalls(ulong Crashed, ulong Reported, ulong SetEvent, ulong WaitForSingleObject, ulong GetCurrentThreadId);

    /// <summary>
    /// The handler (rcx = EXCEPTION_POINTERS*): for 0xC0000374 only, claims the report once, stores code, address and
    /// thread id in the page, signals the reporter and waits for it; always returns EXCEPTION_CONTINUE_SEARCH (0).
    /// </summary>
    private static byte[] EmitHandler(ulong rip, ulong page, HandlerCalls calls)
    {
        var a = new Assembler(64);
        Label done = a.CreateLabel();
        a.push(rbx);
        a.mov(rbx, rcx);
        a.mov(rax, __qword_ptr[rcx]);
        a.cmp(__dword_ptr[rax], unchecked((int)HeapCorruption));
        a.jne(done);
        // claim: only the first one of the process is reported
        a.mov(r10, page);
        a.mov(r8d, 1);
        a.xchg(__dword_ptr[r10], r8d);
        a.test(r8d, r8d);
        a.jnz(done);
        a.mov(rax, __qword_ptr[rbx]);
        a.mov(edx, __dword_ptr[rax]);
        a.mov(__dword_ptr[r10 + 4], edx);
        a.mov(rdx, __qword_ptr[rax + 16]);
        a.mov(__qword_ptr[r10 + 8], rdx);
        // rsp is 16-byte aligned after the push; 32 bytes of shadow space for the calls
        a.sub(rsp, 32);
        a.mov(rax, calls.GetCurrentThreadId);
        a.call(rax);
        a.mov(r10, page);
        a.mov(__dword_ptr[r10 + 16], eax);
        a.mov(rcx, calls.Crashed);
        a.mov(rax, calls.SetEvent);
        a.call(rax);
        a.mov(rcx, calls.Reported);
        a.mov(edx, ReportWaitMs);
        a.mov(rax, calls.WaitForSingleObject);
        a.call(rax);
        a.add(rsp, 32);
        a.Label(ref done);
        a.pop(rbx);
        a.xor(eax, eax);
        a.ret();
        using var ms = new MemoryStream();
        a.Assemble(new StreamCodeWriter(ms), rip);
        return ms.ToArray();
    }

    private static void ReportLoop()
    {
        s_crashed!.WaitOne();
        try
        {
            uint code = *(uint*)(s_page + 4);
            ulong address = *(ulong*)(s_page + 8);
            uint threadId = *(uint*)(s_page + 16);
            // the line first: the crashing thread may hold the loader lock (a thread ending), which the module and
            // thread name lookups can need; if they block, the line is already in the file when the wait times out
            Log.Error(FormatLine(code, address, null, 0, threadId, null));
            Log.Flush();
            (string? module, ulong moduleBase) = ModuleOf(address);
            Log.Error("FATAL native exception detail: " + FormatWhere(address, module, moduleBase, threadId, ThreadName(threadId)));
            Log.Flush();
        }
        finally
        {
            s_reported!.Set();
        }
    }

    /// <summary>Resolves what the reporter calls at crash time (P/Invoke targets, JIT) while the process is healthy.</summary>
    private static void WarmUp()
    {
        (string? module, ulong moduleBase) = ModuleOf((ulong)(nint)(delegate*<void>)&WarmUp);
        _ = FormatLine(HeapCorruption, moduleBase, module, moduleBase, 0, ThreadName(GetCurrentThreadId()));
    }

    /// <summary>The log line of a fatal native exception.</summary>
    public static string FormatLine(uint code, ulong address, string? module, ulong moduleBase, uint threadId, string? threadName)
    {
        return $"FATAL native exception 0x{code:X8} ({Describe(code)}) at {FormatWhere(address, module, moduleBase, threadId, threadName)}: the process ends with exit code 0x{code:X8} (Windows Error Reporting records it as an Application Error)";
    }

    private static string FormatWhere(ulong address, string? module, ulong moduleBase, uint threadId, string? threadName)
    {
        // The module path is a Windows path (this log runs on Windows only), so the file name is cut at either
        // separator: Path.GetFileName on Linux and macOS would keep a backslash path whole.
        string where = module != null ? $"{module[(module.LastIndexOfAny(['\\', '/']) + 1)..]}+0x{address - moduleBase:X}" : $"0x{address:X}";
        string thread = string.IsNullOrEmpty(threadName) ? $"thread {threadId}" : $"thread {threadId} '{threadName}'";
        return $"{where} on {thread}";
    }

    /// <summary>What a Windows exception code means, for the log line.</summary>
    public static string Describe(uint code) => code switch
    {
        0xC0000005 => "access violation",
        0xC0000374 => "native heap corruption",
        0xC00000FD => "stack overflow",
        0xC000001D => "illegal instruction",
        0xC0000094 => "integer division by zero",
        0xC0000409 => "stack buffer overrun / fail fast",
        0xC0000420 => "assertion failure",
        0x80000003 => "breakpoint",
        _ => "native exception",
    };

    private static (string? Module, ulong Base) ModuleOf(ulong address)
    {
        const uint FromAddress = 0x4;
        const uint UnchangedRefCount = 0x2;
        if (!GetModuleHandleExW(FromAddress | UnchangedRefCount, (void*)address, out nint module) || module == 0)
        {
            return (null, 0);
        }

        char* name = stackalloc char[520];
        uint length = GetModuleFileNameW(module, name, 520);
        return (length > 0 ? new string(name, 0, (int)length) : null, (ulong)module);
    }

    private static string? ThreadName(uint threadId)
    {
        const uint QueryLimitedInformation = 0x0800;
        nint thread = OpenThread(QueryLimitedInformation, false, threadId);
        if (thread == 0)
        {
            return null;
        }

        try
        {
            if (GetThreadDescription(thread, out char* description) < 0 || description == null)
            {
                return null;
            }

            string name = new(description);
            LocalFree(description);
            return name;
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
        finally
        {
            CloseHandle(thread);
        }
    }

    /// <summary>
    /// Developer self-test (<see cref="EnvVars.DebugNativeCrash"/>=heap): frees a block of a private heap twice, which
    /// ntdll detects as heap corruption (0xC0000374), on a thread named like the one of the real crash.
    /// </summary>
    public static void CrashForTest(string kind)
    {
        Log.Warn($"{EnvVars.DebugNativeCrash}={kind}: ending the process with a native crash (self-test)");
        Log.Flush();
        const uint NoFaultErrorBox = 0x0002;
        SetErrorMode(NoFaultErrorBox);
        if (kind == "heap")
        {
            var thread = new Thread(() =>
            {
                nint heap = HeapCreate(0, 0, 0);
                void* block = HeapAlloc(heap, 0, 64);
                HeapFree(heap, 0, block);
                HeapFree(heap, 0, block);
                HeapDestroy(heap);
            })
            {
                Name = "crash self-test",
            };
            thread.Start();
            thread.Join();
        }

        Log.Warn($"{EnvVars.DebugNativeCrash}={kind}: the process did not crash");
    }

    private const uint MemCommit = 0x1000;
    private const uint MemReserve = 0x2000;
    private const uint PageReadWrite = 0x04;
    private const uint PageExecuteReadWrite = 0x40;

    [LibraryImport("kernel32.dll")]
    private static partial void* VirtualAlloc(void* address, nuint size, uint type, uint protect);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool VirtualProtect(void* address, nuint size, uint protect, out uint oldProtect);

    [LibraryImport("kernel32.dll")]
    private static partial nint AddVectoredExceptionHandler(uint first, void* handler);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetModuleHandleExW(uint flags, void* address, out nint module);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetModuleFileNameW(nint module, char* name, uint size);

    [LibraryImport("kernel32.dll")]
    private static partial nint OpenThread(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint threadId);

    [LibraryImport("kernel32.dll")]
    private static partial int GetThreadDescription(nint thread, out char* description);

    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(void* memory);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll")]
    private static partial uint SetErrorMode(uint mode);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();

    [LibraryImport("kernel32.dll")]
    private static partial nint HeapCreate(uint options, nuint initialSize, nuint maximumSize);

    [LibraryImport("kernel32.dll")]
    private static partial void* HeapAlloc(nint heap, uint flags, nuint bytes);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool HeapFree(nint heap, uint flags, void* block);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool HeapDestroy(nint heap);
}
