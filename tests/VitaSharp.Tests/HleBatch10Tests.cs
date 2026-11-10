using System.Reflection;
using VitaSharp.Core;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using Xunit;

namespace VitaSharp.Tests;

/// <summary>HLE batch 10: np_manager's own IPMI requests, the last libnetctl exports, kernel services of the system
/// software (update manager, Bluetooth, USB PSP-communication, motion driver, registry region strings...).</summary>
[Collection("guest")]
public sealed class HleBatch10Tests(GuestMemoryFixture fx) : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "vitasharp-hle10-" + Guid.NewGuid().ToString("N"));
    Emulator? _emu;

    Emulator Emu()
    {
        if (_emu != null) return _emu;
        Directory.CreateDirectory(_root);
        return _emu = new Emulator(new EmulatorOptions { FsRoot = _root, Memory = fx.Memory, EnableJit = false, CpuBackend = "interp" });
    }

    T Module<T>() => Emu().Hle.Modules.OfType<T>().Single();

    static object? Invoke(object module, string name, params object?[] args) =>
        module.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!.Invoke(module, args);

    static int Call(object module, string name, params object?[] args) => Convert.ToInt32(Invoke(module, name, args));

    uint Alloc(uint size) { uint p = fx.Memory.Alloc(size, "hle10"); GuestMemory.Fill(p, 0, size); return p; }

    public void Dispose()
    {
        _emu?.Dispose();
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    const int NpNotInit = unchecked((int)0x80550502), NpInvalid = unchecked((int)0x80550503);

    [Fact]
    public void NpManagerIpmiRequestsCheckArgumentsAndNeedTheConnection()
    {
        var np = Module<NpManager>();
        uint p = Alloc(0x200);
        // the library's argument checks come before the connection
        Assert.Equal(NpInvalid, Call(np, "NpIpmiB002C", 1u, 0u, 0u, p));
        Assert.Equal(NpInvalid, Call(np, "NpIpmiB0030", 1u, 0u, 0u, 0u, 1u, p));
        Assert.Equal(NpInvalid, Call(np, "NpIpmiB0040", 1u, 0u, 0u, 0u, 0u, 0u));
        // no sceNpInit / no libshellsvc in this process: no connection, the library's "not initialised"
        Assert.Equal(NpNotInit, Call(np, "NpIpmiNewRequest1"));
        Assert.Equal(NpNotInit, Call(np, "NpIpmiNewRequest4", p));
        Assert.Equal(NpNotInit, Call(np, "NpIpmiDeleteRequestA", 1u));
        Assert.Equal(NpNotInit, Call(np, "NpIpmiAbortRequestA", 1u));
        Assert.Equal(NpNotInit, Call(np, "NpIpmiB0000", 1u, p));
        Assert.Equal(NpNotInit, Call(np, "NpIpmiB0048"));
        Assert.Equal(0, Call(np, "sceNpInit", 0u, 0u));
        Assert.Equal(NpNotInit, Call(np, "NpIpmiB0042", 1u, p));
        Assert.Equal(0, Call(np, "sceNpTerm"));
    }

    [Fact]
    public void NetCtlAdhocAuthAndPortMappingFailWithoutTheNetworks()
    {
        var net = Module<Net>();
        uint ctx = Alloc(0x1F44), tried = Alloc(4), done = Alloc(4), commId = Alloc(0x10);
        GuestMemory.W32(tried, 0xFF); GuestMemory.W32(done, 0xFF);
        Assert.Equal(unchecked((int)0x80414105), Call(net, "NetAuthCtxMapPorts", 0u, 0u, 0u, 0u, 0u, tried, done));
        Assert.Equal(0u, GuestMemory.R32(tried));
        Assert.Equal(unchecked((int)0x80414101), Call(net, "NetAuthCtxMapPorts", ctx, 0u, 0u, 0u, 0u, tried, done));
        Assert.Equal(unchecked((int)0x80412101), Call(net, "NetCtlAdhocAuthStart", 0u, 0, 0u, 0u, 0, 0u, commId));
        Assert.Equal(0, Call(net, "sceNetCtlInit"));
        Assert.Equal(0, Call(net, "NetAuthCtxInit", ctx));
        Assert.Equal(unchecked((int)0x80414187), Call(net, "NetAuthCtxMapPorts", ctx, 0u, 0u, 0u, 0u, tried, done));
        Assert.Equal(1u, GuestMemory.R32(tried));
        Assert.Equal(0u, GuestMemory.R32(done));
        Assert.Equal(unchecked((int)0x80412119), Call(net, "NetCtlAdhocAuthStart", 0u, 0, 0u, 0u, 0, 0u, 0u));
        Assert.Equal(unchecked((int)0x80412108), Call(net, "NetCtlAdhocAuthStart", 0u, 0, 0u, 0u, 0, 0u, commId));
    }

    [Fact]
    public void SystemServicesAnswerLikeTheirKernelModules()
    {
        var sys = Module<SystemServicesVsh>();
        uint p = Alloc(0x40);
        Assert.Equal(0, Call(sys, "sceSblUsSetUpdateMode", 3));
        Assert.Equal(0, Call(sys, "sceSblUsGetUpdateMode", p));
        Assert.Equal((byte)3, GuestMemory.Read<byte>(p));
        Assert.Equal(0, Call(sys, "sceSblUsGetApplicableVersion", 0, p));
        Assert.Equal(0x03740011u, GuestMemory.R32(p));
        Assert.Equal(unchecked((int)0x80020004), Call(sys, "sceSblUsVerifyPup", p));
        Assert.Equal(unchecked((int)0x802F1202), Call(sys, "sceBtAvrcpSetPlayStatus", 5));
        Assert.Equal(0, Call(sys, "sceBtAvrcpSetPlayStatus", 4));
        Assert.Equal(unchecked((int)0x802F0109), Call(sys, "sceBtStartConnect", 1u, 2u));
        Assert.Equal(unchecked((int)0x8024450D), Call(sys, "sceUsbPspcmOpen", 0u));
        Assert.Equal(0, Call(sys, "sceRegMgrSrvCnvRegionStr", 2, p, 16));
        Assert.Equal("UC2", GuestMemory.ReadCString(p, 16));
        Assert.Equal(unchecked((int)0x800D0003), Call(sys, "sceRegMgrSrvCnvRegionStr", 14, p, 16));
        Assert.Equal(unchecked((int)0x80020005), Call(sys, "_sceKernelGetRandomNumber", p, 65u));
        Assert.Equal(0, Call(sys, "_sceKernelGetRandomNumber", p, 64u));
    }

    [Fact]
    public void MotionDriverReturnsItsEmptyRecords()
    {
        var s = Module<Sensors>();
        uint p = Alloc(0x60);
        GuestMemory.Fill(p, 0xAA, 0x60);
        Assert.Equal(0, Call(s, "sceMotionDevGetAccCalibData", p));
        Assert.Equal(0u, GuestMemory.R32(p + 0x5C));
        Assert.Equal(unchecked((int)0x80360001), Call(s, "sceMotionDevGetGyroBias", 0u));
        Assert.Equal(1, Call(s, "sceMotionDevIsReady"));
        Assert.Equal(unchecked((int)0x80360001), Call(s, "sceMotionDevRead", p, 0x41, 1));
    }
}
