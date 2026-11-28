using VitaSharp.Core.Cpu;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// Mirror blocks and ksceKernelUnmapMemBlock (FW 3.74 SceSysmem): ksceKernelAllocMemBlock with HAS_MIRROR_BLOCKID
/// (attr 0x40, the source at option +0x20) gives the source's physical memory at an address of its own, so a store
/// through one address is read at the other and the translation of the mirror is the source's physical address;
/// freeing the source unmaps its mirrors ("Force sceKernelUnmapMemBlock"); ksceKernelUnmapMemBlock keeps the block but
/// its pages stop translating and an access to them is a data abort, while a mirror of the same memory stays mapped.
/// </summary>
[Collection("guest")]
public sealed class KernelSysmemMirrorTests(GuestMemoryFixture fixture) : IDisposable
{
    private const uint CdramUserType = 0x09404060, UncachedUserType = 0x0C208060, KernelCdramType = 0x40404006;
    private const uint HasMirror = 0x40, HasPid = 0x80, OptSize = 0x58;
    private const int InvalidArgument = unchecked((int)0x80020005), NoTranslation = unchecked((int)0x80022007);

    private readonly HleHarness _harness = new(fixture, "ksysmemmirror");

    public void Dispose() => _harness.Dispose();

    private SysmemKernel Sysmem => _harness.Module<SysmemKernel>();

    private int Alloc(string name, uint type, uint size, uint opt = 0) =>
        Call(Sysmem, "ksceKernelAllocMemBlock", _harness.Str(name), type, size, opt);

    /// <summary>The option SceCompat 3.74 passes for a mirror: size 0x58, attr HAS_MIRROR_BLOCKID | HAS_PID, the
    /// source at +0x20.</summary>
    private uint MirrorOption(int source)
    {
        uint opt = _harness.Alloc(OptSize);
        GuestMemory.Fill(opt, 0, OptSize);
        GuestMemory.W32(opt, OptSize);
        GuestMemory.W32(opt + 8, HasMirror | HasPid | 0x1000000);
        GuestMemory.W32(opt + 0x20, (uint)source);
        return opt;
    }

    private uint BaseOf(int uid)
    {
        uint baseOut = _harness.Word(0);
        Assert.Equal(0, Call(Sysmem, "ksceKernelGetMemBlockBase", uid, baseOut));
        return GuestMemory.R32(baseOut);
    }

    private int VAtoPA(uint address, out uint physical)
    {
        uint physicalOut = _harness.Word(0xFFFFFFFF);
        int result = Call(Sysmem, "ksceKernelVAtoPA", address, physicalOut);
        physical = GuestMemory.R32(physicalOut);
        return result;
    }

    /// <summary>What the interpreter's and IL backend's guard (and the native guard, which reads the same byte) do
    /// with an access once data aborts are armed: true when it raises a data abort.</summary>
    private static unsafe bool AccessAborts(uint address, bool write)
    {
        CpuState state = default;
        state.MemBase = GuestMemory.Base;
        state.AbortGuard = 1;
        try
        {
            DataAborts.Check(&state, address, 4, write);
            return false;
        }
        catch (GuestDataAbortException)
        {
            Assert.Equal(address, state.AbortAddr);
            return true;
        }
    }

