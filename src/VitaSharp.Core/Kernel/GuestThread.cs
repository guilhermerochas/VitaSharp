using System.Runtime.InteropServices;
using VitaSharp.Core.Cpu;
using VitaSharp.Core.Hle;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Kernel;

/// <summary>Values match SceThreadStatus (psp2common/kernel/threadmgr.h): written as-is by sceKernelGetThreadInfo.</summary>
public enum ThreadStatus { Running = 1, Ready = 2, Standby = 4, Waiting = 8, Dormant = 0x10, Dead = 0x20, Suspended = 0x100 }

/// <summary>Thrown by HLE code to unwind a guest thread (sceKernelExitThread & co).</summary>
public sealed class GuestThreadExitException(int status, bool delete) : Exception
{
    public int Status { get; } = status;
    public bool Delete { get; } = delete;
}

/// <summary>Thrown when the guest executes an undefined instruction or crashes.</summary>
public sealed class GuestFaultException(string message) : Exception(message);

/// <summary>A guest thread backed by a dedicated host thread (true parallelism across host cores).</summary>
public sealed unsafe class GuestThread : KernelObject, IDisposable
{
    public const uint KernelTlsSize = 0x800;
    public const int TlsThreadId = 1, TlsSpTop = 2, TlsSpBottom = 3, TlsVfpException = 4, TlsReserved6 = 6,
        TlsStackCheckGuard = 7, TlsCurrentPriority = 8, TlsAffinity = 9;

    readonly Emulator _emu;
    /// <summary>The process the thread belongs to.</summary>
    public Emulator Process => _emu;
    public int Pid => _emu.Pid;
    public CpuCore Core { get; }
    public HleContext Hle { get; }
    public uint Entry { get; }
    public int InitPriority { get; }
    public int Priority { get; set; }
    public uint Attr { get; }
    public int Affinity { get; set; }
    /// <summary>The thread currently holds one of the emulated CPU cores (see <see cref="Scheduler"/>).</summary>
    public bool HasCore;
    /// <summary>The thread runs on the system software's core, not the application cores (see <see cref="Scheduler"/>).</summary>
    public bool OnSystemCore { get; }
    /// <summary>Blocked inside host-side HLE work (audio device, GPU queue, display queue): not waiting for a core.</summary>
    public volatile bool BlockedInHost;
    public uint StackAddr { get; }
    public uint StackSize { get; }
    public uint TlsBase { get; }
    public uint UserTls => TlsBase + KernelTlsSize;
    public ThreadStatus Status { get; internal set; } = ThreadStatus.Dormant;
    public int ExitStatus { get; private set; }
    public int WakeupCount;
    public readonly object WaitLock = new();
    /// <summary>Thread signal (sceKernelSendSignal / sceKernelWaitSignal): one pending flag guarded by SignalLock.</summary>
    public bool SignalPending;
    public readonly object SignalLock = new();
    public long CpuTimeTicks;
    public string WaitingOn = "";
    /// <summary>When set, the thread runs this host code instead of guest code at Entry (it may still CallGuest).</summary>
    public Action<GuestThread>? HostBody { get; set; }

    // callback support for *CB waits
    public int PendingCallbacks;
    public bool CallbackWait;
    public volatile object? CurrentWaitSync;
    public static Func<GuestThread, int>? CallbackProcessor;

    Thread? _host;
    readonly ManualResetEventSlim _finished = new(true);
    int _callDepth;

    [ThreadStatic] static GuestThread? _current;
    public static GuestThread? Current => _current;

    /// <summary>Set by an HLE function that loaded a whole new register context (e.g. a fiber switch): the dispatcher
    /// then resumes at the loaded PC instead of returning to LR.</summary>
    public bool ContextReplaced;

    public GuestThread(Emulator emu, string name, uint entry, int priority, uint stackSize, uint attr, int affinity)
    {
        _emu = emu;
        Name = name;
        Entry = entry;
        InitPriority = Priority = priority;
        Attr = attr;
        Affinity = affinity;
        OnSystemCore = emu.RunsOnSystemCore;
        Core = new CpuCore(emu.Code, emu.Space.Base);
        Hle = new HleContext { Cpu = Core.State, Thread = this, Emu = emu };

        StackSize = GuestMemory.AlignUp(Math.Max(stackSize, 0x4000u), 0x1000);
        using var scope = ProcessScope.Enter(emu); // the stack and TLS belong to the thread's process
        StackAddr = emu.Memory.Alloc(StackSize, $"stack:{name}");
        GuestMemory.Fill(StackAddr, 0xCC, StackSize);

        uint tlsSize = KernelTlsSize + emu.Kernel.TlsAreaSize;
        TlsBase = emu.Memory.Alloc(Math.Max(tlsSize, 0x1000u), $"tls:{name}");
    }

