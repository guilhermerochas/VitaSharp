using VitaSharp.Core.Util;
using System.Numerics;
using static VitaSharp.Core.Cpu.Arm;

namespace VitaSharp.Core.Cpu.Native;

/// <summary>
/// Translates a decoded guest block (ARM or Thumb) into IR. Every instruction the IR covers is translated inline with
/// exactly the interpreter's semantics (the handlers in A32/T16/T32 are the reference, including their behaviour for
/// UNPREDICTABLE encodings where it matters); anything else - VFP/NEON, exclusives, saturating and parallel media ops,
/// status register moves, odd encodings - becomes a callout that runs the interpreter handler for that one instruction.
/// The block therefore always compiles; <see cref="Stats"/> reports how much of it ran inline.
/// </summary>
public sealed unsafe partial class ArmFrontend
{
    IrBuilder _b = null!;
    bool _thumb;
    readonly Func<VecKind, uint, bool> _supportsVec;
    readonly bool _tinyCheck;

    /// <summary>Frontend for the x64 emitter (its SIMD coverage and FZ tininess slow paths).</summary>
    public ArmFrontend() : this(X64Emitter.SupportsVec, tinyCheck: true) { }

    /// <param name="supportsVec">Which <see cref="VecKind"/>s (at a lane size) the target emitter lowers inline.</param>
    /// <param name="tinyCheck">The target flushes tiny float results after rounding (x86): mark the affected kinds
    /// <see cref="VecFlags.SlowPath"/>. ARM64 hosts detect tininess before rounding like the guest and need no check.</param>
    /// <param name="reuseIr">Translate into one IR block owned by this frontend (overwritten by the next Translate): the
    /// compilers' per-thread frontends, which consume the IR before translating the next block, allocate nothing.</param>
    public ArmFrontend(Func<VecKind, uint, bool> supportsVec, bool tinyCheck, bool reuseIr = false)
    {
        _supportsVec = supportsVec;
        _tinyCheck = tinyCheck;
        _reuseIr = reuseIr;
    }

    readonly bool _reuseIr;
    IrBuilder? _reused;

    /// <summary>Conditional instructions that fall through keep the cached dirty state across their region instead of
    /// flushing it before the branch (<see cref="IrBuilder.BeginCond"/>; the ARM64 backend's
    /// <see cref="Arm64Emitter.A64Opt.DeferRegions"/>, off for x64).</summary>
    public bool DeferRegions;

    static readonly uint ExitOffset = (uint)StateLayout.Of(nameof(CpuState.Exit));
    static readonly uint SvcImmOffset = (uint)StateLayout.Of(nameof(CpuState.SvcImm));
    static readonly uint SvcPcOffset = (uint)StateLayout.Of(nameof(CpuState.SvcPc));
    static readonly uint TpidrUroOffset = (uint)StateLayout.Of(nameof(CpuState.TpidrUro));
    static readonly uint TpidrUrwOffset = (uint)StateLayout.Of(nameof(CpuState.TpidrUrw));
    static readonly uint ExclusiveValidOffset = (uint)StateLayout.Of(nameof(CpuState.ExclusiveValid));
    static readonly uint DOffset = (uint)StateLayout.Of(nameof(CpuState.D));
    static readonly uint FpscrOffset = (uint)StateLayout.Of(nameof(CpuState.Fpscr));

    /// <summary>
    /// Debug bisection: VITASHARP_NATIVE_NOINLINE=all or a comma-separated list of ops (e.g. A32_LdmStm,T16_Pop)
    /// turns those instructions into interpreter callouts.
    /// </summary>
    static readonly bool[]? NoInline = Environment.GetEnvironmentVariable(EnvVars.NativeNoinline) is { Length: > 0 } list
        ? list == "all" ? [] : Enumerable.Range(0, (int)Op.Count).Select(i => list.Split(',').Contains(((Op)i).ToString())).ToArray()
        : null;

    /// <summary>Per-op inline/callout counters (diagnostics: which instructions are worth adding to the IR next).</summary>
    public static readonly long[] InlineCount = new long[(int)Op.Count], CalloutCount = new long[(int)Op.Count];

    public IrBlock Translate(Block block)
    {
        if (!_reuseIr) _b = new IrBuilder(block.Start, block.Thumb);
        else if (_reused == null) _b = _reused = new IrBuilder(block.Start, block.Thumb);
        else { _reused.Reset(block.Start, block.Thumb); _b = _reused; }
        _thumb = block.Thumb;
        var ir = _b.Block;
        ir.Cycles = block.Cycles;
        ir.GuestInstructions = block.Insts.Length;
        var insts = block.Insts;
        bool terminated = false;
        for (int i = 0; i < insts.Length; i++)
        {
            ref readonly var d = ref insts[i];
            bool last = i == insts.Length - 1;
            if (d.EndsBlock || last) _b.SetReg(15, _b.Const(d.Address + d.Size));
            if (d.Op is Op.Thumb_It or Op.A32_Nop or Op.T16_Nop or Op.T32_Nop) { if (last) { _b.Exit(); terminated = true; } continue; }

            bool cond = (d.Flags & InstFlags.Conditional) != 0;
            if (cond) _b.BeginCond(d.Cond, defer: DeferRegions && !d.EndsBlock && !last);
            bool exitNow = Translate(d);
            if (d.EndsBlock || last || exitNow)
            {
                // the block ends after this instruction (on the path that executed it)
                _b.Exit(neverLink: exitNow);
                if (cond)
                {
                    _b.EndRegion(terminated: true);
                    _b.Exit(); // condition failed: fall through to the next address (R15 was set before the region)
                }
                terminated = true;
                break;
            }
            if (cond) _b.EndRegion(terminated: false);
        }
        if (!terminated) _b.Exit();
        return ir;
    }

    // ------------------------------------------------------------------ helpers

    IrBuilder B => _b;
    int C(uint v) => _b.Const(v);
    int Reg(uint r) => _b.Reg((int)r);
    void SetReg(uint r, int v) => _b.SetReg((int)r, v);

    /// <summary>Register operand as the handlers read it: PC reads as the address plus 8 (ARM) or 4 (Thumb).</summary>
    int Rg(uint n, in DecodedInst d) => n == 15 ? C(d.Address + (_thumb ? 4u : 8u)) : Reg(n);

    void SetNZ(int r) { var (n, z) = B.TestNZ(r); B.SetFlag(IrFlag.N, n); B.SetFlag(IrFlag.Z, z); }

    /// <summary>BXWritePC: interworking branch to a computed address.</summary>
    void BxWritePc(int target)
    {
        if (B.IsConst(target, out uint t))
        {
            if ((t & 1) != 0) { B.SetThumb(C(1)); SetReg(15, C(t & ~1u)); }
            else { B.SetThumb(C(0)); SetReg(15, C(t & ~3u)); }
            return;
        }
        int thumb = B.Bit(target, 0);
        B.SetThumb(thumb);
        SetReg(15, B.And(target, B.Select(thumb, C(0xFFFFFFFE), C(0xFFFFFFFC))));
    }

    bool Callout(in DecodedInst d)
    {
        bool touchesCore = d.Op is not (Op.Vfp_Arith or Op.Vfp_MovImm or Op.Vfp_Other or Op.Neon_ThreeSame or Op.Neon_ThreeDiff
            or Op.Neon_Scalar or Op.Neon_Shift or Op.Neon_Misc or Op.Neon_Imm or Op.Neon_Vext or Op.Neon_Vtbl or Op.Neon_VdupScalar);
        // the handler reads R15 for EndsBlock/last instructions only through the state, which Flush stores
        if (!touchesCore && (d.EndsBlock)) touchesCore = true;
        B.Callout(d.Op, d.Raw, d.Address, touchesCore);
        Interlocked.Increment(ref CalloutCount[(int)d.Op]);
        // a callout that may branch or raise an exit leaves the block (callouts return nonzero when Exit is set)
        return d.EndsBlock;
    }

    /// <summary>Shift by an immediate (DecodeImmShift + Shift_C). Carry -1 = unchanged.</summary>
    (int V, int C) ShiftImm(int v, uint type, uint imm5)
    {
        DecodeImmShift(type, imm5, out int t, out int n);
        if (n == 0 && t != RRX) return (v, -1);
        switch (t)
        {
            case LSL: return (B.Shl(v, n), B.Bit(v, 32 - n));
            case LSR: return n == 32 ? (C(0), B.Bit(v, 31)) : (B.Shr(v, n), B.Bit(v, n - 1));
            case ASR: return n == 32 ? (B.Sar(v, 31), B.Bit(v, 31)) : (B.Sar(v, n), B.Bit(v, n - 1));
            case ROR: return (B.Ror(v, n), B.Bit(v, n - 1));
            default: return (B.Or(B.Shl(B.Flag(IrFlag.C), 31), B.Shr(v, 1)), B.Bit(v, 0)); // RRX
        }
    }

    // ------------------------------------------------------------------ dispatch

