using System.Numerics;
using static VitaSharp.Core.Cpu.Arm;

namespace VitaSharp.Core.Cpu;

internal static unsafe partial class Neon
{
    /// <summary>Two registers and a shift amount: 1111 001U 1 D imm6 Vd A L Q M 1 Vm</summary>
    public static void Shift(CpuState* s, uint ins, uint pc)
    {
        if (ShiftFast(s, ins)) return;
        uint a = Bits(ins, 11, 8);
        bool u = Bit(ins, 24) != 0, l = Bit(ins, 7) != 0, q = Bit(ins, 6) != 0;
        uint imm6 = Bits(ins, 21, 16);
        uint d = Rd(ins), m = Rm(ins);
        int esize = l ? 64 : imm6 >= 32 ? 32 : imm6 >= 16 ? 16 : 8;
        int rshift = l ? 64 - (int)imm6 : 2 * esize - (int)imm6;
        int lshift = l ? (int)imm6 : (int)imm6 - esize;

        byte* tm = stackalloc byte[16];
        byte* td = stackalloc byte[16];
        Buffer.MemoryCopy(Reg(s, m), tm, 16, 16);
        Buffer.MemoryCopy(Reg(s, d), td, 16, 16);

        if (a == 8 || a == 9) // narrowing right shifts (result esize, operand 2*esize)
        {
            int w = esize * 2;
            bool round = q;
            int count = 64 / esize;
            for (int i = 0; i < count; i++)
            {
                bool srcUnsigned = a == 9 && u;
                Int128 x = GetI(tm, i, w, srcUnsigned);
                Int128 r = (x + (round ? (Int128.One << (rshift - 1)) : 0)) >> rshift;
                ulong res;
                if (a == 8 && !u) res = (ulong)(long)r;                 // VSHRN / VRSHRN
                else if (a == 8) res = SatU(s, r, esize);                // VQSHRUN / VQRSHRUN
                else res = u ? SatU(s, r, esize) : (ulong)SatS(s, r, esize); // VQSHRN / VQRSHRN
                Set(td, i, esize, res);
            }
            Buffer.MemoryCopy(td, Reg(s, d), 8, 8);
            return;
        }

        if (a == 10) // VSHLL / VMOVL
        {
            int count = 64 / esize;
            int w = esize * 2;
            byte* res = stackalloc byte[16];
            for (int i = 0; i < count; i++)
                Set(res, i, w, (ulong)(long)(GetI(tm, i, esize, u) << lshift));
            Buffer.MemoryCopy(res, Reg(s, d), 16, 16);
            return;
        }

        int bytes = q ? 16 : 8;
        if (a >= 14) // VCVT fixed <-> float (32-bit only)
        {
            int frac = 64 - (int)imm6;
            bool toFixed = (a & 1) != 0;
            for (int i = 0; i < bytes / 4; i++)
            {
                if (toFixed)
                {
                    double v = Math.Truncate((double)Ftz(((float*)tm)[i]) * Math.Pow(2, frac));
                    ((uint*)td)[i] = u ? Vfp.ToU32(v) : (uint)Vfp.ToS32(v);
                }
                else
                {
                    uint raw = ((uint*)tm)[i];
                    double v = u ? raw : (int)raw;
                    ((float*)td)[i] = (float)(v / Math.Pow(2, frac));
                }
            }
            Buffer.MemoryCopy(td, Reg(s, d), bytes, bytes);
            return;
        }

        int n = bytes * 8 / esize;
        ulong mask = Mask(esize);
        for (int i = 0; i < n; i++)
        {
            switch (a)
            {
                case 0: case 1: case 2: case 3: // VSHR / VSRA / VRSHR / VRSRA
                {
                    Int128 x = GetI(tm, i, esize, u);
                    bool round = (a & 2) != 0;
                    Int128 r = (x + (round ? (Int128.One << (rshift - 1)) : 0)) >> rshift;
                    ulong res = (ulong)(long)r;
                    if ((a & 1) != 0) res += GetU(td, i, esize);
                    Set(td, i, esize, res);
                    break;
                }
                case 4: // VSRI
                {
                    ulong ins_ = rshift >= 64 ? 0 : GetU(tm, i, esize) >> rshift;
                    ulong keep = rshift >= 64 ? mask : ~(mask >> rshift) & mask;
                    Set(td, i, esize, (GetU(td, i, esize) & keep) | ins_);
                    break;
                }
                case 5: // VSHL / VSLI
                {
                    ulong sh = GetU(tm, i, esize) << lshift;
                    if (u)
                    {
                        ulong keep = ~(mask << lshift) & mask;
                        Set(td, i, esize, (GetU(td, i, esize) & keep) | (sh & mask));
                    }
                    else Set(td, i, esize, sh);
                    break;
                }
                case 6: // VQSHLU (signed in, unsigned saturate)
                    Set(td, i, esize, SatU(s, GetI(tm, i, esize, false) << lshift, esize));
                    break;
                case 7: // VQSHL (imm)
                {
                    Int128 r = GetI(tm, i, esize, u) << lshift;
                    Set(td, i, esize, u ? SatU(s, r, esize) : (ulong)SatS(s, r, esize));
                    break;
                }
            }
        }
        Buffer.MemoryCopy(td, Reg(s, d), bytes, bytes);
    }

