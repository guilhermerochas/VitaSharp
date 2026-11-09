using VitaSharp.Core.Memory;
using static VitaSharp.Core.Hle.ShellSvcIpc;

namespace VitaSharp.Core.Hle.Modules;

// The rest of libnetctl.suprx's system-side exports (FW 3.74, NIDs without public names), read in the module: the
// library checks that it is initialised (0x80412101), the pointers (0x80412107) and the sizes (0x80412111), and asks
// the shell's netctl service over libshellsvc (method 0x200xx; the shell, which hosts the service, gives the library a
// handler table instead, sceNetCtlSetServiceHandlers). The HLE forwards the same request to the shell's service
// (ShellSvcIpc), or calls the shell's handler when one is set. Without libshellsvc in the process the real library could
// not have initialised (sceNetCtlInit connects to the service first): not initialised. The request whose answer the HLE
// already gives locally (0x2000B, the connection information) stays local.

public sealed partial class Net
{
    const int NETCTL_ID_NOT_FOUND = unchecked((int)0x80412104), NETCTL_INVALID_ID = unchecked((int)0x80412105),
        NETCTL_INVALID_ADDR = unchecked((int)0x80412107);
    const int NETAUTH_NOT_INITIALIZED = unchecked((int)0x80414101), NETAUTH_INVALID_ADDR = unchecked((int)0x80414105);

    readonly Dictionary<int, NetCallback> _sysCallbacks3 = new(), _sysCallbacks4 = new();
    ulong _savedNetworkTick;
    uint _stopHook;

    int Svc(uint method, ReadOnlySpan<Arg> inArgs, ReadOnlySpan<Arg> outArgs) => Invoke(Emu, method, inArgs, outArgs, NETCTL_NOT_INITIALIZED);
    int Svc(uint method, params Arg[] inArgs) => Invoke(Emu, method, inArgs, [], NETCTL_NOT_INITIALIZED);

    /// <summary>The shell's handler at <paramref name="offset"/> of its table, or 0.</summary>
    uint Handler(uint offset) => _netctlServiceTable == 0 ? 0 : GuestMemory.R32(_netctlServiceTable + offset);

    int NoInit() => _netctlInited ? 0 : NETCTL_NOT_INITIALIZED;

    // ---- requests without arguments (no output but the service's result)

    [Hle("sceNetCtl_25EE061C", Nid = 0x25EE061C, Library = "SceNetCtl")] int NetCtlSvc3B() => _netctlInited ? Svc(0x2003B) : NETCTL_NOT_INITIALIZED;
    [Hle("sceNetCtl_2792E1B6", Nid = 0x2792E1B6, Library = "SceNetCtl")] int NetCtlSvc2D() => _netctlInited ? Svc(0x2002D) : NETCTL_NOT_INITIALIZED;
    [Hle("sceNetCtl_2B84A67F", Nid = 0x2B84A67F, Library = "SceNetCtl")] int NetCtlSvc3A() => _netctlInited ? Svc(0x2003A) : NETCTL_NOT_INITIALIZED;
    [Hle("sceNetCtl_2FB386D9", Nid = 0x2FB386D9, Library = "SceNetCtl")] int NetCtlSvc33() => _netctlInited ? Svc(0x20033) : NETCTL_NOT_INITIALIZED;
    [Hle("sceNetCtl_32A0BD7F", Nid = 0x32A0BD7F, Library = "SceNetCtl")] int NetCtlSvc29() => _netctlInited ? Svc(0x20029) : NETCTL_NOT_INITIALIZED;
    [Hle("sceNetCtl_85C9CC02", Nid = 0x85C9CC02, Library = "SceNetCtl")] int NetCtlSvc2C() => _netctlInited ? Svc(0x2002C) : NETCTL_NOT_INITIALIZED;
    [Hle("sceNetCtl_8F09A038", Nid = 0x8F09A038, Library = "SceNetCtl")] int NetCtlSvc28() => _netctlInited ? Svc(0x20028) : NETCTL_NOT_INITIALIZED;
    [Hle("sceNetCtl_9629B72F", Nid = 0x9629B72F, Library = "SceNetCtl")] int NetCtlSvc27() => _netctlInited ? Svc(0x20027) : NETCTL_NOT_INITIALIZED;
    [Hle("sceNetCtl_9D96400C", Nid = 0x9D96400C, Library = "SceNetCtl")] int NetCtlSvc3E() => _netctlInited ? Svc(0x2003E) : NETCTL_NOT_INITIALIZED;
    [Hle("sceNetCtl_AA63CAEC", Nid = 0xAA63CAEC, Library = "SceNetCtl")] int NetCtlSvc2A() => _netctlInited ? Svc(0x2002A) : NETCTL_NOT_INITIALIZED;

