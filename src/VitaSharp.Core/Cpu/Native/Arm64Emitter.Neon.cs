using static VitaSharp.Core.Cpu.Native.Arm64Assembler;

namespace VitaSharp.Core.Cpu.Native;

/// <summary>
/// Advanced SIMD lowering on ARM64: nearly every ARMv7 NEON operation is one ASIMD instruction with the same lane
/// semantics (AArch32 and AArch64 share the pseudocode), run on v0-v7/v16-v17 with operands loaded from and stored to
/// the guest register file in the state (D forms use the 64-bit arrangements, so the other half of a Q register is
/// untouched). Floats run under the guest FPCR (FZ, DN, round to nearest = the Advanced SIMD standard mode), so no
/// explicit flushing is needed. The exceptions are handled explicitly: VMLA/VMLS.F32 and VRECPS/VRSQRTS are not fused
/// in AArch32 (FMUL then FADD/FSUB, with VRECPS/VRSQRTS's infinity * zero rule), 64-bit lanes of D registers use the
/// scalar forms, VTBL/VTBX limit the indices to the table length, and QC comes from FPSR.QC.
/// </summary>
public sealed unsafe partial class Arm64Emitter
{
    /// <summary>Whether <paramref name="k"/> (at lane <paramref name="size"/>) has an inline lowering (every ARMv8-A core has ASIMD).</summary>
    public static bool SupportsVec(VecKind k, uint size = 2) => true;

    // ------------------------------------------------------------------ operand helpers

    /// <summary>Loads Vr from the register file at byte offset <paramref name="off"/> from D0 (Q: 16 bytes, else 8).</summary>
    void LdV(int r, int off, bool q) => VMem(q ? LsKind.LdrQ : LsKind.LdrD, r, OffD + off);
    void StV(int off, int r, bool q) => VMem(q ? LsKind.StrQ : LsKind.StrD, r, OffD + off);

    void VMem(LsKind k, int r, int off)
    {
        if (FitsOffset(k, off)) _a.LdSt(k, r, State, off);
        else { _a.AddImm(X16, State, (uint)off, true); _a.LdSt(k, r, X16, 0); }
    }

    /// <summary>Vr = <paramref name="v"/> replicated in every 32-bit lane.</summary>
    void Splat32(int r, uint v) => _a.LdrLitQ(r, v | (ulong)v << 32, v | (ulong)v << 32);

    /// <summary>Three-same integer op; 64-bit lanes of a D register use the scalar encoding (1D is reserved).</summary>
    void I3(uint opcode, bool u, uint size, bool q, int rd, int rn, int rm)
    {
        if (size == 3 && !q) _a.Emit(0x5E200400 | (u ? 1u << 29 : 0) | 3u << 22 | (uint)rm << 16 | opcode << 11 | (uint)rn << 5 | (uint)rd);
        else _a.V3(opcode, u, size, q, rd, rn, rm);
    }

    /// <summary>Shift by immediate; 64-bit lanes of a D register use the scalar encoding.</summary>
    void ShImm(uint opcode, bool u, uint size, bool q, uint immhb, int rd, int rn)
    {
        if (size == 3 && !q) _a.SShiftImm(opcode, u, immhb, rd, rn);
        else _a.VShiftImm(opcode, u, q, immhb, rd, rn);
    }

    /// <summary>Clears FPSR (so QC reflects this op only).</summary>
    void QcBegin() => _a.Msr(SysReg.Fpsr, ZR);

    /// <summary>FPSCR.QC |= FPSR.QC.</summary>
    void QcEnd()
    {
        var a = _a;
        a.Mrs(X16, SysReg.Fpsr);
        a.Ubfx(X16, X16, 27, 1);
        LdState(X17, OffFpscr);
        a.Orr(X17, X17, X16, Shift.Lsl, 27);
        StState(X17, OffFpscr);
    }

    // ------------------------------------------------------------------ lowering

