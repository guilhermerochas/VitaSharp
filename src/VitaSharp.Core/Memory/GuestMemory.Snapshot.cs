using System.Buffers.Binary;
using System.Runtime.InteropServices;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Memory;

/// <summary>
/// Save states: the block table and the contents of every block: the shared CDRAM / kernel regions and the private
/// blocks of every process's address space (the shell and an application it opened hold user blocks at the same
/// addresses, each in its own space; <see cref="Block.Space"/> says which). Contents are stored page by page with a
/// bitmap of the pages that are not all zero, so untouched heaps cost nothing.
/// </summary>
public sealed unsafe partial class GuestMemory
{
    /// <summary>Every block of every space, by address (the caller has stopped every guest thread).</summary>
    internal List<Block> SnapshotBlocks()
    {
        lock (_lock)
        {
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

    /// <summary>
    /// The block's contents: a bitmap of its non-zero pages, then those pages. A page that is one 32-bit word repeated
    /// (a memset pattern, the trap fill of a firmware module's code segment) is stored as that word: when the block has
    /// one, <paramref name="wordPages"/> is true and the layout is the bitmap of non-zero pages, the bitmap of word
    /// pages, then each non-zero page in order as its word or its bytes (<see cref="RestoreContents"/> with the same
    /// flag); otherwise it is the plain layout.
    /// </summary>
    internal byte[] CopyContents(Block b, out bool wordPages)
    {
        uint pages = b.Size / PageSize;
        int bitmapLen = (int)((pages + 7) / 8);
        byte* p = HostOf(b);
        byte[] bitmap = new byte[bitmapLen];
        byte[] wordBitmap = new byte[bitmapLen];
        long fullPages = 0, filledPages = 0;
        for (uint i = 0; i < pages; i++)
        {
            var page = new ReadOnlySpan<byte>(p + (long)i * PageSize, (int)PageSize);
            if (page.IndexOfAnyExcept((byte)0) < 0)
            {
                continue;
            }

            bitmap[i >> 3] |= (byte)(1 << (int)(i & 7));
            ReadOnlySpan<uint> words = MemoryMarshal.Cast<byte, uint>(page);
            if (words[1..].IndexOfAnyExcept(words[0]) < 0)
            {
                wordBitmap[i >> 3] |= (byte)(1 << (int)(i & 7));
                filledPages++;
            }
            else
            {
                fullPages++;
            }
        }

        wordPages = filledPages > 0;
        if (!wordPages)
        {
            fullPages += filledPages;
        }

        int headerLen = wordPages ? 2 * bitmapLen : bitmapLen;
        byte[] data = new byte[checked(headerLen + fullPages * PageSize + (wordPages ? filledPages * 4 : 0))];
        bitmap.CopyTo(data, 0);
        if (wordPages)
        {
            wordBitmap.CopyTo(data, bitmapLen);
        }

        long at = headerLen;
        for (uint i = 0; i < pages; i++)
        {
            if ((bitmap[i >> 3] & (1 << (int)(i & 7))) == 0)
            {
                continue;
            }

            var page = new ReadOnlySpan<byte>(p + (long)i * PageSize, (int)PageSize);
            if (wordPages && (wordBitmap[i >> 3] & (1 << (int)(i & 7))) != 0)
            {
                page[..4].CopyTo(data.AsSpan((int)at));
                at += 4;
                continue;
            }

            page.CopyTo(data.AsSpan((int)at));
            at += PageSize;
        }
        return data;
    }

    /// <summary>Writes back what <see cref="CopyContents"/> copied, in the layout <paramref name="wordPages"/> names.</summary>
    internal void RestoreContents(Block b, byte[] data, bool wordPages = false)
    {
        uint pages = b.Size / PageSize;
        int bitmapLen = (int)((pages + 7) / 8);
        int headerLen = wordPages ? 2 * bitmapLen : bitmapLen;
        if (data.Length < headerLen)
        {
            throw new SnapshotException($"memory block 0x{b.Address:X8} is corrupt");
        }

        byte* p = HostOf(b);
        AddressSpace space = b.Space ?? Primary;
        bool shared = b.Space == null;
        long at = headerLen;
        for (uint i = 0; i < pages; i++)
        {
            if ((data[i >> 3] & (1 << (int)(i & 7))) == 0)
            {
                // a run of zero pages reads zero without being touched, as in a new block: clearing them by hand made
                // the whole block resident (a state's heaps and video memory are mostly zero pages)
                uint run = i + 1;
                while (run < pages && (data[run >> 3] & (1 << (int)(run & 7))) == 0)
                {
                    run++;
                }

                ZeroWithoutTouching(space, b.Address + i * PageSize, (run - i) * PageSize, shared);
                i = run - 1;
                continue;
            }

            var page = new Span<byte>(p + (long)i * PageSize, (int)PageSize);

            if (wordPages && (data[bitmapLen + (i >> 3)] & (1 << (int)(i & 7))) != 0)
            {
                if (at + 4 > data.Length)
                {
                    throw new SnapshotException($"memory block 0x{b.Address:X8} is corrupt");
                }

                MemoryMarshal.Cast<byte, uint>(page).Fill(BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan((int)at)));
                at += 4;
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
    /// Makes the block table exactly <paramref name="saved"/> with every private block in the first address space (a
    /// state of one process).
    /// </summary>
    internal List<Block> RestoreBlocks(List<Block> saved) => RestoreBlocks(saved, null);

    /// <summary>
    /// Makes the block table exactly <paramref name="saved"/>: blocks that are not in it are freed, missing ones are
    /// committed, and the free lists are rebuilt from the result. <paramref name="spaces"/> gives, by index, the address
    /// space of each saved private block (null, or a null entry: the first space); CDRAM and kernel blocks are in every
    /// space. Returns the blocks as they now are, in the saved order. Contents are not touched
    /// (<see cref="RestoreContents"/> follows).
    /// </summary>
    internal List<Block> RestoreBlocks(List<Block> saved, IReadOnlyList<AddressSpace?>? spaces)
    {
        if (spaces != null && spaces.Count != saved.Count)
        {
            throw new SnapshotException($"the memory table names the space of {spaces.Count} blocks, it has {saved.Count}");
        }

        lock (_lock)
        {
            AddressSpace SpaceOf(int index) => spaces?[index] ?? Primary;
            var want = new Dictionary<(AddressSpace? Space, uint Address), Block>();
            for (int i = 0; i < saved.Count; i++)
            {
                Block s = saved[i];
                want[(IsSharedRegion(s.Region) ? null : SpaceOf(i), s.Address)] = s;
            }

            foreach (Block? b in AllBlocks().ToList())
            {
                if (!want.TryGetValue((b.Space, b.Address), out Block? s) || s.Size != b.Size || s.Region != b.Region)
                {
                    Free(b);
                }
            }

            var result = new List<Block>(saved.Count);
            for (int i = 0; i < saved.Count; i++)
            {
                Block s = saved[i];
                bool shared = IsSharedRegion(s.Region);
                AddressSpace space = SpaceOf(i);
                Dictionary<uint, Block> dict = shared ? _shared : space._blocks;
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
                    Commit(space, s.Address, s.Size, s.Name, s.Region);
                }

                result.Add(dict[s.Address]);
            }

            // the free lists: each region whole, minus every block in it (user blocks of several spaces may cover the
            // same range: the user list holds what no space uses)
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

                RemoveFromFree(FreeList(b.Region), b.Address, (ulong)b.Address + b.Size);
            }

            return result;
        }
    }

    /// <summary>Takes [<paramref name="start"/>, <paramref name="end"/>) out of <paramref name="free"/>, whatever free
    /// ranges it overlaps.</summary>
    private static void RemoveFromFree(SortedList<uint, uint> free, uint start, ulong end)
    {
        for (int i = free.Count - 1; i >= 0; i--)
        {
            uint freeStart = free.Keys[i];
            uint freeLength = free.Values[i];
            ulong freeEnd = (ulong)freeStart + freeLength;
            if (freeEnd <= start || freeStart >= end)
            {
                continue;
            }

            uint overlapStart = Math.Max(freeStart, start);
            uint overlapEnd = (uint)Math.Min(freeEnd, end);
            Carve(free, freeStart, freeLength, overlapStart, overlapEnd - overlapStart);
        }
    }
}
