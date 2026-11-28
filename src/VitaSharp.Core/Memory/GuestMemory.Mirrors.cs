namespace VitaSharp.Core.Memory;

/// <summary>
/// Mirror blocks: one memory at two guest addresses, as the Vita's ksceKernelAllocMemBlock makes them when its option
/// names a mirror source (attr 0x40, the source block at +0x20): the new block gets its own virtual range over the
/// source's physical pages (SceCompat 3.74 maps its "SceCompatCached" CDRAM a second time as "SceCompatUncached" and
/// "SceCompatCdram"). Here the source is a block of the shared memories (CDRAM, kernel memory) and the mirror is a
/// second host view of the same pages of that memory, mapped in the alias window [<see cref="MirrorStart"/>,
/// <see cref="MirrorEnd"/>) of every address space: a store through one address is the same byte at the other.
/// Windows keeps the window a placeholder and replaces pieces of it with views; Linux, Android and macOS map the memory
/// file over it. Views start at 64 KB boundaries (Windows' granularity), so a mirror keeps its source's offset inside
/// 64 KB. Without per-process spaces (no shared memory on the host) there is nothing to map twice: no mirror.
/// <para>
/// A page's mapping can also be taken away while its block stays allocated (<see cref="UnmapPages"/>,
/// ksceKernelUnmapMemBlock): its mapping-table byte goes to 0, so an access raises a data abort as on the console
/// (<see cref="Cpu.DataAborts"/>), and with host faults armed the pages are inaccessible on the host too.
/// </para>
/// </summary>
public sealed unsafe partial class GuestMemory
{
    /// <summary>The alias window mirrors are mapped in (between the kernel memory and CDRAM; nothing else uses it).</summary>
    public const uint MirrorStart = 0x5000_0000, MirrorEnd = 0x6000_0000;

    /// <summary>The window's halves: user mirrors below, kernel mirrors from here (on the Vita they come from the user
    /// and the kernel virtual spaces, each allocated in its own order: SceCompat's three uncached blocks follow each
    /// other, and so do its three kernel views, although it allocates them alternately).</summary>
    public const uint MirrorKernelStart = 0x5800_0000;

    /// <summary>The host's view granularity (Windows' allocation granularity; a multiple of every host page).</summary>
    private const uint AliasGranule = 0x10000;

    /// <summary>A mapped mirror: its block's range, the source's address and the 64 KB aligned window piece the views
    /// cover (<paramref name="ViewStart"/> in the window, <paramref name="ViewSize"/> bytes, over the source memory at
    /// <paramref name="ViewOffset"/>).</summary>
    private sealed record MirrorView(uint Address, uint Size, uint Source, uint ViewStart, uint ViewSize, nint Memory, ulong ViewOffset);

    private readonly SortedList<uint, uint> _freeMirrorUser = new() { { MirrorStart, MirrorKernelStart - MirrorStart } };
    private readonly SortedList<uint, uint> _freeMirrorKernel = new() { { MirrorKernelStart, MirrorEnd - MirrorKernelStart } };

    /// <summary>The free list of the window half holding <paramref name="addr"/>.</summary>
    private SortedList<uint, uint> FreeMirrorAt(uint addr) => addr < MirrorKernelStart ? _freeMirrorUser : _freeMirrorKernel;
    private readonly Dictionary<uint, MirrorView> _mirrors = new();

    // the mirrored page pairs for the dirty-page sweep (mirror page, source page, count), rebuilt when mirrors change
    private (uint Mirror, uint Source, uint Pages)[] _mirrorPages = [];

    /// <summary>The mirrors' page pairs (<see cref="DirtyPages.Sweep"/> gives a write through either page to both).</summary>
    internal ReadOnlySpan<(uint Mirror, uint Source, uint Pages)> MirrorPages => Volatile.Read(ref _mirrorPages);

    /// <summary>
    /// A mirror of [<paramref name="source"/>, + <paramref name="size"/>) (inside a CDRAM or kernel block): a new block
    /// named <paramref name="name"/> in the alias window over the same memory, in every address space, at an address
    /// whose alignment is at least <paramref name="align"/> (and the source's offset inside 64 KB). 0 when the range is
    /// not shared memory, the host maps no second view, or the window is full.
    /// </summary>
    public uint AllocMirror(uint source, uint size, string name, uint align = PageSize, bool kernel = false)
    {
        lock (_lock)
        {
            return MapNewMirror(source, size, name, Kernel.ProcessScope.CurrentPid, 0, align, kernel);
        }
    }