    /// <summary>Translates one instruction. Returns true when the block must end right after it without linking.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    bool Translate(DecodedInst d)
    {
        _ins = d.Raw; _op = d.Op;
        if (NoInline != null && (NoInline.Length == 0 || NoInline[(int)d.Op])) return Callout(in d);
        bool? r = d.Op switch
        {
            Op.A32_DPImm or Op.A32_DPRegImm or Op.A32_DPRegReg => A32DataProc(in d),
            Op.A32_MovW => A32MovW(in d),
            Op.A32_MovT => A32MovT(in d),
            Op.A32_Multiply => A32Multiply(in d),
            Op.A32_HalfMultiply => A32HalfMultiply(in d),
            Op.A32_SignedMultiplyMedia => A32SignedMultiplyMedia(in d),
            Op.A32_SatAddSub => A32SatAddSub(in d),
            Op.A32_Parallel => A32Parallel(in d),
            Op.A32_Pkh => A32Pkh(in d),
            Op.A32_Sat => A32Sat(in d),
            Op.A32_Sat16 => A32Sat16(in d),
            Op.A32_Sel => A32Sel(in d),
            Op.A32_Usad8 => A32Usad8(in d),
            Op.A32_Extend => A32Extend(in d),
            Op.A32_Reverse => A32Reverse(in d),
            Op.A32_Bfx => Bfx(in d, Bits(d.Raw, 15, 12), Reg(d.Raw & 0xF), Bit(d.Raw, 22) != 0, (int)Bits(d.Raw, 11, 7), (int)Bits(d.Raw, 20, 16)),
            Op.A32_Bfi => Bfi(in d, Bits(d.Raw, 15, 12), d.Raw & 0xF, (int)Bits(d.Raw, 11, 7), (int)Bits(d.Raw, 20, 16)),
            Op.A32_Clz => A32Clz(in d),
            Op.A32_Bx => Bx(Reg(d.Raw & 0xF)),
            Op.A32_BlxReg => A32BlxReg(in d),
            Op.A32_B => DirectBranch(d.Address + 8 + (SignExtend(d.Raw & 0xFFFFFF, 24) << 2)),
            Op.A32_Bl => A32Bl(in d),
            Op.A32_BlxImm => A32BlxImm(in d),
            Op.A32_Svc => Svc(in d, d.Raw & 0xFFFFFF, 4),
            Op.A32_Clrex or Op.T32_Clrex => Clrex(),
            Op.A32_Barrier or Op.T32_Barrier => Fence(),
            Op.A32_Mrc or Op.T32_Mrc => Mrc(in d),
            Op.A32_Mcr or Op.T32_Mcr => Mcr(in d),
            Op.A32_LdStImm => A32LdSt(in d, C(d.Raw & 0xFFF)),
            Op.A32_LdStReg => A32LdStReg(in d),
            Op.A32_LdStExtra => A32LdStExtra(in d),
            Op.A32_LdmStm => A32LdmStm(in d),

            Op.T16_ShiftImm => T16ShiftImm(in d),
            Op.T16_AddSub3 => T16AddSub3(in d),
            Op.T16_Imm8 => T16Imm8(in d),
            Op.T16_DataProc => T16DataProc(in d),
            Op.T16_AddHi => T16AddHi(in d),
            Op.T16_CmpHi => T16CmpHi(in d),
            Op.T16_MovHi => T16MovHi(in d),
            Op.T16_BxBlx => T16BxBlx(in d),
            Op.T16_LdrLit => Done(Bits(d.Raw, 10, 8), B.Load(IrOp.Load32, C(((d.Address + 4) & ~3u) + ((d.Raw & 0xFF) << 2)))),
            Op.T16_LdStReg => T16LdStReg(in d),
            Op.T16_LdStImm => T16LdStImm(in d),
            Op.T16_LdStH => T16LdStH(in d),
            Op.T16_LdStSp => T16LdStSp(in d),
            Op.T16_Adr => Done(Bits(d.Raw, 10, 8), C(((d.Address + 4) & ~3u) + ((d.Raw & 0xFF) << 2))),
            Op.T16_AddSpImm => Done(Bits(d.Raw, 10, 8), B.AddK(Reg(13), (d.Raw & 0xFF) << 2)),
            Op.T16_AdjSp => Done(13, Bit(d.Raw, 7) != 0 ? B.SubK(Reg(13), (d.Raw & 0x7F) << 2) : B.AddK(Reg(13), (d.Raw & 0x7F) << 2)),
            Op.T16_Cbz => T16Cbz(in d),
            Op.T16_Extend => T16Extend(in d),
            Op.T16_Push => BlockTransfer(in d, 13, (d.Raw & 0xFF) | (Bit(d.Raw, 8) << 14), true, false, true, false, d.Address + 4),
            Op.T16_Pop => BlockTransfer(in d, 13, (d.Raw & 0xFF) | (Bit(d.Raw, 8) << 15), false, true, true, true, d.Address + 4),
            Op.T16_Rev => T16Rev(in d),
            Op.T16_Stm => BlockTransfer(in d, Bits(d.Raw, 10, 8), d.Raw & 0xFF, false, true, true, false, d.Address + 4),
            Op.T16_Ldm => BlockTransfer(in d, Bits(d.Raw, 10, 8), d.Raw & 0xFF, false, true, (d.Raw & 0xFF & (1u << (int)Bits(d.Raw, 10, 8))) == 0, true, d.Address + 4),
            Op.T16_BCond => DirectBranch(d.Address + 4 + (SignExtend(d.Raw & 0xFF, 8) << 1)),
            Op.T16_B => DirectBranch(d.Address + 4 + (SignExtend(d.Raw & 0x7FF, 11) << 1)),
            Op.T16_Svc => Svc(in d, d.Raw & 0xFF, 2),

            Op.T32_DPModImm => T32DPModImm(in d),
            Op.T32_DPShiftReg => T32DPShiftReg(in d),
            Op.T32_DPPlainImm => T32DPPlainImm(in d),
            Op.T32_ShiftReg => T32ShiftReg(in d),
            Op.T32_Extend => T32Extend(in d),
            Op.T32_Parallel => T32Parallel(in d),
            Op.T32_Misc => T32Misc(in d),
            Op.T32_Mul => T32Mul(in d),
            Op.T32_LongMul => T32LongMul(in d),
            Op.T32_BCondW => T32BCondW(in d),
            Op.T32_BW => DirectBranch(d.Address + 4 + BranchImm24(d.Raw)),
            Op.T32_Bl => T32Bl(in d),
            Op.T32_BlxImm => T32BlxImm(in d),
            Op.T32_Tbb => T32Tbb(in d),
            Op.T32_LdmStm => BlockTransfer(in d, Bits(d.Raw, 19, 16), d.Raw & 0xFFFF, Bits(d.Raw, 24, 23) != 1, Bits(d.Raw, 24, 23) == 1, Bit(d.Raw, 21) != 0, Bit(d.Raw, 20) != 0, d.Address + 4),
            Op.T32_LdStDual => T32LdStDual(in d),
            Op.T32_LdSt => T32LdSt(in d),

            Op.A32_Exclusive => A32Exclusive(in d),
            Op.Neon_ThreeSame => NeonThreeSame(in d),
            Op.Neon_ThreeDiff => NeonThreeDiff(in d),
            Op.Neon_VdupScalar => NeonVdupScalar(in d),
            Op.Neon_Imm => NeonImm(in d),
            Op.Neon_LoadStore => NeonLoadStore(in d),
            Op.Neon_Scalar => NeonScalar(in d),
            Op.Neon_Shift => NeonShift(in d),
            Op.Neon_Misc => NeonMisc(in d),
            Op.Neon_Vext => NeonVext(in d),
            Op.Neon_Vtbl => NeonVtbl(in d),
            Op.T32_ExclusiveWord => T32ExclusiveWord(in d),
            Op.T32_ExclusiveOther => T32ExclusiveOther(in d),
            Op.Vfp_LdrStrArm => VfpLdrStr(in d, false),
            Op.Vfp_LdrStrThumb => VfpLdrStr(in d, true),
            Op.Vfp_LdmStm => VfpLdmStm(in d),
            Op.Vfp_MovCoreSingle => VfpMovCoreSingle(in d),
            Op.Vfp_Mov64 => VfpMov64(in d),
            Op.Vfp_Vmrs => VfpVmrs(in d),
            Op.Vfp_Vmsr => VfpVmsr(in d),
            Op.Vfp_VdupCore => VfpVdupCore(in d),
            Op.Vfp_MovCoreScalar => VfpMovCoreScalar(in d),
            Op.Vfp_MovScalarCore => VfpMovScalarCore(in d),
            Op.Vfp_Arith => VfpArith(in d),
            Op.Vfp_Other => VfpOther(in d),
            Op.Vfp_MovImm => VfpMovImm(in d),
            _ => null,
        };
        if (r is { } exitNow) { Interlocked.Increment(ref InlineCount[(int)d.Op]); return exitNow; }
        return Callout(in d);
    }

    /// <summary>R[rd] = v (an inline instruction that only sets one register).</summary>
    bool? Done(uint rd, int v) { SetReg(rd, v); return false; }

    bool? DirectBranch(uint target) { SetReg(15, C(target)); return false; }

    bool? Bx(int target) { BxWritePc(target); return false; }

    /// <summary>The callout op value of an HLE call made from native code (<see cref="HleCallouts"/>).</summary>
    public const uint HleCalloutOp = 0xFFFF; // Op is 16-bit: the largest value, never a real op

    /// <summary>
    /// SVC as a callout into the guest thread's HLE dispatch (<see cref="NativeRuntime.HleCall"/>) instead of an exit to
    /// the dispatcher: the block then continues at the pc the call set, through the dispatch table. ARM64 backend only.
    /// </summary>
    public bool HleCallouts;

    bool? Svc(in DecodedInst d, uint imm, uint size)
    {
        if (HleCallouts)
        {
            B.StoreState(SvcImmOffset, C(imm));
            B.StoreState(SvcPcOffset, C(d.Address));
            SetReg(15, C(d.Address + size));
            B.Callout(unchecked((Op)HleCalloutOp), imm, d.Address, touchesCore: true);
            return false; // SVC ends the block: an indirect exit to the pc the call left in the state
        }
        B.StoreState(ExitOffset, C((uint)ExitReason.Svc));
        B.StoreState(SvcImmOffset, C(imm));
        B.StoreState(SvcPcOffset, C(d.Address));
        SetReg(15, C(d.Address + size));
        return true;
    }

