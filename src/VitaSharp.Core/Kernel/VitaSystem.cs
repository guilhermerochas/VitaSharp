using VitaSharp.Core.Util;

namespace VitaSharp.Core.Kernel;

/// <summary>
/// The process a host thread is working for: a guest thread's own process, or the one a host-side operation (loading a
/// new process's modules from the shell's launch call, say) declared with <see cref="Enter"/>. Kernel objects and guest
/// memory blocks created on the thread are owned by it, and go away with the process.
/// </summary>
public static class ProcessScope
{
    [ThreadStatic] static int t_pid;

    /// <summary>The current process id; 0 = the system (host threads that belong to no process).</summary>
    public static int CurrentPid => t_pid != 0 ? t_pid : GuestThread.Current?.Pid ?? 0;

    /// <summary>Makes <paramref name="pid"/> the current process of this host thread until the result is disposed.</summary>
    public static Scope Enter(int pid)
    {
        var s = new Scope(t_pid);
        t_pid = pid;
        return s;
    }

    public readonly struct Scope(int previous) : IDisposable
    {
        public void Dispose() => t_pid = previous;
    }
}

/// <summary>The kernel's record of a process (its UID is the process id).</summary>
public sealed class ProcessObject(Emulator process) : KernelObject
{
    public Emulator Process { get; } = process;
}

/// <summary>
/// SceAppMgr shell event types (FW 3.74 appmgr: each has its own posting function; the shell's event loop dispatches on
/// them). Only those VitaSharp posts are named; their payloads follow the kernel's posting code.
/// </summary>
public static class ShellEvents
{
    /// <summary>An application process was created: +4 process id, +8 application id.</summary>
    public const int AppCreated = 2;
    /// <summary>A launch requested by the shell finished: +4 the application id, -1 when it failed (the shell then
    /// reads the application's status and app0 path and activates it).</summary>
    public const int AppLaunched = 0xA;
    /// <summary>An application process ended: +4 pid, +0x14 application id (the shell's handler reads the id there).</summary>
    public const int AppEnded = 4;
    /// <summary>Bring up the LiveArea of the running application +4 (the PS button while it is in front).</summary>
    public const int ShowLiveArea = 0x10;
}

/// <summary>SceAppMgr's record of the application a process runs (its UID is the application id the shell uses).</summary>
public sealed class AppObject(Emulator process) : KernelObject
{
    public Emulator Process { get; } = process;
}

/// <summary>
/// What the processes of one emulated PS Vita share: the guest address space, the GPU, the host devices, one kernel
/// object table (a single UID space and the system-wide namespace that sceKernelOpen* searches, so the IPC objects a
/// server creates are found by its clients in other processes) and the display, which shows the foreground process.
/// <para>
/// The first process (the game run directly, or the shell) creates it. Every other process is a further
/// <see cref="Emulator"/> with its own kernel state, HLE modules, linker and mounts, placed apart in the shared address
/// space (executables are relocatable and the allocator hands each process free ranges), the way the Vita kernel
/// runs the shell and an application side by side.
/// </para>
/// </summary>
public sealed class VitaSystem
{
    public ObjectTable Objects { get; } = new();

    readonly object _lock = new();
    readonly List<Emulator> _processes = new();
    Emulator? _foreground;

    /// <summary>Raised (off the lock) when another process takes the display and the input.</summary>
    public event Action<Emulator>? ForegroundChanged;

    public IReadOnlyList<Emulator> Processes { get { lock (_lock) return _processes.ToArray(); } }

    /// <summary>The process on screen: its frames are displayed and it receives the buttons and touches.</summary>
    public Emulator? Foreground { get { lock (_lock) return _foreground; } }

    public bool IsForeground(Emulator e) => ReferenceEquals(Volatile.Read(ref _foreground), e) || _foreground == null;

    internal int Register(Emulator e)
    {
        int pid = Objects.Register(new ProcessObject(e));
        lock (_lock)
        {
            _processes.Add(e);
            _foreground ??= e;
        }
        return pid;
    }

    /// <summary>The application id of process <paramref name="e"/> (an <see cref="AppObject"/> it owns).</summary>
    internal int RegisterApp(Emulator e)
    {
        using var scope = ProcessScope.Enter(e.Pid);
        return Objects.Register(new AppObject(e));
    }

