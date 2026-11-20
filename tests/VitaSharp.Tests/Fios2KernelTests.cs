using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// SceFios2KernelForDriver (SceFios2Kernel 3.74): the kernel entries of the overlay table the user calls use (one
/// table), with the module's checks: NULL outputs 0x80820006, unknown ids 0x8082001A, a type above 3 0x8082001A,
/// unterminated paths 0x80820018, a 65th application overlay 0x80820019, the resolve buffer and range checks; the
/// thread's "application overlays disabled" flag; the overlay directory handles over the I/O manager.
/// </summary>
[Collection("guest")]
public sealed class Fios2KernelTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int Null = unchecked((int)0x80820006), NotFound = unchecked((int)0x8082001A);
    private const int PathTooLong = unchecked((int)0x80820018), TableFull = unchecked((int)0x80820019);
    private const int BufferTooLarge = unchecked((int)0x80820008), BadRange = unchecked((int)0x8082001B);
    private const int BadHandle = unchecked((int)0x8082000C);

    private readonly HleHarness _harness = new(fixture, "fios2k");

    public void Dispose() => _harness.Dispose();

    private Fios2User Fios => _harness.Module<Fios2User>();

    private uint Overlay(byte type, byte order, string dst, string src)
    {
        uint overlay = _harness.Alloc(0x258);
        GuestMemory.Fill(overlay, 0, 0x258);
        GuestMemory.Write(overlay, type);
        GuestMemory.Write(overlay + 1, order);
        GuestMemory.WriteCString(overlay + 0x10, dst, 292);
        GuestMemory.WriteCString(overlay + 0x134, src, 292);
        return overlay;
    }

    private int Add(byte order, string dst, string src)
    {
        uint id = _harness.Alloc(4);
        Assert.Equal(0, Call(Fios, "ksceFiosKernelOverlayAddForProcess", 0, Overlay(0, order, dst, src), id));
        return (int)GuestMemory.R32(id);
    }

    private string Resolve(string path, uint minOrder = 0, uint maxOrder = 0xFF)
    {
        uint output = _harness.Alloc(0x400);
        Assert.Equal(0, Call(Fios, "ksceFiosKernelOverlayResolveWithRangeSync", 0, 0, _harness.Str(path), output, 0x400u, minOrder, maxOrder));
        return GuestMemory.ReadCString(output, 0x400);
    }

    [Theory]
    [InlineData(0x03727E5Eu)]
    [InlineData(0x0F456345u)]
    [InlineData(0x17E65A1Cu)]
    [InlineData(0x23247EFBu)]
    [InlineData(0x2368FEB5u)]
    [InlineData(0x241BF0D6u)]
    [InlineData(0x2607EE4Cu)]
    [InlineData(0x2649408Bu)]
    [InlineData(0x28E28A58u)]
    [InlineData(0x35E7E75Cu)]
    [InlineData(0x3B329E86u)]
    [InlineData(0x725E6817u)]
    [InlineData(0x7F8B960Cu)]
    [InlineData(0x853EA82Au)]
    [InlineData(0x990F46A5u)]
    [InlineData(0xD3D968FCu)]
    [InlineData(0xDD7627ECu)]
    [InlineData(0xE71192C5u)]
    [InlineData(0xF1762BC2u)]
    [InlineData(0xFF42AAF0u)]
    public void EveryExportHasAnEntry(uint nid)
    {
        var bindings = HleBindings.Modules.SelectMany(m => m.Bindings())
            .Where(b => b.Nid == nid && b.Library == "SceFios2KernelForDriver").ToList();
        Assert.Single(bindings);
    }

    [Fact]
    public void KernelAndUserCallsShareOneTable()
    {
        int id = Add(0x10, "app0:", "ux0:patch/X");
        Assert.Equal("ux0:patch/X/eboot.bin", Fios.Resolve("app0:eboot.bin"));
        uint info = _harness.Alloc(0x258);
        Assert.Equal(0, Call(Fios, "_sceFiosKernelOverlayGetInfo", id, info));
        Assert.Equal("ux0:patch/X", GuestMemory.ReadCString(info + 0x134, 292));
        Assert.Equal(0, Call(Fios, "_sceFiosKernelOverlayRemove", id));
        Assert.Equal(NotFound, Call(Fios, "ksceFiosKernelOverlayRemoveForProcess", 0, id));
        Assert.Equal("app0:eboot.bin", Fios.Resolve("app0:eboot.bin"));
    }

    [Fact]
    public void UnknownIdsAreNotFoundForUserAndKernelCalls()
    {
        uint info = _harness.Alloc(0x258);
        Assert.Equal(NotFound, Call(Fios, "ksceFiosKernelOverlayGetInfo", 77, info));
        Assert.Equal(NotFound, Call(Fios, "ksceFiosKernelOverlayRemove", 77));
        Assert.Equal(NotFound, Call(Fios, "ksceFiosKernelOverlayModify", 77, Overlay(0, 1, "a:", "b:")));
        Assert.Equal(NotFound, Call(Fios, "_sceFiosKernelOverlayGetInfo", 77, info));
        Assert.Equal(NotFound, Call(Fios, "_sceFiosKernelOverlayRemove", 77));
        Assert.Equal(NotFound, Call(Fios, "_sceFiosKernelOverlayModify", 77, Overlay(0, 1, "a:", "b:")));
    }

    [Fact]
    public void ArgumentsAndOverlaysAreChecked()
    {
        uint id = _harness.Alloc(4);
        uint good = Overlay(0, 1, "app0:", "ux0:x");
        Assert.Equal(Null, Call(Fios, "ksceFiosKernelOverlayAdd", 0u, id));
        Assert.Equal(Null, Call(Fios, "ksceFiosKernelOverlayAdd", good, 0u));
        Assert.Equal(Null, Call(Fios, "ksceFiosKernelOverlayAddForProcess", 0, good, 0u));
        Assert.Equal(NotFound, Call(Fios, "ksceFiosKernelOverlayAdd", Overlay(4, 1, "app0:", "ux0:x"), id));
        uint unterminated = Overlay(0, 1, "app0:", "ux0:x");
        GuestMemory.Fill(unterminated + 0x134, (byte)'a', 292);
        Assert.Equal(PathTooLong, Call(Fios, "ksceFiosKernelOverlayAdd", unterminated, id));
        Assert.Equal(Null, Call(Fios, "ksceFiosKernelOverlayGetInfo", 1, 0u));
        Assert.Equal(Null, Call(Fios, "ksceFiosKernelOverlayModifyForProcess", 0, 1, 0u));
        Assert.Equal(Null, Call(Fios, "ksceFiosKernelOverlayGetList", 0, 0u, 0xFFu, 0u, 4u, 0u));
    }

    [Fact]
    public void AProcessHoldsSixtyFourApplicationOverlays()
    {
        for (int i = 0; i < 64; i++)
        {
            Add(0x10, $"app0:d{i}", "ux0:x");
        }

        uint id = _harness.Alloc(4);
        Assert.Equal(TableFull, Call(Fios, "ksceFiosKernelOverlayAdd", Overlay(0, 0x10, "app0:e", "ux0:x"), id));
        Assert.Equal(0, Call(Fios, "ksceFiosKernelOverlayAdd", Overlay(0, 0x80, "app0:e", "ux0:x"), id));
    }

    [Fact]
    public void TheListIsZeroedAndFilteredByOrder()
    {
        int low = Add(0x10, "app0:", "ux0:a");
        int high = Add(0x90, "app0:", "ux0:b");
        uint ids = _harness.Alloc(16);
        uint actual = _harness.Alloc(4);
        GuestMemory.Fill(ids, 0xAA, 16);
        Assert.Equal(0, Call(Fios, "ksceFiosKernelOverlayGetList", 0, 0x80u, 0xFFu, ids, 4u, actual));
        Assert.Equal(1u, GuestMemory.R32(actual));
        Assert.Equal((uint)high, GuestMemory.R32(ids));
        Assert.Equal(0u, GuestMemory.R32(ids + 4));
        Assert.Equal(0, Call(Fios, "ksceFiosKernelOverlayGetList", 0, 0u, 0xFFu, ids, 4u, actual));
        Assert.Equal(2u, GuestMemory.R32(actual));
        Assert.Equal((uint)low, GuestMemory.R32(ids));
    }

    [Fact]
    public void ResolveChecksItsBufferAndRange()
    {
        Add(0x10, "app0:", "ux0:a");
        Add(0x90, "ux0:a", "ux0:b");
        uint input = _harness.Str("app0:x");
        uint output = _harness.Alloc(0x400);
        Assert.Equal(Null, Call(Fios, "ksceFiosKernelOverlayResolveSync", 0, 0, 0u, output, 0x100u));
        Assert.Equal(Null, Call(Fios, "ksceFiosKernelOverlayResolveSync", 0, 0, input, 0u, 0x100u));
        Assert.Equal(BufferTooLarge, Call(Fios, "ksceFiosKernelOverlayResolveSync", 0, 0, input, output, 0x401u));
        Assert.Equal(PathTooLong, Call(Fios, "ksceFiosKernelOverlayResolveSync", 0, 0, input, output, 0u));
        Assert.Equal(PathTooLong, Call(Fios, "ksceFiosKernelOverlayResolveSync", 0, 0, input, output, 6u));
        Assert.Equal(BadRange, Call(Fios, "ksceFiosKernelOverlayResolveWithRangeSync", 0, 0, input, output, 0x100u, 0x20u, 0x10u));
        Assert.Equal(0, Call(Fios, "ksceFiosKernelOverlayResolveSync", 0, 0, input, output, 0x100u));
        Assert.Equal("ux0:b/x", GuestMemory.ReadCString(output, 0x400));
        Assert.Equal("ux0:a/x", Resolve("app0:x", 0, 0x7F));
    }

    [Fact]
    public void DisabledApplicationOverlaysLeaveOnlyThePrivilegedOnes()
    {
        Add(0x10, "app0:", "ux0:a");
        Add(0x90, "app0:", "ux0:b");
        Assert.Equal(0, Call(Fios, "ksceFiosKernelOverlayThreadIsDisabled"));
        Assert.Equal(0, Call(Fios, "ksceFiosKernelOverlayThreadSetDisabled", 1));
        Assert.Equal(1, Call(Fios, "ksceFiosKernelOverlayThreadIsDisabled"));
        Assert.Equal("ux0:b/x", Resolve("app0:x"));
        Assert.Equal(0, Call(Fios, "ksceFiosKernelOverlayThreadSetDisabled", 0));
        Assert.Equal("ux0:a/x", Resolve("app0:x", 0, 0x7F));
    }

    [Fact]
    public void TheSchedulerIsRecommendedForHostPaths()
    {
        Assert.Equal(1, Call(Fios, "ksceFiosKernelOverlayGetRecommendedScheduler", 2, _harness.Str("host0:x")));
        Assert.Equal(0, Call(Fios, "ksceFiosKernelOverlayGetRecommendedScheduler", 1, _harness.Str("host0:x")));
        Assert.Equal(0, Call(Fios, "ksceFiosKernelOverlayGetRecommendedScheduler", 2, _harness.Str("ux0:x")));
        Assert.Equal(0, Call(Fios, "ksceFiosKernelOverlayGetRecommendedScheduler", 2, 0u));
    }

    [Fact]
    public void ADirectoryHandleReadsTheDirectoryAndCloses()
    {
        string? host = _harness.Emu.Vfs.Resolve("ux0:fiosdh", true);
        Assert.NotNull(host);
        Directory.CreateDirectory(host!);
        File.WriteAllText(Path.Combine(host!, "one.txt"), "1");
        uint handleOut = _harness.Alloc(4);
        Assert.Equal(Null, Call(Fios, "ksceFiosKernelOverlayDHOpenSync", 0u, _harness.Str("ux0:fiosdh"), 0u, 0u));
        Assert.Equal(0, Call(Fios, "ksceFiosKernelOverlayDHOpenSync", handleOut, _harness.Str("ux0:fiosdh"), 0u, 0u));
        int handle = (int)GuestMemory.R32(handleOut);
        uint dirent = _harness.Alloc(0x200);
        Assert.Equal(1, Call(Fios, "ksceFiosKernelOverlayDHReadSync", handle, dirent));
        Assert.Equal("one.txt", GuestMemory.ReadCString(dirent + 88, 256));
        Assert.Equal(0, Call(Fios, "ksceFiosKernelOverlayDHReadSync", handle, dirent));
        uint stat = _harness.Alloc(0x58);
        Assert.Equal(0, Call(Fios, "ksceFiosKernelOverlayDHStatSync", handle, stat));
        Assert.Equal(0x1186u, GuestMemory.R32(stat));
        Assert.Equal(0, Call(Fios, "ksceFiosKernelOverlayDHCloseSync", handle));
        Assert.Equal(BadHandle, Call(Fios, "ksceFiosKernelOverlayDHCloseSync", handle));
        Assert.Equal(BadHandle, Call(Fios, "ksceFiosKernelOverlayDHReadSync", handle, dirent));
        Assert.Equal(BadHandle, Call(Fios, "ksceFiosKernelOverlayDHSyncSync", handle, 0));
        Assert.Equal(Null, Call(Fios, "ksceFiosKernelOverlayDHStatSync", handle, 0u));
    }
}
