using VitaSharp.Core.Cpu;
using VitaSharp.Core.Memory;
using Xunit;

namespace VitaSharp.Tests;

/// <summary>
/// Per-process address spaces: private user memory at the same guest address holds different data in each space, the
/// shared regions (CDRAM, kernel) are one memory seen by all, and CPU code bound to a space reads and writes there.
/// </summary>
[Collection("guest")]
public sealed unsafe class AddressSpaceTests(GuestMemoryFixture fx)
{
    // a user range high enough that nothing else in the test process uses it
    const uint Fixed = 0xD000_0000, FixedSize = 0x20000;
    const uint Code = 0x0090_0000;

    /// <summary>A second space; the tests pass trivially on hosts that cannot map shared memory twice (one space).</summary>
    AddressSpace? NewSpace() => fx.Memory.SeparateSpaces ? fx.Memory.CreateSpace("test") : null;

    [Fact]
    public void FixedUserAddressesArePrivatePerSpace()
    {
        if (NewSpace() is not { } b) return;
        var a = fx.Memory.Primary;
        try
        {
            Assert.Equal(Fixed, fx.Memory.TryAllocAt(Fixed, FixedSize, "a"));
            GuestMemory.Write(Fixed, 0xAAAA_AAAAu);
            using (AddressSpace.Use(b))
            {
                // taken in the first space: only a fixed (non-exclusive) request gets it, in this space
                Assert.Equal(0u, fx.Memory.TryAllocAt(Fixed, FixedSize, "b exclusive"));
                Assert.Equal(Fixed, fx.Memory.TryAllocAt(Fixed, FixedSize, "b", exclusive: false));
                Assert.Equal(0u, GuestMemory.Read<uint>(Fixed)); // fresh memory, not the other space's
                GuestMemory.Write(Fixed, 0xBBBB_BBBBu);
                Assert.Equal("b", fx.Memory.FindBlock(Fixed)!.Name);
                // a relocatable allocation never lands on addresses another space uses
                uint r = fx.Memory.Alloc(0x1000, "b reloc");
                Assert.True(r < Fixed || r >= Fixed + FixedSize);
                fx.Memory.Free(r);
            }
            Assert.Equal(0xAAAA_AAAAu, GuestMemory.Read<uint>(Fixed));
            Assert.Equal("a", fx.Memory.FindBlock(Fixed)!.Name);
            using (AddressSpace.Use(b)) Assert.Equal(0xBBBB_BBBBu, GuestMemory.Read<uint>(Fixed));

            // the range becomes free for everyone only when both spaces released it
            fx.Memory.Free(Fixed);
            Assert.Equal(0u, fx.Memory.TryAllocAt(Fixed, FixedSize, "a again"));
            using (AddressSpace.Use(b)) fx.Memory.Free(Fixed);
            Assert.Equal(Fixed, fx.Memory.TryAllocAt(Fixed, FixedSize, "a again"));
            fx.Memory.Free(Fixed);
        }
        finally
        {
            using (AddressSpace.Use(b)) fx.Memory.Free(Fixed);
            using (AddressSpace.Use(a)) fx.Memory.Free(Fixed);
            fx.Memory.ReleaseSpace(b);
        }
    }

    [Fact]
    public void SharedRegionsAreTheSameMemoryEverywhere()
    {
        if (NewSpace() is not { } b) return;
        uint cdram = fx.Memory.Alloc(0x10000, "shared cdram", MemoryRegion.Cdram);
        uint kernel = fx.Memory.Alloc(0x1000, "shared kernel", MemoryRegion.Kernel);
        try
        {
            GuestMemory.Write(cdram + 0x8000, 0x1234_5678u);
            using (AddressSpace.Use(b))
            {
                Assert.Equal(0x1234_5678u, GuestMemory.Read<uint>(cdram + 0x8000));
                GuestMemory.Write(kernel, 0xCAFE_F00Du);
                Assert.NotNull(fx.Memory.FindBlock(cdram));
                Assert.True(GuestMemory.IsMapped(cdram));
            }
            Assert.Equal(0xCAFE_F00Du, GuestMemory.Read<uint>(kernel));
        }
        finally
        {
            fx.Memory.Free(cdram);
            fx.Memory.Free(kernel);
            fx.Memory.ReleaseSpace(b);
        }
    }

    [Fact]
    public void DirtyPagesOfEverySpaceAreSwept()
    {
        if (NewSpace() is not { } b) return;
        try
        {
            using (AddressSpace.Use(b)) Assert.True(fx.Memory.MapFixed(Code + 0x10000, 0x1000, "dirty"));
            uint stamp = DirtyPages.Sweep();
            Assert.False(DirtyPages.WrittenSince(Code + 0x10000, 4, stamp + 1));
            using (AddressSpace.Use(b)) GuestMemory.Write(Code + 0x10000, 1u);
            uint e = DirtyPages.Sweep();
            Assert.True(DirtyPages.WrittenSince(Code + 0x10000, 4, e));
        }
        finally
        {
            using (AddressSpace.Use(b)) fx.Memory.Free(Code + 0x10000);
            fx.Memory.ReleaseSpace(b);
        }
    }

    /// <summary>The same code address in two spaces holds two programs; each core runs its own space's (interpreter
    /// and native backend) and stores into its own space.</summary>
    [Fact]
    public void CpuRunsInItsSpace()
    {
        if (NewSpace() is not { } b) return;
        var a = fx.Memory.Primary;
        var spaces = new[] { a, b };
        try
        {
            for (int i = 0; i < 2; i++)
                using (AddressSpace.Use(spaces[i]))
                {
                    Assert.True(fx.Memory.MapFixed(Code, 0x2000, $"code {i}"));
                    // movw r1, #(0x1111 * (i + 1)); str r1, [r0]; svc #0
                    uint imm = 0x1111u * (uint)(i + 1);
                    GuestMemory.Write(Code, 0xE3001000u | ((imm >> 12) << 16) | (imm & 0xFFF));
                    GuestMemory.Write(Code + 4, 0xE5801000u);
                    GuestMemory.Write(Code + 8, 0xEF000000u);
                }
            for (int i = 0; i < 2; i++)
            {
                for (int native = 0; native < 2; native++)
                {
                    var cache = new BlockCache();
                    using var core = new CpuCore(cache, spaces[i].Base);
                    using (AddressSpace.Use(spaces[i])) // blocks are built on the process's own threads
                    {
                        var block = cache.Get(Code, false);
                        if (native == 1)
                        {
                            if (Jit.Chain.Length == 0 || Jit.Chain[0].Name != "native") continue;
                            Jit.Compile(block);
                            if (block.NativeEntry == 0) continue;
                        }
                    }
                    var s = core.State;
                    s->R[0] = Code + 0x1000;
                    s->R[15] = Code;
                    Assert.Equal(ExitReason.Svc, core.Run());
                }
                using (AddressSpace.Use(spaces[i])) Assert.Equal(0x1111u * (uint)(i + 1), GuestMemory.Read<uint>(Code + 0x1000));
            }
        }
        finally
        {
            foreach (var sp in spaces) using (AddressSpace.Use(sp)) fx.Memory.Free(Code);
            fx.Memory.ReleaseSpace(b);
        }
    }
}