    /// <summary>
    /// Sets up the TLS block when the thread is created (needs the UID). It is not cleared again by Start: creators
    /// may fill another thread's slots between create and start through sceKernelGetThreadTLSAddr (newlib's
    /// per-thread reent / pthread data in vitasdk's pthread-embedded).
    /// </summary>
    internal void InitTls()
    {
        var tls = GuestMemory.Span<int>(TlsBase, (int)(KernelTlsSize / 4));
        tls.Clear();
        tls[TlsThreadId] = Uid;
        tls[TlsSpTop] = (int)(StackAddr + StackSize);
        tls[TlsSpBottom] = (int)StackAddr;
        tls[TlsReserved6] = unchecked((int)0xFEFA7314);
        tls[TlsStackCheckGuard] = unchecked((int)0xC325C2A7);
        tls[TlsCurrentPriority] = Priority;
        tls[TlsAffinity] = Affinity;
        var k = _emu.Kernel;
        if (k.TlsInit != 0 && k.TlsInitSize > 0)
            GuestMemory.Copy(UserTls, k.TlsInit, k.TlsInitSize);
    }

    /// <summary>Value of r2-r12 when a thread starts (Vita3K's initial_context uses the same marker).</summary>
    public const uint InitialRegisterFill = 0xDEADBEEF;

    public int Start(uint argSize, uint argp)
    {
        lock (WaitLock)
        {
            if (Status != ThreadStatus.Dormant) return SceError.NOT_DORMANT;
            var s = Core.State;
            new Span<byte>(s, sizeof(CpuState)).Clear();
            s->Fpscr = 0x0300_0000;
            // r2-r12 are not zero at thread entry: like Vita3K (initial_context) fill them with a marker, so code
            // that reads a callee-saved register before writing it does not see a convenient 0. Silverio Vendetta's
            // 'umdReadAhead' threads compare an uninitialised r9 with the file index (0 for the first archive) and
            // skipped the open, reading fd -1 forever.
            for (int r = 2; r <= 12; r++) s->R[r] = InitialRegisterFill;
            var tls = GuestMemory.Span<int>(TlsBase, (int)(KernelTlsSize / 4));
            tls[TlsCurrentPriority] = Priority;
            tls[TlsAffinity] = Affinity;
            s->TpidrUro = UserTls;
            s->TpidrUrw = 0;
            s->R[13] = StackAddr + StackSize;
            s->R[14] = _emu.Kernel.ThreadExitTrap;
            s->R[0] = argSize;
            if (argp != 0 && argSize > 0)
            {
                uint size = (argSize + 7) & ~7u;
                s->R[13] -= size;
                GuestMemory.Copy(s->R[13], argp, argSize);
                s->R[1] = s->R[13];
            }
            else s->R[1] = 0;
            s->R[15] = Entry & ~1u;
            s->Thumb = Entry & 1;
            Status = ThreadStatus.Running;
            _finished.Reset();
            Core.StopRequested = false;
            _host = new Thread(HostMain, 256 * 1024) { Name = $"Guest:{Name}", IsBackground = true };
            _host.Start();
            return SceError.OK;
        }
    }

    void HostMain()
    {
        _current = this;
        Live[this] = 0;
        // the HLE calls of this thread (and the tasks they start) work in its process's address space
        Memory.AddressSpace.Enter(_emu.Space);
        Util.NativeThread.Register(Name, Util.NativeThread.Role.Guest);
        Util.HostThreads.Apply(Util.HostThreads.Role.Guest);
        int status = 0;
        bool delete = false;
        Core.ShouldYield = () => Scheduler.ShouldYield(this);
        try
        {
            Scheduler.Acquire(this);
            if (HostBody != null) HostBody(this);
            else status = (int)RunUntilTrap();
        }
        catch (GuestThreadExitException e)
        {
            status = e.Status;
            delete = e.Delete;
        }
        catch (Exception e)
        {
            Log.Error($"Thread '{Name}' (0x{Uid:X}) crashed: {e}");
            DumpState();
            _emu.ReportFatal(this, e);
        }
        finally
        {
            Scheduler.Release(this);
            lock (WaitLock)
            {
                ExitStatus = status;
                Status = ThreadStatus.Dormant;
                Monitor.PulseAll(WaitLock);
            }
            Live.TryRemove(this, out _);
            if (FinishingScene) _emu.OpenSceneThread = null;
            _finished.Set();
            _emu.Kernel.OnThreadExit(this);
            if (delete) _emu.Kernel.DeleteThread(this);
            _current = null;
        }
    }