    // ---- one int in

    [Hle("sceNetCtl_0815CAC5", Nid = 0x0815CAC5, Library = "SceNetCtl")] int NetCtlSvc32(uint a) => _netctlInited ? Svc(0x20032, Val(a)) : NETCTL_NOT_INITIALIZED;
    [Hle("sceNetCtl_56C95F8F", Nid = 0x56C95F8F, Library = "SceNetCtl")] int NetCtlSvc2F(uint a) => _netctlInited ? Svc(0x2002F, Val(a)) : NETCTL_NOT_INITIALIZED;
    [Hle("sceNetCtl_57CC3812", Nid = 0x57CC3812, Library = "SceNetCtl")] int NetCtlSvc3D(uint a) => _netctlInited ? Svc(0x2003D, Val(a)) : NETCTL_NOT_INITIALIZED;
    [Hle("sceNetCtl_67DE0C31", Nid = 0x67DE0C31, Library = "SceNetCtl")] int NetCtlSvc07(uint a) => _netctlInited ? Svc(0x20007, Val(a)) : NETCTL_NOT_INITIALIZED;
    /// <summary>(void): method 0x20006 with a 0 word.</summary>
    [Hle("sceNetCtl_2CF683AD", Nid = 0x2CF683AD, Library = "SceNetCtl")] int NetCtlSvc06() => _netctlInited ? Svc(0x20006, Val(0)) : NETCTL_NOT_INITIALIZED;
    /// <summary>(void): method 0x2000A with a 0 word.</summary>
    [Hle("sceNetCtl_DD96DA85", Nid = 0xDD96DA85, Library = "SceNetCtl")] int NetCtlSvc0A() => _netctlInited ? Svc(0x2000A, Val(0)) : NETCTL_NOT_INITIALIZED;

    /// <summary>(void): handler +0x28, else method 0x20011 with a 0 word.</summary>
    [Hle("sceNetCtl_47980B9B", Nid = 0x47980B9B, Library = "SceNetCtl")]
    int NetCtlSvc11()
    {
        if (!_netctlInited) return NETCTL_NOT_INITIALIZED;
        return Handler(0x28) is var h and not 0 ? CallHandler(h) : Svc(0x20011, Val(0));
    }

    /// <summary>(int a): handler +0x4C, else method 0x2001A.</summary>
    [Hle("sceNetCtl_CCF30B92", Nid = 0xCCF30B92, Library = "SceNetCtl")]
    int NetCtlSvc1A(uint a)
    {
        if (!_netctlInited) return NETCTL_NOT_INITIALIZED;
        return Handler(0x4C) is var h and not 0 ? CallHandler(h, a) : Svc(0x2001A, Val(a));
    }

    /// <summary>(int a, int b): handler +0x5C, else method 0x20035 with both words.</summary>
    [Hle("sceNetCtl_8B2FEFAA", Nid = 0x8B2FEFAA, Library = "SceNetCtl")]
    int NetCtlSvc35(uint a, uint b)
    {
        if (!_netctlInited) return NETCTL_NOT_INITIALIZED;
        return Handler(0x5C) is var h and not 0 ? CallHandler(h, a, b) : Svc(0x20035, Val(a, b));
    }

    // ---- a buffer in (SSID-sized strings of 0x21 bytes, a 0x478-byte network entry)

    int SvcInBuf(uint method, uint p, uint size, bool checkNull)
    {
        if (!_netctlInited) return NETCTL_NOT_INITIALIZED;
        if (checkNull && p == 0) return NETCTL_INVALID_ADDR;
        return Svc(method, Buf(p, size));
    }

