using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// SceAppUtil save data on a savedata0: of the test's own folder: the 64 KiB safe memory round trip, slots
/// (SceAppUtilSaveDataSlotParam, 0x34C bytes: create, get, set only on an existing slot, delete), the file list of
/// sceAppUtilSaveDataDataSave (write at an offset, truncate, make a directory) and its removal, and slot search sorted
/// by id or user parameter. Errors are SCE_APPUTIL_ERROR_* (0x801006xx).
/// </summary>
[Collection("guest")]
public sealed class AppUtilSaveDataTests : IDisposable
{
    const int ERR_PARAMETER = unchecked((int)0x80100600);
    readonly HleHarness _h;
    readonly string _save;
    AppUtil U => _h.Module<AppUtil>();

    public AppUtilSaveDataTests(GuestMemoryFixture fx)
    {
        _h = new HleHarness(fx, "apputil");
        _save = Path.Combine(_h.Root, "savedata");
        Directory.CreateDirectory(_save);
        _h.Emu.Vfs.Mount("savedata0", _save);
    }

    public void Dispose() => _h.Dispose();

    [Fact]
    public void SafeMemoryRoundTrip()
    {
        uint buf = _h.Alloc(64);
        GuestMemory.Fill(buf, 0xEE, 64);
        Assert.Equal(64, Call(U, "sceAppUtilLoadSafeMemory", buf, 64u, 0L)); // nothing saved yet: zeros, and the size copied (apputil.suprx 3.74)
        Assert.All(GuestMemory.ReadSpan(buf, 64).ToArray(), b => Assert.Equal(0, b));
        for (uint i = 0; i < 64; i++) GuestMemory.Write(buf + i, (byte)(i + 1));
        Assert.Equal(64, Call(U, "sceAppUtilSaveSafeMemory", buf, 64u, 100L));
        Assert.True(File.Exists(Path.Combine(_save, "sce_sys", "safemem.dat")));
        Assert.Equal(64 * 1024, new FileInfo(Path.Combine(_save, "sce_sys", "safemem.dat")).Length);
        uint back = _h.Alloc(64);
        Assert.Equal(64, Call(U, "sceAppUtilLoadSafeMemory", back, 64u, 100L));
        Assert.Equal(GuestMemory.ReadSpan(buf, 64).ToArray(), GuestMemory.ReadSpan(back, 64).ToArray());
        Assert.Equal(ERR_PARAMETER, Call(U, "sceAppUtilSaveSafeMemory", buf, 64u, 64L * 1024 - 32));
        Assert.Equal(ERR_PARAMETER, Call(U, "sceAppUtilLoadSafeMemory", 0u, 4u, 0L));
        Assert.Equal(unchecked((int)0x80801001), U.LoadSafeMemoryKernel(0u, 4u, 0L)); // the kernel code sceAppMgrLoadSafeMemory returns
        Assert.Equal(unchecked((int)0x80801000), U.LoadSafeMemoryKernel(buf, 64u, 64L * 1024));
        Assert.Equal(0, Call(U, "sceAppUtilLoadSafeMemory", buf, 0u, 0L));
    }

    uint SlotParam(string title, uint userParam)
    {
        uint p = _h.Alloc(AppUtil.SlotParamSize);
        GuestMemory.WriteCString(p + 4, title, 64);
        GuestMemory.W32(p + 4 + 64 + 128 + 512 + 64, userParam);
        return p;
    }

