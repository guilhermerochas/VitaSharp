using VitaSharp.Core.Cpu;
using VitaSharp.Core.Memory;

namespace VitaSharp.Tests;

/// <summary>
/// <see cref="BlockCache.Invalidate"/> finds the blocks a rewrite drops through the cache's page index, not by scanning
/// every cached block: the linker rebinds hundreds of import stubs (12 bytes each) for every firmware module the shell
/// loads, and with the ARM code precompilation's ~300,000 blocks in the cache each of those scans cost milliseconds
/// (5 s for livespace_db.suprx on the shell's single core, the Vita system's slow boot). The contract around it: a
/// rewrite still drops every block whose code or dependency range it touches (blocks across a page boundary, the
/// dependency ranges the ARM64 backend records, ranges larger than the index), keeps the others, and a block decoded
/// again after a drop is found by the next rewrite.
/// </summary>
[Collection("guest")]
public sealed class BlockCachePageIndexTests(GuestMemoryFixture fixture)
{
    private const uint Nop = 0xE1A00000; // mov r0, r0
    private const uint ReturnToCaller = 0xE12FFF1E; // bx lr
    private const uint PageSize = 1u << BlockCache.PageShift;

    /// <summary>A region of <paramref name="pages"/> pages with a one-instruction block (bx lr) every 16 bytes.</summary>
    private uint CodeRegion(int pages, string name)
    {
        uint code = fixture.Memory.Alloc((uint)pages * PageSize, name);
        for (uint offset = 0; offset < pages * PageSize; offset += 16)
        {
            GuestMemory.W32(code + offset, ReturnToCaller);
        }

        return code;
    }

    private static void FillCache(BlockCache cache, uint code, int pages)
    {
        for (uint offset = 0; offset < pages * PageSize; offset += 16)
        {
            cache.Get(code + offset, false);
        }
    }

    [Fact]
    public void RewritingAStubLooksOnlyAtTheBlocksOfItsPage()
    {
        const int Pages = 64;
        uint code = CodeRegion(Pages, "page index stub");
        var cache = new BlockCache();
        FillCache(cache, code, Pages);
        int blocksPerPage = (int)(PageSize / 16);
        Assert.Equal(Pages * blocksPerPage, cache.Count);

        uint stub = code + 20 * PageSize + 0x40;
        cache.Invalidate(stub, 12);

        // the block at the stub is dropped, its neighbours stay, and only the stub's page was looked at (a scan of the
        // whole cache looked at all 16,384 blocks)
        Assert.False(cache.TryGet(stub, false, out _));
        Assert.True(cache.TryGet(stub - 16, false, out _));
        Assert.True(cache.TryGet(stub + 16, false, out _));
        Assert.Equal(Pages * blocksPerPage - 1, cache.Count);
        Assert.True(cache.InvalidateExamined <= blocksPerPage, $"examined {cache.InvalidateExamined} blocks");
    }

    [Fact]
    public void ABlockAcrossAPageBoundaryIsDroppedByARewriteOfEitherPage()
    {
        uint code = fixture.Memory.Alloc(3 * PageSize, "page index boundary");
        uint start = code + PageSize - 8;
        GuestMemory.W32(start, Nop);
        GuestMemory.W32(start + 4, Nop);
        GuestMemory.W32(start + 8, Nop);
        GuestMemory.W32(start + 12, ReturnToCaller);
        var cache = new BlockCache();

        Block first = cache.Get(start, false);
        Assert.True(first.End > code + PageSize);
        cache.Invalidate(code + PageSize + 4, 4);
        Assert.False(cache.TryGet(start, false, out _));

        Block second = cache.Get(start, false);
        Assert.NotSame(first, second);
        cache.Invalidate(start, 4);
        Assert.False(cache.TryGet(start, false, out _));
    }