    [Hle("sceNetCtl_1581FABC", Nid = 0x1581FABC, Library = "SceNetCtl")] int NetCtlSvc1C(uint p) => SvcInBuf(0x2001C, p, 0x21, true);
    [Hle("sceNetCtl_6FFC4D4E", Nid = 0x6FFC4D4E, Library = "SceNetCtl")] int NetCtlSvc1D(uint p) => SvcInBuf(0x2001D, p, 0x21, true);
    [Hle("sceNetCtl_C1677C65", Nid = 0xC1677C65, Library = "SceNetCtl")] int NetCtlSvc1B(uint p) => SvcInBuf(0x2001B, p, 0x21, false);
    [Hle("sceNetCtl_236DED99", Nid = 0x236DED99, Library = "SceNetCtl")] int NetCtlSvc38(uint p) => SvcInBuf(0x20038, p, 0x478, true);

    /// <summary>(const char name[0x21], int a): method 0x2001E with the word, then the name.</summary>
    [Hle("sceNetCtl_D315EE4F", Nid = 0xD315EE4F, Library = "SceNetCtl")]
    int NetCtlSvc1E(uint p, uint a)
    {
        if (!_netctlInited) return NETCTL_NOT_INITIALIZED;
        if (p == 0) return NETCTL_INVALID_ADDR;
        return Svc(0x2001E, Val(a), Buf(p, 0x21));
    }

    /// <summary>(const entry *entries 0x478 each, uint count 1..30): method 0x20036 with the count and the entries.</summary>
    [Hle("sceNetCtl_3C35D284", Nid = 0x3C35D284, Library = "SceNetCtl")]
    int NetCtlSvc36(uint p, uint n)
    {
        if (!_netctlInited) return NETCTL_NOT_INITIALIZED;
        if (p == 0) return NETCTL_INVALID_ADDR;
        if (n == 0 || n > 30) return NETCTL_INVALID_SIZE;
        return Svc(0x20036, Val(n), Buf(p, n * 0x478));
    }

    /// <summary>(entry *entries 0x478 each, uint count 1..30): method 0x20037 sends the count and the first word of each
    /// entry (its id) and gets the entries back.</summary>
    [Hle("sceNetCtl_1EA25873", Nid = 0x1EA25873, Library = "SceNetCtl")]
    int NetCtlSvc37(uint p, uint n)
    {
        if (!_netctlInited) return NETCTL_NOT_INITIALIZED;
        if (p == 0) return NETCTL_INVALID_ADDR;
        if (n == 0 || n > 30) return NETCTL_INVALID_SIZE;
        var ids = new uint[n];
        for (uint i = 0; i < n; i++) ids[i] = GuestMemory.R32(p + i * 0x478);
        return Svc(0x20037, [Val(n), Val(ids)], [Buf(p, n * 0x478)]);
    }

    /// <summary>(config *c 0x608 with two (pointer, size) blocks at +0x5C8/+0x5D0): method 0x20008 with the two sizes,
    /// the config and both blocks.</summary>
    [Hle("sceNetCtl_17FB44C4", Nid = 0x17FB44C4, Library = "SceNetCtl")]
    int NetCtlSvc08(uint p)
    {
        if (!_netctlInited) return NETCTL_NOT_INITIALIZED;
        if (p == 0) return NETCTL_INVALID_ADDR;
        uint b1 = GuestMemory.R32(p + 0x5C8), s1 = GuestMemory.R32(p + 0x5CC), b2 = GuestMemory.R32(p + 0x5D0), s2 = GuestMemory.R32(p + 0x5D4);
        return Svc(0x20008, Val(s1, s2), Buf(p, 0x608), Buf(b1, s1), Buf(b2, s2));
    }

    /// <summary>(config *c, int a): as NID_17FB44C4 with a third word, method 0x20009.</summary>
    [Hle("sceNetCtl_54848654", Nid = 0x54848654, Library = "SceNetCtl")]
    int NetCtlSvc09(uint p, uint a)
    {
        if (!_netctlInited) return NETCTL_NOT_INITIALIZED;
        if (p == 0) return NETCTL_INVALID_ADDR;
        uint b1 = GuestMemory.R32(p + 0x5C8), s1 = GuestMemory.R32(p + 0x5CC), b2 = GuestMemory.R32(p + 0x5D0), s2 = GuestMemory.R32(p + 0x5D4);
        return Svc(0x20009, Val(s1, s2, a), Buf(p, 0x608), Buf(b1, s1), Buf(b2, s2));
    }

