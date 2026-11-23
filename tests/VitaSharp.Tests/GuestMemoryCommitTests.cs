using System.Runtime.InteropServices;
using VitaSharp.Core.Memory;
using Xunit;

namespace VitaSharp.Tests;

/// <summary>
/// What a new guest block costs on the host: it reads as zeros (the kernel hands out cleared memory, also where a freed
/// block was) but takes host RAM only for the pages the guest touches. Games take their whole heap at boot (Killzone
/// Mercenary: about 300 MB of user and CDRAM memory in its first seconds) and clearing each block by hand made all of it
/// resident at once.
/// </summary>
[Collection("guest")]
public sealed unsafe partial class GuestMemoryCommitTests(GuestMemoryFixture fixture)
{
    // user and CDRAM ranges high enough that nothing else in the test process uses them
    private const uint UserBlock = 0xD800_0000, CdramBlock = 0x6F00_0000;
    private const uint BlockSize = 32u << 20;

    [Fact]
    public void ANewUserBlockTakesNoHostMemoryUntilTouched()
    {
        Assert.Equal(UserBlock, fixture.Memory.TryAllocAt(UserBlock, BlockSize, "untouched heap"));
        try
        {
            byte* host = fixture.Memory.Primary.Base + UserBlock;
            long resident = ResidentBytes(host, BlockSize);
            if (resident < 0)
            {
                return; // no residency query on this host
            }

            // the old clear by hand made all 32 MB resident; a few pages may be in for other reasons
            Assert.True(resident < BlockSize / 8, $"{resident >> 10} KB of a new {BlockSize >> 20} MB block are resident before the guest touched it");
            Assert.Equal(0u, GuestMemory.Read<uint>(UserBlock));
            Assert.Equal(0u, GuestMemory.Read<uint>(UserBlock + BlockSize - 4));
        }
        finally
        {
            fixture.Memory.Free(UserBlock);
        }
    }

    [Fact]
    public void AUserRangeAllocatedAgainReadsZero()
    {
        AllocWriteFreeAndAllocAgain(UserBlock, 3u << 20);
    }

    /// <summary>CDRAM is shared memory mapped in every address space: a freed block there is only discarded, so the
    /// next block in its place is cleared by hand.</summary>
    [Fact]
    public void ACdramRangeAllocatedAgainReadsZero()
    {
        AllocWriteFreeAndAllocAgain(CdramBlock, 3u << 20);
    }

    /// <summary>A block whose start and end are not on a 16 KB boundary (guest pages are 4 KB, host pages can be 16 KB)
    /// next to a live block: the new one reads zero and the neighbour keeps its data.</summary>
    [Fact]
    public void ANewBlockBesideALiveOneLeavesTheNeighbourIntact()
    {
        const uint Neighbour = UserBlock, NeighbourSize = 0x5000;
        Assert.Equal(Neighbour, fixture.Memory.TryAllocAt(Neighbour, NeighbourSize, "live neighbour"));
        try
        {
            GuestMemory.Write(Neighbour + NeighbourSize - 4, 0xCAFE_F00Du);
            AllocWriteFreeAndAllocAgain(Neighbour + NeighbourSize, 0x3000);
            Assert.Equal(0xCAFE_F00Du, GuestMemory.Read<uint>(Neighbour + NeighbourSize - 4));
        }
        finally
        {
            fixture.Memory.Free(Neighbour);
        }
    }

