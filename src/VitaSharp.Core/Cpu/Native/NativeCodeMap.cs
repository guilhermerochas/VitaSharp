using System.Text;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Cpu.Native;

/// <summary>
/// Profiling aid (<see cref="EnvVars.NativeMap"/>=file): one line per piece of generated code,
/// <c>host-address size guest-pc arm|thumb hex-bytes</c>, so the samples a CPU profiler (simpleperf on a phone, perf on
/// Linux) puts in the anonymous code arena can be attributed to guest blocks and disassembled on another machine.
/// </summary>
public static unsafe class NativeCodeMap
{
    static readonly StreamWriter? Writer = Open();

    public static bool Enabled => Writer != null;

    static StreamWriter? Open()
    {
        if (Environment.GetEnvironmentVariable(EnvVars.NativeMap) is not { Length: > 0 } path) return null;
        try
        {
            var w = new StreamWriter(path, append: false, Encoding.ASCII) { AutoFlush = true };
            Log.Info($"Native code map: {path}");
            return w;
        }
        catch (Exception e) { Log.Warn($"Native code map {path}: {e.Message}"); return null; }
    }

    /// <summary>Records code at <paramref name="code"/> (<paramref name="size"/> bytes) for <paramref name="guestPc"/>
    /// (0 for runtime stubs, <paramref name="name"/> then says which).</summary>
    public static void Add(nint code, int size, uint guestPc, bool thumb, string? name = null)
    {
        var w = Writer;
        if (w == null) return;
        var sb = new StringBuilder(size * 2 + 48);
        sb.Append($"{code:X} {size} {guestPc:X8} {(name ?? (thumb ? "thumb" : "arm"))} ");
        var bytes = new ReadOnlySpan<byte>((void*)code, size);
        foreach (byte b in bytes) sb.Append(b.ToString("X2"));
        lock (w) w.WriteLine(sb.ToString());
    }
}
