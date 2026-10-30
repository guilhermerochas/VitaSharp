using System.Diagnostics;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Cpu.Backends;

/// <summary>
/// Where a native backend spends its compile time, summed over all blocks (Stopwatch ticks): IR build (frontend), IR
/// passes, register allocation, lowering into the assembler, encoding, and installing the code in the arena. Wall time
/// on the compile thread, so contention with emulation threads inflates it; NativeJitTests.CompileCostBenchmark measures
/// the same phases on an idle thread.
/// </summary>
public sealed class CompilePhases
{
    public long Frontend, Passes, Alloc, Lower, Encode, Install, Blocks;

    public void Add(long frontend, long passes, long alloc, long lower, long encode, long install)
    {
        Interlocked.Add(ref Frontend, frontend);
        Interlocked.Add(ref Passes, passes);
        Interlocked.Add(ref Alloc, alloc);
        Interlocked.Add(ref Lower, lower);
        Interlocked.Add(ref Encode, encode);
        Interlocked.Add(ref Install, install);
        Interlocked.Increment(ref Blocks);
    }

    public override string ToString()
    {
        double f = 1e6 / Stopwatch.Frequency / Math.Max(1, Blocks);
        return $"phases per block: frontend {Frontend * f:F1} us, passes {Passes * f:F1}, register allocation {Alloc * f:F1}, " +
               $"lowering {Lower * f:F1}, encoding {Encode * f:F1}, install {Install * f:F1}";
    }

    // ------------------------------------------------------------------ corpus (VITASHARP_NATIVE_CORPUS)

    static readonly StreamWriter? Corpus = Environment.GetEnvironmentVariable(EnvVars.NativeCorpus) is { Length: > 0 } p
        ? new StreamWriter(p, append: true) { AutoFlush = false } : null;

    static readonly Lock CorpusLock = new();

    /// <summary>
    /// Appends the block to the corpus file when one is configured: "A|T start size:raw ...", one line per block, the
    /// instructions in memory order (Thumb-2 raw = first halfword &lt;&lt; 16 | second).
    /// </summary>
    public static void Record(Block b)
    {
        if (Corpus == null) return;
        var sb = new System.Text.StringBuilder(16 + b.Insts.Length * 12);
        sb.Append(b.Thumb ? 'T' : 'A').Append(' ').Append(b.Start.ToString("X8"));
        foreach (ref readonly var d in b.Insts.AsSpan()) sb.Append(' ').Append(d.Size).Append(':').Append(d.Raw.ToString("X"));
        lock (CorpusLock)
        {
            Corpus.WriteLine(sb.ToString());
            Corpus.Flush();
        }
    }
}
