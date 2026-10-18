using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace VitaSharp.Core.Util;

/// <summary>
/// Maps OS thread ids to the emulator's thread names so per-thread CPU time (Process.Threads) can be attributed:
/// the PERF report names the guest threads, GPU worker and JIT workers that actually burn CPU.
/// </summary>
public static class NativeThread
{
    public static readonly ConcurrentDictionary<int, string> Names = new();

    public static int CurrentId()
    {
        if (OperatingSystem.IsWindows()) return (int)GetCurrentThreadId();
        if (OperatingSystem.IsLinux() || OperatingSystem.IsAndroid()) return (int)syscall(186); // SYS_gettid (x86-64); arm64 uses 178
        return Environment.CurrentManagedThreadId;
    }

    public static void Register(string name) { try { Names[CurrentId()] = name; } catch { } }

    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("libc", SetLastError = true)] static extern long syscall(long number);
}
