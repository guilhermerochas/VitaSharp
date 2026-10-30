using System.Runtime.CompilerServices;
using System.Text;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Memory;

public enum MemoryRegion
{
    /// <summary>Main LPDDR2 user memory (eboot, heaps, stacks, user memblocks): private to the process's address space.</summary>
    User,
    /// <summary>CDRAM (video memory) memblocks: shared by every address space.</summary>
    Cdram,
    /// <summary>Emulator-internal allocations (HLE trampolines, kernel objects visible to the guest): shared by every
    /// address space.</summary>
    Kernel,
    /// <summary>A fixed range outside the three allocator regions (<see cref="GuestMemory.MapFixed"/>: tests and tools).</summary>
    Fixed,
}

/// <summary>
/// Guest memory of the emulated system: the allocator of the three regions (user, CDRAM, kernel) and the address spaces of
/// the processes (<see cref="AddressSpace"/>), each mapping the 32-bit guest address space 1:1 onto a host reservation
/// (fastmem: host pointer = space base + guest address). Pages are committed on allocation and, on Windows, lazily on
/// first touch through a native vectored exception handler.
/// <para>
/// The CDRAM and kernel regions are shared: one host memory mapped into every space, one allocator. User memory is
/// private: a block exists only in the space of the process that allocated it. Relocatable allocations still take
/// ranges no space uses (so a guest address names the same data for the GPU's caches whichever process submitted it);
/// only executables linked at fixed addresses (<see cref="TryAllocAt"/> with exclusive = false) share addresses with
/// other processes' blocks, in their own space.
/// </para>
/// The static accessors read and write the current host thread's space (<see cref="AddressSpace.Current"/>).
/// </summary>
public sealed unsafe class GuestMemory : IDisposable
{
    public const uint PageSize = 0x1000;
    internal const ulong ReserveSize = 0x1_0001_0000; // 4 GiB + slack so 16-byte accesses at the top don't fault
    /// <summary>
    /// The page dirty table (<see cref="DirtyPages"/>) sits right below guest address 0, inside the same reservation,
    /// so native code reaches it from the guest base register with a 32-bit displacement.
    /// </summary>
    const ulong DirtyTableBytes = DirtyPages.TableBytes;

    // the current thread's space base (null = the first process's): read by every static accessor
    [ThreadStatic] internal static byte* t_base;
    static byte* s_primaryBase;

