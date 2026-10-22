using System.Reflection;
using System.Runtime.CompilerServices;
using VitaSharp.Core;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Io;
using VitaSharp.Core.Memory;

namespace VitaSharp.Tests;

/// <summary>Package D, second pass: SceAppMgrUser mounts, boot param and save data info.</summary>
[Collection("guest")]
public sealed class AppMgrHleTests(GuestMemoryFixture fx) : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "vitasharp-appmgr-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    static T Call<T>(object module, string name, params object[] args) =>
        (T)module.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.Invoke(module, args)!;

    uint Alloc(uint size) { uint p = fx.Memory.Alloc(size, "appmgr"); GuestMemory.Fill(p, 0, size); return p; }

    uint Str(string s) { uint p = Alloc((uint)s.Length + 16); GuestMemory.WriteCString(p, s); return p; }

    static void SetBacking(object o, string prop, object value) =>
        typeof(Emulator).GetField($"<{prop}>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(o, value);

    /// <summary>An Emulator shell with only Options, Vfs and TitleId (a real one would reserve a second guest address space).</summary>
    (AppMgr mgr, Emulator emu) Make()
    {
        var emu = (Emulator)RuntimeHelpers.GetUninitializedObject(typeof(Emulator));
        SetBacking(emu, "Options", new EmulatorOptions { FsRoot = _root });
        SetBacking(emu, "Vfs", new Vfs(_root));
        SetBacking(emu, "TitleId", "PCSA00001");
        emu.Vfs.Mount("addcont0", Path.Combine(_root, "ux0", "addcont", "PCSA00001"));
        return (new AppMgr { Emu = emu }, emu);
    }

    const int ERR_PARAM = unchecked((int)0x80802002), ERR_NOT_FOUND = unchecked((int)0x80802012),
        ERR_NULL_POINTER = unchecked((int)0x80802016);

    [Fact]
    public void AppDataMountByIdMountsPsmDirectories()
    {
        var (mgr, emu) = Make();
        uint mp = Alloc(16), tid = Str("NPXS10001");
        Assert.Equal(ERR_NOT_FOUND, Call<int>(mgr, "sceAppMgrAppDataMountById", 106, tid, mp));
        Directory.CreateDirectory(Path.Combine(_root, "ux0", "psm", "NPXS10001", "RO"));
        Assert.Equal(0, Call<int>(mgr, "sceAppMgrAppDataMountById", 106, tid, mp));
        string dev = GuestMemory.ReadCString(mp, 16);
        Assert.Equal("am106:", dev);
        Assert.Equal(Path.Combine(_root, "ux0", "psm", "NPXS10001", "RO", "a.bin"), emu.Vfs.Resolve(dev + "a.bin"));
        Assert.Equal(ERR_PARAM, Call<int>(mgr, "sceAppMgrAppDataMountById", 104, tid, mp)); // target undocumented
        Assert.Equal(ERR_NULL_POINTER, Call<int>(mgr, "sceAppMgrAppDataMountById", 106, tid, 0u));
        Assert.Equal(0, Call<int>(mgr, "sceAppMgrUmount", mp));
        Assert.False(emu.Vfs.IsMounted("am106"));
    }

    [Fact]
    public void GameDataMountMountsInstalledApp()
    {
        var (mgr, emu) = Make();
        uint mp = Alloc(16), opt = Alloc(0x18);
        Assert.Equal(ERR_NOT_FOUND, Call<int>(mgr, "sceAppMgrGameDataMount", Str("ux0:app/PCSB99999"), 0u, 0u, mp));
        Directory.CreateDirectory(Path.Combine(_root, "ux0", "app", "PCSB00002"));
        GuestMemory.W32(opt, mp);
        Assert.Equal(0, Call<int>(mgr, "_sceAppMgrGameDataMount", Str("ux0:app/PCSB00002"), 0u, 0u, opt));
        string dev = GuestMemory.ReadCString(mp, 16);
        Assert.StartsWith("gd", dev);
        Assert.Equal(Path.Combine(_root, "ux0", "app", "PCSB00002", "eboot.bin"), emu.Vfs.Resolve(dev + "eboot.bin"));
        Assert.Equal(0, Call<int>(mgr, "sceAppMgrUmount", mp));
        Assert.Equal(ERR_NOT_FOUND, Call<int>(mgr, "sceAppMgrUmount", mp));
    }

    [Fact]
    public void GenericMountContextMounts()
    {
        var (mgr, emu) = Make();
        uint ctx = Alloc(0x40);
        GuestMemory.WriteCString(ctx, "DLC0001");
        Assert.Equal(ERR_NOT_FOUND, Call<int>(mgr, "sceAppMgrAddContMount", 0, ctx));
        Directory.CreateDirectory(Path.Combine(_root, "ux0", "addcont", "PCSA00001", "DLC0001"));
        Assert.Equal(0, Call<int>(mgr, "sceAppMgrAddContMount", 0, ctx));
        Assert.Equal(0, Call<int>(mgr, "sceAppMgrAddContAddMount", ctx, 0));
        Assert.Equal(ERR_NULL_POINTER, Call<int>(mgr, "sceAppMgrAddContAddMount", 0u, 0));

        GuestMemory.WriteCString(ctx, "PCSA00002");
        Assert.Equal(ERR_NOT_FOUND, Call<int>(mgr, "sceAppMgrSaveDataAddMount", ctx));
        Directory.CreateDirectory(Path.Combine(_root, "ux0", "user", "00", "savedata", "PCSA00002"));
        Assert.Equal(0, Call<int>(mgr, "sceAppMgrSaveDataAddMount", ctx));
        Assert.Equal(Path.Combine(_root, "ux0", "user", "00", "savedata", "PCSA00002", "s.dat"), emu.Vfs.Resolve("savedata1:s.dat"));
    }

    [Fact]
    public void BootParamZeroesReservedWords()
    {
        var (mgr, _) = Make();
        uint bp = Alloc(0x2C), opt = Alloc(8);
        for (uint o = 0; o < 0x2C; o += 4) GuestMemory.W32(bp + o, 0xA5A5A5A5);
        GuestMemory.W32(opt, 0x2D);
        Assert.Equal(ERR_PARAM, Call<int>(mgr, "_sceAppMgrGetBootParam", 0u, bp, opt));
        GuestMemory.W32(opt, 0x2C);
        Assert.Equal(0, Call<int>(mgr, "_sceAppMgrGetBootParam", 0u, bp, opt));
        Assert.Equal(0xA5A5A5A5u, GuestMemory.R32(bp)); // size untouched
        Assert.Equal(0u, GuestMemory.R32(bp + 4));
        Assert.Equal(0u, GuestMemory.R32(bp + 8));
        Assert.Equal(0xA5A5A5A5u, GuestMemory.R32(bp + 0xC)); // reserved_0C left as is
        // a short request only touches its own bytes
        GuestMemory.W32(bp + 8, 0x11111111);
        GuestMemory.W32(opt, 8);
        Assert.Equal(0, Call<int>(mgr, "_sceAppMgrGetBootParam", 0u, bp, opt));
        Assert.Equal(0x11111111u, GuestMemory.R32(bp + 8));
    }

    [Fact]
    public void SaveDataInfoReportsSize()
    {
        var (mgr, _) = Make();
        uint res = Alloc(0x1C), opt = Alloc(0x10);
        GuestMemory.W32(opt, res);
        Assert.Equal(ERR_NOT_FOUND, Call<int>(mgr, "_sceAppMgrGetSaveDataInfo", 0u, Str("PCSA00001"), 0, opt));
        string dir = Path.Combine(_root, "ux0", "user", "00", "savedata", "PCSA00001");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "a.bin"), new byte[3000]);
        Assert.Equal(0, Call<int>(mgr, "_sceAppMgrGetSaveDataInfo", 0u, Str("PCSA00001"), 0, opt));
        Assert.Equal(4u, GuestMemory.R32(res));        // USER storage
        Assert.Equal(1u, GuestMemory.R32(res + 4));    // own title: mounted as savedata0:
        Assert.Equal(3u, GuestMemory.R32(res + 0x18)); // KiB, rounded up
        Assert.Equal(ERR_PARAM, Call<int>(mgr, "_sceAppMgrGetSaveDataInfo", 0u, Str("PCSA0001"), 0, opt));
    }

    // ---- third pass: the wrappers whose layouts came from the FW 3.60 SceDriverUser/SceAppMgr/libSceAppUtil

    const int ERR_STRUCT = unchecked((int)0x80800001), ERR_TOO_BIG = unchecked((int)0x8002710B),
        ERR_SLOT_NOT_FOUND = unchecked((int)0x8080108B), ERR_SLOT_RANGE = unchecked((int)0x80801084);

    /// <summary>Make() plus the HLE AppUtil the save data functions share, with savedata0: mounted.</summary>
    (AppMgr mgr, AppUtil util, Emulator emu) MakeWithAppUtil()
    {
        var (mgr, emu) = Make();
        var hle = new VitaSharp.Core.Hle.HleRegistry(emu);
        var util = new AppUtil { Emu = emu };
        hle.Modules.Add(util);
        SetBacking(emu, "Hle", hle);
        emu.Vfs.Mount("savedata0", Path.Combine(_root, "ux0", "user", "00", "savedata", "PCSA00001"));
        Directory.CreateDirectory(Path.Combine(_root, "ux0", "user", "00", "savedata", "PCSA00001"));
        return (mgr, util, emu);
    }

    uint Sized(uint size) { uint p = Alloc(size); GuestMemory.W32(p, size); return p; }

    [Fact]
    public void PublicWrappersOfBootParamAndSaveDataInfo()
    {
        var (mgr, _) = Make();
        uint bp = Alloc(0x2C);
        for (uint o = 4; o < 0x2C; o += 4) GuestMemory.W32(bp + o, 0xA5A5A5A5);
        GuestMemory.W32(bp, 0x2C); // the wrapper takes the copy size from bootParam->size
        Assert.Equal(0, Call<int>(mgr, "sceAppMgrGetBootParam", 0u, bp));
        Assert.Equal(0u, GuestMemory.R32(bp + 4));
        Assert.Equal(0xA5A5A5A5u, GuestMemory.R32(bp + 0xC));
        GuestMemory.W32(bp, 0x30);
        Assert.Equal(ERR_PARAM, Call<int>(mgr, "sceAppMgrGetBootParam", 0u, bp));

        uint res = Alloc(0x1C);
        string dir = Path.Combine(_root, "ux0", "user", "00", "savedata", "PCSA00001");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "a.bin"), new byte[1500]);
        Assert.Equal(0, Call<int>(mgr, "sceAppMgrGetSaveDataInfo", 0u, Str("PCSA00001"), 0, res)); // result pointer, no opt
        Assert.Equal(4u, GuestMemory.R32(res));
        Assert.Equal(2u, GuestMemory.R32(res + 0x18));
    }

    [Fact]
    public void SaveDataSlotStatusQuotaAndFileGetParam()
    {
        var (mgr, util, _) = MakeWithAppUtil();
        uint param = Alloc(0x34C);
        GuestMemory.W32(param, 0); // status ok
        GuestMemory.WriteCString(param + 4, "slot title");
        Assert.Equal(0, Call<int>(util, "sceAppUtilSaveDataSlotCreate", 3u, param, 0u));

        uint st = Sized(0x1C);
        GuestMemory.W32(st + 4, 3);
        GuestMemory.W32(st + 8, 0xFFFF);
        Assert.Equal(0, Call<int>(mgr, "sceAppMgrSaveDataSlotGetStatus", st));
        Assert.Equal(0u, GuestMemory.R32(st + 8));
        GuestMemory.W32(st + 8, 1); // broken
        Assert.Equal(0, Call<int>(mgr, "sceAppMgrSaveDataSlotSetStatus", st));
        GuestMemory.W32(st + 8, 0);
        Assert.Equal(0, Call<int>(mgr, "sceAppMgrSaveDataSlotGetStatus", st));
        Assert.Equal(1u, GuestMemory.R32(st + 8));
        GuestMemory.W32(st + 4, 4);
        Assert.Equal(ERR_SLOT_NOT_FOUND, Call<int>(mgr, "sceAppMgrSaveDataSlotGetStatus", st));
        GuestMemory.W32(st + 4, 256);
        Assert.Equal(ERR_SLOT_RANGE, Call<int>(mgr, "sceAppMgrSaveDataSlotSetStatus", st));
        GuestMemory.W32(st, 0x18);
        Assert.Equal(ERR_STRUCT, Call<int>(mgr, "sceAppMgrSaveDataSlotGetStatus", st));
        GuestMemory.W32(st, 0x20);
        Assert.Equal(ERR_TOO_BIG, Call<int>(mgr, "sceAppMgrSaveDataSlotGetStatus", st));

        // the kernel always fills a 0x400-byte buffer, the slot param (0x34C) first
        uint buf = Alloc(0x400);
        GuestMemory.Fill(buf, 0xEE, 0x400);
        Assert.Equal(0, Call<int>(mgr, "sceAppMgrSaveDataSlotFileGetParam", 3u, buf));
        Assert.Equal(1u, GuestMemory.R32(buf));
        Assert.Equal("slot title", GuestMemory.ReadCString(buf + 4, 64));
        Assert.Equal(0u, GuestMemory.R32(buf + 0x3FC));
        Assert.Equal(ERR_SLOT_NOT_FOUND, Call<int>(mgr, "sceAppMgrSaveDataSlotFileGetParam", 9u, buf));

        uint quota = Sized(0x1C);
        GuestMemory.WriteCString(quota + 0xC, "savedata0:");
        Assert.Equal(0, Call<int>(mgr, "sceAppMgrSaveDataGetQuota", quota));
        Assert.Equal(1024u, GuestMemory.R32(quota + 4)); // no param.sfo: 1 MiB default
        Assert.Equal(ERR_STRUCT, Call<int>(mgr, "sceAppMgrSaveDataGetQuota", 0u));
    }

    [Fact]
    public void SaveDataMountUmountAndVariants()
    {
        var (mgr, util, emu) = MakeWithAppUtil();
        uint m = Sized(0x3C);
        GuestMemory.WriteCString(m + 0x14, "savedata0");
        Assert.Equal(0, Call<int>(mgr, "sceAppMgrSaveDataMount", m));
        uint u = Sized(0x14);
        GuestMemory.WriteCString(u + 4, "savedata0:");
        Assert.Equal(0, Call<int>(mgr, "sceAppMgrSaveDataUmount", u));
        Assert.True(emu.Vfs.IsMounted("savedata0")); // the process's own save data stays mounted
        GuestMemory.WriteCString(u + 4, "am100:");
        Assert.Equal(ERR_NOT_FOUND, Call<int>(mgr, "sceAppMgrSaveDataUmount", u));
        GuestMemory.W32(u, 0x18);
        Assert.Equal(ERR_TOO_BIG, Call<int>(mgr, "sceAppMgrSaveDataUmount", u));

        // DataSave2 / DataRemove2 take the same structs as the first versions
        uint name = Str("SAVE.BIN"), payload = Alloc(16), files = Alloc(0x40);
        GuestMemory.WriteCString(payload, "data");
        GuestMemory.W32(files, name);
        GuestMemory.W32(files + 4, payload);
        GuestMemory.W32(files + 8, 4); // bufSize
        uint save = Sized(0x4C);
        GuestMemory.W32(save + 0x2C, files);
        GuestMemory.W32(save + 0x30, 1);
        Assert.Equal(0, Call<int>(mgr, "sceAppMgrSaveDataDataSave2", save));
        string host = Path.Combine(_root, "ux0", "user", "00", "savedata", "PCSA00001", "SAVE.BIN");
        Assert.True(File.Exists(host));
        GuestMemory.W32(save, 0x48);
        Assert.Equal(ERR_STRUCT, Call<int>(mgr, "sceAppMgrSaveDataDataSave2", save));
        uint del = Sized(0x44);
        GuestMemory.W32(del + 0x2C, files);
        GuestMemory.W32(del + 0x30, 1);
        Assert.Equal(0, Call<int>(mgr, "sceAppMgrSaveDataDataRemove2", del));
        Assert.False(File.Exists(host));
    }

    [Fact]
    public void AppParam2TrophyMountsDateTimeAndMediaType()
    {
        var (mgr, util, emu) = MakeWithAppUtil();
        uint p2 = Alloc(0x440);
        GuestMemory.Fill(p2, 0xEE, 0x440);
        mgr.LaunchParam = "psgm:play?titleid=PCSA00001";
        Assert.Equal(0, Call<int>(mgr, "sceAppMgrGetAppParam2", p2));
        Assert.Equal("psgm:play?titleid=PCSA00001", GuestMemory.ReadCString(p2, 0x400));
        Assert.Equal(0u, GuestMemory.R32(p2 + 0x43C));
        Assert.Equal(unchecked((int)0x80802015), Call<int>(mgr, "sceAppMgrGetAppParam2", p2));

        uint mp = Alloc(16);
        Assert.Equal(0, Call<int>(mgr, "sceAppMgrTrophyMount", 302, 0, mp));
        Assert.Equal("trophy_dat0:", GuestMemory.ReadCString(mp, 16));
        Assert.Equal(ERR_NOT_FOUND, Call<int>(mgr, "sceAppMgrTrophyMount", 300, 0, mp)); // no app0:sce_sys/trophy
        Assert.Equal(ERR_PARAM, Call<int>(mgr, "sceAppMgrTrophyMount", 301, 0, mp));
        Assert.Equal(0, Call<int>(mgr, "sceAppMgrTrophyMountById", 304, Str("NPWR01234_00"), mp));
        Assert.Equal("trophy_dbk0:", GuestMemory.ReadCString(mp, 16));
        Assert.Equal(Path.Combine(_root, "ur0", "user", "00", "trophy", "data", "NPWR01234_00_BK", "x"), emu.Vfs.Resolve("trophy_dbk0:x"));
        Assert.Equal(ERR_PARAM, Call<int>(mgr, "sceAppMgrTrophyMountById", 303, Str("NPWR01234"), mp));
        Assert.Equal(0, Call<int>(mgr, "sceAppMgrUmount", mp));

        uint conf = Alloc(0x14);
        GuestMemory.Fill(conf, 0xEE, 0x14);
        Assert.Equal(0, Call<int>(mgr, "sceAppMgrSystemParamDateTimeGetConf", conf));
        Assert.Equal((uint)AppUtil.SystemParamInt(4), GuestMemory.R32(conf));
        Assert.Equal((uint)AppUtil.SystemParamInt(7), GuestMemory.R32(conf + 0x10));
        Assert.Equal(unchecked((int)0x80801001), Call<int>(mgr, "sceAppMgrSystemParamDateTimeGetConf", 0u));

        uint type = Alloc(4);
        emu.Vfs.Mount("app0", Path.Combine(_root, "ux0", "app", "PCSA00001"));
        Assert.Equal(0, Call<int>(mgr, "sceAppMgrGetMediaTypeFromDrive", Str("app0:"), new Ptr<int>(type)));
        Assert.Equal(4u, GuestMemory.R32(type)); // memory card
        Assert.Equal(unchecked((int)0x80800002), Call<int>(mgr, "sceAppMgrGetMediaTypeFromDrive", Str("ux0:"), new Ptr<int>(type)));
        Assert.Equal(ERR_STRUCT, Call<int>(mgr, "sceAppMgrGetMediaTypeFromDrive", 0u, new Ptr<int>(type)));
    }
}
