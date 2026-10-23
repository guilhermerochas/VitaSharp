using System.Runtime.CompilerServices;
using System.Text;

namespace VitaSharp.Core.Memory;

public enum MemoryRegion
{
    /// <summary>Main LPDDR2 user memory (eboot, heaps, stacks, user memblocks).</summary>
    User,
    /// <summary>CDRAM (video memory) memblocks.</summary>
    Cdram,
    /// <summary>Emulator-internal allocations (HLE trampolines, kernel objects visible to the guest).</summary>
    Kernel,
    /// <summary>A fixed range outside the three allocator regions (<see cref="GuestMemory.MapFixed"/>: tests and tools).</summary>
    Fixed,
}

/// <summary>
/// The whole 32-bit guest address space mapped 1:1 onto a host reservation (fastmem):
/// host pointer = Base + guest address. Pages are committed on allocation and, on Windows x64,
/// lazily on first touch through a native vectored exception handler.
/// </summary>
public sealed unsafe class GuestMemory : IDisposable
{
    public const uint PageSize = 0x1000;
    const ulong ReserveSize = 0x1_0001_0000; // 4 GiB + slack so 16-byte accesses at the top don't fault
    /// <summary>
    /// The page dirty table (<see cref="DirtyPages"/>) sits right below guest address 0, inside the same reservation,
    /// so native code reaches it from the guest base register with a 32-bit displacement.
    /// </summary>
    const ulong DirtyTableBytes = DirtyPages.TableBytes;

    /// <summary>Host address of guest address 0. Static so JIT-compiled code can embed it.</summary>
    public static byte* Base { get; private set; }

    readonly bool _lazy;
    readonly object _lock = new();
    readonly Dictionary<uint, Block> _blocks = new();
    readonly SortedList<uint, uint> _freeUser = new();   // start -> size
    readonly SortedList<uint, uint> _freeCdram = new();
    readonly SortedList<uint, uint> _freeKernel = new();

    public sealed record Block(uint Address, uint Size, string Name, MemoryRegion Region);

    /// <summary>The process-wide address space (debug tools).</summary>
    public static GuestMemory? Instance { get; private set; }

    /// <summary>Snapshot of the allocated blocks (debug tools).</summary>
    public List<Block> Blocks() { lock (_lock) return _blocks.Values.OrderBy(b => b.Address).ToList(); }

    public const uint UserStart = 0x8100_0000, UserEnd = 0xE000_0000;
    public const uint CdramStart = 0x6000_0000, CdramEnd = 0x7000_0000;
    public const uint KernelStart = 0x4000_0000, KernelEnd = 0x5000_0000;

    public long CommittedBytes { get; private set; }

    public GuestMemory()
    {
        if (Base != null) throw new InvalidOperationException("Only one guest address space per process");
        byte* reservation = HostVirtualMemory.Reserve(unchecked((nuint)(ReserveSize + DirtyTableBytes)), out _lazy);
        HostVirtualMemory.Commit(reservation, (nuint)DirtyTableBytes);
        Base = reservation + DirtyTableBytes;
        Instance = this;
        if (!_lazy) _lazy = HostVirtualMemory.InstallLazyCommitHandler(Base, ReserveSize);
        _freeUser.Add(UserStart, UserEnd - UserStart);
        _freeCdram.Add(CdramStart, CdramEnd - CdramStart);
        _freeKernel.Add(KernelStart, KernelEnd - KernelStart);
    }

    // ---------------- raw access ----------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte* Ptr(uint addr) => Base + addr;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T Read<T>(uint addr) where T : unmanaged => Unsafe.ReadUnaligned<T>(Base + addr);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Write<T>(uint addr, T value) where T : unmanaged
    {
        Unsafe.WriteUnaligned(Base + addr, value);
        DirtyPages.Mark(addr); // after the store (see DirtyPages); a store crossing into the next page is covered by the sweep
    }

    /// <summary>Writable reference: the pages are marked written (callers may store through it).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ref T Ref<T>(uint addr) where T : unmanaged
    {
        DirtyPages.MarkRange(addr, (uint)sizeof(T));
        return ref Unsafe.AsRef<T>(Base + addr);
    }

    public static uint R32(uint a) => Read<uint>(a);
    public static void W32(uint a, uint v) => Write(a, v);

