using VitaSharp.Core.Hle;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using Xunit;

namespace VitaSharp.Tests;

/// <summary>
/// The rest of SceSysmemForDriver (FW 3.74): the memory block type table (memory type, privileges, physical kind,
/// debug-only types), block information, physical ranges and vectors, page attribute changes inside a block, the
/// remaps the firmware allows, user mappings into the kernel, range locks, the uncached heap, heap checks, the 64-bit
/// fill count and the PUID calls by name.
/// </summary>
[Collection("guest")]
public sealed class KernelSysmemDriverRestTests(GuestMemoryFixture fixture) : IDisposable
{
    private const uint InvalidArgument = 0x80020005, BadSize = 0x80020009, UnknownType = 0x80024B01;

    private readonly HleHarness _harness = new(fixture, "ksysmemdrv");

    public void Dispose() => _harness.Dispose();

    private unsafe uint R0(GuestThread thread, uint nid, params uint[] args)
    {
        Action<HleContext> implementation = _harness.Emu.Hle.ImplOf(nid) ?? throw new InvalidOperationException($"0x{nid:X8} not implemented");
        for (int i = 0; i < 4; i++)
        {
            thread.Core.State->R[i] = i < args.Length ? args[i] : 0xA5A5A5A5;
        }

        uint stack = thread.Core.State->R[13];
        thread.Core.State->R[13] = stack - 0x40;
        for (int i = 4; i < args.Length; i++)
        {
            GuestMemory.W32(stack - 0x40 + (uint)(i - 4) * 4, args[i]);
        }

        implementation(thread.Hle);
        thread.Core.State->R[13] = stack;
        return thread.Core.State->R[0];
    }

    /// <summary>A kernel block of the given type (ksceKernelAllocMemBlock).</summary>
    private uint KernelBlock(GuestThread thread, string name, uint type, uint size)
    {
        uint uid = R0(thread, 0xC94850C9, _harness.Str(name), type, size, 0);
        Assert.True((int)uid > 0, $"ksceKernelAllocMemBlock: 0x{uid:X8}");
        return uid;
    }

    private uint BaseOf(GuestThread thread, uint uid)
    {
        uint baseOut = _harness.Word(0);
        Assert.Equal(0u, R0(thread, 0xA841EDDA, uid, baseOut));
        return GuestMemory.R32(baseOut);
    }

    [Fact]
    public void TheTypeTableGivesMemoryTypePrivilegesAndPhysicalKind()
    {
        _harness.OnGuest(t =>
        {
            Assert.Equal(0xD000u, R0(t, 0x20C811FA, 0x0C20D060)); // user read-write, normal memory
            Assert.Equal(0x8000u, R0(t, 0x20C811FA, 0x09408060)); // CDRAM
            Assert.Equal(UnknownType, R0(t, 0x20C811FA, 0x12345678));
            Assert.Equal(UnknownType, R0(t, 0x20C811FA, 0x10F0D005)); // debug-only without the switch 0xD2
            Assert.Equal(0x30u, R0(t, 0x6A0792A3, 0x0C20D060)); // user read + write
            Assert.Equal(5u, R0(t, 0x6A0792A3, 0x1020D005)); // kernel read + execute
            Assert.Equal(0x200000u, R0(t, 0xCB0F3A33, 0x0C20D060));
            Assert.Equal(0x400000u, R0(t, 0xCB0F3A33, 0x09408060));
            Assert.Equal(0u, R0(t, 0xCB0F3A33, 0x12345678));
        });
    }

