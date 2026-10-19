using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// sceKernelAtomic* (SceLibKernel, psp2/kernel/cpu.h): read-modify-write operations on guest memory. On hardware they
/// are LDREX/STREX loops inside the library; here a compare-and-swap on the value (8/16-bit operations swap the
/// containing aligned word). "XAndGet" returns the new value, "GetAndX" the old one; 8/16-bit results are sign-extended.
/// </summary>
public sealed class Atomic : HleModule
{
    /// <summary>Sign-extends the low <paramref name="bytes"/> bytes of <paramref name="v"/>.</summary>
    internal static long Narrow(long v, int bytes) => bytes switch { 1 => (sbyte)v, 2 => (short)v, 4 => (int)v, _ => v };

    /// <summary>Atomically replaces the value at <paramref name="addr"/> by f(old) (no store when f returns null).</summary>
    internal static (long Old, long New) Rmw(uint addr, int bytes, Func<long, long?> f)
    {
        switch (bytes)
        {
            case 8:
            {
                ref long r = ref GuestMemory.Ref<long>(addr);
                while (true)
                {
                    long old = Volatile.Read(ref r);
                    long? nv = f(old);
                    if (nv == null) return (old, old);
                    if (Interlocked.CompareExchange(ref r, nv.Value, old) == old) return (old, nv.Value);
                }
            }
            case 4:
            {
                ref int r = ref GuestMemory.Ref<int>(addr);
                while (true)
                {
                    int old = Volatile.Read(ref r);
                    long? nv = f(old);
                    if (nv == null) return (old, old);
                    if (Interlocked.CompareExchange(ref r, (int)nv.Value, old) == old) return (old, (int)nv.Value);
                }
            }
            default:
            {
                uint wa = addr & ~3u;
                int sh = (int)(addr & 3) * 8;
                uint mask = (bytes == 1 ? 0xFFu : 0xFFFFu) << sh;
                ref int r = ref GuestMemory.Ref<int>(wa);
                while (true)
                {
                    int word = Volatile.Read(ref r);
                    long old = Narrow((uint)word >> sh, bytes);
                    long? nv = f(old);
                    if (nv == null) return (old, old);
                    int nword = (int)(((uint)word & ~mask) | (((uint)nv.Value << sh) & mask));
                    if (Interlocked.CompareExchange(ref r, nword, word) == word) return (old, Narrow(nv.Value, bytes));
                }
            }
        }
    }

    /// <summary>Vita behaviour (Vita3K: "despite the name"): sum = old + v (32-bit wrap below 64 bits); when sum &gt;= limit
    /// stores limit and returns 1, else stores sum and returns 0.</summary>
    internal static int AddUnless(uint p, int bytes, long v, long limit)
    {
        v = Narrow(v, bytes);
        limit = Narrow(limit, bytes);
        var (old, _) = Rmw(p, bytes, o => Sum(o, v, bytes) is var s && s >= limit ? limit : s);
        return Sum(old, v, bytes) >= limit ? 1 : 0;
    }

    static long Sum(long o, long v, int bytes) => bytes == 8 ? unchecked(o + v) : unchecked((int)(o + v));