    [Fact]
    public void ARewriteOfADependencyRangeOnAnotherPageDropsTheBlock()
    {
        uint code = CodeRegion(8, "page index deps");
        var cache = new BlockCache();
        Block block = cache.Get(code, false);
        cache.Get(code + 16, false);
        Block successor = cache.Get(code + 6 * PageSize, false);
        // the ARM64 backend compiled the block relying on its successor's code (Block._deps)
        block.SetDependencies([successor.Start, successor.End]);

        cache.Invalidate(successor.Start, 4);

        Assert.False(cache.TryGet(code, false, out _));
        Assert.False(cache.TryGet(successor.Start, false, out _));
        Assert.True(cache.TryGet(code + 16, false, out _));
    }

    [Fact]
    public void ARewriteOfTheWholeAddressSpaceDropsEveryBlock()
    {
        const int Pages = 4;
        uint code = CodeRegion(Pages, "page index all");
        var cache = new BlockCache();
        FillCache(cache, code, Pages);
        Assert.NotEqual(0, cache.Count);

        cache.Invalidate(0, uint.MaxValue);

        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void ARangeLargerThanTheIndexStillFindsTheBlocksInIt()
    {
        uint code = CodeRegion(4, "page index large");
        var cache = new BlockCache();
        FillCache(cache, code, 4);
        int before = cache.Count;

        // a 16 MB range around the code: more pages than the index holds, so the index's pages are walked instead
        uint from = code >= 0x0080_0000 ? code - 0x0080_0000 : 0;
        cache.Invalidate(from, code + PageSize - from);

        Assert.False(cache.TryGet(code, false, out _));
        Assert.False(cache.TryGet(code + PageSize - 16, false, out _));
        Assert.True(cache.TryGet(code + PageSize, false, out _));
        Assert.Equal(before - (int)(PageSize / 16), cache.Count);
    }

    [Fact]
    public void ABlockDecodedAgainAfterADropIsFoundByTheNextRewrite()
    {
        uint code = CodeRegion(2, "page index again");
        var cache = new BlockCache();
        cache.Get(code, false);
        cache.Invalidate(code, 4);
        Block again = cache.Get(code, false);

        GuestMemory.W32(code, Nop);
        GuestMemory.W32(code + 4, ReturnToCaller);
        cache.Invalidate(code, 4);

        Assert.True(again.NativeEntry != 0 || again.Invalidated);
        Block now = cache.Get(code, false);
        Assert.Equal(Nop, now.Insts[0].Raw);
    }

    [Fact]
    public void AfterClearTheIndexStartsEmptyAndNewBlocksAreFound()
    {
        uint code = CodeRegion(2, "page index clear");
        var cache = new BlockCache();
        FillCache(cache, code, 2);
        cache.Clear();
        Assert.Equal(0, cache.Count);

        cache.Get(code + 32, false);
        long examinedBefore = cache.InvalidateExamined;
        cache.Invalidate(code + 32, 4);

        Assert.False(cache.TryGet(code + 32, false, out _));
        Assert.Equal(1, cache.InvalidateExamined - examinedBefore);
    }

    [Fact]
    public void BlocksDroppedByRevalidateLeaveTheIndexWhenTheirPageIsNextLookedAt()
    {
        uint code = CodeRegion(1, "page index revalidate");
        var cache = new BlockCache();
        cache.Get(code, false);
        cache.Get(code + 16, false);
        // a save state replaced the code: Revalidate drops the changed block without the page index
        GuestMemory.W32(code, Nop);
        GuestMemory.W32(code + 4, ReturnToCaller);
        (int kept, int dropped) = cache.Revalidate();
        Assert.Equal((1, 1), (kept, dropped));

        cache.Invalidate(code + 16, 4);
        long examinedBefore = cache.InvalidateExamined;
        cache.Invalidate(code + 16, 4);

        // the dropped block's stale entry and the invalidated one went with the first look; nothing is left to examine
        Assert.Equal(0, cache.InvalidateExamined - examinedBefore);
        Assert.Equal(0, cache.Count);
    }
}
