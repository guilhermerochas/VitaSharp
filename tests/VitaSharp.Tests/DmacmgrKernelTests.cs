using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>SceDmacmgrForDriver (dmacmgr.skprx 3.74): the channel reset's kind and channel checks (the sixth controller
/// only with its debug switches), the entry that always refuses.</summary>
[Collection("guest")]
public sealed class DmacmgrKernelTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int Invalid = unchecked((int)0x80020005);

    private readonly HleHarness _harness = new(fixture, "dmack");

    public void Dispose() => _harness.Dispose();

    private SystemServicesVsh Services => _harness.Module<SystemServicesVsh>();

    [Theory]
    [InlineData(0xBCBB5E85u)]
    [InlineData(0x893FCB80u)]
    public void EveryEntryExists(uint nid)
    {
        var bindings = HleBindings.Modules.SelectMany(m => m.Bindings())
            .Where(b => b.Nid == nid && b.Library == "SceDmacmgrForDriver").ToList();
        Assert.Single(bindings);
    }

    [Theory]
    [InlineData(0u, 0u, 0)]
    [InlineData(0u, 1u, 0)]
    [InlineData(0u, 2u, Invalid)]
    [InlineData(0u, 0xFFFFu, 0)]
    [InlineData(1u, 1u, 0)]
    [InlineData(1u, 2u, Invalid)]
    [InlineData(4u, 15u, 0)]
    [InlineData(4u, 16u, Invalid)]
    [InlineData(4u, 0xFFFFu, 0)]
    [InlineData(5u, 1u, 0)]
    [InlineData(6u, 0u, Invalid)]
    [InlineData(7u, 0u, Invalid)]
    public void TheChannelResetChecksKindAndChannel(uint kind, uint channel, int expected)
    {
        Assert.Equal(expected, Call(Services, "ksceDmacResetChannel", kind, channel));
    }

    [Fact]
    public void TheSixthControllerNeedsItsDebugSwitch()
    {
        Call(Services, "ksceKernelSetDipsw", 0xC1u);
        Assert.Equal(0, Call(Services, "ksceDmacResetChannel", 6u, 1u));
        Assert.Equal(Invalid, Call(Services, "ksceDmacResetChannel", 6u, 2u));
        Assert.Equal(unchecked((int)0x80020002), Call(Services, "ksceDmacUnsupported"));
    }
}
