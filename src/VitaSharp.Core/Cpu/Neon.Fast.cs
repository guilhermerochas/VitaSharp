using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using static VitaSharp.Core.Cpu.Arm;

namespace VitaSharp.Core.Cpu;

/// <summary>
/// Vector fast paths for the NEON instructions that dominate real workloads (video decoders, audio mixing, physics):
/// whole-register operations with <see cref="Vector128{T}"/> instead of per-element Int128 arithmetic. Each
/// handler returns false for the encodings it does not cover, and the generic (reference) implementation runs.
/// Results, including the QC sticky flag, must match the reference bit for bit (the CPU fuzz tests check that).
/// </summary>
internal static unsafe partial class Neon
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static Vector128<byte> LoadD(CpuState* s, uint r) => Vector128.Create(*(ulong*)Reg(s, r), 0UL).AsByte();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static Vector128<byte> LoadQ(CpuState* s, uint r) => Vector128.Load(Reg(s, r));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static Vector128<byte> LoadV(CpuState* s, uint r, bool q) => q ? LoadQ(s, r) : LoadD(s, r);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void StoreV(CpuState* s, uint r, Vector128<byte> v, bool q)
    {
        if (q) v.Store(Reg(s, r));
        else *(ulong*)Reg(s, r) = v.AsUInt64().GetElement(0);
    }

    // ------------------------------------------------------------------ three registers, same length

    static bool ThreeSameFast(CpuState* s, uint ins)
    {
        uint a = Bits(ins, 11, 8);
        bool b = Bit(ins, 4) != 0, u = Bit(ins, 24) != 0, q = Bit(ins, 6) != 0;
        uint size = Bits(ins, 21, 20);
        if (a >= 0xC) return false;                   // floating point: reference path
        if (size == 3 && !(a == 8 && !b)) return false; // only VADD/VSUB exist for 64-bit lanes
        uint d = Rd(ins), n = Rn(ins), m = Rm(ins);
        if (q && ((d | n | m) & 1) != 0) return false;
        var x = LoadV(s, n, q);
        var y = LoadV(s, m, q);
        Vector128<byte> r;
        switch (a)
        {
            case 0x0 when !b: r = HalvingAdd(x, y, size, u, false); break;          // VHADD
            case 0x1 when !b: r = HalvingAdd(x, y, size, u, true); break;           // VRHADD
            case 0x2 when !b: r = HalvingSub(x, y, size, u); break;                 // VHSUB
            case 0x0 when b:                                                        // VQADD
            case 0x2 when b:                                                        // VQSUB
            {
                if (!Sse2.IsSupported || size > 1) return false;
                bool sub = a == 2;
                Vector128<byte> plain, sat;
                if (size == 0)
                {
                    plain = sub ? x - y : x + y;
                    sat = u ? (sub ? Sse2.SubtractSaturate(x, y) : Sse2.AddSaturate(x, y))
                            : (sub ? Sse2.SubtractSaturate(x.AsSByte(), y.AsSByte()) : Sse2.AddSaturate(x.AsSByte(), y.AsSByte())).AsByte();
                }
                else
                {
                    plain = (sub ? x.AsUInt16() - y.AsUInt16() : x.AsUInt16() + y.AsUInt16()).AsByte();
                    sat = u ? (sub ? Sse2.SubtractSaturate(x.AsUInt16(), y.AsUInt16()) : Sse2.AddSaturate(x.AsUInt16(), y.AsUInt16())).AsByte()
                            : (sub ? Sse2.SubtractSaturate(x.AsInt16(), y.AsInt16()) : Sse2.AddSaturate(x.AsInt16(), y.AsInt16())).AsByte();
                }
                if (!LanesEqual(plain, sat, q)) SetQC(s);
                r = sat;
                break;
            }
            case 0x3: r = Compare(x, y, size, u, b); break;                         // VCGT / VCGE
            case 0x6: r = MaxMin(x, y, size, u, b); break;                          // VMAX / VMIN
            case 0x7:                                                               // VABD / VABA
            {
                var diff = AbsDiff(x, y, size, u);
                r = b ? AddSub(LoadV(s, d, q), diff, size, false) : diff;
                break;
            }
            case 0x8 when !b: r = AddSub(x, y, size, u); break;                     // VADD / VSUB
            case 0x8:                                                               // VTST / VCEQ
                r = u ? Equal(x, y, size) : ~Equal(x & y, Vector128<byte>.Zero, size);
                break;
            case 0x9 when !b:                                                       // VMLA / VMLS
            {
                var acc = LoadV(s, d, q);
                var prod = Mul(x, y, size);
                r = AddSub(acc, prod, size, u);
                break;
            }
            case 0x9 when !u: r = Mul(x, y, size); break;                           // VMUL (integer)
            default: return false;
        }
        StoreV(s, d, r, q);
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool LanesEqual(Vector128<byte> a, Vector128<byte> b, bool q)
        => q ? Vector128.EqualsAll(a, b) : a.AsUInt64().GetElement(0) == b.AsUInt64().GetElement(0);

    static Vector128<byte> AddSub(Vector128<byte> x, Vector128<byte> y, uint size, bool sub) => size switch
    {
        0 => sub ? x - y : x + y,
        1 => (sub ? x.AsUInt16() - y.AsUInt16() : x.AsUInt16() + y.AsUInt16()).AsByte(),
        2 => (sub ? x.AsUInt32() - y.AsUInt32() : x.AsUInt32() + y.AsUInt32()).AsByte(),
        _ => (sub ? x.AsUInt64() - y.AsUInt64() : x.AsUInt64() + y.AsUInt64()).AsByte(),
    };

    static Vector128<byte> Mul(Vector128<byte> x, Vector128<byte> y, uint size) => size switch
    {
        0 => x * y,
        1 => (x.AsUInt16() * y.AsUInt16()).AsByte(),
        _ => (x.AsUInt32() * y.AsUInt32()).AsByte(),
    };

    static Vector128<byte> Equal(Vector128<byte> x, Vector128<byte> y, uint size) => size switch
    {
        0 => Vector128.Equals(x, y),
        1 => Vector128.Equals(x.AsUInt16(), y.AsUInt16()).AsByte(),
        _ => Vector128.Equals(x.AsUInt32(), y.AsUInt32()).AsByte(),
    };

    static Vector128<byte> Compare(Vector128<byte> x, Vector128<byte> y, uint size, bool u, bool orEqual)
    {
        if (u) return size switch
        {
            0 => orEqual ? Vector128.GreaterThanOrEqual(x, y) : Vector128.GreaterThan(x, y),
            1 => (orEqual ? Vector128.GreaterThanOrEqual(x.AsUInt16(), y.AsUInt16()) : Vector128.GreaterThan(x.AsUInt16(), y.AsUInt16())).AsByte(),
            _ => (orEqual ? Vector128.GreaterThanOrEqual(x.AsUInt32(), y.AsUInt32()) : Vector128.GreaterThan(x.AsUInt32(), y.AsUInt32())).AsByte(),
        };
        return size switch
        {
            0 => (orEqual ? Vector128.GreaterThanOrEqual(x.AsSByte(), y.AsSByte()) : Vector128.GreaterThan(x.AsSByte(), y.AsSByte())).AsByte(),
            1 => (orEqual ? Vector128.GreaterThanOrEqual(x.AsInt16(), y.AsInt16()) : Vector128.GreaterThan(x.AsInt16(), y.AsInt16())).AsByte(),
            _ => (orEqual ? Vector128.GreaterThanOrEqual(x.AsInt32(), y.AsInt32()) : Vector128.GreaterThan(x.AsInt32(), y.AsInt32())).AsByte(),
        };
    }

    static Vector128<byte> MaxMin(Vector128<byte> x, Vector128<byte> y, uint size, bool u, bool min)
    {
        if (u) return size switch
        {
            0 => min ? Vector128.Min(x, y) : Vector128.Max(x, y),
            1 => (min ? Vector128.Min(x.AsUInt16(), y.AsUInt16()) : Vector128.Max(x.AsUInt16(), y.AsUInt16())).AsByte(),
            _ => (min ? Vector128.Min(x.AsUInt32(), y.AsUInt32()) : Vector128.Max(x.AsUInt32(), y.AsUInt32())).AsByte(),
        };
        return size switch
        {
            0 => (min ? Vector128.Min(x.AsSByte(), y.AsSByte()) : Vector128.Max(x.AsSByte(), y.AsSByte())).AsByte(),
            1 => (min ? Vector128.Min(x.AsInt16(), y.AsInt16()) : Vector128.Max(x.AsInt16(), y.AsInt16())).AsByte(),
            _ => (min ? Vector128.Min(x.AsInt32(), y.AsInt32()) : Vector128.Max(x.AsInt32(), y.AsInt32())).AsByte(),
        };
    }

    /// <summary>|x - y| per lane as the unsigned lane width: max - min in the operand's own domain wraps to the magnitude.</summary>
    static Vector128<byte> AbsDiff(Vector128<byte> x, Vector128<byte> y, uint size, bool u)
        => AddSub(MaxMin(x, y, size, u, false), MaxMin(x, y, size, u, true), size, true);

    /// <summary>(x + y) >> 1 (rounding: (x + y + 1) >> 1) without the intermediate overflow: (x &amp; y) + ((x ^ y) >> 1), or (x | y) - ((x ^ y) >> 1).</summary>
    static Vector128<byte> HalvingAdd(Vector128<byte> x, Vector128<byte> y, uint size, bool u, bool round)
    {
        var xor = x ^ y;
        var half = ShiftRight1(xor, size, u);
        return round ? AddSub(x | y, half, size, true) : AddSub(x & y, half, size, false);
    }

    /// <summary>(x - y) >> 1 = (x >> 1) - (y >> 1) - (~x &amp; y &amp; 1).</summary>
    static Vector128<byte> HalvingSub(Vector128<byte> x, Vector128<byte> y, uint size, bool u)
    {
        var one = size switch { 0 => Vector128.Create((byte)1), 1 => Vector128.Create((ushort)1).AsByte(), _ => Vector128.Create(1u).AsByte() };
        var borrow = ~x & y & one;
        return AddSub(AddSub(ShiftRight1(x, size, u), ShiftRight1(y, size, u), size, true), borrow, size, true);
    }

    static Vector128<byte> ShiftRight1(Vector128<byte> v, uint size, bool u) => ShiftRightN(v, size, u, 1);

    static Vector128<byte> ShiftRightN(Vector128<byte> v, uint size, bool u, int n)
    {
        if (u) return size switch
        {
            0 => Vector128.ShiftRightLogical(v, n),
            1 => Vector128.ShiftRightLogical(v.AsUInt16(), n).AsByte(),
            2 => Vector128.ShiftRightLogical(v.AsUInt32(), n).AsByte(),
            _ => Vector128.ShiftRightLogical(v.AsUInt64(), n).AsByte(),
        };
        return size switch
        {
            0 => Vector128.ShiftRightArithmetic(v.AsSByte(), n).AsByte(),
            1 => Vector128.ShiftRightArithmetic(v.AsInt16(), n).AsByte(),
            2 => Vector128.ShiftRightArithmetic(v.AsInt32(), n).AsByte(),
            _ => Vector128.ShiftRightArithmetic(v.AsInt64(), n).AsByte(),
        };
    }

    static Vector128<byte> ShiftLeftN(Vector128<byte> v, uint size, int n) => size switch
    {
        0 => Vector128.ShiftLeft(v, n),
        1 => Vector128.ShiftLeft(v.AsUInt16(), n).AsByte(),
        2 => Vector128.ShiftLeft(v.AsUInt32(), n).AsByte(),
        _ => Vector128.ShiftLeft(v.AsUInt64(), n).AsByte(),
    };

    static Vector128<byte> LaneOnes(uint size) => size switch
    {
        0 => Vector128.Create((byte)1),
        1 => Vector128.Create((ushort)1).AsByte(),
        2 => Vector128.Create(1u).AsByte(),
        _ => Vector128.Create(1ul).AsByte(),
    };

    // ------------------------------------------------------------------ shifts by immediate

    static bool ShiftFast(CpuState* s, uint ins)
    {
        uint a = Bits(ins, 11, 8);
        bool u = Bit(ins, 24) != 0, l = Bit(ins, 7) != 0, q = Bit(ins, 6) != 0;
        uint imm6 = Bits(ins, 21, 16);
        uint d = Rd(ins), m = Rm(ins);
        int esize = l ? 64 : imm6 >= 32 ? 32 : imm6 >= 16 ? 16 : 8;
        uint size = esize == 8 ? 0u : esize == 16 ? 1u : esize == 32 ? 2u : 3u;
        int rshift = l ? 64 - (int)imm6 : 2 * esize - (int)imm6;
        int lshift = l ? (int)imm6 : (int)imm6 - esize;

        if (a <= 3) // VSHR / VSRA / VRSHR / VRSRA
        {
            if (q && ((d | m) & 1) != 0) return false;
            var x = LoadV(s, m, q);
            bool round = (a & 2) != 0, accumulate = (a & 1) != 0;
            Vector128<byte> r;
            if (rshift >= esize)
            {
                // shifting the whole lane out: unsigned gives 0, signed gives the sign; rounding adds the top bit
                r = u ? Vector128<byte>.Zero : ShiftRightN(x, size, false, esize - 1);
                if (round) r = AddSub(r, ShiftRightN(x, size, true, esize - 1) & LaneOnes(size), size, false);
            }
            else
            {
                r = ShiftRightN(x, size, u, rshift);
                if (round) r = AddSub(r, ShiftRightN(x, size, true, rshift - 1) & LaneOnes(size), size, false);
            }
            if (accumulate) r = AddSub(LoadV(s, d, q), r, size, false);
            StoreV(s, d, r, q);
            return true;
        }
        if (a == 5 && !u) // VSHL (immediate)
        {
            if (q && ((d | m) & 1) != 0) return false;
            StoreV(s, d, ShiftLeftN(LoadV(s, m, q), size, lshift), q);
            return true;
        }
        if (a == 10) // VSHLL / VMOVL: D source, Q destination, lanes widen
        {
            if (esize == 64 || (d & 1) != 0) return false;
            var x = LoadD(s, m);
            Vector128<byte> wide = size switch
            {
                0 => (u ? Vector128.WidenLower(x).AsByte() : Vector128.WidenLower(x.AsSByte()).AsByte()),
                1 => (u ? Vector128.WidenLower(x.AsUInt16()).AsByte() : Vector128.WidenLower(x.AsInt16()).AsByte()),
                _ => (u ? Vector128.WidenLower(x.AsUInt32()).AsByte() : Vector128.WidenLower(x.AsInt32()).AsByte()),
            };
            if (lshift > 0) wide = ShiftLeftN(wide, size + 1, lshift);
            wide.Store(Reg(s, d));
            return true;
        }
        if (a == 8 || a == 9) // narrowing right shifts: Q source, D destination
        {
            if (esize == 32 || (m & 1) != 0) return false; // 64 -> 32 has no pack instruction: reference path
            uint wsize = size + 1; // source lane size
            bool round = q;
            bool srcUnsigned = a == 9 && u;
            var x = LoadQ(s, m);
            // the shifted value, kept at the wide lane size
            Vector128<byte> sh;
            if (rshift >= 2 * esize) sh = srcUnsigned ? Vector128<byte>.Zero : ShiftRightN(x, wsize, false, 2 * esize - 1);
            else sh = ShiftRightN(x, wsize, srcUnsigned, rshift);
            if (round)
            {
                var bit = rshift >= 2 * esize ? ShiftRightN(x, wsize, true, 2 * esize - 1) & LaneOnes(wsize) : ShiftRightN(x, wsize, true, rshift - 1) & LaneOnes(wsize);
                sh = AddSub(sh, bit, wsize, false);
            }
            Vector128<byte> narrow;
            if (a == 8 && !u)
            {
                // VSHRN / VRSHRN: truncating
                narrow = wsize == 1 ? Vector128.Narrow(sh.AsUInt16(), sh.AsUInt16()) : Vector128.Narrow(sh.AsUInt32(), sh.AsUInt32()).AsByte();
            }
            else
            {
                if (!Sse2.IsSupported) return false;
                Vector128<byte> back; // the narrowed result widened again, to detect saturation
                if (a == 8) // VQSHRUN / VQRSHRUN: signed source, unsigned result
                {
                    if (wsize == 1) { var p = Sse2.PackUnsignedSaturate(sh.AsInt16(), sh.AsInt16()); narrow = p; back = Vector128.WidenLower(p).AsByte(); }
                    else { if (!Sse41.IsSupported) return false; var p = Sse41.PackUnsignedSaturate(sh.AsInt32(), sh.AsInt32()); narrow = p.AsByte(); back = Vector128.WidenLower(p).AsByte(); }
                }
                else if (!u) // VQSHRN / VQRSHRN signed
                {
                    if (wsize == 1) { var p = Sse2.PackSignedSaturate(sh.AsInt16(), sh.AsInt16()); narrow = p.AsByte(); back = Vector128.WidenLower(p).AsByte(); }
                    else { var p = Sse2.PackSignedSaturate(sh.AsInt32(), sh.AsInt32()); narrow = p.AsByte(); back = Vector128.WidenLower(p).AsByte(); }
                }
                else // VQSHRN / VQRSHRN unsigned: clamp to the narrow maximum, then truncate
                {
                    if (wsize == 1) { var c = Vector128.Min(sh.AsUInt16(), Vector128.Create((ushort)255)); narrow = Vector128.Narrow(c, c); back = c.AsByte(); }
                    else { var c = Vector128.Min(sh.AsUInt32(), Vector128.Create(65535u)); narrow = Vector128.Narrow(c, c).AsByte(); back = c.AsByte(); }
                }
                if (!Vector128.EqualsAll(back, sh)) SetQC(s);
            }
            *(ulong*)Reg(s, d) = narrow.AsUInt64().GetElement(0);
            return true;
        }
        return false;
    }

    // ------------------------------------------------------------------ three registers, different lengths

    static bool ThreeDiffFast(CpuState* s, uint ins)
    {
        uint a = Bits(ins, 11, 8);
        bool u = Bit(ins, 24) != 0;
        uint size = Bits(ins, 21, 20);
        if (size == 3) return false;
        uint d = Rd(ins), n = Rn(ins), m = Rm(ins);
        if ((d & 1) != 0) return false;
        bool wideN = a == 1 || a == 3;
        if (a is 4 or 6 or 9 or 11 or 13 or 14 or 15) return false; // narrowing / saturating doubling / polynomial: reference path
        if (wideN && (n & 1) != 0) return false;
        uint wsize = size + 1;
        var xn = wideN ? LoadQ(s, n) : Widen(LoadD(s, n), size, u);
        var ym = Widen(LoadD(s, m), size, u);
        Vector128<byte> r;
        switch (a)
        {
            case 0: case 1: r = AddSub(xn, ym, wsize, false); break;                       // VADDL / VADDW
            case 2: case 3: r = AddSub(xn, ym, wsize, true); break;                        // VSUBL / VSUBW
            case 5: r = AddSub(LoadQ(s, d), AbsDiff(xn, ym, wsize, u), wsize, false); break; // VABAL
            case 7: r = AbsDiff(xn, ym, wsize, u); break;                                  // VABDL
            case 8: r = AddSub(LoadQ(s, d), MulWide(xn, ym, wsize), wsize, false); break;  // VMLAL
            case 10: r = AddSub(LoadQ(s, d), MulWide(xn, ym, wsize), wsize, true); break;  // VMLSL
            case 12: r = MulWide(xn, ym, wsize); break;                                    // VMULL
            default: return false;
        }
        r.Store(Reg(s, d));
        return true;
    }

    static Vector128<byte> Widen(Vector128<byte> x, uint size, bool u) => size switch
    {
        0 => u ? Vector128.WidenLower(x).AsByte() : Vector128.WidenLower(x.AsSByte()).AsByte(),
        1 => u ? Vector128.WidenLower(x.AsUInt16()).AsByte() : Vector128.WidenLower(x.AsInt16()).AsByte(),
        _ => u ? Vector128.WidenLower(x.AsUInt32()).AsByte() : Vector128.WidenLower(x.AsInt32()).AsByte(),
    };

    static Vector128<byte> MulWide(Vector128<byte> x, Vector128<byte> y, uint wsize) => wsize switch
    {
        1 => (x.AsUInt16() * y.AsUInt16()).AsByte(),
        2 => (x.AsUInt32() * y.AsUInt32()).AsByte(),
        _ => (x.AsUInt64() * y.AsUInt64()).AsByte(),
    };
}
