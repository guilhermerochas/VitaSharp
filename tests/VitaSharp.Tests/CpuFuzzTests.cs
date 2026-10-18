using System.Text;
using UcArm = UnicornEngine.Const.Arm;
using Decoder = VitaSharp.Core.Cpu.Decoder;
using UnicornEngine;
using UnicornEngine.Const;
using VitaSharp.Core.Cpu;
using VitaSharp.Core.Memory;
using Xunit;
using Xunit.Abstractions;

namespace VitaSharp.Tests;

public sealed class GuestMemoryFixture : IDisposable
{
    public GuestMemory Memory { get; } = new();
    public BlockCache Cache { get; } = new();
    public void Dispose() { }
}

[CollectionDefinition("guest")]
public sealed class GuestCollection : ICollectionFixture<GuestMemoryFixture>;

/// <summary>
/// Differential fuzzing of the interpreter against Unicorn (QEMU's ARM model): random instructions are
/// executed one at a time on both CPUs from identical random state and the resulting state is compared.
/// </summary>
[Collection("guest")]
public sealed unsafe class CpuFuzzTests(GuestMemoryFixture fx, ITestOutputHelper output)
{
    const uint CodeBase = 0x0010_0000, DataBase = 0x0020_0000, DataSize = 0x10000;

    static readonly int[] RegIds =
    [
        UcArm.UC_ARM_REG_R0, UcArm.UC_ARM_REG_R1, UcArm.UC_ARM_REG_R2, UcArm.UC_ARM_REG_R3, UcArm.UC_ARM_REG_R4,
        UcArm.UC_ARM_REG_R5, UcArm.UC_ARM_REG_R6, UcArm.UC_ARM_REG_R7, UcArm.UC_ARM_REG_R8, UcArm.UC_ARM_REG_R9,
        UcArm.UC_ARM_REG_R10, UcArm.UC_ARM_REG_R11, UcArm.UC_ARM_REG_R12, UcArm.UC_ARM_REG_R13, UcArm.UC_ARM_REG_R14,
    ];

    static Unicorn NewUnicorn(bool thumb)
    {
        var uc = new Unicorn(Common.UC_ARCH_ARM, thumb ? Common.UC_MODE_THUMB : Common.UC_MODE_ARM);
        uc.MemMap(CodeBase, 0x1000, Common.UC_PROT_ALL);
        uc.MemMap(DataBase, DataSize, Common.UC_PROT_ALL);
        // enable VFP/NEON from privileged mode: CPACR full access to cp10/cp11, then FPEXC.EN
        uint[] init =
        [
            0xEE110F50, // mrc p15, 0, r0, c1, c0, 2
            0xE380060F, // orr r0, r0, #0xF00000
            0xEE010F50, // mcr p15, 0, r0, c1, c0, 2
            0xF57FF06F, // isb
            0xE3A00101, // mov r0, #0x40000000
            0xEEE80A10, // vmsr fpexc, r0
        ];
        var bytes = init.SelectMany(BitConverter.GetBytes).ToArray();
        uc.MemWrite(CodeBase, bytes);
        uc.EmuStart(CodeBase, CodeBase + (uint)bytes.Length, 0, 0);
        return uc;
    }

    void EnsureGuestPages()
    {
        if (fx.Memory.FindBlock(CodeBase) == null) fx.Memory.TryAllocAt(CodeBase, 0x1000, "fuzz code");
        if (fx.Memory.FindBlock(DataBase) == null) fx.Memory.TryAllocAt(DataBase, DataSize, "fuzz data");
    }

    /// <summary>Re-runs specific encodings (from a fuzz failure) with many random states and prints every difference.</summary>
    [Theory]
    [InlineData(true, "0000BF00")]
    public void Replay(bool thumb, string encodings)
    {
        foreach (var enc in encodings.Split(','))
            Fuzz(thumb, 40, 7, Convert.ToUInt32(enc, 16));
    }

    [Theory]
    [InlineData(false, 30_000, 1)]
    [InlineData(true, 30_000, 2)]
    public void FuzzAgainstUnicorn(bool thumb, int iterations, int seed) => Fuzz(thumb, iterations, seed, null);