    // ---- a buffer out

    int SvcOut(uint method, uint p, uint size, bool checkNull, uint handlerOffset = uint.MaxValue)
    {
        if (!_netctlInited) return NETCTL_NOT_INITIALIZED;
        if (checkNull && p == 0) return NETCTL_INVALID_ADDR;
        if (handlerOffset != uint.MaxValue && Handler(handlerOffset) is var h and not 0) return CallHandler(h, p);
        return Svc(method, [], [Buf(p, size)]);
    }

    [Hle("sceNetCtl_1AEEB480", Nid = 0x1AEEB480, Library = "SceNetCtl")] int NetCtlSvc26(uint p) => SvcOut(0x20026, p, 4, true);
    [Hle("sceNetCtl_2F2A259D", Nid = 0x2F2A259D, Library = "SceNetCtl")] int NetCtlSvc39(uint p) => SvcOut(0x20039, p, 0x478, true);
    [Hle("sceNetCtl_32237A58", Nid = 0x32237A58, Library = "SceNetCtl")] int NetCtlSvc3C(uint p) => SvcOut(0x2003C, p, 4, false);
    /// <summary>(uint8_t mac[6]).</summary>
    [Hle("sceNetCtl_6D63C34B", Nid = 0x6D63C34B, Library = "SceNetCtl")] int NetCtlSvc0C(uint p) => SvcOut(0x2000C, p, 6, true);
    [Hle("sceNetCtl_C2C30635", Nid = 0xC2C30635, Library = "SceNetCtl")] int NetCtlSvc2E(uint p) => SvcOut(0x2002E, p, 0x30C, true);
    [Hle("sceNetCtl_C15740E9", Nid = 0xC15740E9, Library = "SceNetCtl")] int NetCtlSvc19(uint p) => SvcOut(0x20019, p, 4, true, 0x48);

    int SvcIntOut(uint method, uint a, uint p, uint size, uint handlerOffset = uint.MaxValue)
    {
        if (!_netctlInited) return NETCTL_NOT_INITIALIZED;
        if (p == 0) return NETCTL_INVALID_ADDR;
        if (handlerOffset != uint.MaxValue && Handler(handlerOffset) is var h and not 0) return CallHandler(h, a, p);
        return Svc(method, [Val(a)], [Buf(p, size)]);
    }

    [Hle("sceNetCtl_300E8877", Nid = 0x300E8877, Library = "SceNetCtl")] int NetCtlSvc03(uint a, uint p) => SvcIntOut(0x20003, a, p, 4);
    [Hle("sceNetCtl_AC00BA34", Nid = 0xAC00BA34, Library = "SceNetCtl")] int NetCtlSvc05(uint a, uint p) => SvcIntOut(0x20005, a, p, 4);
    [Hle("sceNetCtl_92EB6B1A", Nid = 0x92EB6B1A, Library = "SceNetCtl")] int NetCtlSvc12(uint a, uint p) => SvcIntOut(0x20012, a, p, 0x60, 0x2C);
    [Hle("sceNetCtl_CDEBA500", Nid = 0xCDEBA500, Library = "SceNetCtl")] int NetCtlSvc21(uint a, uint p) => SvcIntOut(0x20021, a, p, 0x24, 0x0C);

    /// <summary>(int code, SceNetCtlInfo *info): handler +0, else method 0x2000B, the request sceNetCtlInetGetInfo
    /// makes; the HLE answers it from its connection model, as for sceNetCtlInetGetInfo.</summary>
    [Hle("sceNetCtl_D24E1CBB", Nid = 0xD24E1CBB, Library = "SceNetCtl")]
    int NetCtlInetGetInfoInternal(int code, uint info)
    {
        if (!_netctlInited) return NETCTL_NOT_INITIALIZED;
        if (info == 0) return NETCTL_INVALID_ADDR;
        if (Handler(0) is var h and not 0) return CallHandler(h, (uint)code, info);
        return sceNetCtlInetGetInfo(code, info);
    }

