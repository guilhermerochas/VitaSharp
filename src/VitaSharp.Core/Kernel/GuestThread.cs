using System.Runtime.InteropServices;
using VitaSharp.Core.Cpu;
using VitaSharp.Core.Hle;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Kernel;

public enum ThreadStatus { Dormant = 0x10, Ready = 2, Running = 1, Waiting = 4, Suspended = 8, Dead = 0x20 }

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
    public CpuCore Core { get; }
    public HleContext Hle { get; }
    public uint Entry { get; }
    public int InitPriority { get; }
    public int Priority { get; set; }
    public uint Attr { get; }
    public int Affinity { get; set; }
    /// <summary>The thread currently holds one of the emulated CPU cores (see <see cref="Scheduler"/>).</summary>
    public bool HasCore;
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
        Core = new CpuCore(emu.Code);
        Hle = new HleContext { Cpu = Core.State, Thread = this, Emu = emu };

        StackSize = GuestMemory.AlignUp(Math.Max(stackSize, 0x4000u), 0x1000);
        StackAddr = emu.Memory.Alloc(StackSize, $"stack:{name}");
        GuestMemory.Fill(StackAddr, 0xCC, StackSize);

        uint tlsSize = KernelTlsSize + emu.Kernel.TlsAreaSize;
        TlsBase = emu.Memory.Alloc(Math.Max(tlsSize, 0x1000u), $"tls:{name}");
    }

    void InitTls()
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

    public int Start(uint argSize, uint argp)
    {
        lock (WaitLock)
        {
            if (Status != ThreadStatus.Dormant) return SceError.NOT_DORMANT;
            var s = Core.State;
            new Span<byte>(s, sizeof(CpuState)).Clear();
            s->Fpscr = 0x0300_0000;
            InitTls();
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
        Util.NativeThread.Register(Name);
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
            if (SuspendCount > 0) ParkWhileSuspended();
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
        if (trace) Log.Info($"HLE f{VitaSharp.Core.Hle.Modules.Display.FramesPresented} {f.Name}({s->R[0]:X}, {s->R[1]:X}, {s->R[2]:X}, {s->R[3]:X}) lr={s->R[14]:X8}");
        return trace;
    }

    void DispatchHle(CpuState* s)
    {
        var f = _emu.Hle.GetFunction(s->SvcImm);
        f.CallCount++;
        Core.ChargeInstructions(f.Cost);
        // tracing checks live in a separate method: a lambda capturing f here would allocate a closure on every HLE call
        bool trace = (_emu.TraceHle || _emu.TraceHleOnly != null || _emu.TraceHleFrames != null) && TraceHleCall(f, s);
        if (_emu.BacktraceHle != null && f.Name == _emu.BacktraceHle) Log.Info($"BT {f.Name}({s->R[0]:X}) lr={s->R[14]:X8} {Backtrace(s)}");
        if (f.Invoke != null)
        {
            if (f.IsCallbackWait)
            {
                bool prev = CallbackWait;
                CallbackWait = true;
                try
                {
                    if (PendingCallbacks > 0) CallbackProcessor?.Invoke(this);
                    f.Invoke(Hle);
                }
                finally { CallbackWait = prev; }
            }
            else f.Invoke(Hle);
            if (trace) Log.Info($"  -> {f.Name} = 0x{s->R[0]:X}");
            if (ContextReplaced) { ContextReplaced = false; return; }
        }
        else
        {
            Log.WarnOnce($"nid{f.Nid}", $"Unimplemented HLE function {f.Library}::{f.Name} (NID 0x{f.Nid:X8}) called from 0x{s->R[14]:X8}");
            s->R[0] = 0;
        }
        // The stub's next instruction is "bx lr": return directly.
        Arm.BxWritePc(s, s->R[14]);
    }

    /// <summary>Calls a guest function on this (current) thread and returns R0. Re-entrant.</summary>
    public uint CallGuest(uint addr, params uint[] args)
    {
        var s = Core.State;
        CpuState saved = *s;
        try
        {
            uint sp = (s->R[13] - 64) & ~7u;
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

    void ParkWhileSuspended()
    {
        using var blocked = Scheduler.Block();
        lock (_suspendLock)
            while (SuspendCount > 0 && !Core.StopRequested) Monitor.Wait(_suspendLock, 50);
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
        Log.Error(sb.ToString());
    }

    public void Dispose()
    {
        Core.Dispose();
    }
}