    /// <summary>
    /// CDRAM and kernel memory are shared memory mapped in every address space (a section view on Windows, a memfd/shm
    /// mapping elsewhere): a new block there takes no host RAM for the pages that already read zero, until the guest or
    /// the GPU writes them; the pages an earlier block (or a stray access) used may hold its data and are cleared.
    /// One guest memory serves the whole test process and a Windows section page stays committed once used, so the
    /// range cannot be assumed fresh: what was used before the allocation is measured, and 8 MB of the block (more than
    /// the old bound of an eighth) are used and freed first so both kinds of page are always there.
    /// </summary>
    [Theory]
    [InlineData(0x6C00_0000u)]
    [InlineData(0x4C00_0000u)]
    public void ANewSharedBlockTakesNoHostMemoryUntilTouched(uint region)
    {
        const uint UsedOffset = 8u << 20, UsedSize = 8u << 20;
        uint? untouched = MostlyUntouchedBlock(region);
        if (untouched is not { } addr)
        {
            // every block of the segment was used earlier in this test process (a long suite run): the residency of a
            // fresh block cannot be measured here; the zero-reading contract is covered by the AllocatedAgain tests
            return;
        }

        AllocWriteAndFree(addr + UsedOffset, UsedSize);
        byte* host = fixture.Memory.Primary.Base + addr;
        bool[]? mayHoldData = PagesThatMayHoldData(host, BlockSize);
        Assert.Equal(addr, fixture.Memory.TryAllocAt(addr, BlockSize, "untouched video memory"));
        try
        {
            bool[]? resident = ResidentPages(host, BlockSize);
            if (mayHoldData != null && resident != null)
            {
                // the old clear by hand made all 32 MB resident; a few pages may be in for other reasons
                long fresh = 0, freshResident = 0;
                for (int i = 0; i < resident.Length; i++)
                {
                    if (mayHoldData[i])
                    {
                        continue;
                    }

                    fresh++;
                    if (resident[i])
                    {
                        freshResident++;
                    }
                }

                int pageSize = Environment.SystemPageSize;
                Assert.True(fresh * pageSize >= BlockSize / 2, $"only {(fresh * pageSize) >> 10} KB of the block read zero before it was allocated");
                Assert.True(freshResident < fresh / 8, $"{(freshResident * pageSize) >> 10} KB of the {(fresh * pageSize) >> 10} KB of a new {BlockSize >> 20} MB shared block that read zero before are resident before the guest touched them");
            }

            for (uint offset = UsedOffset; offset < UsedOffset + UsedSize; offset += GuestMemory.PageSize)
            {
                Assert.Equal(0u, GuestMemory.Read<uint>(addr + offset)); // the earlier block's data is gone
            }

            Assert.Equal(0u, GuestMemory.Read<uint>(addr));
            Assert.Equal(0u, GuestMemory.Read<uint>(addr + BlockSize - 4));
        }
        finally
        {
            fixture.Memory.Free(addr);
        }
    }

    /// <summary>The first free 32 MB block (null when none is left) from <paramref name="region"/> on whose pages at least half were never used
    /// in this test process: other tests share the one guest memory, and a Windows section page they used stays
    /// committed, so a fixed address can be wholly used before this test runs.</summary>
    private uint? MostlyUntouchedBlock(uint region)
    {
        // the 8 blocks of the region's 256 MB segment (CDRAM or kernel memory), starting at the region: a block past
        // the segment's end is not the same kind of memory
        const uint SegmentSize = 0x1000_0000;
        const int Candidates = (int)(SegmentSize / BlockSize);
        uint segment = region & ~(SegmentSize - 1);
        for (uint i = 0; i < Candidates; i++)
        {
            uint addr = segment + ((region - segment + (i * BlockSize)) % SegmentSize);
            bool[]? mayHoldData = PagesThatMayHoldData(fixture.Memory.Primary.Base + addr, BlockSize);
            if (mayHoldData == null)
            {
                return addr;
            }

            int untouched = mayHoldData.Count(used => !used);
            bool free = !fixture.Memory.Blocks().Any(block => block.Address < addr + BlockSize && addr < block.Address + block.Size);
            if (free && untouched * 2 >= mayHoldData.Length)
            {
                return addr;
            }
        }

        return null;
    }

    private void AllocWriteAndFree(uint addr, uint size)
    {
        Assert.Equal(addr, fixture.Memory.TryAllocAt(addr, size, "earlier block"));
        for (uint offset = 0; offset < size; offset += GuestMemory.PageSize)
        {
            GuestMemory.Write(addr + offset, 0xDEAD_BEEFu);
        }

        fixture.Memory.Free(addr);
    }

    [Fact]
    public void AKernelRangeAllocatedAgainReadsZero()
    {
        AllocWriteFreeAndAllocAgain(0x4F00_0000, 3u << 20);
    }

