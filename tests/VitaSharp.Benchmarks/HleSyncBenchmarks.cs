using BenchmarkDotNet.Attributes;
using VitaSharp.Core;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using KMutex = VitaSharp.Core.Kernel.Mutex;
using KSemaphore = VitaSharp.Core.Kernel.Semaphore;

namespace VitaSharp.Benchmarks;

/// <summary>
/// The kernel side of the most called HLE functions in games (PERF calls): uncontended lightweight mutex lock/unlock
/// as sceKernelLockLwMutex/UnlockLwMutex do it after the guest fast path fell back (uid from the work area, the kernel
/// object, the owner word in guest memory), a normal mutex, and a semaphore signal/poll. The uncontended cost is what
/// the guest pays on every call; blocking waits are measured by the PERF lines of a game run, not here.
/// </summary>
public class HleSyncBenchmarks
{
    GuestMemory? _memory;
    Emulator? _emu;
    string _root = "";
    GuestThread _self = null!;
    ObjectTable _objects = null!;
    uint _work;
    KMutex _mutex = null!;
    KSemaphore _sema = null!;

    [GlobalSetup]
    public void Setup()
    {
        _memory = new GuestMemory();
        _root = Path.Combine(Path.GetTempPath(), "vitasharp-bench-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _emu = new Emulator(new EmulatorOptions { FsRoot = _root, Memory = _memory, EnableJit = false, CpuBackend = "interp" });
        _objects = _emu.Kernel.Objects;
        _self = _emu.Kernel.CreateThread("bench", 0, 0x10000100, 0x10000, 0, 0x70000);
        _work = _memory.Alloc(0x1000, "bench lwmutex");
        GuestMemory.Span(_work, 32).Clear();
        var lw = new KMutex { Name = "bench lw", Attr = 0, InitCount = 0, WorkArea = _work, Threads = _objects };
        int uid = _objects.Register(lw);
        GuestMemory.W32(_work + KMutex.LwAttrOffset, 0);
        GuestMemory.W32(_work + KMutex.LwUidOffset, (uint)uid);
        _mutex = new KMutex { Name = "bench", Attr = 0, InitCount = 0, Threads = _objects };
        _objects.Register(_mutex);
        _sema = new KSemaphore { Name = "bench", InitCount = 0, Count = 0, MaxCount = 1 << 20 };
        _objects.Register(_sema);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _emu?.Dispose();
        _memory?.Dispose();
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Benchmark]
    public int LwMutexLockUnlock()
    {
        var m = _objects.Get<KMutex>((int)GuestMemory.R32(_work + KMutex.LwUidOffset))!;
        int r = m.Lock(_self, 1, default);
        return r | m.Unlock(_self, 1);
    }

    [Benchmark]
    public int LwMutexTryLockUnlock()
    {
        var m = _objects.Get<KMutex>((int)GuestMemory.R32(_work + KMutex.LwUidOffset))!;
        int r = m.TryLock(_self, 1);
        return r | m.Unlock(_self, 1);
    }

    [Benchmark]
    public int MutexLockUnlock() => _mutex.Lock(_self, 1, default) | _mutex.Unlock(_self, 1);

    [Benchmark]
    public int SemaSignalPoll() => _sema.Signal(1) | _sema.Poll(1);
}
