using System.Reflection;
using VitaSharp.Core;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using Xunit;

namespace VitaSharp.Tests;

/// <summary>The small libraries (GPU driver signals and salvage, cookie statistics, Wi-Fi location
/// history, the GXM replay arena, AppUtil's book/video/additional-content mounts, event notify callbacks).</summary>
[Collection("guest")]
public sealed class SmallSystemLibrariesTests(GuestMemoryFixture fixture) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vitasharp-hle13-" + Guid.NewGuid().ToString("N"));
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

    private static int I(object module, string name, params object?[] args) =>
        Convert.ToInt32(module.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!.Invoke(module, args));

    private uint Alloc(uint size)
    {
        uint p = fixture.Memory.Alloc(size, "hle13");
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

    [Fact]
    public void GpuSalvageRegistration()
    {
        GpuEs4 g = Module<GpuEs4>();
        Assert.Equal(unchecked((int)0x804C0000), I(g, "sceGpuRegisterSalvage", -1));
        Assert.Equal(0, I(g, "sceGpuRegisterSalvage", 0x1234));
        Assert.Equal(unchecked((int)0x804C000E), I(g, "sceGpuRegisterSalvage", 0x5678));
        Assert.Equal(unchecked((int)0x804C0000), I(g, "sceGpuUnregisterSalvage", 0x5678));
        Assert.Equal(0, I(g, "sceGpuUnregisterSalvage", 0x1234));
        Assert.Equal(0, I(g, "sceGpuRegisterSalvage", 0x5678));
    }

    [Fact]
    public void PvrServicesBridgeNeedsAConnection()
    {
        const int Fault = -14;
        GpuEs4 g = Module<GpuEs4>();
        // PVRSRV_BRIDGE_PACKAGE { bridgeId, size, in, inSize, out, outSize, hKernelServices }
        uint package = Alloc(0x1C), output = Alloc(8);
        void Package(uint index, uint outSize, uint kernelServices, uint inSize = 0)
        {
            GuestMemory.W32(package, 0x1000 + index);
            GuestMemory.W32(package + 4, 0x1C);
            GuestMemory.W32(package + 8, inSize == 0 ? 0 : output);
            GuestMemory.W32(package + 12, inSize);
            GuestMemory.W32(package + 16, output);
            GuestMemory.W32(package + 20, outSize);
            GuestMemory.W32(package + 24, kernelServices);
        }

        Assert.Equal(Fault, I(g, "PVRSRV_BridgeDispatchKM", 0u, 0u));
        // CONNECT_SERVICES before PVRSRVOpen: the process has no per-process data
        Package(0x0C, 8, 0);
        Assert.Equal(Fault, I(g, "PVRSRV_BridgeDispatchKM", 0u, package));
        Assert.Equal(0, I(g, "PVRSRVOpen"));
        GuestMemory.W32(output, 0xFFFFFFFF);
        Assert.Equal(0, I(g, "PVRSRV_BridgeDispatchKM", 0u, package));
        Assert.Equal(0u, GuestMemory.R32(output)); // eError = PVRSRV_OK
        uint services = GuestMemory.R32(output + 4);
        Assert.Equal(GpuEs4.KernelServicesHandle, services);
        // the other calls name the process's handle
        Package(0x0D, 4, 0);
        Assert.Equal(Fault, I(g, "PVRSRV_BridgeDispatchKM", 0u, package));
        Package(0x0D, 4, services);
        GuestMemory.W32(output, 0xFFFFFFFF);
        Assert.Equal(0, I(g, "PVRSRV_BridgeDispatchKM", 0u, package));
        Assert.Equal(0u, GuestMemory.R32(output));
        // buffers above 0x1000 bytes and indexes from 0x94 are refused
        Package(0x0C, 0x1001, 0);
        Assert.Equal(Fault, I(g, "PVRSRV_BridgeDispatchKM", 0u, package));
        Package(0x94, 4, services);
        Assert.Equal(Fault, I(g, "PVRSRV_BridgeDispatchKM", 0u, package));
        // before the services' init server has run, only the connection calls get through
        Package(0x01, 4, services, 4);
        Assert.Equal(Fault, I(g, "PVRSRV_BridgeDispatchKM", 0u, package));
        // the connection is counted per open
        Assert.Equal(0, I(g, "PVRSRVOpen"));
        Assert.Equal(0, I(g, "PVRSRVRelease"));
        Package(0x0C, 8, 0);
        Assert.Equal(0, I(g, "PVRSRV_BridgeDispatchKM", 0u, package));
        Assert.Equal(0, I(g, "PVRSRVRelease"));
        Assert.Equal(Fault, I(g, "PVRSRV_BridgeDispatchKM", 0u, package));
        Assert.Equal(0, I(g, "PVRSRVRelease"));
    }

    [Fact]
    public void PvrServicesBridgeFollowsTheInitServerStateTheKernelSets()
    {
        const int Fault = -14;
        GpuEs4 g = Module<GpuEs4>();
        uint package = Alloc(0x1C), output = Alloc(8);
        GuestMemory.W32(package, 0x1000 + 0x0C); // CONNECT_SERVICES
        GuestMemory.W32(package + 4, 0x1C);
        GuestMemory.W32(package + 16, output);
        GuestMemory.W32(package + 20, 8);
        Assert.Equal(0, I(g, "PVRSRVOpen"));
        Assert.Equal(0, I(g, "PVRSRV_BridgeDispatchKM", 0u, package));
        // the init server running: nothing but its own process gets through
        Assert.Equal(0, I(g, "PVRSRVSetInitServerState", 0u, 1u));
        Assert.Equal(Fault, I(g, "PVRSRV_BridgeDispatchKM", 0u, package));
        // it ran and failed: refused
        Assert.Equal(0, I(g, "PVRSRVSetInitServerState", 0u, 0u));
        Assert.Equal(0, I(g, "PVRSRVSetInitServerState", 1u, 1u));
        Assert.Equal(Fault, I(g, "PVRSRV_BridgeDispatchKM", 0u, package));
        // it ran successfully: the calls get through again
        Assert.Equal(0, I(g, "PVRSRVSetInitServerState", 2u, 1u));
        Assert.Equal(0, I(g, "PVRSRV_BridgeDispatchKM", 0u, package));
        Assert.Equal(0, I(g, "PVRSRVRelease"));
    }

    [Fact]
    public void GpuSignalWaitReturnsTheCounter()
    {
        GpuEs4 g = Module<GpuEs4>();
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
        Http h = Module<Http>();
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
        Location l = Module<Location>();
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
        Gxm g = Module<Gxm>();
        uint arena = Alloc(0x20), outp = Alloc(4), mem = Alloc(0x40);
        GuestMemory.W32(arena + 8, mem + 1);
        GuestMemory.W32(arena + 0xC, mem + 0x20);
        Assert.Equal(0, I(g, "GxmReplayArenaAlloc", arena, 0x10u, outp));
        Assert.Equal(mem + 4, GuestMemory.R32(outp));
        Assert.Equal(mem + 0x14, GuestMemory.R32(arena + 8));
        Assert.Equal(unchecked((int)0x805B000B), I(g, "GxmReplayArenaAlloc", arena, 0x10u, outp));
        Assert.Equal(0u, GuestMemory.R32(outp));
    }

    [Fact]
    public void AppUtilVideoAndAddcontMounts()
    {
        AppUtil a = Module<AppUtil>();
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
        RegMgrSystem r = Module<RegMgrSystem>();
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
        AppMgr a = Module<AppMgr>();
        Assert.Equal(0, I(a, "sceAppMgrDump"));
        Assert.Equal(unchecked((int)0x8080201F), I(a, "sceAppMgrQuitForNonSuspendableApp")); // not a system program
    }
}
