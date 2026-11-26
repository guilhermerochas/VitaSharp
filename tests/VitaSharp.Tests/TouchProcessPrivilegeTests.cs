using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Loader.Self;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// sceTouchSetProcessPrivilege (FW 3.74 SceTouch 0x609EA489): a system program stores the value in its process's touch
/// control block and gets 0; any other caller gets 0x80350002. The shell calls it with 0 each time it takes the screen
/// back (after the PS button): it got 0x80350002 because every caller did.
/// </summary>
[Collection("guest")]
public sealed class TouchProcessPrivilegeTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int PrivilegeRequired = unchecked((int)0x80350002);
    private const ulong GameAuthorityId = 0x210000101CC404D8UL;

    private readonly HleHarness _harness = new(fixture, "touchpriv");

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void OnlyASystemProgramSetsItsTouchPrivilege()
    {
        Touch touch = _harness.Module<Touch>();

        _harness.Emu.Access = new ProgramAccess(GameAuthorityId, new byte[ProgramAccess.CapabilitySize], false);
        Assert.Equal(PrivilegeRequired, Call(touch, "sceTouchSetProcessPrivilege", 1));
        Assert.Equal(0, touch.ProcessPrivilege);

        byte[] capability = new byte[ProgramAccess.CapabilitySize];
        capability[0] = 0x40;
        _harness.Emu.Access = new ProgramAccess(0x2800000000000025UL, capability, false);
        Assert.True(_harness.Emu.Access.IsSystemProgram);
        Assert.Equal(0, Call(touch, "sceTouchSetProcessPrivilege", 1));
        Assert.Equal(1, touch.ProcessPrivilege);
        Assert.Equal(0, Call(touch, "sceTouchSetProcessPrivilege", 0));
        Assert.Equal(0, touch.ProcessPrivilege);
    }
}
