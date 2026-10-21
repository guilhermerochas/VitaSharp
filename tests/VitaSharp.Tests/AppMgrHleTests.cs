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
}
