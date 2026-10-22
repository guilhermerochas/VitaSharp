using System.Text;
using UcArm = UnicornEngine.Const.Arm;
using Decoder = VitaSharp.Core.Cpu.Decoder;
using UnicornEngine;
using VitaSharp.Core.Cpu;
using VitaSharp.Core.Cpu.Backends;
using VitaSharp.Core.Cpu.Native;
using VitaSharp.Core.Memory;

namespace VitaSharp.Tests;

/// <summary>
/// Advanced SIMD in the native backend: blocks of random NEON instructions per encoding class, with operands biased
/// to special values (denormals, NaNs, infinities, values at the smallest normal, lane extremes for saturation),
/// natively against the interpreter (bit for bit, FPSCR.QC included) and against Unicorn.
/// </summary>
public sealed unsafe partial class NativeJitTests
{
    /// <summary>
    /// Gives the code arena back after a fuzz test: the full suite compiles hundreds of thousands of throwaway blocks in
    /// one process and would otherwise fill the 256 MiB arena. Only for tests whose blocks are dead when they end.
    /// </summary>
    sealed class ArenaScope : IDisposable
    {
        readonly int _used = NativeRuntime.Instance.Code.Used;
        public void Dispose() => NativeRuntime.Instance.Code.Reset(_used);
    }

    /// <summary>NEON classes the generator targets (each with its encoding template).</summary>
    public enum NeonClass { ThreeSame, ThreeSameFloat, Scalar, Shift, Misc, Vext, Vtbl, LoadStore, Any }

    /// <summary>A random NEON instruction of <paramref name="cls"/> in ARM form (callers convert it to Thumb).</summary>
    internal static uint RandomNeon(Random rng, NeonClass cls)
    {
        uint r = (uint)rng.NextInt64();
        if (cls == NeonClass.Any) cls = (NeonClass)rng.Next((int)NeonClass.Any);
        switch (cls)
        {
            case NeonClass.ThreeSame: // 1111 001U 0Dsz nnnn dddd AAAA NQMB mmmm
                return 0xF2000000u | (r & 0x017FFFFF);
            case NeonClass.ThreeSameFloat: // A = 1100-1111, sz<0> = 0
                return 0xF2000000u | (r & 0x016FF0FF) | ((uint)rng.Next(12, 16) << 8);
            case NeonClass.Scalar: // 1111 001Q 1Dsz nnnn dddd AAAA N1M0 mmmm, sz = 01/10
            {
                uint[] aField = [0, 1, 4, 5, 8, 9, 1, 9, (uint)rng.Next(16)];
                uint ins = 0xF2800040u | (r & 0x014FF0AF) | (aField[rng.Next(aField.Length)] << 8);
                return ins | ((uint)rng.Next(1, 3) << 20);
            }
            case NeonClass.Shift: // 1111 001U 1D imm6 dddd AAAA LQM1 mmmm
            {
                uint ins = 0xF2800010u | (r & 0x017FFFEF);
                if ((ins & 0x80) == 0 && ((ins >> 16) & 0x3F) < 8) ins |= 8u << 16;
                return ins;
            }
            case NeonClass.Misc: // 1111 0011 1D11 ssAA dddd 0BBB BBM0 mmmm
                return 0xF3B00000u | (r & 0x004FF7EF);
            case NeonClass.Vext: // 1111 0010 1D11 nnnn dddd iiii NQM0 mmmm
                return 0xF2B00000u | (r & 0x004FFFEF);
            case NeonClass.Vtbl: // 1111 0011 1D11 nnnn dddd 10ll NxM0 mmmm
                return 0xF3B00800u | (r & 0x004FF3EF);
            default: // 1111 0100 AD L0 nnnn dddd tttt ssaa mmmm
            {
                // Rn = r4-r12 (data addresses), Rm = r0-r3 (small increments, see NeonState): every access stays inside
                // the data area the test restores between the two runs
                uint ins = 0xF4000000u | (r & 0x00E00FF0);
                ins |= (uint)rng.Next(4, 13) << 16;
                ins |= rng.Next(3) switch { 0 => 15u, 1 => 13u, _ => (uint)rng.Next(4) };
                if (rng.Next(2) == 0) ins |= 1u << 23; // lanes / all lanes
                return ins;
            }
        }
    }

