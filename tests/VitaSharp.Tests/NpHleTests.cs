using System.Reflection;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;

namespace VitaSharp.Tests;

/// <summary>SceNp* offline model: community request state machine, NpId helpers, init/auth bookkeeping.</summary>
[Collection("guest")]
public sealed class NpHleTests(GuestMemoryFixture fx)
{
    static int Call<T>(T module, string name, params object[] args) =>
        (int)typeof(T).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.Invoke(module, args)!;

    uint CommId()
    {
        uint p = fx.Memory.Alloc(0x10, "commid");
        GuestMemory.Fill(p, 0, 0x10);
        GuestMemory.WriteCString(p, "NPWR00000");
        return p;
    }

    /// <summary>The offline model (no local PSN services): every transaction ends with the server error.</summary>
    static void Offline(Action body)
    {
        var saved = VitaSharp.Core.Hle.PsnAccount.Current;
        VitaSharp.Core.Hle.PsnAccount.Current = VitaSharp.Core.Hle.PsnAccount.Create(true, null, localServices: false);
        try { body(); }
        finally { VitaSharp.Core.Hle.PsnAccount.Current = saved; }
    }

    [Fact]
    public void CommunityAsyncFlow() => Offline(() =>
    {
        var lib = new NpCommunity("test");
        uint result = fx.Memory.Alloc(4, "res");
        Assert.Equal(NpCommunity.NOT_INITIALIZED, lib.CreateTitleCtx(CommId()));
        Assert.Equal(0, lib.Init());
        Assert.Equal(NpCommunity.ALREADY_INITIALIZED, lib.Init());
        Assert.Equal(NpCommunity.INSUFFICIENT_ARGUMENT, lib.CreateTitleCtx(0));
        int ctx = lib.CreateTitleCtx(CommId());
        Assert.True(ctx > 0);
        Assert.Equal(NpCommunity.INVALID_ID, lib.CreateRequest(ctx + 100));
        int req = lib.CreateRequest(ctx);
        Assert.True(req > 0);
        Assert.Equal(NpCommunity.INVALID_ARGUMENT, lib.Poll(req, result)); // nothing started yet
        Assert.Equal(0, lib.StartAsync(req));
        Assert.Equal(NpCommunity.TRANSACTION_ALREADY_END, lib.StartAsync(req));
        Assert.Equal(0, lib.Poll(req, result));
        Assert.Equal(unchecked((uint)NpCommunity.OfflineResult), GuestMemory.R32(result));
        Assert.Equal(0, lib.ValidateId(ctx));
        Assert.Equal(0, lib.ValidateId(req));
        Assert.Equal(0, lib.DeleteTitleCtx(ctx)); // drops its requests too
        Assert.Equal(NpCommunity.INVALID_ID, lib.DeleteRequest(req));
        Assert.Equal(0, lib.Term());
        Assert.Equal(NpCommunity.NOT_INITIALIZED, lib.Term());
    });

    [Fact]
    public void CommunitySyncAndAbort() => Offline(() =>
    {
        var lib = new NpCommunity("test");
        uint result = fx.Memory.Alloc(4, "res");
        lib.Init();
        int ctx = lib.CreateTitleCtx(CommId());
        int a = lib.CreateRequest(ctx), b = lib.CreateRequest(ctx);
        Assert.Equal(NpCommunity.OfflineResult, lib.Sync(a));
        Assert.Equal(0, lib.Abort(b));
        Assert.Equal(0, lib.Poll(b, result));
        Assert.Equal(unchecked((uint)NpCommunity.ABORTED), GuestMemory.R32(result));
        Assert.Equal(NpCommunity.TRANSACTION_ALREADY_END, lib.StartAsync(b));
    });

    [Fact]
    public void CommunityObjectLimit()
    {
        var lib = new NpCommunity("test");
        lib.Init();
        uint id = CommId();
        for (int i = 0; i < NpCommunity.MaxObjects; i++) Assert.True(lib.CreateTitleCtx(id) > 0);
        Assert.Equal(NpCommunity.TOO_MANY_OBJECTS, lib.CreateTitleCtx(id));
    }