    bool? Clrex() { B.StoreState(ExclusiveValidOffset, C(0)); return false; }
    bool? Fence() { B.Fence(); return false; }

    bool? Mrc(in DecodedInst d)
    {
        uint ins = d.Raw, rt = Bits(ins, 15, 12), crn = Bits(ins, 19, 16), opc2 = Bits(ins, 7, 5);
        int v = crn == 13 && opc2 == 3 ? B.LoadState(TpidrUroOffset) : crn == 13 && opc2 == 2 ? B.LoadState(TpidrUrwOffset) : C(0);
        if (rt == 15)
        {
            B.SetFlag(IrFlag.N, B.Bit(v, 31)); B.SetFlag(IrFlag.Z, B.Bit(v, 30));
            B.SetFlag(IrFlag.C, B.Bit(v, 29)); B.SetFlag(IrFlag.V, B.Bit(v, 28));
        }
        else SetReg(rt, v);
        return false;
    }

    bool? Mcr(in DecodedInst d)
    {
        uint ins = d.Raw, rt = Bits(ins, 15, 12), crn = Bits(ins, 19, 16), opc2 = Bits(ins, 7, 5);
        if (crn == 13 && opc2 == 2) B.StoreState(TpidrUrwOffset, Reg(rt));
        if (crn == 7) B.Fence();
        return false;
    }

    // ------------------------------------------------------------------ A32 data processing

    /// <summary>A32.DataProc: opc 0..15; shCarry = -1 keeps C.</summary>
    void A32DP(uint opc, bool S, uint rd, int a, int b, int shCarry)
    {
        int r;
        switch (opc)
        {
            case 0x0: r = B.And(a, b); break;
            case 0x1: r = B.Xor(a, b); break;
            case 0x2: if (!S || rd == 15) { A32WriteRd(rd, B.Sub(a, b)); return; } { var f = B.SubC(a, b, -1); Arith(S, rd, f); return; }
            case 0x3: if (!S || rd == 15) { A32WriteRd(rd, B.Sub(b, a)); return; } { var f = B.SubC(b, a, -1); Arith(S, rd, f); return; }
            case 0x4: if (!S || rd == 15) { A32WriteRd(rd, B.Add(a, b)); return; } { var f = B.AddC(a, b, -1); Arith(S, rd, f); return; }
            case 0x5: { var f = B.AddC(a, b, B.Flag(IrFlag.C)); Arith(S, rd, f); return; }
            case 0x6: { var f = B.SubC(a, b, B.Flag(IrFlag.C)); Arith(S, rd, f); return; }
            case 0x7: { var f = B.SubC(b, a, B.Flag(IrFlag.C)); Arith(S, rd, f); return; }
            case 0x8: SetNZ(B.And(a, b)); B.SetFlag(IrFlag.C, shCarry); return;
            case 0x9: SetNZ(B.Xor(a, b)); B.SetFlag(IrFlag.C, shCarry); return;
            case 0xA: { var f = B.SubC(a, b, -1); SetNZCV(f); return; }
            case 0xB: { var f = B.AddC(a, b, -1); SetNZCV(f); return; }
            case 0xC: r = B.Or(a, b); break;
            case 0xD: r = b; break;
            case 0xE: r = B.AndNot(a, b); break;
            default: r = B.Not(b); break;
        }
        if (S && rd != 15) { SetNZ(r); B.SetFlag(IrFlag.C, shCarry); }
        A32WriteRd(rd, r);
    }

    void SetNZCV((int R, int N, int Z, int C, int V) f)
    {
        B.SetFlag(IrFlag.N, f.N); B.SetFlag(IrFlag.Z, f.Z); B.SetFlag(IrFlag.C, f.C); B.SetFlag(IrFlag.V, f.V);
    }

    void Arith(bool S, uint rd, (int R, int N, int Z, int C, int V) f)
    {
        if (S && rd != 15) SetNZCV(f);
        A32WriteRd(rd, f.R);
    }

    void A32WriteRd(uint rd, int v) { if (rd == 15) BxWritePc(v); else SetReg(rd, v); }

    bool? A32DataProc(in DecodedInst d)
    {
        uint ins = d.Raw, opc = Bits(ins, 24, 21), rn = Bits(ins, 19, 16), rd = Bits(ins, 15, 12);
        bool S = Bit(ins, 20) != 0;
        // Rd = PC on an instruction that does not end the block (TST/CMP forms never write it): interpreter does it
        if (rd == 15 && !d.EndsBlock && (opc < 8 || opc > 11)) return null;
        int a, b, carry;
        switch (d.Op)
        {
            case Op.A32_DPImm:
            {
                uint imm8 = ins & 0xFF;
                int rot = (int)((ins >> 8) & 0xF) * 2;
                uint imm = BitOperations.RotateRight(imm8, rot);
                b = C(imm);
                carry = rot == 0 ? -1 : C(imm >> 31);
                a = Rg(rn, in d);
                break;
            }
            case Op.A32_DPRegImm:
                (b, carry) = ShiftImm(Rg(ins & 0xF, in d), Bits(ins, 6, 5), Bits(ins, 11, 7));
                a = Rg(rn, in d);
                break;
            default: // DPRegReg reads the registers raw (PC operands are UNPREDICTABLE)
                (b, carry) = B.ShiftReg(Bits(ins, 6, 5), Reg(ins & 0xF), Reg(Bits(ins, 11, 8)), B.Flag(IrFlag.C));
                a = Reg(rn);
                break;
        }
        A32DP(opc, S, rd, a, b, carry);
        return false;
    }

    bool? A32MovW(in DecodedInst d)
    {
        uint rd = Bits(d.Raw, 15, 12);
        if (rd == 15) return null;
        SetReg(rd, C((Bits(d.Raw, 19, 16) << 12) | (d.Raw & 0xFFF)));
        return false;
    }

    bool? A32MovT(in DecodedInst d)
    {
        uint rd = Bits(d.Raw, 15, 12);
        if (rd == 15) return null;
        SetReg(rd, B.Or(B.AndK(Reg(rd), 0xFFFF), C(((Bits(d.Raw, 19, 16) << 12) | (d.Raw & 0xFFF)) << 16)));
        return false;
    }

    bool? A32Multiply(in DecodedInst d)
    {
        uint ins = d.Raw, op = Bits(ins, 23, 21), hi = Bits(ins, 19, 16), lo = Bits(ins, 15, 12), rm = Bits(ins, 11, 8), rn = ins & 0xF;
        bool S = Bit(ins, 20) != 0;
        if (hi == 15 || (op >= 2 && lo == 15) || (op == 1 && lo == 15)) return null; // PC destinations / accumulators
        int a = Rg(rn, in d), b = Rg(rm, in d);
        switch (op)
        {
            case 0: { int r = B.Mul(a, b); SetReg(hi, r); if (S) SetNZ(r); break; }
            case 1: { int r = B.Add(B.Mul(a, b), Reg(lo)); SetReg(hi, r); if (S) SetNZ(r); break; }
            case 2: // UMAAL
            {
                var (pl, ph) = B.MulLong(a, b, false);
                var (l1, h1) = B.Add64(pl, ph, Reg(lo), C(0));
                var (l2, h2) = B.Add64(l1, h1, Reg(hi), C(0));
                SetReg(lo, l2); SetReg(hi, h2);
                break;
            }
            case 3: SetReg(hi, B.Sub(Reg(lo), B.Mul(a, b))); break; // MLS
            default:
            {
                var (pl, ph) = B.MulLong(a, b, op >= 6);
                if ((op & 1) != 0) (pl, ph) = B.Add64(pl, ph, Reg(lo), Reg(hi));
                SetReg(lo, pl); SetReg(hi, ph);
                if (S) { B.SetFlag(IrFlag.N, B.Bit(ph, 31)); B.SetFlag(IrFlag.Z, B.CmpEq(B.Or(pl, ph), C(0))); }
                break;
            }
        }
        return false;
    }

    /// <summary>ExtendCore ops 0/4 (SXTB16/UXTB16, optionally accumulating per halfword) and 2/3/6/7 (byte/halfword).</summary>
    bool? Extend(uint rd, uint op, uint rn, uint rm, int rot)
    {
        if (rd == 15 || op is not (0 or 2 or 3 or 4 or 6 or 7)) return null;
        int v = B.Ror(Reg(rm), rot);
        if (op is 0 or 4)
        {
            int lo = op == 0 ? B.SExt8(v) : B.ZExt8(v), hi = op == 0 ? B.SExt8(B.Shr(v, 16)) : B.ZExt8(B.Shr(v, 16));
            if (rn != 15) { int a = Reg(rn); lo = B.Add(a, lo); hi = B.Add(B.Shr(a, 16), hi); }
            SetReg(rd, B.Or(B.ZExt16(lo), B.Shl(hi, 16)));
            return false;
        }
        int e = op switch { 2 => B.SExt8(v), 3 => B.SExt16(v), 6 => B.ZExt8(v), _ => B.ZExt16(v) };
        SetReg(rd, rn == 15 ? e : B.Add(Reg(rn), e));
        return false;
    }

    bool? A32Extend(in DecodedInst d) => Extend(Bits(d.Raw, 15, 12), Bits(d.Raw, 22, 20), Bits(d.Raw, 19, 16), d.Raw & 0xF, (int)Bits(d.Raw, 11, 10) * 8);

    bool? A32Reverse(in DecodedInst d)
    {
        uint rd = Bits(d.Raw, 15, 12);
        if (rd == 15) return null;
        int v = Reg(d.Raw & 0xF);
        uint o1 = Bit(d.Raw, 22), o2 = Bit(d.Raw, 7);
        SetReg(rd, o1 == 0 ? (o2 == 0 ? B.Bswap(v) : B.Rev16(v)) : (o2 == 0 ? B.Rbit(v) : B.RevSh(v)));
        return false;
    }