    [Fact]
    public void SlotsCreateUpdateSearchAndDelete()
    {
        uint get = _h.Alloc(AppUtil.SlotParamSize);
        Assert.NotEqual(0, Call(U, "sceAppUtilSaveDataSlotGetParam", 3u, get, 0u)); // no such slot
        Assert.NotEqual(0, Call(U, "sceAppUtilSaveDataSlotSetParam", 3u, SlotParam("x", 0), 0u)); // set needs the slot
        Assert.Equal(ERR_PARAMETER, Call(U, "sceAppUtilSaveDataSlotSetParam", 3u, 0u, 0u));
        Assert.Equal(0, Call(U, "sceAppUtilSaveDataSlotCreate", 3u, SlotParam("Chapter 1", 30), 0u));
        Assert.Equal(0, Call(U, "sceAppUtilSaveDataSlotCreate", 1u, SlotParam("Chapter 2", 10), 0u));
        Assert.Equal(0, Call(U, "sceAppUtilSaveDataSlotCreate", 2u, SlotParam("Chapter 3", 20), 0u));
        Assert.True(U.SlotExists(3));
        Assert.Equal(0, Call(U, "sceAppUtilSaveDataSlotGetParam", 3u, get, 0u));
        Assert.Equal("Chapter 1", GuestMemory.ReadCString(get + 4, 64));
        Assert.Equal(0u, GuestMemory.R32(get)); // status: available
        Assert.Equal(0, Call(U, "sceAppUtilSaveDataSlotSetParam", 3u, SlotParam("Chapter 1b", 30), 0u));
        Call(U, "sceAppUtilSaveDataSlotGetParam", 3u, get, 0u);
        Assert.Equal("Chapter 1b", GuestMemory.ReadCString(get + 4, 64));

        // search used slots (type 0) 0..9 by user parameter, then by id descending; free slots (type 1)
        uint cond = _h.Alloc(0x20), result = _h.Alloc(0x10), list = _h.Alloc(16 * 16), work = _h.Alloc(8);
        GuestMemory.W32(work, list); GuestMemory.W32(work + 4, 16 * 16);
        GuestMemory.W32(cond, 0); GuestMemory.W32(cond + 4, 0); GuestMemory.W32(cond + 8, 10); GuestMemory.W32(cond + 12, 1); GuestMemory.W32(cond + 16, 0);
        Assert.Equal(0, Call(U, "sceAppUtilSaveDataSlotSearch", work, cond, result, 0u));
        Assert.Equal(3u, GuestMemory.R32(result));
        Assert.Equal(new uint[] { 1, 2, 3 }, Enumerable.Range(0, 3).Select(i => GuestMemory.R32(list + (uint)i * 16)).ToArray());
        Assert.Equal(10u, GuestMemory.R32(list + 8));
        GuestMemory.W32(cond + 12, 0); GuestMemory.W32(cond + 16, 1);
        Call(U, "sceAppUtilSaveDataSlotSearch", work, cond, result, 0u);
        Assert.Equal(new uint[] { 3, 2, 1 }, Enumerable.Range(0, 3).Select(i => GuestMemory.R32(list + (uint)i * 16)).ToArray());
        GuestMemory.W32(cond, 1); GuestMemory.W32(cond + 16, 0);
        Call(U, "sceAppUtilSaveDataSlotSearch", work, cond, result, 0u);
        Assert.Equal(7u, GuestMemory.R32(result)); // 0 and 4..9 are free
        Assert.Equal(ERR_PARAMETER, Call(U, "sceAppUtilSaveDataSlotSearch", work, 0u, result, 0u));

        Assert.Equal(0, Call(U, "sceAppUtilSaveDataSlotDelete", 2u, 0u));
        Assert.False(U.SlotExists(2));
        Assert.Equal(0, Call(U, "sceAppUtilSaveDataSlotDelete", 2u, 0u)); // already gone: fine
    }