    [Fact]
    public void ABlocksInformationAndPhysicalRangesAreItsOwn()
    {
        _harness.OnGuest(t =>
        {
            uint uid = KernelBlock(t, "info", 0x1020D006, 0x2000);
            uint blockBase = BaseOf(t, uid);
            Assert.Equal(0xD000u, R0(t, 0xF3BBE2E1, blockBase + 0x10));
            uint size = _harness.Word(0);
            Assert.Equal(InvalidArgument, R0(t, 0x78337B62, uid, 0));
            Assert.Equal(0x80024701u, R0(t, 0x78337B62, 0x7FFFFFF1, size));
            Assert.Equal(0u, R0(t, 0x78337B62, uid, size));
            Assert.Equal(0x2000u, GuestMemory.R32(size));

            uint info = _harness.Alloc(0xB8);
            GuestMemory.W32(info, 0xB4);
            Assert.Equal(BadSize, R0(t, 0x24A99FFF, uid, info));
            GuestMemory.W32(info, 0xB8);
            Assert.Equal(0u, R0(t, 0x24A99FFF, uid, info));
            Assert.Equal(0x1020D006u, GuestMemory.R32(info + 4));
            Assert.Equal(uid, GuestMemory.R32(info + 8));
            Assert.Equal("info", GuestMemory.ReadCString(GuestMemory.R32(info + 0xC)));
            Assert.Equal(blockBase, GuestMemory.R32(info + 0x10));
            Assert.Equal(1u, GuestMemory.R32(info + 0x30));
            Assert.Equal(0x2000u, GuestMemory.R32(info + 0x78));
            Assert.Equal(InvalidArgument, R0(t, 0xA73CFFEF, uid, 8, info));
            Assert.Equal(0u, R0(t, 0xA73CFFEF, uid, 7, info));

            uint pair = _harness.Alloc(8);
            Assert.Equal(0u, R0(t, 0x98C15666, uid, pair));
            Assert.Equal(blockBase, GuestMemory.R32(pair));
            Assert.Equal(0x2000u, GuestMemory.R32(pair + 4));
            uint vector = _harness.Alloc(0x14);
            uint pairs = _harness.Alloc(16);
            GuestMemory.W32(vector, 0x14);
            GuestMemory.W32(vector + 4, 2);
            GuestMemory.W32(vector + 0x10, pairs);
            Assert.Equal(0u, R0(t, 0x19A51AC7, uid, vector));
            Assert.Equal(1u, GuestMemory.R32(vector + 8));
            Assert.Equal(1u, GuestMemory.R32(vector + 0xC));
            Assert.Equal(blockBase, GuestMemory.R32(pairs));
            GuestMemory.W32(vector + 0x10, 0);
            Assert.Equal(InvalidArgument, R0(t, 0x19A51AC7, uid, vector));
        });
    }

    [Fact]
    public void PageAttributesChangeOnlyInsideTheBlockAndWithTheirFlags()
    {
        _harness.OnGuest(t =>
        {
            uint uid = KernelBlock(t, "pages", 0x1020D006, 0x3000);
            uint blockBase = BaseOf(t, uid);
            Assert.Equal(0u, R0(t, 0x16713BE8, uid, blockBase + 0x1000, 0x1000, 2));
            Assert.Equal(InvalidArgument, R0(t, 0x16713BE8, uid, blockBase + 0x1000, 0x1000, 1));
            Assert.Equal(InvalidArgument, R0(t, 0x16713BE8, uid, blockBase + 0x800, 0x1000, 0)); // not page-aligned
            Assert.Equal(InvalidArgument, R0(t, 0x8C43B052, uid, blockBase + 0x2000, 0x2000, 0)); // past the block
            Assert.Equal(0u, R0(t, 0x13805CA8, uid, blockBase, 0x3000, 8));
            Assert.Equal(InvalidArgument, R0(t, 0x13805CA8, uid, blockBase, 0x3000, 2));
            Assert.Equal(0u, R0(t, 0x6C76AD89, uid, blockBase, 0x1000, 4));
            Assert.Equal(0u, R0(t, 0x4C584B29, uid, blockBase, 0x1000, 0x2020));
            Assert.Equal(InvalidArgument, R0(t, 0x4C584B29, uid, blockBase, 0x1000, 0x3000));
        });
    }

    [Fact]
    public void OnlyTheFirmwaresRemapsAreAllowed()
    {
        _harness.OnGuest(t =>
        {
            uint uid = KernelBlock(t, "text", 0x1020D006, 0x1000);
            Assert.Equal(UnknownType, R0(t, 0xDFE2C8CB, uid, 0x12345678));
            Assert.Equal(0x80024B02u, R0(t, 0xDFE2C8CB, uid, 0x0C20D060));
            Assert.Equal(0u, R0(t, 0xDFE2C8CB, uid, 0x1020D005)); // read-write-execute to read-execute
            Assert.Equal(0x80024B02u, R0(t, 0xDFE2C8CB, uid, 0x1020D006)); // and never back
            Assert.Equal(0xD000u, R0(t, 0x20C811FA, 0x1020D005));
        });
    }

    [Fact]
    public void AUserMappingCoversTheBuffersPagesUntilUnmapped()
    {
        _harness.OnGuest(t =>
        {
            uint buffer = _harness.Alloc(0x3000) + 0x123;
            uint page = _harness.Word(0);
            uint size = _harness.Word(0);
            uint offset = _harness.Word(0);
            Assert.Equal(InvalidArgument, R0(t, 0x7D4F8B5F, _harness.Str("map"), 1, 2, buffer, 0x1000, page, size, offset));
            Assert.Equal(InvalidArgument, R0(t, 0x278BC201, _harness.Str("map"), 1, buffer, 0x1000, page, size, 0));
            uint uid = R0(t, 0x278BC201, _harness.Str("map"), 1, buffer, 0x1000, page, size, offset);
            Assert.True((int)uid > 0);
            Assert.Equal(buffer & ~0xFFFu, GuestMemory.R32(page));
            Assert.Equal(0x2000u, GuestMemory.R32(size));
            Assert.Equal(0x123u, GuestMemory.R32(offset));
            Assert.Equal(0u, R0(t, 0x00575B00, uid));
            Assert.NotEqual(0u, R0(t, 0x00575B00, uid));
        });
    }

