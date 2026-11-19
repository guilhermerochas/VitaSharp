using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// SceNetPsForDriver (SceNetPs 3.74): ksceNetClose is the socket close, and with no network driver registered the
/// interface detach and the hook and gateway unregistrations answer 0x80418101; NID_E41C0794 is empty.
/// </summary>
[Collection("guest")]
public sealed class NetPsKernelTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int Invalid = unchecked((int)0x80418101);

    private readonly HleHarness _harness = new(fixture, "netpsk");

    public void Dispose() => _harness.Dispose();

    private SystemServicesVsh Services => _harness.Module<SystemServicesVsh>();

    [Theory]
    [InlineData(0x21F4428Du)]
    [InlineData(0xF5788FF8u)]
    [InlineData(0xC000CA8Fu)]
    [InlineData(0x8C4346CAu)]
    [InlineData(0xE41C0794u)]
    public void EveryExportHasAnEntry(uint nid)
    {
        var bindings = HleBindings.Modules.SelectMany(m => m.Bindings())
            .Where(b => b.Nid == nid && b.Library == "SceNetPsForDriver").ToList();
        Assert.Single(bindings);
        Assert.False(bindings[0].Default);
    }

    [Fact]
    public void NothingIsRegisteredToUndo()
    {
        uint driver = _harness.Alloc(0x80);
        Assert.Equal(Invalid, Call(Services, "NetPsDetachInterface", driver));
        Assert.Equal(Invalid, Call(Services, "NetPsUnregisterHook", 0u));
        Assert.Equal(Invalid, Call(Services, "NetPsUnregisterHook", driver));
        Assert.Equal(Invalid, Call(Services, "NetPsUnregisterGateway", driver));
        Assert.Equal(0, Call(Services, "NetPsNop"));
    }
}
