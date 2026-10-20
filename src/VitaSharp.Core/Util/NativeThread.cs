using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace VitaSharp.Core.Util;

/// <summary>
/// Maps OS thread ids to the emulator's thread names so per-thread CPU time (Process.Threads) can be attributed:
/// the PERF report names the guest threads, GPU worker and JIT workers that actually burn CPU.
/// The ids match ProcessThread.Id on each platform: GetCurrentThreadId on Windows, the kernel tid (gettid) on
/// Linux/Android, the 64-bit thread id (pthread_threadid_np) on macOS. OS-level thread names come from
/// Thread.Name, which .NET forwards to SetThreadDescription / pthread_setname_np itself.
/// </summary>
public static class NativeThread
{
    public static readonly ConcurrentDictionary<int, string> Names = new();

    // SYS_gettid differs per architecture
    static readonly long GettidSyscall = RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => 186,
        Architecture.Arm64 => 178,
        Architecture.X86 or Architecture.Arm => 224,
        _ => -1,
    };

    public static int CurrentId()
    {
        try
        {
            if (OperatingSystem.IsWindows()) return (int)GetCurrentThreadId();
            if ((OperatingSystem.IsLinux() || OperatingSystem.IsAndroid()) && GettidSyscall >= 0) return (int)syscall(GettidSyscall);
            if (OperatingSystem.IsMacOS() && pthread_threadid_np(0, out ulong tid) == 0) return (int)tid;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { }
        return Environment.CurrentManagedThreadId;
    }

    public static void Register(string name) { try { Names[CurrentId()] = name; } catch { } }

    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("libc", SetLastError = true)] static extern long syscall(long number);
    [DllImport("libc")] static extern int pthread_threadid_np(nint thread, out ulong id);
}