    /// <summary>A CDRAM block on 4 KB but not 16/64 KB boundaries next to a live one: the new block reads zero, the
    /// neighbour (a render target the GPU wrote, say) keeps its data on both sides.</summary>
    [Fact]
    public void ANewCdramBlockBetweenLiveOnesLeavesTheNeighboursIntact()
    {
        const uint Before = CdramBlock + 0x0040_0000, BeforeSize = 0x5000;
        const uint Middle = Before + BeforeSize, MiddleSize = 0x3000;
        const uint After = Middle + MiddleSize, AfterSize = 0x2000;
        Assert.Equal(Before, fixture.Memory.TryAllocAt(Before, BeforeSize, "live before"));
        Assert.Equal(After, fixture.Memory.TryAllocAt(After, AfterSize, "live after"));
        try
        {
            GuestMemory.Write(Before + BeforeSize - 4, 0xCAFE_F00Du);
            GuestMemory.Write(After, 0x0BAD_CAFEu);
            AllocWriteFreeAndAllocAgain(Middle, MiddleSize);
            Assert.Equal(0xCAFE_F00Du, GuestMemory.Read<uint>(Before + BeforeSize - 4));
            Assert.Equal(0x0BAD_CAFEu, GuestMemory.Read<uint>(After));
        }
        finally
        {
            fixture.Memory.Free(Before);
            fixture.Memory.Free(After);
        }
    }

    /// <summary>The same CDRAM is seen by every process: a block written and freed by one and allocated again reads zero
    /// through another process's view too.</summary>
    [Fact]
    public void ACdramRangeReusedReadsZeroInEveryAddressSpace()
    {
        if (!fixture.Memory.SeparateSpaces)
        {
            return; // one address space on this host
        }

        const uint Addr = CdramBlock + 0x0080_0000, Size = 0x0010_0000;
        AddressSpace other = fixture.Memory.CreateSpace("second process");
        try
        {
            Assert.Equal(Addr, fixture.Memory.TryAllocAt(Addr, Size, "first"));
            for (uint offset = 0; offset < Size; offset += 0x1000)
            {
                GuestMemory.Write(Addr + offset, 0xDEAD_BEEFu);
            }

            Assert.Equal(0xDEAD_BEEFu, *(uint*)(other.Base + Addr)); // one memory behind both views
            fixture.Memory.Free(Addr);
            Assert.Equal(Addr, fixture.Memory.TryAllocAt(Addr, Size, "second"));
            for (uint offset = 0; offset < Size; offset += 0x1000)
            {
                Assert.Equal(0u, *(uint*)(other.Base + Addr + offset));
                Assert.Equal(0u, GuestMemory.Read<uint>(Addr + offset));
            }
            fixture.Memory.Free(Addr);
        }
        finally
        {
            fixture.Memory.ReleaseSpace(other);
        }
    }

    /// <summary>A stray write to CDRAM no block holds (it still lands, data accesses to free pages are allowed) must
    /// not show up in the block allocated there later.</summary>
    [Fact]
    public void AStrayWriteToFreeCdramDoesNotLeakIntoTheNextBlock()
    {
        const uint Addr = CdramBlock + 0x00C0_0000, Size = 0x4000;
        GuestMemory.Write(Addr + 0x1000, 0xDEAD_BEEFu);
        Assert.Equal(Addr, fixture.Memory.TryAllocAt(Addr, Size, "after a stray write"));
        try
        {
            Assert.Equal(0u, GuestMemory.Read<uint>(Addr + 0x1000));
        }
        finally
        {
            fixture.Memory.Free(Addr);
        }
    }