    bool? Bfx(in DecodedInst d, uint rd, int v, bool unsigned, int lsb, int widthm1)
    {
        if (rd == 15) return null;
        int width = widthm1 + 1;
        uint mask = width >= 32 ? 0xFFFFFFFF : (1u << width) - 1;
        int field = B.AndK(B.Shr(v, lsb), mask);
        if (!unsigned && width < 32) field = B.Sar(B.Shl(field, 32 - width), 32 - width);
        SetReg(rd, field);
        return false;
    }

    bool? Bfi(in DecodedInst d, uint rd, uint rn, int lsb, int msb)
    {
        if (rd == 15) return null;
        if (msb < lsb) return false;
        int width = msb - lsb + 1;
        uint mask = (uint)((width >= 32 ? 0xFFFFFFFFul : ((1ul << width) - 1)) << lsb);
        int src = rn == 15 ? C(0) : Reg(rn);
        SetReg(rd, B.Or(B.AndK(Reg(rd), ~mask), B.AndK(B.Shl(src, lsb), mask)));
        return false;
    }

    bool? A32Clz(in DecodedInst d)
    {
        uint rd = Bits(d.Raw, 15, 12);
        if (rd == 15) return null;
        SetReg(rd, B.Clz(Reg(d.Raw & 0xF)));
        return false;
    }

    bool? A32BlxReg(in DecodedInst d)
    {
        int target = Reg(d.Raw & 0xF);
        SetReg(14, C(d.Address + 4));
        BxWritePc(target);
        return false;
    }

    bool? A32Bl(in DecodedInst d)
    {
        SetReg(14, C(d.Address + 4));
        SetReg(15, C(d.Address + 8 + (SignExtend(d.Raw & 0xFFFFFF, 24) << 2)));
        return false;
    }

    bool? A32BlxImm(in DecodedInst d)
    {
        SetReg(14, C(d.Address + 4));
        B.SetThumb(C(1));
        SetReg(15, C(d.Address + 8 + (SignExtend(d.Raw & 0xFFFFFF, 24) << 2) + (Bit(d.Raw, 24) << 1)));
        return false;
    }

    // ------------------------------------------------------------------ A32 load/store

    bool? A32LdStReg(in DecodedInst d)
    {
        DecodeImmShift(Bits(d.Raw, 6, 5), Bits(d.Raw, 11, 7), out int t, out int n);
        int rm = Rg(d.Raw & 0xF, in d);
        int off = (n == 0 && t != RRX) ? rm : ShiftImm(rm, Bits(d.Raw, 6, 5), Bits(d.Raw, 11, 7)).V;
        return A32LdSt(in d, off);
    }

    bool? A32LdSt(in DecodedInst d, int offset)
    {
        uint ins = d.Raw, rn = Bits(ins, 19, 16), rt = Bits(ins, 15, 12);
        bool P = Bit(ins, 24) != 0, U = Bit(ins, 23) != 0, Bb = Bit(ins, 22) != 0, W = Bit(ins, 21) != 0, L = Bit(ins, 20) != 0;
        bool wback = !P || W;
        if (wback && rn == 15) return null;
        int baseVal = rn == 15 ? C((d.Address + 8) & ~3u) : Reg(rn);
        int offAddr = U ? B.Add(baseVal, offset) : B.Sub(baseVal, offset);
        int addr = P ? offAddr : baseVal;
        if (L)
        {
            int v = B.Load(Bb ? IrOp.Load8 : IrOp.Load32, addr);
            if (wback) SetReg(rn, offAddr);
            if (rt == 15) BxWritePc(v); else SetReg(rt, v);
        }
        else
        {
            int v = rt == 15 ? C(d.Address + 8) : Reg(rt);
            B.Store(Bb ? IrOp.Store8 : IrOp.Store32, addr, v);
            if (wback) SetReg(rn, offAddr);
        }
        return false;
    }

    bool? A32LdStExtra(in DecodedInst d)
    {
        uint ins = d.Raw, rn = Bits(ins, 19, 16), rt = Bits(ins, 15, 12), op = Bits(ins, 6, 5);
        bool P = Bit(ins, 24) != 0, U = Bit(ins, 23) != 0, I = Bit(ins, 22) != 0, W = Bit(ins, 21) != 0, L = Bit(ins, 20) != 0;
        bool wback = !P || W;
        uint kind = (L ? 4u : 0u) | op;
        if (kind is 0 or 4) return null;
        if (kind is 2 or 3 && rt >= 14) return null; // LDRD/STRD pairs touching PC: interpreter
        if (wback && rn == 15) return null;
        int offset = I ? C((Bits(ins, 11, 8) << 4) | (ins & 0xF)) : Reg(ins & 0xF);
        int baseVal = rn == 15 ? C((d.Address + 8) & ~3u) : Reg(rn);
        int offAddr = U ? B.Add(baseVal, offset) : B.Sub(baseVal, offset);
        int addr = P ? offAddr : baseVal;
        switch (kind)
        {
            case 1: B.Store(IrOp.Store16, addr, Rg(rt, in d)); break;
            case 2:
            {
                int a = B.Load(IrOp.Load32, addr), b = B.Load(IrOp.Load32, B.AddK(addr, 4));
                SetReg(rt, a); SetReg(rt + 1, b);
                break;
            }
            case 3:
                B.Store(IrOp.Store32, addr, Rg(rt, in d));
                B.Store(IrOp.Store32, B.AddK(addr, 4), Rg(rt + 1, in d));
                break;
            default:
            {
                if (wback) SetReg(rn, offAddr);
                int v = kind switch { 5 => B.Load(IrOp.Load16, addr), 6 => B.Load(IrOp.Load8S, addr), _ => B.Load(IrOp.Load16S, addr) };
                SetReg(rt, v);
                return false;
            }
        }
        if (wback) SetReg(rn, offAddr);
        return false;
    }

    bool? A32LdmStm(in DecodedInst d)
    {
        uint ins = d.Raw;
        return BlockTransfer(in d, Bits(ins, 19, 16), ins & 0xFFFF, Bit(ins, 24) != 0, Bit(ins, 23) != 0, Bit(ins, 21) != 0, Bit(ins, 20) != 0, d.Address + 8);
    }

    /// <summary>A32.BlockTransfer unrolled for a constant register list.</summary>
    bool? BlockTransfer(in DecodedInst d, uint rn, uint list, bool P, bool U, bool W, bool L, uint pcValue)
    {
        if (list == 0 || rn == 15) return null;
        if (L && (list & 0x8000) != 0 && !d.EndsBlock) return null;
        uint count = (uint)BitOperations.PopCount(list);
        int baseVal = Reg(rn);
        int start = U ? (P ? B.AddK(baseVal, 4) : baseVal) : (P ? B.SubK(baseVal, 4 * count) : B.SubK(baseVal, 4 * count - 4));
        int newBase = U ? B.AddK(baseVal, 4 * count) : B.SubK(baseVal, 4 * count);
        uint off = 0;
        if (L)
        {
            if (W) SetReg(rn, newBase);
            int pcLoad = -1;
            for (uint i = 0; i < 16; i++)
            {
                if ((list & (1u << (int)i)) == 0) continue;
                int v = B.Load(IrOp.Load32, B.AddK(start, off));
                off += 4;
                if (i == 15) pcLoad = v; else SetReg(i, v);
            }
            if (pcLoad >= 0) BxWritePc(pcLoad);
        }
        else
        {
            for (uint i = 0; i < 16; i++)
            {
                if ((list & (1u << (int)i)) == 0) continue;
                int v = i == 15 ? C(pcValue) : i == rn ? baseVal : Reg(i);
                B.Store(IrOp.Store32, B.AddK(start, off), v);
                off += 4;
            }
            if (W) SetReg(rn, newBase);
        }
        return false;
    }

    // ------------------------------------------------------------------ Thumb 16-bit

    static bool InIt(uint ins) => (ins & 0x8000_0000) != 0;

    bool? T16ShiftImm(in DecodedInst d)
    {
        uint ins = d.Raw, rm = Bits(ins, 5, 3), rd = ins & 7;
        var (r, c) = ShiftImm(Reg(rm), Bits(ins, 12, 11), Bits(ins, 10, 6));
        SetReg(rd, r);
        if (!InIt(ins)) { SetNZ(r); B.SetFlag(IrFlag.C, c); }
        return false;
    }

    bool? T16AddSub3(in DecodedInst d)
    {
        uint ins = d.Raw, m = Bits(ins, 8, 6), rn = Bits(ins, 5, 3), rd = ins & 7;
        int b = Bit(ins, 10) != 0 ? C(m) : Reg(m);
        var f = Bit(ins, 9) != 0 ? B.SubC(Reg(rn), b, -1) : B.AddC(Reg(rn), b, -1);
        SetReg(rd, f.R);
        if (!InIt(ins)) SetNZCV(f);
        return false;
    }

    bool? T16Imm8(in DecodedInst d)
    {
        uint ins = d.Raw, op = Bits(ins, 12, 11), rdn = Bits(ins, 10, 8), imm = ins & 0xFF;
        bool sf = !InIt(ins);
        switch (op)
        {
            case 0: SetReg(rdn, C(imm)); if (sf) SetNZ(C(imm)); break;
            case 1: SetNZCV(B.SubC(Reg(rdn), C(imm), -1)); break;
            case 2: { var f = B.AddC(Reg(rdn), C(imm), -1); SetReg(rdn, f.R); if (sf) SetNZCV(f); break; }
            default: { var f = B.SubC(Reg(rdn), C(imm), -1); SetReg(rdn, f.R); if (sf) SetNZCV(f); break; }
        }
        return false;
    }