    [Fact]
    public void DataSaveWritesFilesAndUpdatesTheSlot()
    {
        uint path = _h.Str("savedata0:game/progress.bin"), dirPath = _h.Str("savedata0:game/extra");
        uint data = _h.Alloc(16);
        for (uint i = 0; i < 16; i++) GuestMemory.Write(data + i, (byte)(0x40 + i));
        // SceAppUtilSaveDataFile (0x40): dataPath, buf, bufSize, pad, offset (s64), mode (0 write, 1 truncate, 2 dir)
        uint files = _h.Alloc(64 * 3);
        GuestMemory.W32(files, path); GuestMemory.W32(files + 4, data); GuestMemory.W32(files + 8, 16);
        GuestMemory.W32(files + 64, path); GuestMemory.W32(files + 68, data); GuestMemory.W32(files + 72, 4);
        GuestMemory.Write(files + 64 + 16, 20L); GuestMemory.W32(files + 64 + 24, 1); // 4 bytes at 20, then cut there
        GuestMemory.W32(files + 128, dirPath); GuestMemory.W32(files + 128 + 24, 2);
        uint slot = _h.Alloc(8), req = _h.Word(9);
        GuestMemory.W32(slot, 5); GuestMemory.W32(slot + 4, SlotParam("Autosave", 7));
        Assert.Equal(0, Call(U, "sceAppUtilSaveDataDataSave", slot, files, 3u, 0u, new Ptr<uint>(req)));
        Assert.Equal(0u, GuestMemory.R32(req));
        string host = Path.Combine(_save, "game", "progress.bin");
        var bytes = File.ReadAllBytes(host);
        Assert.Equal(24, bytes.Length);
        Assert.Equal((byte)0x40, bytes[0]);
        Assert.Equal((byte)0, bytes[16]);
        Assert.Equal((byte)0x40, bytes[20]);
        Assert.True(Directory.Exists(Path.Combine(_save, "game", "extra")));
        // the slot was written with the save's time
        uint get = _h.Alloc(AppUtil.SlotParamSize);
        Assert.Equal(0, Call(U, "sceAppUtilSaveDataSlotGetParam", 5u, get, 0u));
        Assert.Equal("Autosave", GuestMemory.ReadCString(get + 4, 64));
        Assert.Equal((ushort)DateTime.Now.Year, GuestMemory.Read<ushort>(get + 4 + 64 + 128 + 512 + 64 + 8));

        // removal: SceAppUtilSaveDataRemoveItem (0x2C) {dataPath, mode}; mode 0 also removes the slot
        uint rm = _h.Alloc(0x2C * 2);
        GuestMemory.W32(rm, path); GuestMemory.W32(rm + 0x2C, dirPath);
        Assert.Equal(0, Call(U, "sceAppUtilSaveDataDataRemove", slot, rm, 2u, 0u));
        Assert.False(File.Exists(host));
        Assert.False(Directory.Exists(Path.Combine(_save, "game", "extra")));
        Assert.False(U.SlotExists(5));
        GuestMemory.W32(slot, 256);
        Assert.Equal(ERR_PARAMETER, Call(U, "sceAppUtilSaveDataDataRemove", slot, rm, 0u, 0u));
    }

    [Fact]
    public void AppEventsAndBrowserStubs()
    {
        uint ev = _h.Alloc(0x1C);
        Assert.Equal(0, Call(U, "sceAppUtilReceiveAppEvent", ev));
        Assert.Equal(0u, GuestMemory.R32(ev)); // no event
        foreach (var fn in new[] { "sceAppUtilAppEventParseLiveArea", "sceAppUtilAppEventParseNearGift", "sceAppUtilAppEventParseNpBasicJoinablePresence",
            "sceAppUtilAppEventParseNpInviteMessage", "sceAppUtilAppEventParseNpAppDataMessage", "sceAppUtilAppEventParseGameCustomData",
            "sceAppUtilAppEventParseIncomingDialog", "sceAppUtilAppEventParseNpActivity", "sceAppUtilAppEventParseScreenShotNotification",
            "sceAppUtilAppEventParseSessionInvitation", "sceAppUtilAppEventParseTeleport", "sceAppUtilAppEventParseTriggerUtil", "sceAppUtilAppEventParseWebBrowser" })
        {
            Assert.Equal(ERR_PARAMETER, Call(U, fn, 0u, 0u));
            Assert.NotEqual(0, Call(U, fn, ev, _h.Alloc(0x100))); // never an event of that type
        }
        Assert.Equal(0, Call(U, "sceAppUtilLaunchWebBrowser", 0u));
        Assert.Equal(ERR_PARAMETER, Call(U, "sceAppUtilAddCookieWebBrowser", 0u));
        Assert.Equal(0, Call(U, "sceAppUtilResetCookieWebBrowser"));
    }
}
