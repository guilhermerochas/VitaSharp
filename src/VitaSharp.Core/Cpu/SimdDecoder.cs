namespace VitaSharp.Core.Cpu;

/// <summary>Decoder for VFP (coprocessors 10/11) and Advanced SIMD encodings.</summary>
public static class SimdDecoder
{
    static uint B(uint v, int n) => (v >> n) & 1;
    static uint Bs(uint v, int hi, int lo) => (v >> lo) & (uint)((1L << (hi - lo + 1)) - 1);

    public static Op DecodeVfp(uint ins, out bool writesPc, bool thumb = false)
    {
        writesPc = false;
        if (Bs(ins, 27, 25) == 6)
        {
            if ((ins & 0x0FE0_0000) == 0x0C40_0000) return Op.Vfp_Mov64;
            if (B(ins, 24) == 1 && B(ins, 21) == 0) return thumb ? Op.Vfp_LdrStrThumb : Op.Vfp_LdrStrArm;
            if ((ins & 0x0FE0_0000) == 0x0C00_0000) { writesPc = true; return Op.A32_Udf; }
            return Op.Vfp_LdmStm;
        }
        if (Bs(ins, 27, 24) != 0xE) { writesPc = true; return Op.A32_Udf; }
        if (B(ins, 4) == 0)
        {
            uint opc1 = (B(ins, 23) << 2) | Bs(ins, 21, 20);
            if (opc1 == 7) return B(ins, 6) == 0 ? Op.Vfp_MovImm : Op.Vfp_Other;
            return Op.Vfp_Arith;
        }
        uint c = B(ins, 8), a = Bs(ins, 23, 21), l = B(ins, 20);
        if (c == 0)
        {
            if (a == 0) return Op.Vfp_MovCoreSingle;
            if (a == 7) return l != 0 ? Op.Vfp_Vmrs : Op.Vfp_Vmsr;
        }
        else
        {
            if (l != 0) return Op.Vfp_MovScalarCore;
            return (a & 4) == 0 ? Op.Vfp_MovCoreScalar : Op.Vfp_VdupCore;
        }
        writesPc = true;
        return Op.A32_Udf;
    }

    /// <summary>ARM-encoded Advanced SIMD data processing (1111 001U ...).</summary>
    public static Op DecodeNeonData(uint ins, bool thumb)
    {
        uint a = Bs(ins, 23, 19), b = Bs(ins, 11, 8), c = Bs(ins, 7, 4);
        bool u = B(ins, 24) != 0;
        if ((a & 0x10) == 0) return Op.Neon_ThreeSame;
        if ((c & 1) == 1)
        {
            if ((a & 0x17) == 0x10 && (c & 8) == 0) return Op.Neon_Imm;
            return Op.Neon_Shift;
        }
        // c<0> == 0
        if ((a & 0x16) == 0x16)
        {
            if (!u) return Op.Neon_Vext;
            if ((b & 8) == 0) return Op.Neon_Misc;
            if ((b & 0xC) == 8) return Op.Neon_Vtbl;
            if (b == 0xC && (c & 8) == 0) return Op.Neon_VdupScalar;
            return Op.A32_Udf;
        }
        if ((c & 4) == 0) return Op.Neon_ThreeDiff;
        return Op.Neon_Scalar;
    }

    public static Op DecodeNeonLoadStore(uint ins) => Op.Neon_LoadStore;

    /// <summary>Thumb coprocessor / Advanced SIMD space. Rewrites NEON data processing into ARM form.</summary>
    public static Op DecodeThumbCoproc(ref uint ins, out bool writesPc)
    {
        writesPc = false;
        uint hw1 = ins >> 16;
        if ((hw1 & 0xEF00) == 0xEF00)
        {
            ins = 0xF200_0000 | (B(hw1, 12) << 24) | (ins & 0x00FF_FFFF);
            return DecodeNeonData(ins, true);
        }
        uint coproc = Bs(ins, 11, 8);
        if ((hw1 >> 12) == 0xE)
        {
            if ((coproc & 0xE) == 0xA) return DecodeVfp(ins, out writesPc, thumb: true);
            if (coproc == 15 && Bs(ins, 27, 24) == 0xE && B(ins, 4) != 0)
                return B(ins, 20) != 0 ? Op.T32_Mrc : Op.T32_Mcr;
        }
        writesPc = true;
        return Op.T32_Udf;
    }
}
