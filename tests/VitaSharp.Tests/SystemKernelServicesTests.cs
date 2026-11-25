using System.Reflection;
using VitaSharp.Core;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using Xunit;

namespace VitaSharp.Tests;

/// <summary>np_manager's own IPMI requests, the last libnetctl exports, kernel services of the system
/// software (update manager, Bluetooth, USB PSP-communication, motion driver, registry region strings...).</summary>
[Collection("guest")]
public sealed class SystemKernelServicesTests(GuestMemoryFixture fixture) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vitasharp-hle10-" + Guid.NewGuid().ToString("N"));
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
        uint p = fixture.Memory.Alloc(size, "hle10");
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

    private const int NpNotInit = unchecked((int)0x80550502), NpInvalid = unchecked((int)0x80550503);

    [Fact]
    public void NpManagerIpmiRequestsCheckArgumentsAndNeedTheConnection()
    {
        NpManager np = Module<NpManager>();
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
        Net net = Module<Net>();
        uint ctx = Alloc(0x1F44), tried = Alloc(4), done = Alloc(4), commId = Alloc(0x10);
        GuestMemory.W32(tried, 0xFF);
        GuestMemory.W32(done, 0xFF);
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
        SystemServicesVsh sys = Module<SystemServicesVsh>();
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
        Sensors s = Module<Sensors>();
        uint p = Alloc(0x60);
        GuestMemory.Fill(p, 0xAA, 0x60);
        Assert.Equal(0, Call(s, "sceMotionDevGetAccCalibData", p));
        Assert.Equal(0u, GuestMemory.R32(p + 0x5C));
        Assert.Equal(unchecked((int)0x80360001), Call(s, "sceMotionDevGetGyroBias", 0u));
        Assert.Equal(1, Call(s, "sceMotionDevIsReady"));
        Assert.Equal(unchecked((int)0x80360001), Call(s, "sceMotionDevRead", p, 0x41, p));
        Assert.Equal(0, Call(s, "sceMotionDevRead", p, 16, p + 0x40));   // no raw ring: no record, nothing new
    }

    /// <summary>FW 3.74 SceMotionDev keeps the magnetometer calibration the shell's updater stores (12 words and a
    /// version counting the updates; 0x80360105 until the first), refuses the update to a program that is no system
    /// program (0x80360002), and checks the raw read's buffer and count (1..64). Before, the update kept nothing and the
    /// current calibration stayed "not calibrated".</summary>
    [Fact]
    public void TheMagnetometerCalibrationIsKeptAsTheFirmwareDoes()
    {
        Sensors s = Module<Sensors>();
        uint calib = Alloc(0x40), version = Alloc(4), current = Alloc(0x40), raw = Alloc(0x40 * 64);
        for (uint i = 0; i < 13; i++)
        {
            GuestMemory.W32(calib + i * 4, 0x100 + i);
        }

        Assert.Equal(unchecked((int)0x80360105), Call(s, "sceMotionDevGetCurrentMagnCalibData", current));
        Assert.Equal(unchecked((int)0x80360002), Call(s, "sceMotionDevUpdateMagnCalibData", calib, version));   // no system program
        Assert.Equal(unchecked((int)0x80360002), Call(s, "sceMotionDevReadForMagnCalib", raw, 16, 0));

        Emu().Access = VitaSharp.Core.Loader.Self.ProgramAccess.Of(0x2800000000000001UL, fromSystemSoftware: true, retailApplication: true);
        Assert.Equal(unchecked((int)0x80360001), Call(s, "sceMotionDevUpdateMagnCalibData", 0u, version));
        Assert.Equal(unchecked((int)0x80360001), Call(s, "sceMotionDevUpdateMagnCalibData", calib, 0u));
        Assert.Equal(0, Call(s, "sceMotionDevUpdateMagnCalibData", calib, version));
        Assert.Equal(1u, GuestMemory.R32(version));
        Assert.Equal(0, Call(s, "sceMotionDevGetCurrentMagnCalibData", current));
        Assert.Equal(1u, GuestMemory.R32(current));                   // the version, not the record's own first word
        Assert.Equal(0x101u, GuestMemory.R32(current + 4));
        Assert.Equal(0x10Cu, GuestMemory.R32(current + 0x30));
        Assert.Equal(0, Call(s, "sceMotionDevUpdateMagnCalibData", calib, version));
        Assert.Equal(2u, GuestMemory.R32(version));
        Assert.Equal(unchecked((int)0x80360001), Call(s, "sceMotionDevGetCurrentMagnCalibData", 0u));

        Assert.Equal(unchecked((int)0x80360001), Call(s, "sceMotionDevReadForMagnCalib", 0u, 16, 0));
        Assert.Equal(unchecked((int)0x80360001), Call(s, "sceMotionDevReadForMagnCalib", raw, 0x41, 0));
        Assert.Equal(unchecked((int)0x80360001), Call(s, "sceMotionDevReadForMagnCalib", raw, 0, 0));
        Assert.Equal(0, Call(s, "sceMotionDevReadForMagnCalib", raw, 16, 0));
    }
}