    /// <summary>A mirror at <paramref name="at"/> (its start; 0: the first free place aligned to
    /// <paramref name="align"/>) of [<paramref name="source"/>, + <paramref name="size"/>), owned by
    /// <paramref name="owner"/>; 0 when it cannot be made. Called under the lock.</summary>
    private uint MapNewMirror(uint source, uint size, string name, int owner, uint at, uint align, bool kernel)
    {
        size = AlignUp(Math.Max(size, 1), PageSize);
        if (!SeparateSpaces || (source & (PageSize - 1)) != 0)
        {
            return 0;
        }

        if (SharedBlockHolding(source, size) is not { Region: MemoryRegion.Cdram or MemoryRegion.Kernel })
        {
            return 0;
        }

        uint lead = source & (AliasGranule - 1);
        uint viewSize = AlignUp(lead + size, AliasGranule);
        uint viewAlign = Math.Max(AlignUp(Math.Max(align, PageSize), AliasGranule), AliasGranule);
        if (at != 0 && (at & (AliasGranule - 1)) != lead)
        {
            return 0;
        }

        uint viewStart = 0, freeStart = 0, freeLength = 0;
        SortedList<uint, uint> free = at != 0 ? FreeMirrorAt(at) : kernel ? _freeMirrorKernel : _freeMirrorUser;
        for (int i = 0; i < free.Count; i++)
        {
            uint start = free.Keys[i], length = free.Values[i];
            uint aligned = at != 0 ? at - lead : AlignUp(start, viewAlign);
            if (aligned >= start && aligned - start + (ulong)viewSize <= length)
            {
                Carve(free, start, length, aligned, viewSize);
                viewStart = aligned;
                freeStart = start;
                freeLength = length;
                break;
            }
        }

        if (viewStart == 0)
        {
            return 0;
        }

        nint memory = SharedMemoryAt(source, out ulong offset);
        var mirror = new MirrorView(viewStart + lead, size, source, viewStart, viewSize, memory, offset - lead);
        foreach (AddressSpace space in _spaces)
        {
            // each free window range is one placeholder (Windows): the view's piece is split off it
            if (space.HasSharedViews && !MapMirrorView(space, mirror, freeStart, freeStart + freeLength))
            {
                UnmapMirrorViews(mirror);
                ReleaseWindowRange(viewStart, viewSize);
                return 0;
            }
        }

        _mirrors[mirror.Address] = mirror;
        ProtectShared(mirror.Address, size, accessible: true);
        SetMapped(_sharedMapped, mirror.Address, size, true);
        SetMapped(_sharedUsed, mirror.Address, size, true);
        MarkPages(null, mirror.Address, size, PageMapped);
        DirtyPages.MarkRange(Primary.Base, mirror.Address, size);
        _shared[mirror.Address] = new Block(mirror.Address, size, name, MemoryRegion.Mirror, owner);
        CommittedBytes += size;
        RebuildMirrorPages();
        return mirror.Address;
    }

    /// <summary>The source address of the mirror block at <paramref name="addr"/> (its start), 0 when it is none.</summary>
    public uint MirrorSourceOf(uint addr)
    {
        lock (_lock)
        {
            return _mirrors.TryGetValue(addr, out MirrorView? mirror) ? mirror.Source : 0;
        }
    }

    /// <summary>
    /// The pages of [<paramref name="addr"/>, + <paramref name="size"/>) are no longer mapped although their block stays
    /// allocated (ksceKernelUnmapMemBlock): an access is a data abort (<see cref="Cpu.DataAborts"/>); with host faults
    /// armed the pages are inaccessible on the host too. A mirror over them keeps its own mapping.
    /// </summary>
    public void UnmapPages(uint addr, uint size)
    {
        uint start = addr & ~(PageSize - 1);
        uint length = AlignUp(addr + size, PageSize) - start;
        lock (_lock)
        {
            if (InSharedRegion(start))
            {
                _unmappedShared[start] = length;
                SetMapped(_sharedMapped, start, length, false);
                MarkPages(null, start, length, 0);
                if (SeparateSpaces)
                {
                    ProtectShared(start, length, accessible: false);
                }

                return;
            }

            AddressSpace space = CurrentOf(this);
            SetMapped(space._mappedPages, start, length, false);
            MarkPages(space, start, length, 0);
            if (space._hostFaults)
            {
                HostVirtualMemory.ProtectCommitted(space.Base + start, length, accessible: false);
            }
        }
    }

    /// <summary>True when the page of <paramref name="addr"/> is mapped in the calling thread's space (its mapping-table
    /// byte: a block maps it, or a data abort nobody handled let it through).</summary>
    public static bool IsPageMapped(uint addr) => MappedTable(Base)[addr >> 12] != 0;

    /// <summary>The CDRAM or kernel block holding the whole range, or null. Called under the lock.</summary>
    private Block? SharedBlockHolding(uint addr, uint size)
    {
        foreach (Block block in _shared.Values)
        {
            if (addr >= block.Address && (ulong)addr + size <= (ulong)block.Address + block.Size)
            {
                return block;
            }
        }

        return null;
    }