    [Fact]
    public void AStoreThroughAMirrorIsReadAtItsSourceAndBack()
    {
        _harness.OnGuest(thread =>
        {
            int source = Alloc("cached", CdramUserType, 0x20000);
            Assert.True(source > 0, $"0x{source:X8}");
            int uncached = Alloc("uncached", UncachedUserType, 0x20000, MirrorOption(source));
            int kernelView = Alloc("kernel view", KernelCdramType, 0x20000, MirrorOption(source));
            Assert.True(uncached > 0, $"0x{uncached:X8}");
            Assert.True(kernelView > 0, $"0x{kernelView:X8}");
            uint sourceBase = BaseOf(source), mirrorBase = BaseOf(uncached), kernelBase = BaseOf(kernelView);
            Assert.NotEqual(sourceBase, mirrorBase);
            Assert.NotEqual(mirrorBase, kernelBase);

            GuestMemory.W32(sourceBase + 0x1234, 0xCAFEF00D);
            Assert.Equal(0xCAFEF00Du, GuestMemory.R32(mirrorBase + 0x1234));
            Assert.Equal(0xCAFEF00Du, GuestMemory.R32(kernelBase + 0x1234));
            GuestMemory.W32(kernelBase + 0x1FFFC, 0x12345678);
            Assert.Equal(0x12345678u, GuestMemory.R32(sourceBase + 0x1FFFC));
            Assert.Equal(0x12345678u, GuestMemory.R32(mirrorBase + 0x1FFFC));

            // one physical memory: the mirror translates to the source's physical address
            Assert.True(VAtoPA(mirrorBase + 0x40, out uint physical) > 0);
            Assert.True(VAtoPA(sourceBase + 0x40, out uint sourcePhysical) > 0);
            Assert.Equal(sourcePhysical, physical);
            Assert.True(GuestMemory.IsPageMapped(mirrorBase));
            Assert.False(AccessAborts(mirrorBase + 0x10, write: true));

            Assert.Equal(0, Call(Sysmem, "ksceKernelFreeMemBlock", kernelView));
            Assert.Equal(0, Call(Sysmem, "ksceKernelFreeMemBlock", uncached));
            Assert.Equal(0, Call(Sysmem, "ksceKernelFreeMemBlock", source));
            Assert.False(GuestMemory.IsPageMapped(mirrorBase));
        });
    }

    [Fact]
    public void AMirrorNeedsAnExistingMappedSourceThatHoldsItsSize()
    {
        _harness.OnGuest(thread =>
        {
            int source = Alloc("source", CdramUserType, 0x10000);
            Assert.True(Alloc("no source", UncachedUserType, 0x10000, MirrorOption(0x7FFF0001)) < 0);
            Assert.Equal(InvalidArgument, Alloc("too big", UncachedUserType, 0x20000, MirrorOption(source)));
            Assert.Equal(0, Call(Sysmem, "ksceKernelUnmapMemBlock", source));
            Assert.Equal(InvalidArgument, Alloc("unmapped source", UncachedUserType, 0x10000, MirrorOption(source)));
            Assert.Equal(0, Call(Sysmem, "ksceKernelFreeMemBlock", source));
        });
    }

    [Fact]
    public void FreeingTheSourceUnmapsItsMirrorsWhichStayUntilFreed()
    {
        _harness.OnGuest(thread =>
        {
            int source = Alloc("source", CdramUserType, 0x10000);
            int mirror = Alloc("mirror", UncachedUserType, 0x10000, MirrorOption(source));
            uint mirrorBase = BaseOf(mirror);
            Assert.Equal(0, Call(Sysmem, "ksceKernelFreeMemBlock", source));
            Assert.False(GuestMemory.IsPageMapped(mirrorBase));
            Assert.True(AccessAborts(mirrorBase, write: false));
            Assert.Equal(mirrorBase, BaseOf(mirror)); // the block is still there
            Assert.Equal(0, Call(Sysmem, "ksceKernelFreeMemBlock", mirror));

            // its window range is free again: a new mirror can take it
            int again = Alloc("again", CdramUserType, 0x10000);
            int again2 = Alloc("mirror again", UncachedUserType, 0x10000, MirrorOption(again));
            Assert.True(again2 > 0);
            Assert.Equal(0, Call(Sysmem, "ksceKernelFreeMemBlock", again2));
            Assert.Equal(0, Call(Sysmem, "ksceKernelFreeMemBlock", again));
        });
    }

