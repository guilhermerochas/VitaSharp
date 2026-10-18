using System.Numerics;
using System.Runtime.CompilerServices;
using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Cpu;

/// <summary>Raw guest memory base cached as a static readonly so JIT-compiled code folds it to a constant.</summary>
internal static unsafe class Mem
{
    public static readonly byte* Base = GuestMemory.Base;

    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static byte R8(uint a) => Base[a];
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ushort R16(uint a) => Unsafe.ReadUnaligned<ushort>(Base + a);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static uint R32(uint a) { if (WriteWatch.Enabled && a - WriteWatch.ReadLo + 3 < WriteWatch.ReadSpan + 3) WriteWatch.ReadHit(a, 4); return Unsafe.ReadUnaligned<uint>(Base + a); }
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong R64(uint a) { if (WriteWatch.Enabled && a - WriteWatch.ReadLo + 7 < WriteWatch.ReadSpan + 7) WriteWatch.ReadHit(a, 8); return Unsafe.ReadUnaligned<ulong>(Base + a); }
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static void W8(uint a, uint v) { if (WriteWatch.Enabled && a - WriteWatch.Lo < WriteWatch.Span) WriteWatch.Hit(a, 1, v); Base[a] = (byte)v; }
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static void W16(uint a, uint v) { if (WriteWatch.Enabled && a - WriteWatch.Lo + 1 < WriteWatch.Span + 1) WriteWatch.Hit(a, 2, v); Unsafe.WriteUnaligned(Base + a, (ushort)v); }
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static void W32(uint a, uint v) { if (WriteWatch.Enabled && a - WriteWatch.Lo + 3 < WriteWatch.Span + 3) WriteWatch.Hit(a, 4, v); Unsafe.WriteUnaligned(Base + a, v); }
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static void W64(uint a, ulong v) { if (WriteWatch.Enabled && a - WriteWatch.Lo + 7 < WriteWatch.Span + 7) WriteWatch.Hit(a, 8, v); Unsafe.WriteUnaligned(Base + a, v); }
}

/// <summary>Shared ARMv7 semantics (pseudocode functions from the ARM ARM) used by every encoding.</summary>
internal static unsafe class Arm
{
    public const uint CondAL = 14;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool CondPassed(CpuState* s, uint cond)
    {
        switch (cond)
        {
            case 0: return s->Z != 0;
            case 1: return s->Z == 0;
            case 2: return s->C != 0;
            case 3: return s->C == 0;
            case 4: return s->N != 0;
            case 5: return s->N == 0;
            case 6: return s->V != 0;
            case 7: return s->V == 0;
            case 8: return s->C != 0 && s->Z == 0;
            case 9: return s->C == 0 || s->Z != 0;
            case 10: return s->N == s->V;
            case 11: return s->N != s->V;
            case 12: return s->Z == 0 && s->N == s->V;
            case 13: return s->Z != 0 || s->N != s->V;
            default: return true;
        }
    }