    /// <summary>Maps <paramref name="mirror"/>'s view in <paramref name="space"/>, inside the free window range
    /// [<paramref name="freeStart"/>, <paramref name="freeEnd"/>) (one placeholder on Windows). Called under the lock.</summary>
    private static bool MapMirrorView(AddressSpace space, MirrorView mirror, uint freeStart, uint freeEnd)
    {
        return HostVirtualMemory.MapAlias(space.Base + mirror.ViewStart, mirror.ViewSize, mirror.Memory, mirror.ViewOffset,
            space.Base + freeStart, freeEnd - freeStart);
    }

    /// <summary>A window range no view uses any more becomes free again, joined with its free neighbours (Windows: one
    /// placeholder again, so a later view of any size inside it can be split off). Called under the lock.</summary>
    private void ReleaseWindowRange(uint start, uint size)
    {
        SortedList<uint, uint> free = FreeMirrorAt(start);
        AddFree(free, start, size);
        for (int i = 0; i < free.Count; i++)
        {
            uint freeStart = free.Keys[i], freeLength = free.Values[i];
            if (start >= freeStart && start - freeStart < freeLength)
            {
                foreach (AddressSpace space in _spaces)
                {
                    if (space.HasSharedViews)
                    {
                        HostVirtualMemory.CoalesceAliasWindow(space.Base + freeStart, freeLength);
                    }
                }

                return;
            }
        }
    }

    /// <summary>Removes <paramref name="mirror"/>'s view from every space that has one. Called under the lock.</summary>
    private void UnmapMirrorViews(MirrorView mirror)
    {
        foreach (AddressSpace space in _spaces)
        {
            if (space.HasSharedViews)
            {
                HostVirtualMemory.UnmapAlias(space.Base + mirror.ViewStart, mirror.ViewSize);
            }
        }
    }

    /// <summary>A new space gets every mirror's view (as it gets the shared regions). Called under the lock.</summary>
    private void MapMirrorsInto(AddressSpace space)
    {
        // the new window is one placeholder: each view (in address order) is split off what is left after the last
        uint placeholderStart = MirrorStart;
        foreach (MirrorView mirror in _mirrors.Values.OrderBy(m => m.ViewStart))
        {
            bool kernelHalf = mirror.ViewStart >= MirrorKernelStart;
            placeholderStart = Math.Max(placeholderStart, kernelHalf ? MirrorKernelStart : MirrorStart);
            bool mapped = MapMirrorView(space, mirror, placeholderStart, kernelHalf ? MirrorEnd : MirrorKernelStart);
            placeholderStart = mirror.ViewStart + mirror.ViewSize;
            if (!mapped)
            {
                Util.Log.Warn($"Guest memory: mirror 0x{mirror.Address:X8} of 0x{mirror.Source:X8} could not be mapped in {space}");
            }
        }
    }

    /// <summary>Frees a mirror block: its views go, its window range is free again. Called under the lock.</summary>
    private void FreeMirror(Block block)
    {
        if (!_shared.Remove(block.Address))
        {
            return;
        }

        CommittedBytes -= block.Size;
        ForgetUnmapped(block.Address, block.Size);
        SetMapped(_sharedMapped, block.Address, block.Size, false);
        MarkPages(null, block.Address, block.Size, 0);
        DirtyPages.MarkRange(Primary.Base, block.Address, block.Size);
        if (!_mirrors.Remove(block.Address, out MirrorView? mirror))
        {
            return;
        }

        UnmapMirrorViews(mirror);
        RebuildMirrorPages();

        if (!OperatingSystem.IsWindows())
        {
            // POSIX: the window's fresh pages are read/write again; with host faults they must fault
            ProtectShared(mirror.ViewStart, mirror.ViewSize, accessible: false);
        }

        ReleaseWindowRange(mirror.ViewStart, mirror.ViewSize);
    }

    /// <summary>The source block at [<paramref name="addr"/>, + <paramref name="size"/>) is being freed: the mirrors over
    /// it are unmapped (the firmware's "Force sceKernelUnmapMemBlock" of each mirror when the source goes): their pages
    /// abort; the blocks, and their host views, stay until freed (a placeholder in the window would fault on the host
    /// instead of reading zeros for an access no data abort guard checks). Called under the lock.</summary>
    private void UnmapMirrorsOver(uint addr, uint size)
    {
        if (_mirrors.Count == 0)
        {
            return;
        }

        ulong end = (ulong)addr + size;
        foreach (MirrorView mirror in _mirrors.Values.ToArray())
        {
            if (mirror.Source < end && (ulong)mirror.Source + mirror.Size > addr)
            {
                SetMapped(_sharedMapped, mirror.Address, mirror.Size, false);
                MarkPages(null, mirror.Address, mirror.Size, 0);
                ProtectShared(mirror.Address, mirror.Size, accessible: false);
            }
        }
    }