    static uint ToThumb(uint arm) => (arm >> 24) == 0xF4 ? 0xF9000000u | (arm & 0x00FFFFFF)
        : ((arm & 0x01000000) != 0 ? 0xFF000000u : 0xEF000000u) | (arm & 0x00FFFFFF);

    /// <summary>Lanes of 8/16/32/64 bits built from extreme bytes (0, 1, 0x7F, 0x80, 0xFF...) for saturation edges.</summary>
    static ulong SpecialInts(Random rng)
    {
        byte[] special = [0x00, 0x01, 0x7F, 0x80, 0xFF, 0xFE, 0x81, 0x40];
        int lane = 1 << rng.Next(4);
        var bytes = new byte[8];
        for (int i = 0; i < 8; i += lane)
        {
            // top byte special, the rest all zeros / all ones / random
            int fill = rng.Next(3);
            for (int k = 0; k < lane - 1; k++) bytes[i + k] = fill == 0 ? (byte)0 : fill == 1 ? (byte)0xFF : (byte)rng.Next(256);
            bytes[i + lane - 1] = rng.Next(4) == 0 ? (byte)rng.Next(256) : special[rng.Next(special.Length)];
        }
        return BitConverter.ToUInt64(bytes);
    }

    /// <summary>A block of 1-6 NEON instructions of one class (or mixed) whose encodings Unicorn accepts.</summary>
    static Block? NeonBlock(Random rng, bool thumb, NeonClass cls)
    {
        var code = new List<byte>();
        int n = rng.Next(1, 7);
        for (int k = 0, attempts = 0; k < n && attempts < 200; attempts++)
        {
            uint arm = RandomNeon(rng, cls);
            uint ins = thumb ? ToThumb(arm) : arm;
            var d = !thumb ? Decoder.DecodeArm(ins, CodeBase) : Decoder.DecodeThumb32(ins, CodeBase, false, 14);
            if (!d.Op.ToString().StartsWith("Neon_") || d.EndsBlock || CpuFuzzTests.IsUnpredictable(d, thumb) || !SimdDefined(ins, thumb)) continue;
            code.AddRange(!thumb ? BitConverter.GetBytes(ins) : [(byte)(ins >> 16), (byte)(ins >> 24), (byte)ins, (byte)(ins >> 8)]);
            k++;
        }
        if (code.Count == 0) return null;
        code.AddRange(thumb ? [0xFE, 0xE7] : [0xFE, 0xFF, 0xFF, 0xEA]);
        code.ToArray().CopyTo(GuestMemory.Span(CodeBase, 0x1000));
        return BlockBuilder.Build(CodeBase, thumb);
    }

    static void NeonState(Random rng, RandomState st)
    {
        for (int i = 0; i < 32; i++) st.D[i] = rng.Next(3) == 0 ? SpecialInts(rng) : CpuFuzzTests.RandomSpecialSimd(rng);
        // lanes whose products round up to +/- 2^-126 from below (ARM flushes them: tininess before rounding):
        // (1 - 2^-24) * 2^-63 times 2^-63, with both signs
        uint[] tinyEdge = [0x1FFFFFFF, 0x20000000, 0x9FFFFFFF, 0xA0000000, 0x20000001, 0x00800000];
        for (int i = 0; i < 32; i++)
            if (rng.Next(4) == 0)
            {
                ulong lo = tinyEdge[rng.Next(tinyEdge.Length)], hi = tinyEdge[rng.Next(tinyEdge.Length)];
                st.D[i] = lo | (hi << 32);
            }
        st.Fpscr = (uint)rng.Next(32) << 27 | 0x0300_0000; // NZCV and QC random, DN + FZ
        for (int r = 0; r < 4; r++) st.Regs[r] = (uint)rng.Next(-64, 64);
        for (int r = 4; r < 13; r++) st.Regs[r] = DataBase + 0x4000 + (uint)rng.Next(0x6000);
    }