    bool? T16DataProc(in DecodedInst d)
    {
        uint ins = d.Raw, op = Bits(ins, 9, 6), rm = Bits(ins, 5, 3), rdn = ins & 7;
        int a = Reg(rdn), b = Reg(rm);
        bool sf = !InIt(ins);
        switch (op)
        {
            case 0x0: { int r = B.And(a, b); SetReg(rdn, r); if (sf) SetNZ(r); break; }
            case 0x1: { int r = B.Xor(a, b); SetReg(rdn, r); if (sf) SetNZ(r); break; }
            case 0x2: case 0x3: case 0x4: case 0x7:
            {
                uint type = op switch { 2 => 0u, 3 => 1u, 4 => 2u, _ => 3u };
                var (r, c) = B.ShiftReg(type, a, b, B.Flag(IrFlag.C));
                SetReg(rdn, r);
                if (sf) { SetNZ(r); B.SetFlag(IrFlag.C, c); }
                break;
            }
            case 0x5: { var f = B.AddC(a, b, B.Flag(IrFlag.C)); SetReg(rdn, f.R); if (sf) SetNZCV(f); break; }
            case 0x6: { var f = B.SubC(a, b, B.Flag(IrFlag.C)); SetReg(rdn, f.R); if (sf) SetNZCV(f); break; }
            case 0x8: SetNZ(B.And(a, b)); break;
            case 0x9: { var f = B.SubC(C(0), b, -1); SetReg(rdn, f.R); if (sf) SetNZCV(f); break; }
            case 0xA: SetNZCV(B.SubC(a, b, -1)); break;
            case 0xB: SetNZCV(B.AddC(a, b, -1)); break;
            case 0xC: { int r = B.Or(a, b); SetReg(rdn, r); if (sf) SetNZ(r); break; }
            case 0xD: { int r = B.Mul(a, b); SetReg(rdn, r); if (sf) SetNZ(r); break; }
            case 0xE: { int r = B.AndNot(a, b); SetReg(rdn, r); if (sf) SetNZ(r); break; }
            default: { int r = B.Not(b); SetReg(rdn, r); if (sf) SetNZ(r); break; }
        }
        return false;
    }

    bool? T16AddHi(in DecodedInst d)
    {
        uint rdn = (Bit(d.Raw, 7) << 3) | (d.Raw & 7), rm = Bits(d.Raw, 6, 3);
        int r = B.Add(Rg(rdn, in d), Rg(rm, in d));
        if (rdn == 15) SetReg(15, B.AndK(r, 0xFFFFFFFE)); else SetReg(rdn, r);
        return false;
    }

    bool? T16CmpHi(in DecodedInst d)
    {
        uint rn = (Bit(d.Raw, 7) << 3) | (d.Raw & 7), rm = Bits(d.Raw, 6, 3);
        SetNZCV(B.SubC(Rg(rn, in d), Rg(rm, in d), -1));
        return false;
    }

    bool? T16MovHi(in DecodedInst d)
    {
        uint rd = (Bit(d.Raw, 7) << 3) | (d.Raw & 7), rm = Bits(d.Raw, 6, 3);
        int v = Rg(rm, in d);
        if (rd == 15) SetReg(15, B.AndK(v, 0xFFFFFFFE)); else SetReg(rd, v);
        return false;
    }

    bool? T16BxBlx(in DecodedInst d)
    {
        int target = Rg(Bits(d.Raw, 6, 3), in d);
        if (Bit(d.Raw, 7) != 0) SetReg(14, C((d.Address + 2) | 1));
        BxWritePc(target);
        return false;
    }

    bool? T16LdStReg(in DecodedInst d)
    {
        uint ins = d.Raw, op = Bits(ins, 11, 9), rm = Bits(ins, 8, 6), rn = Bits(ins, 5, 3), rt = ins & 7;
        int addr = B.Add(Reg(rn), Reg(rm));
        switch (op)
        {
            case 0: B.Store(IrOp.Store32, addr, Reg(rt)); break;
            case 1: B.Store(IrOp.Store16, addr, Reg(rt)); break;
            case 2: B.Store(IrOp.Store8, addr, Reg(rt)); break;
            case 3: SetReg(rt, B.Load(IrOp.Load8S, addr)); break;
            case 4: SetReg(rt, B.Load(IrOp.Load32, addr)); break;
            case 5: SetReg(rt, B.Load(IrOp.Load16, addr)); break;
            case 6: SetReg(rt, B.Load(IrOp.Load8, addr)); break;
            default: SetReg(rt, B.Load(IrOp.Load16S, addr)); break;
        }
        return false;
    }

    bool? T16LdStImm(in DecodedInst d)
    {
        uint ins = d.Raw, imm5 = Bits(ins, 10, 6), rn = Bits(ins, 5, 3), rt = ins & 7;
        bool b = Bit(ins, 12) != 0, l = Bit(ins, 11) != 0;
        int addr = B.AddK(Reg(rn), b ? imm5 : imm5 << 2);
        if (l) SetReg(rt, B.Load(b ? IrOp.Load8 : IrOp.Load32, addr));
        else B.Store(b ? IrOp.Store8 : IrOp.Store32, addr, Reg(rt));
        return false;
    }

    bool? T16LdStH(in DecodedInst d)
    {
        uint ins = d.Raw, imm5 = Bits(ins, 10, 6), rn = Bits(ins, 5, 3), rt = ins & 7;
        int addr = B.AddK(Reg(rn), imm5 << 1);
        if (Bit(ins, 11) != 0) SetReg(rt, B.Load(IrOp.Load16, addr)); else B.Store(IrOp.Store16, addr, Reg(rt));
        return false;
    }

    bool? T16LdStSp(in DecodedInst d)
    {
        uint ins = d.Raw, rt = Bits(ins, 10, 8);
        int addr = B.AddK(Reg(13), (ins & 0xFF) << 2);
        if (Bit(ins, 11) != 0) SetReg(rt, B.Load(IrOp.Load32, addr)); else B.Store(IrOp.Store32, addr, Reg(rt));
        return false;
    }

    bool? T16Cbz(in DecodedInst d)
    {
        if ((d.Flags & InstFlags.Conditional) != 0) return null; // CBZ inside IT is UNPREDICTABLE: interpreter
        uint ins = d.Raw;
        bool nonZero = Bit(ins, 11) != 0;
        uint target = d.Address + 4 + ((Bit(ins, 9) << 6) | (Bits(ins, 7, 3) << 1));
        // taken when (Rn != 0) == nonZero: the taken path exits to the target, the other falls through
        B.BeginIf(Reg(ins & 7), runIfNonZero: nonZero);
        SetReg(15, C(target));
        B.Exit();
        B.EndRegion(terminated: true);
        return false; // the generic code emits the fall-through exit (R15 = next address)
    }

    bool? T16Extend(in DecodedInst d)
    {
        int v = Reg(Bits(d.Raw, 5, 3));
        SetReg(d.Raw & 7, Bits(d.Raw, 7, 6) switch { 0 => B.SExt16(v), 1 => B.SExt8(v), 2 => B.ZExt16(v), _ => B.ZExt8(v) });
        return false;
    }

    bool? T16Rev(in DecodedInst d)
    {
        int v = Reg(Bits(d.Raw, 5, 3));
        SetReg(d.Raw & 7, Bits(d.Raw, 7, 6) switch { 0 => B.Bswap(v), 1 => B.Rev16(v), _ => B.RevSh(v) });
        return false;
    }

    // ------------------------------------------------------------------ Thumb 32-bit

    /// <summary>T32.DataProc. Returns false (callout) for opcodes the interpreter does not define.</summary>
    bool T32DP(uint op, bool S, uint rd, int a, int b, int carry)
    {
        int r;
        (int R, int N, int Z, int C, int V)? f = null;
        switch (op)
        {
            case 0: r = B.And(a, b); break;
            case 1: r = B.AndNot(a, b); break;
            case 2: r = B.Or(a, b); break;
            case 3: r = B.OrNot(a, b); break;
            case 4: r = B.Xor(a, b); break;
            case 8: if (S) { f = B.AddC(a, b, -1); r = f.Value.R; } else r = B.Add(a, b); break;
            case 10: f = B.AddC(a, b, B.Flag(IrFlag.C)); r = f.Value.R; break;
            case 11: f = B.SubC(a, b, B.Flag(IrFlag.C)); r = f.Value.R; break;
            case 13: if (S) { f = B.SubC(a, b, -1); r = f.Value.R; } else r = B.Sub(a, b); break;
            case 14: if (S) { f = B.SubC(b, a, -1); r = f.Value.R; } else r = B.Sub(b, a); break;
            default: return false;
        }
        if (S)
        {
            if (f is { } ff) SetNZCV(ff);
            else { SetNZ(r); B.SetFlag(IrFlag.C, carry); }
        }
        if (rd != 15) SetReg(rd, r);
        return true;
    }

    static bool T32DPDefined(uint op) => op is 0 or 1 or 2 or 3 or 4 or 8 or 10 or 11 or 13 or 14;

    bool? T32DPModImm(in DecodedInst d)
    {
        uint ins = d.Raw, op = Bits(ins, 24, 21), rn = Bits(ins, 19, 16), rd = Bits(ins, 11, 8);
        if (!T32DPDefined(op)) return null;
        uint imm12 = (Bit(ins, 26) << 11) | (Bits(ins, 14, 12) << 8) | (ins & 0xFF);
        uint imm = ThumbExpandImmC(imm12, 0, out uint cout);
        int carry = (imm12 >> 10) == 0 ? -1 : C(cout);
        int a = (rn == 15 && (op == 2 || op == 3)) ? C(0) : Reg(rn);
        T32DP(op, Bit(ins, 20) != 0, rd, a, C(imm), carry);
        return false;
    }

