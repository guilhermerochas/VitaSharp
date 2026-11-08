using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

// SceLibc heap: malloc and friends over the process heap, mspace_* over caller blocks. libc.suprx (3.74) runs a
// dlmalloc mspace ("SceLibcMalloc") in memory blocks it allocates as it grows, bounded by the heap size of the
// process parameters (sceLibcHeapSize, unlimited unless the application sets it; sceLibcHeapExtendedAlloc lets it grow
// past it). Here the process heap is a list of SceClib mspaces in "SceLibcMalloc" blocks, made on demand.
public sealed partial class Libc
{
    const uint HeapChunk = 1u << 20, HeapAlign = 8;

    readonly List<uint> _heapBlocks = new();
    readonly object _heapLock = new();
    long _heapLimit = -2; // -2 = not read yet, -1 = unlimited
    long _heapReserved;

    /// <summary>The heap limit from __sce_libcparam (+8 → sceLibcHeapSize, +0x10 → sceLibcHeapExtendedAlloc).</summary>
    long HeapLimit()
    {
        if (_heapLimit != -2) return _heapLimit;
        _heapLimit = -1;
        uint pp = Emu.Linker.ProcessParam;
        uint lp = pp != 0 && GuestMemory.R32(pp) >= 0x30 ? GuestMemory.R32(pp + 0x2C) : 0;
        if (lp != 0)
        {
            uint size = GuestMemory.R32(lp);
            uint heapSizePtr = size >= 0x0C ? GuestMemory.R32(lp + 8) : 0;
            uint extendedPtr = size >= 0x14 ? GuestMemory.R32(lp + 0x10) : 0;
            bool extended = extendedPtr != 0 && GuestMemory.R32(extendedPtr) != 0;
            if (heapSizePtr != 0 && !extended) _heapLimit = GuestMemory.R32(heapSizePtr);
        }
        return _heapLimit;
    }

    /// <summary>An allocation from the process heap (0 when it cannot grow).</summary>
    uint HeapAlloc(uint size, uint align)
    {
        lock (_heapLock)
        {
            foreach (var h in _heapBlocks)
            {
                uint p = ClibM.MspaceMemalign(h, align, size);
                if (p != 0) return p;
            }
            uint chunk = Math.Max(HeapChunk, GuestMemory.AlignUp(size + align + 0x100, 0x10000));
            long limit = HeapLimit();
            if (limit >= 0 && _heapReserved + chunk > limit)
            {
                if (_heapReserved >= limit || size + align + 0x100 > limit - _heapReserved) return 0;
                chunk = (uint)(limit - _heapReserved);
            }
            uint block;
            try { block = Mem.Alloc(chunk, "SceLibcMalloc", MemoryRegion.User, 0x1000); }
            catch (Exception e) when (e is OutOfMemoryException or InvalidOperationException) { return 0; }
            if (block == 0) return 0;
            uint ms = ClibM.sceClibMspaceCreate(block, chunk);
            if (ms == 0) return 0;
            _heapBlocks.Add(ms);
            _heapReserved += chunk;
            return ClibM.MspaceMemalign(ms, align, size);
        }
    }

    [Hle]
    uint malloc(uint size)
    {
        uint p = HeapAlloc(size, HeapAlign);
        if (p == 0) SetErrno(ENOMEM);
        return p;
    }

    [Hle]
    uint memalign(uint align, uint size)
    {
        uint p = HeapAlloc(size, Math.Max(align, HeapAlign));
        if (p == 0) SetErrno(ENOMEM);
        return p;
    }

    [Hle]
    uint calloc(uint n, uint size)
    {
        ulong total = (ulong)n * size;
        if (total > uint.MaxValue) { SetErrno(ENOMEM); return 0; }
        uint p = malloc((uint)total);
        if (p != 0) GuestMemory.Fill(p, 0, (uint)total);
        return p;
    }

    [Hle] void free(uint p) { if (p != 0) ClibM.sceClibMspaceFree(0, p); }