    // =====================================================================================
    /// <summary>Two registers miscellaneous: 1111 0011 1 D 11 size A Vd 0 B(10:6) M 0 Vm</summary>
    public static void Misc(CpuState* s, uint ins, uint pc)
    {
        uint a = Bits(ins, 17, 16), b = Bits(ins, 10, 6);
        bool q = Bit(ins, 6) != 0;
        uint size = Bits(ins, 19, 18);
        int esize = 8 << (int)size;
        uint d = Rd(ins), m = Rm(ins);
        int bytes = q ? 16 : 8;
        int count = bytes * 8 / esize;
        ulong mask = Mask(esize);

        byte* tm = stackalloc byte[16];
        byte* td = stackalloc byte[16];
        Buffer.MemoryCopy(Reg(s, m), tm, 16, 16);
        Buffer.MemoryCopy(Reg(s, d), td, 16, 16);
        uint op = b >> 1;

        switch (a)
        {
            case 0:
                switch (op)
                {
                    case 0: case 1: case 2: // VREV64 / VREV32 / VREV16
                    {
                        int group = op == 0 ? 64 : op == 1 ? 32 : 16;
                        int per = group / esize;
                        for (int i = 0; i < count; i++)
                        {
                            int baseIdx = i - i % per;
                            Set(td, i, esize, GetU(tm, baseIdx + per - 1 - i % per, esize));
                        }
                        break;
                    }
                    case 4: case 5: case 0xC: case 0xD: // VPADDL / VPADAL
                    {
                        bool uns = (op & 1) != 0;
                        bool acc = op >= 0xC;
                        int w = esize * 2;
                        for (int i = 0; i < count / 2; i++)
                        {
                            Int128 r = GetI(tm, 2 * i, esize, uns) + GetI(tm, 2 * i + 1, esize, uns);
                            if (acc) r += GetI(td, i, w, uns);
                            Set(td, i, w, (ulong)(long)r);
                        }
                        break;
                    }
                    case 8: // VCLS
                        for (int i = 0; i < count; i++)
                        {
                            long v = GetS(tm, i, esize);
                            ulong x = (ulong)(v ^ (v >> 63)) & mask;
                            int lz = esize - (64 - BitOperations.LeadingZeroCount(x));
                            Set(td, i, esize, (ulong)(lz - 1));
                        }
                        break;
                    case 9: // VCLZ
                        for (int i = 0; i < count; i++)
                        {
                            ulong x = GetU(tm, i, esize);
                            Set(td, i, esize, (ulong)(esize - (64 - BitOperations.LeadingZeroCount(x))));
                        }
                        break;
                    case 0xA: // VCNT
                        for (int i = 0; i < bytes; i++) td[i] = (byte)BitOperations.PopCount(tm[i]);
                        break;
                    case 0xB: // VMVN
                        for (int i = 0; i < bytes; i++) td[i] = (byte)~tm[i];
                        break;
                    case 0xE: // VQABS
                        for (int i = 0; i < count; i++) Set(td, i, esize, (ulong)SatS(s, Int128.Abs(GetS(tm, i, esize)), esize));
                        break;
                    case 0xF: // VQNEG
                        for (int i = 0; i < count; i++) Set(td, i, esize, (ulong)SatS(s, -(Int128)GetS(tm, i, esize), esize));
                        break;
                    default: Arm.Undefined(s, pc); return;
                }
                break;
            case 1:
            {
                bool fp = (op & 8) != 0;
                uint kind = op & 7;
                for (int i = 0; i < count; i++)
                {
                    if (fp)
                    {
                        float x = kind >= 6 ? ((float*)tm)[i] : F(tm, i); // VABS/VNEG are bitwise (no flush)
                        uint r = kind switch
                        {
                            0 => x > 0 ? 0xFFFFFFFF : 0,
                            1 => x >= 0 ? 0xFFFFFFFF : 0,
                            2 => x == 0 ? 0xFFFFFFFF : 0,
                            3 => x <= 0 ? 0xFFFFFFFF : 0,
                            4 => x < 0 ? 0xFFFFFFFF : 0,
                            6 => BitConverter.SingleToUInt32Bits(x) & 0x7FFFFFFF,
                            _ => BitConverter.SingleToUInt32Bits(x) ^ 0x80000000,
                        };
                        ((uint*)td)[i] = r;
                    }
                    else
                    {
                        long x = GetS(tm, i, esize);
                        ulong r = kind switch
                        {
                            0 => x > 0 ? mask : 0,
                            1 => x >= 0 ? mask : 0,
                            2 => x == 0 ? mask : 0,
                            3 => x <= 0 ? mask : 0,
                            4 => x < 0 ? mask : 0,
                            6 => (ulong)Math.Abs(x),
                            _ => (ulong)(-x),
                        };
                        Set(td, i, esize, r);
                    }
                }
                break;
            }
            case 2:
                switch (op)
                {
                    case 0: // VSWP
                        Buffer.MemoryCopy(Reg(s, d), Reg(s, m), bytes, bytes);
                        Buffer.MemoryCopy(td, Reg(s, m), bytes, bytes);
                        Buffer.MemoryCopy(tm, Reg(s, d), bytes, bytes);
                        return;
                    case 1: // VTRN
                        for (int i = 0; i < count; i += 2)
                        {
                            ulong d1 = GetU(td, i + 1, esize), m0 = GetU(tm, i, esize);
                            Set(td, i + 1, esize, m0);
                            Set(tm, i, esize, d1);
                        }
                        Buffer.MemoryCopy(tm, Reg(s, m), bytes, bytes);
                        break;
                    case 2: // VUZP
                    {
                        byte* all = stackalloc byte[32];
                        Buffer.MemoryCopy(td, all, 32, bytes);
                        Buffer.MemoryCopy(tm, all + bytes, 32, bytes);
                        for (int i = 0; i < count; i++)
                        {
                            Set(td, i, esize, GetU(all, 2 * i, esize));
                            Set(tm, i, esize, GetU(all, 2 * i + 1, esize));
                        }
                        Buffer.MemoryCopy(tm, Reg(s, m), bytes, bytes);
                        break;
                    }
                    case 3: // VZIP
                    {
                        byte* all = stackalloc byte[32];
                        for (int i = 0; i < count; i++)
                        {
                            Set(all, 2 * i, esize, GetU(td, i, esize));
                            Set(all, 2 * i + 1, esize, GetU(tm, i, esize));
                        }
                        Buffer.MemoryCopy(all, td, 16, bytes);
                        Buffer.MemoryCopy(all + bytes, Reg(s, m), bytes, bytes);
                        break;
                    }
                    case 4: // VMOVN (b=01000) / VQMOVUN (b=01001)
                    case 5: // VQMOVN signed (01010) / unsigned (01011)
                    {
                        int w = esize * 2;
                        byte* res = stackalloc byte[8];
                        for (int i = 0; i < 64 / esize; i++)
                        {
                            ulong r;
                            if (b == 8) r = GetU(tm, i, w);
                            else if (b == 9) r = SatU(s, GetS(tm, i, w), esize);
                            else if (b == 10) r = (ulong)SatS(s, GetS(tm, i, w), esize);
                            else r = SatU(s, GetU(tm, i, w), esize);
                            Set(res, i, esize, r);
                        }
                        Buffer.MemoryCopy(res, Reg(s, d), 8, 8);
                        return;
                    }
                    case 6: // VSHLL (shift = esize)
                    {
                        int w = esize * 2;
                        byte* res = stackalloc byte[16];
                        for (int i = 0; i < 64 / esize; i++) Set(res, i, w, GetU(tm, i, esize) << esize);
                        Buffer.MemoryCopy(res, Reg(s, d), 16, 16);
                        return;
                    }
                    case 0xC: // VCVT.F16.F32 (b=11000)
                    {
                        ushort* res = stackalloc ushort[4];
                        // Advanced SIMD runs with the standard FPSCR (DN=1): NaNs become the default NaN
                        for (int i = 0; i < 4; i++) { float x = ((float*)tm)[i]; res[i] = float.IsNaN(x) ? (ushort)0x7E00 : BitConverter.HalfToUInt16Bits((Half)x); }
                        Buffer.MemoryCopy(res, Reg(s, d), 8, 8);
                        return;
                    }
                    case 0xE: // VCVT.F32.F16 (b=11100)
                    {
                        float* res = stackalloc float[4];
                        for (int i = 0; i < 4; i++) { Half h = BitConverter.UInt16BitsToHalf(((ushort*)tm)[i]); res[i] = Half.IsNaN(h) ? BitConverter.UInt32BitsToSingle(0x7FC00000) : (float)h; }
                        Buffer.MemoryCopy(res, Reg(s, d), 16, 16);
                        return;
                    }
                    default: Arm.Undefined(s, pc); return;
                }
                break;
            case 3:
            {
                for (int i = 0; i < bytes / 4; i++)
                {
                    uint raw = ((uint*)tm)[i];
                    float x = Ftz(BitConverter.UInt32BitsToSingle(raw));
                    uint r;
                    if ((op & 0xC) == 0x8) // VRECPE / VRSQRTE
                    {
                        bool fp = (op & 2) != 0, rsqrt = (op & 1) != 0;
                        if (fp) r = BitConverter.SingleToUInt32Bits(rsqrt ? RSqrtEstimate(x) : RecipEstimate(x));
                        else r = rsqrt ? URSqrtEstimate(raw) : URecipEstimate(raw);
                    }
                    else // VCVT: op bits 1:0 -> 00 s32->f32, 01 u32->f32, 10 f32->s32, 11 f32->u32
                    {
                        r = (op & 3) switch
                        {
                            0 => BitConverter.SingleToUInt32Bits((float)(int)raw),
                            1 => BitConverter.SingleToUInt32Bits((float)raw),
                            2 => (uint)Vfp.ToS32(Math.Truncate(x)),
                            _ => Vfp.ToU32(Math.Truncate(x)),
                        };
                    }
                    ((uint*)td)[i] = r;
                }
                break;
            }
        }
        Buffer.MemoryCopy(td, Reg(s, d), bytes, bytes);
    }