    bool? T32DPShiftReg(in DecodedInst d)
    {
        uint ins = d.Raw, op = Bits(ins, 24, 21), rn = Bits(ins, 19, 16), rd = Bits(ins, 11, 8);
        if (op == 6)
        {
            if (rd == 15) return null;
            SetReg(rd, Pkh(Reg(rn), Reg(ins & 0xF), Bit(ins, 5) != 0, (Bits(ins, 14, 12) << 2) | Bits(ins, 7, 6)));
            return false;
        }
        if (!T32DPDefined(op)) return null; // undefined opcodes: interpreter
        uint imm5 = (Bits(ins, 14, 12) << 2) | Bits(ins, 7, 6);
        var (b, carry) = ShiftImm(Reg(ins & 0xF), Bits(ins, 5, 4), imm5);
        int a = (rn == 15 && (op == 2 || op == 3)) ? C(0) : Reg(rn);
        T32DP(op, Bit(ins, 20) != 0, rd, a, b, carry);
        return false;
    }

    bool? T32DPPlainImm(in DecodedInst d)
    {
        uint ins = d.Raw, op = Bits(ins, 24, 20), rn = Bits(ins, 19, 16), rd = Bits(ins, 11, 8);
        uint imm12 = (Bit(ins, 26) << 11) | (Bits(ins, 14, 12) << 8) | (ins & 0xFF);
        uint imm3 = Bits(ins, 14, 12), imm2 = Bits(ins, 7, 6);
        if (rd == 15) return null;
        uint pcBase = (d.Address + 4) & ~3u;
        int baseVal() => rn == 15 ? C(pcBase) : Reg(rn);
        switch (op)
        {
            case 0x00: SetReg(rd, B.AddK(baseVal(), imm12)); return false;
            case 0x0A: SetReg(rd, B.SubK(baseVal(), imm12)); return false;
            case 0x04: SetReg(rd, C((rn << 12) | imm12)); return false;
            case 0x0C: SetReg(rd, B.Or(B.AndK(Reg(rd), 0xFFFF), C(((rn << 12) | imm12) << 16))); return false;
            case 0x14: return Bfx(in d, rd, Reg(rn), false, (int)((imm3 << 2) | imm2), (int)(ins & 0x1F));
            case 0x1C: return Bfx(in d, rd, Reg(rn), true, (int)((imm3 << 2) | imm2), (int)(ins & 0x1F));
            case 0x16: return Bfi(in d, rd, rn, (int)((imm3 << 2) | imm2), (int)(ins & 0x1F));
            case 0x10: case 0x12: case 0x18: case 0x1A: return T32Sat(in d, op, rn, rd, imm3, imm2);
            default: return null; // no effect in the handler
        }
    }

    bool? T32ShiftReg(in DecodedInst d)
    {
        uint ins = d.Raw, rd = Bits(ins, 11, 8);
        if (rd == 15) return null;
        var (r, c) = B.ShiftReg(Bits(ins, 22, 21), Reg(Bits(ins, 19, 16)), Reg(ins & 0xF), B.Flag(IrFlag.C));
        SetReg(rd, r);
        if (Bit(ins, 20) != 0) { SetNZ(r); B.SetFlag(IrFlag.C, c); }
        return false;
    }

    bool? T32Extend(in DecodedInst d)
    {
        uint op = Bits(d.Raw, 22, 20);
        uint armOp = op switch { 0 => 3u, 1 => 7u, 2 => 0u, 3 => 4u, 4 => 2u, _ => 6u };
        return Extend(Bits(d.Raw, 11, 8), armOp, Bits(d.Raw, 19, 16), d.Raw & 0xF, (int)Bits(d.Raw, 5, 4) * 8);
    }

    bool? T32Misc(in DecodedInst d)
    {
        uint ins = d.Raw, op1 = Bits(ins, 21, 20), op2 = Bits(ins, 5, 4), rd = Bits(ins, 11, 8), rm = ins & 0xF;
        if (rd == 15) return null;
        if (op1 == 1)
        {
            int v = Reg(rm);
            SetReg(rd, op2 switch { 0 => B.Bswap(v), 1 => B.Rev16(v), 2 => B.Rbit(v), _ => B.RevSh(v) });
            return false;
        }
        if (op1 == 3) { SetReg(rd, B.Clz(Reg(rm))); return false; }
        uint rn = Bits(ins, 19, 16);
        if (op1 == 2) { SetReg(rd, Sel(Reg(rn), Reg(rm))); return false; }
        SetReg(rd, SatAddSub(((op2 & 1) << 1) | (op2 >> 1), Reg(rm), Reg(rn))); // QADD / QDADD / QSUB / QDSUB
        return false;
    }

    bool? T32Mul(in DecodedInst d) => T32MulMedia(in d);

    bool? T32LongMul(in DecodedInst d)
    {
        uint ins = d.Raw, op1 = Bits(ins, 22, 20), op2 = Bits(ins, 7, 4);
        uint rn = Bits(ins, 19, 16), lo = Bits(ins, 15, 12), hi = Bits(ins, 11, 8), rm = ins & 0xF;
        if (hi == 15 || (op1 is 0 or 2 or 4 or 6 && lo == 15)) return null;
        int vn = Reg(rn), vm = Reg(rm);
        switch (op1)
        {
            case 0: case 2: { var (l, h) = B.MulLong(vn, vm, op1 == 0); SetReg(lo, l); SetReg(hi, h); return false; }
            case 5: return T32LongMulMedia(in d, op1, op2, rn, lo, hi, rm); // SMLSLD
            case 4: case 6:
                if (op2 != 0) return T32LongMulMedia(in d, op1, op2, rn, lo, hi, rm); // SMLALxy / SMLALD / UMAAL
                {
                    var (l, h) = B.MulLong(vn, vm, op1 == 4);
                    (l, h) = B.Add64(l, h, Reg(lo), Reg(hi));
                    SetReg(lo, l); SetReg(hi, h);
                    return false;
                }
            case 1: SetReg(hi, B.SDiv(vn, vm)); return false;
            case 3: SetReg(hi, B.UDiv(vn, vm)); return false;
            default: return null;
        }
    }

    bool? T32BCondW(in DecodedInst d)
    {
        uint ins = d.Raw, S = Bit(ins, 26), j1 = Bit(ins, 13), j2 = Bit(ins, 11);
        uint imm = (S << 20) | (j2 << 19) | (j1 << 18) | (Bits(ins, 21, 16) << 12) | ((ins & 0x7FF) << 1);
        return DirectBranch(d.Address + 4 + SignExtend(imm, 21));
    }

    static uint BranchImm24(uint ins)
    {
        uint S = Bit(ins, 26), j1 = Bit(ins, 13), j2 = Bit(ins, 11);
        uint i1 = ~(j1 ^ S) & 1, i2 = ~(j2 ^ S) & 1;
        uint imm = (S << 24) | (i1 << 23) | (i2 << 22) | (Bits(ins, 25, 16) << 12) | ((ins & 0x7FF) << 1);
        return SignExtend(imm, 25);
    }

    bool? T32Bl(in DecodedInst d)
    {
        SetReg(14, C((d.Address + 4) | 1));
        SetReg(15, C(d.Address + 4 + BranchImm24(d.Raw)));
        return false;
    }

    bool? T32BlxImm(in DecodedInst d)
    {
        SetReg(14, C((d.Address + 4) | 1));
        B.SetThumb(C(0));
        SetReg(15, C((((d.Address + 4) & ~3u) + BranchImm24(d.Raw)) & ~3u));
        return false;
    }

    bool? T32Tbb(in DecodedInst d)
    {
        uint ins = d.Raw, rn = Bits(ins, 19, 16), rm = ins & 0xF;
        int baseAddr = rn == 15 ? C(d.Address + 4) : Reg(rn);
        int off = Bit(ins, 4) != 0 ? B.Load(IrOp.Load16, B.Add(baseAddr, B.Shl(Reg(rm), 1))) : B.Load(IrOp.Load8, B.Add(baseAddr, Reg(rm)));
        SetReg(15, B.Add(B.Shl(off, 1), C(d.Address + 4)));
        return false;
    }

    bool? T32LdStDual(in DecodedInst d)
    {
        uint ins = d.Raw, rn = Bits(ins, 19, 16), rt = Bits(ins, 15, 12), rt2 = Bits(ins, 11, 8);
        bool P = Bit(ins, 24) != 0, U = Bit(ins, 23) != 0, W = Bit(ins, 21) != 0, L = Bit(ins, 20) != 0;
        if (rt == 15 || rt2 == 15 || (W && rn == 15)) return null;
        uint imm = (ins & 0xFF) << 2;
        int baseVal = rn == 15 ? C((d.Address + 4) & ~3u) : Reg(rn);
        int offAddr = U ? B.AddK(baseVal, imm) : B.SubK(baseVal, imm);
        int addr = P ? offAddr : baseVal;
        if (L)
        {
            int a = B.Load(IrOp.Load32, addr), b = B.Load(IrOp.Load32, B.AddK(addr, 4));
            if (W) SetReg(rn, offAddr);
            SetReg(rt, a); SetReg(rt2, b);
        }
        else
        {
            B.Store(IrOp.Store32, addr, Reg(rt));
            B.Store(IrOp.Store32, B.AddK(addr, 4), Reg(rt2));
            if (W) SetReg(rn, offAddr);
        }
        return false;
    }

