using System.Numerics;
using System.Runtime.CompilerServices;
using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Cpu;

/// <summary>
/// Guest memory accesses of the interpreter and the IL backend: through the thread's address space
/// (<see cref="CpuState.MemBase"/>, its process's <see cref="AddressSpace"/>), one load from the state the handlers already
/// hold. The page dirty table sits right below guest address 0 of the same space.
/// </summary>
internal static unsafe class Mem
{
    /// <summary>Marks the page of a store as written: after the store, and the byte is only written while clear.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void MarkDirty(CpuState* s, uint a)
    {
        if (!DirtyPages.Enabled) return;
        byte* p = s->MemBase - DirtyPages.TableBytes + (a >> DirtyPages.PageShift);
        if (*p == 0) *p = 1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static byte* Ptr(CpuState* s, uint a) => s->MemBase + a;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static byte R8(CpuState* s, uint a) => s->MemBase[a];
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ushort R16(CpuState* s, uint a) => Unsafe.ReadUnaligned<ushort>(s->MemBase + a);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static uint R32(CpuState* s, uint a) { if (WriteWatch.Enabled && a - WriteWatch.ReadLo + 3 < WriteWatch.ReadSpan + 3) WriteWatch.ReadHit(a, 4); return Unsafe.ReadUnaligned<uint>(s->MemBase + a); }
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong R64(CpuState* s, uint a) { if (WriteWatch.Enabled && a - WriteWatch.ReadLo + 7 < WriteWatch.ReadSpan + 7) WriteWatch.ReadHit(a, 8); return Unsafe.ReadUnaligned<ulong>(s->MemBase + a); }
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static void W8(CpuState* s, uint a, uint v) { if (WriteWatch.Enabled && a - WriteWatch.Lo < WriteWatch.Span) WriteWatch.Hit(a, 1, v); s->MemBase[a] = (byte)v; MarkDirty(s, a); }
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static void W16(CpuState* s, uint a, uint v) { if (WriteWatch.Enabled && a - WriteWatch.Lo + 1 < WriteWatch.Span + 1) WriteWatch.Hit(a, 2, v); Unsafe.WriteUnaligned(s->MemBase + a, (ushort)v); MarkDirty(s, a); }
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static void W32(CpuState* s, uint a, uint v) { if (WriteWatch.Enabled && a - WriteWatch.Lo + 3 < WriteWatch.Span + 3) WriteWatch.Hit(a, 4, v); Unsafe.WriteUnaligned(s->MemBase + a, v); MarkDirty(s, a); }
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static void W64(CpuState* s, uint a, ulong v) { if (WriteWatch.Enabled && a - WriteWatch.Lo + 7 < WriteWatch.Span + 7) WriteWatch.Hit(a, 8, v); Unsafe.WriteUnaligned(s->MemBase + a, v); MarkDirty(s, a); }
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
    //
    // LDREX/STREX used to be emulated as a compare-and-swap against the value LDREX read. That is not LL/SC: a STREX
    // must fail when another core stored to the location since the LDREX, even if the value is back to what was read
    // (ABA). libult (SceUlt, shipped by Uncharted: Golden Abyss) keeps semaphore state in untagged words updated with
    // LDREXD/STREXD; with plain CAS a stale snapshot could still be committed, leaving a semaphore with count 1 and
    // its waiter asleep forever (the game hung while loading). Every successful exclusive store now also bumps a
    // generation counter for its 8-byte granule (striped table); LDREX snapshots it and STREX fails if it moved.
    // Plain stores do not bump it (they are still caught by the value compare).

    internal const int ExclusiveStripes = 4096;
    // pinned (pinned object heap): the native backend addresses it directly
    internal static readonly uint[] ExclusiveGens = GC.AllocateArray<uint>(ExclusiveStripes, pinned: true);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static ref uint GenOf(uint addr) => ref ExclusiveGens[(addr >> 3) & (ExclusiveStripes - 1)];

    /// <summary>Arms the monitor. Must run BEFORE the value is read, so an exclusive store in between fails the STREX.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ReserveExclusive(CpuState* s, uint addr)
    {
        ref uint g = ref GenOf(addr);
        uint v;
        while (((v = Volatile.Read(ref g)) & 1) != 0) Thread.SpinWait(1); // an exclusive store is in flight
        s->ExclusiveGen = v;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetExclusive(CpuState* s, uint addr, ulong value)
    {
        s->ExclusiveAddr = addr;
        s->ExclusiveValue = value;
        s->ExclusiveValid = 1;
    }

    /// <summary>Locks the granule if nobody stored to it exclusively since our LDREX; false = the STREX fails.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool BeginExclusiveStore(CpuState* s, uint addr)
    {
        if (s->ExclusiveValid == 0 || s->ExclusiveAddr != addr) return false;
        s->ExclusiveValid = 0;
        uint snap = s->ExclusiveGen;
        return Interlocked.CompareExchange(ref GenOf(addr), snap + 1, snap) == snap;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool EndExclusiveStore(CpuState* s, uint addr, bool ok)
    {
        Volatile.Write(ref GenOf(addr), ok ? s->ExclusiveGen + 2 : s->ExclusiveGen);
        if (ok) Mem.MarkDirty(s, addr);
        return ok;
    }

    public static bool StoreExclusive32(CpuState* s, uint addr, uint value)
    {
        if (!BeginExclusiveStore(s, addr)) return false;
        ref uint target = ref Unsafe.AsRef<uint>(s->MemBase +addr);
        bool ok = Interlocked.CompareExchange(ref target, value, (uint)s->ExclusiveValue) == (uint)s->ExclusiveValue;
        if (WriteWatch.Enabled && ok && addr - WriteWatch.Lo + 3 < WriteWatch.Span + 3) WriteWatch.Hit(addr, 4, value);
        return EndExclusiveStore(s, addr, ok);
    }

    public static bool StoreExclusive64(CpuState* s, uint addr, ulong value)
    {
        if (!BeginExclusiveStore(s, addr)) return false;
        ref ulong target = ref Unsafe.AsRef<ulong>(s->MemBase +addr);
        bool ok = Interlocked.CompareExchange(ref target, value, s->ExclusiveValue) == s->ExclusiveValue;
        return EndExclusiveStore(s, addr, ok);
    }

    public static bool StoreExclusive16(CpuState* s, uint addr, uint value)
    {
        if (!BeginExclusiveStore(s, addr)) return false;
        // CAS on the containing aligned word, only replacing our halfword.
        uint wa = addr & ~3u;
        int sh = (int)(addr & 2) * 8;
        ref uint w = ref Unsafe.AsRef<uint>(s->MemBase +wa);
        while (true)
        {
            uint old = Volatile.Read(ref w);
            if (((old >> sh) & 0xFFFF) != (uint)(s->ExclusiveValue & 0xFFFF)) return EndExclusiveStore(s, addr, false);
            uint nw = (old & ~(0xFFFFu << sh)) | ((value & 0xFFFF) << sh);
            if (Interlocked.CompareExchange(ref w, nw, old) == old) return EndExclusiveStore(s, addr, true);
        }
    }

    public static bool StoreExclusive8(CpuState* s, uint addr, uint value)
    {
        if (!BeginExclusiveStore(s, addr)) return false;
        uint wa = addr & ~3u;
        int sh = (int)(addr & 3) * 8;
        ref uint w = ref Unsafe.AsRef<uint>(s->MemBase +wa);
        while (true)
        {
            uint old = Volatile.Read(ref w);
            if (((old >> sh) & 0xFF) != (uint)(s->ExclusiveValue & 0xFF)) return EndExclusiveStore(s, addr, false);
            uint nw = (old & ~(0xFFu << sh)) | ((value & 0xFF) << sh);
            if (Interlocked.CompareExchange(ref w, nw, old) == old) return EndExclusiveStore(s, addr, true);
        }
    }

    // ------------------------------------------------------------- misc

    /// <summary>
    /// Sign-extends the low <paramref name="bits"/> bits of <paramref name="v"/>. Written with unsigned xor/subtract,
    /// not the usual (int)(v &lt;&lt; n) &gt;&gt; n: Mono's JIT (.NET for Android) folds that shift pair as a logical shift when
    /// v is a constant, which it is once the IL backend inlines a handler with the instruction word as a constant -
    /// every backward Thumb B then branched 4 KB forward (found with VITASHARP_NATIVE_VERIFY on the Android emulator).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint SignExtend(uint v, int bits)
    {
        uint sign = 1u << (bits - 1);
        return ((v & ((sign << 1) - 1)) ^ sign) - sign;
    }

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
