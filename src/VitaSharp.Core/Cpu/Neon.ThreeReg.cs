using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using static VitaSharp.Core.Cpu.Arm;

namespace VitaSharp.Core.Cpu;

internal static unsafe partial class Neon
{
    /// <summary>Three registers of the same length: 1111 001U 0 D size Vn Vd A N Q M B Vm</summary>
    public static void ThreeSame(CpuState* s, uint ins, uint pc)
    {
        if (ThreeSameFast(s, ins)) return;
        uint a = Bits(ins, 11, 8);
        bool b = Bit(ins, 4) != 0, u = Bit(ins, 24) != 0, q = Bit(ins, 6) != 0;
        uint size = Bits(ins, 21, 20);
        uint d = Rd(ins), n = Rn(ins), m = Rm(ins);
        int bytes = q ? 16 : 8;

        // bitwise ops and float fast paths operate on whole vectors
        if (a == 1 && b)
        {
            ulong* vd = (ulong*)Reg(s, d), vn = (ulong*)Reg(s, n), vm = (ulong*)Reg(s, m);
            int words = q ? 2 : 1;
            for (int i = 0; i < words; i++)
            {
                ulong x = vn[i], y = vm[i], z = vd[i];
                vd[i] = ((u ? 4u : 0u) + size) switch
                {
                    0 => x & y,          // VAND
                    1 => x & ~y,         // VBIC
                    2 => x | y,          // VORR / VMOV
                    3 => x | ~y,         // VORN
                    4 => x ^ y,          // VEOR
                    5 => (x & z) | (y & ~z),   // VBSL
                    6 => (x & y) | (z & ~y),   // VBIT
                    _ => (z & y) | (x & ~y),   // VBIF
                };
            }
            return;
        }

        byte* tn = stackalloc byte[16];
        byte* tm = stackalloc byte[16];
        byte* td = stackalloc byte[16];
        Buffer.MemoryCopy(Reg(s, n), tn, 16, bytes);
        Buffer.MemoryCopy(Reg(s, m), tm, 16, bytes);
        Buffer.MemoryCopy(Reg(s, d), td, 16, bytes);

        if (a >= 0xC) { ThreeSameFloat(s, a, b, u, size, tn, tm, td, bytes); Buffer.MemoryCopy(td, Reg(s, d), bytes, bytes); return; }

        int esize = 8 << (int)size;
        int count = bytes * 8 / esize;
        ulong mask = Mask(esize);

        switch (a)
        {
            case 0x0: // VHADD / VQADD
                for (int i = 0; i < count; i++)
                {
                    Int128 x = GetI(tn, i, esize, u), y = GetI(tm, i, esize, u);
                    ulong r = b ? (u ? SatU(s, x + y, esize) : (ulong)SatS(s, x + y, esize)) : (ulong)(long)((x + y) >> 1);
                    Set(td, i, esize, r);
                }
                break;
            case 0x1: // VRHADD
                for (int i = 0; i < count; i++)
                {
                    Int128 x = GetI(tn, i, esize, u), y = GetI(tm, i, esize, u);
                    Set(td, i, esize, (ulong)(long)((x + y + 1) >> 1));
                }
                break;
            case 0x2: // VHSUB / VQSUB
                for (int i = 0; i < count; i++)
                {
                    Int128 x = GetI(tn, i, esize, u), y = GetI(tm, i, esize, u);
                    ulong r = b ? (u ? SatU(s, x - y, esize) : (ulong)SatS(s, x - y, esize)) : (ulong)(long)((x - y) >> 1);
                    Set(td, i, esize, r);
                }
                break;
            case 0x3: // VCGT / VCGE
                for (int i = 0; i < count; i++)
                {
                    Int128 x = GetI(tn, i, esize, u), y = GetI(tm, i, esize, u);
                    bool t = b ? x >= y : x > y;
                    Set(td, i, esize, t ? mask : 0);
                }
                break;
            case 0x4: // VSHL / VQSHL (register)
            case 0x5: // VRSHL / VQRSHL
                for (int i = 0; i < count; i++)
                {
                    Int128 x = GetI(tm, i, esize, u);
                    long sh = (long)GetU(tn, i, esize);
                    Set(td, i, esize, ShiftElem(s, x, sh, u, esize, a == 5, b, u));
                }
                break;
            case 0x6: // VMAX / VMIN
                for (int i = 0; i < count; i++)
                {
                    Int128 x = GetI(tn, i, esize, u), y = GetI(tm, i, esize, u);
                    Set(td, i, esize, (ulong)(long)(b ? (x < y ? x : y) : (x > y ? x : y)));
                }
                break;
            case 0x7: // VABD / VABA
                for (int i = 0; i < count; i++)
                {
                    Int128 x = GetI(tn, i, esize, u), y = GetI(tm, i, esize, u);
                    ulong diff = (ulong)(long)Int128.Abs(x - y);
                    Set(td, i, esize, b ? GetU(td, i, esize) + diff : diff);
                }
                break;
            case 0x8:
                for (int i = 0; i < count; i++)
                {
                    ulong x = GetU(tn, i, esize), y = GetU(tm, i, esize);
                    ulong r = b ? (u ? (x == y ? mask : 0) : ((x & y) != 0 ? mask : 0)) : (u ? x - y : x + y);
                    Set(td, i, esize, r);
                }
                break;
            case 0x9:
                for (int i = 0; i < count; i++)
                {
                    ulong x = GetU(tn, i, esize), y = GetU(tm, i, esize);
                    if (!b) Set(td, i, esize, u ? GetU(td, i, esize) - x * y : GetU(td, i, esize) + x * y); // VMLA/VMLS
                    else Set(td, i, esize, u ? PolyMul8((byte)x, (byte)y) : x * y);                          // VMUL (int / poly)
                }
                break;
            case 0xA: // VPMAX / VPMIN
            {
                int half = count / 2;
                byte* res = stackalloc byte[16];
                for (int i = 0; i < half; i++)
                {
                    Int128 x0 = GetI(tn, 2 * i, esize, u), x1 = GetI(tn, 2 * i + 1, esize, u);
                    Int128 y0 = GetI(tm, 2 * i, esize, u), y1 = GetI(tm, 2 * i + 1, esize, u);
                    Set(res, i, esize, (ulong)(long)(b ? Int128.Min(x0, x1) : Int128.Max(x0, x1)));
                    Set(res, i + half, esize, (ulong)(long)(b ? Int128.Min(y0, y1) : Int128.Max(y0, y1)));
                }
                Buffer.MemoryCopy(res, td, 16, bytes);
                break;
            }
            case 0xB:
                if (!b) // VQDMULH / VQRDMULH
                {
                    for (int i = 0; i < count; i++)
                    {
                        Int128 x = GetS(tn, i, esize), y = GetS(tm, i, esize);
                        Int128 p = 2 * x * y + (u ? (Int128.One << (esize - 1)) : 0);
                        Set(td, i, esize, (ulong)SatS(s, p >> esize, esize));
                    }
                }
                else // VPADD (integer)
                {
                    int half = count / 2;
                    byte* res = stackalloc byte[16];
                    for (int i = 0; i < half; i++)
                    {
                        Set(res, i, esize, GetU(tn, 2 * i, esize) + GetU(tn, 2 * i + 1, esize));
                        Set(res, i + half, esize, GetU(tm, 2 * i, esize) + GetU(tm, 2 * i + 1, esize));
                    }
                    Buffer.MemoryCopy(res, td, 16, bytes);
                }
                break;
        }
        Buffer.MemoryCopy(td, Reg(s, d), bytes, bytes);
    }

