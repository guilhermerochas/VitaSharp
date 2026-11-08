using VitaSharp.Core.Util;

namespace VitaSharp.Core.Kernel;

/// <summary>
/// The process a host thread is working for: a guest thread's own process, or the one a host-side operation (loading a
/// new process's modules from the shell's launch call, say) declared with <see cref="Enter"/>. Kernel objects and guest
/// memory blocks created on the thread are owned by it, and go away with the process; guest memory is accessed in its
/// address space (<see cref="Memory.AddressSpace.Current"/>).
/// </summary>
public static class ProcessScope
{
    [ThreadStatic] static int t_pid;

    /// <summary>The current process id; 0 = the system (host threads that belong to no process).</summary>
    public static int CurrentPid => t_pid != 0 ? t_pid : GuestThread.Current?.Pid ?? 0;

    /// <summary>Makes <paramref name="process"/> the current process of this host thread (and its address space the one
    /// guest memory accesses use, also in the tasks the thread starts) until the result is disposed.</summary>
    public static Scope Enter(Emulator process)
    {
        var s = new Scope(t_pid, Memory.AddressSpace.Enter(process.Space));
        t_pid = process.Pid;
        return s;
    }

    /// <summary>Only the process id (ownership of kernel objects and blocks); guest memory stays in the thread's space.</summary>
    public static PidScope Enter(int pid)
    {
        var s = new PidScope(t_pid);
        t_pid = pid;
        return s;
    }

    public readonly struct Scope(int previous, Memory.AddressSpace.FlowScope space) : IDisposable
    {
        public void Dispose()
        {
            t_pid = previous;
            space.Dispose();
        }
    }

    public readonly struct PidScope(int previous) : IDisposable
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
    /// <summary>An application process ended (<see cref="VitaSystem.PostAppEnded"/>): +4 pid, +8 state, +0x14 application
    /// id, +0xC/+0x18 -1, +0x20 the title id.</summary>
    public const int AppEnded = 4;
    /// <summary>An application asked for another one to start (sceAppMgrLaunchAppByName): +4 and +8 its application
    /// id (kernel appmgr 0x8101410C); the shell answers with sceAppMgrLaunchAppByName2ndStage or LaunchAppCancel.</summary>
    public const int LaunchRequest = 6;
    /// <summary>Bring up the LiveArea of the running application +4 (the PS button while it is in front).</summary>
    public const int ShowLiveArea = 0x10;
}

/// <summary>SceAppMgr's record of the application a process runs (its UID is the application id the shell uses). An
/// application another application asked for has its record before its process (<see cref="PendingLaunch"/>).</summary>
public sealed class AppObject(Emulator? process) : KernelObject
{
    public Emulator? Process { get; internal set; } = process;
}