    /// <summary>Quick per-class pass for the fast suite (the long version below runs under Category=Slow).</summary>
    [Theory]
    [InlineData(false, NeonClass.ThreeSame, 400, 1601)]
    [InlineData(true, NeonClass.ThreeSame, 300, 1602)]
    [InlineData(false, NeonClass.ThreeSameFloat, 400, 1603)]
    [InlineData(false, NeonClass.Scalar, 300, 1604)]
    [InlineData(false, NeonClass.Shift, 400, 1605)]
    [InlineData(true, NeonClass.Shift, 300, 1606)]
    [InlineData(false, NeonClass.Misc, 400, 1607)]
    [InlineData(true, NeonClass.Misc, 300, 1608)]
    [InlineData(false, NeonClass.Vext, 300, 1609)]
    [InlineData(false, NeonClass.Vtbl, 300, 1610)]
    [InlineData(false, NeonClass.LoadStore, 400, 1611)]
    [InlineData(true, NeonClass.LoadStore, 300, 1612)]
    [InlineData(false, NeonClass.Any, 400, 1613)]
    [InlineData(true, NeonClass.Any, 400, 1614)]
    public void NativeNeonQuick(bool thumb, NeonClass cls, int iterations, int seed) => NativeNeonMatchesInterpreter(thumb, cls, iterations, seed);

    [Theory]
    [Trait("Category", "Slow")]
    [InlineData(false, NeonClass.ThreeSame, 6_000, 601)]
    [InlineData(true, NeonClass.ThreeSame, 3_000, 602)]
    [InlineData(false, NeonClass.ThreeSameFloat, 6_000, 603)]
    [InlineData(false, NeonClass.Scalar, 4_000, 604)]
    [InlineData(false, NeonClass.Shift, 6_000, 605)]
    [InlineData(true, NeonClass.Shift, 3_000, 606)]
    [InlineData(false, NeonClass.Misc, 6_000, 607)]
    [InlineData(true, NeonClass.Misc, 3_000, 608)]
    [InlineData(false, NeonClass.Vext, 2_000, 609)]
    [InlineData(false, NeonClass.Vtbl, 2_000, 610)]
    [InlineData(false, NeonClass.LoadStore, 6_000, 611)]
    [InlineData(true, NeonClass.LoadStore, 3_000, 612)]
    [InlineData(false, NeonClass.Any, 6_000, 613)]
    [InlineData(true, NeonClass.Any, 6_000, 614)]
    public void NativeNeonMatchesInterpreter(bool thumb, NeonClass cls, int iterations, int seed)
    {
        using var arena = new ArenaScope(); // the code of these throwaway blocks is reclaimed afterwards
        EnsureGuestPages();
        if (int.TryParse(Environment.GetEnvironmentVariable("VITASHARP_FUZZ_ITER"), out int itOverride)) iterations = itOverride;
        bool oldValidate = NativeX64Compiler.ValidateIr;
        NativeX64Compiler.ValidateIr = true;
        var rng = new Random(seed);
        var s = CpuState.Allocate();
        var failures = new Dictionary<string, (int, string)>();
        int compared = 0, detailed = 0, inlined = 0;
        try
        {
            for (int it = 0; it < iterations; it++)
            {
                var b = NeonBlock(rng, thumb, cls);
                if (b == null) continue;
                var st = new RandomState(rng);
                NeonState(rng, st);
                st.Apply(s, thumb);
                var interp = Take(s, Guard(() => CpuCore.Interpret(s, b)));
                var cb = NativeX64Compiler.Instance.Compile(b)!;
                if (new ArmFrontend().Translate(b).Callouts == 0) inlined++;
                st.Apply(s, thumb);
                var native = Take(s, Guard(() => RunNative(s, cb.NativeEntry)));
                compared++;
                string diff = Diff(interp, native);
                if (diff.Length > 0)
                {
                    if (detailed++ < 6) output.WriteLine($"--- fpscr {st.Fpscr:X8} D=[{string.Join(" ", st.D.Select(v => v.ToString("X16")))}]{diff}\n{DescribeNative(b, cb)}");
                    Record(failures, "native", b, diff);
                }
            }
        }
        finally { CpuState.Free(s); NativeX64Compiler.ValidateIr = oldValidate; }
        output.WriteLine($"compared {compared} NEON blocks ({inlined} fully inline); failing kinds {failures.Count}");
        foreach (var (k, (count, ex)) in failures.OrderByDescending(f => f.Value.Item1).Take(40))
            output.WriteLine($"{k,-40} x{count,-5} {ex}");
        Assert.True(failures.Count == 0, $"{failures.Count} kinds of differences (see output)");
    }