    internal static ulong PolyMul8(byte a, byte b)
    {
        uint r = 0;
        for (int i = 0; i < 8; i++) if ((b & (1 << i)) != 0) r ^= (uint)a << i;
        return (byte)r;
    }

    static void ThreeSameFloat(CpuState* s, uint a, bool b, bool u, uint size, byte* tn, byte* tm, byte* td, int bytes)
    {
        int count = bytes / 4;
        bool sz1 = (size & 2) != 0;
        switch (a)
        {
            case 0xC: // VFMA / VFMS
                for (int i = 0; i < count; i++)
                {
                    float x = F(tn, i), y = F(tm, i);
                    SetF(td, i, MathF.FusedMultiplyAdd(sz1 ? -x : x, y, F(td, i)));
                }
                break;
            case 0xD:
                if (!b)
                {
                    if (!u)
                    {
                        for (int i = 0; i < count; i++) SetF(td, i, sz1 ? F(tn, i) - F(tm, i) : F(tn, i) + F(tm, i));
                    }
                    else if (!sz1) // VPADD.F32
                    {
                        int half = count / 2;
                        float* res = stackalloc float[4];
                        for (int i = 0; i < half; i++)
                        {
                            res[i] = FRes(F(tn, 2 * i) + F(tn, 2 * i + 1));
                            res[i + half] = FRes(F(tm, 2 * i) + F(tm, 2 * i + 1));
                        }
                        Buffer.MemoryCopy(res, td, 16, bytes);
                    }
                    else for (int i = 0; i < count; i++) SetF(td, i, MathF.Abs(F(tn, i) - F(tm, i))); // VABD.F32
                }
                else
                {
                    if (!u) // VMLA / VMLS (float)
                        for (int i = 0; i < count; i++)
                        {
                            float p = FRes(F(tn, i) * F(tm, i));
                            SetF(td, i, sz1 ? F(td, i) - p : F(td, i) + p);
                        }
                    else // VMUL.F32
                        for (int i = 0; i < count; i++) SetF(td, i, F(tn, i) * F(tm, i));
                }
                break;
            case 0xE:
                for (int i = 0; i < count; i++)
                {
                    float x = F(tn, i), y = F(tm, i);
                    bool t;
                    if (!b) t = !u ? x == y : (sz1 ? x > y : x >= y);           // VCEQ / VCGE / VCGT
                    else t = sz1 ? MathF.Abs(x) > MathF.Abs(y) : MathF.Abs(x) >= MathF.Abs(y); // VACGT / VACGE
                    ((uint*)td)[i] = t ? 0xFFFFFFFF : 0;
                }
                break;
            case 0xF:
                if (!b)
                {
                    if (!u) for (int i = 0; i < count; i++) SetF(td, i, sz1 ? FMin(F(tn, i), F(tm, i)) : FMax(F(tn, i), F(tm, i)));
                    else
                    {
                        int half = count / 2;
                        float* res = stackalloc float[4];
                        for (int i = 0; i < half; i++)
                        {
                            res[i] = sz1 ? FMin(F(tn, 2 * i), F(tn, 2 * i + 1)) : FMax(F(tn, 2 * i), F(tn, 2 * i + 1));
                            res[i + half] = sz1 ? FMin(F(tm, 2 * i), F(tm, 2 * i + 1)) : FMax(F(tm, 2 * i), F(tm, 2 * i + 1));
                        }
                        Buffer.MemoryCopy(res, td, 16, bytes);
                    }
                }
                else
                {
                    // VRECPS: 2 - a*b ; VRSQRTS: (3 - a*b) / 2
                    for (int i = 0; i < count; i++)
                    {
                        float x = F(tn, i), y = F(tm, i);
                        float r;
                        if ((float.IsInfinity(x) && y == 0) || (x == 0 && float.IsInfinity(y))) r = sz1 ? 1.5f : 2f;
                        else r = sz1 ? (3f - x * y) / 2f : 2f - x * y;
                        SetF(td, i, r);
                    }
                }
                break;
        }
    }