    /// <summary>
    /// Writable view of guest memory. The range is marked written (conservatively: the caller may store through the
    /// span), so code that only reads must use <see cref="ReadSpan(uint, int)"/>.
    /// </summary>
    public static Span<byte> Span(uint addr, int length)
    {
        DirtyPages.MarkRange(addr, (uint)length);
        return new(Base + addr, length);
    }

    /// <inheritdoc cref="Span(uint, int)"/>
    public static Span<T> Span<T>(uint addr, int count) where T : unmanaged
    {
        DirtyPages.MarkRange(addr, (uint)((long)count * sizeof(T)));
        return new(Base + addr, count);
    }

    /// <summary>Read-only view of guest memory (the pages are not marked written).</summary>
    public static ReadOnlySpan<byte> ReadSpan(uint addr, int length)
    {
        if (Gpu.GpuDumpRecorder.TapEnabled) Gpu.GpuDumpRecorder.Tap(addr, length); // GPU dump recording (one flag check when off)
        return new(Base + addr, length);
    }
    /// <inheritdoc cref="ReadSpan(uint, int)"/>
    public static ReadOnlySpan<T> ReadSpan<T>(uint addr, int count) where T : unmanaged
    {
        if (Gpu.GpuDumpRecorder.TapEnabled) Gpu.GpuDumpRecorder.Tap(addr, (long)count * sizeof(T));
        return new(Base + addr, count);
    }

    public static string ReadCString(uint addr, int max = 4096)
    {
        if (addr == 0) return "";
        var p = Base + addr;
        int n = 0;
        while (n < max && p[n] != 0) n++;
        return Encoding.UTF8.GetString(p, n);
    }

    public static void WriteCString(uint addr, string s, int max = int.MaxValue)
    {
        int len = Encoding.UTF8.GetByteCount(s);
        Span<byte> bytes = len <= 256 ? stackalloc byte[256] : new byte[len]; // short names: no garbage
        Encoding.UTF8.GetBytes(s, bytes);
        int n = Math.Min(len, max - 1);
        bytes[..n].CopyTo(new Span<byte>(Base + addr, n));
        Base[addr + (uint)n] = 0;
        DirtyPages.MarkRange(addr, (uint)n + 1);
    }

    public static void Copy(uint dst, uint src, uint len)
    {
        Buffer.MemoryCopy(Base + src, Base + dst, len, len);
        DirtyPages.MarkRange(dst, len);
    }

    public static void Fill(uint dst, byte value, uint len)
    {
        new Span<byte>(Base + dst, (int)len).Fill(value);
        DirtyPages.MarkRange(dst, len);
    }

    // ---------------- allocation ----------------

    SortedList<uint, uint> FreeList(MemoryRegion r) => r switch
    {
        MemoryRegion.Cdram => _freeCdram,
        MemoryRegion.Kernel => _freeKernel,
        _ => _freeUser,
    };

    public uint Alloc(uint size, string name, MemoryRegion region = MemoryRegion.User, uint align = PageSize)
    {
        if (size == 0) size = 1;
        size = AlignUp(size, PageSize);
        align = Math.Max(align, PageSize);
        lock (_lock)
        {
            var free = FreeList(region);
            for (int i = 0; i < free.Count; i++)
            {
                uint start = free.Keys[i], len = free.Values[i];
                uint aligned = AlignUp(start, align);
                if (aligned < start || aligned - start + (ulong)size > len) continue;
                Carve(free, start, len, aligned, size);
                return Commit(aligned, size, name, region);
            }
        }
        throw new OutOfMemoryException($"Guest {region} memory exhausted allocating 0x{size:X} for {name}");
    }

    /// <summary>Allocate at a fixed address. Returns 0 when the range is not free.</summary>
    public uint TryAllocAt(uint addr, uint size, string name)
    {
        uint start = addr & ~(PageSize - 1);
        uint end = AlignUp(addr + size, PageSize);
        lock (_lock)
        {
            foreach (var region in new[] { MemoryRegion.User, MemoryRegion.Cdram, MemoryRegion.Kernel })
            {
                var free = FreeList(region);
                for (int i = 0; i < free.Count; i++)
                {
                    uint fs = free.Keys[i], fl = free.Values[i];
                    if (start >= fs && (ulong)end <= (ulong)fs + fl)
                    {
                        Carve(free, fs, fl, start, end - start);
                        Commit(start, end - start, name, region);
                        return addr;
                    }
                }
            }
        }
        return 0;
    }

