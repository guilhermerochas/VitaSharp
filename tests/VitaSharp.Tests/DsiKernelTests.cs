using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// SceDsiForDriver (lowio.skprx 3.74): the fifteen-mode table (the panel's modes, 960x544 or 960x540, picked by the
/// panel type; the HDMI head's CEA modes), the head, lane, pixel size and mode checks, the pixel clock by pixel size, enabling and
/// starting a head.
/// </summary>
[Collection("guest")]
public sealed class DsiKernelTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int Head = unchecked((int)0x803F0600), Mode = unchecked((int)0x803F0601);
    private const int Setting = unchecked((int)0x803F0602), NotEnabled = unchecked((int)0x803F0603);

    private readonly HleHarness _harness = new(fixture, "dsik");

    public void Dispose() => _harness.Dispose();

    private SystemServicesVsh Services => _harness.Module<SystemServicesVsh>();

    [Theory]
    [InlineData(0x114D1413u)]
    [InlineData(0x3FB0DF1Fu)]
    [InlineData(0x4DF9E924u)]
    [InlineData(0x5BE5AA9Bu)]
    [InlineData(0x6F8029A1u)]
    [InlineData(0x7640F607u)]
    [InlineData(0x78E6E3CFu)]
    [InlineData(0x8610B795u)]
    [InlineData(0x89C00D2Fu)]
    [InlineData(0x97BFEA76u)]
    [InlineData(0x98120684u)]
    [InlineData(0xB3A70C05u)]
    [InlineData(0xBA6BC89Fu)]
    [InlineData(0xC2E85919u)]
    [InlineData(0xF2921E29u)]
    public void EveryExportHasAnEntry(uint nid)
    {
        var bindings = HleBindings.Modules.SelectMany(m => m.Bindings())
            .Where(b => b.Nid == nid && b.Library == "SceDsiForDriver").ToList();
        Assert.Single(bindings);
    }

    private (int Width, int Height) Resolution(uint vic)
    {
        uint width = _harness.Word(0);
        uint height = _harness.Word(0);
        Assert.Equal(0, Call(Services, "ksceDsiGetVicResolution", vic, width, height));
        return ((int)GuestMemory.R32(width), (int)GuestMemory.R32(height));
    }

    [Fact]
    public void TheTableGivesThePanelAndHdmiResolutions()
    {
        Assert.Equal((960, 544), Resolution(0));
        Assert.Equal((960, 540), Resolution(0x80));
        Assert.Equal((960, 544), Resolution(0x20));
        Assert.Equal((1280, 720), Resolution(0x8900));
        Assert.Equal((720, 480), Resolution(0x8300));
        Assert.Equal((720, 576), Resolution(0x8480));
        Assert.Equal((1920, 1080), Resolution(0x8500));
        Assert.Equal((1280, 720), Resolution(0x8600));
        Assert.Equal((1920, 1080), Resolution(0x8710));
        Assert.Equal((1920, 1080), Resolution(0x8730));
        Assert.Equal(Mode, Call(Services, "ksceDsiGetVicResolution", 0x1234u, 0u, 0u));
    }

    [Fact]
    public void ThePanelTypePicksThePanelsModes()
    {
        Assert.Equal(Head, Call(Services, "ksceDsiSetPanelType", 2u, 0u));
        Assert.Equal(0, Call(Services, "ksceDsiSetPanelType", 0u, 1u));
        Assert.Equal((960, 544), Resolution(0));
        Assert.Equal(Mode, Call(Services, "ksceDsiGetVicResolution", 0x80u, 0u, 0u));
        Assert.Equal(0, Call(Services, "ksceDsiSetPanelType", 1u, 1u));
        Assert.Equal(Mode, Call(Services, "ksceDsiGetVicResolution", 0u, 0u, 0u));
        Assert.Equal((1920, 1080), Resolution(0x8500));
        Assert.Equal(0, Call(Services, "ksceDsiSetPanelType", 0u, 0u));
        Assert.Equal((960, 544), Resolution(0x20));
    }

    [Fact]
    public void LanesPixelSizeAndThePixelClock()
    {
        Assert.Equal(Setting, Call(Services, "ksceDsiSetLanesAndPixelSize", 0u, 3u, 24u));
        Assert.Equal(Setting, Call(Services, "ksceDsiSetLanesAndPixelSize", 1u, 4u, 24u));
        Assert.Equal(Setting, Call(Services, "ksceDsiSetLanesAndPixelSize", 1u, 2u, 16u));
        Assert.Equal(0, Call(Services, "ksceDsiSetLanesAndPixelSize", 0u, 2u, 24u));
        Assert.Equal(0, Call(Services, "ksceDsiSetLanesAndPixelSize", 1u, 3u, 30u));
        Assert.Equal(Mode, Call(Services, "ksceDsiGetPixelClock", 1u));
        Assert.Equal(Mode, Call(Services, "ksceDsiSetVic", 1u, 0x1234u));
        Assert.Equal(0, Call(Services, "ksceDsiSetVic", 1u, 0x8500u));
        Assert.Equal(3708791, Call(Services, "ksceDsiGetPixelClock", 1u));
        Assert.Equal(0, Call(Services, "ksceDsiSetVic", 0u, 0u));
        Assert.Equal(2243100, Call(Services, "ksceDsiGetPixelClock", 0u));
    }

    [Fact]
    public void AHeadIsEnabledBeforeItStarts()
    {
        Assert.Equal(Mode, Call(Services, "ksceDsiStartDisplay", 0u, 0u));
        Assert.Equal(NotEnabled, Call(Services, "ksceDsiGetScanline", 0u));
        Assert.Equal(0, Call(Services, "ksceDsiEnableHead", 0u));
        Assert.Equal(Setting, Call(Services, "ksceDsiStartDisplay", 0u, 5u));
        Assert.Equal(0, Call(Services, "ksceDsiStartDisplay", 0u, 4u));
        Assert.Equal(0, Call(Services, "ksceDsiGetScanline", 0u));
        Assert.Equal(0, Call(Services, "ksceDsiDisableHead", 0u));
        Assert.Equal(Mode, Call(Services, "ksceDsiStartDisplay", 0u, 0u));
        Assert.Equal(Head, Call(Services, "ksceDsiEnableHead", 2u));
    }
}