    /// <summary>Runs guest code until the return trap fires at the current call depth. Returns R0.</summary>
    uint RunUntilTrap()
    {
        var s = Core.State;
        uint trap = _emu.Kernel.ThreadExitTrap;
        int depth = _callDepth;
        while (true)
        {
            if (SuspendCount > 0 || ProcessSuspended || PausedHere) ParkWhileSuspended();
            _inGuest = true;
            ExitReason exit;
            try { exit = Core.Run(); }
            finally { _inGuest = false; }
            switch (exit)
            {
                case ExitReason.Svc:
                    if (s->SvcImm == Kernel.TrapSvc)
                    {
                        if (_callDepth == depth)
                        {
                            if (depth == 0 && s->R[13] < StackAddr + StackSize - 0x200)
                            {
                                Log.Warn($"Thread '{Name}' reached the exit trap with a deep stack (sp=0x{s->R[13]:X8}): probable jump to a bad address");
                                DumpState();
                            }
                            return s->R[0];
                        }
                        throw new GuestFaultException("Unbalanced guest call return");
                    }
                    DispatchHle(s);
                    break;
                case ExitReason.Yield:
                    Scheduler.Yield(this);
                    break;
                case ExitReason.Halt:
                    throw new GuestThreadExitException(0, false);
                case ExitReason.Breakpoint:
                    throw new GuestFaultException($"BKPT #{s->SvcImm} at 0x{s->SvcPc:X8}");
                case ExitReason.Undefined:
                    if (!GuestMemory.IsMapped(s->SvcPc))
                        throw new GuestFaultException($"Prefetch abort: jump to unmapped address 0x{s->SvcPc:X8} (lr=0x{s->R[14]:X8})");
                    throw new GuestFaultException($"Undefined instruction 0x{s->SvcImm:X8} at 0x{s->SvcPc:X8} ({(s->Thumb != 0 ? "Thumb" : "ARM")})");
                default:
                    throw new GuestFaultException($"Unexpected CPU exit {exit}");
            }
            s->Exit = ExitReason.None;
        }
    }

    bool TraceHleCall(HleFunction f, CpuState* s)
    {
        if (_emu.TraceHleThreads is { } names)
        {
            bool match = false;
            foreach (var n in names) if (Name.Contains(n, StringComparison.Ordinal)) { match = true; break; }
            if (!match) return false;
        }
        bool trace = _emu.TraceHle;
        if (!trace && _emu.TraceHleOnly is { } only)
            foreach (var p in only) if (f.Name.StartsWith(p, StringComparison.Ordinal)) { trace = true; break; }
        if (!trace && _emu.TraceHleFrames is { } win)
        {
            long fr = VitaSharp.Core.Hle.Modules.Display.FramesPresented;
            if (fr >= win.From && fr < win.To)
            {
                trace = true;
                foreach (var p in _emu.TraceHleExclude) if (f.Name.StartsWith(p, StringComparison.Ordinal)) { trace = false; break; }
            }
        }
        if (trace) Log.Info($"HLE f{VitaSharp.Core.Hle.Modules.Display.FramesPresented} {f.Name}({s->R[0]:X}, {s->R[1]:X}, {s->R[2]:X}, {s->R[3]:X}) lr={s->R[14]:X8} t={Util.GuestClock.Microseconds}{TraceStrings(s)}");
        return trace;
    }

    /// <summary>Profiling (--profile-hle): per HLE function index (<see cref="CpuState.SvcImm"/>) three counters:
    /// calls, host wall ticks inside the implementation (blocking included) and host CPU cycles the thread used there.
    /// Written only by this thread; the sampler reads it racily at the end.</summary>
    public long[] HleProfile = [];
    /// <summary>Profiling (--profile): the HLE function the thread is inside (null in guest code); the sampler names
    /// host waits with it.</summary>
    public volatile HleFunction? CurrentHle;

