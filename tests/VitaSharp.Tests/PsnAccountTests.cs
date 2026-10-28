using System.Reflection;
using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;

namespace VitaSharp.Tests;

/// <summary>
/// The emulated PSN account (<see cref="PsnAccount"/>): NetCheckDialog results, NP service state, NpId and region, and
/// the errors of server requests, signed in and signed out.
/// </summary>
[Collection("guest")]
public sealed class PsnAccountTests(GuestMemoryFixture fx)
{
    const int USER_CANCELED = 1, OK = 0, NP_SIGNED_OUT = unchecked((int)0x80550006), HTTP_NETWORK = unchecked((int)0x80431063);

    static int Call<T>(T module, string name, params object[] args) =>
        Convert.ToInt32(typeof(T).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.Invoke(module, args)!);

    uint Alloc(uint size, byte fill = 0)
    {
        uint p = fx.Memory.Alloc(size, "psn test");
        GuestMemory.Fill(p, fill, size);
        return p;
    }

    static void With(PsnAccount account, Action body)
    {
        var saved = PsnAccount.Current;
        PsnAccount.Current = account;
        try { body(); }
        finally { PsnAccount.Current = saved; }
    }

    /// <summary>Runs one NetCheckDialog of <paramref name="mode"/> and returns (result, psnModeSucceeded).</summary>
    (uint Result, uint PsnSucceeded) NetCheck(CommonDialog d, int mode)
    {
        uint param = Alloc(0xE0);
        GuestMemory.W32(param + 0x50, (uint)mode);
        Assert.Equal(0, Call(d, "sceNetCheckDialogInit", param));
        Assert.Equal(2, Call(d, "sceNetCheckDialogGetStatus")); // FINISHED
        uint res = Alloc(0x100, 0xCC);
        Assert.Equal(0, Call(d, "sceNetCheckDialogGetResult", res));
        Assert.Equal(0xCCCCCCCCu, GuestMemory.R32(res + 0x84)); // SceNetCheckDialogResult is 0x84 bytes
        Assert.Equal(0, Call(d, "sceNetCheckDialogTerm"));
        return (GuestMemory.R32(res), GuestMemory.R32(res + 4));
    }

    [Fact]
    public void DefaultAccountIsSignedIn()
    {
        Assert.True(PsnAccount.Default.SignedIn);
        Assert.Equal("VitaSharp", PsnAccount.Default.OnlineId);
        Assert.False(PsnAccount.Offline.SignedIn);
        Assert.Equal(HTTP_NETWORK, PsnAccount.Default.ServerError);
        Assert.Equal(NP_SIGNED_OUT, PsnAccount.Offline.ServerError);
    }

    [Theory]
    [InlineData("VitaSharp", true)]
    [InlineData("a_b-c9", true)]
    [InlineData("abc", true)]
    [InlineData("ab", false)]                  // too short
    [InlineData("abcdefghijklmnopq", false)]   // 17 characters
    [InlineData("9lives", false)]              // must start with a letter
    [InlineData("has space", false)]
    [InlineData("ação", false)]                // ASCII only
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OnlineIdValidation(string? id, bool valid)
    {
        Assert.Equal(valid, PsnAccount.IsValidOnlineId(id));
        Assert.Equal(valid ? id : PsnAccount.DefaultOnlineId, PsnAccount.Create(true, id).OnlineId);
    }

    [Fact]
    public void NetCheckDialogPsnModesSucceedWhenSignedIn() => With(PsnAccount.Default, () =>
    {
        var d = new CommonDialog();
        Assert.Equal((0u, 1u), NetCheck(d, 2));  // PSN: sign-in check
        Assert.Equal((0u, 1u), NetCheck(d, 3));  // PSN_ONLINE: sign-in + connection check
        Assert.Equal(((uint)USER_CANCELED, 0u), NetCheck(d, 1)); // ADHOC_CONN: nobody to connect to
        Assert.Equal(((uint)USER_CANCELED, 0u), NetCheck(d, 4)); // PS3_CONNECT: no PS3
    });