    /// <summary>Host address of guest address 0 in the calling thread's address space.</summary>
    public static byte* Base
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get { byte* b = t_base; return b != null ? b : s_primaryBase; }
    }

    /// <summary>The first process's address space (also the only one when <see cref="SeparateSpaces"/> is false).</summary>
    public AddressSpace Primary { get; }

    /// <summary>True when every process gets its own address space (<see cref="CreateSpace"/>); false when the host cannot
    /// map shared memory twice or VITASHARP_SHARED_ADDRESS_SPACE=1: then all processes share <see cref="Primary"/>.</summary>
    public bool SeparateSpaces { get; }

    readonly object _lock = new();
    readonly nint _kernelMemory, _cdramMemory;
    readonly Dictionary<uint, Block> _shared = new();   // CDRAM and kernel blocks (in every space)
    readonly SortedList<uint, uint> _freeUser = new();   // start -> size: ranges no space uses
    readonly SortedList<uint, uint> _freeCdram = new();
    readonly SortedList<uint, uint> _freeKernel = new();
    readonly List<AddressSpace> _spaces = new();
    readonly Stack<AddressSpace> _idle = new();
    AddressSpace[] _spaceSnapshot = [];
    readonly ulong[] _sharedMapped = new ulong[(1 << 20) / 64];

    /// <summary>An allocated range; <paramref name="Owner"/> is the process that allocated it (0 = the system),
    /// <paramref name="Space"/> the address space of a private block (null: CDRAM and kernel blocks are in all).</summary>
    public sealed record Block(uint Address, uint Size, string Name, MemoryRegion Region, int Owner = 0, AddressSpace? Space = null);

    /// <summary>The system's guest memory (debug tools).</summary>
    public static GuestMemory? Instance { get; private set; }

    public const uint UserStart = 0x8100_0000, UserEnd = 0xE000_0000;
    public const uint CdramStart = 0x6000_0000, CdramEnd = 0x7000_0000;
    public const uint KernelStart = 0x4000_0000, KernelEnd = 0x5000_0000;

    public long CommittedBytes { get; private set; }

    /// <param name="separateSpaces">Per-process address spaces; null = unless VITASHARP_SHARED_ADDRESS_SPACE=1.</param>
    public GuestMemory(bool? separateSpaces = null)
    {
        if (s_primaryBase != null) throw new InvalidOperationException("Only one guest memory per process");
        bool separate = separateSpaces ?? Environment.GetEnvironmentVariable(EnvVars.SharedAddressSpace) != "1";
        if (separate)
        {
            _kernelMemory = HostVirtualMemory.CreateSharedMemory(KernelEnd - KernelStart, "kernel");
            _cdramMemory = HostVirtualMemory.CreateSharedMemory(CdramEnd - CdramStart, "cdram");
            separate = _kernelMemory != 0 && _cdramMemory != 0;
        }
        var primary = separate ? NewSpace(true) : null;
        if (primary == null)
        {
            separate = false;
            primary = NewSpace(false)!;
        }
        SeparateSpaces = separate;
        Primary = primary;
        Primary.Label = "first process";
        s_primaryBase = Primary.Base;
        Instance = this;
        _freeUser.Add(UserStart, UserEnd - UserStart);
        _freeCdram.Add(CdramStart, CdramEnd - CdramStart);
        _freeKernel.Add(KernelStart, KernelEnd - KernelStart);
        if (!separate && separateSpaces != false && Environment.GetEnvironmentVariable(EnvVars.SharedAddressSpace) != "1")
            Log.Info("Guest memory: the host cannot map shared memory twice; all processes share one address space");
    }

    /// <summary>Reserves a new space; with <paramref name="views"/> the shared regions are views of the system's memory.
    /// Null when that mapping fails.</summary>
    AddressSpace? NewSpace(bool views)
    {
        nuint total = unchecked((nuint)(ReserveSize + DirtyTableBytes));
        byte* reservation;
        bool lazy;
        (nint, nuint, bool)[] parts;
        if (views)
        {
            ReadOnlySpan<HostVirtualMemory.View> v =
            [
                new((nuint)(DirtyTableBytes + KernelStart), KernelEnd - KernelStart, _kernelMemory),
                new((nuint)(DirtyTableBytes + CdramStart), CdramEnd - CdramStart, _cdramMemory),
            ];
            reservation = HostVirtualMemory.ReserveWithViews(total, v, out lazy, out parts);
            if (reservation == null) return null;
        }
        else
        {
            reservation = HostVirtualMemory.Reserve(total, out lazy);
            parts = [((nint)reservation, total, false)];
        }
        HostVirtualMemory.Commit(reservation, (nuint)DirtyTableBytes);
        byte* @base = reservation + DirtyTableBytes;
        if (!lazy) HostVirtualMemory.InstallLazyCommitHandler(@base, ReserveSize);
        var space = new AddressSpace(@base, parts, views);
        _spaces.Add(space);
        _spaceSnapshot = _spaces.ToArray();
        return space;
    }

    /// <summary>
    /// An address space for a new process: a fresh one (or one a process that ended left), with the shared regions
    /// mapped. Without <see cref="SeparateSpaces"/> it is <see cref="Primary"/>.
    /// </summary>
    public AddressSpace CreateSpace(string label)
    {
        if (!SeparateSpaces) return Primary;
        lock (_lock)
        {
            if (_idle.TryPop(out var s)) s.Renew();
            else s = NewSpace(true) ?? throw new OutOfMemoryException("cannot reserve another guest address space");
            s.Label = label;
            Log.Info($"Guest memory: {s} created");
            return s;
        }
    }

    /// <summary>The process using <paramref name="space"/> ended and its blocks were freed: the space is kept for the next
    /// process (reservations are never handed back while the system runs: a straggling host thread may still touch it).</summary>
    public void ReleaseSpace(AddressSpace space)
    {
        if (ReferenceEquals(space, Primary) || !SeparateSpaces) return;
        lock (_lock)
        {
            if (space.Blocks.Count > 0)
            {
                Log.Warn($"Guest memory: {space} still has {space.Blocks.Count} blocks; it is not reused");
                return;
            }
            space.Label = "";
            if (!_idle.Contains(space)) _idle.Push(space);
        }
    }

    /// <summary>Every address space (the dirty-page sweep, fault reports).</summary>
    public ReadOnlySpan<AddressSpace> Spaces => Volatile.Read(ref _spaceSnapshot);

    /// <summary>The space a host address belongs to, or null.</summary>
    public AddressSpace? SpaceOfHost(ulong hostAddress)
    {
        foreach (var s in Spaces) if (s.ContainsHost(hostAddress)) return s;
        return null;
    }

    static AddressSpace CurrentOf(GuestMemory m) => AddressSpace.Current ?? m.Primary;

    // ---------------- raw access (the calling thread's space)

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte* Ptr(uint addr) => Base + addr;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T Read<T>(uint addr) where T : unmanaged => Unsafe.ReadUnaligned<T>(Base + addr);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Write<T>(uint addr, T value) where T : unmanaged
    {
        byte* b = Base;
        Unsafe.WriteUnaligned(b + addr, value);
        DirtyPages.Mark(b, addr); // after the store (see DirtyPages); a store crossing into the next page is covered by the sweep
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
        byte* b = Base;
        bytes[..n].CopyTo(new Span<byte>(b + addr, n));
        b[addr + (uint)n] = 0;
        DirtyPages.MarkRange(addr, (uint)n + 1);
    }

    public static void Copy(uint dst, uint src, uint len)
    {
        byte* b = Base;
        Buffer.MemoryCopy(b + src, b + dst, len, len);
        DirtyPages.MarkRange(dst, len);
    }

    public static void Fill(uint dst, byte value, uint len)
    {
        new Span<byte>(Base + dst, (int)len).Fill(value);
        DirtyPages.MarkRange(dst, len);
    }

    // ---------------- allocation

    SortedList<uint, uint> FreeList(MemoryRegion r) => r switch
    {
        MemoryRegion.Cdram => _freeCdram,
        MemoryRegion.Kernel => _freeKernel,
        _ => _freeUser,
    };

    static bool IsSharedRegion(MemoryRegion r) => r is MemoryRegion.Cdram or MemoryRegion.Kernel;

    /// <summary>Allocates in <paramref name="region"/>: user memory in the calling thread's address space (a range no
    /// other space uses either), CDRAM and kernel memory in all of them.</summary>
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
                return Commit(CurrentOf(this), aligned, size, name, region);
            }
        }
        throw new OutOfMemoryException($"Guest {region} memory exhausted allocating 0x{size:X} for {name}");
    }

    /// <summary>
    /// Allocates at a fixed address. Returns 0 when the range is not free. <paramref name="exclusive"/> = false (an
    /// executable linked at fixed addresses) only needs the range free in the calling thread's address space: other
    /// processes may use the same user addresses in theirs.
    /// </summary>
    public uint TryAllocAt(uint addr, uint size, string name, bool exclusive = true)
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
                        Commit(CurrentOf(this), start, end - start, name, region);
                        return addr;
                    }
                }
            }
            if (exclusive || !SeparateSpaces || start < UserStart || end > UserEnd || end <= start) return 0;
            // the range is used by other processes: it is free when this space has nothing there
            var space = CurrentOf(this);
            foreach (var b in space.Blocks.Values)
                if (start < b.Address + b.Size && b.Address < end) return 0;
            // the parts no space uses leave the shared free list
            for (int i = _freeUser.Count - 1; i >= 0; i--)
            {
                uint fs = _freeUser.Keys[i], fl = _freeUser.Values[i];
                ulong fe = (ulong)fs + fl;
                if (fe <= start || fs >= end) continue;
                uint os = Math.Max(fs, start), oe = (uint)Math.Min(fe, end);
                Carve(_freeUser, fs, fl, os, oe - os);
            }
            Commit(space, start, end - start, name, MemoryRegion.User);
            return addr;
        }
    }

    /// <summary>Maps a fixed range that lies outside the user/CDRAM/kernel regions (CPU tests place code at low
    /// addresses) in the calling thread's space. Returns false when it overlaps a region or an existing block.</summary>
    public bool MapFixed(uint addr, uint size, string name)
    {
        uint start = addr & ~(PageSize - 1), end = AlignUp(addr + size, PageSize);
        static bool Overlaps(uint s, uint e, uint rs, uint re) => s < re && rs < e;
        if (Overlaps(start, end, UserStart, UserEnd) || Overlaps(start, end, CdramStart, CdramEnd) || Overlaps(start, end, KernelStart, KernelEnd))
            return false;
        lock (_lock)
        {
            var space = CurrentOf(this);
            foreach (var b in space.Blocks.Values)
                if (Overlaps(start, end, b.Address, b.Address + b.Size)) return false;
            Commit(space, start, end - start, name, MemoryRegion.Fixed);
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

    uint Commit(AddressSpace space, uint addr, uint size, string name, MemoryRegion region)
    {
        bool shared = IsSharedRegion(region);
        byte* host = space.Base + addr; // a shared region is the same memory in every space
        HostVirtualMemory.Commit(host, size);
        SetMapped(shared ? _sharedMapped : space.MappedPages, addr, size, true);
        new Span<byte>(host, (int)size).Clear();
        DirtyPages.MarkRange(space.Base, addr, size);
        var block = new Block(addr, size, name, region, Kernel.ProcessScope.CurrentPid, shared ? null : space);
        if (shared) _shared[addr] = block;
        else space.Blocks[addr] = block;
        CommittedBytes += size;
        return addr;
    }

    /// <summary>Frees every block (process exec: the address space is reused for a new executable).</summary>
    public void FreeAll()
    {
        Block[] all;
        lock (_lock) all = AllBlocks().ToArray();
        foreach (var b in all) Free(b);
    }

    /// <summary>Frees the blocks process <paramref name="pid"/> allocated (its exit); returns the bytes released.</summary>
    public long FreeOwnedBy(int pid)
    {
        Block[] owned;
        lock (_lock) owned = AllBlocks().Where(b => b.Owner == pid).ToArray();
        foreach (var b in owned) Free(b);
        return owned.Sum(b => (long)b.Size);
    }

    IEnumerable<Block> AllBlocks()
    {
        foreach (var b in _shared.Values) yield return b;
        foreach (var s in _spaces)
            foreach (var b in s.Blocks.Values) yield return b;
    }

    /// <summary>Frees the block at <paramref name="addr"/> of the calling thread's space (or a CDRAM / kernel block).</summary>
    public void Free(uint addr)
    {
        Block? b;
        lock (_lock)
            if (!CurrentOf(this).Blocks.TryGetValue(addr, out b) && !_shared.TryGetValue(addr, out b)) return;
        Free(b);
    }

    void Free(Block b)
    {
        lock (_lock)
        {
            bool shared = b.Space == null;
            if (shared ? !_shared.Remove(b.Address) : !b.Space!.Blocks.Remove(b.Address)) return;
            CommittedBytes -= b.Size;
            var space = b.Space ?? Primary;
            byte* host = space.Base + b.Address;
            if (shared && SeparateSpaces) HostVirtualMemory.Discard(host, b.Size); // a view: its pages stay committed
            else HostVirtualMemory.Decommit(host, b.Size);
            SetMapped(shared ? _sharedMapped : space.MappedPages, b.Address, b.Size, false);
            DirtyPages.MarkRange(space.Base, b.Address, b.Size);
            if (b.Region == MemoryRegion.Fixed) return;
            if (b.Region == MemoryRegion.User && SeparateSpaces) ReturnUserRange(b.Address, b.Size);
            else AddFree(FreeList(b.Region), b.Address, b.Size);
        }
    }

    /// <summary>A private user block was freed: the parts of its range no other space uses become free again.</summary>
    void ReturnUserRange(uint start, uint size)
    {
        ulong end = (ulong)start + size;
        List<(uint S, ulong E)>? used = null;
        foreach (var s in _spaces)
            foreach (var b in s.Blocks.Values)
                if (b.Region == MemoryRegion.User && b.Address < end && (ulong)b.Address + b.Size > start)
                    (used ??= new()).Add((b.Address, (ulong)b.Address + b.Size));
        if (used == null) { AddFree(_freeUser, start, size); return; }
        used.Sort();
        ulong at = start;
        foreach (var (s, e) in used)
        {
            if (s > at) AddFree(_freeUser, (uint)at, (uint)(Math.Min(s, end) - at));
            at = Math.Max(at, e);
            if (at >= end) break;
        }
        if (at < end) AddFree(_freeUser, (uint)at, (uint)(end - at));
    }

    static void AddFree(SortedList<uint, uint> free, uint start, uint size)
    {
        if (size == 0) return;
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

    /// <summary>The block containing <paramref name="addr"/> in the calling thread's space (or in the shared regions).</summary>
    public Block? FindBlock(uint addr)
    {
        lock (_lock)
        {
            foreach (var b in CurrentOf(this).Blocks.Values)
                if (addr >= b.Address && addr - b.Address < b.Size) return b;
            foreach (var b in _shared.Values)
                if (addr >= b.Address && addr - b.Address < b.Size) return b;
        }
        return null;
    }

    /// <summary>Snapshot of the allocated blocks of every space (debug tools).</summary>
    public List<Block> Blocks() { lock (_lock) return AllBlocks().OrderBy(b => b.Address).ToList(); }

    // One bit per 4 KB page: set while the page belongs to an allocated block. Data accesses to other pages still
    // succeed (lazy commit reads zeros), but instruction fetches check it so a jump to a bad address faults instead
    // of sliding through gigabytes of zero words ("andeq r0, r0, r0") and translating each 192-instruction block.
    // The shared regions have one bitmap, every space its own for the rest.
    static void SetMapped(ulong[] bits, uint addr, uint size, bool mapped)
    {
        for (ulong p = addr / PageSize, end = ((ulong)addr + size + PageSize - 1) / PageSize; p < end; p++)
        {
            ref ulong w = ref bits[p >> 6];
            if (mapped) w |= 1UL << (int)(p & 63); else w &= ~(1UL << (int)(p & 63));
        }
    }

    static bool InSharedRegion(uint addr) => addr - KernelStart < KernelEnd - KernelStart || addr - CdramStart < CdramEnd - CdramStart;

    /// <summary>True for CDRAM and kernel addresses: the same memory in every address space.</summary>
    public static bool IsSharedAddress(uint addr) => InSharedRegion(addr);

    /// <summary>True when <paramref name="addr"/> lies in an allocated block (module segment, memblock, stack, ...) of the
    /// calling thread's space.</summary>
    public static bool IsMapped(uint addr)
    {
        var m = Instance;
        if (m == null) return false;
        var bits = InSharedRegion(addr) ? m._sharedMapped : CurrentOf(m).MappedPages;
        return (Volatile.Read(ref bits[addr >> 18]) & (1UL << (int)((addr >> 12) & 63))) != 0;
    }

    public static uint AlignUp(uint v, uint a) => (v + a - 1) & ~(a - 1);

    public void Dispose()
    {
        if (s_primaryBase == null) return;
        lock (_lock)
        {
            foreach (var s in _spaces) HostVirtualMemory.ReleaseWithViews(s.Parts);
            _spaces.Clear();
            _spaceSnapshot = [];
            _idle.Clear();
        }
        HostVirtualMemory.ReleaseSharedMemory(_kernelMemory);
        HostVirtualMemory.ReleaseSharedMemory(_cdramMemory);
        s_primaryBase = null;
        t_base = null;
        if (ReferenceEquals(Instance, this)) Instance = null;
    }
}