    /// <summary>
    /// VCVTB/VCVTT (half &lt;-&gt; single, inline with F16C) against the interpreter: values around the half range limits,
    /// half denormals, rounding ties, f32 denormals, signalling/quiet NaNs, infinities; Vita FPSCR mode and others
    /// (handler fallback).
    /// </summary>
    [Theory]
    [InlineData(false, 3_000, 801)]
    [InlineData(true, 3_000, 802)]
    public void NativeHalfConversionsMatchInterpreter(bool thumb, int iterations, int seed)
    {
        using var arena = new ArenaScope(); // the code of these throwaway blocks is reclaimed afterwards
        EnsureGuestPages();
        uint[] special =
        [
            0, 0x80000000, 0x33800000 /* 2^-24 */, 0x33000000 /* 2^-25 */, 0x33C00000 /* 1.5 * 2^-24 */, 0x387FC000 /* near half min normal */,
            0x477FE000 /* 65504 */, 0x477FF000 /* 65520: rounds to inf */, 0x477FEFFF, 0x3F801000 /* tie */, 0x3F803000 /* tie */,
            0x00000001, 0x807FFFFF /* f32 denormals */, 0x7F800000, 0xFF800000, 0x7F800001, 0xFFC00001, 0x7FC00000, 0x3F800000, 0xC0490FDB,
            0x7C007C00 /* half infs */, 0x7C017E00 /* half NaNs */, 0x00010003 /* half denormals */, 0x83FF8400, 0x3C00BC00,
        ];
        var rng = new Random(seed);
        var s = CpuState.Allocate();
        var failures = new Dictionary<string, (int, string)>();
        int compared = 0;
        try
        {
            for (int it = 0; it < iterations; it++)
            {
                var code = new List<byte>();
                int n = rng.Next(1, 6);
                for (int k = 0; k < n; k++)
                {
                    // 1110 1D11 001o Vd 1010 T1M0 Vm (cond AL)
                    uint ins = 0xEEB20A40u | ((uint)rng.Next(2) << 22) | ((uint)rng.Next(2) << 16) | ((uint)rng.Next(16) << 12)
                        | ((uint)rng.Next(2) << 7) | ((uint)rng.Next(2) << 5) | (uint)rng.Next(16);
                    code.AddRange(!thumb ? BitConverter.GetBytes(ins) : [(byte)(ins >> 16), (byte)(ins >> 24), (byte)ins, (byte)(ins >> 8)]);
                }
                code.AddRange(thumb ? [0xFE, 0xE7] : [0xFE, 0xFF, 0xFF, 0xEA]);
                code.ToArray().CopyTo(GuestMemory.Span(CodeBase, 0x1000));
                var b = BlockBuilder.Build(CodeBase, thumb);
                var st = new RandomState(rng);
                for (int i = 0; i < 32; i++)
                {
                    uint lo = rng.Next(3) == 0 ? (uint)rng.NextInt64() : special[rng.Next(special.Length)];
                    uint hi = rng.Next(3) == 0 ? (uint)rng.NextInt64() : special[rng.Next(special.Length)];
                    st.D[i] = lo | ((ulong)hi << 32);
                }
                st.Fpscr = rng.Next(5) != 0 ? 0x0300_0000u : (uint)rng.Next(16) << 22;
                st.Apply(s, thumb);
                var interp = Take(s, Guard(() => CpuCore.Interpret(s, b)));
                var cb = NativeX64Compiler.Instance.Compile(b)!;
                st.Apply(s, thumb);
                var native = Take(s, Guard(() => RunNative(s, cb.NativeEntry)));
                compared++;
                string diff = Diff(interp, native);
                if (diff.Length > 0) Record(failures, $"fpscr {st.Fpscr:X8}", b, diff);
            }
        }
        finally { CpuState.Free(s); }
        output.WriteLine($"compared {compared} half-conversion blocks; failing kinds {failures.Count}");
        foreach (var (k, (count, ex)) in failures.OrderByDescending(f => f.Value.Item1).Take(20))
            output.WriteLine($"{k,-40} x{count,-5} {ex}");
        Assert.True(failures.Count == 0, $"{failures.Count} kinds of differences (see output)");
    }

