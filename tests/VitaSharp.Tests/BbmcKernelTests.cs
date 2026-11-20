using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// SceBbmcForDriver (SceBbmc 3.74) on a unit without a 3G modem: 0x80330003, 0x80330002 for a NULL data pointer
/// where it is checked first, and the modem power line's reference count.
/// </summary>
[Collection("guest")]
public sealed class BbmcKernelTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int NoModem = unchecked((int)0x80330003);
    private const int BbmcArgument = unchecked((int)0x80330002);

    private readonly HleHarness _harness = new(fixture, "kbbmc");

    public void Dispose() => _harness.Dispose();

    private SystemServicesVsh Services => _harness.Module<SystemServicesVsh>();

    private static List<HleBinding> BindingsOf(string library, uint nid) => HleBindings.Modules
        .SelectMany(m => m.Bindings()).Where(b => b.Nid == nid && b.Library == library).ToList();

    private static void AssertEveryExportBound(string library, uint[] nids, int expected)
    {
        Assert.Equal(expected, nids.Distinct().Count());
        foreach (uint nid in nids)
        {
            int count = BindingsOf(library, nid).Count;
            Assert.True(count == 1, $"{library} 0x{nid:X8}: {count} entries");
        }
    }

    // ------------------------------------------------------------------ SceBbmcForDriver

    private static readonly uint[] s_bbmc =
    [
        0x009C73D2, 0x07ADDA90, 0x0819A4DD, 0x0AF6520F, 0x192D641F, 0x1B122CE5, 0x24EE6E9C, 0x2930DB51, 0x2CD4131A,
        0x393E70CB, 0x399EF002, 0x3D06FDB0, 0x3D27000A, 0x4281E476, 0x435858B1, 0x44217A55, 0x454DF20B, 0x46C5F794,
        0x503C7E0B, 0x580A3C06, 0x683D19BF, 0x6A56F88C, 0x6AD4053A, 0x6F3697F6, 0x739274F3, 0x75FA68D3, 0x77030C3C,
        0x7CB72009, 0x7EA964B6, 0x864E6CA0, 0x8820F7C4, 0x891AFA0C, 0x8AAC6F1E, 0x902D3CAC, 0x92D7C12C, 0x93E5345A,
        0x94A31A9C, 0x9611C253, 0xA41FA509, 0xA5E182D0, 0xA9467106, 0xA9CF664F, 0xACB0EC17, 0xB17A056F, 0xB7E650A9,
        0xBC3C8942, 0xBF550F4A, 0xC35ECB1F, 0xC835DB15, 0xD16D0845, 0xD288E771, 0xD98DB926, 0xDCE38C84, 0xE09808A2,
        0xE39F8435, 0xE767F32C, 0xE83B73CA, 0xE89B746D, 0xEBC5D3DB, 0xF2DABC5B, 0xF4C8482D, 0xFB3DA35E, 0xFCF6A1EA,
    ];

    [Fact]
    public void EveryBbmcExportHasAnEntry()
    {
        AssertEveryExportBound("SceBbmcForDriver", s_bbmc, 63);
        // everything but the power line is the no-modem default
        foreach (uint nid in s_bbmc)
        {
            bool powerLine = nid is 0xD98DB926 or 0xD16D0845;
            Assert.Equal(!powerLine, BindingsOf("SceBbmcForDriver", nid)[0].Default);
        }
    }

    [Fact]
    public void BbmcAnswersWithoutAModem()
    {
        Assert.Equal(NoModem, Call(Services, "ksceBbmcNoModem"));
        // the four that check their data pointer first
        Assert.Equal(BbmcArgument, Call(Services, "ksceBbmcNoModemData", 1u, 0u));
        Assert.Equal(NoModem, Call(Services, "ksceBbmcNoModemData", 1u, _harness.Alloc(0x10)));
    }

    [Fact]
    public void BbmcPowerLineIsAReferenceCount()
    {
        Assert.Equal(0, Services.BbmcPowerReferences);
        Assert.Equal(0, Call(Services, "ksceBbmcPowerLineRelease")); // none held: nothing changes
        Assert.Equal(0, Services.BbmcPowerReferences);
        Assert.Equal(0, Call(Services, "ksceBbmcPowerLineAcquire"));
        Assert.Equal(0, Call(Services, "ksceBbmcPowerLineAcquire"));
        Assert.Equal(2, Services.BbmcPowerReferences);
        Assert.Equal(0, Call(Services, "ksceBbmcPowerLineRelease"));
        Assert.Equal(1, Services.BbmcPowerReferences);
    }
}