    // =====================================================================================
    /// <summary>One register and a modified immediate: 1111 001i 1 D 000 imm3 Vd cmode 0 Q op 1 imm4</summary>
    public static void Imm(CpuState* s, uint ins, uint pc)
    {
        uint imm8 = (Bit(ins, 24) << 7) | (Bits(ins, 18, 16) << 4) | (ins & 0xF);
        uint cmode = Bits(ins, 11, 8);
        bool op = Bit(ins, 5) != 0, q = Bit(ins, 6) != 0;
        uint d = Rd(ins);
        ulong imm = ExpandImm(op, cmode, imm8);
        ulong* vd = (ulong*)Reg(s, d);
        int words = q ? 2 : 1;
        bool orrBic = (cmode & 1) != 0 && cmode < 12;
        for (int i = 0; i < words; i++)
        {
            if (orrBic) vd[i] = op ? vd[i] & ~imm : vd[i] | imm;
            else if (op && cmode != 14) vd[i] = ~imm;
            else vd[i] = imm;
        }
    }

    internal static ulong ExpandImm(bool op, uint cmode, uint imm8)
    {
        ulong r32(uint v) => v | ((ulong)v << 32);
        ulong r16(uint v) => (v & 0xFFFF) * 0x0001000100010001ul;
        switch (cmode >> 1)
        {
            case 0: return r32(imm8);
            case 1: return r32(imm8 << 8);
            case 2: return r32(imm8 << 16);
            case 3: return r32(imm8 << 24);
            case 4: return r16(imm8);
            case 5: return r16(imm8 << 8);
            case 6: return (cmode & 1) == 0 ? r32((imm8 << 8) | 0xFF) : r32((imm8 << 16) | 0xFFFF);
            default:
                if ((cmode & 1) == 0)
                {
                    if (!op) return imm8 * 0x0101010101010101ul;
                    ulong r = 0;
                    for (int i = 0; i < 8; i++) if ((imm8 & (1u << i)) != 0) r |= 0xFFul << (i * 8);
                    return r;
                }
                uint f = ((imm8 & 0x80) << 24) | ((imm8 & 0x40) != 0 ? 0x3E000000u : 0x40000000u) | ((imm8 & 0x3F) << 19);
                return r32(f);
        }
    }