    /// <summary>
    /// Loading a save state writes back each block page by page; its zero pages (most of a state's heaps) read zero
    /// without being touched, as in a new block: clearing them by hand made the whole of every block resident. Both the
    /// block the restore committed fresh and a block that was live with other data (a state loaded over a running game).
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RestoringAStateLeavesItsZeroPagesUntouched(bool blockWasLive)
    {
        Assert.Equal(UserBlock, fixture.Memory.TryAllocAt(UserBlock, BlockSize, "restored heap"));
        try
        {
            GuestMemory.Write(UserBlock + 3 * GuestMemory.PageSize + 8, 0x1234_5678u);
            GuestMemory.Write(UserBlock + 5 * GuestMemory.PageSize, 0xE7FD_DEFEu);
            GuestMemory.Write(UserBlock + 5 * GuestMemory.PageSize + 4, 0xE7FD_DEFEu);
            GuestMemory.Block block = fixture.Memory.FindBlock(UserBlock)!;
            byte[] data = fixture.Memory.CopyContents(block, out bool wordPages);
            if (blockWasLive)
            {
                GuestMemory.Fill(UserBlock, 0xEE, BlockSize);
            }
            else
            {
                fixture.Memory.Free(UserBlock);
                Assert.Equal(UserBlock, fixture.Memory.TryAllocAt(UserBlock, BlockSize, "restored heap"));
                block = fixture.Memory.FindBlock(UserBlock)!;
            }

            fixture.Memory.RestoreContents(block, data, wordPages);
            byte* host = fixture.Memory.Primary.Base + UserBlock;
            long resident = ResidentBytes(host, BlockSize);
            if (resident >= 0)
            {
                Assert.True(resident < BlockSize / 8, $"{resident >> 10} KB of a restored {BlockSize >> 20} MB block with two non-zero pages are resident");
            }

            Assert.Equal(0x1234_5678u, GuestMemory.Read<uint>(UserBlock + 3 * GuestMemory.PageSize + 8));
            Assert.Equal(0xE7FD_DEFEu, GuestMemory.Read<uint>(UserBlock + 5 * GuestMemory.PageSize + 4));
            Assert.Equal(0u, GuestMemory.Read<uint>(UserBlock + 5 * GuestMemory.PageSize + 8));
            for (uint offset = 0; offset < BlockSize; offset += 0x1_0000)
            {
                Assert.Equal(0u, GuestMemory.Read<uint>(UserBlock + offset));
            }

            Assert.Equal(0u, GuestMemory.Read<uint>(UserBlock + BlockSize - 4));
        }
        finally
        {
            fixture.Memory.Free(UserBlock);
        }
    }

    /// <summary>The zero pages of a restored CDRAM block (shared memory, seen by every process) read zero in every view,
    /// and the live blocks around it keep their data.</summary>
    [Fact]
    public void RestoringACdramBlockZeroesItsZeroPagesAndKeepsTheNeighbours()
    {
        const uint Before = CdramBlock + 0x00E0_0000, BeforeSize = 0x5000;
        const uint Middle = Before + BeforeSize, MiddleSize = 0x7000;
        const uint After = Middle + MiddleSize, AfterSize = 0x2000;
        Assert.Equal(Before, fixture.Memory.TryAllocAt(Before, BeforeSize, "live before"));
        Assert.Equal(Middle, fixture.Memory.TryAllocAt(Middle, MiddleSize, "restored"));
        Assert.Equal(After, fixture.Memory.TryAllocAt(After, AfterSize, "live after"));
        AddressSpace? other = fixture.Memory.SeparateSpaces ? fixture.Memory.CreateSpace("second process") : null;
        try
        {
            GuestMemory.Write(Before + BeforeSize - 4, 0xCAFE_F00Du);
            GuestMemory.Write(After, 0x0BAD_CAFEu);
            GuestMemory.Write(Middle + 2 * GuestMemory.PageSize + 0x10, 0x1357_9BDFu);
            GuestMemory.Block block = fixture.Memory.FindBlock(Middle)!;
            byte[] data = fixture.Memory.CopyContents(block, out bool wordPages);
            GuestMemory.Fill(Middle, 0xEE, MiddleSize);
            fixture.Memory.RestoreContents(block, data, wordPages);
            for (uint offset = 0; offset < MiddleSize; offset += 4)
            {
                uint expected = offset == 2 * GuestMemory.PageSize + 0x10 ? 0x1357_9BDFu : 0u;
                Assert.Equal(expected, GuestMemory.Read<uint>(Middle + offset));
                if (other != null)
                {
                    Assert.Equal(expected, *(uint*)(other.Base + Middle + offset));
                }
            }

            Assert.Equal(0xCAFE_F00Du, GuestMemory.Read<uint>(Before + BeforeSize - 4));
            Assert.Equal(0x0BAD_CAFEu, GuestMemory.Read<uint>(After));
        }
        finally
        {
            if (other != null)
            {
                fixture.Memory.ReleaseSpace(other);
            }

            fixture.Memory.Free(Before);
            fixture.Memory.Free(Middle);
            fixture.Memory.Free(After);
        }
    }

