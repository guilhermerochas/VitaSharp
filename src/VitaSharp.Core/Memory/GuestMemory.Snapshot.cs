using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Memory;

/// <summary>
/// Save states: the block table and the contents of every block of a single-process system (the first address space
/// plus the shared CDRAM / kernel regions). Contents are stored page by page with a bitmap of the pages that are not
/// all zero, so untouched heaps cost nothing.
/// </summary>
public sealed unsafe partial class GuestMemory
{
    /// <summary>Every block, by address (the caller has stopped every guest thread).</summary>
    internal List<Block> SnapshotBlocks()
    {
        lock (_lock)
        {
            if (_spaces.Any(s => !ReferenceEquals(s, Primary) && s._blocks.Count > 0))
            {
                throw new SnapshotException("save states support one process only (another address space is in use)");
            }

            return AllBlocks().OrderBy(b => b.Address).ToList();
        }
    }

    internal static void WriteBlockTable(SnapshotWriter w, List<Block> blocks)
    {
        w.Write(blocks.Count);
        foreach (Block b in blocks)
        {
            w.Write(b.Address);
            w.Write(b.Size);
            w.Write(b.Name);
            w.Write((int)b.Region);
            w.Write(b.Owner);
        }
    }

    internal static List<Block> ReadBlockTable(SnapshotReader r)
    {
        int n = r.ReadInt32();
        var list = new List<Block>(n);
        for (int i = 0; i < n; i++)
        {
            list.Add(new Block(r.ReadUInt32(), r.ReadUInt32(), r.ReadStringNotNull(), (MemoryRegion)r.ReadInt32(), r.ReadInt32()));
        }

        return list;
    }

    private byte* HostOf(Block b) => (b.Space ?? Primary).Base + b.Address;

    /// <summary>The block's contents: a bitmap of its non-zero pages, then those pages.</summary>
    internal byte[] CopyContents(Block b)
    {
        uint pages = b.Size / PageSize;
        int bitmapLen = (int)((pages + 7) / 8);
        byte* p = HostOf(b);
        byte[] bitmap = new byte[bitmapLen];
        long n = 0;
        for (uint i = 0; i < pages; i++)
        {
            if (new ReadOnlySpan<byte>(p + (long)i * PageSize, (int)PageSize).IndexOfAnyExcept((byte)0) < 0)
            {
                continue;
            }

            bitmap[i >> 3] |= (byte)(1 << (int)(i & 7));
            n++;
        }
        byte[] data = new byte[checked(bitmapLen + n * PageSize)];
        bitmap.CopyTo(data, 0);
        long at = bitmapLen;
        for (uint i = 0; i < pages; i++)
        {
            if ((bitmap[i >> 3] & (1 << (int)(i & 7))) == 0)
            {
                continue;
            }

            new ReadOnlySpan<byte>(p + (long)i * PageSize, (int)PageSize).CopyTo(data.AsSpan((int)at));
            at += PageSize;
        }
        return data;
    }

    internal void RestoreContents(Block b, byte[] data)
    {
        uint pages = b.Size / PageSize;
        int bitmapLen = (int)((pages + 7) / 8);
        if (data.Length < bitmapLen)
        {
            throw new SnapshotException($"memory block 0x{b.Address:X8} is corrupt");
        }

        byte* p = HostOf(b);
        long at = bitmapLen;
        for (uint i = 0; i < pages; i++)
        {
            var page = new Span<byte>(p + (long)i * PageSize, (int)PageSize);
            if ((data[i >> 3] & (1 << (int)(i & 7))) == 0)
            {
                page.Clear();
                continue;
            }
            if (at + PageSize > data.Length)
            {
                throw new SnapshotException($"memory block 0x{b.Address:X8} is corrupt");
            }

            data.AsSpan((int)at, (int)PageSize).CopyTo(page);
            at += PageSize;
        }
        DirtyPages.MarkRange((b.Space ?? Primary).Base, b.Address, b.Size);
    }

    /// <summary>
    /// Makes the block table exactly <paramref name="saved"/>: blocks that are not in it are freed, missing ones are
    /// committed (all in the first address space), and the free lists are rebuilt from the result. Returns the blocks
    /// as they now are, in the saved order. Contents are not touched (<see cref="ReadContents"/> follows).
    /// </summary>
    internal List<Block> RestoreBlocks(List<Block> saved)
    {
        lock (_lock)
        {
            var want = saved.ToDictionary(b => b.Address);
            foreach (Block? b in AllBlocks().ToList())
            {
                if (!want.TryGetValue(b.Address, out Block? s) || s.Size != b.Size || s.Region != b.Region || (b.Space != null && !ReferenceEquals(b.Space, Primary)))
                {
                    Free(b);
                }
            }

            var result = new List<Block>(saved.Count);
            foreach (Block s in saved)
            {
                bool shared = IsSharedRegion(s.Region);
                Dictionary<uint, Block> dict = shared ? _shared : Primary._blocks;
                if (dict.TryGetValue(s.Address, out Block? cur))
                {
                    Block keep = cur with
                    {
                        Name = s.Name,
                        Owner = s.Owner
                    };
                    dict[s.Address] = keep;
                    result.Add(keep);
                    continue;
                }
                using (Kernel.ProcessScope.Enter(s.Owner))
                {
                    Commit(Primary, s.Address, s.Size, s.Name, s.Region);
                }

                result.Add(dict[s.Address]);
            }
            // the free lists: each region whole, minus every block in it
            _freeUser.Clear();
            _freeUser.Add(UserStart, UserEnd - UserStart);
            _freeCdram.Clear();
            _freeCdram.Add(CdramStart, CdramEnd - CdramStart);
            _freeKernel.Clear();
            _freeKernel.Add(KernelStart, KernelEnd - KernelStart);
            foreach (Block b in result)
            {
                if (b.Region == MemoryRegion.Fixed)
                {
                    continue;
                }

                SortedList<uint, uint> free = FreeList(b.Region);
                for (int i = 0; i < free.Count; i++)
                {
                    uint fs = free.Keys[i], fl = free.Values[i];
                    if (b.Address >= fs && (ulong)b.Address + b.Size <= (ulong)fs + fl)
                    {
                        Carve(free, fs, fl, b.Address, b.Size);
                        break;
                    }
                }
            }
            return result;
        }
    }
}
