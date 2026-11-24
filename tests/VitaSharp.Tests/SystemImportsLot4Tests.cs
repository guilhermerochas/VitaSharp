using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// System-imported functions without public names of SceNpTrophy and SceNpSnsFacebook, each as its FW 3.74 module
/// answers (np_trophy, np_sns_facebook read in their code); without the services the requests end neutrally.
/// </summary>
[Collection("guest")]
public sealed class SystemImportsLot4Tests(GuestMemoryFixture fixture) : IDisposable
{
    private readonly HleHarness _harness = new(fixture, "sys-lot4");
    private readonly PsnAccount _savedAccount = PsnAccount.Current;

    public void Dispose()
    {
        PsnAccount.Current = _savedAccount;
        _harness.Dispose();
    }

    [Theory]
    [InlineData(0x17FB1D9Eu, false)] // SceNpTrophy
    [InlineData(0x44E0F73Eu, false)]
    [InlineData(0xFD34F7CFu, false)]
    [InlineData(0xC80794CFu, false)]
    [InlineData(0x10371F25u, false)]
    [InlineData(0x97D19AB6u, false)]
    [InlineData(0x10182F1Bu, true)]
    [InlineData(0x2B0CC831u, true)]
    [InlineData(0x3AC04020u, true)]
    [InlineData(0x62E37599u, true)]
    [InlineData(0x2EE76D88u, true)]
    [InlineData(0xC338E582u, true)]
    [InlineData(0x061DE3F5u, true)]
    [InlineData(0x0AC3CEA8u, true)]
    [InlineData(0xCCDEB18Bu, true)]
    [InlineData(0xD70DAFA7u, true)]
    [InlineData(0x9C6AAB52u, false)] // SceNpSnsFacebook
    [InlineData(0x06A167FCu, false)]
    [InlineData(0xA9C661E9u, true)]
    [InlineData(0x501CCC39u, true)]
    [InlineData(0xFB99FF8Eu, true)]
    [InlineData(0x6CAB3583u, false)]
    [InlineData(0x3574800Cu, false)]
    [InlineData(0x8770E114u, true)]
    [InlineData(0xC9ACC1C1u, true)]
    [InlineData(0x46C23078u, true)]
    [InlineData(0x5069C04Eu, true)]
    [InlineData(0x64E5B424u, true)]
    [InlineData(0x9A0A4832u, true)]
    [InlineData(0x6A170F5Du, true)]
    [InlineData(0xB22D89FDu, true)]
    [InlineData(0xFD842B21u, true)]
    [InlineData(0x4A453D97u, true)]
    [InlineData(0xCB2ED352u, true)]
    [InlineData(0x18041141u, true)]
    [InlineData(0xABF56333u, true)]
    [InlineData(0xC10D390Cu, true)]
    [InlineData(0x9DE0B1BCu, true)]
    [InlineData(0xFA6FBAAEu, true)]
    [InlineData(0x089FB01Eu, false)] // SceCommonDialog
    [InlineData(0x35387E0Cu, false)]
    [InlineData(0x396D7B68u, false)]
    [InlineData(0x460FDFF7u, false)]
    [InlineData(0x463DB9EDu, false)]
    [InlineData(0x47E7AD8Eu, false)]
    [InlineData(0x500229F0u, false)]
    [InlineData(0x631DC45Cu, false)]
    [InlineData(0x63B08CA3u, false)]
    [InlineData(0x64F65B8Du, false)]
    [InlineData(0x675248CAu, false)]
    [InlineData(0x7030E4A6u, false)]
    [InlineData(0x76A52CDEu, false)]
    [InlineData(0x9FA21E09u, false)]
    [InlineData(0xA68A44EAu, false)]
    [InlineData(0xA785D111u, false)]
    [InlineData(0xC2D6F7F2u, false)]
    [InlineData(0xC36C7DD1u, false)]
    [InlineData(0xE7F645AEu, false)]
    [InlineData(0xED2F1495u, false)]
    [InlineData(0xEFABDD6Fu, false)]
    [InlineData(0xF2F0049Eu, false)]
    [InlineData(0xFA26EB30u, false)]
    [InlineData(0xFCDA2F0Du, false)]
    public void TheLibrarysSystemFunctionsHaveAnEntry(uint nid, bool isDefault)
    {
        var bindings = HleBindings.Modules.SelectMany(m => m.Bindings()).Where(b => b.Nid == nid).ToList();
        Assert.NotEmpty(bindings);
        Assert.All(bindings, b => Assert.Equal(isDefault, b.Default));
        Assert.NotNull(_harness.Emu.Hle.ImplOf(nid));
    }