    bool? T32LdSt(in DecodedInst d)
    {
        uint ins = d.Raw, size = Bits(ins, 22, 21), rn = Bits(ins, 19, 16), rt = Bits(ins, 15, 12);
        bool signed = Bit(ins, 24) != 0, imm12Form = Bit(ins, 23) != 0, L = Bit(ins, 20) != 0;
        int addr, offAddr = -1;
        bool wback = false;
        if (rn == 15)
        {
            uint basePc = (d.Address + 4) & ~3u;
            addr = C(imm12Form ? basePc + (ins & 0xFFF) : basePc - (ins & 0xFFF));
        }
        else if (imm12Form) addr = B.AddK(Reg(rn), ins & 0xFFF);
        else if (Bit(ins, 11) != 0)
        {
            bool P = Bit(ins, 10) != 0, U = Bit(ins, 9) != 0;
            wback = Bit(ins, 8) != 0;
            uint imm8 = ins & 0xFF;
            int bv = Reg(rn);
            offAddr = U ? B.AddK(bv, imm8) : B.SubK(bv, imm8);
            addr = P ? offAddr : bv;
        }
        else addr = B.Add(Reg(rn), B.Shl(Reg(ins & 0xF), (int)Bits(ins, 5, 4)));

        if (L)
        {
            int v = size switch
            {
                0 => B.Load(signed ? IrOp.Load8S : IrOp.Load8, addr),
                1 => B.Load(signed ? IrOp.Load16S : IrOp.Load16, addr),
                _ => B.Load(IrOp.Load32, addr),
            };
            if (wback) SetReg(rn, offAddr);
            if (rt == 15) { if (size == 2) BxWritePc(v); }
            else SetReg(rt, v);
        }
        else
        {
            int v = Reg(rt);
            B.Store(size switch { 0 => IrOp.Store8, 1 => IrOp.Store16, _ => IrOp.Store32 }, addr, v);
            if (wback) SetReg(rn, offAddr);
        }
        return false;
    }

    // ------------------------------------------------------------------ VFP register transfers and loads/stores
    // The SIMD register file stays in CpuState (callouts for arithmetic read it there): these are plain moves
    // between it, the core registers and memory, with the handlers' exact address arithmetic and order.

    static uint SingleD(uint ins) => (Bits(ins, 15, 12) << 1) | Bit(ins, 22);
    static uint SingleN(uint ins) => (Bits(ins, 19, 16) << 1) | Bit(ins, 7);
    static uint SingleM(uint ins) => ((ins & 0xF) << 1) | Bit(ins, 5);
    static uint DoubleD(uint ins) => (Bit(ins, 22) << 4) | Bits(ins, 15, 12);
    static uint DoubleN(uint ins) => (Bit(ins, 7) << 4) | Bits(ins, 19, 16);
    static uint DoubleM(uint ins) => (Bit(ins, 5) << 4) | (ins & 0xF);
    static uint SOff(uint s) => DOffset + 4 * s;           // S register (S32.. alias D16.. like the handlers' uint view)
    static uint DOff(uint d) => DOffset + 8 * d;

    void LoadPair(int addr, uint off)
    {
        int lo = B.Load(IrOp.Load32, addr), hi = B.Load(IrOp.Load32, B.AddK(addr, 4));
        B.StoreState(off, lo); B.StoreState(off + 4, hi);
    }

    void StorePair(int addr, uint off)
    {
        int lo = B.LoadState(off), hi = B.LoadState(off + 4);
        B.Store(IrOp.Store32, addr, lo); B.Store(IrOp.Store32, B.AddK(addr, 4), hi);
    }

    bool? VfpLdrStr(in DecodedInst d, bool thumb)
    {
        uint ins = d.Raw, rn = Bits(ins, 19, 16), imm = (ins & 0xFF) << 2;
        bool u = Bit(ins, 23) != 0, l = Bit(ins, 20) != 0, dbl = Bit(ins, 8) != 0;
        int baseVal = rn == 15 ? C((d.Address + (thumb ? 4u : 8u)) & ~3u) : Reg(rn);
        int addr = u ? B.AddK(baseVal, imm) : B.SubK(baseVal, imm);
        if (dbl)
        {
            uint off = DOff(DoubleD(ins));
            if (l) LoadPair(addr, off); else StorePair(addr, off);
        }
        else
        {
            uint off = SOff(SingleD(ins));
            if (l) B.StoreState(off, B.Load(IrOp.Load32, addr)); else B.Store(IrOp.Store32, addr, B.LoadState(off));
        }
        return false;
    }

    bool? VfpLdmStm(in DecodedInst d)
    {
        uint ins = d.Raw, rn = Bits(ins, 19, 16), imm8 = ins & 0xFF;
        bool p = Bit(ins, 24) != 0, u = Bit(ins, 23) != 0, w = Bit(ins, 21) != 0, l = Bit(ins, 20) != 0, dbl = Bit(ins, 8) != 0;
        if (rn == 15) return null;
        int baseVal = Reg(rn);
        int start = p ? B.SubK(baseVal, imm8 * 4) : baseVal;
        if (w) SetReg(rn, u ? B.AddK(baseVal, imm8 * 4) : B.SubK(baseVal, imm8 * 4));
        if (dbl)
        {
            uint dd = DoubleD(ins), regs = imm8 >> 1;
            for (uint i = 0; i < regs; i++)
            {
                int addr = B.AddK(start, i * 8);
                if (l) LoadPair(addr, DOff((dd + i) & 31)); else StorePair(addr, DOff((dd + i) & 31));
            }
        }
        else
        {
            uint sd = SingleD(ins);
            for (uint i = 0; i < imm8; i++)
            {
                int addr = B.AddK(start, i * 4);
                uint off = SOff((sd + i) & 63);
                if (l) B.StoreState(off, B.Load(IrOp.Load32, addr)); else B.Store(IrOp.Store32, addr, B.LoadState(off));
            }
        }
        return false;
    }

    bool? VfpMovCoreSingle(in DecodedInst d)
    {
        uint ins = d.Raw, rt = Bits(ins, 15, 12), off = SOff(SingleN(ins));
        if (Bit(ins, 20) != 0)
        {
            if (rt == 15) return null;
            SetReg(rt, B.LoadState(off));
        }
        else B.StoreState(off, Reg(rt));
        return false;
    }

    bool? VfpMov64(in DecodedInst d)
    {
        uint ins = d.Raw, rt2 = Bits(ins, 19, 16), rt = Bits(ins, 15, 12);
        bool toCore = Bit(ins, 20) != 0;
        if (toCore && (rt == 15 || rt2 == 15)) return null;
        uint lo, hi;
        if (Bit(ins, 8) == 0) { uint m = SingleM(ins); lo = SOff(m); hi = SOff(m + 1); }
        else { uint m = DoubleM(ins); lo = DOff(m); hi = lo + 4; }
        if (toCore) { int a = B.LoadState(lo), b = B.LoadState(hi); SetReg(rt, a); SetReg(rt2, b); }
        else { B.StoreState(lo, Reg(rt)); B.StoreState(hi, Reg(rt2)); }
        return false;
    }

    bool? VfpVmrs(in DecodedInst d)
    {
        uint ins = d.Raw, rt = Bits(ins, 15, 12), reg = Bits(ins, 19, 16);
        int v = reg switch
        {
            1 => B.LoadState(FpscrOffset),
            0 => C(0x41033094),
            6 or 7 => C(0x10110222),
            8 => C(0x40000000),
            _ => C(0),
        };
        if (rt == 15)
        {
            B.SetFlag(IrFlag.N, B.Bit(v, 31)); B.SetFlag(IrFlag.Z, B.Bit(v, 30));
            B.SetFlag(IrFlag.C, B.Bit(v, 29)); B.SetFlag(IrFlag.V, B.Bit(v, 28));
        }
        else SetReg(rt, v);
        return false;
    }

    bool? VfpVmsr(in DecodedInst d)
    {
        if (Bits(d.Raw, 19, 16) == 1) B.StoreState(FpscrOffset, Reg(Bits(d.Raw, 15, 12)));
        return false;
    }

    bool? VfpVdupCore(in DecodedInst d)
    {
        uint ins = d.Raw, dd = DoubleN(ins), rt = Bits(ins, 15, 12);
        bool q = Bit(ins, 21) != 0;
        uint be = (Bit(ins, 22) << 1) | Bit(ins, 5);
        int v = Reg(rt);
        int rep = be switch
        {
            0 => v,
            1 => B.Mul(B.AndK(v, 0xFFFF), C(0x00010001)),
            _ => B.Mul(B.AndK(v, 0xFF), C(0x01010101)),
        };
        for (uint k = 0; k < (q ? 2u : 1u); k++) { B.StoreState(DOff(dd + k), rep); B.StoreState(DOff(dd + k) + 4, rep); }
        return false;
    }

    bool? VfpMovCoreScalar(in DecodedInst d)
    {
        uint ins = d.Raw, dd = DoubleN(ins), rt = Bits(ins, 15, 12);
        uint opc = (Bits(ins, 22, 21) << 2) | Bits(ins, 6, 5);
        int v = Reg(rt);
        uint byteOff, size;
        if ((opc & 8) != 0) { byteOff = opc & 7; size = 1; }
        else if ((opc & 1) != 0) { byteOff = ((opc >> 1) & 3) * 2; size = 2; }
        else { B.StoreState(DOff(dd) + ((opc >> 2) & 1) * 4, v); return false; }
        uint wordOff = DOff(dd) + (byteOff & 4), sh = (byteOff & 3) * 8, mask = (size == 1 ? 0xFFu : 0xFFFFu) << (int)sh;
        int word = B.LoadState(wordOff);
        B.StoreState(wordOff, B.Or(B.AndK(word, ~mask), B.AndK(B.Shl(v, (int)sh), mask)));
        return false;
    }

