using BenchmarkDotNet.Attributes;
using VitaSharp.Core;
using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;

namespace VitaSharp.Benchmarks;

/// <summary>
/// The cost of the HLE's own calls going through the generated <c>XxxThroughEntry</c> wrapper when no hook exists (the
/// normal case): the same call made directly and through the wrapper (sceIoLseek on a closed descriptor, a short
/// body), and the wrapper's check alone (<see cref="HleEntryCalls.AnyHook"/>), with the firmware kernel modules shown
/// (the check reads their patch count) as with an installed firmware. The difference is the wrapper's whole cost; it
/// allocates nothing.
/// </summary>
[MemoryDiagnoser]
public class HleEntryCallBenchmarks
{
    private GuestMemory? _memory;
    private Emulator? _emu;
    private string _root = "";
    private IoFileMgr _io = null!;

    // the arguments come from fields set in Setup, so the JIT cannot fold either call into a constant
    private int _fd;
    private long _offset;
    private int _whence;

    [GlobalSetup]
    public void Setup()
    {
        _memory = new GuestMemory();
        _root = Path.Combine(Path.GetTempPath(), "vitasharp-bench-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _emu = new Emulator(new EmulatorOptions { FsRoot = _root, Memory = _memory, EnableJit = false, CpuBackend = "interp" });
        _io = _emu.Hle.Modules.OfType<IoFileMgr>().Single();
        _ = _emu.FirmwareKernel;
        _fd = -1;
        _offset = 0;
        _whence = 0;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _emu?.Dispose();
        _memory?.Dispose();
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Benchmark(Baseline = true)]
    public long DirectCall() => _io.sceIoLseek(_fd, _offset, _whence);

    [Benchmark]
    public long ThroughEntry() => _io.sceIoLseekThroughEntry(_fd, _offset, _whence);

    [Benchmark]
    public bool AnyHookCheck() => HleEntryCalls.AnyHook();
}