    [Fact]
    public void TrophySystemCallsCheckInitArgumentsHandleAndContext()
    {
        NpTrophy t = _harness.Module<NpTrophy>();
        const int NotInit = unchecked((int)0x80551601), Invalid = unchecked((int)0x80551604), Handle = unchecked((int)0x80551608),
            Context = unchecked((int)0x80551609);
        uint out1 = _harness.Word(0), a = _harness.Alloc(16);
        Assert.Equal(NotInit, Call(t, "NpTrophyCreateSystemHandle", out1));
        Assert.Equal(NotInit, Call(t, "NpTrophySystemRequestCCDEB18B", 1, a));
        Assert.Equal(0, Call(t, "sceNpTrophyInit", 0u));
        Assert.Equal(Invalid, Call(t, "NpTrophyCreateSystemHandle", 0u));
        Assert.Equal(0, Call(t, "NpTrophyCreateSystemHandle", out1));
        int handle = (int)GuestMemory.R32(out1);
        Assert.True(handle > 0);

        Assert.Equal(Invalid, Call(t, "NpTrophySystemRequestCCDEB18B", -1, a));
        Assert.Equal(Invalid, Call(t, "NpTrophySystemRequestCCDEB18B", handle, 0u));
        Assert.Equal(Handle, Call(t, "NpTrophySystemRequestCCDEB18B", handle + 100, a));
        Assert.Equal(0, Call(t, "NpTrophySystemRequestCCDEB18B", handle, a));
        Assert.Equal(Invalid, Call(t, "NpTrophySystemRequestD70DAFA7", handle, 0x100000u, 0u, 0u, 1UL, a));
        Assert.Equal(0, Call(t, "NpTrophySystemRequestD70DAFA7", handle, 0x100000u, 0u, 0u, 0UL, a));
        Assert.Equal(Context, Call(t, "NpTrophySystemRequest10182F1B", handle, a, 77, a));
        Assert.Equal(Invalid, Call(t, "NpTrophySystemRequest10182F1B", handle, a, -1, a));

        Assert.Equal(Invalid, Call(t, "NpTrophyCreateSystemContext", 0u, a, 0UL));
        Assert.Equal(Invalid, Call(t, "NpTrophyCreateSystemContext", out1, a, 1UL));
        Assert.Equal(Invalid, Call(t, "NpTrophyDestroySystemContext", -1));
        Assert.Equal(Context, Call(t, "NpTrophyDestroySystemContext", 77));
        Assert.Equal(Invalid, Call(t, "NpTrophyReleaseSystemRequest", 0u));
        Assert.Equal(0, Call(t, "NpTrophyReleaseSystemRequest", a));

        Assert.Equal(0, Call(t, "NpTrophyAbortSystemHandle", handle));
        Assert.Equal(0, Call(t, "NpTrophyDestroySystemHandle", handle));
        Assert.Equal(Handle, Call(t, "NpTrophyDestroySystemHandle", handle));
        Assert.Equal(Invalid, Call(t, "NpTrophyDestroySystemHandle", -1));
        Assert.Equal(0, Call(t, "sceNpTrophyTerm"));
    }

    [Fact]
    public void FacebookSystemSlotNeedsItsInitAndRequestsEndWithTheNetworksError()
    {
        NpSnsFacebook f = _harness.Module<NpSnsFacebook>();
        PsnAccount.Current = PsnAccount.Create(true, "Alice");
        uint name = _harness.Alloc(0x40);
        Assert.Equal(NpSnsFacebook.FB_NOT_INITIALIZED, Call(f, "NpSnsFacebookSystemTerm"));
        Assert.Equal(NpSnsFacebook.FB_NOT_INITIALIZED, Call(f, "NpSnsFacebookSystemCreateRequest"));
        Assert.Equal(NpSnsFacebook.FB_NOT_INITIALIZED, Call(f, "NpSnsFacebookRequestRoom", 0));
        Assert.Equal(0, Call(f, "NpSnsFacebookSystemInit"));
        Assert.Equal(NpSnsFacebook.FB_ALREADY_INITIALIZED, Call(f, "NpSnsFacebookSystemInit"));
        Assert.Equal(NpSnsFacebook.FB_NO_ENTRY, Call(f, "NpSnsFacebookRequestRoom", 0));
        Assert.Equal(NpSnsFacebook.FB_INVALID_ARGUMENT, Call(f, "NpSnsFacebookSystemAccountName", 0u, 0x40u));
        Assert.Equal(NpSnsFacebook.FB_INVALID_ARGUMENT, Call(f, "NpSnsFacebookSystemRequest6A170F5D", 0, name, 0u));
        Assert.Equal(PsnAccount.Current.ServerError, Call(f, "NpSnsFacebookSystemAccountName", name, 0x40u));
        Assert.Equal(PsnAccount.Current.ServerError, Call(f, "NpSnsFacebookSystemCreateRequest"));
        Assert.Equal(0, Call(f, "NpSnsFacebookDeleteRequestOn", 0));
        Assert.Equal(NpSnsFacebook.FB_NOT_INITIALIZED, Call(f, "NpSnsFacebookDeleteRequestOn", 1)); // the game's slot: no init
        Assert.Equal(0, Call(f, "NpSnsFacebookSystemTerm"));
        Assert.Equal(NpSnsFacebook.FB_NOT_INITIALIZED, Call(f, "NpSnsFacebookSystemWait", 0u));
    }