    [Fact]
    public void NetCheckDialogIsCanceledWhenSignedOut() => With(PsnAccount.Offline, () =>
    {
        var d = new CommonDialog();
        Assert.Equal(((uint)USER_CANCELED, 0u), NetCheck(d, 2));
        Assert.Equal(((uint)USER_CANCELED, 0u), NetCheck(d, 3));
    });

    [Fact]
    public void NetCheckDialogAbortAfterFinishKeepsResult() => With(PsnAccount.Default, () =>
    {
        var d = new CommonDialog();
        uint param = Alloc(0xE0);
        GuestMemory.W32(param + 0x50, 3);
        Assert.Equal(0, Call(d, "sceNetCheckDialogInit", param));
        Assert.Equal(0, Call(d, "sceNetCheckDialogAbort")); // already finished: keeps OK
        uint res = Alloc(0x84);
        Assert.Equal(0, Call(d, "sceNetCheckDialogGetResult", res));
        Assert.Equal((uint)OK, GuestMemory.R32(res));
        Assert.Equal(1u, GuestMemory.R32(res + 4));
        Assert.Equal(0, Call(d, "sceNetCheckDialogTerm"));
    });

    [Fact]
    public void NpManagerReportsTheSignedInAccount() => With(PsnAccount.Create(true, "Tester_01"), () =>
    {
        var np = new NpManager();
        uint state = Alloc(4, 0xFF);
        Assert.Equal(0, Call(np, "sceNpGetServiceState", new Ptr<int>(state)));
        Assert.Equal(2u, GuestMemory.R32(state)); // SCE_NP_SERVICE_STATE_SIGNED_IN

        uint id = Alloc(0x28, 0xCC);
        Assert.Equal(0, Call(np, "sceNpManagerGetNpId", id));
        Assert.Equal("Tester_01", GuestMemory.ReadCString(id, 16));
        Assert.Equal(0, GuestMemory.Read<byte>(id + 0x10));                 // onlineId terminator
        Assert.Equal(NpIdUtil.PlatformVita, NpIdUtil.GetPlatformType(id)); // "psp2"
        Assert.Equal(1, GuestMemory.Read<byte>(id + 0x1C));                 // valid
        Assert.Equal(0xCCCCCCCCu, GuestMemory.R32(id + 0x24));              // 0x24-byte SceNpId
        Assert.Equal(0, NpIdUtil.Compare(id, id));

        uint country = Alloc(4, 0xCC), lang = Alloc(4, 0xCC);
        Assert.Equal(0, Call(np, "sceNpManagerGetAccountRegion", country, lang));
        Assert.Equal("us", GuestMemory.ReadCString(country, 4));
        Assert.Equal(1u, GuestMemory.R32(lang)); // English (US), the system language

        // a server request: the ticket never arrives
        Assert.Equal(HTTP_NETWORK, Call(np, "sceNpAuthGetTicket", 1, Alloc(16), 16u));
        Assert.Equal(HTTP_NETWORK, NpCommunity.OfflineResult);
    });

    [Fact]
    public void NpManagerSignedOut() => With(PsnAccount.Offline, () =>
    {
        var np = new NpManager();
        uint state = Alloc(4);
        Assert.Equal(0, Call(np, "sceNpGetServiceState", new Ptr<int>(state)));
        Assert.Equal(1u, GuestMemory.R32(state)); // SIGNED_OUT
        uint id = Alloc(0x24, 0xCC);
        Assert.Equal(NP_SIGNED_OUT, Call(np, "sceNpManagerGetNpId", id));
        Assert.Equal(0xCCCCCCCCu, GuestMemory.R32(id));                     // untouched
        Assert.Equal(NP_SIGNED_OUT, Call(np, "sceNpManagerGetAccountRegion", Alloc(4), Alloc(4)));
        Assert.Equal(NP_SIGNED_OUT, Call(np, "sceNpAuthGetTicket", 1, Alloc(16), 16u));
        Assert.Equal(NpCommunity.NO_LOGIN, NpCommunity.OfflineResult);
    });
}
