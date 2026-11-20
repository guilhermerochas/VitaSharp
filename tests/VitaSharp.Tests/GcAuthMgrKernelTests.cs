using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// SceSblGcAuthMgrDrmBBForDriver, SclkForDriver and MlnpsnlForDriver as gcauthmgr.skprx 3.74 answers on a unit with no
/// game card authenticated: the context setup it does in its own memory, a clear card secret, no hash that matches
/// (never a game card license validated), and every MAC, cipher and exchange step "not supported" after its checks.
/// </summary>
[Collection("guest")]
public sealed class GcAuthMgrKernelTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int Invalid = unchecked((int)0x808A040A), Alignment = unchecked((int)0x808A0406);
    private const int Unsupported = unchecked((int)0x80020004);

    private readonly HleHarness _harness = new(fixture, "gcauthk");

    public void Dispose() => _harness.Dispose();

    private SystemServicesVsh Services => _harness.Module<SystemServicesVsh>();

    [Theory]
    [InlineData(0x48D7784Eu)]
    [InlineData(0x500F5157u)]
    [InlineData(0x6EF3C9DBu)]
    [InlineData(0x3C25F9FAu)]
    [InlineData(0xE950BE32u)]
    [InlineData(0x7F98E4E2u)]
    [InlineData(0xC0F37F18u)]
    [InlineData(0x6E6D2B89u)]
    [InlineData(0xB13577E2u)]
    [InlineData(0x30A0B441u)]
    [InlineData(0x4CEA150Cu)]
    [InlineData(0x050DC6DFu)]
    [InlineData(0x4B506BE7u)]
    [InlineData(0xBB70DDC0u)]
    [InlineData(0x22FD5D23u)]
    [InlineData(0xBB451E83u)]
    [InlineData(0x812B2B5Cu)]
    [InlineData(0x535B87BCu)]
    [InlineData(0x4C5DE1AAu)]
    [InlineData(0x4FE89ADBu)]
    [InlineData(0x11D5570Bu)]
    [InlineData(0xF723A9BAu)]
    [InlineData(0xDABC01F5u)]
    [InlineData(0x296ABD68u)]
    public void Exports_are_served(uint nid)
    {
        Assert.NotNull(_harness.Emu.Hle.ImplOf(nid));
    }

    [Fact]
    public void Contexts_are_set_up_in_the_callers_memory()
    {
        uint context = _harness.Alloc(0x28);
        GuestMemory.Fill(context, 0xEE, 0x28);
        Assert.Equal(Invalid, Call(Services, "GcAuthBbMacInit", 0u, 2u));
        Assert.Equal(0, Call(Services, "GcAuthBbMacInit", context, 2u));
        Assert.Equal(2u, GuestMemory.R32(context));
        Assert.Equal(new byte[0x24], GuestMemory.ReadSpan(context + 4, 0x24).ToArray());
        GuestMemory.Fill(context, 0xEE, 0x18);
        Assert.Equal(0, Call(Services, "GcAuthBbCipherClear", context));
        Assert.Equal(new byte[0x18], GuestMemory.ReadSpan(context, 0x18).ToArray());
        Assert.Equal(Invalid, Call(Services, "GcAuthBbCipherClear", 0u));
    }

    [Fact]
    public void No_game_card_is_ever_authenticated()
    {
        uint secret = _harness.Alloc(0x20);
        GuestMemory.Fill(secret, 0xEE, 0x20);
        Assert.Equal(0, Call(Services, "ksceSblGcAuthMgrDrmBBGetCartSecret", secret));
        Assert.Equal(new byte[0x20], GuestMemory.ReadSpan(secret, 0x20).ToArray());
        // a zero hash is the cleared card's: still refused, no game card license may pass
        Assert.Equal(Invalid, Call(Services, "ksceSblGcAuthMgrDrmBBCheckCartHash", _harness.Alloc(0x14)));
        Assert.Equal(0, Call(Services, "ksceSblGcAuthMgrDrmBBClearCartSecret"));
        uint revoked = _harness.Word(0xFFFFFFFF);
        Assert.Equal(0, Call(Services, "GcAuthDrmBbRevocation", 0u, revoked));
        Assert.Equal(0u, GuestMemory.R32(revoked));
        Assert.Equal(Invalid, Call(Services, "GcAuthDrmBbNoCard"));
    }

    [Fact]
    public void Mac_cipher_and_exchanges_need_the_secure_processor()
    {
        uint context = _harness.Alloc(0x28);
        uint data = _harness.Alloc(0x40);
        Assert.Equal(Invalid, Call(Services, "GcAuthBbMac", context, 0u));
        Assert.Equal(Unsupported, Call(Services, "GcAuthBbMac", context, data));
        Assert.Equal(Invalid, Call(Services, "GcAuthBbCipherInit", context, 0u, 1u, 0u));
        Assert.Equal(Unsupported, Call(Services, "GcAuthBbCipherInit", context, 0u, 1u, data));
        Assert.Equal(Alignment, Call(Services, "GcAuthBbCipherUpdate", context, data, 0x11u));
        Assert.Equal(Unsupported, Call(Services, "GcAuthBbCipherUpdate", context, data, 0x10u));
        Assert.Equal(Unsupported, Call(Services, "GcAuthDrmBbSecure"));
        Assert.Equal(Unsupported, Call(Services, "GcAuthSecureExchange"));
    }
}