    /// <summary>(int a, struct *s with s->size == 0x40): handler +0x54, else method 0x20031 with (a, 0x40) in and the
    /// struct out.</summary>
    [Hle("sceNetCtl_8F2C2053", Nid = 0x8F2C2053, Library = "SceNetCtl")]
    int NetCtlSvc31(uint a, uint p)
    {
        if (!_netctlInited) return NETCTL_NOT_INITIALIZED;
        if (p == 0) return NETCTL_INVALID_ADDR;
        if (GuestMemory.R32(p) != 0x40) return NETCTL_INVALID_SIZE;
        if (Handler(0x54) is var h and not 0) return CallHandler(h, a, p);
        return Svc(0x20031, [Val(a, 0x40)], [Buf(p, 0x40)]);
    }

    /// <summary>(struct *s with s->size == 0x28): handler +0x20, else method 0x20025 with the size in and the struct out.</summary>
    [Hle("sceNetCtl_F30365D6", Nid = 0xF30365D6, Library = "SceNetCtl")]
    int NetCtlSvc25(uint p)
    {
        if (!_netctlInited) return NETCTL_NOT_INITIALIZED;
        if (p == 0) return NETCTL_INVALID_ADDR;
        if (GuestMemory.R32(p) != 0x28) return NETCTL_INVALID_SIZE;
        if (Handler(0x20) is var h and not 0) return CallHandler(h, p);
        return Svc(0x20025, [Val(0x28)], [Buf(p, 0x28)]);
    }

    /// <summary>(void *out 0x60, int a): handler +0x30, else method 0x20013 with the word in, the block and a word out
    /// (the library drops the word).</summary>
    [Hle("sceNetCtl_A840C74C", Nid = 0xA840C74C, Library = "SceNetCtl")]
    int NetCtlSvc13(uint p, uint a)
    {
        if (!_netctlInited) return NETCTL_NOT_INITIALIZED;
        if (Handler(0x30) is var h and not 0) return CallHandler(h, p, a);
        return Svc(0x20013, [Val(a)], [Buf(p, 0x60), Cell()]);
    }

    /// <summary>(void *out 0x60, int a): handler +0x3C, else method 0x20016 (as NID_A840C74C).</summary>
    [Hle("sceNetCtl_484EE0B2", Nid = 0x484EE0B2, Library = "SceNetCtl")]
    int NetCtlSvc16(uint p, uint a)
    {
        if (!_netctlInited) return NETCTL_NOT_INITIALIZED;
        if (Handler(0x3C) is var h and not 0) return CallHandler(h, p, a);
        return Svc(0x20016, [Val(a)], [Buf(p, 0x60), Cell()]);
    }

    /// <summary>(const uint8_t mac[6], void *out 0x60, void *data 0x800, uint *size): the size goes in (when data and size
    /// are given), the block, the data and the new size come back. Handler +0x34, method 0x20014.</summary>
    [Hle("sceNetCtl_1A254169", Nid = 0x1A254169, Library = "SceNetCtl")]
    int NetCtlSvc14(uint mac, uint p, uint data, uint size) => MacQuery(0x20014, 0x34, mac, p, data, size);

    /// <summary>As NID_1A254169, handler +0x40, method 0x20017.</summary>
    [Hle("sceNetCtl_A59146E1", Nid = 0xA59146E1, Library = "SceNetCtl")]
    int NetCtlSvc17(uint mac, uint p, uint data, uint size) => MacQuery(0x20017, 0x40, mac, p, data, size);

    int MacQuery(uint method, uint handlerOffset, uint mac, uint p, uint data, uint size)
    {
        if (!_netctlInited) return NETCTL_NOT_INITIALIZED;
        if (mac == 0 || p == 0) return NETCTL_INVALID_ADDR;
        if (Handler(handlerOffset) is var h and not 0) return CallHandler(h, mac, p, data, size);
        uint v = size != 0 && data != 0 ? GuestMemory.R32(size) : 0;
        Span<uint> cell = stackalloc uint[1];
        int r = Invoke(Emu, method, [Val(v), Buf(mac, 6)], [Buf(p, 0x60), Buf(data, 0x800), Cell()], cell, NETCTL_NOT_INITIALIZED);
        if (r >= 0 && size != 0) GuestMemory.W32(size, cell[0]);
        return r;
    }