    [Hle] uint realloc(uint p, uint size) => Reallocate(p, size, HeapAlign);
    [Hle] uint reallocalign(uint p, uint size, uint align) => Reallocate(p, size, Math.Max(align, HeapAlign));

    uint Reallocate(uint p, uint size, uint align)
    {
        if (p == 0) return HeapAlloc(size, align) is var n and not 0 ? n : Fail();
        if (size == 0) { free(p); return 0; }
        uint old = ClibM.UsableSize(p);
        uint q = HeapAlloc(size, align);
        if (q == 0) return Fail();
        GuestMemory.Copy(q, p, Math.Min(old, size));
        free(p);
        return q;

        static uint Fail() { SetErrno(ENOMEM); return 0; }
    }

    [Hle] uint malloc_usable_size(uint p) => p == 0 ? 0 : ClibM.UsableSize(p);

    /// <summary>malloc_stats(SceLibcMallocManagedSize*): sizes summed over the heap's blocks (max/current system
    /// size, max/current in use).</summary>
    [Hle("malloc_stats")]
    [Hle("malloc_stats_fast")]
    int malloc_stats(uint stats)
    {
        if (stats == 0) return EINVAL;
        uint sys = 0, peak = 0, inUse = 0;
        lock (_heapLock)
            foreach (var h in _heapBlocks)
            {
                var (s, pk, u) = ClibM.HeapStats(h);
                sys += s; peak += pk; inUse += u;
            }
        GuestMemory.W32(stats, sys);
        GuestMemory.W32(stats + 4, sys);
        GuestMemory.W32(stats + 8, peak);
        GuestMemory.W32(stats + 12, inUse);
        return 0;
    }

    [Hle] void sceLibcSetHeapInitError(int error) => _heapInitError = error;
    int _heapInitError;

    // mspace_*: the same heaps as sceClibMspace* (block handle = block address)
    [Hle] uint mspace_create(uint block, uint size) => ClibM.sceClibMspaceCreate(block, size);
    /// <summary>mspace_create_with_flag(block, size, flags): flag bit 3 = thread-safe; any other bit is refused (NULL).</summary>
    [Hle] uint mspace_create_with_flag(uint block, uint size, uint flags) => (flags & ~8u) != 0 ? 0 : ClibM.sceClibMspaceCreate(block, size);
    [Hle] uint mspace_create_internal(uint block, uint size, uint flags) => ClibM.sceClibMspaceCreate(block, size);
    [Hle] uint mspace_destroy(uint ms) { ClibM.sceClibMspaceDestroy(ms); return 0; }
    [Hle] uint mspace_malloc(uint ms, uint size) => ClibM.sceClibMspaceMalloc(ms, size);
    [Hle] uint mspace_memalign(uint ms, uint align, uint size) => ClibM.MspaceMemalign(ms, align, size);
    [Hle] uint mspace_calloc(uint ms, uint n, uint size) => ClibM.MspaceCalloc(ms, n, size);
    [Hle] uint mspace_realloc(uint ms, uint p, uint size) => ClibM.sceClibMspaceRealloc(ms, p, size);
    [Hle] uint mspace_reallocalign(uint ms, uint p, uint size, uint align) => ClibM.MspaceReallocalign(ms, p, size, align);
    [Hle] void mspace_free(uint ms, uint p) => ClibM.sceClibMspaceFree(ms, p);
    [Hle] uint mspace_malloc_usable_size(uint p) => ClibM.UsableSize(p);
    [Hle] int mspace_is_heap_empty(uint ms) => ClibM.IsHeapEmpty(ms);

    [Hle("mspace_malloc_stats")]
    [Hle("mspace_malloc_stats_fast")]
    int mspace_malloc_stats(uint ms, uint stats)
    {
        if (stats == 0) return EINVAL;
        var (s, peak, inUse) = ClibM.HeapStats(ms);
        GuestMemory.W32(stats, s);
        GuestMemory.W32(stats + 4, s);
        GuestMemory.W32(stats + 8, peak);
        GuestMemory.W32(stats + 12, inUse);
        return 0;
    }
}
