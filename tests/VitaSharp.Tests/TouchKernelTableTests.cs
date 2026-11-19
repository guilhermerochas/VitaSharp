using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// SceTouchForDriver (SceTouch 3.74): the region clear of sceTouchClearRegion and its kernel side (port 0..1, region
/// 0..7; the shell's region entry goes away), NID_9C8CCB66's two bytes per port and NID_3F3FBF69's table.
/// </summary>
[Collection("guest")]
public sealed class TouchKernelTableTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int Invalid = unchecked((int)0x80350001);

    private readonly HleHarness _harness = new(fixture, "touchk2");

    public void Dispose() => _harness.Dispose();

    private SystemServicesVsh Services => _harness.Module<SystemServicesVsh>();

    private Touch Touch => _harness.Module<Touch>();

    [Theory]
    [InlineData(0xE1C5D95Fu)]
    [InlineData(0x9C8CCB66u)]
    [InlineData(0x3F3FBF69u)]
    [InlineData(0x328E38B0u)]
    [InlineData(0x062A11A2u)]
    [InlineData(0x845E2115u)]
    [InlineData(0x63DCF2FEu)]
    [InlineData(0xDDCF5145u)]
    [InlineData(0x423F26D3u)]
    [InlineData(0x4E6C4558u)]
    public void EveryExportHasAnEntry(uint nid)
    {
        var bindings = HleBindings.Modules.SelectMany(m => m.Bindings())
            .Where(b => b.Nid == nid && b.Library == "SceTouchForDriver").ToList();
        Assert.Single(bindings);
        Assert.False(bindings[0].Default);
    }

    [Fact]
    public void ClearRegionChecksAndClearsTheShellsRegion()
    {
        Assert.Equal(Invalid, Call(Touch, "sceTouchClearRegion", 2u, 0u));
        Assert.Equal(Invalid, Call(Touch, "sceTouchClearRegion", 0u, 8u));
        _harness.Emu.IsShell = true;
        uint rect = _harness.Alloc(8);
        GuestMemory.Write<ushort>(rect + 4, 100);
        GuestMemory.Write<ushort>(rect + 6, 50);
        Assert.Equal(0, Call(Touch, "sceTouchSetRegion", 0, 2, rect, 0));
        Assert.True(_harness.Emu.Vita.HasShellTouchRegion(0, 2));
        Assert.Equal(0, Call(Touch, "sceTouchClearRegion", 1u, 2u)); // the other port's region 2
        Assert.True(_harness.Emu.Vita.HasShellTouchRegion(0, 2));
        Assert.Equal(0, Call(Touch, "sceTouchClearRegion", 0u, 2u));
        Assert.False(_harness.Emu.Vita.HasShellTouchRegion(0, 2));
    }

    [Fact]
    public void PortBytesAndTheTableAreKept()
    {
        Assert.Equal(Invalid, Call(Services, "TouchSetPortBytes", 2u, 1u, 2u));
        Assert.Equal(0, Call(Services, "TouchSetPortBytes", 1u, 0x1234u, 0x56u));
        Assert.Equal((ushort)0x5634, Services.TouchKernelTable.Back);
        Assert.Equal((ushort)0, Services.TouchKernelTable.Front);

        Assert.Equal(Invalid, Call(Services, "TouchSetTable", 0u));
        uint table = _harness.Alloc(4 * 20);
        GuestMemory.W32(table, 19);
        for (uint word = 1; word <= 19; word++)
        {
            GuestMemory.W32(table + word * 4, 0x100 + word);
        }

        Assert.Equal(0, Call(Services, "TouchSetTable", table));
        Assert.Equal(19u, Services.TouchKernelTable.TableCount);
        Assert.Equal(0x101u, Services.TouchKernelTable.FirstWord);
    }

    [Fact]
    public void TheEnableFlagAndTheUpdateModeFollowTheModule()
    {
        Assert.Equal(Invalid, Call(Services, "ksceTouchSetEnableFlag", 2u, 0));
        Assert.Equal(0, Call(Services, "ksceTouchSetEnableFlag", 1u, 0));
        Assert.Equal((byte)2, Services.TouchKernelMode.Back);
        Assert.Equal(0, Call(Services, "ksceTouchSetEnableFlag", 1u, 1));
        Assert.Equal((byte)0, Services.TouchKernelMode.Back);

        const int NotUpdating = unchecked((int)0x80350103);
        Assert.Equal(1, Call(Services, "TouchIsInitialized"));
        Assert.Equal(NotUpdating, Call(Services, "TouchLeaveUpdateMode"));
        Assert.Equal(NotUpdating, Call(Services, "TouchResetPanels", 3u));
        Assert.Equal(0, Call(Services, "TouchEnterUpdateMode"));
        Assert.Equal(0, Call(Services, "TouchResetPanels", 3u));
        Assert.Equal(0, Call(Services, "TouchLeaveUpdateMode"));
        Assert.Equal(2, Services.TouchKernelMode.Mode);
        Assert.Equal(NotUpdating, Call(Services, "TouchLeaveUpdateMode"));

        Invoke(Services, "TouchSetWords", 0x77u);
        Assert.Equal(0x77u, Services.TouchKernelMode.Word);
    }
}
