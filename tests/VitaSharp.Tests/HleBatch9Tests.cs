using System.Reflection;
using VitaSharp.Core;
using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using Xunit;

namespace VitaSharp.Tests;

/// <summary>HLE batch 9: the NP side of the system software (libnetctl's and np_manager's requests to the shell's
/// services, np_commerce2's contexts and requests).</summary>
[Collection("guest")]
public sealed class HleBatch9Tests(GuestMemoryFixture fixture) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vitasharp-hle9-" + Guid.NewGuid().ToString("N"));
    private Emulator? _emu;

    private Emulator Emu()
    {
        if (_emu != null)
        {
            return _emu;
        }

        Directory.CreateDirectory(_root);
        return _emu = new Emulator(new EmulatorOptions { FsRoot = _root, Memory = fixture.Memory, EnableJit = false, CpuBackend = "interp" });
    }

    private T Module<T>() => Emu().Hle.Modules.OfType<T>().Single();

    private static object? Invoke(object module, string name, params object?[] args) =>
        module.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!.Invoke(module, args);

    private static int Call(object module, string name, params object?[] args) => Convert.ToInt32(Invoke(module, name, args));

    private uint Alloc(uint size)
    {
        uint p = fixture.Memory.Alloc(size, "hle9");
        GuestMemory.Fill(p, 0, size);
        return p;
    }

    public void Dispose()
    {
        _emu?.Dispose();
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException) { }
    }

    private const int NetCtlNotInit = unchecked((int)0x80412101), NetCtlAddr = unchecked((int)0x80412107), NetCtlSize = unchecked((int)0x80412111);

    [Fact]
    public void NetCtlRequestsCheckLikeTheLibraryAndNeedTheShell()
    {
        Net net = Module<Net>();
        uint p = Alloc(0x478 * 2);
        Assert.Equal(NetCtlNotInit, Call(net, "NetCtlSvc1C", p));
        Assert.Equal(0, Call(net, "sceNetCtlInit"));
        Assert.Equal(NetCtlAddr, Call(net, "NetCtlSvc1C", 0u));
        Assert.Equal(NetCtlSize, Call(net, "NetCtlSvc36", p, 0u));
        Assert.Equal(NetCtlSize, Call(net, "NetCtlSvc37", p, 31u));
        GuestMemory.W32(p, 0x41);
        Assert.Equal(NetCtlSize, Call(net, "NetCtlSvc31", 0u, p));
        // no libshellsvc in this process: the request cannot reach the shell's service
        Assert.Equal(NetCtlNotInit, Call(net, "NetCtlSvc1C", p));
        Assert.Equal(NetCtlNotInit, Call(net, "NetCtlSvc26", p));
    }

    [Fact]
    public void NetCtlSystemCallbackListsRegisterAndUnregister()
    {
        Net net = Module<Net>();
        uint cid = Alloc(4);
        Assert.Equal(0, Call(net, "sceNetCtlInit"));
        Assert.Equal(0, Call(net, "NetCtlRegisterCallbackSys3", 0x81000001u, 7u, cid));
        int id = (int)GuestMemory.R32(cid);
        Assert.Equal(0, Call(net, "NetCtlUnregisterCallbackSys3", id));
        Assert.Equal(unchecked((int)0x80412104), Call(net, "NetCtlUnregisterCallbackSys3", id));
        Assert.Equal(unchecked((int)0x80412105), Call(net, "NetCtlUnregisterCallbackSys4", 8));
        for (int i = 0; i < 8; i++)
        {
            Assert.Equal(0, Call(net, "NetCtlRegisterCallbackSys4", 0x81000001u, 0u, cid));
        }

        Assert.Equal(unchecked((int)0x80412103), Call(net, "NetCtlRegisterCallbackSys4", 0x81000001u, 0u, cid));
    }

    [Fact]
    public void NetCtlKeepsTheNetworkTickAndBuildsTheAuthName()
    {
        Net net = Module<Net>();
        uint tick = Alloc(8), id = Alloc(12), buf = Alloc(0x40);
        Assert.Equal(0, Call(net, "sceNetCtlInit"));
        Assert.Equal(0, Call(net, "NetCtlSaveNetworkTick"));
        Assert.Equal(0, Call(net, "NetCtlGetSavedNetworkTick", tick));
        Assert.NotEqual(0ul, GuestMemory.Read<ulong>(tick));
        GuestMemory.WriteCString(id, "NPWR01234");
        GuestMemory.Write<byte>(id + 10, 3);
        Assert.Equal(NetCtlSize, Call(net, "NetCtlAdhocAuthName", id, buf, 0x20u));
        Assert.Equal(0, Call(net, "NetCtlAdhocAuthName", id, buf, 0x40u));
        Assert.Equal("SCE_NPWR01234_03", GuestMemory.ReadCString(buf));
    }

    [Fact]
    public void NetCtlAuthContextLifecycle()
    {
        Net net = Module<Net>();
        uint ctx = Alloc(0x1F44);
        Assert.Equal(unchecked((int)0x80414101), Call(net, "NetAuthCtxAbort", ctx));
        Assert.Equal(0, Call(net, "NetAuthCtxInit", ctx));
        Assert.Equal(1, GuestMemory.Read<byte>(ctx));
        Assert.Equal(uint.MaxValue, GuestMemory.R32(ctx + 8));
        Assert.Equal(0, Call(net, "NetAuthCtxAbort", ctx));
        Assert.Equal(1, GuestMemory.Read<byte>(ctx + 1));
        Invoke(net, "NetAuthCtxTerm", ctx);
        Assert.Equal(0, GuestMemory.Read<byte>(ctx));
    }

    [Fact]
    public void NpManagerKeepsTheCommunicationConfig()
    {
        NpManager np = Module<NpManager>();
        uint cfg = Alloc(12), id = Alloc(12), sign = Alloc(0xA0), outp = Alloc(12);
        GuestMemory.WriteCString(id, "NPWR00001");
        GuestMemory.W32(cfg, id);
        GuestMemory.W32(cfg + 8, sign);
        Assert.Equal(0, Call(np, "NpIsInitialized"));
        Assert.Equal(unchecked((int)0x80550002), Call(np, "NpGetCommunicationConfig", outp));
        Assert.Equal(0, Call(np, "sceNpInit", cfg, 0u));
        Assert.Equal(1, Call(np, "NpIsInitialized"));
        Assert.Equal(0, Call(np, "NpGetCommunicationConfig", outp));
        uint keptId = GuestMemory.R32(outp);
        Assert.NotEqual(0u, keptId);
        Assert.NotEqual(id, keptId);
        Assert.Equal("NPWR00001", GuestMemory.ReadCString(keptId));
        Assert.Equal(0u, GuestMemory.R32(outp + 4)); // no passphrase given
        Assert.NotEqual(0u, GuestMemory.R32(outp + 8));
        Assert.Equal(unchecked((int)0x80550503), Call(np, "NpSvcB0025", 0u));
        Assert.Equal(unchecked((int)0x80550503), Call(np, "NpSvcB001F", outp, 0u));
        Assert.Equal(0, Call(np, "NpSvcA0008", 0u, 0u)); // nothing to send
        Assert.Equal(unchecked((int)0x80550502), Call(np, "NpSvcB0025", outp)); // no shell service in this process
        Assert.Equal(0, Call(np, "sceNpTerm"));
    }

    [Fact]
    public void Commerce2TypedRequestsLiveInTheirContext()
    {
        NpCommerce2 c2 = Module<NpCommerce2>();
        uint ctxOut = Alloc(4), req = Alloc(4);
        Assert.Equal(unchecked((int)0x80550F01), Call(c2, "Create07C16199", 1u, req));
        Assert.Equal(0, Call(c2, "sceNpCommerce2Init"));
        Assert.Equal(0, Call(c2, "sceNpCommerce2CreateCtx", 2u, new Ptr<uint>(ctxOut)));
        uint ctx = GuestMemory.R32(ctxOut);
        Assert.Equal(unchecked((int)0x80550F03), Call(c2, "Create07C16199", ctx, 0u));
        Assert.Equal(unchecked((int)0x80550F0C), Call(c2, "Create07C16199", ctx + 100, req));
        Assert.Equal(0, Call(c2, "Create07C16199", ctx, req));
        uint id = GuestMemory.R32(req);
        Assert.Equal(ctx, id >> 16);
        // the reply buffer is checked, then the request; the store itself cannot be reached
        Assert.Equal(0, Call(c2, "CreateACABE7E1", ctx, req)); // type 0x11
        uint buf = Alloc(0x100), filled = Alloc(4);
        Assert.Equal(unchecked((int)0x80550F03), Call(c2, "StoreExchange", id, 0u, 0x100u, filled));
        Assert.Equal(unchecked((int)0x80550F0E), Call(c2, "StoreExchange", ctx << 16 | 0xFFFE, buf, 0x100u, filled));
        Assert.Equal(PsnAccount.Current.ServerError, Call(c2, "StoreExchange", id, buf, 0x100u, filled));
        for (int i = 2; i < 8; i++)
        {
            Assert.Equal(0, Call(c2, "Create0985546A", ctx, req));
        }

        Assert.Equal(unchecked((int)0x80550F19), Call(c2, "Create0985546A", ctx, req));
        Assert.Equal(unchecked((int)0x80550F91), Call(c2, "CopyCtxSession", ctx, ctx, buf, 0x100u));
        Assert.Equal(0, Call(c2, "DeleteCtx", ctx));
        Assert.Equal(unchecked((int)0x80550F0C), Call(c2, "DeleteCtx", ctx));
    }

    private uint Str(string s)
    {
        uint p = Alloc((uint)s.Length + 1);
        GuestMemory.WriteCString(p, s);
        return p;
    }

    [Fact]
    public void NpCommonEscapesUrlAndXml()
    {
        NpManager np = Module<NpManager>();
        uint src = Str("a b&<\"/~"), dst = Alloc(64), len = Alloc(4);
        Assert.Equal(0, Call(np, "NpCommonUrlEncode", dst, 64u, src, 8u, len));
        Assert.Equal("a+b%26%3C%22%2F%7E", GuestMemory.ReadCString(dst));
        Assert.Equal(19u, GuestMemory.R32(len));
        Assert.Equal(unchecked((int)0x80550602), Call(np, "NpCommonUrlEncode", dst, 18u, src, 8u, len));
        Assert.Equal(0, Call(np, "NpCommonUrlEncode", 0u, 0u, src, 8u, len)); // measure only
        Assert.Equal(19u, GuestMemory.R32(len));
        Assert.Equal(0, Call(np, "NpCommonXmlEscape", dst, 64u, src, 8u, len));
        Assert.Equal("a b&amp;&lt;&quot;/~", GuestMemory.ReadCString(dst));
        uint bad = Str("a");
        Assert.Equal(unchecked((int)0x80550608), Call(np, "NpCommonXmlEscape", dst, 64u, bad, 2u, len));
        uint esc = Str("x&lt;&amp;&zz;");
        Assert.Equal(0, Call(np, "NpCommonXmlUnescape", dst, 64u, esc, 14u, len));
        Assert.Equal("x<&&zz;", GuestMemory.ReadCString(dst));
    }

    [Fact]
    public void NpCommonDecodesBase64AndParsesNumbers()
    {
        NpManager np = Module<NpManager>();
        uint b = Str("aGVsbG8="), dst = Alloc(16), end = Alloc(4);
        Assert.Equal(5, Call(np, "NpCommonBase64DecodedSize", b, 8u));
        Assert.Equal(5, Call(np, "NpCommonBase64Decode", b, 8u, dst, 16u));
        Assert.Equal("hello", GuestMemory.ReadCString(dst));
        Assert.Equal(unchecked((int)0x80558003), Call(np, "NpCommonBase64Decode", b, 7u, dst, 16u));
        Assert.Equal(unchecked((int)0x80558004), Call(np, "NpCommonBase64Decode", Str("a*Vs"), 4u, dst, 16u));
        uint n = Str("  -0x1Fz");
        Assert.Equal(-31, Call(np, "NpCommonStrtol", n, end, 0));
        Assert.Equal(n + 7, GuestMemory.R32(end));
        Assert.Equal(8, Call(np, "NpCommonStrtol", Str("o10"), 0u, 10)); // a leading 'o' is octal
        Assert.Equal(5, Call(np, "NpCommonStrtol", Str("0b101"), 0u, 0));
        Assert.Equal(123, Call(np, "NpCommonStrtol", Str("123abc"), 0u, 10));
        Assert.Equal(-5000000000L, (long)Invoke(np, "NpCommonStrtoll", Str("-5000000000"), 0u, 10)!);
    }
    [Fact]
    public void SqliteVshRunsStatementsOnARealDatabase()
    {
        SqliteVsh sq = Module<SqliteVsh>();
        uint ppDb = Alloc(4), ppStmt = Alloc(4), err = Alloc(4);
        Assert.Equal(0, Call(sq, "SqlInitialize"));
        Assert.Equal(0, Call(sq, "Open", Str(":memory:"), ppDb));
        uint db = GuestMemory.R32(ppDb);
        Assert.NotEqual(0u, db);
        Assert.Equal(0, Call(sq, "Exec", db, Str("CREATE TABLE t(a INTEGER, b TEXT, c BLOB); INSERT INTO t VALUES(7, 'x''y', x'0102');"), 0u, 0u, err));
        Assert.Equal(1, Call(sq, "Changes", db));
        Assert.Equal(1L, (long)Invoke(sq, "LastInsertRowid", db)!);
        Assert.Equal(0, Call(sq, "Prepare", db, Str("SELECT a, b, c FROM t WHERE a = ?"), -1, ppStmt, 0u));
        uint st = GuestMemory.R32(ppStmt);
        Assert.Equal(3, Call(sq, "ColumnCount", st));
        Assert.Equal(1, Call(sq, "BindParameterCount", st));
        Assert.Equal(0, Call(sq, "BindInt64", st, 1, 7L));
        Assert.Equal(100, Call(sq, "Step", st)); // SQLITE_ROW
        Assert.Equal(7L, (long)Invoke(sq, "ColumnInt64", st, 0)!);
        Assert.Equal("x'y", GuestMemory.ReadCString(Convert.ToUInt32(Invoke(sq, "ColumnText", st, 1))));
        Assert.Equal("b", GuestMemory.ReadCString(Convert.ToUInt32(Invoke(sq, "ColumnName", st, 1))));
        Assert.Equal(2, Call(sq, "ColumnBytes", st, 2));
        uint blob = Convert.ToUInt32(Invoke(sq, "ColumnBlob", st, 2));
        Assert.Equal(0x0201, GuestMemory.Read<ushort>(blob));
        Assert.Equal(101, Call(sq, "Step", st)); // SQLITE_DONE
        Assert.Equal(0, Call(sq, "StmtFinalize", st));
        Assert.Equal(1, Call(sq, "Exec", db, Str("SELECT * FROM missing"), 0u, 0u, err));
        Assert.Contains("no such table", GuestMemory.ReadCString(GuestMemory.R32(err)));
        Assert.Equal(0, Call(sq, "Close", db));
    }
    [Fact]
    public void PafMembersFollowTheLibrary()
    {
        Paf paf = Module<Paf>();
        uint surf = Alloc(0x80), style = Alloc(0x20), img = Alloc(0x40), info = Alloc(0x60), rect = Alloc(16);
        Assert.Equal(0u, Convert.ToUInt32(Invoke(paf, "SurfaceSetWrapS", surf, 4)));
        Assert.Equal(1u, GuestMemory.R32(surf + 0x54));
        Assert.Equal(1, GuestMemory.Read<byte>(surf + 0x60));
        Assert.Equal(0x80AF2202u, Convert.ToUInt32(Invoke(paf, "SurfaceSetWrapT", surf, 3)));
        Assert.Equal(0u, Convert.ToUInt32(Invoke(paf, "SurfaceSetMinFilter", surf, 5u)));
        Assert.Equal(1u, GuestMemory.R32(surf + 0x4C));
        Assert.Equal(0x200u, GuestMemory.R32(surf + 0x50));
        Assert.Equal(4, Call(paf, "GestureIdFromBitmap", 0u, 0b10110u, 2));
        Assert.Equal(-1, Call(paf, "GestureIdFromBitmap", 0u, 0b10110u, 3));
        Invoke(paf, "TextStyleSetBool", style, 9, true);
        Assert.Equal(4, GuestMemory.Read<ushort>(style + 6));
        GuestMemory.W32(img + 0x2C, info);
        GuestMemory.W32(info + 0x1C, 100);
        GuestMemory.W32(info + 0x20, 50);
        GuestMemory.W32(rect, unchecked((uint)-10));
        GuestMemory.W32(rect + 4, 10);
        GuestMemory.W32(rect + 8, 60);
        GuestMemory.W32(rect + 0xC, 100);
        Assert.True((bool)Invoke(paf, "ImageSetClip", img, rect)!);
        Assert.Equal(0u, GuestMemory.R32(info + 0x24));
        Assert.Equal(50u, GuestMemory.R32(info + 0x2C)); // clipped at x = 0 and the 100-pixel width: 50 wide
        Assert.Equal(40u, GuestMemory.R32(info + 0x30));
    }
}