    void VecOp(IrInst x)
    {
        var a = _a;
        var kind = (VecKind)(x.Imm & 0xFF);
        bool q = (x.Imm & (uint)VecFlags.Q) != 0;
        uint size = (x.Imm >> 9) & 3;
        bool u = (x.Imm & (uint)VecFlags.Unsigned) != 0;
        bool round = (x.Imm & (uint)VecFlags.Round) != 0;
        bool acc = (x.Imm & (uint)VecFlags.Accumulate) != 0;
        int aux = (int)(x.Imm2 & 0xFFFF);
        int dOff = (int)(x.Imm4 & 0xFF), nOff = (int)((x.Imm4 >> 8) & 0xFF), mOff = (int)((x.Imm4 >> 16) & 0xFF);
        const int V0 = 0, V1 = 1, V2 = 2, V3 = 3, V4 = 4, V5 = 5, V6 = 6, V7 = 7;

        void Load3(bool withD) { LdV(V1, nOff, q); LdV(V2, mOff, q); if (withD) LdV(V0, dOff, q); }
        void Store() => StV(dOff, V0, q);

        switch (kind)
        {
            // ---------------------------------------------------------- bitwise and integer three-same
            case VecKind.And: case VecKind.Bic: case VecKind.Orr: case VecKind.Orn: case VecKind.Eor:
            case VecKind.Bsl: case VecKind.Bit: case VecKind.Bif:
            {
                int k = kind - VecKind.And;            // U:size of the ASIMD logical group, in the guest's order
                Load3(k >= 5);
                a.V3(3, k >= 4, (uint)k & 3, q, V0, V1, V2);
                Store();
                return;
            }
            case VecKind.AddI: case VecKind.SubI:
                Load3(false); I3(0x10, kind == VecKind.SubI, size, q, V0, V1, V2); Store(); return;
            case VecKind.MulI: Load3(false); a.V3(0x13, false, size, q, V0, V1, V2); Store(); return;
            case VecKind.MlaI: case VecKind.MlsI:
                Load3(true); a.V3(0x12, kind == VecKind.MlsI, size, q, V0, V1, V2); Store(); return;
            case VecKind.CeqI: Load3(false); a.V3(0x11, true, size, q, V0, V1, V2); Store(); return;
            case VecKind.TstI: Load3(false); a.V3(0x11, false, size, q, V0, V1, V2); Store(); return;
            case VecKind.CgtI: Load3(false); a.V3(0x06, u, size, q, V0, V1, V2); Store(); return;
            case VecKind.CgeI: Load3(false); a.V3(0x07, u, size, q, V0, V1, V2); Store(); return;
            case VecKind.MaxI: Load3(false); a.V3(0x0C, u, size, q, V0, V1, V2); Store(); return;
            case VecKind.MinI: Load3(false); a.V3(0x0D, u, size, q, V0, V1, V2); Store(); return;
            case VecKind.AbdI: Load3(false); a.V3(0x0E, u, size, q, V0, V1, V2); Store(); return;
            case VecKind.AbaI: Load3(true); a.V3(0x0F, u, size, q, V0, V1, V2); Store(); return;
            case VecKind.HaddI: Load3(false); a.V3(0x00, u, size, q, V0, V1, V2); Store(); return;
            case VecKind.RhaddI: Load3(false); a.V3(0x02, u, size, q, V0, V1, V2); Store(); return;
            case VecKind.HsubI: Load3(false); a.V3(0x04, u, size, q, V0, V1, V2); Store(); return;
            case VecKind.QaddI: case VecKind.QsubI:
                Load3(false); QcBegin(); I3(kind == VecKind.QaddI ? 0x01u : 0x05u, u, size, q, V0, V1, V2); Store(); QcEnd(); return;
            case VecKind.PaddI: Load3(false); a.V3(0x17, false, size, q, V0, V1, V2); Store(); return;
            case VecKind.PmaxI: Load3(false); a.V3(0x14, u, size, q, V0, V1, V2); Store(); return;
            case VecKind.PminI: Load3(false); a.V3(0x15, u, size, q, V0, V1, V2); Store(); return;
            case VecKind.QdmulhI: case VecKind.QrdmulhI:
                Load3(false); QcBegin(); a.V3(0x16, kind == VecKind.QrdmulhI, size, q, V0, V1, V2); Store(); QcEnd(); return;
            case VecKind.ShlRegI:
                // VSHL (register) Dd, Dm, Dn: the value is m, the signed shift amounts are the low bytes of n's lanes
                Load3(false); I3(0x08, u, size, q, V0, V2, V1); Store(); return;

            // ---------------------------------------------------------- float three-same (size field = a:sz)
            case VecKind.AddF: Load3(false); a.V3(0x1A, false, 0, q, V0, V1, V2); Store(); return;
            case VecKind.SubF: Load3(false); a.V3(0x1A, false, 2, q, V0, V1, V2); Store(); return;
            case VecKind.MulF: Load3(false); a.V3(0x1B, true, 0, q, V0, V1, V2); Store(); return;
            case VecKind.MlaF: case VecKind.MlsF:
                // AArch32 VMLA/VMLS.F32 round the product first (not fused)
                Load3(true);
                a.V3(0x1B, true, 0, q, V3, V1, V2);
                a.V3(0x1A, false, kind == VecKind.MlsF ? 2u : 0u, q, V0, V0, V3);
                Store();
                return;
            case VecKind.FmaF: case VecKind.FmsF: Load3(true); a.V3(0x19, false, kind == VecKind.FmsF ? 2u : 0u, q, V0, V1, V2); Store(); return;
            case VecKind.PaddF: Load3(false); a.V3(0x1A, true, 0, q, V0, V1, V2); Store(); return;
            case VecKind.AbdF: Load3(false); a.V3(0x1A, true, 2, q, V0, V1, V2); Store(); return;
            case VecKind.CeqF: Load3(false); a.V3(0x1C, false, 0, q, V0, V1, V2); Store(); return;
            case VecKind.CgeF: Load3(false); a.V3(0x1C, true, 0, q, V0, V1, V2); Store(); return;
            case VecKind.CgtF: Load3(false); a.V3(0x1C, true, 2, q, V0, V1, V2); Store(); return;
            case VecKind.AcgeF: Load3(false); a.V3(0x1D, true, 0, q, V0, V1, V2); Store(); return;
            case VecKind.AcgtF: Load3(false); a.V3(0x1D, true, 2, q, V0, V1, V2); Store(); return;
            case VecKind.MaxF: Load3(false); a.V3(0x1E, false, 0, q, V0, V1, V2); Store(); return;
            case VecKind.MinF: Load3(false); a.V3(0x1E, false, 2, q, V0, V1, V2); Store(); return;
            case VecKind.PmaxF: Load3(false); a.V3(0x1E, true, 0, q, V0, V1, V2); Store(); return;
            case VecKind.PminF: Load3(false); a.V3(0x1E, true, 2, q, V0, V1, V2); Store(); return;
            case VecKind.RecpsF: case VecKind.RsqrtsF:
            {
                // AArch32 FPRecipStep / FPRSqrtStep: 2 - n*m and (3 - n*m) / 2 with the product rounded (and flushed)
                // first; infinity * zero (zero including flushed denormals) gives exactly 2 / 1.5
                bool rs = kind == VecKind.RsqrtsF;
                Load3(false);
                a.V3(0x1B, true, 0, q, V3, V1, V2);                  // p = n * m
                Splat32(V4, rs ? 0x40400000u : 0x40000000u);         // 3 / 2
                a.V3(0x1A, false, 2, q, V0, V4, V3);                 // K - p
                if (rs) { Splat32(V4, 0x3F000000u); a.V3(0x1B, true, 0, q, V0, V0, V4); } // * 0.5 (exact halving)
                Splat32(V7, 0x7F800000u);
                a.V2(0x0F, false, 2, q, V5, V1); a.V3(0x1C, false, 0, q, V5, V5, V7);      // |n| == inf
                a.V2(0x0F, false, 2, q, V6, V2); a.V3(0x1C, false, 0, q, V6, V6, V7);      // |m| == inf
                a.V2(0x0D, false, 2, q, V3, V1);                                          // n == 0
                a.V2(0x0D, false, 2, q, V4, V2);                                          // m == 0
                a.V3(3, false, 0, q, V5, V5, V4);                                         // inf(n) & zero(m)
                a.V3(3, false, 0, q, V6, V6, V3);                                         // inf(m) & zero(n)
                a.V3(3, false, 2, q, V5, V5, V6);
                Splat32(V4, rs ? 0x3FC00000u : 0x40000000u);         // 1.5 / 2
                a.V3(3, true, 2, q, V0, V4, V5);                     // BIT: take the constant where the mask is set
                Store();
                return;
            }

            // ---------------------------------------------------------- by scalar (aux = byte offset of the element)
            case VecKind.MulFS: case VecKind.MlaFS: case VecKind.MlsFS:
                LdV(V1, nOff, q);
                VMem(LsKind.LdrS, V2, OffD + aux);
                a.DupElem(2, q, V2, V2, 0);
                if (kind == VecKind.MulFS) a.V3(0x1B, true, 0, q, V0, V1, V2);
                else
                {
                    LdV(V0, dOff, q);
                    a.V3(0x1B, true, 0, q, V3, V1, V2);
                    a.V3(0x1A, false, kind == VecKind.MlsFS ? 2u : 0u, q, V0, V0, V3);
                }
                Store();
                return;
            case VecKind.MulIS: case VecKind.MlaIS: case VecKind.MlsIS:
                LdV(V1, nOff, q);
                VMem(size == 1 ? LsKind.LdrH : LsKind.LdrS, V2, OffD + aux);
                a.DupElem(size, q, V2, V2, 0);
                if (kind == VecKind.MulIS) a.V3(0x13, false, size, q, V0, V1, V2);
                else { LdV(V0, dOff, q); a.V3(0x12, kind == VecKind.MlsIS, size, q, V0, V1, V2); }
                Store();
                return;

            // ---------------------------------------------------------- shifts by immediate (source m)
            case VecKind.ShrImm:
            {
                LdV(V2, mOff, q);
                if (acc) LdV(V0, dOff, q);
                uint opc = (round ? 4u : 0u) | (acc ? 2u : 0u);      // SSHR SSRA SRSHR SRSRA
                ShImm(opc, u, size, q, ShrImmField(size, aux), V0, V2);
                Store();
                return;
            }
            case VecKind.ShlImm:
                LdV(V2, mOff, q);
                ShImm(0x0A, false, size, q, ShlImmField(size, aux), V0, V2);
                Store();
                return;
            case VecKind.SliImm:
                LdV(V2, mOff, q); LdV(V0, dOff, q);
                ShImm(0x0A, true, size, q, ShlImmField(size, aux), V0, V2);
                Store();
                return;
            case VecKind.SriImm:
                LdV(V2, mOff, q); LdV(V0, dOff, q);
                ShImm(0x08, true, size, q, ShrImmField(size, aux), V0, V2);
                Store();
                return;
            case VecKind.ShrnImm: // Q source, D result (size = narrow lanes)
                LdV(V2, mOff, true);
                a.VShiftImm(round ? 0x11u : 0x10u, false, false, ShrImmField(size, aux), V0, V2);
                StV(dOff, V0, false);
                return;
            case VecKind.QshrnImm: case VecKind.QshrunImm:
                LdV(V2, mOff, true);
                QcBegin();
                if (kind == VecKind.QshrunImm) a.VShiftImm(round ? 0x11u : 0x10u, true, false, ShrImmField(size, aux), V0, V2);
                else a.VShiftImm(round ? 0x13u : 0x12u, u, false, ShrImmField(size, aux), V0, V2);
                StV(dOff, V0, false);
                QcEnd();
                return;
            case VecKind.ShllImm: // D source, Q result
                LdV(V2, mOff, false);
                if (aux == 8 << (int)size) a.V2(0x13, true, size, false, V0, V2);       // SHLL (shift = lane size)
                else a.VShiftImm(0x14, u, false, ShlImmField(size, aux), V0, V2);   // SSHLL / USHLL
                StV(dOff, V0, true);
                return;

            // ---------------------------------------------------------- two registers, miscellaneous (source m)
            case VecKind.Rev:
                LdV(V2, mOff, q);
                a.V2(aux == 2 ? 1u : 0u, aux == 4, size, q, V0, V2);  // REV16 / REV32 / REV64
                Store();
                return;
            case VecKind.Mvn: LdV(V2, mOff, q); a.V2(0x05, true, 0, q, V0, V2); Store(); return;
            case VecKind.AbsF: LdV(V2, mOff, q); a.V2(0x0F, false, 2, q, V0, V2); Store(); return;
            case VecKind.NegF: LdV(V2, mOff, q); a.V2(0x0F, true, 2, q, V0, V2); Store(); return;
            case VecKind.AbsI: LdV(V2, mOff, q); a.V2(0x0B, false, size, q, V0, V2); Store(); return;
            case VecKind.NegI: LdV(V2, mOff, q); a.V2(0x0B, true, size, q, V0, V2); Store(); return;
            case VecKind.CmpZeroI:
            {
                LdV(V2, mOff, q);
                (uint opc, bool uu) = aux switch { 0 => (0x08u, false), 1 => (0x08u, true), 2 => (0x09u, false), 3 => (0x09u, true), _ => (0x0Au, false) };
                a.V2(opc, uu, size, q, V0, V2);
                Store();
                return;
            }
            case VecKind.CmpZeroF:
            {
                LdV(V2, mOff, q);
                (uint opc, bool uu) = aux switch { 0 => (0x0Cu, false), 1 => (0x0Cu, true), 2 => (0x0Du, false), 3 => (0x0Du, true), _ => (0x0Eu, false) };
                a.V2(opc, uu, 2, q, V0, V2);
                Store();
                return;
            }
            case VecKind.Swp:
                LdV(V0, dOff, q); LdV(V2, mOff, q);
                StV(mOff, V0, q); StV(dOff, V2, q);
                return;
            case VecKind.Trn: case VecKind.Uzp: case VecKind.Zip:
            {
                // new d = op1(d, m), new m = op2(d, m); stored m first, then d (like the handlers)
                uint op1 = kind == VecKind.Uzp ? 1u : kind == VecKind.Trn ? 2u : 3u;
                LdV(V0, dOff, q); LdV(V2, mOff, q);
                a.VPerm(op1, size, q, V3, V0, V2);
                a.VPerm(op1 + 4, size, q, V4, V0, V2);
                StV(mOff, V4, q);
                StV(dOff, V3, q);
                return;
            }
            case VecKind.Movn: LdV(V2, mOff, true); a.V2(0x12, false, size, false, V0, V2); StV(dOff, V0, false); return;
            case VecKind.Cnt: LdV(V2, mOff, q); a.V2(0x05, false, 0, q, V0, V2); Store(); return;
            case VecKind.Paddl:
                LdV(V2, mOff, q);
                if (acc) LdV(V0, dOff, q);
                a.V2(acc ? 0x06u : 0x02u, u, size, q, V0, V2);       // SADDLP / UADDLP / SADALP / UADALP
                Store();
                return;
            case VecKind.Clz: LdV(V2, mOff, q); a.V2(0x04, true, size, q, V0, V2); Store(); return;
            case VecKind.Cls: LdV(V2, mOff, q); a.V2(0x04, false, size, q, V0, V2); Store(); return;
            case VecKind.Qmovn: // SQXTN / UQXTN / SQXTUN: Q source, D result (size = narrow lanes)
                LdV(V2, mOff, true);
                QcBegin();
                if (aux == 1) a.V2(0x12, true, size, false, V0, V2); else a.V2(0x14, u, size, false, V0, V2);
                StV(dOff, V0, false);
                QcEnd();
                return;
            case VecKind.MullI:
            {
                // SMULL/UMULL/PMULL (opcode 1100/1110), SMLAL/UMLAL (1000), SMLSL/UMLSL (1010): D sources, Q result
                LdV(V1, nOff, false); LdV(V2, mOff, false);
                if (acc) LdV(V0, dOff, true);
                uint opc = (aux & 1) != 0 ? 0xEu : !acc ? 0xCu : (aux & 2) != 0 ? 0xAu : 0x8u;
                a.V3Diff(opc, u && (aux & 1) == 0, size, false, V0, V1, V2);
                StV(dOff, V0, true);
                return;
            }
            case VecKind.RecpeF: LdV(V2, mOff, q); a.V2(0x1D, false, 2, q, V0, V2); Store(); return;
            case VecKind.RsqrteF: LdV(V2, mOff, q); a.V2(0x1D, true, 2, q, V0, V2); Store(); return;
            case VecKind.RecpeU: LdV(V2, mOff, q); a.V2(0x1C, false, 2, q, V0, V2); Store(); return;
            case VecKind.RsqrteU: LdV(V2, mOff, q); a.V2(0x1C, true, 2, q, V0, V2); Store(); return;
            case VecKind.CvtS2F: LdV(V2, mOff, q); a.V2(0x1D, false, 0, q, V0, V2); Store(); return;
            case VecKind.CvtU2F: LdV(V2, mOff, q); a.V2(0x1D, true, 0, q, V0, V2); Store(); return;
            case VecKind.CvtF2S: LdV(V2, mOff, q); a.V2(0x1B, false, 2, q, V0, V2); Store(); return;
            case VecKind.CvtF2U: LdV(V2, mOff, q); a.V2(0x1B, true, 2, q, V0, V2); Store(); return;
            case VecKind.CvtF2H: LdV(V2, mOff, true); a.V2(0x16, false, 0, false, V0, V2); StV(dOff, V0, false); return;  // FCVTN 4S -> 4H
            case VecKind.CvtH2F: LdV(V2, mOff, false); a.V2(0x17, false, 0, false, V0, V2); StV(dOff, V0, true); return;  // FCVTL 4H -> 4S
            case VecKind.Ext:
                Load3(false);
                a.Ext(q, V0, V1, V2, aux);
                Store();
                return;
            case VecKind.Tbl:
            {
                // table: D[n..n+len-1] (register numbers wrap like the handlers) in v16/v17; indices >= 8 * len are
                // forced to 0xFF so the 16-byte table registers cannot read past the guest table
                int len = aux & 0xF;
                bool tbx = (aux & 0x10) != 0;
                int n = nOff / 8;
                for (int r = 0; r < len; r++)
                {
                    int reg = 16 + r / 2;
                    if ((r & 1) == 0) LdV(reg, ((n + r) & 31) * 8, false);
                    else { LdV(V5, ((n + r) & 31) * 8, false); a.InsD1(reg, V5); }
                }
                LdV(V2, mOff, false);
                a.MoviB(V7, (uint)(8 * len), false);
                a.V3(0x07, true, 0, false, V6, V2, V7);              // CMHS: index >= 8 * len
                a.V3(3, false, 2, false, V2, V2, V6);                // ORR
                if (tbx) LdV(V0, dOff, false);
                a.Tbl(false, V0, 16, (len + 1) / 2, V2, tbx);
                StV(dOff, V0, false);
                return;
            }
            default: throw new NotSupportedException($"vector kind {kind}");
        }
    }
}
