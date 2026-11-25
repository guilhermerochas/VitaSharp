using System.Runtime.InteropServices;

namespace VitaSharp.Core.Memory;

/// <summary>
/// The pages of the last incremental save state (the "Gravar tudo" pre-roll's ring of states): every block's pages as
/// they were then (empty: a zero page), and the <see cref="DirtyPages"/> epoch of that moment. The next incremental
/// state reads from guest memory only the pages written since (<see cref="DirtyPages.WrittenSince"/>) and takes the
/// others from here, so the game stops for the pages it wrote, not for its whole memory; the state file itself is
/// built from the pages after the game goes on. Kept by its owner between states (~ the non-zero guest memory).
/// </summary>
public sealed class SnapshotPages
{
    internal uint Epoch { get; set; }
    internal Dictionary<(AddressSpace? Space, uint Address, uint Size), ReadOnlyMemory<byte>[]> Blocks { get; set; } = new();

    /// <summary>Pages read from guest memory and pages taken from the previous image in the last state.</summary>
    public long LastRead { get; internal set; }
    public long LastReused { get; internal set; }

    /// <summary>Drops the image: the next state reads every page.</summary>
    public void Clear()
    {
        Blocks = new Dictionary<(AddressSpace? Space, uint Address, uint Size), ReadOnlyMemory<byte>[]>();
        Epoch = 0;
    }
}

public sealed unsafe partial class GuestMemory
{
    /// <summary>
    /// The block's pages for an incremental state (the caller has stopped every guest thread): a page not written since
    /// <paramref name="previousEpoch"/> is the previous image's (no read of guest memory), any other is read (empty when
    /// it is all zero). <paramref name="previous"/> null, or tracking off (VITASHARP_TEXDIRTY=0): every page is read.
    /// </summary>
    internal ReadOnlyMemory<byte>[] CapturePages(Block b, ReadOnlyMemory<byte>[]? previous, uint previousEpoch, ref long read, ref long reused)
    {
        uint pages = b.Size / PageSize;
        var result = new ReadOnlyMemory<byte>[pages];
        byte* p = HostOf(b);
        // the capture's sweep is exact (every guest thread is stopped, no store in flight): a page dirty at it was
        // written before the previous state and is in it, so only versions after that sweep count as written
        uint since = previousEpoch + 1;
        bool canReuse = previous != null && previous.Length == pages && DirtyPages.Enabled && previousEpoch != 0;
        if (canReuse && !DirtyPages.WrittenSince(b.Address, b.Size, since))
        {
            Array.Copy(previous!, result, pages);
            reused += pages;
            return result;
        }

        // the pages to read, copied into one buffer of their own (one allocation, whatever their number)
        uint toRead = 0;
        for (uint i = 0; i < pages; i++)
        {
            if (!canReuse || DirtyPages.WrittenSince(b.Address + i * PageSize, PageSize, since))
            {
                toRead++;
            }
            else
            {
                result[i] = previous![i];
                reused++;
            }
        }

        byte[] copies = GC.AllocateUninitializedArray<byte>(checked((int)(toRead * PageSize)));
        int used = 0;
        for (uint i = 0; i < pages; i++)
        {
            if (canReuse && !DirtyPages.WrittenSince(b.Address + i * PageSize, PageSize, since))
            {
                continue;
            }

            read++;
            var page = new ReadOnlySpan<byte>(p + (long)i * PageSize, (int)PageSize);
            if (page.IndexOfAnyExcept((byte)0) < 0)
            {
                continue; // a zero page: default
            }

            Memory<byte> slot = copies.AsMemory(used, (int)PageSize);
            page.CopyTo(slot.Span);
            result[i] = slot;
            used += (int)PageSize;
        }

        return result;
    }

    /// <summary>The pages of <see cref="CapturePages"/> in the layout of <see cref="CopyContents"/> (any thread: the
    /// pages are the state's own copies).</summary>
    internal static byte[] SerializePages(ReadOnlyMemory<byte>[] pages, out bool wordPages)
    {
        int bitmapLen = (pages.Length + 7) / 8;
        byte[] bitmap = new byte[bitmapLen];
        byte[] wordBitmap = new byte[bitmapLen];
        long fullPages = 0, filledPages = 0;
        for (int i = 0; i < pages.Length; i++)
        {
            if (pages[i].IsEmpty)
            {
                continue;
            }

            bitmap[i >> 3] |= (byte)(1 << (i & 7));
            ReadOnlySpan<uint> words = MemoryMarshal.Cast<byte, uint>(pages[i].Span);
            if (words[1..].IndexOfAnyExcept(words[0]) < 0)
            {
                wordBitmap[i >> 3] |= (byte)(1 << (i & 7));
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
        for (int i = 0; i < pages.Length; i++)
        {
            if (pages[i].IsEmpty)
            {
                continue;
            }

            ReadOnlySpan<byte> page = pages[i].Span;
            if (wordPages && (wordBitmap[i >> 3] & (1 << (i & 7))) != 0)
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
}
