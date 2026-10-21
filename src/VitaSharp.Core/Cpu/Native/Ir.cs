namespace VitaSharp.Core.Cpu.Native;

/// <summary>
/// Backend-neutral intermediate representation of one guest block, produced by <see cref="ArmFrontend"/> and lowered by
/// a host emitter (<see cref="X64Emitter"/> today, an ARM64 emitter later). Values are 32-bit virtual registers
/// ("temps", ids &gt;= 0) defined exactly once; a temp may be a compile-time constant, which emitters encode as an
/// immediate. Control flow is forward only: <see cref="IrOp.CondBranch"/>/<see cref="IrOp.BranchIf"/> skip to a
/// later <see cref="IrOp.Label"/>, and every path ends in an exit. Guest state (registers, flags, PC, Thumb) is read
/// and written with explicit ops; the frontend caches it in temps between flush points, so emitters see plain data flow.
/// </summary>
public enum IrOp : byte
{
    Nop,
    // ---- guest state
    GetReg,       // D = R[Imm]
    SetReg,       // R[Imm] = A
    GetFlag,      // D = flag Imm (IrFlag) as 0/1
    SetFlag,      // flag Imm = A (0/1)
    LoadState,    // D = *(uint*)(state + Imm)
    StoreState,   // *(uint*)(state + Imm) = A
    // ---- 32-bit ALU, no flags
    Add, Sub, And, Or, Xor, AndNot /*A & ~B*/, OrNot /*A | ~B*/, Mul,
    Not, Neg,
    Shl, Shr, Sar, Ror,   // amount B: constant 0..31, or a temp (only its low 5 bits count)
    Clz, Bswap, Rev16, RevSh, Rbit,
    SExt8, SExt16, ZExt8, ZExt16,
    Bit,          // D = (A >> Imm) & 1
    CmpEq,        // D = A == B ? 1 : 0
    Select,       // D = C != 0 ? A : B
    UDiv, SDiv,   // ARM semantics: division by zero = 0, INT_MIN / -1 = INT_MIN
    // ---- flag-producing ALU: outputs D plus the optional flag temps Fn Fz Fc Fv
    AddC,         // D = A + B + C  (C = carry-in temp 0/1, or -1 for 0). Flags as ARM AddWithCarry.
    SubC,         // D = A + ~B + C (C = carry-in temp 0/1, or -1 for 1): A - B - !C. Flags as ARM AddWithCarry.
    TestNZ,       // Fn = A >> 31, Fz = A == 0
    ShiftReg,     // ARM register-controlled shift: Imm = type (0 LSL,1 LSR,2 ASR,3 ROR), A = value, B = Rs (low byte used),
                  // C = carry-in; D = result, Fc = carry-out (optional)
    // ---- 64-bit pairs
    UMulL, SMulL, // D = low, D2 = high of A * B
    Add64,        // (D2:D) = (B:A) + (E:C)
    // ---- memory (guest address A; Imm unused)
    Load8, Load8S, Load16, Load16S, Load32,
    Store8, Store16, Store32,   // value B
    Fence,
    VecOp,        // Advanced SIMD on the register file: Imm = VecKind | q << 8 | size << 9, Imm4 = byte offsets d | n << 8 | m << 16
    LoadD,        // 8 bytes at guest address A -> state offset Imm (one unaligned 64-bit access, like Mem.R64)
    StoreD,       // state offset Imm (8 bytes) -> guest address A (like Mem.W64)
    LdrEx,        // D = word at guest address A, arming the exclusive monitor (interpreter generation-counter protocol)
    StrEx,        // exclusive store of B to address A; D = 0 on success, 1 on failure
    // ---- control
    Label,        // Imm = label id
    CondBranch,   // if ARM condition Imm does NOT hold on (A=N, B=Z, C=C, E=V) goto label Imm2
    BranchIf,     // if (A != 0) == (Imm != 0) goto label Imm2
    Callout,      // run the interpreter handler: Imm = Op, Imm2 = instruction word, Imm3 = address
    ExitStatic,   // leave the block for guest PC Imm in Thumb state Imm2 (R15/Thumb already stored); linkable
    ExitDynamic,  // leave the block; PC/Thumb are in the state (indirect branch); Imm = 1: never link (SVC, callout exit)
    // ---- floating point on the guest SIMD register file (state memory)
    FpOp,         // Imm = FpKind | FpFlags, Imm4 = byte offsets in D of d | n << 8 | m << 16; Imm2/Imm3 = instruction
                  // word and address: an emitter may run the interpreter handler instead (any FPSCR mode, edge cases)
}