    bool? VfpMovScalarCore(in DecodedInst d)
    {
        uint ins = d.Raw, n = DoubleN(ins), rt = Bits(ins, 15, 12);
        if (rt == 15) return null;
        bool u = Bit(ins, 23) != 0;
        uint opc = (Bits(ins, 22, 21) << 2) | Bits(ins, 6, 5);
        if ((opc & 8) == 0 && (opc & 1) == 0) { SetReg(rt, B.LoadState(DOff(n) + ((opc >> 2) & 1) * 4)); return false; }
        uint byteOff = (opc & 8) != 0 ? opc & 7 : ((opc >> 1) & 3) * 2;
        int word = B.LoadState(DOff(n) + (byteOff & 4));
        int f = B.Shr(word, (int)((byteOff & 3) * 8));
        int v = (opc & 8) != 0 ? (u ? B.ZExt8(f) : B.SExt8(f)) : (u ? B.ZExt16(f) : B.SExt16(f));
        SetReg(rt, v);
        return false;
    }

    // ------------------------------------------------------------------ VFP arithmetic
    // Bit-level operations (VMOV, VABS, VNEG, VMOV immediate) are integer IR; real arithmetic becomes FpOp, which the
    // emitter runs natively in the Vita's default FPSCR mode and through the interpreter handler otherwise.

    bool? VfpMovImm(in DecodedInst d)
    {
        uint ins = d.Raw, imm8 = (Bits(ins, 19, 16) << 4) | (ins & 0xF);
        if (Bit(ins, 8) == 0)
        {
            uint v = ((imm8 & 0x80) << 24) | ((imm8 & 0x40) != 0 ? 0x3E000000u : 0x40000000u) | ((imm8 & 0x3F) << 19);
            B.StoreState(SOff(SingleD(ins)), C(v));
        }
        else
        {
            ulong v = ((ulong)(imm8 & 0x80) << 56) | ((imm8 & 0x40) != 0 ? 0x3FC0000000000000ul : 0x4000000000000000ul) | ((ulong)(imm8 & 0x3F) << 48);
            uint off = DOff(DoubleD(ins));
            B.StoreState(off, C((uint)v)); B.StoreState(off + 4, C((uint)(v >> 32)));
        }
        return false;
    }

    bool? VfpArith(in DecodedInst d)
    {
        uint ins = d.Raw, opc1 = (Bit(ins, 23) << 2) | Bits(ins, 21, 20);
        bool op = Bit(ins, 6) != 0, dbl = Bit(ins, 8) != 0;
        FpKind kind;
        switch (opc1)
        {
            case 0: kind = op ? FpKind.Mls : FpKind.Mla; break;
            case 1: kind = op ? FpKind.Nmla : FpKind.Nmls; break;
            case 2: kind = op ? FpKind.NMul : FpKind.Mul; break;
            case 3: kind = op ? FpKind.Sub : FpKind.Add; break;
            case 4: if (op) return null; kind = FpKind.Div; break;
            default: return null; // VFMA family (VFPv4: not on the Cortex-A9) and the rest
        }
        if (dbl) B.FpOp(kind, FpFlags.Double, DoubleD(ins) * 8, DoubleN(ins) * 8, DoubleM(ins) * 8, ins, d.Address);
        else B.FpOp(kind, 0, SingleD(ins) * 4, SingleN(ins) * 4, SingleM(ins) * 4, ins, d.Address);
        return false;
    }

    bool? VfpOther(in DecodedInst d)
    {
        uint ins = d.Raw, opc2 = Bits(ins, 19, 16);
        bool dbl = Bit(ins, 8) != 0, b7 = Bit(ins, 7) != 0;
        uint sd = SingleD(ins) * 4, sm = SingleM(ins) * 4, dd = DoubleD(ins) * 8, dm = DoubleM(ins) * 8;
        const FpFlags O = FpFlags.OtherOp;
        switch (opc2)
        {
            case 0: case 1:
                if (opc2 == 1 && b7) // VSQRT
                {
                    B.FpOp(FpKind.Sqrt, O | (dbl ? FpFlags.Double : 0), dbl ? dd : sd, 0, dbl ? dm : sm, ins, d.Address);
                    return false;
                }
                // VMOV / VABS / VNEG: bit operations on the high word holding the sign
                if (!dbl)
                {
                    int v = B.LoadState(DOffset + sm);
                    B.StoreState(DOffset + sd, opc2 == 0 ? (b7 ? B.AndK(v, 0x7FFFFFFF) : v) : B.Xor(v, C(0x80000000)));
                }
                else
                {
                    int lo = B.LoadState(DOffset + dm), hi = B.LoadState(DOffset + dm + 4);
                    B.StoreState(DOffset + dd, lo);
                    B.StoreState(DOffset + dd + 4, opc2 == 0 ? (b7 ? B.AndK(hi, 0x7FFFFFFF) : hi) : B.Xor(hi, C(0x80000000)));
                }
                return false;
            case 2: case 3:
            {
                // VCVTB/VCVTT: the half lives in bits 15:0 (B) or 31:16 (T) of the single register
                if (dbl || !_supportsVec(VecKind.CvtF2H, 2)) return null;
                uint half = b7 ? 2u : 0u;
                if (opc2 == 3) B.FpOp(FpKind.CvtToHalf, O, sd + half, 0, sm, ins, d.Address);
                else B.FpOp(FpKind.CvtFromHalf, O, sd, 0, sm + half, ins, d.Address);
                return false;
            }
            case 4: case 5:
                B.FpOp(opc2 == 5 ? FpKind.CmpZero : FpKind.Cmp, O | (dbl ? FpFlags.Double : 0), dbl ? dd : sd, 0, dbl ? dm : sm, ins, d.Address);
                return false;
            case 7:
                if (!dbl) B.FpOp(FpKind.CvtToDouble, O, dd, 0, sm, ins, d.Address);
                else B.FpOp(FpKind.CvtToSingle, O | FpFlags.Double, sd, 0, dm, ins, d.Address);
                return false;
            case 8:
                B.FpOp(b7 ? FpKind.CvtFromS32 : FpKind.CvtFromU32, O | (dbl ? FpFlags.Double : 0), dbl ? dd : sd, 0, sm, ins, d.Address);
                return false;
            case 0xC: case 0xD:
            {
                bool signed = opc2 == 0xD;
                var kind = b7 ? (signed ? FpKind.CvtToS32 : FpKind.CvtToU32) : (signed ? FpKind.CvtToS32R : FpKind.CvtToU32R);
                B.FpOp(kind, O | (dbl ? FpFlags.Double : 0), sd, 0, dbl ? dm : sm, ins, d.Address);
                return false;
            }
            default: return null; // half-precision and fixed-point conversions
        }
    }

    // ------------------------------------------------------------------ exclusive monitor (word forms)

    /// <summary>LDREX/STREX word (A32.ExclusiveCore op 0); byte/halfword/doubleword forms stay callouts.</summary>
    bool? A32Exclusive(in DecodedInst d)
    {
        uint ins = d.Raw, op = Bits(ins, 22, 21), rn = Bits(ins, 19, 16), rd = Bits(ins, 15, 12), rt = ins & 0xF;
        bool L = Bit(ins, 20) != 0;
        if (op == 1)
        {
            // LDREXD Rt(15:12), Rt+1 / STREXD Rd(15:12), Rt(3:0), Rt+1; odd or R14 first registers are UNPREDICTABLE
            uint first = L ? rd : rt;
            if (rn == 15 || first >= 14 || (!L && rd == 15)) return null;
            return ExclusiveDual(Reg(rn), L, first, first + 1, rd);
        }
        if (op != 0 || rn == 15 || rd == 15 || (!L && rt == 15)) return null;
        int addr = Reg(rn);
        if (L) SetReg(rd, B.LoadExclusive(addr));
        else SetReg(rd, B.StoreExclusive(addr, Reg(rt)));
        return false;
    }

    /// <summary>LDREXD/STREXD (Thumb-2); byte and halfword forms stay callouts.</summary>
    bool? T32ExclusiveOther(in DecodedInst d)
    {
        uint ins = d.Raw, rn = Bits(ins, 19, 16), rt = Bits(ins, 15, 12), rt2 = Bits(ins, 11, 8), op3 = Bits(ins, 7, 4), rd = ins & 0xF;
        bool L = Bit(ins, 20) != 0;
        if (op3 != 7 || rn == 15 || rt == 15 || rt2 == 15 || (!L && rd == 15)) return null;
        return ExclusiveDual(Reg(rn), L, rt, rt2, rd);
    }

    /// <summary>Doubleword exclusive: the interpreter's ExclusiveCore op 1 (64-bit read / 64-bit CAS).</summary>
    bool? ExclusiveDual(int addr, bool load, uint rt, uint rt2, uint rd)
    {
        if (load)
        {
            var (lo, hi) = B.LoadExclusiveDual(addr);
            SetReg(rt, lo);
            SetReg(rt2, hi); // like the handler: R[rt2] written last
        }
        else SetReg(rd, B.StoreExclusiveDual(addr, Reg(rt), Reg(rt2)));
        return false;
    }

    bool? T32ExclusiveWord(in DecodedInst d)
    {
        uint ins = d.Raw, rn = Bits(ins, 19, 16), rt = Bits(ins, 15, 12), rd = Bits(ins, 11, 8);
        bool L = Bit(ins, 20) != 0;
        if (rn == 15 || rt == 15 || (!L && rd == 15)) return null;
        int addr = B.AddK(Reg(rn), (ins & 0xFF) << 2);
        if (L) SetReg(rt, B.LoadExclusive(addr));
        else SetReg(rd, B.StoreExclusive(addr, Reg(rt)));
        return false;
    }
}