    // ---- 8-bit
    [Hle] int sceKernelAtomicAddAndGet8(uint p, int v) => (int)Rmw(p, 1, o => o + v).New;
    [Hle] int sceKernelAtomicSubAndGet8(uint p, int v) => (int)Rmw(p, 1, o => o - v).New;
    [Hle] int sceKernelAtomicAndAndGet8(uint p, int v) => (int)Rmw(p, 1, o => o & v).New;
    [Hle] int sceKernelAtomicOrAndGet8(uint p, int v) => (int)Rmw(p, 1, o => o | v).New;
    [Hle] int sceKernelAtomicXorAndGet8(uint p, int v) => (int)Rmw(p, 1, o => o ^ v).New;
    [Hle] int sceKernelAtomicClearAndGet8(uint p, int v) => (int)Rmw(p, 1, o => o & ~v).New;
    [Hle] int sceKernelAtomicGetAndAdd8(uint p, int v) => (int)Rmw(p, 1, o => o + v).Old;
    [Hle] int sceKernelAtomicGetAndSub8(uint p, int v) => (int)Rmw(p, 1, o => o - v).Old;
    [Hle] int sceKernelAtomicGetAndAnd8(uint p, int v) => (int)Rmw(p, 1, o => o & v).Old;
    [Hle] int sceKernelAtomicGetAndOr8(uint p, int v) => (int)Rmw(p, 1, o => o | v).Old;
    [Hle] int sceKernelAtomicGetAndXor8(uint p, int v) => (int)Rmw(p, 1, o => o ^ v).Old;
    [Hle] int sceKernelAtomicGetAndClear8(uint p, int v) => (int)Rmw(p, 1, o => o & ~v).Old;
    [Hle] int sceKernelAtomicGetAndSet8(uint p, int v) => (int)Rmw(p, 1, o => v).Old;
    [Hle] void sceKernelAtomicSet8(uint p, int v) => Rmw(p, 1, o => v);
    [Hle] void sceKernelAtomicClearMask8(uint p, int v) => Rmw(p, 1, o => o & ~v);
    [Hle] int sceKernelAtomicCompareAndSet8(uint p, int v, int nv) => (int)Rmw(p, 1, o => o == Narrow(v, 1) ? nv : null).Old;
    [Hle] int sceKernelAtomicAddUnless8(uint p, int v, int limit) => AddUnless(p, 1, v, limit);
    [Hle] int sceKernelAtomicDecIfPositive8(uint p) => (int)Rmw(p, 1, o => o >= 0 ? o - 1 : null).Old;
    // ---- 16-bit
    [Hle] int sceKernelAtomicAddAndGet16(uint p, int v) => (int)Rmw(p, 2, o => o + v).New;
    [Hle] int sceKernelAtomicSubAndGet16(uint p, int v) => (int)Rmw(p, 2, o => o - v).New;
    [Hle] int sceKernelAtomicAndAndGet16(uint p, int v) => (int)Rmw(p, 2, o => o & v).New;
    [Hle] int sceKernelAtomicOrAndGet16(uint p, int v) => (int)Rmw(p, 2, o => o | v).New;
    [Hle] int sceKernelAtomicXorAndGet16(uint p, int v) => (int)Rmw(p, 2, o => o ^ v).New;
    [Hle] int sceKernelAtomicClearAndGet16(uint p, int v) => (int)Rmw(p, 2, o => o & ~v).New;
    [Hle] int sceKernelAtomicGetAndAdd16(uint p, int v) => (int)Rmw(p, 2, o => o + v).Old;
    [Hle] int sceKernelAtomicGetAndSub16(uint p, int v) => (int)Rmw(p, 2, o => o - v).Old;
    [Hle] int sceKernelAtomicGetAndAnd16(uint p, int v) => (int)Rmw(p, 2, o => o & v).Old;
    [Hle] int sceKernelAtomicGetAndOr16(uint p, int v) => (int)Rmw(p, 2, o => o | v).Old;
    [Hle] int sceKernelAtomicGetAndXor16(uint p, int v) => (int)Rmw(p, 2, o => o ^ v).Old;
    [Hle] int sceKernelAtomicGetAndClear16(uint p, int v) => (int)Rmw(p, 2, o => o & ~v).Old;
    [Hle] int sceKernelAtomicGetAndSet16(uint p, int v) => (int)Rmw(p, 2, o => v).Old;
    [Hle] void sceKernelAtomicSet16(uint p, int v) => Rmw(p, 2, o => v);
    [Hle] void sceKernelAtomicClearMask16(uint p, int v) => Rmw(p, 2, o => o & ~v);
    [Hle] int sceKernelAtomicCompareAndSet16(uint p, int v, int nv) => (int)Rmw(p, 2, o => o == Narrow(v, 2) ? nv : null).Old;
    [Hle] int sceKernelAtomicAddUnless16(uint p, int v, int limit) => AddUnless(p, 2, v, limit);
    [Hle] int sceKernelAtomicDecIfPositive16(uint p) => (int)Rmw(p, 2, o => o >= 0 ? o - 1 : null).Old;
    // ---- 32-bit
    [Hle] int sceKernelAtomicAddAndGet32(uint p, int v) => (int)Rmw(p, 4, o => o + v).New;
    [Hle] int sceKernelAtomicSubAndGet32(uint p, int v) => (int)Rmw(p, 4, o => o - v).New;
    [Hle] int sceKernelAtomicAndAndGet32(uint p, int v) => (int)Rmw(p, 4, o => o & v).New;
    [Hle] int sceKernelAtomicOrAndGet32(uint p, int v) => (int)Rmw(p, 4, o => o | v).New;
    [Hle] int sceKernelAtomicXorAndGet32(uint p, int v) => (int)Rmw(p, 4, o => o ^ v).New;
    [Hle] int sceKernelAtomicClearAndGet32(uint p, int v) => (int)Rmw(p, 4, o => o & ~v).New;
    [Hle] int sceKernelAtomicGetAndAdd32(uint p, int v) => (int)Rmw(p, 4, o => o + v).Old;
    [Hle] int sceKernelAtomicGetAndSub32(uint p, int v) => (int)Rmw(p, 4, o => o - v).Old;
    [Hle] int sceKernelAtomicGetAndAnd32(uint p, int v) => (int)Rmw(p, 4, o => o & v).Old;
    [Hle] int sceKernelAtomicGetAndOr32(uint p, int v) => (int)Rmw(p, 4, o => o | v).Old;
    [Hle] int sceKernelAtomicGetAndXor32(uint p, int v) => (int)Rmw(p, 4, o => o ^ v).Old;
    [Hle] int sceKernelAtomicGetAndClear32(uint p, int v) => (int)Rmw(p, 4, o => o & ~v).Old;
    [Hle] int sceKernelAtomicGetAndSet32(uint p, int v) => (int)Rmw(p, 4, o => v).Old;
    [Hle] void sceKernelAtomicSet32(uint p, int v) => Rmw(p, 4, o => v);
    [Hle] void sceKernelAtomicClearMask32(uint p, int v) => Rmw(p, 4, o => o & ~v);
    [Hle] int sceKernelAtomicCompareAndSet32(uint p, int v, int nv) => (int)Rmw(p, 4, o => o == Narrow(v, 4) ? nv : null).Old;
    [Hle] int sceKernelAtomicAddUnless32(uint p, int v, int limit) => AddUnless(p, 4, v, limit);
    [Hle] int sceKernelAtomicDecIfPositive32(uint p) => (int)Rmw(p, 4, o => o > 0 ? o - 1 : null).Old;
    // ---- 64-bit
    [Hle] long sceKernelAtomicAddAndGet64(uint p, long v) => (long)Rmw(p, 8, o => o + v).New;
    [Hle] long sceKernelAtomicSubAndGet64(uint p, long v) => (long)Rmw(p, 8, o => o - v).New;
    [Hle] long sceKernelAtomicAndAndGet64(uint p, long v) => (long)Rmw(p, 8, o => o & v).New;
    [Hle] long sceKernelAtomicOrAndGet64(uint p, long v) => (long)Rmw(p, 8, o => o | v).New;
    [Hle] long sceKernelAtomicXorAndGet64(uint p, long v) => (long)Rmw(p, 8, o => o ^ v).New;
    [Hle] long sceKernelAtomicClearAndGet64(uint p, long v) => (long)Rmw(p, 8, o => o & ~v).New;
    [Hle] long sceKernelAtomicGetAndAdd64(uint p, long v) => (long)Rmw(p, 8, o => o + v).Old;
    [Hle] long sceKernelAtomicGetAndSub64(uint p, long v) => (long)Rmw(p, 8, o => o - v).Old;
    [Hle] long sceKernelAtomicGetAndAnd64(uint p, long v) => (long)Rmw(p, 8, o => o & v).Old;
    [Hle] long sceKernelAtomicGetAndOr64(uint p, long v) => (long)Rmw(p, 8, o => o | v).Old;
    [Hle] long sceKernelAtomicGetAndXor64(uint p, long v) => (long)Rmw(p, 8, o => o ^ v).Old;
    [Hle] long sceKernelAtomicGetAndClear64(uint p, long v) => (long)Rmw(p, 8, o => o & ~v).Old;
    [Hle] long sceKernelAtomicGetAndSet64(uint p, long v) => (long)Rmw(p, 8, o => v).Old;
    [Hle] void sceKernelAtomicSet64(uint p, long v) => Rmw(p, 8, o => v);
    [Hle] void sceKernelAtomicClearMask64(uint p, long v) => Rmw(p, 8, o => o & ~v);
    [Hle] long sceKernelAtomicCompareAndSet64(uint p, long v, long nv) => (long)Rmw(p, 8, o => o == Narrow(v, 8) ? nv : null).Old;
    [Hle] int sceKernelAtomicAddUnless64(uint p, long v, long limit) => AddUnless(p, 8, v, limit);
    [Hle] long sceKernelAtomicDecIfPositive64(uint p) => (long)Rmw(p, 8, o => o > 0 ? o - 1 : null).Old;
}
