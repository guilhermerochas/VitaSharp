using System.Diagnostics;
using VitaSharp.Core.Util;
using VitaSharp.Core.Cpu;
using VitaSharp.Core.Cpu.Native;
using VitaSharp.Core.Memory;

namespace VitaSharp.Tests;

/// <summary>
/// Compile cost of the native backends per phase on an idle thread: IR build, passes, register allocation, lowering
/// (Prepare minus allocation) and encoding, for x64 and ARM64, in microseconds and allocated bytes per block. The blocks
/// come from a corpus recorded in a game (VITASHARP_NATIVE_CORPUS=file while running it, then VITASHARP_BENCH_CORPUS=file here),
/// else from the random block generator. Only reports; never fails on timings.
/// </summary>
public sealed unsafe partial class NativeJitTests
{
    [NativeFact]
    [Trait("Category", "Slow")]
    public void CompileCostBenchmark()
    {
        EnsureGuestPages();
        var blocks = new List<(bool Thumb, byte[] Code)>();
        string? path = Environment.GetEnvironmentVariable(EnvVars.BenchCorpus);
        if (path is { Length: > 0 } && File.Exists(path))
        {
            foreach (var line in File.ReadLines(path))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 3) continue;
                bool thumb = parts[0] == "T";
                var bytes = new List<byte>();
                foreach (var p in parts.Skip(2))
                {
                    int c = p.IndexOf(':');
                    int size = int.Parse(p[..c]);
                    uint raw = Convert.ToUInt32(p[(c + 1)..], 16);
                    if (size == 2) bytes.AddRange([(byte)raw, (byte)(raw >> 8)]);
                    else if (thumb) bytes.AddRange([(byte)(raw >> 16), (byte)(raw >> 24), (byte)raw, (byte)(raw >> 8)]);
                    else bytes.AddRange(BitConverter.GetBytes(raw));
                }
                blocks.Add((thumb, bytes.ToArray()));
                if (blocks.Count >= 20_000) break;
            }
        }
        else
        {
            var rng = new Random(77);
            for (int i = 0; i < 3_000; i++)
            {
                bool thumb = (i & 1) != 0;
                var b = RandomBlock(rng, thumb, 12, forUnicorn: false);
                if (b != null) blocks.Add((thumb, GuestMemory.Span(CodeBase, 0x1000)[..(int)(b.End - b.Start + 4)].ToArray()));
            }
        }
        output.WriteLine($"corpus: {blocks.Count} blocks ({(path is { Length: > 0 } ? path : "random")})");

        var decoded = new List<Block>();
        foreach (var (thumb, code) in blocks)
        {
            var mem = GuestMemory.Span(CodeBase, 0x1000);
            mem.Clear();
            code.AsSpan(0, Math.Min(code.Length, 0xFF0)).CopyTo(mem);
            // a branch-to-self after the block keeps the builder inside the written code
            var pad = thumb ? new byte[] { 0xFE, 0xE7 } : [0xFE, 0xFF, 0xFF, 0xEA];
            pad.CopyTo(mem[Math.Min(code.Length, 0xFF0)..]);
            decoded.Add(BlockBuilder.Build(CodeBase, thumb));
        }
        long insts = decoded.Sum(b => (long)b.Insts.Length);
        output.WriteLine($"{insts} guest instructions, {insts / (double)decoded.Count:F1} per block");

        // end to end through the compilers (IR, passes, allocation, lowering, encoding, arena install), several rounds;
        // the per-phase split comes from BackendStats.Phases where the build has it (looked up by reflection so this file
        // also builds against older trees for before/after numbers)
        var phasesField = typeof(VitaSharp.Core.Cpu.Backends.BackendStats).GetField("Phases");
        using var sb = Arm64Runner.Create();
        foreach (bool arm64 in new[] { false, true })
        {
            VitaSharp.Core.Cpu.Backends.IBlockCompiler compiler = arm64 ? VitaSharp.Core.Cpu.Backends.NativeArm64Compiler.Instance : VitaSharp.Core.Cpu.Backends.NativeX64Compiler.Instance;
            var results = new List<double>();
            for (int round = 0; round < 7; round++)
            {
                using var arena = new ArenaScope();
                object? before = phasesField?.GetValue(compiler.Stats) is { } p0 ? Snapshot(p0) : null;
                long m0 = GC.GetAllocatedBytesForCurrentThread(), c0 = Stopwatch.GetTimestamp();
                foreach (var b in decoded)
                {
                    if (arm64) VitaSharp.Core.Cpu.Backends.NativeArm64Compiler.Instance.Compile(b, sb.Stubs);
                    else VitaSharp.Core.Cpu.Backends.NativeX64Compiler.Instance.Compile(b);
                }
                double us = (Stopwatch.GetTimestamp() - c0) * 1e6 / Stopwatch.Frequency / decoded.Count;
                long bytes = (GC.GetAllocatedBytesForCurrentThread() - m0) / decoded.Count;
                results.Add(us);
                string phases = phasesField?.GetValue(compiler.Stats) is { } p1 && before != null ? Delta(before, p1) : "";
                output.WriteLine($"{(arm64 ? "arm64" : "x64  ")} round {round}: {us:F1} us/block, {bytes} B allocated/block {phases}");
            }
            results.Sort();
            output.WriteLine($"{(arm64 ? "arm64" : "x64")}: median {results[results.Count / 2]:F1} us/block, best {results[0]:F1}");
        }

        static object Snapshot(object p) =>
            p.GetType().GetFields().Where(f => f.FieldType == typeof(long)).ToDictionary(f => f.Name, f => (long)f.GetValue(p)!);
        static string Delta(object before, object p)
        {
            var b = (Dictionary<string, long>)before;
            var now = p.GetType().GetFields().Where(f => f.FieldType == typeof(long)).ToDictionary(f => f.Name, f => (long)f.GetValue(p)!);
            double n = Math.Max(1, now["Blocks"] - b["Blocks"]);
            return "(" + string.Join(", ", now.Where(kv => kv.Key != "Blocks").Select(kv => $"{kv.Key} {(kv.Value - b[kv.Key]) * 1e6 / Stopwatch.Frequency / n:F1}")) + " us)";
        }
    }

    /// <summary>
    /// What keeping NEON registers in host registers could save per op on x64: 128 VADD.F32/VADD.I32 per block, one
    /// dependent chain through q0 (every op reloads what the previous one just stored: store-forwarding latency) and
    /// eight independent chains (q0-q7), timed natively. A pinned chain would run at the instruction latency alone.
    /// </summary>
    [NativeFact]
    [Trait("Category", "Slow")]
    public void NeonRegisterFileBenchmark()
    {
        EnsureGuestPages();
        using var arena = new ArenaScope();
        var s = CpuState.Allocate();
        try
        {
            foreach (var (name, word, rotate) in new[] { ("vadd.f32 dependent", 0xF2000D42u, false), ("vadd.f32 8 chains", 0xF2000D42u, true), ("vadd.i32 dependent", 0xF2200842u, false), ("vadd.i32 8 chains", 0xF2200842u, true) })
            {
                var code = new List<byte>();
                for (int i = 0; i < 128; i++)
                {
                    uint q = rotate ? (uint)(i & 7) : 0, ins = word | (q * 2) << 12 | (q * 2) << 16; // Qd = Qn = q, Qm = q1... (d2)
                    if (rotate && q == 1) ins = (word & ~0xFF0FF000u) | 0x00080000u | 0x8000u; // q1 is the addend: use q8 instead
                    code.AddRange(BitConverter.GetBytes(ins));
                }
                code.AddRange([0xFE, 0xFF, 0xFF, 0xEA]);
                code.ToArray().CopyTo(GuestMemory.Span(CodeBase, 0x1000));
                var b = BlockBuilder.Build(CodeBase, false);
                var cb = VitaSharp.Core.Cpu.Backends.NativeX64Compiler.Instance.Compile(b)!;
                *s = default; s->Fpscr = 0x03000000;
                for (int i = 0; i < 32; i++) s->D[i] = 0x3F8000003F800000;
                int ops = b.Insts.Count(x => x.Op == Op.Neon_ThreeSame);
                double best = double.MaxValue;
                for (int round = 0; round < 5; round++)
                {
                    long t0 = Stopwatch.GetTimestamp();
                    for (int k = 0; k < 20_000; k++) RunNative(s, cb.NativeEntry);
                    double ns = (Stopwatch.GetTimestamp() - t0) * 1e9 / Stopwatch.Frequency / 20_000 / ops;
                    best = Math.Min(best, ns);
                }
                output.WriteLine($"{name}: {best:F2} ns per op ({ops} ops per block, block entry/exit included)");
            }
        }
        finally { CpuState.Free(s); }
    }
}