    void Fuzz(bool thumb, int iterations, int seed, uint? fixedIns)
    {
        EnsureGuestPages();
        var rng = new Random(seed);
        using var core = new CpuCore(new BlockCache());
        var uc = NewUnicorn(thumb);
        var failures = new Dictionary<Op, (int count, string example)>();
        int compared = 0, skipped = 0;
        var data = new byte[DataSize];

        for (int it = 0; it < iterations; it++)
        {
            uint ins;
            byte[] code;
            if (fixedIns is { } fi)
            {
                ins = fi;
                code = !thumb ? BitConverter.GetBytes(ins) : ins > 0xFFFF ? [(byte)(ins >> 16), (byte)(ins >> 24), (byte)ins, (byte)(ins >> 8)] : [(byte)ins, (byte)(ins >> 8)];
            }
            else if (!thumb)
            {
                ins = (uint)rng.NextInt64();
                if ((ins >> 28) == 0xF && rng.Next(4) != 0) ins = (ins & 0x0FFFFFFF) | 0xE000_0000;
                code = BitConverter.GetBytes(ins);
            }
            else
            {
                ushort hw1 = (ushort)rng.Next(0x10000);
                if (rng.Next(2) == 0) hw1 |= 0xE800; // bias towards 32-bit encodings
                if (Decoder.IsThumb32(hw1))
                {
                    ushort hw2 = (ushort)rng.Next(0x10000);
                    ins = ((uint)hw1 << 16) | hw2;
                    code = [(byte)hw1, (byte)(hw1 >> 8), (byte)hw2, (byte)(hw2 >> 8)];
                }
                else
                {
                    ins = hw1;
                    code = [(byte)hw1, (byte)(hw1 >> 8)];
                }
            }

            // decode with our decoder
            DecodedInst d;
            if (!thumb) d = Decoder.DecodeArm(ins, CodeBase);
            else d = code.Length == 4 ? Decoder.DecodeThumb32(ins, CodeBase, false, 14) : Decoder.DecodeThumb16(ins, CodeBase, false, 14);
            if (d.Op is Op.A32_Udf or Op.T16_Udf or Op.T32_Udf)
            {
                // Our decoder claims UNDEFINED: Unicorn must agree (raise an exception) or we have a decoding hole.
                bool ucRan;
                try
                {
                    uc.Close(); uc = NewUnicorn(thumb);
                    uc.MemWrite(CodeBase, (!thumb && (ins >> 28) < 0xE ? BitConverter.GetBytes((ins & 0x0FFFFFFFu) | 0xE0000000u) : code));
                    uc.RegWrite(UcArm.UC_ARM_REG_CPSR, (long)(0x10u | (thumb ? 0x20u : 0)));
                    for (int r = 0; r < 15; r++) uc.RegWrite(RegIds[r], DataBase + 0x8000);
                    uc.EmuStart(CodeBase | (thumb ? 1u : 0), CodeBase + 0x800, 0, 1);
                    uint pcAfter = (uint)uc.RegRead(UcArm.UC_ARM_REG_PC);
                    ucRan = (pcAfter & ~1u) != 0x4 && (pcAfter & ~1u) != 0xFFFF0004; // undefined-instruction vector taken = UNDEF
                }
                catch (Exception) { ucRan = false; uc.Close(); uc = NewUnicorn(thumb); }
                // Unicorn silently ignores accesses to coprocessors the Cortex-A9 does not have.
                uint cp = (ins >> 8) & 0xF;
                bool absentCoproc = (thumb ? (ins >> 25 & 7) == 6 || (ins >> 24 & 0xF) == 0xE : (ins >> 25 & 7) == 6 || (ins >> 24 & 0xF) == 0xE)
                                    && cp != 10 && cp != 11 && cp != 15;
                if (ucRan && !absentCoproc) Record(failures, d.Op, $"{ins:X8}: decoded as UNDEFINED but executes on hardware");
                skipped++;
                continue;
            }
            if (d.Op is Op.A32_Svc or Op.T16_Svc or Op.A32_Bkpt or Op.T16_Bkpt
                or Op.Thumb_It or Op.A32_Yield or Op.T16_Yield or Op.T32_Yield or Op.A32_Mrc or Op.A32_Mcr or Op.T32_Mrc or Op.T32_Mcr
                or Op.Vfp_Vmrs or Op.Vfp_Vmsr or Op.A32_Mrs or Op.T32_Mrs or Op.A32_MsrImm or Op.A32_MsrReg or Op.T32_Msr
                or Op.T32_BxjOrSubsPcLr)
            { skipped++; continue; }
            if (IsUnpredictable(d, thumb)) { skipped++; continue; }

            // random initial state; most registers point into the data window so memory ops stay mapped
            var regs = new uint[16];
            for (int r = 0; r < 15; r++)
                regs[r] = rng.Next(3) == 0 ? (uint)rng.NextInt64() : DataBase + 0x4000 + (uint)rng.Next(0x4000) * (rng.Next(2) == 0 ? 4u : 1u) % 0x7000;
            uint flags = (uint)rng.Next(32) << 27 | (uint)rng.Next(16) << 16;
            var dregs = new ulong[32];
            for (int r = 0; r < 32; r++) dregs[r] = RandomSimd(rng);
            uint fpscr = (uint)rng.Next(16) << 28 | 0x0300_0000; // DN + FZ like the Vita runtime
            rng.NextBytes(data);

            // ---- unicorn
            try
            {
                uc.Close(); uc = NewUnicorn(thumb); // fresh instance: QEMU caches translated code per address
                uc.MemWrite(CodeBase, code);
                uc.MemWrite(DataBase, data);
                uc.RegWrite(UcArm.UC_ARM_REG_CPSR, (long)(flags | 0x10 | (thumb ? 0x20u : 0)));
                for (int r = 0; r < 15; r++) uc.RegWrite(RegIds[r], regs[r]);
                for (int r = 0; r < 32; r++) uc.RegWrite(UcArm.UC_ARM_REG_D0 + r, (long)dregs[r]);
                uc.RegWrite(UcArm.UC_ARM_REG_FPSCR, fpscr);
                uc.EmuStart(CodeBase | (thumb ? 1u : 0), CodeBase + 0x800, 0, 1);
            }
            catch (Exception) { skipped++; uc.Close(); uc = NewUnicorn(thumb); continue; }

            // ---- ours
            var s = core.State;
            for (int r = 0; r < 15; r++) s->R[r] = regs[r];
            s->R[15] = CodeBase;
            s->Cpsr = flags | 0x10 | (thumb ? 0x20u : 0);
            for (int r = 0; r < 32; r++) s->D[r] = dregs[r];
            s->Fpscr = fpscr;
            s->Exit = ExitReason.None;
            data.CopyTo(GuestMemory.Span(DataBase, (int)DataSize));
            var ucCode = new byte[0x1000];
            uc.MemRead(CodeBase, ucCode);
            ucCode.CopyTo(GuestMemory.Span(CodeBase, 0x1000));
            var block = new Block { Start = CodeBase, Thumb = thumb, Insts = [d], End = CodeBase + d.Size };
            try { CpuCore.Interpret(s, block); }
            catch (Exception e) { Record(failures, d.Op, $"{ins:X8}: exception {e.GetType().Name} {e.Message}"); continue; }

            // ---- compare
            compared++;
            var sb = new StringBuilder();
            for (int r = 0; r < 15; r++)
            {
                uint u = (uint)uc.RegRead(RegIds[r]);
                if (u != s->R[r]) sb.Append($" R{r}: uc={u:X8} us={s->R[r]:X8}");
            }
            uint ucPc = (uint)uc.RegRead(UcArm.UC_ARM_REG_PC);
            uint ucCpsr = (uint)uc.RegRead(UcArm.UC_ARM_REG_CPSR);
            // interworking to an address with bits[1:0]=10 is UNPREDICTABLE in ARM state (QEMU keeps bit 1, we align)
            bool oddArmTarget = (ucPc & 3) == 2 && (ucCpsr & 0x20) == 0;
            if ((ucPc & ~1u) != s->R[15] && !oddArmTarget) sb.Append($" PC: uc={ucPc:X8} us={s->R[15]:X8}");
            if ((ucCpsr & 0xF80F0020) != (s->Cpsr & 0xF80F0020)) sb.Append($" CPSR: uc={ucCpsr & 0xF80F0020:X8} us={s->Cpsr & 0xF80F0020:X8}");
            for (int r = 0; r < 32; r++)
            {
                ulong u = (ulong)uc.RegRead(UcArm.UC_ARM_REG_D0 + r);
                if (u != s->D[r]) sb.Append($" D{r}: uc={u:X16} us={s->D[r]:X16}");
            }
            uint ucFpscr = (uint)uc.RegRead(UcArm.UC_ARM_REG_FPSCR);
            if ((ucFpscr & 0xF8000000) != (s->Fpscr & 0xF8000000)) sb.Append($" FPSCR: uc={ucFpscr:X8} us={s->Fpscr:X8}");
            var ucData = new byte[DataSize];
            uc.MemRead(DataBase, ucData);
            var ourData = GuestMemory.Span(DataBase, (int)DataSize);
            for (int i = 0; i < DataSize; i++)
                if (ucData[i] != ourData[i]) { sb.Append($" MEM@{DataBase + i:X8}: uc={ucData[i]:X2} us={ourData[i]:X2} (was {data[i]:X2})"); if (fixedIns == null) break; }

            if (sb.Length > 0 && fixedIns != null)
                output.WriteLine($"{ins:X8} regs=[{string.Join(" ", regs.Take(15).Select(r => r.ToString("X8")))}] D=[{string.Join(" ", dregs.Take(8).Select(r => r.ToString("X16")))}] cpsr={flags:X8}:{sb}");

            if (sb.Length > 0) Record(failures, d.Op, $"{ins:X8}:{sb}");
        }

        output.WriteLine($"compared={compared} skipped={skipped} failingOps={failures.Count}");
        foreach (var (op, (count, example)) in failures.OrderByDescending(f => f.Value.count))
            output.WriteLine($"{op,-28} x{count,-6} e.g. {example}");
        uc.Close();
        Assert.True(failures.Count == 0, $"{failures.Count} handlers disagree with Unicorn (see output)");
    }