    /// <summary>(void *out 0x60, void *data 0x800, uint *size): as NID_1A254169 without the address; handler +0x38,
    /// method 0x20015.</summary>
    [Hle("sceNetCtl_27932A1B", Nid = 0x27932A1B, Library = "SceNetCtl")]
    int NetCtlSvc15(uint p, uint data, uint size)
    {
        if (!_netctlInited) return NETCTL_NOT_INITIALIZED;
        if (p == 0) return NETCTL_INVALID_ADDR;
        if (Handler(0x38) is var h and not 0) return CallHandler(h, p, data, size);
        uint v = size != 0 && data != 0 ? GuestMemory.R32(size) : 0;
        Span<uint> cell = stackalloc uint[1];
        int r = Invoke(Emu, 0x20015, [Val(v)], [Buf(p, 0x60), Buf(data, 0x800), Cell()], cell, NETCTL_NOT_INITIALIZED);
        if (r >= 0 && size != 0) GuestMemory.W32(size, cell[0]);
        return r;
    }

    // ---- local state of the library

    /// <summary>(void): keeps the current network time (sceRtcGetCurrentNetworkTick) for NID_2B34DCCE.</summary>
    [Hle("sceNetCtl_21AD7A46", Nid = 0x21AD7A46, Library = "SceNetCtl")]
    int NetCtlSaveNetworkTick()
    {
        if (!_netctlInited) return NETCTL_NOT_INITIALIZED;
        _savedNetworkTick = Rtc.NowTick;
        return 0;
    }

    /// <summary>(SceRtcTick *tick): the time NID_21AD7A46 kept (0 before).</summary>
    [Hle("sceNetCtl_2B34DCCE", Nid = 0x2B34DCCE, Library = "SceNetCtl")]
    int NetCtlGetSavedNetworkTick(uint tick)
    {
        if (!_netctlInited) return NETCTL_NOT_INITIALIZED;
        if (tick == 0) return NETCTL_INVALID_ADDR;
        GuestMemory.Write(tick, _savedNetworkTick);
        return 0;
    }

    /// <summary>(void (*hook)(void)): a function the library calls when it stops and on sceNetCtlAdhocDisconnect.</summary>
    [Hle("sceNetCtl_326FEE96", Nid = 0x326FEE96, Library = "SceNetCtl")]
    int NetCtlSetStopHook(uint hook) { _stopHook = hook; return 0; }

    /// <summary>(void): clears the shell events sceNetCtlInit registered (ten of them); the HLE init registers none.</summary>
    [Hle("sceNetCtl_F081EFF9", Nid = 0xF081EFF9, Library = "SceNetCtl")]
    int NetCtlClearEvents() => _netctlInited ? 0 : NETCTL_NOT_INITIALIZED;

    /// <summary>(int kind 1..7): the SceShellSvc feature bit for the kind (10, 2, 6, 8, 12, 14, 4), else 0 without a call.</summary>
    [Hle("sceNetCtl_8E379B9C", Nid = 0x8E379B9C, Library = "SceNetCtl")]
    int NetCtlShellFeature3(int kind) => ShellSvcFeature(kind is >= 1 and <= 7 ? new[] { 10, 2, 6, 8, 12, 14, 4 }[kind - 1] : 0);

    /// <summary>(int kind 1..3): the SceShellSvc feature bit 0x14, 0x10, 0x12, else 0.</summary>
    [Hle("sceNetCtl_E4B3142E", Nid = 0xE4B3142E, Library = "SceNetCtl")]
    int NetCtlShellFeature4(int kind) => ShellSvcFeature(kind is >= 1 and <= 3 ? new[] { 0x14, 0x10, 0x12 }[kind - 1] : 0);

    // the two other system callback lists (8 slots each) and the unregister calls of all four

    [Hle("sceNetCtl_5AD6CBE8", Nid = 0x5AD6CBE8, Library = "SceNetCtl")]
    int NetCtlRegisterCallbackSys3(uint func, uint arg, uint cid) => RegisterSysCallback(2, func, arg, cid);
    [Hle("sceNetCtl_A25FE027", Nid = 0xA25FE027, Library = "SceNetCtl")]
    int NetCtlRegisterCallbackSys4(uint func, uint arg, uint cid) => RegisterSysCallback(3, func, arg, cid);