    // ------------------------------------------------------------- flags / ALU

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetNZ(CpuState* s, uint r)
    {
        s->N = r >> 31;
        s->Z = r == 0 ? 1u : 0u;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint AddWithCarry(uint x, uint y, uint carryIn, out uint carryOut, out uint overflow)
    {
        ulong usum = (ulong)x + y + carryIn;
        uint r = (uint)usum;
        carryOut = (uint)(usum >> 32);
        overflow = ((~(x ^ y) & (x ^ r)) >> 31);
        return r;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint AddFlags(CpuState* s, uint x, uint y, uint cin)
    {
        uint r = AddWithCarry(x, y, cin, out uint c, out uint v);
        s->N = r >> 31; s->Z = r == 0 ? 1u : 0u; s->C = c; s->V = v;
        return r;
    }

    // ------------------------------------------------------------- shifts

    public const int LSL = 0, LSR = 1, ASR = 2, ROR = 3, RRX = 4;

    /// <summary>DecodeImmShift: returns shift type (with RRX) and amount.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void DecodeImmShift(uint type, uint imm5, out int t, out int n)
    {
        switch (type)
        {
            case 0: t = LSL; n = (int)imm5; break;
            case 1: t = LSR; n = imm5 == 0 ? 32 : (int)imm5; break;
            case 2: t = ASR; n = imm5 == 0 ? 32 : (int)imm5; break;
            default:
                if (imm5 == 0) { t = RRX; n = 1; } else { t = ROR; n = (int)imm5; }
                break;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Shift(uint v, int type, int amount, uint carryIn)
    {
        if (amount == 0 && type != RRX) return v;
        switch (type)
        {
            case LSL: return amount >= 32 ? 0 : v << amount;
            case LSR: return amount >= 32 ? 0 : v >> amount;
            case ASR: return amount >= 32 ? (uint)((int)v >> 31) : (uint)((int)v >> amount);
            case ROR: return BitOperations.RotateRight(v, amount & 31);
            default: return (carryIn << 31) | (v >> 1);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint ShiftC(uint v, int type, int amount, uint carryIn, out uint carryOut)
    {
        if (amount == 0 && type != RRX) { carryOut = carryIn; return v; }
        switch (type)
        {
            case LSL:
                if (amount > 32) { carryOut = 0; return 0; }
                if (amount == 32) { carryOut = v & 1; return 0; }
                carryOut = (v >> (32 - amount)) & 1;
                return v << amount;
            case LSR:
                if (amount > 32) { carryOut = 0; return 0; }
                if (amount == 32) { carryOut = v >> 31; return 0; }
                carryOut = (v >> (amount - 1)) & 1;
                return v >> amount;
            case ASR:
                if (amount >= 32) { carryOut = v >> 31; return (uint)((int)v >> 31); }
                carryOut = (v >> (amount - 1)) & 1;
                return (uint)((int)v >> amount);
            case ROR:
            {
                uint r = BitOperations.RotateRight(v, amount & 31);
                carryOut = r >> 31;
                return r;
            }
            default:
                carryOut = v & 1;
                return (carryIn << 31) | (v >> 1);
        }
    }

    /// <summary>Register-specified shift (amount from low byte of Rs). type is 0..3.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint ShiftRegC(uint v, uint type, uint rs, uint carryIn, out uint carryOut)
    {
        int amount = (int)(rs & 0xFF);
        if (amount == 0) { carryOut = carryIn; return v; }
        if (type == 3)
        {
            int a = amount & 31;
            if (a == 0) { carryOut = v >> 31; return v; }
            return ShiftC(v, ROR, a, carryIn, out carryOut);
        }
        return ShiftC(v, (int)type, amount, carryIn, out carryOut);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint ArmExpandImmC(uint imm12, uint carryIn, out uint carryOut)
    {
        uint imm8 = imm12 & 0xFF;
        int rot = (int)((imm12 >> 8) & 0xF) * 2;
        if (rot == 0) { carryOut = carryIn; return imm8; }
        uint r = BitOperations.RotateRight(imm8, rot);
        carryOut = r >> 31;
        return r;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint ThumbExpandImmC(uint imm12, uint carryIn, out uint carryOut)
    {
        if ((imm12 >> 10) == 0)
        {
            carryOut = carryIn;
            uint b = imm12 & 0xFF;
            switch ((imm12 >> 8) & 3)
            {
                case 0: return b;
                case 1: return (b << 16) | b;
                case 2: return (b << 24) | (b << 8);
                default: return (b << 24) | (b << 16) | (b << 8) | b;
            }
        }
        uint unrot = 0x80 | (imm12 & 0x7F);
        uint r = BitOperations.RotateRight(unrot, (int)(imm12 >> 7));
        carryOut = r >> 31;
        return r;
    }

    // ------------------------------------------------------------- PC writes

    /// <summary>BXWritePC / LoadWritePC: interworking branch.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void BxWritePc(CpuState* s, uint addr)
    {
        if ((addr & 1) != 0) { s->Thumb = 1; s->R[15] = addr & ~1u; }
        else { s->Thumb = 0; s->R[15] = addr & ~3u; }
    }

    /// <summary>ALUWritePC in ARM state is interworking in ARMv7.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void AluWritePcArm(CpuState* s, uint addr) => BxWritePc(s, addr);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void BranchWritePcThumb(CpuState* s, uint addr) => s->R[15] = addr & ~1u;

    // ------------------------------------------------------------- saturation

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int SignedSat(long v, int bits, out bool sat)
    {
        long max = (1L << (bits - 1)) - 1, min = -(1L << (bits - 1));
        if (v > max) { sat = true; return (int)max; }
        if (v < min) { sat = true; return (int)min; }
        sat = false; return (int)v;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint UnsignedSat(long v, int bits, out bool sat)
    {
        long max = (1L << bits) - 1;
        if (v > max) { sat = true; return (uint)max; }
        if (v < 0) { sat = true; return 0; }
        sat = false; return (uint)v;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint SatQ(CpuState* s, long v)
    {
        int r = SignedSat(v, 32, out bool sat);
        if (sat) s->Q = 1;
        return (uint)r;
    }

    // ------------------------------------------------------------- exclusive monitor

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetExclusive(CpuState* s, uint addr, ulong value)
    {
        s->ExclusiveAddr = addr;
        s->ExclusiveValue = value;
        s->ExclusiveValid = 1;
    }

    public static bool StoreExclusive32(CpuState* s, uint addr, uint value)
    {
        if (s->ExclusiveValid == 0 || s->ExclusiveAddr != addr) return false;
        s->ExclusiveValid = 0;
        ref uint target = ref Unsafe.AsRef<uint>(Mem.Base + addr);
        return Interlocked.CompareExchange(ref target, value, (uint)s->ExclusiveValue) == (uint)s->ExclusiveValue;
    }

    public static bool StoreExclusive64(CpuState* s, uint addr, ulong value)
    {
        if (s->ExclusiveValid == 0 || s->ExclusiveAddr != addr) return false;
        s->ExclusiveValid = 0;
        ref ulong target = ref Unsafe.AsRef<ulong>(Mem.Base + addr);
        return Interlocked.CompareExchange(ref target, value, s->ExclusiveValue) == s->ExclusiveValue;
    }

    public static bool StoreExclusive16(CpuState* s, uint addr, uint value)
    {
        if (s->ExclusiveValid == 0 || s->ExclusiveAddr != addr) return false;
        s->ExclusiveValid = 0;
        // CAS on the containing aligned word, only replacing our halfword.
        uint wa = addr & ~3u;
        int sh = (int)(addr & 2) * 8;
        ref uint w = ref Unsafe.AsRef<uint>(Mem.Base + wa);
        while (true)
        {
            uint old = Volatile.Read(ref w);
            if (((old >> sh) & 0xFFFF) != (uint)(s->ExclusiveValue & 0xFFFF)) return false;
            uint nw = (old & ~(0xFFFFu << sh)) | ((value & 0xFFFF) << sh);
            if (Interlocked.CompareExchange(ref w, nw, old) == old) return true;
        }
    }

    public static bool StoreExclusive8(CpuState* s, uint addr, uint value)
    {
        if (s->ExclusiveValid == 0 || s->ExclusiveAddr != addr) return false;
        s->ExclusiveValid = 0;
        uint wa = addr & ~3u;
        int sh = (int)(addr & 3) * 8;
        ref uint w = ref Unsafe.AsRef<uint>(Mem.Base + wa);
        while (true)
        {
            uint old = Volatile.Read(ref w);
            if (((old >> sh) & 0xFF) != (uint)(s->ExclusiveValue & 0xFF)) return false;
            uint nw = (old & ~(0xFFu << sh)) | ((value & 0xFF) << sh);
            if (Interlocked.CompareExchange(ref w, nw, old) == old) return true;
        }
    }

    // ------------------------------------------------------------- misc

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint SignExtend(uint v, int bits) => (uint)(((int)(v << (32 - bits))) >> (32 - bits));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Bit(uint v, int n) => (v >> n) & 1;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Bits(uint v, int hi, int lo) => (v >> lo) & (uint)((1L << (hi - lo + 1)) - 1);

    public static void Undefined(CpuState* s, uint pc)
    {
        s->Exit = ExitReason.Undefined;
        s->SvcPc = pc;
    }

    /// <summary>Raised by handlers for encodings that are architecturally UNPREDICTABLE/UNDEFINED.</summary>
    public sealed class UndefinedInstructionException(uint pc, uint ins, bool thumb)
        : Exception($"Undefined instruction 0x{ins:X8} at 0x{pc:X8} ({(thumb ? "Thumb" : "ARM")})")
    {
        public uint Pc { get; } = pc;
        public uint Instruction { get; } = ins;
    }
}
