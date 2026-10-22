using System.Reflection;

namespace VitaSharp.Core.Cpu;

/// <summary>
/// Handler identifiers. The name is "Class_Method": it resolves to the static handler method
/// <c>Class.Method(CpuState*, uint ins, uint pc)</c> in this namespace.
/// </summary>
public enum Op : ushort
{
    Invalid,
    // ARM
    A32_DPImm, A32_DPRegImm, A32_DPRegReg, A32_MovW, A32_MovT, A32_Multiply, A32_HalfMultiply,
    A32_SignedMultiplyMedia, A32_SatAddSub, A32_Parallel, A32_Pkh, A32_Sat, A32_Sat16, A32_Extend,
    A32_Reverse, A32_Sel, A32_Usad8, A32_Bfx, A32_Bfi, A32_Clz, A32_Bx, A32_BlxReg, A32_B, A32_Bl,
    A32_BlxImm, A32_Mrs, A32_MsrReg, A32_MsrImm, A32_Nop, A32_Svc, A32_Bkpt, A32_Udf, A32_Yield,
    A32_Clrex, A32_Barrier, A32_Mrc, A32_Mcr, A32_LdStImm, A32_LdStReg, A32_LdStExtra, A32_LdmStm,
    A32_Exclusive, A32_Swp,
    // Thumb 16-bit
    T16_ShiftImm, T16_AddSub3, T16_Imm8, T16_DataProc, T16_AddHi, T16_CmpHi, T16_MovHi, T16_BxBlx,
    T16_LdrLit, T16_LdStReg, T16_LdStImm, T16_LdStH, T16_LdStSp, T16_Adr, T16_AddSpImm, T16_AdjSp,
    T16_Cbz, T16_Extend, T16_Push, T16_Pop, T16_Rev, T16_Stm, T16_Ldm, T16_BCond, T16_B, T16_Svc,
    T16_Bkpt, T16_Udf, T16_Yield, T16_Nop,
    Thumb_It,
    // Thumb 32-bit
    T32_DPModImm, T32_DPShiftReg, T32_DPPlainImm, T32_ShiftReg, T32_Extend, T32_Parallel, T32_Misc,
    T32_Mul, T32_LongMul, T32_BCondW, T32_BW, T32_Bl, T32_BlxImm, T32_Msr, T32_Mrs, T32_BxjOrSubsPcLr,
    T32_Tbb, T32_LdmStm, T32_LdStDual, T32_ExclusiveWord, T32_ExclusiveOther, T32_LdSt, T32_Clrex,
    T32_Barrier, T32_Nop, T32_Yield, T32_Udf, T32_Mrc, T32_Mcr,
    // VFP
    Vfp_Arith, Vfp_MovImm, Vfp_Other, Vfp_MovCoreSingle, Vfp_Vmrs, Vfp_Vmsr, Vfp_MovCoreScalar,
    Vfp_MovScalarCore, Vfp_VdupCore, Vfp_Mov64, Vfp_LdrStrArm, Vfp_LdrStrThumb, Vfp_LdmStm,
    // NEON
    Neon_ThreeSame, Neon_ThreeDiff, Neon_Scalar, Neon_Shift, Neon_Misc, Neon_Imm, Neon_Vext, Neon_Vtbl,
    Neon_VdupScalar, Neon_LoadStore,
    Count,
}

internal static unsafe class Thumb
{
    /// <summary>IT itself has no effect at execution time: the block builder applies its conditions.</summary>
    public static void It(CpuState* s, uint ins, uint pc) { }
}

/// <summary>
/// Op → handler function pointer table (and MethodInfo for the IL backend). Generated at compile time by
/// VitaSharp.Core.Generators.OpHandlerTableGenerator from the Op names, so it needs no reflection (Native AOT).
/// </summary>
public static unsafe partial class HandlerTable
{
    public static readonly delegate*<CpuState*, uint, uint, void>[] Pointers = BuildPointers();

    static delegate*<CpuState*, uint, uint, void>[] BuildPointers()
    {
        var p = new delegate*<CpuState*, uint, uint, void>[(int)Op.Count];
        FillGenerated(p);
        return p;
    }

    static partial void FillGenerated(delegate*<CpuState*, uint, uint, void>[] p);
    static partial void FillMethods(MethodInfo[] m);

    static MethodInfo[]? _methods;

    /// <summary>Handler methods (IL backend only; looked up by constant name, so trimming keeps them).</summary>
    public static MethodInfo[] Methods
    {
        get
        {
            if (_methods != null) return _methods;
            var m = new MethodInfo[(int)Op.Count];
            FillMethods(m);
            return _methods = m;
        }
    }

    public static void EnsureInitialized() { }
}
