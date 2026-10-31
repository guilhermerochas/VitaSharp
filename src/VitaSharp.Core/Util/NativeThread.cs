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

    /// <summary>What an emulator thread does, for host-side thread placement (the Android app pins the hot ones to the
    /// fast cores: docs/android/README.md, "CPU cores and ADPF").</summary>
    public enum Role { Other, Guest, Gpu, JitWorker }

    /// <summary>OS thread id to role, for the threads registered with <see cref="Register(string, Role)"/>. Entries
    /// are never removed: users check that the thread still exists.</summary>
    public static readonly ConcurrentDictionary<int, Role> Roles = new();

    /// <summary>Registers the calling thread's name and role.</summary>
    public static void Register(string name, Role role)
    {
        try
        {
            int id = CurrentId();
            Names[id] = name;
            Roles[id] = role;
            Registered?.Invoke(id, role);
        }
        catch { }
    }

    /// <summary>Raised on the registering thread itself (Android: per-thread timer slack, immediate placement).</summary>
    public static Action<int, Role>? Registered;

    static bool _noCycleTime = !OperatingSystem.IsWindows();
    /// <summary>CPU cycles the calling thread has consumed (Windows QueryThreadCycleTime; 0 elsewhere): the host CPU
    /// cost of a region without the time the thread was blocked (profiling).</summary>
    public static ulong CycleTime()
    {
        if (_noCycleTime) return 0;
        try { return QueryThreadCycleTime(-2, out ulong c) ? c : 0; } // -2: GetCurrentThread() pseudo-handle
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { _noCycleTime = true; return 0; }
    }

    [DllImport("kernel32.dll")][return: MarshalAs(UnmanagedType.Bool)] static extern bool QueryThreadCycleTime(nint thread, out ulong cycles);

    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("libc", SetLastError = true)] static extern long syscall(long number);
    [DllImport("libc")] static extern int pthread_threadid_np(nint thread, out ulong id);
}
