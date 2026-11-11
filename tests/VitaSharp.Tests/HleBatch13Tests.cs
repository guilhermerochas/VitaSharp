using System.Reflection;
using VitaSharp.Core;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using Xunit;

namespace VitaSharp.Tests;

/// <summary>HLE batch 13: the small libraries (GPU driver signals and salvage, cookie statistics, Wi-Fi location
/// history, the GXM replay arena, AppUtil's book/video/additional-content mounts, event notify callbacks).</summary>
[Collection("guest")]
public sealed class HleBatch13Tests(GuestMemoryFixture fx) : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "vitasharp-hle13-" + Guid.NewGuid().ToString("N"));
    Emulator? _emu;

    Emulator Emu()
    {
        if (_emu != null) return _emu;
        Directory.CreateDirectory(_root);
        return _emu = new Emulator(new EmulatorOptions { FsRoot = _root, Memory = fx.Memory, EnableJit = false, CpuBackend = "interp" });
    }

    T Module<T>() => Emu().Hle.Modules.OfType<T>().Single();

    static int I(object module, string name, params object?[] args) =>
        Convert.ToInt32(module.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!.Invoke(module, args));

    uint Alloc(uint size) { uint p = fx.Memory.Alloc(size, "hle13"); GuestMemory.Fill(p, 0, size); return p; }

    public void Dispose()
    {
        _emu?.Dispose();
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    [Fact]
    public void GpuSalvageRegistration()
    {
        var g = Module<GpuEs4>();
        Assert.Equal(unchecked((int)0x804C0000), I(g, "sceGpuRegisterSalvage", -1));
        Assert.Equal(0, I(g, "sceGpuRegisterSalvage", 0x1234));
        Assert.Equal(unchecked((int)0x804C000E), I(g, "sceGpuRegisterSalvage", 0x5678));
        Assert.Equal(unchecked((int)0x804C0000), I(g, "sceGpuUnregisterSalvage", 0x5678));
        Assert.Equal(0, I(g, "sceGpuUnregisterSalvage", 0x1234));
        Assert.Equal(0, I(g, "sceGpuRegisterSalvage", 0x5678));
    }

    [Fact]
    public void GpuSignalWaitReturnsTheCounter()
    {
        var g = Module<GpuEs4>();
        uint cell = Alloc(4);
        Assert.Equal(unchecked((int)0x804C0000), I(g, "sceGpuSignalWait", 0u, 0u));
        GuestMemory.W32(cell, VitaSharp.Core.Gpu.GpuQueue.SignalCount + 1); // not the current value: no wait
        Assert.Equal(0, I(g, "sceGpuSignalWait", cell, 0u));
        Assert.Equal(VitaSharp.Core.Gpu.GpuQueue.SignalCount, GuestMemory.R32(cell));
        // the current value: the wait ends with the emulator (not running here) and hands the counter back
        Assert.Equal(0, I(g, "sceGpuSignalWait", cell, 20_000u));
        Assert.Equal(VitaSharp.Core.Gpu.GpuQueue.SignalCount, GuestMemory.R32(cell));
    }

    [Fact]
    public void CookieStatsNeedInit()
    {
        var h = Module<Http>();
        uint stats = Alloc(20);
        Assert.Equal(unchecked((int)0x80431001), I(h, "sceHttpGetCookieStats", stats));
        Assert.Equal(0, I(h, "sceHttpInit", 0x10000u));
        Assert.Equal(unchecked((int)0x804311FE), I(h, "sceHttpGetCookieStats", 0u));
        Assert.Equal(0, I(h, "sceHttpGetCookieStats", stats));
        Assert.Equal(0u, GuestMemory.R32(stats + 4));
    }

    [Fact]
    public void LocationWifiHistoryIsDeniedLikeTheOtherMeasurements()
    {
        var l = Module<Location>();
        uint handle = Alloc(4), info = Alloc(0x40);
        Assert.Equal(0, I(l, "sceLocationOpen", new Ptr<uint>(handle), 0u, 0u));
        uint h = GuestMemory.R32(handle);
        Assert.Equal(unchecked((int)0x80101240), I(l, "GetLocationWifiHistory", h, 0u, 0u));
        Assert.Equal(unchecked((int)0x80101241), I(l, "GetLocationWifiHistory", h + 99, 0u, info));
        Assert.Equal(unchecked((int)0x8010120D), I(l, "GetLocationWifiHistory", h, 0u, info));
        Assert.Equal(0, I(l, "CancelQueryLocationWifiHistory", h));
    }

    [Fact]
    public void ReplayArenaAllocates()
    {
        var g = Module<Gxm>();
        uint arena = Alloc(0x20), outp = Alloc(4), mem = Alloc(0x40);
        GuestMemory.W32(arena + 8, mem + 1); GuestMemory.W32(arena + 0xC, mem + 0x20);
        Assert.Equal(0, I(g, "GxmReplayArenaAlloc", arena, 0x10u, outp));
        Assert.Equal(mem + 4, GuestMemory.R32(outp));
        Assert.Equal(mem + 0x14, GuestMemory.R32(arena + 8));
        Assert.Equal(unchecked((int)0x805B000B), I(g, "GxmReplayArenaAlloc", arena, 0x10u, outp));
        Assert.Equal(0u, GuestMemory.R32(outp));
    }

    [Fact]
    public void AppUtilVideoAndAddcontMounts()
    {
        var a = Module<AppUtil>();
        Assert.Equal(unchecked((int)0x80100604), I(a, "sceAppUtilExtVideoUmount"));
        Assert.Equal(0, I(a, "sceAppUtilExtVideoMount"));
        Assert.Equal(unchecked((int)0x80100603), I(a, "sceAppUtilExtVideoMount"));
        Assert.Equal(0, I(a, "sceAppUtilExtVideoUmount"));
        // the force mount checks the title ID before anything else (after sceAppUtilInit)
        typeof(AppUtil).GetField("_initialized", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(a, true);
        uint title = Alloc(16), content = Alloc(32);
        GuestMemory.WriteCString(title, "PCSE0000");
        GuestMemory.WriteCString(content, "DLC1");
        Assert.Equal(unchecked((int)0x80100600), I(a, "sceAppUtilAddcontForceAddcontMount", title, content));
        GuestMemory.WriteCString(title, "PCSE00001");
        Assert.Equal(unchecked((int)0x80100606), I(a, "sceAppUtilAddcontForceAddcontMount", title, content));
        Directory.CreateDirectory(Path.Combine(_root, "ux0", "addcont", "PCSE00001", "DLC1"));
        Assert.Equal(0, I(a, "sceAppUtilAddcontForceAddcontMount", title, content));
        Assert.NotNull(Emu().Vfs.Resolve("addcont1:"));
        GuestMemory.WriteCString(content, "DL C");
        Assert.Equal(unchecked((int)0x80100600), I(a, "sceAppUtilAddcontForceAddcontMount", title, content));
    }

    [Fact]
    public void RegMgrRegionNamesAndInitValChecks()
    {
        var r = Module<RegMgrSystem>();
        uint name = Alloc(4);
        GuestMemory.WriteCString(name, "UC2");
        Assert.Equal(2, I(r, "sceRegMgrSrvCnvRegionInt", name));
        GuestMemory.WriteCString(name, "CN9");
        Assert.Equal(13, I(r, "sceRegMgrSrvCnvRegionInt", name));
        GuestMemory.WriteCString(name, "XX");
        Assert.Equal(unchecked((int)0x800D0004), I(r, "sceRegMgrSrvCnvRegionInt", name));
        uint cat = Alloc(32), keys = Alloc(32);
        GuestMemory.WriteCString(cat, "/CONFIG/SYSTEM/");
        Assert.Equal(unchecked((int)0x800D0004), I(r, "sceRegMgrGetInitVals", cat, keys, 0));
        Assert.Equal(unchecked((int)0x800D001C), I(r, "sceRegMgrGetInitVals", cat, keys, 1));
        GuestMemory.W32(keys, 0x10);
        Assert.Equal(unchecked((int)0x800D0004), I(r, "sceRegMgrGetInitVals", cat, keys, 1));
    }

    [Fact]
    public void AppMgrDumpAndNonSuspendableQuit()
    {
        var a = Module<AppMgr>();
        Assert.Equal(0, I(a, "sceAppMgrDump"));
        Assert.Equal(unchecked((int)0x8080201F), I(a, "sceAppMgrQuitForNonSuspendableApp")); // not a system program
    }
}