    /// <summary>Removes every mirror's view and joins the window again (the memory is being released).</summary>
    private void UnmapAllMirrors()
    {
        foreach (MirrorView mirror in _mirrors.Values)
        {
            UnmapMirrorViews(mirror);
        }

        _mirrors.Clear();
        foreach (AddressSpace space in _spaces)
        {
            if (space.HasSharedViews)
            {
                HostVirtualMemory.CoalesceAliasWindow(space.Base + MirrorStart, MirrorKernelStart - MirrorStart);
                HostVirtualMemory.CoalesceAliasWindow(space.Base + MirrorKernelStart, MirrorEnd - MirrorKernelStart);
            }
        }
    }

    // shared ranges whose pages ksceKernelUnmapMemBlock took out of the map (start -> size), for save states
    private readonly Dictionary<uint, uint> _unmappedShared = new();

    /// <summary>Forgets the unmapped ranges inside a freed shared block. Called under the lock.</summary>
    private void ForgetUnmapped(uint addr, uint size)
    {
        if (_unmappedShared.Count == 0)
        {
            return;
        }

        foreach (uint start in _unmappedShared.Keys.ToArray())
        {
            if (start >= addr && start - addr < size)
            {
                _unmappedShared.Remove(start);
            }
        }
    }

    /// <summary>Save states: the mirrors (address, size, source, name, owner, mapped) and the unmapped shared ranges. A
    /// mirror's memory is its source's, saved with it; this is only where it is mapped.</summary>
    internal void WriteMirrors(SaveState.SnapshotWriter w)
    {
        lock (_lock)
        {
            w.Write(_mirrors.Count);
            foreach (MirrorView mirror in _mirrors.Values)
            {
                Block block = _shared[mirror.Address];
                w.Write(mirror.Address);
                w.Write(mirror.Size);
                w.Write(mirror.Source);
                w.Write(block.Name);
                w.Write(block.Owner);
                w.Write(MappedTable(Primary.Base)[mirror.Address >> 12] != 0);
            }

            w.Write(_unmappedShared.Count);
            foreach ((uint start, uint size) in _unmappedShared)
            {
                w.Write(start);
                w.Write(size);
            }
        }
    }

    /// <summary>
    /// Makes the mirrors and unmapped shared ranges those <see cref="WriteMirrors"/> wrote, after the block table was
    /// restored (<see cref="RestoreBlocks(List{Block})"/>): mirrors the state does not have go, the missing ones are
    /// mapped again at their addresses over their sources, and the saved ranges are unmapped again.
    /// </summary>
    internal void RestoreMirrors(SaveState.SnapshotReader r)
    {
        int count = r.ReadInt32();
        var saved = new List<(uint Address, uint Size, uint Source, string Name, int Owner, bool Mapped)>(count);
        for (int i = 0; i < count; i++)
        {
            saved.Add((r.ReadUInt32(), r.ReadUInt32(), r.ReadUInt32(), r.ReadStringNotNull(), r.ReadInt32(), r.ReadBool()));
        }

        int ranges = r.ReadInt32();
        var unmapped = new List<(uint Start, uint Size)>(ranges);
        for (int i = 0; i < ranges; i++)
        {
            unmapped.Add((r.ReadUInt32(), r.ReadUInt32()));
        }

        lock (_lock)
        {
            foreach (MirrorView mirror in _mirrors.Values.ToArray())
            {
                if (!saved.Exists(s => s.Address == mirror.Address && s.Size == mirror.Size && s.Source == mirror.Source))
                {
                    FreeMirror(_shared[mirror.Address]);
                }
            }

            foreach ((uint address, uint size, uint source, string name, int owner, bool mapped) in saved)
            {
                if (!_mirrors.ContainsKey(address) && MapNewMirror(source, size, name, owner, address, PageSize, kernel: false) != address)
                {
                    throw new SaveState.SnapshotException($"mirror block 0x{address:X8} of 0x{source:X8} cannot be mapped again");
                }

                if (!mapped)
                {
                    SetMapped(_sharedMapped, address, size, false);
                    MarkPages(null, address, size, 0);
                    ProtectShared(address, size, accessible: false);
                }
            }
        }

        foreach ((uint start, uint size) in unmapped)
        {
            UnmapPages(start, size);
        }
    }

    private void RebuildMirrorPages()
    {
        var pages = new List<(uint, uint, uint)>(_mirrors.Count);
        foreach (MirrorView mirror in _mirrors.Values)
        {
            pages.Add((mirror.Address >> 12, mirror.Source >> 12, mirror.Size >> 12));
        }

        Volatile.Write(ref _mirrorPages, pages.ToArray());
    }
}