    void InvokeTimed(HleFunction f, uint index)
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        ulong c0 = Util.NativeThread.CycleTime();
        try { f.Invoke!(Hle); }
        finally
        {
            long cyc = (long)(Util.NativeThread.CycleTime() - c0), ticks = System.Diagnostics.Stopwatch.GetTimestamp() - t0;
            var p = HleProfile;
            int i = (int)index * 3;
            if (i + 3 > p.Length) { Array.Resize(ref p, Math.Max(i + 3, p.Length * 2)); HleProfile = p; }
            p[i]++; p[i + 1] += ticks; p[i + 2] += cyc;
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    void Invoke(HleFunction f, uint index)
    {
        if (!HleFunction.Track) { f.Invoke!(Hle); return; }
        InvokeTracked(f, index);
    }

    void InvokeTracked(HleFunction f, uint index)
    {
        // no try/finally: a throwing HLE call (thread exit) leaves a stale name, which the next call replaces
        var outer = CurrentHle;
        CurrentHle = f;
        if (HleFunction.Profile) InvokeTimed(f, index); else f.Invoke!(Hle);
        CurrentHle = outer;
    }

    void DispatchHle(CpuState* s)
    {
        var f = _emu.Hle.GetFunction(s->SvcImm);
        f.CallCount++;
        long charged0 = Core.ChargedCycles;
        Core.ChargeInstructions(f.Cost);
        // tracing checks live in a separate method: a lambda capturing f here would allocate a closure on every HLE call
        bool trace = (_emu.TraceHle || _emu.TraceHleOnly != null || _emu.TraceHleFrames != null) && TraceHleCall(f, s);
        if (_emu.BacktraceHle != null && f.Name == _emu.BacktraceHle) Log.Info($"BT {f.Name}({s->R[0]:X}) lr={s->R[14]:X8} {Backtrace(s)}");
        // debug mode: arguments kept for the error log and the recent-calls ring (one static read when off)
        bool dbg = DebugMode.Enabled;
        uint a0 = 0, a1 = 0, a2 = 0, a3 = 0, lr = 0;
        if (dbg) { a0 = s->R[0]; a1 = s->R[1]; a2 = s->R[2]; a3 = s->R[3]; lr = s->R[14]; RecordHle(f, a0, a1, a2, a3, lr); }
        if (f.Invoke != null)
        {
            if (f.IsCallbackWait)
            {
                bool prev = CallbackWait;
                CallbackWait = true;
                try
                {
                    if (PendingCallbacks > 0) CallbackProcessor?.Invoke(this);
                    Invoke(f, s->SvcImm);
                }
                finally { CallbackWait = prev; }
            }
            else Invoke(f, s->SvcImm);
            f.ChargedCycles += Core.ChargedCycles - charged0;
            if (dbg) DebugHleReturn(f, s->R[0], a0, a1, a2, a3, lr);
            if (trace) Log.Info($"  -> {f.Name} = 0x{s->R[0]:X} t={Util.GuestClock.Microseconds}");
            if (ContextReplaced) { ContextReplaced = false; return; }
        }
        else
        {
            if (!f.WarnedUnimplemented) { f.WarnedUnimplemented = true; Log.WarnOnce($"nid{f.Nid}", $"Unimplemented HLE function {f.Library}::{f.Name} (NID 0x{f.Nid:X8}) called from 0x{s->R[14]:X8}"); }
            if (dbg) DebugMode.HleUnimplemented(Name, f.Library, f.Name, f.Nid, lr);
            s->R[0] = 0;
        }
        // The stub's next instruction is "bx lr": return directly.
        Arm.BxWritePc(s, s->R[14]);
    }

    /// <summary>Calls a guest function on this (current) thread and returns R0. Re-entrant.</summary>
    public uint CallGuest(uint addr, params ReadOnlySpan<uint> args) => CallGuestOnStack(addr, 0, args);

    /// <summary>Like <see cref="CallGuest"/>, with sp set to <paramref name="stackTop"/> (0 = just below the current sp).</summary>
    public uint CallGuestOnStack(uint addr, uint stackTop, params ReadOnlySpan<uint> args)
    {
        var s = Core.State;
        CpuState saved = *s;
        try
        {
            uint sp = stackTop != 0 ? stackTop & ~7u : (s->R[13] - 64) & ~7u;
            if (stackTop != 0 && args.Length > 4) sp = (sp - (uint)(args.Length - 4) * 4) & ~7u; // stack arguments below the given top
            for (int i = 0; i < args.Length; i++)
            {
                if (i < 4) s->R[i] = args[i];
                else GuestMemory.W32(sp + (uint)(i - 4) * 4, args[i]);
            }
            s->R[13] = sp;
            s->R[14] = _emu.Kernel.ThreadExitTrap;
            s->R[15] = addr & ~1u;
            s->Thumb = addr & 1;
            _callDepth++;
            s->Exit = ExitReason.None;
            return RunUntilTrap();
        }
        finally
        {
            _callDepth--;
            uint r0 = s->R[0];
            *s = saved;
            s->Exit = ExitReason.None;
        }
    }

    public bool WaitForExit(int timeoutMs) => _finished.Wait(timeoutMs);

    // ---- VM suspension (sceKernelSuspendThreadForVM / ForMono)
    public volatile int SuspendCount;
    volatile bool _inGuest;
    readonly object _suspendLock = new();

    public void SuspendForVm()
    {
        lock (_suspendLock) SuspendCount++;
        Core.InterruptRequested = true;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (_inGuest && Status == ThreadStatus.Running && sw.ElapsedMilliseconds < 1000) Thread.SpinWait(64);
    }

    public bool ResumeForVm()
    {
        lock (_suspendLock)
        {
            if (SuspendCount == 0) return false;
            SuspendCount--;
            Monitor.PulseAll(_suspendLock);
            return true;
        }
    }

    /// <summary>The thread's process is suspended (an application sent to the background by the shell), and the thread
    /// may stop here: not while its process has a GPU scene open, which the other processes would wait for.</summary>
    bool ProcessSuspended => _emu.Suspended && !_emu.Gpu.HasOpenScene(_emu.Pid);

    /// <summary>The emulation is paused (<see cref="EmulationPause"/>) and the thread may stop here: every thread but the
    /// one that has a GPU scene open, which goes on until it ends the scene so the GPU completes the frame.</summary>
    bool PausedHere => EmulationPause.IsPaused && !FinishingScene;

    /// <summary>The thread submitted a BeginScene whose EndScene is still to come (<see cref="Emulator.OpenSceneThread"/>).</summary>
    public bool FinishingScene => ReferenceEquals(_emu.OpenSceneThread, this);

    void ParkWhileSuspended()
    {
        using var blocked = Scheduler.Block();
        lock (_suspendLock)
            while ((SuspendCount > 0 || ProcessSuspended || PausedHere) && !Core.StopRequested) Monitor.Wait(_suspendLock, 50);
    }

    /// <summary>Wakes the thread if it is parked by a process suspension (the process resumed).</summary>
    public void WakeFromSuspend()
    {
        lock (_suspendLock) Monitor.PulseAll(_suspendLock);
    }

    /// <summary>Guest threads whose host thread is running, in every process (<see cref="EmulationPause"/>).</summary>
    static readonly System.Collections.Concurrent.ConcurrentDictionary<GuestThread, byte> Live = new();

    /// <summary>The emulation was paused: every running guest thread leaves its guest code at the next block boundary
    /// and parks (threads blocked in HLE park when they return to guest code).</summary>
    internal static void InterruptAll()
    {
        foreach (var t in Live.Keys) t.Core.InterruptRequested = true;
    }

    /// <summary>The emulation resumed: parked threads go on at once instead of at their next 50 ms check.</summary>
    internal static void WakeAll()
    {
        foreach (var t in Live.Keys) t.WakeFromSuspend();
    }

    /// <summary>--trace-hle: the arguments that point at a printable C string (paths, object names), quoted.</summary>
    static string TraceStrings(CpuState* s)
    {
        System.Text.StringBuilder? sb = null;
        for (int r = 0; r < 4; r++)
        {
            uint p = s->R[r];
            if (p < 0x10000 || !GuestMemory.IsMapped(p) || !GuestMemory.IsMapped(p + 63)) continue;
            int n = 0;
            while (n < 64 && GuestMemory.Read<byte>(p + (uint)n) is >= 0x20 and < 0x7F) n++;
            if (n < 3 || (n < 64 && GuestMemory.Read<byte>(p + (uint)n) != 0)) continue;
            (sb ??= new()).Append($" a{r}=\"{GuestMemory.ReadCString(p)}\"");
        }
        return sb?.ToString() ?? "";
    }

    /// <summary>Heuristic backtrace: stack words that point just after a BL/BLX in loaded code.</summary>
    /// <summary>Frames shown by Backtrace (--bt-depth).</summary>
    public static int BacktraceDepth = 10;

    public string Backtrace(CpuState* s)
    {
        var sb = new System.Text.StringBuilder();
        int found = 0;
        for (uint a = s->R[13]; a < StackAddr + StackSize && a < s->R[13] + (uint)(0x400 * BacktraceDepth / 5) && found < BacktraceDepth; a += 4)
        {
            uint v = GuestMemory.R32(a);
            if ((v & 1) == 0 || v < 0x81000000 || v >= 0x90000000) continue;
            uint ret = v & ~1u;
            uint hw1 = GuestMemory.Read<ushort>(ret - 4), hw2 = GuestMemory.Read<ushort>(ret - 2);
            bool bl = (hw1 & 0xF800) == 0xF000 && (hw2 & 0xD000) is 0xD000 or 0xC000;
            bool blxReg = (hw2 & 0xFF87) == 0x4780;
            if (!bl && !blxReg) continue;
            sb.Append($" {ret:X8}");
            found++;
        }
        return sb.ToString();
    }

    // ---- debug mode: the last HLE calls of this thread (watchdog / hang reports)
    const int HleRingSize = 16;
    struct HleRecord { public HleFunction F; public uint A0, A1, A2, A3, Lr, Ret; public bool Returned; public long Frame; }
    HleRecord[]? _hleRing;
    int _hleRingPos;

    void RecordHle(HleFunction f, uint a0, uint a1, uint a2, uint a3, uint lr)
    {
        var ring = _hleRing ??= new HleRecord[HleRingSize];
        ring[_hleRingPos++ % HleRingSize] = new HleRecord { F = f, A0 = a0, A1 = a1, A2 = a2, A3 = a3, Lr = lr, Frame = VitaSharp.Core.Hle.Modules.Display.FramesPresented };
    }

    void DebugHleReturn(HleFunction f, uint ret, uint a0, uint a1, uint a2, uint a3, uint lr)
    {
        // the call may have re-entered guest code (callbacks): only complete the record if it is still the newest one
        if (_hleRing is { } ring && _hleRingPos > 0 && ring[(_hleRingPos - 1) % HleRingSize].F == f)
        {
            ref var r = ref ring[(_hleRingPos - 1) % HleRingSize];
            r.Ret = ret;
            r.Returned = true;
        }
        if (ret is >= 0x80000000 and < 0x81000000) DebugMode.HleError(Name, f.Name, ret, a0, a1, a2, a3, lr);
    }

    /// <summary>Debug mode: this thread's last HLE calls, oldest first (a call without a result has not returned: the
    /// thread is inside it, e.g. blocked in a wait).</summary>
    public IEnumerable<string> RecentHleCalls(int max = HleRingSize)
    {
        if (_hleRing is not { } ring) yield break;
        int n = Math.Min(Math.Min(_hleRingPos, HleRingSize), max);
        for (int i = _hleRingPos - n; i < _hleRingPos; i++)
        {
            var r = ring[i % HleRingSize];
            if (r.F == null) continue;
            yield return $"f{r.Frame} {r.F.Name}(0x{r.A0:X}, 0x{r.A1:X}, 0x{r.A2:X}, 0x{r.A3:X}) lr=0x{r.Lr:X8} " + (r.Returned ? $"= 0x{r.Ret:X}" : "(not returned)");
        }
    }

    public string Describe()
    {
        var s = Core.State;
        string bt = Status == ThreadStatus.Dormant ? "" : " bt:" + Backtrace(s);
        return $"'{Name}' uid=0x{Uid:X} {Status} wait=[{WaitingOn}] pc=0x{s->R[15]:X8} lr=0x{s->R[14]:X8} sp=0x{s->R[13]:X8}{bt}";
    }

    public void RequestStop()
    {
        Core.StopRequested = true;
        lock (WaitLock) Monitor.PulseAll(WaitLock);
    }

    public void DumpState()
    {
        var s = Core.State;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Thread '{Name}' state:");
        for (int i = 0; i < 16; i++) sb.Append($" R{i}={s->R[i]:X8}{(i % 4 == 3 ? "\n" : "")}");
        sb.AppendLine($" CPSR={s->Cpsr:X8} FPSCR={s->Fpscr:X8} T={s->Thumb}");
        sb.AppendLine($" pc {_emu.Kernel.Symbolize(s->R[15])}, lr {_emu.Kernel.Symbolize(s->R[14])}");
        sb.Append(" backtrace:");
        foreach (var a in Backtrace(s).Split(' ', StringSplitOptions.RemoveEmptyEntries))
            sb.Append(' ').Append(_emu.Kernel.Symbolize(Convert.ToUInt32(a, 16)));
        Log.Error(sb.ToString());
    }

    public void Dispose()
    {
        Core.Dispose();
    }
}
