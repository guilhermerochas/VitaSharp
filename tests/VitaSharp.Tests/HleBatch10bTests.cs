using System.Reflection;
using VitaSharp.Core;
using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using Xunit;

namespace VitaSharp.Tests;

/// <summary>HLE batch 10b: the system side the shell and its applications import (MTP, HTTP over MTP and its wrapper,
/// the MTP service client, livespace_db, the email engine, ScePsp2Compat helpers).</summary>
[Collection("guest")]
public sealed class HleBatch10bTests(GuestMemoryFixture fx) : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "vitasharp-hle10b-" + Guid.NewGuid().ToString("N"));
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

    uint Alloc(uint size) { uint p = fx.Memory.Alloc(size, "hle10b"); GuestMemory.Fill(p, 0, size); return p; }

    uint Str(string s) { uint p = Alloc((uint)s.Length + 1); GuestMemory.WriteCString(p, s); return p; }

    public void Dispose()
    {
        _emu?.Dispose();
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    [Fact]
    public void MtpHttpWrapperPicksItsLibraryAndChecksTheMode()
    {
        var w = Module<MtpHttpWrapper>();
        Assert.Equal(unchecked((int)0x80546500), Call(w, "sceMtpHttpWrapperStopUnloadModules"));
        Assert.Equal(unchecked((int)0x80546502), Call(w, "sceMtpHttpWrapperLoadStartModules", (ushort)0));
        Assert.Equal(unchecked((int)0x80546502), Call(w, "sceMtpHttpWrapperLoadStartModules", (ushort)5));
        Assert.Equal(0, Call(w, "sceMtpHttpWrapperLoadStartModules", (ushort)3));
        Assert.Equal(unchecked((int)0x80546501), Call(w, "sceMtpHttpWrapperLoadStartModules", (ushort)1));
        // libmtphttp's client was started by the wrapper: starting it again is "already"
        Assert.Equal(unchecked((int)0x80410110), Call(Module<MtpHttp>(), "sceMtpHttpStart"));
        Assert.Equal(0, Call(w, "sceMtpHttpWrapperStopUnloadModules"));
        Assert.Equal(unchecked((int)0x804101C8), Call(Module<MtpHttp>(), "sceMtpHttpStop"));
        Assert.Equal(0, Call(w, "sceMtpHttpWrapperLoadStartModules", (ushort)2));
        Assert.Equal(0, Call(w, "sceMtpHttpWrapperStopUnloadModules"));
    }

    [Fact]
    public void HttpSetProxyNeedsTheLibraryAndAHost()
    {
        var http = Module<Http>();
        Assert.Equal(unchecked((int)0x80431001), Call(http, "sceHttpSetProxy", 1, 0, 0, Str("proxy"), (ushort)8080));
        Assert.Equal(0, Call(http, "sceHttpInit", 0x10000u));
        int tmpl = Call(http, "sceHttpCreateTemplate", Str("ua"), 1, 0);
        Assert.True(tmpl > 0);
        Assert.Equal(unchecked((int)0x804311FE), Call(http, "sceHttpSetProxy", tmpl, 1, 0, 0u, (ushort)8080));
        Assert.Equal(unchecked((int)0x80431100), Call(http, "sceHttpSetProxy", tmpl + 100, 1, 0, Str("p"), (ushort)1));
        Assert.Equal(0, Call(http, "sceHttpSetProxy", tmpl, 1, 0, Str("proxy.local"), (ushort)8080));
        Assert.Equal(0, Call(http, "sceHttpTerm"));
    }

    const int MtpInvalid = unchecked((int)0x80540003), MtpNoHost = unchecked((int)0x8054001B), MtpUsbDisconnected = unchecked((int)0x80540034);

    [Fact]
    public void MtpCoreGoesThroughItsStatesWithoutAHost()
    {
        var mtp = Module<Mtp>();
        uint id = Alloc(4), info = Alloc(0x68), flag = Alloc(4);
        Assert.Equal(unchecked((int)0x80540001), Call(mtp, "sceMtpStart", 1u, 0u));
        Assert.Equal(unchecked((int)0x80540001), Call(mtp, "sceMtpEnd"));
        Assert.Equal(0, Call(mtp, "sceMtpInit", 0u));
        Assert.Equal(unchecked((int)0x80540002), Call(mtp, "sceMtpInit", 0u));
        Assert.Equal(0, Call(mtp, "sceMtpGetHostInfo", info));
        Assert.Equal(7u, GuestMemory.R32(info)); // stopped
        Assert.Equal(MtpInvalid, Call(mtp, "BeginGetNumOfObject", 0u, 0u, 0u));
        Assert.Equal(MtpNoHost, Call(mtp, "BeginGetNumOfObject", 0u, 0u, id));
        Assert.Equal(unchecked((int)0x80540008), Call(mtp, "EndGetNumOfObject", 5u, 0u)); // no queue before Start
        Assert.Equal(0, Call(mtp, "sceMtpStart", 1u, 0u));
        Assert.Equal(unchecked((int)0x80540002), Call(mtp, "sceMtpStart", 1u, 0u));
        Assert.Equal(unchecked((int)0x80540004), Call(mtp, "sceMtpEnd"));
        Assert.Equal(0, Call(mtp, "sceMtpIsStarted", flag));
        Assert.Equal(1u, GuestMemory.R32(flag));
        Assert.Equal(0, Call(mtp, "sceMtpGetHostInfo", info));
        Assert.Equal(0u, GuestMemory.R32(info)); // disconnected
        Assert.Equal(3u, GuestMemory.R32(info + 0x60)); // no cable
        Assert.Equal(MtpUsbDisconnected, Call(mtp, "BeginImportObject", 0u, 0u, 0u, id, 0u, 0u));
        Assert.Equal(MtpInvalid, Call(mtp, "BeginGetTotalObjectSize", 0u, id));
        Assert.Equal(unchecked((int)0x80540011), Call(mtp, "EndImportObject", 5u, 0u));
        Assert.Equal(unchecked((int)0x80540011), Call(mtp, "sceMtpCheckContextValid", 5u));
        Assert.Equal(MtpUsbDisconnected, Call(mtp, "MarlinActivation", 0u, id, id));
        Assert.Equal(0, Call(mtp, "sceMtpStop"));
        Assert.Equal(unchecked((int)0x80540002), Call(mtp, "sceMtpStop"));
        Assert.Equal(0, Call(mtp, "sceMtpEnd"));
    }

    [Fact]
    public void MtpKeepsItsLocalSettings()
    {
        var mtp = Module<Mtp>();
        uint v = Alloc(16), got = Alloc(4), id = Alloc(16);
        // the Marlin settings: a size reallocates its buffer, which then holds the data
        GuestMemory.W32(v, 8);
        Assert.Equal(0, Call(mtp, "SetMarlinConfig", 1, 0u, v, 4u));
        GuestMemory.W32(v, 0x11223344); GuestMemory.W32(v + 4, 0x55667788);
        Assert.Equal(0, Call(mtp, "SetMarlinConfig", 3, 0u, v, 8u));
        Assert.Equal(0, Call(mtp, "GetMarlinConfig", 3, 4u, 4u, id, got));
        Assert.Equal(0x55667788u, GuestMemory.R32(id));
        Assert.Equal(4u, GuestMemory.R32(got));
        Assert.Equal(0, Call(mtp, "GetMarlinConfig", 1, 0u, 0u, id, got));
        Assert.Equal(8u, GuestMemory.R32(id));
        // one event callback slot; unsupported event bits are refused
        Assert.Equal(unchecked((int)0x805400FF), Call(mtp, "RegisterCallback", 0x40u, 0x81000001u, 0u));
        Assert.Equal(0, Call(mtp, "RegisterCallback", 0x1u, 0x81000001u, 0u));
        Assert.Equal(unchecked((int)0x80540008), Call(mtp, "RegisterCallback", 0x2u, 0x81000003u, 0u));
        Assert.Equal(0, Call(mtp, "UnregisterCallback", 0x1u, 0x81000001u));
        Assert.Equal(unchecked((int)0x80540011), Call(mtp, "UnregisterCallback", 0x1u, 0x81000001u));
        // Wi-Fi: the connection mode, no PC registered, the device id starts with the MAC address
        Assert.Equal(MtpInvalid, Call(mtp, "NetSetConnectionMode", 3));
        Assert.Equal(0, Call(mtp, "NetSetConnectionMode", 2));
        Assert.Equal(0, Call(mtp, "sceMtpGetCurrentInterface", got));
        Assert.Equal(2u, GuestMemory.R32(got));
        Assert.Equal(0, Call(mtp, "NetGetNumRegisteredHosts", got));
        Assert.Equal(0u, GuestMemory.R32(got));
        Assert.Equal(unchecked((int)0x805400D2), Call(mtp, "NetGetSelectedHost", id));
        Assert.Equal(0, Call(mtp, "NetGetDeviceId", id));
        Assert.Equal(0x00, GuestMemory.Read<byte>(id));
        Assert.Equal(0x1F, GuestMemory.Read<byte>(id + 1));
        Assert.Equal(1, GuestMemory.Read<byte>(id + 15));
        // the reset time is read once
        Assert.Equal(0, Call(mtp, "sceMtpReset"));
        Assert.NotEqual(0ul, (ulong)Invoke(mtp, "sceMtpGetResetTime")!);
        Assert.Equal(0ul, (ulong)Invoke(mtp, "sceMtpGetResetTime")!);
    }

    [Fact]
    public void MtpClientWithoutTheServiceFailsItsRequestsAndKeepsListeners()
    {
        var c = Module<MtpClient>();
        uint client = Alloc(0x60), id = Alloc(4), obs = Alloc(0x14), bg = Alloc(0x10);
        Assert.Equal(unchecked((int)0x80540102), Call(c, "ClientInitialize", 0u));
        Assert.Equal(unchecked((int)0x80540105), Call(c, "ClientInitialize", client)); // SceMtpService not reachable
        Assert.Equal(unchecked((int)0x80540107), Call(c, "Request", client));
        Assert.Equal(unchecked((int)0x80540107), Call(c, "ClientFinalize", client));
        Assert.Equal(unchecked((int)0x80540102), Call(c, "GetMsgFromServer", client, 0u, 1u, 1u, 0u));
        Assert.Equal(unchecked((int)0x80540107), Call(c, "GetMsgFromServer", client, 1u, 1u, 1u, 0u));
        Assert.Equal(unchecked((int)0x80540102), Call(c, "SetStatusListener", client, 0u));
        Assert.Equal(0, Call(c, "SetStatusListener", client, 0x1234u));
        Assert.Equal(unchecked((int)0x80540106), Call(c, "SetStatusListener", client, 0x1234u));
        Assert.Equal(0, Call(c, "ClearStatusListener", client));
        Assert.Equal(unchecked((int)0x80540106), Call(c, "ClearStatusListener", client));
        // the status observer: its vtable is callable guest code, its list is the process's
        Invoke(c, "StatusObserverCtor", obs);
        Assert.NotEqual(0u, GuestMemory.R32(obs));
        Assert.Equal(uint.MaxValue, GuestMemory.R32(obs + 0xC));
        Assert.Equal(unchecked((int)0x80540107), Call(c, "StatusObserverUnregister", obs, -1));
        Assert.Equal(0, Call(c, "StatusObserverInit", obs, client));
        Assert.Equal(unchecked((int)0x80540106), Call(c, "StatusObserverInit", obs, client));
        Assert.Equal(unchecked((int)0x80540107), Call(c, "StatusObserverRegister", obs, 1u, 2u));
        Invoke(c, "StatusObserverTerm", obs);
        // the background observer becomes the client's background listener
        Invoke(c, "BgObserverCtor", bg);
        Assert.Equal(unchecked((int)0x80540107), Call(c, "BgObserverRegister", bg, 1u, 2u));
        Assert.Equal(0, Call(c, "BgObserverInit", bg, client));
        Assert.Equal(0, Call(c, "BgObserverRegister", bg, 1u, 2u));
        Assert.Equal(bg, GuestMemory.R32(client + 0x50));
        Assert.Equal(unchecked((int)0x80540106), Call(c, "BgObserverRegister", bg, 1u, 2u));
        Invoke(c, "BgObserverTerm", bg);
        Assert.Equal(0u, GuestMemory.R32(client + 0x50));
        Assert.Equal("MTP USB_DISCONNECTED", GuestMemory.ReadCString(Convert.ToUInt32(Invoke(c, "GetErrorString", client, 0x80540034u))));
        Assert.Equal("MTP Marlin Core error code", MtpClient.ErrorString(0x80546123));
        Assert.Equal("unknown error code", MtpClient.ErrorString(0x80123456));
        GC.KeepAlive(id);
    }
}