    // =====================================================================================
    /// <summary>VEXT: 1111 0010 1 D 11 Vn Vd imm4 N Q M 0 Vm</summary>
    public static void Vext(CpuState* s, uint ins, uint pc)
    {
        bool q = Bit(ins, 6) != 0;
        int bytes = q ? 16 : 8;
        int imm = (int)Bits(ins, 11, 8);
        byte* all = stackalloc byte[32];
        Buffer.MemoryCopy(Reg(s, Rn(ins)), all, 32, bytes);
        Buffer.MemoryCopy(Reg(s, Rm(ins)), all + bytes, 32, bytes);
        Buffer.MemoryCopy(all + imm, Reg(s, Rd(ins)), bytes, bytes);
    }

    /// <summary>VTBL/VTBX: 1111 0011 1 D 11 Vn Vd 10 len N op M 0 Vm</summary>
    public static void Vtbl(CpuState* s, uint ins, uint pc)
    {
        int len = (int)Bits(ins, 9, 8) + 1;
        bool tbx = Bit(ins, 6) != 0;
        uint n = Rn(ins);
        byte* table = stackalloc byte[32];
        for (int i = 0; i < len; i++) *(ulong*)(table + 8 * i) = s->D[(n + (uint)i) & 31];
        byte* idx = Reg(s, Rm(ins));
        byte* dst = Reg(s, Rd(ins));
        byte* res = stackalloc byte[8];
        for (int i = 0; i < 8; i++)
        {
            int j = idx[i];
            res[i] = j < 8 * len ? table[j] : (tbx ? dst[i] : (byte)0);
        }
        *(ulong*)dst = *(ulong*)res;
    }

    /// <summary>VDUP (scalar): 1111 0011 1 D 11 imm4 Vd 11000 Q M 0 Vm</summary>
    public static void VdupScalar(CpuState* s, uint ins, uint pc)
    {
        uint imm4 = Bits(ins, 19, 16);
        bool q = Bit(ins, 6) != 0;
        byte* src = Reg(s, Rm(ins));
        ulong rep;
        if ((imm4 & 1) != 0) rep = src[imm4 >> 1] * 0x0101010101010101ul;
        else if ((imm4 & 2) != 0) rep = ((ushort*)src)[imm4 >> 2] * 0x0001000100010001ul;
        else { uint v = ((uint*)src)[imm4 >> 3]; rep = v | ((ulong)v << 32); }
        uint d = Rd(ins);
        s->D[d] = rep;
        if (q) s->D[(d + 1) & 31] = rep;
    }
}