    /// <summary>Maps a fixed range that lies outside the user/CDRAM/kernel regions (CPU tests place code at low
    /// addresses). Returns false when it overlaps a region or an existing block.</summary>
    public bool MapFixed(uint addr, uint size, string name)
    {
        uint start = addr & ~(PageSize - 1), end = AlignUp(addr + size, PageSize);
        static bool Overlaps(uint s, uint e, uint rs, uint re) => s < re && rs < e;
        if (Overlaps(start, end, UserStart, UserEnd) || Overlaps(start, end, CdramStart, CdramEnd) || Overlaps(start, end, KernelStart, KernelEnd))
            return false;
        lock (_lock)
        {
            foreach (var b in _blocks.Values)
                if (Overlaps(start, end, b.Address, b.Address + b.Size)) return false;
            Commit(start, end - start, name, MemoryRegion.Fixed);
        }
        return true;
    }

    static void Carve(SortedList<uint, uint> free, uint fs, uint fl, uint start, uint size)
    {
        free.Remove(fs);
        if (start > fs) free.Add(fs, start - fs);
        ulong tail = (ulong)fs + fl - ((ulong)start + size);
        if (tail > 0) free.Add(start + size, (uint)tail);
    }

    uint Commit(uint addr, uint size, string name, MemoryRegion region)
    {
        HostVirtualMemory.Commit(Base + addr, size);
        SetMapped(addr, size, true);
        new Span<byte>(Base + addr, (int)size).Clear();
        DirtyPages.MarkRange(addr, size);
        _blocks[addr] = new Block(addr, size, name, region);
        CommittedBytes += size;
        return addr;
    }

    /// <summary>Frees every block (process exec: the address space is reused for a new executable).</summary>
    public void FreeAll()
    {
        uint[] addrs;
        lock (_lock) addrs = _blocks.Keys.ToArray();
        foreach (var a in addrs) Free(a);
    }

    public void Free(uint addr)
    {
        lock (_lock)
        {
            if (!_blocks.Remove(addr, out var b)) return;
            CommittedBytes -= b.Size;
            HostVirtualMemory.Decommit(Base + b.Address, b.Size);
            SetMapped(b.Address, b.Size, false);
            DirtyPages.MarkRange(b.Address, b.Size);
            if (b.Region == MemoryRegion.Fixed) return;
            var free = FreeList(b.Region);
            uint start = b.Address, size = b.Size;
            // merge with neighbours
            int idx = 0;
            while (idx < free.Count && free.Keys[idx] < start) idx++;
            if (idx > 0 && free.Keys[idx - 1] + free.Values[idx - 1] == start)
            {
                start = free.Keys[idx - 1];
                size += free.Values[idx - 1];
                free.RemoveAt(idx - 1);
                idx--;
            }
            if (idx < free.Count && start + size == free.Keys[idx])
            {
                size += free.Values[idx];
                free.RemoveAt(idx);
            }
            free.Add(start, size);
        }
    }

    public Block? FindBlock(uint addr)
    {
        lock (_lock)
        {
            foreach (var b in _blocks.Values)
                if (addr >= b.Address && addr - b.Address < b.Size) return b;
        }
        return null;
    }

    // One bit per 4 KB page: set while the page belongs to an allocated block. Data accesses to other pages still
    // succeed (lazy commit reads zeros), but instruction fetches check it so a jump to a bad address faults instead
    // of sliding through gigabytes of zero words ("andeq r0, r0, r0") and translating each 192-instruction block.
    static readonly ulong[] s_mappedPages = new ulong[(1 << 20) / 64];

    static void SetMapped(uint addr, uint size, bool mapped)
    {
        for (ulong p = addr / PageSize, end = ((ulong)addr + size + PageSize - 1) / PageSize; p < end; p++)
        {
            ref ulong w = ref s_mappedPages[p >> 6];
            if (mapped) w |= 1UL << (int)(p & 63); else w &= ~(1UL << (int)(p & 63));
        }
    }

    /// <summary>True when <paramref name="addr"/> lies in an allocated block (module segment, memblock, stack, ...).</summary>
    public static bool IsMapped(uint addr) => (Volatile.Read(ref s_mappedPages[addr >> 18]) & (1UL << (int)((addr >> 12) & 63))) != 0;

    public static uint AlignUp(uint v, uint a) => (v + a - 1) & ~(a - 1);

    public void Dispose()
    {
        if (Base != null) HostVirtualMemory.Release(Base - DirtyTableBytes, unchecked((nuint)(ReserveSize + DirtyTableBytes)));
        Base = null;
        Array.Clear(s_mappedPages);
    }
}