    /// <summary>
    /// Encodings the architecture leaves UNPREDICTABLE (PC used where it may not be, exception returns
    /// from user mode, writeback into the transfer register). Real games never execute them.
    /// </summary>
    static bool IsUnpredictable(DecodedInst d, bool thumb)
    {
        uint i = d.Raw;
        uint F(int hi, int lo) => (i >> lo) & (uint)((1L << (hi - lo + 1)) - 1);
        if (d.Op == Op.Neon_LoadStore) return NeonRegsOutOfRange(i);
        if (!thumb)
        {
            switch (d.Op)
            {
                case Op.A32_DPImm or Op.A32_DPRegImm:
                    return F(15, 12) == 15; // Rd=PC: S is an exception return; otherwise results with bits[1:0]=10 are UNPREDICTABLE
                case Op.A32_DPRegReg:
                    return F(19, 16) == 15 || F(15, 12) == 15 || F(11, 8) == 15 || F(3, 0) == 15;
                case Op.A32_LdStImm or Op.A32_LdStReg or Op.A32_LdStExtra:
                {
                    bool wback = F(24, 24) == 0 || F(21, 21) == 1;
                    return wback && (F(19, 16) == 15 || F(19, 16) == F(15, 12)) || (d.Op == Op.A32_LdStReg && F(3, 0) == 15);
                }
                case Op.A32_Multiply or Op.A32_HalfMultiply or Op.A32_SignedMultiplyMedia or Op.A32_Usad8:
                    return F(19, 16) == 15 || F(11, 8) == 15 || F(3, 0) == 15 || (d.Op == Op.A32_Multiply && F(23, 23) == 1 && F(15, 12) == 15)
                           || (d.Op == Op.A32_HalfMultiply && F(15, 12) == 15 && F(22, 21) != 3); // Ra=PC (SMULxy has no Ra)
                case Op.A32_Pkh or Op.A32_Sat or Op.A32_Sat16 or Op.A32_Extend or Op.A32_Reverse or Op.A32_Sel or Op.A32_Bfx or Op.A32_Bfi
                    or Op.A32_Parallel or Op.A32_Clz or Op.A32_SatAddSub or Op.A32_MovW or Op.A32_MovT:
                    return F(15, 12) == 15 || F(3, 0) == 15 || (F(19, 16) == 15 && d.Op is Op.A32_Pkh or Op.A32_Parallel or Op.A32_Sel or Op.A32_SatAddSub);
                case Op.A32_LdmStm:
                    return F(19, 16) == 15 || (F(21, 21) == 1 && (i & (1u << (int)F(19, 16))) != 0) || (i & 0xFFFF) == 0;
                case Op.A32_Exclusive or Op.A32_Swp:
                    return true; // monitor / bus-lock semantics are covered by dedicated tests
                case Op.Vfp_Mov64 or Op.Vfp_MovCoreSingle or Op.Vfp_MovScalarCore or Op.Vfp_MovCoreScalar or Op.Vfp_VdupCore:
                    return F(15, 12) == 15 || F(19, 16) == 15;
                case Op.Vfp_LdmStm:
                    return F(19, 16) == 15 || (i & 0xFF) == 0;
            }
            return false;
        }
        switch (d.Op)
        {
            case Op.T32_DPModImm or Op.T32_DPShiftReg or Op.T32_DPPlainImm or Op.T32_Extend or Op.T32_Parallel or Op.T32_Misc
                or Op.T32_Mul or Op.T32_LongMul or Op.T32_ShiftReg:
                return F(11, 8) == 15 || F(11, 8) == 13 || F(3, 0) == 15 || F(3, 0) == 13 || F(19, 16) == 13
                       || (d.Op == Op.T32_Mul && F(15, 12) == 15 && F(22, 20) is not (0 or 1 or 5)); // Ra=PC only means "no accumulate" for MUL/SMUL*/SMMUL
            case Op.T32_LdStDual:
                return F(21, 21) == 1 && (F(19, 16) == F(15, 12) || F(19, 16) == F(11, 8)) || F(15, 12) == F(11, 8) || F(15, 12) >= 13 || F(11, 8) >= 13;
            case Op.T32_LdSt:
                return F(15, 12) == 15 || (F(19, 16) == 15 && F(20, 20) == 0) ||(F(11, 11) == 1 && F(23, 23) == 0 && F(8, 8) == 1 && F(19, 16) == F(15, 12));
            case Op.T32_LdmStm:
                return F(19, 16) == 15 || (i & 0xFFFF) == 0 || (F(21, 21) == 1 && (i & (1u << (int)F(19, 16))) != 0) || (i & 0x2000) != 0;
            case Op.T32_ExclusiveWord or Op.T32_ExclusiveOther or Op.T32_Tbb:
                return true;
            case Op.T16_AddHi or Op.T16_MovHi:
                return false;
            case Op.T16_BxBlx:
                return (F(6, 3) == 15 && F(7, 7) == 1); // BLX pc is UNPREDICTABLE
        }
        return false;
    }