    int UnregisterSysCallback(int list, int id)
    {
        if (!_netctlInited) return NETCTL_NOT_INITIALIZED;
        if ((uint)id > 7) return NETCTL_INVALID_ID;
        lock (_inetCallbacks) return SysList(list).Remove(id) ? 0 : NETCTL_ID_NOT_FOUND;
    }

    Dictionary<int, NetCallback> SysList(int list) => list switch { 0 => _sysCallbacks1, 1 => _sysCallbacks2, 2 => _sysCallbacks3, _ => _sysCallbacks4 };

    [Hle("sceNetCtl_229EAA64", Nid = 0x229EAA64, Library = "SceNetCtl")] int NetCtlUnregisterCallbackSys1(int id) => UnregisterSysCallback(0, id);
    [Hle("sceNetCtl_3AB4D791", Nid = 0x3AB4D791, Library = "SceNetCtl")] int NetCtlUnregisterCallbackSys2(int id) => UnregisterSysCallback(1, id);
    [Hle("sceNetCtl_E99EE305", Nid = 0xE99EE305, Library = "SceNetCtl")] int NetCtlUnregisterCallbackSys3(int id) => UnregisterSysCallback(2, id);
    [Hle("sceNetCtl_E63C100B", Nid = 0xE63C100B, Library = "SceNetCtl")] int NetCtlUnregisterCallbackSys4(int id) => UnregisterSysCallback(3, id);

    /// <summary>(const SceNpCommunicationId *id, char *buf, uint size &gt; 0x20): "SCE_&lt;id&gt;_&lt;nn&gt;" (the
    /// ad hoc authentication name), cut to size-1 characters.</summary>
    [Hle("sceNetCtl_FFDC123A", Nid = 0xFFDC123A, Library = "SceNetCtl")]
    int NetCtlAdhocAuthName(uint commId, uint buf, uint size)
    {
        if (size < 0x21) return NETCTL_INVALID_SIZE;
        GuestMemory.Fill(buf, 0, size);
        string s = $"SCE_{GuestMemory.ReadCString(commId, 16)}_{GuestMemory.Read<byte>(commId + 10):D2}";
        GuestMemory.WriteCString(buf, s.Length > (int)size - 2 ? s[..((int)size - 2)] : s, (int)size - 1);
        return 0;
    }

    // ---- the ad hoc authentication client context (0x1F44 bytes; errors 0x80414101 / 0x80414105)

    /// <summary>(ctx *c): clears the context, no socket (+8 = -1), marks it in use.</summary>
    [Hle("sceNetCtl_743E9FCE", Nid = 0x743E9FCE, Library = "SceNetCtl")]
    int NetAuthCtxInit(uint ctx)
    {
        if (ctx == 0) return NETAUTH_INVALID_ADDR;
        GuestMemory.Fill(ctx, 0, 0x1F44);
        GuestMemory.W32(ctx + 8, uint.MaxValue);
        GuestMemory.Write<byte>(ctx, 1);
        return 0;
    }

    /// <summary>(ctx *c): asks the context to stop (+1 = 1) and aborts its socket if it has one.</summary>
    [Hle("sceNetCtl_68C081D1", Nid = 0x68C081D1, Library = "SceNetCtl")]
    int NetAuthCtxAbort(uint ctx)
    {
        if (ctx == 0) return NETAUTH_INVALID_ADDR;
        if (GuestMemory.Read<byte>(ctx) == 0) return NETAUTH_NOT_INITIALIZED;
        GuestMemory.Write<byte>(ctx + 1, 1);
        int sock = (int)GuestMemory.R32(ctx + 8);
        if (sock >= 0) sceNetSocketAbort(sock, 3);
        return 0;
    }

    /// <summary>(ctx *c): aborts a running exchange (+4 bit 0) and clears the context.</summary>
    [Hle("sceNetCtl_9B678FEE", Nid = 0x9B678FEE, Library = "SceNetCtl")]
    void NetAuthCtxTerm(uint ctx)
    {
        if (ctx == 0 || GuestMemory.Read<byte>(ctx) == 0) return;
        if ((GuestMemory.R32(ctx + 4) & 1) != 0) NetAuthCtxAbort(ctx);
        GuestMemory.Fill(ctx, 0, 0x1F44);
    }
}