    [Fact]
    public void MainMemoryNeverNeedsALockAndOtherRangesMustTranslate()
    {
        _harness.OnGuest(t =>
        {
            Assert.Equal(1u, R0(t, 0x59A4402F, 0x40000000, 0x1000));
            uint buffer = _harness.Alloc(0x1000);
            Assert.Equal(0u, R0(t, 0xBC0A1D60, 0, buffer, 0x1000));
            Assert.Equal(0u, R0(t, 0x22CBE925, 0, buffer, 0x1000));
            Assert.Equal(InvalidArgument, R0(t, 0xF4AD89D8, (uint)t.Pid, 0x33, buffer, 0));
        });
    }

    [Fact]
    public void TheHeapCheckCountsItsBlocksAndKnowsThem()
    {
        _harness.OnGuest(t =>
        {
            uint heap = R0(t, 0x9328E0E8, _harness.Str("checked"), 0x1000, 0);
            Assert.True((int)heap > 0);
            uint block = R0(t, 0x7B4CB60A, heap, 0x40);
            Assert.NotEqual(0u, block);
            Assert.Equal(1u, R0(t, 0xC50A9C0D, heap, 0));
            Assert.Equal(1u, R0(t, 0xC50A9C0D, heap, block));
            Assert.Equal(0x80024904u, R0(t, 0xC50A9C0D, heap, block + 8));
            Assert.Equal(0u, R0(t, 0x856FA2E3, heap));
            Assert.Equal(0x80024901u, R0(t, 0x856FA2E3, 0x7FFFFFF1));

            uint uncached = R0(t, 0x7750CEA7, 0x100);
            Assert.NotEqual(0u, uncached);
            Assert.Equal(0u, R0(t, 0xFB817A59, uncached));
            uint option = _harness.Alloc(0x14);
            GuestMemory.W32(option, 0x10);
            Assert.Equal(0u, R0(t, 0x0B4ED16A, 0x100, option));
        });
    }

    [Fact]
    public void TheFillCountsStopAtTheFirstOtherUnit()
    {
        _harness.OnGuest(t =>
        {
            uint buffer = _harness.Alloc(0x20);
            for (uint i = 0; i < 3; i++)
            {
                GuestMemory.Write(buffer + i * 8, 0x1122334455667788UL);
            }

            Assert.Equal(0x18u, R0(t, 0xBB3B02C2, buffer, 0, 0x55667788, 0x11223344, 0x20));
            Assert.Equal(4u, R0(t, 0xBDA6E42B, buffer, 0x55667788, 0x20));
            Assert.Equal(0x18u, R0(t, 0xE83855FD, (uint)t.Pid, buffer, 0x55667788, 0x11223344, 0x20));
        });
    }

    [Fact]
    public void PuidsAreFoundAndNamedByName()
    {
        _harness.OnGuest(t =>
        {
            uint uid = KernelBlock(t, "SceNamedBlock", 0x1020D006, 0x1000);
            Assert.Equal(InvalidArgument, R0(t, 0x513B9DDD, (uint)KernelSpace.KernelPid, _harness.Str("SceNamedBlock")));
            Assert.Equal(InvalidArgument, R0(t, 0x513B9DDD, (uint)t.Pid, 0));
            uint puid = R0(t, 0x513B9DDD, (uint)t.Pid, _harness.Str("SceNamedBlock"));
            Assert.Equal(uid, puid);
            uint name = _harness.Word(0);
            Assert.Equal(0u, R0(t, 0x09896EB7, (uint)t.Pid, puid, name));
            Assert.Equal("SceNamedBlock", GuestMemory.ReadCString(GuestMemory.R32(name)));
            Assert.Equal(0x80024501u, R0(t, 0x09896EB7, (uint)t.Pid, 2, name)); // not a UID
            Assert.Equal(0u, R0(t, 0x12624884, (uint)t.Pid, puid, _harness.Str("SceRenamed")));
            Assert.Equal(0u, R0(t, 0x09896EB7, (uint)t.Pid, puid, name));
            Assert.Equal("SceRenamed", GuestMemory.ReadCString(GuestMemory.R32(name)));
            Assert.Equal(uid, R0(t, 0x184172B1, (uint)t.Pid, puid, 0, 0));
            Assert.Equal(InvalidArgument, R0(t, 0x184172B1, (uint)KernelSpace.KernelPid, puid, 0, 0));
        });
    }
}