    /// <summary>NEON element load/store whose register list runs past D31: UNPREDICTABLE (QEMU reads zeros there).</summary>
    static bool NeonRegsOutOfRange(uint i)
    {
        uint F(int hi, int lo) => (i >> lo) & (uint)((1L << (hi - lo + 1)) - 1);
        {
            {
                uint vd = (F(22, 22) << 4) | F(15, 12);
                int regs, inc;
                if (F(23, 23) == 0)
                {
                    uint type = F(11, 8);
                    (regs, inc) = type switch
                    {
                        0b0111 => (1, 1), 0b1010 => (2, 1), 0b0110 => (3, 1), 0b0010 => (4, 1),
                        0b1000 => (2, 1), 0b1001 => (2, 2), 0b0011 => (4, 1),
                        0b0100 => (3, 1), 0b0101 => (3, 2),
                        0b0000 => (4, 1), 0b0001 => (4, 2),
                        _ => (1, 1),
                    };
                }
                else
                {
                    regs = (int)F(9, 8) + 1;
                    uint size = F(11, 10), ia = F(7, 4);
                    if (size == 3) inc = F(5, 5) == 1 ? 2 : 1;
                    else inc = size switch { 0 => 1, 1 => (ia & 2) != 0 ? 2 : 1, _ => (ia & 4) != 0 ? 2 : 1 };
                    if (regs == 1 && size == 3) regs = 1 + (int)F(5, 5);
                    if (regs == 1) inc = 1;
                }
                return vd + (uint)((regs - 1) * inc) > 31;
            }
        }
    }

    static void Record(Dictionary<Op, (int, string)> f, Op op, string example)
    {
        if (f.TryGetValue(op, out var v)) f[op] = (v.Item1 + 1, v.Item1 < 12 ? v.Item2 + " | " + example.Split(':')[0] : v.Item2);
        else f[op] = (1, example);
    }

    static ulong RandomSimd(Random rng) => rng.Next(4) switch
    {
        0 => (ulong)rng.NextInt64(),
        1 => BitConverter.DoubleToUInt64Bits((rng.NextDouble() - 0.5) * 1000),
        2 => (ulong)BitConverter.SingleToUInt32Bits((float)((rng.NextDouble() - 0.5) * 1000))
             | ((ulong)BitConverter.SingleToUInt32Bits((float)((rng.NextDouble() - 0.5) * 1000)) << 32),
        _ => (ulong)rng.Next(256) * 0x0001000100010001ul,
    };
}
