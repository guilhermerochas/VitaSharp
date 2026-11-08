using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

// SceLibstdcxx's <atomic> support of libc.suprx 3.74 beyond the 4-byte forms (Libc.Cxx.cs): the 1-, 2- and 8-byte
// operations (ldrex/strex loops in the module; the memory order argument only picks barriers, and host atomics are
// sequentially consistent), the atomic_flag, and the generic operations on objects of any size, which the module
// serialises with a caller-supplied atomic_flag spinlock (test_and_set until clear, then clear).
public sealed partial class Libc
{
    /// <summary>A CAS loop on the aligned word holding the 1- or 2-byte cell at <paramref name="p"/>: the new cell value
    /// from the old; returns the old value.</summary>
    static uint SubwordRmw(uint p, int bytes, Func<uint, uint> op)
    {
        uint word = p & ~3u;
        int shift = (int)(p & 3) * 8;
        uint mask = (bytes == 1 ? 0xFFu : 0xFFFFu) << shift;
        ref int cell = ref GuestMemory.Ref<int>(word);
        while (true)
        {
            uint w = (uint)Volatile.Read(ref cell);
            uint old = (w & mask) >> shift;
            uint nw = (w & ~mask) | ((op(old) << shift) & mask);
            if (Interlocked.CompareExchange(ref cell, (int)nw, (int)w) == (int)w) return old;
        }
    }

    static uint Load(uint p, int bytes) => bytes == 1 ? Volatile.Read(ref GuestMemory.Ref<byte>(p)) : Volatile.Read(ref GuestMemory.Ref<ushort>(p));

    // ------------------------------------------------------------------ 1 and 2 bytes

    [Hle] uint _Atomic_load_1(uint p, int order) => Load(p, 1);
    [Hle] uint _Atomic_load_2(uint p, int order) => Load(p, 2);
    [Hle] void _Atomic_store_1(uint p, uint v, int order) => SubwordRmw(p, 1, _ => v);
    [Hle] void _Atomic_store_2(uint p, uint v, int order) => SubwordRmw(p, 2, _ => v);
    [Hle] uint _Atomic_exchange_1(uint p, uint v, int order) => SubwordRmw(p, 1, _ => v);
    [Hle] uint _Atomic_exchange_2(uint p, uint v, int order) => SubwordRmw(p, 2, _ => v);
    [Hle] uint _Atomic_fetch_add_1(uint p, uint v, int order) => SubwordRmw(p, 1, o => o + v);
    [Hle] uint _Atomic_fetch_add_2(uint p, uint v, int order) => SubwordRmw(p, 2, o => o + v);
    [Hle] uint _Atomic_fetch_sub_1(uint p, uint v, int order) => SubwordRmw(p, 1, o => o - v);
    [Hle] uint _Atomic_fetch_sub_2(uint p, uint v, int order) => SubwordRmw(p, 2, o => o - v);
    [Hle] uint _Atomic_fetch_and_1(uint p, uint v, int order) => SubwordRmw(p, 1, o => o & v);
    [Hle] uint _Atomic_fetch_and_2(uint p, uint v, int order) => SubwordRmw(p, 2, o => o & v);
    [Hle] uint _Atomic_fetch_or_1(uint p, uint v, int order) => SubwordRmw(p, 1, o => o | v);
    [Hle] uint _Atomic_fetch_or_2(uint p, uint v, int order) => SubwordRmw(p, 2, o => o | v);
    [Hle] uint _Atomic_fetch_xor_1(uint p, uint v, int order) => SubwordRmw(p, 1, o => o ^ v);
    [Hle] uint _Atomic_fetch_xor_2(uint p, uint v, int order) => SubwordRmw(p, 2, o => o ^ v);

    /// <summary>(p, expected*, desired, success order, failure order): 1 and stored when *p == *expected, else 0 with
    /// *expected = *p.</summary>
    int SubwordCas(uint p, int bytes, uint expected, uint desired)
    {
        uint mask = bytes == 1 ? 0xFFu : 0xFFFFu;
        uint exp = (bytes == 1 ? GuestMemory.Read<byte>(expected) : GuestMemory.Read<ushort>(expected)) & mask;
        bool swapped = false;
        uint old = SubwordRmw(p, bytes, o => (swapped = o == exp) ? desired & mask : o);
        if (swapped) return 1;
        if (bytes == 1) GuestMemory.Write(expected, (byte)old); else GuestMemory.Write(expected, (ushort)old);
        return 0;
    }

    [Hle("_Atomic_compare_exchange_strong_1")] [Hle("_Atomic_compare_exchange_weak_1")]
    int _Atomic_compare_exchange_strong_1(uint p, uint expected, uint desired, int success, int failure) => SubwordCas(p, 1, expected, desired);

