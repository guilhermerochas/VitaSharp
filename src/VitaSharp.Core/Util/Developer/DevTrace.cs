#if VITASHARP_DEVELOPER
using System.Runtime.InteropServices;
using System.Text;

namespace VitaSharp.Core.Util.Developer;

/// <summary>
/// Developer builds only: Perfetto/systrace markers through the NDK's ATrace (libandroid.so), switched on by
/// VITASHARP_TRACE=1 (build/android-trace.* writes it to env.txt). Off — the default, and always off outside Android —
/// every hook is one static readonly test. Markers: guest frames (sceDisplaySetFrameBuf, a counter and an instant
/// slice), the GPU worker's frame recording and vkQueueSubmit, sampled HLE calls (one in VITASHARP_TRACE_HLE, default
/// 64, named after the function), native JIT compiles and audio output grains. See tools/dev/README.md.
/// </summary>
public static unsafe class DevTrace
{
    public static readonly bool On = OperatingSystem.IsAndroid() && Environment.GetEnvironmentVariable(EnvVars.Trace) is "1" or "on";
    /// <summary>HLE calls traced: one in (mask + 1).</summary>
    static readonly int HleMask = (int)Math.Max(1, System.Numerics.BitOperations.RoundUpToPowerOf2(
        uint.TryParse(Environment.GetEnvironmentVariable(EnvVars.TraceHle), out var n) && n > 0 ? n : 64)) - 1;
    static int _hleCount;
    static long _guestFrames;
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, nint> Names = new();

    [DllImport("android", EntryPoint = "ATrace_beginSection")] static extern void BeginSection(byte* name);
    [DllImport("android", EntryPoint = "ATrace_endSection")] static extern void EndSection();
    [DllImport("android", EntryPoint = "ATrace_setCounter")] static extern void SetCounter(byte* name, long value);

    /// <summary>A NUL-terminated copy of <paramref name="s"/> that lives as long as the process (names repeat).</summary>
    static byte* Name(string s) => (byte*)Names.GetOrAdd(s, static k =>
    {
        var b = Encoding.UTF8.GetBytes(k + "\0");
        nint p = Marshal.AllocHGlobal(b.Length);
        Marshal.Copy(b, 0, p, b.Length);
        return p;
    });

    public static void Begin(string name) { if (On) BeginSection(Name(name)); }
    public static void End() { if (On) EndSection(); }
    public static void Counter(string name, long value) { if (On) SetCounter(Name(name), value); }

    /// <summary>sceDisplaySetFrameBuf: one guest frame.</summary>
    public static void GuestFrame()
    {
        if (!On) return;
        long f = Interlocked.Increment(ref _guestFrames);
        fixed (byte* c = "guest frame\0"u8) SetCounter(c, f);
        fixed (byte* s = "guest flip\0"u8) BeginSection(s);
        EndSection();
    }

    /// <summary>True when this HLE call is one of the sampled ones (its slice is begun; end it with <see cref="End"/>).</summary>
    public static bool BeginHle(string function)
    {
        if (!On || (Interlocked.Increment(ref _hleCount) & HleMask) != 0) return false;
        BeginSection(Name("hle " + function));
        return true;
    }

    /// <summary>GPU worker: the frame being recorded (begun before its first command, ended by its present).</summary>
    [ThreadStatic] static bool _gpuFrameOpen;
    [ThreadStatic] static long _gpuCommands;

    public static void GpuCommand(bool present)
    {
        if (!On) return;
        if (!_gpuFrameOpen) { fixed (byte* s = "gpu record frame\0"u8) BeginSection(s); _gpuFrameOpen = true; }
        _gpuCommands++;
        if (!present) return;
        fixed (byte* c = "gpu commands/frame\0"u8) SetCounter(c, _gpuCommands);
        _gpuCommands = 0;
    }

    /// <summary>After the present command executed: the frame's slice ends.</summary>
    public static void GpuPresented()
    {
        if (!On || !_gpuFrameOpen) return;
        EndSection();
        _gpuFrameOpen = false;
    }
}
#endif