    [Fact]
    public void AnUnmappedBlocksPagesAbortAndNoLongerTranslateWhileItsMirrorStaysMapped()
    {
        _harness.OnGuest(thread =>
        {
            int source = Alloc("cached", CdramUserType, 0x20000);
            int mirror = Alloc("kernel view", KernelCdramType, 0x20000, MirrorOption(source));
            uint sourceBase = BaseOf(source), mirrorBase = BaseOf(mirror);
            GuestMemory.W32(sourceBase + 0x100, 0xA5A5A5A5);
            Assert.False(AccessAborts(sourceBase + 0x100, write: false));
            Assert.True(VAtoPA(sourceBase, out _) > 0);

            Assert.Equal(0, Call(Sysmem, "ksceKernelUnmapMemBlock", source));
            for (uint offset = 0; offset < 0x20000; offset += GuestMemory.PageSize)
            {
                Assert.False(GuestMemory.IsPageMapped(sourceBase + offset));
            }

            Assert.True(AccessAborts(sourceBase + 0x100, write: false));
            Assert.True(AccessAborts(sourceBase + 0x1FFF0, write: true));
            Assert.Equal(NoTranslation, VAtoPA(sourceBase + 0x100, out uint cleared));
            Assert.Equal(0u, cleared);
            Assert.Equal(sourceBase, BaseOf(source)); // the block, its base and memory remain
            Assert.Equal(0, Call(Sysmem, "ksceKernelUnmapMemBlock", source)); // again: 0

            // the mirror of the same memory keeps its own mapping and the data
            Assert.False(AccessAborts(mirrorBase + 0x100, write: false));
            Assert.Equal(0xA5A5A5A5u, GuestMemory.R32(mirrorBase + 0x100));

            // the F8 capture's context lists both: the source unmapped, the view as its mirror and mapped
            List<string> lines = Core.Capture.CaptureContext.MemBlockLines(_harness.Emu);
            Assert.Contains(lines, l => l.Contains("'cached'") && l.Contains($"0x{sourceBase:X8}") && l.EndsWith(" unmapped"));
            Assert.Contains(lines, l => l.Contains("'kernel view'") && l.Contains($"mirror of 'cached' (uid 0x{source:X}") && l.EndsWith(" mapped"));

            Assert.Equal(0, Call(Sysmem, "ksceKernelFreeMemBlock", mirror));
            Assert.Equal(0, Call(Sysmem, "ksceKernelFreeMemBlock", source));
        });
    }

    [Fact]
    public void UnmappingAPrivateUserBlockMakesItsPagesAbort()
    {
        _harness.OnGuest(thread =>
        {
            int block = Alloc("user", 0x0C20D060, 0x3000);
            uint blockBase = BaseOf(block);
            Assert.False(AccessAborts(blockBase + 0x2000, write: true));
            Assert.Equal(0, Call(Sysmem, "ksceKernelUnmapMemBlock", block));
            Assert.True(AccessAborts(blockBase + 0x2000, write: true));
            Assert.Equal(0, Call(Sysmem, "ksceKernelFreeMemBlock", block));
        });
    }

    /// <summary>A write through a mirror counts as a write of its source for the GPU's page versions (and back).</summary>
    [Fact]
    public void TheDirtyPageSweepGivesAMirrorsWritesToItsSource()
    {
        _harness.OnGuest(thread =>
        {
            int source = Alloc("cached", CdramUserType, 0x10000);
            int mirror = Alloc("uncached", UncachedUserType, 0x10000, MirrorOption(source));
            uint sourceBase = BaseOf(source), mirrorBase = BaseOf(mirror);
            DirtyPages.Sweep();
            uint stamp = DirtyPages.Sweep();
            Assert.False(DirtyPages.WrittenSince(sourceBase + 0x4000, 4, stamp + 1));
            GuestMemory.W32(mirrorBase + 0x4000, 1);
            DirtyPages.Sweep();
            Assert.True(DirtyPages.WrittenSince(sourceBase + 0x4000, 4, stamp + 1));
            Assert.Equal(0, Call(Sysmem, "ksceKernelFreeMemBlock", mirror));
            Assert.Equal(0, Call(Sysmem, "ksceKernelFreeMemBlock", source));
        });
    }
}