    // =====================================================================================
    /// <summary>Three registers of different lengths: 1111 001U 1 D size Vn Vd A N 0 M 0 Vm</summary>
    public static void ThreeDiff(CpuState* s, uint ins, uint pc)
    {
        if (ThreeDiffFast(s, ins)) return;
        uint a = Bits(ins, 11, 8);
        bool u = Bit(ins, 24) != 0;
        int esize = 8 << (int)Bits(ins, 21, 20);
        uint d = Rd(ins), n = Rn(ins), m = Rm(ins);
        int count = 64 / esize;
        byte* tn = stackalloc byte[16];
        byte* tm = stackalloc byte[16];
        byte* td = stackalloc byte[16];
        bool wideN = a == 1 || a == 3;      // VADDW / VSUBW
        bool narrow = a == 4 || a == 6;     // V(R)ADDHN / V(R)SUBHN
        Buffer.MemoryCopy(Reg(s, n), tn, 16, wideN || narrow ? 16 : 8);
        Buffer.MemoryCopy(Reg(s, m), tm, 16, narrow ? 16 : 8);
        Buffer.MemoryCopy(Reg(s, d), td, 16, narrow ? 8 : 16);
        int w = esize * 2;

        if (narrow)
        {
            for (int i = 0; i < count; i++)
            {
                ulong x = GetU(tn, i, w), y = GetU(tm, i, w);
                ulong r = a == 4 ? x + y : x - y;
                if (u) r += 1ul << (esize - 1);
                Set(td, i, esize, r >> esize);
            }
            Buffer.MemoryCopy(td, Reg(s, d), 8, 8);
            return;
        }

        for (int i = 0; i < count; i++)
        {
            Int128 x = wideN ? GetI(tn, i, w, u) : GetI(tn, i, esize, u);
            Int128 y = GetI(tm, i, esize, u);
            Int128 acc = GetI(td, i, w, u);
            ulong r;
            switch (a)
            {
                case 0: case 1: r = (ulong)(long)(x + y); break;
                case 2: case 3: r = (ulong)(long)(x - y); break;
                case 5: r = (ulong)(long)(acc + Int128.Abs(x - y)); break;            // VABAL
                case 7: r = (ulong)(long)Int128.Abs(x - y); break;                    // VABDL
                case 8: r = (ulong)(long)(acc + x * y); break;                        // VMLAL
                case 9: r = (ulong)SatS(s, acc + (Int128)SatS(s, 2 * x * y, w), w); break;   // VQDMLAL
                case 10: r = (ulong)(long)(acc - x * y); break;                       // VMLSL
                case 11: r = (ulong)SatS(s, acc - (Int128)SatS(s, 2 * x * y, w), w); break;  // VQDMLSL
                case 12: r = (ulong)(long)(x * y); break;                             // VMULL
                case 13: r = (ulong)SatS(s, 2 * x * y, w); break;                     // VQDMULL
                case 14: r = PolyMulLong(GetU(tn, i, esize), GetU(tm, i, esize), esize); break; // VMULL.P8
                default: r = 0; break;
            }
            Set(td, i, w, r);
        }
        Buffer.MemoryCopy(td, Reg(s, d), 16, 16);
    }

