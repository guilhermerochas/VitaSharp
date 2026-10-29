using System.Collections.Concurrent;
using VitaSharp.Core.Loader;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Kernel;

/// <summary>Kernel state: object table, threads, loaded modules and process-wide parameters.</summary>
public sealed class Kernel
{
    public const uint TrapSvc = 0xFFFFFF;

    readonly Emulator _emu;
    public ObjectTable Objects { get; } = new();
    public ConcurrentDictionary<int, LoadedModule> Modules { get; } = new();

    public uint ThreadExitTrap { get; private set; }
    public uint TlsInit { get; set; }
    public uint TlsInitSize { get; set; }
    public uint TlsAreaSize { get; set; }
    public uint ProcessParam { get; set; }
    public GuestThread? MainThread { get; set; }
    public readonly DateTime BootTime = DateTime.UtcNow;

    public Kernel(Emulator emu) => _emu = emu;

    public void Initialize()
    {
        ThreadExitTrap = _emu.Memory.Alloc(0x1000, "kernel:trap", MemoryRegion.Kernel);
        GuestMemory.W32(ThreadExitTrap, 0xEF000000 | TrapSvc); // svc #TrapSvc
        GuestMemory.W32(ThreadExitTrap + 4, 0xEAFFFFFD);        // b . (never reached)
    }

    /// <summary>Microseconds since boot (process time base used by sceKernelGetProcessTime etc.).</summary>
    public ulong ProcessTimeUs => (ulong)(Util.GuestClock.ToMicroseconds(Util.GuestClock.Observed(GuestThread.Current?.Core.EmulatedPosition() ?? 0)) * Util.TimeScale.Proc);

    /// <summary>SCE_KERNEL_DEFAULT_PRIORITY (0x10000100) +/- n means the default user priority (160) +/- n; taken literally it would
    /// be the lowest priority of all and the strict-priority scheduler would starve the thread.</summary>
    public static int ResolvePriority(int priority)
    {
        const int Sentinel = 0x10000100, DefaultUser = 160;
        return (priority & 0x10000000) != 0 ? Math.Clamp(DefaultUser + (priority - Sentinel), 64, 191) : priority;
    }

    public GuestThread CreateThread(string name, uint entry, int priority, uint stackSize, uint attr, int affinity)
    {
        priority = ResolvePriority(priority);
        var t = new GuestThread(_emu, name, entry, priority, stackSize, attr, affinity);
        Objects.Register(t);
        t.InitTls();
        Log.Info($"Created thread '{name}' uid=0x{t.Uid:X} entry=0x{entry:X8} prio={priority} affinity=0x{affinity:X} stack=0x{stackSize:X} f{Hle.Modules.Display.FramesPresented}");
        return t;
    }

    public void OnThreadExit(GuestThread t) => Log.Debug($"Thread '{t.Name}' (0x{t.Uid:X}) exited with 0x{t.ExitStatus:X}");

    public void DeleteThread(GuestThread t)
    {
        Objects.Remove(t.Uid);
    }

    /// <summary>"Module+0xOFFSET" (offset from the module's first segment) for a code address, for crash reports.</summary>
    public string Symbolize(uint addr)
    {
        uint a = addr & ~1u;
        foreach (var m in Modules.Values)
            foreach (var (idx, s) in m.Segments)
                if (a >= s.Address && a - s.Address < s.Size)
                    return $"{a:X8}({m.Name}+{(idx == 0 ? "" : $"s{idx}:")}{a - s.Address:X})";
        return $"{a:X8}";
    }

    public GuestThread? GetThread(int uid) => uid == 0 ? GuestThread.Current : Objects.Get<GuestThread>(uid);

    public IEnumerable<GuestThread> Threads => Objects.All<GuestThread>();

    public void StopAllThreads()
    {
        foreach (var t in Threads) t.RequestStop();
    }

    /// <summary>Waits for every guest thread to leave its host thread (after StopAllThreads).</summary>
    public bool WaitAllThreadsExit(int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        foreach (var t in Threads.ToList())
        {
            if (t == GuestThread.Current) continue;
            if (!t.WaitForExit((int)Math.Max(1, deadline - Environment.TickCount64))) return false;
        }
        return true;
    }
}