    /// <summary>The same NEON blocks natively against Unicorn (QEMU): keeps the shared reference honest.</summary>
    [UnicornTheory]
    [InlineData(false, 4_000, 701)]
    [InlineData(true, 4_000, 702)]
    [Trait("Category", "Slow")]
    public void NativeNeonMatchesUnicorn(bool thumb, int iterations, int seed)
    {
        using var arena = new ArenaScope(); // the code of these throwaway blocks is reclaimed afterwards
        EnsureGuestPages();
        if (int.TryParse(Environment.GetEnvironmentVariable("VITASHARP_FUZZ_ITER"), out int itOverride)) iterations = itOverride;
        var rng = new Random(seed);
        var s = CpuState.Allocate();
        var failures = new Dictionary<string, (int, string)>();
        int compared = 0, skipped = 0, detailed = 0;
        try
        {
            for (int it = 0; it < iterations; it++)
            {
                var b = NeonBlock(rng, thumb, NeonClass.Any);
                if (b == null) continue;
                var st = new RandomState(rng);
                NeonState(rng, st);
                var codeBytes = GuestMemory.Span(CodeBase, 0x1000).ToArray();
                Unicorn? uc = null;
                try
                {
                    uc = CpuFuzzTests.NewUnicorn(thumb);
                    uc.MemWrite(CodeBase, codeBytes);
                    uc.MemWrite(DataBase, st.Data);
                    uc.RegWrite(UcArm.UC_ARM_REG_CPSR, (long)(st.Flags | 0x10 | (thumb ? 0x20u : 0)));
                    for (int r = 0; r < 15; r++) uc.RegWrite(RegIds[r], st.Regs[r]);
                    for (int r = 0; r < 32; r++) uc.RegWrite(UcArm.UC_ARM_REG_D0 + r, (long)st.D[r]);
                    uc.RegWrite(UcArm.UC_ARM_REG_FPSCR, st.Fpscr);
                    uc.EmuStart(CodeBase | (thumb ? 1u : 0), CodeBase + 0x800, 0, b.Insts.Length);
                }
                catch (Exception) { skipped++; uc?.Close(); continue; }
                st.Apply(s, thumb);
                var cb = NativeX64Compiler.Instance.Compile(b)!;
                if (Guard(() => RunNative(s, cb.NativeEntry)) != null) { skipped++; uc.Close(); continue; }
                compared++;
                var sb = new StringBuilder();
                for (int r = 0; r < 15; r++)
                {
                    uint u = (uint)uc.RegRead(RegIds[r]);
                    if (u != s->R[r]) sb.Append($" R{r}: uc={u:X8} us={s->R[r]:X8}");
                }
                for (int r = 0; r < 32; r++)
                {
                    ulong u = (ulong)uc.RegRead(UcArm.UC_ARM_REG_D0 + r);
                    if (u != s->D[r]) sb.Append($" D{r}: uc={u:X16} us={s->D[r]:X16}");
                }
                uint ucFpscr = (uint)uc.RegRead(UcArm.UC_ARM_REG_FPSCR);
                if ((ucFpscr & 0xF8000000) != (s->Fpscr & 0xF8000000)) sb.Append($" FPSCR: uc={ucFpscr:X8} us={s->Fpscr:X8}");
                var ucData = new byte[DataSize];
                uc.MemRead(DataBase, ucData);
                var ours = GuestMemory.Span(DataBase, (int)DataSize);
                for (int i = 0; i < DataSize; i++) if (ucData[i] != ours[i]) { sb.Append($" MEM@{DataBase + i:X8}: uc={ucData[i]:X2} us={ours[i]:X2}"); break; }
                uc.Close();
                if (sb.Length > 0)
                {
                    if (detailed++ < 6) output.WriteLine($"--- fpscr {st.Fpscr:X8} D=[{string.Join(" ", st.D.Select(v => v.ToString("X16")))}]{sb}\n{DescribeNative(b, cb)}");
                    Record(failures, "unicorn", b, sb.ToString());
                }
            }
        }
        finally { CpuState.Free(s); }
        output.WriteLine($"compared {compared} NEON blocks against Unicorn, skipped {skipped}; failing kinds {failures.Count}");
        foreach (var (k, (count, ex)) in failures.OrderByDescending(f => f.Value.Item1).Take(40))
            output.WriteLine($"{k,-40} x{count,-5} {ex}");
        Assert.True(failures.Count == 0, $"{failures.Count} kinds of differences against Unicorn (see output)");
    }
}