    public Emulator? ByAppId(int appId) => Objects.Get<AppObject>(appId)?.Process;

    /// <summary>The process that declared itself the shell (sceAppMgrDeclareShellProcess2).</summary>
    public Emulator? Shell => Processes.FirstOrDefault(p => p.IsShell);

    /// <summary>SceAppMgr's global state block (0x80 bytes, _sceAppMgrGetAppMgrState copies it whole): +0 prioritize
    /// system chat, +4 system impose state, +8 initialised to -1, +0xC audio routing, +0x10 recommended screen
    /// orientation, +0x14 system impose state 2.</summary>
    public readonly byte[] AppMgrState = InitialAppMgrState();

    static byte[] InitialAppMgrState()
    {
        var s = new byte[0x80];
        BitConverter.TryWriteBytes(s.AsSpan(8), -1);
        return s;
    }

    /// <summary>The application holding the BGM port (sceAppMgrAcquireBgmPort*), -1 for none.</summary>
    public int BgmPortOwner = -1;

    // ---- SceAppMgr's events for the shell (sceAppMgrReceiveShellEvent): 0x40-byte records, first word the event type

    public const int ShellEventSize = 0x40;
    readonly Queue<byte[]> _shellEvents = new();

    /// <summary>Queues a shell event: <paramref name="type"/> then the words from +4 (the kernel queue holds 180).</summary>
    public void PostShellEvent(int type, params ReadOnlySpan<int> words)
    {
        var ev = new byte[ShellEventSize];
        BitConverter.TryWriteBytes(ev.AsSpan(0, 4), type);
        for (int i = 0; i < words.Length && 4 + i * 4 < ShellEventSize; i++) BitConverter.TryWriteBytes(ev.AsSpan(4 + i * 4, 4), words[i]);
        lock (_shellEvents)
        {
            if (_shellEvents.Count >= 180) return;
            _shellEvents.Enqueue(ev);
        }
        Log.Info($"Shell event {type} [{string.Join(", ", words.ToArray().Select(w => $"0x{w:X}"))}]");
    }

    /// <summary>
    /// The PS button was pressed (seen by the shell's controller reads). With an application in front, SceAppMgr asks
    /// the shell to bring up that application's LiveArea: shell event 0x10 {application id}; the shell answers with
    /// sceAppMgrDeactivateApp, which suspends the application and gives the shell the screen. (Event 0x10's meaning is
    /// inferred: of the shell's event handlers, it is the one that takes an application id and opens the LiveArea of
    /// the running application; the firmware's kernel posts it from one of its application-state syscalls.)
    /// </summary>
    public void OnPsButton()
    {
        var fg = Foreground;
        if (fg == null || fg.IsShell || Shell == null) return;
        Log.Info($"PS button: {fg.Describe()} goes to its LiveArea");
        PostShellEvent(ShellEvents.ShowLiveArea, fg.AppId);
    }

    public int ShellEventCount { get { lock (_shellEvents) return _shellEvents.Count; } }

    public byte[]? TakeShellEvent() { lock (_shellEvents) return _shellEvents.TryDequeue(out var e) ? e : null; }

    internal void Unregister(Emulator e)
    {
        Emulator? next = null;
        lock (_lock)
        {
            _processes.Remove(e);
            if (ReferenceEquals(_foreground, e))
            {
                next = e.Parent is { } p && _processes.Contains(p) ? p : _processes.LastOrDefault();
                _foreground = next;
            }
        }
        Objects.Remove(e.Pid);
        Objects.Remove(e.AppId);
        if (next != null) NotifyForeground(next);
    }

    /// <summary>Gives the display and the input to <paramref name="e"/>.</summary>
    public void SetForeground(Emulator e)
    {
        lock (_lock)
        {
            if (ReferenceEquals(_foreground, e) || !_processes.Contains(e)) return;
            _foreground = e;
        }
        Log.Info($"Foreground process: {e.Describe()}");
        NotifyForeground(e);
    }

    void NotifyForeground(Emulator e)
    {
        e.OnForeground();
        ForegroundChanged?.Invoke(e);
    }

    public Emulator? ByPid(int pid) => Objects.Get<ProcessObject>(pid)?.Process;
}