    /// <summary>
    /// The NIDs of each system dialog family as libcdlg 3.74 exports them (FUN_81001772 is the Init the worker runs,
    /// FUN_81001C46 with a block the GetResult): the PSP emulator's error dialog calls NID_9FA21E09 with its parameter,
    /// polls NID_FA26EB30, reads NID_64F65B8D and ends with NID_C2D6F7F2. Init and GetResult were swapped in all four
    /// families (and the 0x1B family's abort with its second request), so an Init by NID read a result instead.
    /// </summary>
    [Theory]
    [InlineData("NID_9FA21E09", "NID_FA26EB30", "NID_64F65B8D", "NID_A68A44EA", "NID_C2D6F7F2")] // 0x10
    [InlineData("NID_089FB01E", "NID_C36C7DD1", "NID_47E7AD8E", "NID_F2F0049E", "NID_35387E0C")] // 0x16, Near
    [InlineData("NID_76A52CDE", "NID_E7F645AE", "NID_A785D111", "NID_460FDFF7", "NID_EFABDD6F")] // 0x1A
    [InlineData("NID_631DC45C", "NID_500229F0", "NID_396D7B68", "NID_7030E4A6", "NID_675248CA")] // 0x1B
    public void EachFamilyRunsByItsFirmwareNids(string init, string getStatus, string getResult, string abort, string term)
    {
        CommonDialog d = _harness.Module<CommonDialog>();
        uint param = _harness.Alloc(0x200), result = _harness.Alloc(0x40);
        const int Null = unchecked((int)0x80020402);

        Assert.Equal(0, Call(d, getStatus));
        Assert.Equal(0, Call(d, init, param));
        Assert.Equal(2, Call(d, getStatus)); // finished (the HLE's dialogs end as the user's cancel)
        Assert.Equal(Null, Call(d, getResult, 0u));
        Assert.Equal(0, Call(d, getResult, result));
        Assert.Equal(1u, GuestMemory.R32(result));
        Assert.Equal(0, Call(d, term));
        Assert.Equal(0, Call(d, getStatus));
        Assert.True(Call(d, getResult, result) < 0); // nothing finished to read
        Assert.Equal(0, Call(d, init, param));
        Assert.Equal(0, Call(d, abort));
        Assert.Equal(0, Call(d, term));
    }

    [Fact]
    public void TheSystemDialogFamiliesShareTheCommonDialogSlot()
    {
        CommonDialog d = _harness.Module<CommonDialog>();
        uint param = _harness.Alloc(0x200), result = _harness.Alloc(0x40);
        const int Null = unchecked((int)0x80020402), Busy = unchecked((int)0x80020401), NotInUse = unchecked((int)0x80020411);
        Assert.Equal(Null, Call(d, "CommonDialogNearInit", 0u));
        Assert.Equal(NotInUse, Call(d, "CommonDialogFamily10Request"));
        Assert.Equal(0, Call(d, "CommonDialogNearGetStatus"));
        Assert.Equal(0, Call(d, "CommonDialogNearInit", param));
        Assert.Equal(2, Call(d, "CommonDialogNearGetStatus")); // finished
        Assert.Equal(0, Call(d, "CommonDialogFamily1AGetStatus")); // another family: none
        Assert.Equal(Busy, Call(d, "CommonDialogFamily1BInit", param));
        Assert.Equal(0, Call(d, "CommonDialogNearGetResult", result));
        Assert.Equal(1u, GuestMemory.R32(result)); // the user's cancel
        Assert.Equal(0, Call(d, "CommonDialogNearTerm"));
        Assert.Equal(0, Call(d, "CommonDialogFamily1BInit", param));
        Assert.Equal(0, Call(d, "CommonDialogFamily1BRequest"));
        Assert.Equal(0, Call(d, "CommonDialogFamily1BTerm"));
        Assert.Equal(0, Call(d, "CommonDialogFamily10Init", param));
        Assert.Equal(0, Call(d, "CommonDialogFamily10Term"));
        Assert.Equal(0, Call(d, "CommonDialogTwitterInit", param));
        Assert.Equal(0, Call(d, "sceTwDialogTerm"));
    }
}