    uint NpId(string online, string platform, bool valid = true)
    {
        uint p = fx.Memory.Alloc(0x24, "npid");
        GuestMemory.Fill(p, 0, 0x24);
        GuestMemory.WriteCString(p, online);
        var plat = System.Text.Encoding.ASCII.GetBytes(platform);
        plat.CopyTo(GuestMemory.Span(p + 0x18, plat.Length));
        if (valid) GuestMemory.Write(p + 0x1C, (byte)1);
        return p;
    }

    [Fact]
    public void CmpNpId()
    {
        Assert.Equal(0, NpIdUtil.Compare(NpId("alice", "psp2"), NpId("alice", "psp2")));
        Assert.Equal(NpManager.UTIL_NOT_MATCH, NpIdUtil.Compare(NpId("alice", "psp2"), NpId("bob", "psp2")));
        Assert.Equal(0, NpIdUtil.Compare(NpId("alice", "psp2"), NpId("alice", ""))); // unset platform matches (PS3 rule)
        Assert.Equal(NpManager.UTIL_NOT_MATCH, NpIdUtil.Compare(NpId("alice", "psp2"), NpId("alice", "ps3")));
        Assert.Equal(NpManager.UTIL_INVALID_NP_ID, NpIdUtil.Compare(NpId("alice", "psp2", false), NpId("alice", "psp2")));
        Assert.Equal(NpManager.UTIL_INVALID_ARGUMENT, NpIdUtil.Compare(0, NpId("alice", "psp2")));
        Assert.Equal(0, NpIdUtil.Order(NpId("alice", ""), NpId("bob", ""), out int order));
        Assert.Equal(-1, order);
    }

    [Fact]
    public void PlatformTypeRoundTrip()
    {
        uint id = NpId("alice", "");
        Assert.Equal(NpIdUtil.PlatformNone, NpIdUtil.GetPlatformType(id));
        for (int t = 0; t <= 3; t++)
        {
            Assert.Equal(0, NpIdUtil.SetPlatformType(id, t));
            Assert.Equal(t, NpIdUtil.GetPlatformType(id));
        }
        Assert.Equal("psp2", System.Text.Encoding.ASCII.GetString(GuestMemory.Span(NpId("x", "psp2") + 0x18, 4)));
        Assert.Equal(NpManager.ERR_UNKNOWN_PLATFORM_TYPE, NpIdUtil.SetPlatformType(id, 4));
        Assert.Equal(NpManager.ERR_INVALID_ARGUMENT, NpIdUtil.GetPlatformType(0));
        GuestMemory.W32(id + 0x18, 0x78787878);
        Assert.Equal(NpManager.ERR_UNKNOWN_PLATFORM_TYPE, NpIdUtil.GetPlatformType(id));
    }

    [Fact]
    public void NpInitAndAuthRequests()
    {
        var np = new NpManager();
        uint cfg = fx.Memory.Alloc(0xC, "npcfg");
        GuestMemory.Fill(cfg, 0, 0xC);
        GuestMemory.W32(cfg, CommId());
        Assert.Equal(0, Call(np, "sceNpInit", cfg, 0u));
        Assert.Equal(NpManager.ERR_ALREADY_INITIALIZED, Call(np, "sceNpInit", cfg, 0u));
        Assert.Equal("NPWR00000_00", np.CommunicationId);
        Assert.Equal(0, Call(np, "sceNpTerm"));
        Assert.Equal(0, Call(np, "sceNpInit", cfg, 0u));

        uint param = fx.Memory.Alloc(0x24, "authparam");
        GuestMemory.Fill(param, 0, 0x24);
        Assert.Equal(NpManager.ERR_INVALID_ARGUMENT, Call(np, "sceNpAuthCreateStartRequest", param)); // size 0
        GuestMemory.W32(param, 0x24);
        int req = Call(np, "sceNpAuthCreateStartRequest", param);
        Assert.True(req > 0);
        Assert.Equal(0, Call(np, "sceNpAuthAbortRequest", req));
        Assert.Equal(0, Call(np, "sceNpAuthAbortRequest", 12345)); // idempotent cleanup
        Assert.Equal(0, Call(np, "sceNpAuthDestroyRequest", req));
    }
}