    [Hle("_Atomic_compare_exchange_strong_2")] [Hle("_Atomic_compare_exchange_weak_2")]
    int _Atomic_compare_exchange_strong_2(uint p, uint expected, uint desired, int success, int failure) => SubwordCas(p, 2, expected, desired);

    // ------------------------------------------------------------------ 8 bytes (value in r2:r3, the order on the stack)

    static ref long Cell8(uint p) => ref GuestMemory.Ref<long>(p);

    [Hle] long _Atomic_load_8(uint p, int order) => Interlocked.Read(ref Cell8(p));
    [Hle] void _Atomic_store_8(uint p, long v, int order) => Interlocked.Exchange(ref Cell8(p), v);
    [Hle] long _Atomic_exchange_8(uint p, long v, int order) => Interlocked.Exchange(ref Cell8(p), v);
    [Hle] long _Atomic_fetch_add_8(uint p, long v, int order) => Interlocked.Add(ref Cell8(p), v) - v;
    [Hle] long _Atomic_fetch_sub_8(uint p, long v, int order) => Interlocked.Add(ref Cell8(p), -v) + v;
    [Hle] long _Atomic_fetch_and_8(uint p, long v, int order) => Interlocked.And(ref Cell8(p), v);
    [Hle] long _Atomic_fetch_or_8(uint p, long v, int order) => Interlocked.Or(ref Cell8(p), v);

    [Hle]
    long _Atomic_fetch_xor_8(uint p, long v, int order)
    {
        long old;
        do old = Interlocked.Read(ref Cell8(p)); while (Interlocked.CompareExchange(ref Cell8(p), old ^ v, old) != old);
        return old;
    }

    [Hle("_Atomic_compare_exchange_strong_8")] [Hle("_Atomic_compare_exchange_weak_8")]
    int _Atomic_compare_exchange_strong_8(uint p, uint expected, long desired, int success, int failure)
    {
        long exp = GuestMemory.Read<long>(expected);
        long old = Interlocked.CompareExchange(ref Cell8(p), desired, exp);
        if (old == exp) return 1;
        GuestMemory.Write(expected, old);
        return 0;
    }

    // ------------------------------------------------------------------ atomic_flag and the generic operations

    /// <summary>_Atomic_flag_test_and_set(flag, order): the byte set to 1, its old value returned.</summary>
    [Hle] uint _Atomic_flag_test_and_set(uint flag, int order) => SubwordRmw(flag, 1, _ => 1);
    [Hle] void _Atomic_flag_clear(uint flag, int order) => SubwordRmw(flag, 1, _ => 0);

    /// <summary>A fence (dmb unless the order is relaxed): nothing to do on the host.</summary>
    [Hle] void _Atomic_thread_fence(int order) => Thread.MemoryBarrier();

    void FlagLock(uint flag) { while (SubwordRmw(flag, 1, _ => 1) != 0) Thread.SpinWait(16); }
    void FlagUnlock(uint flag) => SubwordRmw(flag, 1, _ => 0);

    /// <summary>_Atomic_copy(flag, size, dst, src): memcpy under the flag.</summary>
    [Hle]
    void _Atomic_copy(uint flag, uint size, uint dst, uint src)
    {
        FlagLock(flag);
        if (size != 0) GuestMemory.Copy(dst, src, size);
        FlagUnlock(flag);
    }

    /// <summary>_Atomic_exchange(flag, size, a, b): the two objects' bytes swapped under the flag.</summary>
    [Hle]
    void _Atomic_exchange(uint flag, uint size, uint a, uint b)
    {
        FlagLock(flag);
        for (uint i = 0; i < size; i++)
        {
            byte x = GuestMemory.Read<byte>(a + i), y = GuestMemory.Read<byte>(b + i);
            GuestMemory.Write(a + i, y);
            GuestMemory.Write(b + i, x);
        }
        FlagUnlock(flag);
    }

    /// <summary>_Atomic_compare_exchange_strong/weak(flag, size, obj, expected, desired): under the flag, desired is
    /// copied into obj when obj equals expected byte for byte (1), else obj is copied into expected (0).</summary>
    [Hle("_Atomic_compare_exchange_strong")]
    [Hle("_Atomic_compare_exchange_weak")]
    int _Atomic_compare_exchange_strong(uint flag, uint size, uint obj, uint expected, uint desired)
    {
        FlagLock(flag);
        int r = memcmp(obj, expected, size) == 0 ? 1 : 0;
        if (r == 1) { if (size != 0) GuestMemory.Copy(obj, desired, size); }
        else if (size != 0) GuestMemory.Copy(expected, obj, size);
        FlagUnlock(flag);
        return r;
    }
}