    static ulong PolyMulLong(ulong a, ulong b, int esize)
    {
        ulong r = 0;
        for (int i = 0; i < esize; i++) if ((b & (1ul << i)) != 0) r ^= a << i;
        return r;
    }

    // =====================================================================================
    /// <summary>Two registers and a scalar: 1111 001Q 1 D size Vn Vd A N 1 M 0 Vm</summary>
    public static void Scalar(CpuState* s, uint ins, uint pc)
    {
        uint a = Bits(ins, 11, 8);
        bool q = Bit(ins, 24) != 0;
        uint size = Bits(ins, 21, 20);
        int esize = 8 << (int)size;
        uint d = Rd(ins), n = Rn(ins);
        uint mReg, index;
        if (esize == 16) { mReg = ins & 7; index = (Bit(ins, 5) << 1) | Bit(ins, 3); }
        else { mReg = ins & 0xF; index = Bit(ins, 5); }
        bool longOp = a is 2 or 3 or 6 or 7 or 10 or 11;
        bool fp = a is 1 or 5 or 9;
        int bytesIn = longOp ? 8 : (q ? 16 : 8);
        int count = bytesIn * 8 / esize;

        byte* tn = stackalloc byte[16];
        byte* td = stackalloc byte[16];
        Buffer.MemoryCopy(Reg(s, n), tn, 16, bytesIn);
        Buffer.MemoryCopy(Reg(s, d), td, 16, 16);
        byte* sm = Reg(s, mReg);

        if (fp)
        {
            float y = F(sm, (int)index);
            for (int i = 0; i < count; i++)
            {
                float p = FRes(F(tn, i) * y);
                SetF(td, i, a == 1 ? F(td, i) + p : a == 5 ? F(td, i) - p : p);
            }
            Buffer.MemoryCopy(td, Reg(s, d), bytesIn, bytesIn);
            return;
        }

        bool u = q && longOp; // for long ops bit 24 is U
        Int128 ys = longOp ? GetI(sm, (int)index, esize, u) : GetS(sm, (int)index, esize);
        int w = esize * 2;
        for (int i = 0; i < count; i++)
        {
            switch (a)
            {
                case 0: Set(td, i, esize, GetU(td, i, esize) + GetU(tn, i, esize) * (ulong)(long)ys); break;
                case 4: Set(td, i, esize, GetU(td, i, esize) - GetU(tn, i, esize) * (ulong)(long)ys); break;
                case 8: Set(td, i, esize, GetU(tn, i, esize) * (ulong)(long)ys); break;
                case 2: Set(td, i, w, (ulong)(long)(GetI(td, i, w, u) + GetI(tn, i, esize, u) * ys)); break;
                case 6: Set(td, i, w, (ulong)(long)(GetI(td, i, w, u) - GetI(tn, i, esize, u) * ys)); break;
                case 10: Set(td, i, w, (ulong)(long)(GetI(tn, i, esize, u) * ys)); break;
                case 3: Set(td, i, w, (ulong)SatS(s, GetS(td, i, w) + (Int128)SatS(s, 2 * GetS(tn, i, esize) * ys, w), w)); break;
                case 7: Set(td, i, w, (ulong)SatS(s, GetS(td, i, w) - (Int128)SatS(s, 2 * GetS(tn, i, esize) * ys, w), w)); break;
                case 11: Set(td, i, w, (ulong)SatS(s, 2 * GetS(tn, i, esize) * ys, w)); break;
                case 12: case 13:
                {
                    Int128 p = 2 * (Int128)GetS(tn, i, esize) * ys + (a == 13 ? (Int128.One << (esize - 1)) : 0);
                    Set(td, i, esize, (ulong)SatS(s, p >> esize, esize));
                    break;
                }
            }
        }
        int outBytes = longOp ? 16 : bytesIn;
        Buffer.MemoryCopy(td, Reg(s, d), outBytes, outBytes);
    }
}
