using System.Reflection;
using System.Text;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Io;
using VitaSharp.Core.Memory;

namespace VitaSharp.Tests;

/// <summary>System utilities (G2 section 4): registry, LiveArea, notification, incoming dialog, clipboard, game update,
/// trigger util and media export.</summary>
[Collection("guest")]
public sealed class SystemUtilHleTests(GuestMemoryFixture fx) : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), $"vs_sysutil_{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    static object? Invoke<T>(T module, string name, params object[] args) =>
        typeof(T).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.Invoke(module, args);
    static int Call<T>(T module, string name, params object[] args) => (int)Invoke(module, name, args)!;

    uint Str(string s)
    {
        var bytes = Encoding.UTF8.GetByteCount(s) + 1;
        uint p = fx.Memory.Alloc((uint)Math.Max(bytes, 16), "str");
        GuestMemory.WriteCString(p, s);
        return p;
    }

    // ------------------------------------------------------------------ SceRegMgr

    RegMgr NewRegMgr() => new() { FsRootOverride = _root };

    [Fact]
    public void RegMgrReadsDefaultsConsistentWithAppUtil()
    {
        var r = NewRegMgr();
        uint buf = fx.Memory.Alloc(0x1000, "reg");
        Assert.Equal(0, Call(r, "sceRegMgrSystemParamGetInt", 0x00037502u, buf));
        Assert.Equal((uint)AppUtil.SystemParamInt(1), GuestMemory.R32(buf));
        Assert.Equal(0, Call(r, "sceRegMgrSystemParamGetInt", 0x00229142u, buf));
        Assert.Equal((uint)AppUtil.SystemParamInt(2), GuestMemory.R32(buf));
        Assert.Equal(0, Call(r, "sceRegMgrSystemParamGetInt", 0x00100591u, buf));
        Assert.Equal((uint)AppUtil.SystemParamInt(6), GuestMemory.R32(buf));

        Assert.Equal(0, Call(r, "sceRegMgrSystemParamGetStr", 0x00598438u, buf, 64));
        Assert.Equal(AppUtil.UserName, GuestMemory.ReadCString(buf));
        Assert.Equal(0, Call(r, "sceRegMgrSystemParamGetStr", 0x00598438u, buf, 4)); // truncated + NUL
        Assert.Equal("Vit", GuestMemory.ReadCString(buf));

        GuestMemory.Fill(buf, 0xAA, 0x20);
        Assert.Equal(0, Call(r, "sceRegMgrSystemParamGetBin", 0x00450F32u, buf, 16u));
        Assert.All(GuestMemory.Span(buf, 16).ToArray(), b => Assert.Equal(0, b));
        Assert.Equal(0xAA, GuestMemory.Span(buf + 16, 1)[0]);
    }

    [Fact]
    public void RegMgrErrors()
    {
        var r = NewRegMgr();
        uint buf = fx.Memory.Alloc(0x100, "reg");
        Assert.Equal(RegMgr.ERR_INVALID, Call(r, "sceRegMgrSystemParamGetInt", 0x12345678u, buf));
        Assert.Equal(RegMgr.ERR_INVALID, Call(r, "sceRegMgrSystemParamGetInt", 0x00037502u, 0u));
        Assert.Equal(RegMgr.ERR_INVALID, Call(r, "sceRegMgrSystemParamGetInt", 0x00598438u, buf)); // string key
        Assert.Equal(RegMgr.ERR_INVALID, Call(r, "sceRegMgrSystemParamGetStr", 0x00037502u, buf, 16)); // int key
        Assert.Equal(RegMgr.ERR_INVALID, Call(r, "sceRegMgrSystemParamGetBin", 0x00450F32u, 0u, 8u));
        // read-only vs unknown
        Assert.Equal(RegMgr.ERR_READ_ONLY, Call(r, "sceRegMgrSystemParamSetInt", 0x00037502u, 0));
        Assert.Equal(RegMgr.ERR_INVALID, Call(r, "sceRegMgrSystemParamSetInt", 0x12345678u, 0));
        Assert.Equal(RegMgr.ERR_READ_ONLY, Call(r, "sceRegMgrSystemParamSetStr", 0x00598438u, buf, 4u));
        // size / NULL checks before the id
        Assert.Equal(RegMgr.ERR_INVALID, Call(r, "sceRegMgrSystemParamSetStr", 0x005F6737u, 0u, 4u));
        Assert.Equal(RegMgr.ERR_INVALID, Call(r, "sceRegMgrSystemParamSetBin", 0x0094E320u, buf, (uint)RegMgr.MaxSetSize + 1));
        // type mismatch on a writable key
        Assert.Equal(RegMgr.ERR_INVALID, Call(r, "sceRegMgrSystemParamSetInt", 0x005F6737u, 1));
        Assert.Equal(0, Call(r, "sceRegMgrSystemIsBlueScreen"));
    }

    [Fact]
    public void RegMgrTableIsConsistent()
    {
        Assert.Equal(63, RegMgr.Table.Count);
        uint[] writable = [0x0081649F, 0x00068303, 0x001B2292, 0x00872621, 0x00612B3E, 0x005F6737, 0x0091F34F,
            0x008D89EB, 0x004E7A16, 0x00987180, 0x000504E4, 0x004F7E60, 0x0094E320, 0x008C3860];
        Assert.Equal(writable.OrderBy(x => x), RegMgr.Table.Values.Where(k => k.Writable).Select(k => k.Id).OrderBy(x => x));
        foreach (var k in RegMgr.Table.Values)
            Assert.IsType(k.Type == RegMgr.KeyType.Int ? typeof(int) : typeof(byte[]), k.Default());
    }

    [Fact]
    public void RegMgrWritablePersists()
    {
        var r = NewRegMgr();
        uint buf = fx.Memory.Alloc(0x100, "reg");
        Assert.Equal(0, Call(r, "sceRegMgrSystemParamSetInt", 0x00612B3Eu, 1));
        Assert.Equal(0, Call(r, "sceRegMgrSystemParamGetInt", 0x00612B3Eu, buf));
        Assert.Equal(1u, GuestMemory.R32(buf));

        GuestMemory.WriteCString(buf, "token!");
        Assert.Equal(0, Call(r, "sceRegMgrSystemParamSetStr", 0x005F6737u, buf, 5u)); // exactly 5 bytes, no NUL
        new byte[] { 1, 2, 3, 4 }.CopyTo(GuestMemory.Span(buf, 4));
        Assert.Equal(0, Call(r, "sceRegMgrSystemParamSetBin", 0x0094E320u, buf, 4u));
        Assert.True(File.Exists(r.StorePath));
        Assert.StartsWith(_root, r.StorePath);

        var r2 = NewRegMgr(); // reloads from the JSON file
        uint out_ = fx.Memory.Alloc(0x100, "reg2");
        Assert.Equal(0, Call(r2, "sceRegMgrSystemParamGetInt", 0x00612B3Eu, out_));
        Assert.Equal(1u, GuestMemory.R32(out_));
        Assert.Equal(0, Call(r2, "sceRegMgrSystemParamGetStr", 0x005F6737u, out_, 64));
        Assert.Equal("token", GuestMemory.ReadCString(out_));
        GuestMemory.Fill(out_, 0xFF, 8);
        Assert.Equal(0, Call(r2, "sceRegMgrSystemParamGetBin", 0x0094E320u, out_, 8u));
        Assert.Equal(new byte[] { 1, 2, 3, 4, 0, 0, 0, 0 }, GuestMemory.Span(out_, 8).ToArray());
    }

    // ------------------------------------------------------------------ SceLiveAreaUtil

    LiveArea NewLiveArea(out string app)
    {
        var vfs = new Vfs(_root);
        app = Path.Combine(_root, "app");
        Directory.CreateDirectory(Path.Combine(app, "sce_sys", "livearea", "contents"));
        File.WriteAllText(Path.Combine(app, "sce_sys", "livearea", "contents", "template.xml"),
            """<?xml version="1.0" encoding="utf-8"?><livearea style="a1" format-ver="01.00" content-rev="3"><livearea-background><image>bg.png</image></livearea-background><frame id="frame1"><liveitem><image>a.png</image></liveitem></frame><frame id="frame2"><user-data>hello</user-data></frame></livearea>""");
        Directory.CreateDirectory(Path.Combine(app, "sce_sys", "retail", "livearea", "contents"));
        File.WriteAllText(Path.Combine(app, "sce_sys", "retail", "livearea", "contents", "template.xml"),
            """<livearea format-ver="01.00"><frame id="frame2" rev="7"><user-data>retail</user-data></frame></livearea>""");
        vfs.Mount("app0", app);
        return new LiveArea { VfsOverride = vfs, TitleIdOverride = "PCSA00000" };
    }

    [Fact]
    public void LiveAreaPathPolicy()
    {
        Assert.True(LiveArea.IsPermittedImplicitPath("app0:sce_sys/livearea/contents"));
        Assert.True(LiveArea.IsPermittedImplicitPath("app0:/sce_sys/retail/livearea/contents/"));
        Assert.True(LiveArea.IsPermittedImplicitPath("app0:data/livearea"));
        Assert.True(LiveArea.IsPermittedImplicitPath("savedata0:la"));
        Assert.False(LiveArea.IsPermittedImplicitPath("app0:sce_sys/other"));
        Assert.False(LiveArea.IsPermittedImplicitPath("ux0:data/la"));
    }

    [Fact]
    public void LiveAreaReplaceAllSyncValidation()
    {
        var la = NewLiveArea(out var app);
        Directory.CreateDirectory(Path.Combine(app, "la"));
        Assert.Equal(LiveArea.ERR_INVALID_ARG, Call(la, "sceLiveAreaReplaceAllSync", 0u));
        Assert.Equal(LiveArea.ERR_INVALID_ARG, Call(la, "sceLiveAreaReplaceAllSync", Str("app0:" + new string('a', 251))));
        Assert.Equal(LiveArea.ERR_PATH_NOT_PERMITTED, Call(la, "sceLiveAreaReplaceAllSync", Str("ux0:data/la")));
        Assert.Equal(LiveArea.ERR_PATH_NOT_RESOLVED, Call(la, "sceLiveAreaReplaceAllSync", Str("app0:missing")));
        Assert.Equal(0, Call(la, "sceLiveAreaReplaceAllSync", Str("app0:la")));
        Assert.Equal(0, Call(la, "sceLiveAreaReplaceAllSync", Str("app0:sce_sys/livearea/contents")));
        // ForTitle bypasses the prefix rule but still resolves the directory
        uint tick = fx.Memory.Alloc(8, "tick");
        Assert.Equal(0, Call(la, "sceLiveAreaReplaceAllForTitleSync", Str("ux0:"), Str("PCSA00000"), tick));
        Assert.Equal(LiveArea.ERR_PATH_NOT_RESOLVED, Call(la, "sceLiveAreaReplaceAllForTitleSync", Str("ux0:nothere"), 0u, 0u));
    }

    [Fact]
    public void LiveAreaAsyncStatusModel()
    {
        var la = NewLiveArea(out _);
        Assert.Equal(LiveArea.ERR_PENDING, Call(la, "sceLiveAreaGetStatus"));
        Assert.Equal(0, Call(la, "sceLiveAreaReplaceAllAsync", Str("app0:sce_sys/livearea/contents")));
        Assert.Equal(LiveArea.ERR_PENDING, Call(la, "sceLiveAreaReplaceAllAsync", Str("app0:sce_sys/livearea/contents"))); // busy
        Assert.Equal(0, Call(la, "sceLiveAreaGetStatus"));
        Assert.Equal(LiveArea.ERR_PENDING, Call(la, "sceLiveAreaGetStatus"));
        // argument errors are synchronous; path errors become the final result
        Assert.Equal(LiveArea.ERR_INVALID_ARG, Call(la, "sceLiveAreaReplaceAllAsync", 0u));
        Assert.Equal(LiveArea.ERR_PENDING, Call(la, "sceLiveAreaGetStatus"));
        Assert.Equal(0, Call(la, "sceLiveAreaReplaceAllAsync", Str("ux0:data")));
        Assert.Equal(LiveArea.ERR_PATH_NOT_PERMITTED, Call(la, "sceLiveAreaGetStatus"));
        Assert.Equal(LiveArea.ERR_PENDING, Call(la, "sceLiveAreaGetStatus"));
    }

    [Fact]
    public void LiveAreaFramesAndRevisions()
    {
        var la = NewLiveArea(out _);
        uint rev = fx.Memory.Alloc(0x400, "rev");
        Assert.Equal(0, Call(la, "sceLiveAreaGetRevision", rev));
        Assert.Equal(3ul, GuestMemory.Read<ulong>(rev)); // content-rev of the template
        Assert.Equal(LiveArea.ERR_INVALID_ARG, Call(la, "sceLiveAreaGetRevision", 0u));
        Assert.Equal(0, Call(la, "sceLiveAreaGetFrameRevision", Str("frame2"), rev));
        Assert.Equal(7ul, GuestMemory.Read<ulong>(rev)); // retail takes precedence
        Assert.Equal(LiveArea.ERR_UNAVAILABLE, Call(la, "sceLiveAreaGetFrameRevision", Str("frame9"), rev));
        Assert.Equal(LiveArea.ERR_INVALID_ARG, Call(la, "sceLiveAreaGetFrameRevision", Str(""), rev));
        Assert.Equal(0, Call(la, "sceLiveAreaGetFrameUserData", Str("frame2"), rev, 4u));
        Assert.Equal("ret", GuestMemory.ReadCString(rev));
        Assert.Equal(LiveArea.ERR_UNAVAILABLE, Call(la, "sceLiveAreaGetFrameUserData", Str(""), rev, 4u));
        Assert.Equal(LiveArea.ERR_INVALID_ARG, Call(la, "sceLiveAreaGetFrameUserData", Str("frame2"), rev, 0u));

        uint ver = Str("01.00"), path = Str("app0:sce_sys/livearea/contents");
        uint xml = Str("""<frame id="frame1" rev="4"><user-data>ud1</user-data></frame>""");
        Assert.Equal(0, Call(la, "sceLiveAreaUpdateFrameSync", ver, xml, -1, path, 0));
        Assert.Equal(0, Call(la, "sceLiveAreaGetFrameRevision", Str("frame1"), rev));
        Assert.Equal(4ul, GuestMemory.Read<ulong>(rev));
        Assert.Equal(0, Call(la, "sceLiveAreaGetFrameUserData", Str("frame1"), rev, 64u));
        Assert.Equal("ud1", GuestMemory.ReadCString(rev));
        // validation
        Assert.Equal(LiveArea.ERR_INVALID_ARG, Call(la, "sceLiveAreaUpdateFrameSync", Str("02.00"), xml, -1, path, 0));
        Assert.Equal(LiveArea.ERR_INVALID_ARG, Call(la, "sceLiveAreaUpdateFrameSync", ver, xml, -1, path, 2));
        Assert.Equal(LiveArea.ERR_INVALID_ARG, Call(la, "sceLiveAreaUpdateFrameSync", ver, 0u, -1, path, 0));
        Assert.Equal(LiveArea.ERR_XML_TOO_LARGE, Call(la, "sceLiveAreaUpdateFrameSync", ver, xml, LiveArea.MaxXmlSize + 1, path, 0));
        Assert.Equal(LiveArea.ERR_PARSE, Call(la, "sceLiveAreaUpdateFrameSync", ver, Str("<frame>"), -1, path, 0));
        Assert.Equal(LiveArea.ERR_PATH_NOT_PERMITTED, Call(la, "sceLiveAreaUpdateFrameSync", ver, xml, -1, Str("ux0:x"), 0));
        Assert.Equal(0, Call(la, "sceLiveAreaUpdateFrameForTitleSync", ver, xml, -1, Str("ux0:x"), 1, 0u));
        // async: parse error becomes the final status
        Assert.Equal(0, Call(la, "sceLiveAreaUpdateFrameAsync", ver, Str("not xml"), -1, path, 0));
        Assert.Equal(LiveArea.ERR_PARSE, Call(la, "sceLiveAreaGetStatus"));
    }

    [Fact]
    public void LiveAreaUpdateFramesForTitle()
    {
        var la = NewLiveArea(out _);
        uint ver = Str("01.00"), path = Str("ux0:data");
        uint arr = fx.Memory.Alloc(16, "arr");
        GuestMemory.W32(arr, Str("""<frame id="frame3"/>"""));
        GuestMemory.W32(arr + 4, Str("""<frame id="frame4"><user-data>x</user-data></frame>"""));
        Assert.Equal(0, Call(la, "sceLiveAreaUpdateFramesForTitleSync", ver, arr, 2u, path, 0, 0u));
        uint rev = fx.Memory.Alloc(16, "rev");
        Assert.Equal(0, Call(la, "sceLiveAreaGetFrameRevision", Str("frame4"), rev));
        Assert.Equal(LiveArea.ERR_INVALID_ARG, Call(la, "sceLiveAreaUpdateFramesForTitleSync", ver, arr, 0u, path, 0, 0u));
        GuestMemory.W32(arr + 8, 0);
        Assert.Equal(LiveArea.ERR_INVALID_ARG, Call(la, "sceLiveAreaUpdateFramesForTitleSync", ver, arr, 3u, path, 0, 0u));
        uint big = Str("<frame id=\"big\">" + new string('x', 0x3F00) + "</frame>");
        GuestMemory.W32(arr + 8, big);
        Assert.Equal(LiveArea.ERR_IPMI_TOO_LARGE, Call(la, "sceLiveAreaUpdateFramesForTitleSync", ver, arr, 3u, path, 0, 0u));
        Assert.Equal(32 + 8, LiveArea.FramesRequestSize(["0123456789abcdef"]));
    }
}