    private void AllocWriteFreeAndAllocAgain(uint addr, uint size)
    {
        Assert.Equal(addr, fixture.Memory.TryAllocAt(addr, size, "first"));
        for (uint offset = 0; offset < size; offset += 0x1000)
        {
            GuestMemory.Write(addr + offset, 0xDEAD_BEEFu);
        }

        fixture.Memory.Free(addr);
        Assert.Equal(addr, fixture.Memory.TryAllocAt(addr, size, "second"));
        try
        {
            for (uint offset = 0; offset < size; offset += 0x1000)
            {
                Assert.Equal(0u, GuestMemory.Read<uint>(addr + offset));
            }
        }
        finally
        {
            fixture.Memory.Free(addr);
        }
    }

    /// <summary>
    /// The host pages of [<paramref name="start"/>, + <paramref name="size"/>) that may hold data: committed (Windows: a
    /// section page an earlier block or a stray access committed; never-committed pages are the demand-zero page) or in
    /// memory (elsewhere: a page punched out of the memory file is neither); null when the host has no query for it.
    /// </summary>
    private static bool[]? PagesThatMayHoldData(byte* start, uint size)
    {
        int pageSize = Environment.SystemPageSize;
        if (!OperatingSystem.IsWindows())
        {
            return ResidentPages(start, size);
        }

        const uint MEM_COMMIT = 0x1000;
        bool[] committed = new bool[size / (uint)pageSize];
        nuint at = (nuint)start, end = at + size;
        while (at < end)
        {
            MemoryBasicInformation info;
            if (VirtualQuery((void*)at, &info, (nuint)sizeof(MemoryBasicInformation)) == 0)
            {
                return null;
            }

            nuint regionEnd = Math.Min(end, (nuint)info.BaseAddress + info.RegionSize);
            if (info.State == MEM_COMMIT)
            {
                for (nuint page = at; page < regionEnd; page += (nuint)pageSize)
                {
                    committed[(page - (nuint)start) / (nuint)pageSize] = true;
                }
            }

            at = regionEnd;
        }

        return committed;
    }

    /// <summary>Bytes of [<paramref name="start"/>, + <paramref name="size"/>) in the process's physical memory;
    /// -1 when the host has no query for it.</summary>
    private static long ResidentBytes(byte* start, uint size)
    {
        bool[]? pages = ResidentPages(start, size);
        if (pages == null)
        {
            return -1;
        }

        long resident = 0;
        foreach (bool page in pages)
        {
            if (page)
            {
                resident += Environment.SystemPageSize;
            }
        }

        return resident;
    }

    /// <summary>Which host pages of [<paramref name="start"/>, + <paramref name="size"/>) are in the process's physical
    /// memory; null when the host has no query for it.</summary>
    private static bool[]? ResidentPages(byte* start, uint size)
    {
        int pageSize = Environment.SystemPageSize;
        int pages = (int)(size / (uint)pageSize);
        bool[] resident = new bool[pages];
        if (OperatingSystem.IsWindows())
        {
            var info = new WorkingSetExInformation[pages];
            for (int i = 0; i < pages; i++)
            {
                info[i].VirtualAddress = (nint)(start + (long)i * pageSize);
            }

            fixed (WorkingSetExInformation* p = info)
            {
                if (!QueryWorkingSetEx(GetCurrentProcess(), p, pages * sizeof(WorkingSetExInformation)))
                {
                    return null;
                }
            }

            for (int i = 0; i < pages; i++)
            {
                resident[i] = (info[i].VirtualAttributes & 1) != 0;
            }

            return resident;
        }

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            byte[] vector = new byte[pages];
            fixed (byte* v = vector)
            {
                if (mincore(start, (nuint)size, v) != 0)
                {
                    return null;
                }
            }

            for (int i = 0; i < pages; i++)
            {
                resident[i] = (vector[i] & 1) != 0;
            }

            return resident;
        }

        return null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryBasicInformation
    {
        public nint BaseAddress;
        public nint AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public nuint RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    [LibraryImport("kernel32.dll")]
    private static partial nuint VirtualQuery(void* addr, MemoryBasicInformation* info, nuint length);

    [StructLayout(LayoutKind.Sequential)]
    private struct WorkingSetExInformation
    {
        public nint VirtualAddress;
        public nuint VirtualAttributes;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "K32QueryWorkingSetEx")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryWorkingSetEx(nint process, WorkingSetExInformation* info, int bytes);

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

    [LibraryImport("libc", SetLastError = true)]
    private static partial int mincore(void* addr, nuint length, byte* vector);
}