/// <summary>
/// An application one application asked to start (sceAppMgrLaunchAppByName: Music's "Start [Content Manager]"). In
/// the FW 3.74 kernel appmgr (0x8100D89C) the call creates the application's entry, with its launch flags and
/// parameter, and posts shell event 6 {application id}; the shell's handler (shell.self 0x8127D402) reads its status
/// and launch parameter (sceAppMgrPeekLaunchParamForShell) and either starts it with
/// sceAppMgrLaunchAppByName2ndStage or drops it with sceAppMgrLaunchAppCancel. The process exists only after the
/// second stage.
/// </summary>
public sealed class PendingLaunch
{
    public required AppObject App;
    public required string TitleId, Dir;
    public int Flags;
    public string? Param;
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
        using var scope = ProcessScope.Enter(e);
        return Objects.Register(new AppObject(e));
    }

    public Emulator? ByAppId(int appId) => Objects.Get<AppObject>(appId)?.Process;

    readonly Dictionary<int, PendingLaunch> _pendingLaunches = new();

    /// <summary>Reserves the application id of a launch another application asked for (<see cref="PendingLaunch"/>).</summary>
    public PendingLaunch AddPendingLaunch(Emulator caller, string titleId, string dir, int flags, string? param)
    {
        int uid;
        var app = new AppObject(null);
        using (ProcessScope.Enter(caller)) uid = Objects.Register(app);
        var pl = new PendingLaunch { App = app, TitleId = titleId, Dir = dir, Flags = flags, Param = param };
        lock (_pendingLaunches) _pendingLaunches[uid] = pl;
        return pl;
    }

    public PendingLaunch? GetPendingLaunch(int appId) { lock (_pendingLaunches) return _pendingLaunches.GetValueOrDefault(appId); }

    /// <summary>Takes a pending launch out (its second stage, or its cancellation).</summary>
    public PendingLaunch? TakePendingLaunch(int appId)
    {
        lock (_pendingLaunches) return _pendingLaunches.Remove(appId, out var pl) ? pl : null;
    }

    /// <summary>Gives a process the application record reserved for it.</summary>
    internal int ClaimApp(AppObject app, Emulator e)
    {
        app.Process = e;
        app.OwnerPid = e.Pid;
        return app.Uid;
    }

    /// <summary>The process that declared itself the shell (sceAppMgrDeclareShellProcess2).</summary>
    public Emulator? Shell => Processes.FirstOrDefault(p => p.IsShell);

    /// <summary>pid -> host folder of an installed application: while a system program installs that application's
    /// trophy set in the shell's trophy server for it (<see cref="Hle.Np.TrophyServerBridge.InstallStoredTitles"/>),
    /// sceAppMgrTrophyMount(300, pid) mounts this application instead of the program's own.</summary>
    public readonly System.Collections.Concurrent.ConcurrentDictionary<int, string> TrophyApps = new();

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

    /// <summary>SceSharedFb: the system software's framebuffers, created by the shell and drawn into by the system
    /// programs it composites.</summary>
    public Hle.Modules.SharedFb.Registry SharedFbs { get; } = new();

    // ---- the shell's overlay plane: while another process is in front, a frame the shell shows (the on-screen keyboard
    // of its ime_plugin, notifications) is a plane the display blends over that process's frame with the shell frame's
    // alpha. The shell shows such frames only while it has something to overlay; a frame older than this many vblanks
    // is no longer on screen.
    const long ShellOverlayVBlanks = 60;
    Gpu.DisplayFrame? _shellOverlay;
    long _shellOverlayVBlank;

    /// <summary>The shell showed <paramref name="frame"/> (null: no framebuffer) while not in front.</summary>
    public void SetShellOverlay(Gpu.DisplayFrame? frame, long vblank)
    {
        lock (_lock)
        {
            if (_shellOverlay == null && frame != null) Log.Info($"Shell overlay plane on: 0x{frame.Value.Address:X8} {frame.Value.Width}x{frame.Value.Height}");
            _shellOverlay = frame;
            _shellOverlayVBlank = vblank;
        }
    }

    /// <summary>The shell frame to blend over the frame the process in front shows now, if any.</summary>
    public Gpu.DisplayFrame? ShellOverlay(long vblank)
    {
        lock (_lock)
        {
            if (_shellOverlay == null) return null;
            if (vblank - _shellOverlayVBlank <= ShellOverlayVBlanks && _foreground is { IsShell: false }) return _shellOverlay;
            _shellOverlay = null;
            Log.Info("Shell overlay plane off");
            return null;
        }
    }

    // ---- the shell's touch regions (sceTouchSetRegion / ActivateRegion, regions above 0): while one is active over an
    // application in front, the shell's overlay takes the contacts on the panel (its on-screen keyboard)
    readonly Dictionary<(int Port, int Region), (ushort X0, ushort Y0, ushort X1, ushort Y1, bool Active)> _shellTouchRegions = new();

    public void SetShellTouchRegion(int port, int region, ushort x0, ushort y0, ushort x1, ushort y1)
    {
        lock (_shellTouchRegions)
        {
            bool known = _shellTouchRegions.TryGetValue((port, region), out var r);
            if (!known || (r.X0, r.Y0, r.X1, r.Y1) != (x0, y0, x1, y1))
                Log.Info($"Shell touch region {port}/{region}: ({x0}, {y0})-({x1}, {y1})");
            _shellTouchRegions[(port, region)] = (x0, y0, x1, y1, known && r.Active);
        }
    }

    public void ActivateShellTouchRegion(int port, int region, bool enable)
    {
        lock (_shellTouchRegions)
        {
            var r = _shellTouchRegions.TryGetValue((port, region), out var old) ? old : default;
            _shellTouchRegions[(port, region)] = r with { Active = enable };
        }
    }

    /// <summary>True when the shell has an active touch region on <paramref name="port"/> over an application in front.
    /// Only the contacts inside one are the shell's (<see cref="ShellTouchRegionAt"/>): its status bar keeps a region
    /// over the top of the panel while a system application with the bar is in front, its on-screen keyboard one over
    /// the whole panel.</summary>
    public bool TouchClaimedByShell(int port)
    {
        if (Foreground is not { IsShell: false }) return false;
        lock (_shellTouchRegions)
            foreach (var (key, r) in _shellTouchRegions)
                if (key.Port == port && r.Active) return true;
        return false;
    }

    /// <summary>Whether the panel point (x, y) of <paramref name="port"/> (panel coordinates) is in an active shell
    /// region over an application in front.</summary>
    public bool ShellTouchRegionAt(int port, int x, int y)
    {
        if (Foreground is not { IsShell: false }) return false;
        lock (_shellTouchRegions)
            foreach (var (key, r) in _shellTouchRegions)
                if (key.Port == port && r.Active && x >= r.X0 && x <= r.X1 && y >= r.Y0 && y <= r.Y1) return true;
        return false;
    }

    /// <summary>True when the shell's overlay takes the buttons from the application in front: an active front panel
    /// region covering (nearly) the whole panel, which is its on-screen keyboard. A smaller region (the status bar's)
    /// leaves the buttons to the application.</summary>
    public bool ButtonsClaimedByShell()
    {
        if (Foreground is not { IsShell: false }) return false;
        lock (_shellTouchRegions)
            foreach (var (key, r) in _shellTouchRegions)
                if (key.Port == 0 && r.Active && r.X1 - r.X0 >= 1800 && r.Y1 - r.Y0 >= 1000) return true;
        return false;
    }

    /// <summary>While the trophy server bridge gives a trophy earned before (TrophyServerBridge), its unlock date
    /// (SceRtcTick): the shell's trophy server threads read it as the current tick, so the server records the date the
    /// trophy was earned. 0 = none.</summary>
    public long TrophyUnlockTick;

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
    /// sceAppMgrDeactivateApp, which suspends the application and gives the shell the screen. In the FW 3.74 kernel
    /// appmgr (bootimage), event 0x10 is posted only by the function behind SceAppMgrForDriver 0x29421DCD / 0xF3B1D42E,
    /// which other kernel code calls for the application in front: it marks the application as being suspended
    /// (state 3) and queues the event with the application id; the shell's handler for 0x10 opens that application's
    /// LiveArea.
    /// </summary>
    public void OnPsButton()
    {
        var fg = Foreground;
        if (fg == null || fg.IsShell || Shell == null) return;
        Log.Info($"PS button: {fg.Describe()} goes to its LiveArea");
        PostShellEvent(ShellEvents.ShowLiveArea, fg.AppId);
    }

    /// <summary>
    /// An application process ended: shell event 4 as the FW 3.74 kernel appmgr builds it (0x81013FE4, called from the
    /// destroy path 0x81000860; same layout as event 3, the status change): +4 pid, +8 the entry's state (2 = in front),
    /// +0xC and +0x18 the ids of the application it was started for (-1 when none, entry flag bit 18 clear), +0x14 the
    /// application id, +0x1C a halfword of the entry (0 here; 1 makes the kernel drop the event with its pending
    /// create/status events), +0x20 the entry's 0x20-byte name (title id). The shell's handler (shell.self 0x8127DBD2)
    /// looks +0x18 up as an application (-1: none) and reads +8/+0x10; with zeros there (only pid and id written) it
    /// called sceAppMgrSetPowerSaveMode(1, 1) and showed its lock screen after every application end.
    /// </summary>
    public void PostAppEnded(int pid, int appId, int state, string titleId)
    {
        Span<int> w = stackalloc int[15];
        w[0] = pid; w[1] = state; w[2] = -1; w[3] = 0; w[4] = appId; w[5] = -1; w[6] = 0;
        var name = System.Text.Encoding.ASCII.GetBytes(titleId);
        Span<byte> nb = stackalloc byte[0x20];
        nb.Clear(); // locals are not zeroed in this assembly
        name.AsSpan(0, Math.Min(name.Length, 0x1F)).CopyTo(nb);
        System.Runtime.InteropServices.MemoryMarshal.Cast<byte, int>(nb).CopyTo(w[7..]);
        PostShellEvent(ShellEvents.AppEnded, w);
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