public enum IrFlag : byte { N, Z, C, V }

public struct IrInst
{
    public IrOp Op;
    public int D = -1, D2 = -1, Fn = -1, Fz = -1, Fc = -1, Fv = -1;
    public int A = -1, B = -1, C = -1, E = -1;
    public uint Imm, Imm2, Imm3, Imm4;

    public IrInst(IrOp op) { Op = op; }

    public readonly bool HasSideEffects => Op is IrOp.SetReg or IrOp.SetFlag or IrOp.StoreState or IrOp.Store8 or IrOp.Store16
        or IrOp.Store32 or IrOp.Fence or IrOp.VecOp or IrOp.LoadD or IrOp.StoreD or IrOp.LdrEx or IrOp.StrEx or IrOp.Label or IrOp.CondBranch or IrOp.BranchIf or IrOp.Callout or IrOp.ExitStatic
        or IrOp.ExitDynamic or IrOp.FpOp
        // loads may fault on unmapped memory exactly like the interpreter: keep them
        or IrOp.Load8 or IrOp.Load8S or IrOp.Load16 or IrOp.Load16S or IrOp.Load32;

    public readonly override string ToString()
    {
        var sb = new System.Text.StringBuilder();
        void O(string n, int t) { if (t >= 0) sb.Append($" {n}=t{t}"); }
        sb.Append(Op);
        O("d", D); O("d2", D2); O("n", Fn); O("z", Fz); O("c", Fc); O("v", Fv);
        O("a", A); O("b", B); O("c", C); O("e", E);
        sb.Append($" #{Imm:X} #{Imm2:X} #{Imm3:X}");
        return sb.ToString();
    }
}

/// <summary>A translated block: the instruction list plus the constant table of its temps.</summary>
public sealed class IrBlock
{
    public readonly List<IrInst> Insts = new(128);
    public readonly List<bool> IsConst = new(128);
    public readonly List<uint> ConstValue = new(128);
    public int Labels;
    /// <summary>Guest block summary needed by exits (cycle accounting).</summary>
    public long Cycles;
    public int GuestInstructions;
    public uint StartPc;
    public bool Thumb;
    public int Callouts;
    public int TempCount => IsConst.Count;

    public int NewTemp() { IsConst.Add(false); ConstValue.Add(0); return IsConst.Count - 1; }
    public int NewConst(uint v) { IsConst.Add(true); ConstValue.Add(v); return IsConst.Count - 1; }

    public bool TryConst(int t, out uint v)
    {
        if (t >= 0 && IsConst[t]) { v = ConstValue[t]; return true; }
        v = 0; return false;
    }

    public override string ToString() => string.Join("\n", Insts.Select((i, n) => $"{n,4}: {i}"));
}

/// <summary>Field offsets of <see cref="CpuState"/> for generated code.</summary>
public static class StateLayout
{
    public static int Of(string field) => (int)System.Runtime.InteropServices.Marshal.OffsetOf<CpuState>(field);
}

/// <summary>Operation of an <see cref="IrOp.FpOp"/> (VFPv3 scalar arithmetic, ARM semantics).</summary>
public enum FpKind : byte
{
    Add, Sub, Mul, NMul, Div,
    Mla, Mls, Nmla, Nmls,      // d = d + n*m, d - n*m, -d - n*m, -d + n*m (product rounded first)
    Sqrt,
    Cmp, CmpZero,              // FPSCR.NZCV = compare(d, m) / compare(d, 0)
    CvtToDouble, CvtToSingle,  // single m -> double d / double m -> single d
    CvtFromS32, CvtFromU32,    // integer in single m -> float d
    CvtToS32, CvtToU32,        // float m -> integer in single d, round towards zero
    CvtToS32R, CvtToU32R,      // ... with the FPSCR rounding mode
}

[Flags]
public enum FpFlags : uint
{
    Double = 1 << 8,           // operands are doubles (for conversions: the float side)
    OtherOp = 1 << 9,          // the interpreter handler is Vfp_Other (else Vfp_Arith)
}

/// <summary>Operation of an <see cref="IrOp.VecOp"/> (NEON three-same; floats use the Advanced SIMD standard mode:
/// inputs and denormal results flushed to zero, NaN results are the default NaN, round to nearest).</summary>
public enum VecKind : byte
{
    And, Bic, Orr, Orn, Eor, Bsl, Bit, Bif,  // bitwise (d = f(n, m, d))
    AddI, SubI,                              // integer lanes of the given size
    AddF, SubF, MulF, MlaF, MlsF,            // f32 lanes
}
