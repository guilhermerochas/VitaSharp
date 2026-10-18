using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// Network, PSN and web services. VitaSharp behaves like a console that is offline and signed out of PSN:
/// initialisation succeeds, connection attempts fail with the errors games handle gracefully.
/// </summary>
public sealed class Net : HleModule
{
    const int ERR_NET_NOT_CONNECTED = unchecked((int)0x80410179); // SCE_NET_ERROR_ENOTCONN-ish
    const int ERR_NETCTL_NOT_CONNECTED = unchecked((int)0x80412108);
    uint _errnoLoc;
    int _nextId = 1;

    [Hle] int sceNetInit(uint param) => 0;
    [Hle] int sceNetTerm() => 0;
    [Hle] uint sceNetErrnoLoc() { if (_errnoLoc == 0) _errnoLoc = Emu.Memory.Alloc(16, "net errno", MemoryRegion.User, 16); return _errnoLoc; }
    [Hle] uint sceNetHtonl(uint v) => System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(v);
    [Hle] uint sceNetNtohl(uint v) => System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(v);
    [Hle] uint sceNetHtons(uint v) => System.Buffers.Binary.BinaryPrimitives.ReverseEndianness((ushort)v);
    [Hle] uint sceNetNtohs(uint v) => System.Buffers.Binary.BinaryPrimitives.ReverseEndianness((ushort)v);

    [Hle]
    int sceNetGetMacAddress(uint addr, int flags)
    {
        if (addr == 0) return unchecked((int)0x80410116);
        byte[] mac = [0x00, 0x1F, 0xA7, 0x56, 0x49, 0x54];
        mac.CopyTo(GuestMemory.Span(addr, 6));
        return 0;
    }

    [Hle]
    uint sceNetInetNtop(int af, uint src, uint dst, uint size)
    {
        if (src == 0 || dst == 0) return 0;
        var b = GuestMemory.Span(src, 4);
        GuestMemory.WriteCString(dst, $"{b[0]}.{b[1]}.{b[2]}.{b[3]}", (int)size);
        return dst;
    }

    [Hle]
    int sceNetInetPton(int af, string src, uint dst)
    {
        if (dst == 0 || !System.Net.IPAddress.TryParse(src, out var ip)) return 0;
        ip.GetAddressBytes().AsSpan(0, 4).CopyTo(GuestMemory.Span(dst, 4));
        return 1;
    }

    // sockets: creation works, anything touching the network fails
    [Hle] int sceNetSocket(string name, int domain, int type, int protocol) => _nextId++;
    [Hle] int sceNetSocketClose(int s) => 0;
    [Hle] int sceNetSocketAbort(int s, int flags) => 0;
    [Hle] int sceNetShutdown(int s, int how) => 0;
    [Hle] int sceNetBind(int s, uint addr, uint len) => 0;
    [Hle] int sceNetListen(int s, int backlog) => 0;
    [Hle] int sceNetAccept(int s, uint addr, uint len) => ERR_NET_NOT_CONNECTED;
    [Hle] int sceNetConnect(int s, uint addr, uint len) => ERR_NET_NOT_CONNECTED;
    [Hle] int sceNetSend(int s, uint buf, uint len, int flags) => ERR_NET_NOT_CONNECTED;
    [Hle] int sceNetSendto(int s, uint buf, uint len, int flags, uint to, uint tolen) => ERR_NET_NOT_CONNECTED;
    [Hle] int sceNetRecv(int s, uint buf, uint len, int flags) => unchecked((int)0x80410123); // EAGAIN
    [Hle] int sceNetRecvfrom(int s, uint buf, uint len, int flags, uint from, uint fromlen) => unchecked((int)0x80410123);
    [Hle] int sceNetSetsockopt(int s, int level, int opt, uint val, uint len) => 0;
    [Hle] int sceNetGetsockopt(int s, int level, int opt, uint val, uint len) { if (val != 0) GuestMemory.W32(val, 0); return 0; }
    [Hle] int sceNetGetsockname(int s, uint addr, uint len) { if (addr != 0) GuestMemory.Fill(addr, 0, 16); return 0; }
    [Hle] int sceNetGetSockInfo(int s, uint info, int n, int flags) => 0;
    [Hle]
    int sceNetGetStatisticsInfo(uint info, int flags)
    {
        if (info == 0) return unchecked((int)0x80410116);
        // SceNetStatisticsInfo: kernel free/min, packet count, qos count, libnet free/min (24 bytes)
        GuestMemory.W32(info, 0x40000); GuestMemory.W32(info + 4, 0x30000); GuestMemory.W32(info + 8, 0);
        GuestMemory.W32(info + 12, 0); GuestMemory.W32(info + 16, 0x20000); GuestMemory.W32(info + 20, 0x18000);
        return 0;
    }
    [Hle] int sceNetShowNetstat() => 0;
    [Hle] int sceNetEpollCreate(string name, int flags) => _nextId++;
    [Hle] int sceNetEpollDestroy(int id) => 0;
    [Hle] int sceNetEpollControl(int id, int op, int s, uint ev) => 0;
    [Hle] int sceNetEpollWait(int id, uint events, int max, int timeout) { Kernel.Wait.Sleep((ulong)Math.Max(timeout, 1000)); return 0; }
    [Hle] int sceNetResolverCreate(string name, uint param, int flags) => _nextId++;
    [Hle] int sceNetResolverDestroy(int rid) => 0;
    [Hle] int sceNetResolverStartNtoa(int rid, string host, uint addr, int timeout, int retry, int flags) => unchecked((int)0x804101E1);
    [Hle] int sceNetResolverStartAton(int rid, uint addr, uint host, int len, int timeout, int retry, int flags) => unchecked((int)0x804101E1);
    [Hle] int sceNetResolverGetError(int rid, Ptr<int> err) { err.TrySet(unchecked((int)0x804101E1)); return 0; }

    // NetCtl: initialised, never connected
    // NetCtl: initialised, cable unplugged. Registered callbacks are told so once (DISCONNECTED event),
    // which is what games wait for before leaving their "checking connection" states.
    const int NETCTL_EVENT_DISCONNECTED = 1;
    sealed class NetCallback { public uint Func, Arg; public bool Notified; }
    readonly Dictionary<int, NetCallback> _inetCallbacks = new(), _adhocCallbacks = new();
    int _nextCid;

    [Hle] int sceNetCtlInit() => 0;
    [Hle] void sceNetCtlTerm() { lock (_inetCallbacks) { _inetCallbacks.Clear(); _adhocCallbacks.Clear(); } }

    [Hle]
    int sceNetCtlCheckCallback()
    {
        var pending = new List<NetCallback>();
        lock (_inetCallbacks)
        {
            foreach (var c in _inetCallbacks.Values.Concat(_adhocCallbacks.Values))
                if (!c.Notified) { c.Notified = true; pending.Add(c); }
        }
        foreach (var c in pending) Kernel.GuestThread.Current?.CallGuest(c.Func, NETCTL_EVENT_DISCONNECTED, c.Arg);
        return 0;
    }

    // The emulated console is on a LAN (IP obtained) but has no route to Sony's servers and is signed out of PSN,
    // the same profile Vita3K presents; socket/HTTP calls fail like an unreachable server.
    [Hle] int sceNetCtlInetGetState(Ptr<int> state) { if (state.Address == 0) return unchecked((int)0x80412102); state.Value = 3; return 0; }
    [Hle] int sceNetCtlInetGetResult(int eventType, Ptr<int> error) { if (error.Address == 0) return unchecked((int)0x80412102); error.Value = 0; return 0; }

    [Hle]
    int sceNetCtlInetGetInfo(int code, uint info)
    {
        if (info == 0) return unchecked((int)0x80412102);
        switch (code)
        {
            case 1: GuestMemory.WriteCString(info, "VitaSharp", 65); break;           // configuration name
            case 2: GuestMemory.W32(info, 0); break;                                // device: wireless
            case 3: sceNetGetMacAddress(info, 0); break;                           // ether address
            case 4: GuestMemory.W32(info, 1500); break;                             // MTU
            case 5: GuestMemory.W32(info, 1); break;                                // link up
            case 6: new byte[] { 0x00, 0x1F, 0xA7, 0x00, 0x00, 0x01 }.CopyTo(GuestMemory.Span(info, 6)); break;
            case 7: GuestMemory.WriteCString(info, "VitaSharpNet", 33); break;     // SSID
            case 8: GuestMemory.W32(info, 3); break;                                // WPA/WPA2
            case 9: GuestMemory.W32(info, 40); break;                               // rssi dBm
            case 10: GuestMemory.W32(info, 100); break;                             // rssi %
            case 11: GuestMemory.W32(info, 6); break;                               // channel
            case 12: GuestMemory.W32(info, 0); break;                               // DHCP
            case 13: GuestMemory.WriteCString(info, "", 256); break;
            case 14: GuestMemory.WriteCString(info, "", 128); break;
            case 15: GuestMemory.WriteCString(info, "192.168.0.10", 16); break;
            case 16: GuestMemory.WriteCString(info, "255.255.255.0", 16); break;
            case 17: GuestMemory.WriteCString(info, "192.168.0.1", 16); break;
            case 18: GuestMemory.WriteCString(info, "192.168.0.1", 16); break;
            case 19: GuestMemory.WriteCString(info, "0.0.0.0", 16); break;
            case 20: GuestMemory.W32(info, 0); break;                               // no proxy
            case 21: GuestMemory.WriteCString(info, "", 256); break;
            case 22: GuestMemory.W32(info, 0); break;
            default: return unchecked((int)0x80412102);
        }
        return 0;
    }

    int RegisterNetCallback(Dictionary<int, NetCallback> table, uint func, Ptr<int> cid)
    {
        if (func == 0 || cid.Address == 0) return unchecked((int)0x80412102);
        lock (_inetCallbacks)
        {
            if (table.Count >= 8) return unchecked((int)0x80412104);
            int id = _nextCid++;
            table[id] = new NetCallback { Func = func, Arg = 0 };
            cid.Value = id;
            return id;
        }
    }

    [Hle]
    int sceNetCtlInetRegisterCallback(uint func, uint arg, Ptr<int> cid)
    {
        int r = RegisterNetCallback(_inetCallbacks, func, cid);
        if (r < 0) return r;
        lock (_inetCallbacks) _inetCallbacks[r].Arg = arg;
        return 0;
    }

    [Hle] int sceNetCtlInetUnregisterCallback(int cid) { lock (_inetCallbacks) _inetCallbacks.Remove(cid); return 0; }
    [Hle] int sceNetCtlGetNatInfo(uint info) { if (info != 0) { GuestMemory.Fill(info, 0, 16); GuestMemory.W32(info, 16); } return 0; }
    [Hle] int sceNetCtlAdhocGetState(Ptr<int> state) { state.TrySet(0); return 0; }
    [Hle] int sceNetCtlAdhocGetInAddr(uint addr) => ERR_NETCTL_NOT_CONNECTED;
    [Hle]
    int sceNetCtlAdhocRegisterCallback(uint func, uint arg, Ptr<int> cid)
    {
        int r = RegisterNetCallback(_adhocCallbacks, func, cid);
        if (r < 0) return r;
        lock (_inetCallbacks) _adhocCallbacks[r].Arg = arg;
        return 0;
    }

    [Hle] int sceNetCtlAdhocUnregisterCallback(int cid) { lock (_inetCallbacks) _adhocCallbacks.Remove(cid); return 0; }

    // dialogs that check the network report "cancelled"
    [Hle] int sceNetCheckDialogInit(uint param) => 0;
    [Hle] int sceNetCheckDialogGetStatus() => 2;
    [Hle] int sceNetCheckDialogGetResult(uint result) { if (result != 0) { GuestMemory.Fill(result, 0, 0x2C); GuestMemory.W32(result, unchecked((uint)0x80020400 | 1)); } return 0; }
    [Hle] int sceNetCheckDialogTerm() => 0;
    [Hle] int sceNetCheckDialogAbort() => 0;
    [Hle] int sceNetCtlAdhocDisconnect() => 0;

    // HTTP / SSL
    [Hle] int sceHttpInit(uint poolSize) => 0;
    [Hle] int sceHttpTerm() => 0;
    [Hle] int sceSslInit(uint poolSize) => 0;
    [Hle] int sceSslTerm() => 0;
    [Hle] int sceHttpCreateTemplate(string agent, int version, int autoProxy) => _nextId++;
    [Hle] int sceHttpDeleteTemplate(int id) => 0;
    [Hle] int sceHttpCreateConnection(int tmpl, string host, string scheme, uint port, int keepAlive) => _nextId++;
    [Hle] int sceHttpCreateConnectionWithURL(int tmpl, string url, int keepAlive) => _nextId++;
    [Hle] int sceHttpDeleteConnection(int id) => 0;
    [Hle] int sceHttpCreateRequest(int conn, int method, string path, ulong len) => _nextId++;
    [Hle] int sceHttpCreateRequestWithURL(int conn, int method, string url, ulong len) => _nextId++;
    [Hle] int sceHttpDeleteRequest(int id) => 0;
    [Hle] int sceHttpSendRequest(int req, uint data, uint size) => unchecked((int)0x80431063); // resolver failure
    [Hle] int sceHttpAbortRequest(int req) => 0;
    [Hle] int sceHttpGetStatusCode(int req, Ptr<int> code) => unchecked((int)0x80431005);
    [Hle] int sceHttpGetResponseContentLength(int req, uint len) => unchecked((int)0x80431005);
    [Hle] int sceHttpReadData(int req, uint data, uint size) => unchecked((int)0x80431005);
    [Hle] int sceHttpGetAllResponseHeaders(int req, uint header, uint size) => unchecked((int)0x80431005);
    [Hle] int sceHttpParseResponseHeader(uint header, uint len, string field, uint val, uint valLen) => unchecked((int)0x80432025);
    [Hle] int sceHttpAddRequestHeader(int id, string name, string value, uint mode) => 0;
    [Hle] int sceHttpSetRequestContentLength(int id, ulong len) => 0;
    [Hle] int sceHttpSetConnectTimeOut(int id, uint usec) => 0;
    [Hle] int sceHttpSetSendTimeOut(int id, uint usec) => 0;
    [Hle] int sceHttpSetRecvTimeOut(int id, uint usec) => 0;
    [Hle] int sceHttpSetResolveTimeOut(int id, uint usec) => 0;
    [Hle] int sceHttpSetResolveRetry(int id, int retry) => 0;
    [Hle] int sceHttpSetNonblock(int id, int enable) => 0;
    [Hle] int sceHttpsDisableOption(uint flags) => 0;
    [Hle] int sceHttpsLoadCert(int caNum, uint caList, uint cert, uint priv) => 0;
    [Hle] int sceHttpsSetSslCallback(int id, uint cb, uint arg) => 0;
    [Hle] int sceHttpsGetSslError(int id, Ptr<int> err, Ptr<uint> detail) { err.TrySet(0); detail.TrySet(0); return 0; }
    [Hle] int sceHttpGetMemoryPoolStats(uint stats) { if (stats != 0) GuestMemory.Fill(stats, 0, 16); return 0; }
    [Hle] int sceSslGetMemoryPoolStats(uint stats) { if (stats != 0) GuestMemory.Fill(stats, 0, 16); return 0; }

    // No TLS connection ever completes offline, so there are no certificate objects to inspect.
    const int SSL_ERROR_NOT_FOUND = unchecked((int)0x80435025);
    [Hle] uint sceSslGetSubjectName(uint cert) => 0;
    [Hle] uint sceSslGetIssuerName(uint cert) => 0;
    [Hle] int sceSslGetNameEntryCount(uint certName) => certName == 0 ? SSL_ERROR_NOT_FOUND : 0;
    [Hle] int sceSslGetNameEntryInfo(uint certName, int entry, uint oid, uint maxOid, uint value, uint maxValue, uint valueLen) => SSL_ERROR_NOT_FOUND;
    [Hle] int sceSslGetNotAfter(uint cert, uint tick) => SSL_ERROR_NOT_FOUND;
    [Hle] int sceSslGetNotBefore(uint cert, uint tick) => SSL_ERROR_NOT_FOUND;
    [Hle] int sceSslGetSerialNumber(uint cert, uint data, uint len) => SSL_ERROR_NOT_FOUND;
    [Hle] int sceHttpUriParse(uint outUri, string src, uint pool, uint require, uint prepare) => unchecked((int)0x80433060);
}

public sealed class Np : HleModule
{
    const int ERR_SIGNED_OUT = unchecked((int)0x80550006);
    int _nextId = 1;

    // PSN: the console is signed out (SCE_NP_SERVICE_STATE_SIGNED_OUT = 1; 0 would mean "unknown yet").
    const int NP_STATE_SIGNED_OUT = 1;
    readonly List<(uint Func, uint Arg, bool Notified)> _npCallbacks = new();

    [Hle] int sceNpInit(uint config, uint ctx) => 0;
    [Hle] int sceNpTerm() { lock (_npCallbacks) _npCallbacks.Clear(); return 0; }

    [Hle]
    int sceNpCheckCallback()
    {
        List<(uint Func, uint Arg, bool Notified)> pending;
        lock (_npCallbacks)
        {
            pending = _npCallbacks.Where(c => !c.Notified).ToList();
            for (int i = 0; i < _npCallbacks.Count; i++) _npCallbacks[i] = (_npCallbacks[i].Func, _npCallbacks[i].Arg, true);
        }
        foreach (var c in pending) Kernel.GuestThread.Current?.CallGuest(c.Func, NP_STATE_SIGNED_OUT, 0, c.Arg);
        return 0;
    }

    [Hle] int sceNpGetServiceState(Ptr<int> state) { if (state.Address == 0) return unchecked((int)0x80550003); state.Value = NP_STATE_SIGNED_OUT; return 0; }

    [Hle]
    int sceNpRegisterServiceStateCallback(uint cb, uint arg)
    {
        lock (_npCallbacks) _npCallbacks.Add((cb, arg, false));
        return 0;
    }

    [Hle] int sceNpUnregisterServiceStateCallback() { lock (_npCallbacks) _npCallbacks.Clear(); return 0; }
    [Hle] int sceNpManagerGetNpId(uint npId) => ERR_SIGNED_OUT;
    [Hle] int sceNpManagerGetContentRatingFlag(Ptr<int> isRestricted, Ptr<int> age) { isRestricted.TrySet(0); age.TrySet(18); return 0; }

    [Hle] int sceNpAuthInit() => 0;
    [Hle] int sceNpAuthTerm() => 0;
    [Hle] int sceNpAuthCreateStartRequest(uint param) => ERR_SIGNED_OUT;
    [Hle] int sceNpAuthDestroyRequest(int id) => 0;
    [Hle] int sceNpAuthGetTicket(int id, uint buf, uint len) => ERR_SIGNED_OUT;
    [Hle] int sceNpAuthGetEntitlementById(uint ticket, uint size, string id, uint ent) => ERR_SIGNED_OUT;

    [Hle] int sceNpBasicInit(uint opt) => 0;
    [Hle] int sceNpBasicTerm() => 0;
    [Hle] int sceNpBasicCheckCallback() => 0;
    [Hle] int sceNpBasicRegisterHandler(uint handlers, uint ctx, uint arg) => 0;
    [Hle] int sceNpBasicUnregisterHandler() => 0;
    [Hle] int sceNpBasicRegisterJoinGameAckHandler(uint h, uint arg) => 0;
    [Hle] int sceNpBasicUnregisterJoinGameAckHandler() => 0;
    [Hle] int sceNpBasicJoinGameAckResponseSend(uint p) => ERR_SIGNED_OUT;
    [Hle] int sceNpBasicSendInGameDataMessage(uint to, uint data, uint size) => ERR_SIGNED_OUT;
    [Hle] int sceNpBasicSetInGamePresence(uint p) => ERR_SIGNED_OUT;
    [Hle] int sceNpBasicGetFriendListEntryCount(Ptr<uint> count) { count.TrySet(0); return 0; }
    [Hle] int sceNpBasicGetFriendListEntries(uint start, uint entries, uint num, Ptr<uint> retrieved) { retrieved.TrySet(0); return 0; }
    [Hle] int sceNpBasicRecordPlaySessionLog(uint npId, uint type) => 0; // the "recently played with" list lives on PSN
    [Hle] int sceNpManagerGetChatRestrictionFlag(Ptr<int> flag) { flag.TrySet(0); return 0; }
    [Hle] int sceNpBasicGetBlockListEntryCount(Ptr<uint> count) { count.TrySet(0); return 0; }
    [Hle] int sceNpBasicGetBlockListEntries(uint start, uint entries, uint num, Ptr<uint> retrieved) { retrieved.TrySet(0); return 0; }

    [Hle] int sceNpLookupInit(int usesAsync, int threadPrio, int cpuAffinity, uint opt) => 0;
    [Hle] int sceNpLookupTerm() => 0;
    [Hle] int sceNpLookupCreateTitleCtx(uint commId, uint npId) => _nextId++;
    [Hle] int sceNpLookupDeleteTitleCtx(int id) => 0;
    [Hle] int sceNpLookupCreateRequest(int ctx) => _nextId++;
    [Hle] int sceNpLookupDeleteRequest(int id) => 0;
    [Hle] int sceNpLookupAbortRequest(int id) => 0;
    [Hle] int sceNpLookupUserProfileAsync(int id, uint target, uint user, uint about, uint lang, uint country, uint avatar, uint opt) => ERR_SIGNED_OUT;
    [Hle] int sceNpLookupPollAsync(int id, Ptr<int> result) { result.TrySet(ERR_SIGNED_OUT); return 0; }

    [Hle] int sceNpPartyInit(uint p) => 0;
    [Hle] int sceNpPartyTerm() => 0;
    [Hle] int sceNpPartyCheckCallback() => 0;
    [Hle] int sceNpPartyRegisterHandler(uint h, uint arg) => 0;
    [Hle] int sceNpPartyGetMembers(uint members) => unchecked((int)0x80553402);
    [Hle] int sceNpPartyGetMemberInfo(uint id, uint info) => unchecked((int)0x80553402);
    [Hle] int sceNpPartyGetMemberVoiceInfo(uint id, uint info) => unchecked((int)0x80553402);

    [Hle] int sceNpBasicRegisterInGameDataMessageHandler(uint handler, uint arg) => 0; // messages only arrive from PSN
    [Hle] int sceNpBasicUnregisterInGameDataMessageHandler() => 0;

    [Hle] int sceNpMessageDialogInit(uint param) => 0;
    [Hle] int sceNpMessageDialogGetStatus() => 2;
    [Hle] int sceNpMessageDialogGetResult(uint result) { if (result != 0) GuestMemory.W32(result, 0); return 0; }
    [Hle] int sceNpMessageDialogAbort() => 0;
    [Hle] int sceNpMessageDialogTerm() => 0;
}

/// <summary>Common dialogs: they complete immediately with the default choice.</summary>
public sealed class CommonDialog : HleModule
{
    const int StatusNone = 0, StatusRunning = 1, StatusFinished = 2;
    int _msg = StatusNone, _save = StatusNone;

    [Hle] int sceCommonDialogUpdate(uint param) => 0;
    [Hle] int sceCommonDialogSetConfigParam(uint param) => 0; // language / enter button for system dialogs (they complete immediately)
    [Hle] bool sceCommonDialogIsRunning() => false;

    [Hle]
    int sceMsgDialogInit(uint param)
    {
        if (param != 0)
        {
            uint mode = GuestMemory.R32(param + 0x18);
            uint userMsg = GuestMemory.R32(param + 0x1C);
            if (mode == 1 && userMsg != 0) Log.Info($"MsgDialog: {GuestMemory.ReadCString(GuestMemory.R32(userMsg + 4))}");
        }
        _msg = StatusFinished;
        return 0;
    }

    [Hle] int sceMsgDialogGetStatus() => _msg;
    [Hle] int sceMsgDialogTerm() { _msg = StatusNone; return 0; }
    [Hle] int sceMsgDialogClose() { _msg = StatusFinished; return 0; }
    [Hle] int sceMsgDialogAbort() { _msg = StatusFinished; return 0; }

    [Hle]
    int sceMsgDialogGetResult(uint result)
    {
        if (result == 0) return unchecked((int)0x80020401);
        GuestMemory.Fill(result, 0, 0x2C);
        GuestMemory.W32(result + 8, 1); // buttonId: YES / OK
        return 0;
    }

    // ---- SceSaveDataDialog: the dialog "runs" (status RUNNING) and its work completes at once (substatus FINISHED:
    // the user confirmed / the slot was shown); the application then calls Finish to close it (status FINISHED) and
    // Term. Progress-bar dialogs stay running until the application finishes them itself.
    const int SaveModeFixed = 1, SaveModeList = 2, SaveModeUserMsg = 3, SaveModeSystemMsg = 4, SaveModeErrorCode = 5, SaveModeProgressBar = 6;
    int _saveSub = StatusNone, _saveMode, _saveSlot = -1;
    uint _saveUserData, _saveSlotInfo;

    [Hle]
    int sceSaveDataDialogInit(uint param)
    {
        if (param == 0) return unchecked((int)0x80020401);
        if (_save != StatusNone) return unchecked((int)0x80020404); // BUSY
        // SceSaveDataDialogParam: sdkVersion, SceCommonDialogParam (76 bytes), mode, dispType, fixed, list, userMsg,
        // sysMsg, errorCode, progressBar, slotConf, flag, userdata
        _saveMode = (int)GuestMemory.R32(param + 0x50);
        _saveUserData = GuestMemory.R32(param + 0x78);
        uint sub = _saveMode switch { SaveModeFixed => GuestMemory.R32(param + 0x58), SaveModeList => GuestMemory.R32(param + 0x5C),
            SaveModeUserMsg => GuestMemory.R32(param + 0x60), SaveModeSystemMsg => GuestMemory.R32(param + 0x64), _ => 0 };
        _saveSlot = _saveMode switch
        {
            SaveModeFixed => (int)GuestMemory.R32(sub),                                  // targetSlot.id
            SaveModeList => GuestMemory.R32(sub) != 0 ? (int)GuestMemory.R32(GuestMemory.R32(sub)) : -1, // slotList[0].id
            SaveModeUserMsg or SaveModeSystemMsg => (int)GuestMemory.R32(sub + 8),      // targetSlot.id after type/value
            _ => -1,
        };
        _save = StatusRunning;
        _saveSub = _saveMode == SaveModeProgressBar ? StatusRunning : StatusFinished;
        Log.Info($"sceSaveDataDialogInit mode {_saveMode} dispType {GuestMemory.R32(param + 0x54)} slot {_saveSlot} sub {sub:X8} subType {(sub != 0 ? GuestMemory.R32(sub) : 0)} value {(sub != 0 ? GuestMemory.R32(sub + 4) : 0)} flag {GuestMemory.R32(param + 0x74)}");
        return 0;
    }

    [Hle] int sceSaveDataDialogContinue(uint param) { if (_save != StatusRunning) return unchecked((int)0x80020405); _saveSub = StatusFinished; return 0; }
    [Hle] int sceSaveDataDialogGetStatus() => _save;
    [Hle] int sceSaveDataDialogGetSubStatus() => _save == StatusRunning ? _saveSub : StatusNone;
    [Hle] int sceSaveDataDialogFinish(uint param) { if (_save != StatusRunning) return unchecked((int)0x80020405); _save = StatusFinished; return 0; }
    [Hle] int sceSaveDataDialogAbort() { if (_save != StatusRunning) return unchecked((int)0x80020405); _save = StatusFinished; return 0; }
    [Hle] int sceSaveDataDialogTerm() { _save = StatusNone; _saveSub = StatusNone; return 0; }
    [Hle] int sceSaveDataDialogProgressBarInc(int target, uint delta) => 0;
    [Hle] int sceSaveDataDialogProgressBarSetValue(int target, uint rate) => 0;
    [Hle] int sceSaveDataDialogSubClose() { _saveSub = StatusFinished; return 0; }

    [Hle]
    int sceSaveDataDialogGetResult(uint result)
    {
        if (result == 0) return unchecked((int)0x80020401);
        // SceSaveDataDialogResult: mode, result, buttonId, slotId, slotInfo*, userdata, reserved[32]
        GuestMemory.Fill(result, 0, 0x38);
        GuestMemory.W32(result, (uint)_saveMode);
        GuestMemory.W32(result + 8, 1); // SCE_SAVEDATA_DIALOG_BUTTON_ID_OK / YES
        GuestMemory.W32(result + 12, (uint)_saveSlot);
        if (_saveSlot >= 0)
        {
            // SceSaveDataDialogSlotInfo { isExist, slotParam*, reserved[32] }
            if (_saveSlotInfo == 0) _saveSlotInfo = Emu.Memory.Alloc(0x100, "savedata dialog slot info");
            var appUtil = Emu.Hle.Modules.OfType<AppUtil>().First();
            bool exists = appUtil.SlotExists((uint)_saveSlot);
            GuestMemory.Fill(_saveSlotInfo, 0, 0x100);
            GuestMemory.W32(_saveSlotInfo, exists ? 1u : 0u);
            if (exists) { appUtil.ReadSlotParam((uint)_saveSlot, _saveSlotInfo + 0x40); GuestMemory.W32(_saveSlotInfo + 4, _saveSlotInfo + 0x40); }
            GuestMemory.W32(result + 16, _saveSlotInfo);
        }
        GuestMemory.W32(result + 20, _saveUserData);
        return 0;
    }
}

/// <summary>Motion sensors (device at rest, flat), camera (absent), system gestures (none).</summary>
public sealed class Sensors : HleModule
{
    [Hle] int sceMotionStartSampling() => 0;
    [Hle] int sceMotionStopSampling() => 0;
    [Hle] int sceMotionReset() => 0;
    [Hle] int sceMotionMagnetometerOn() => 0;
    [Hle] int sceMotionMagnetometerOff() => 0;
    [Hle] int sceMotionGetMagnetometerState() => 0;
    [Hle] int sceMotionSetTiltCorrection(int e) => 0;
    [Hle] int sceMotionGetTiltCorrection() => 0;
    [Hle] int sceMotionSetDeadband(int e) => 0;
    [Hle] int sceMotionGetDeadband() => 0;
    [Hle] int sceMotionSetAngleThreshold(float angle) => 0;

    [Hle]
    int sceMotionGetState(uint state)
    {
        if (state == 0) return unchecked((int)0x80360001);
        GuestMemory.Fill(state, 0, 0xF8);
        GuestMemory.W32(state, (uint)Emu.Kernel.ProcessTimeUs);
        GuestMemory.Write(state + 12, -1.0f);         // acceleration z
        GuestMemory.Write(state + 52, 1.0f);          // quaternion w
        for (uint i = 0; i < 4; i++)
        {
            GuestMemory.Write(state + 56 + i * 20, 1.0f);   // rotation matrix identity
            GuestMemory.Write(state + 120 + i * 20, 1.0f);  // NED matrix identity
        }
        GuestMemory.Write(state + 188, 0.0f);
        GuestMemory.Write(state + 192, 0.0f);
        GuestMemory.Write(state + 196, 1.0f);         // basic orientation
        GuestMemory.Write(state + 200, Emu.Kernel.ProcessTimeUs);
        return 0;
    }

    const int ERR_CAMERA_NOT_FOUND = unchecked((int)0x802E000B);
    [Hle] int sceCameraOpen(int dev, uint info) => ERR_CAMERA_NOT_FOUND;
    [Hle] int sceCameraClose(int dev) => 0;
    [Hle] int sceCameraStart(int dev) => ERR_CAMERA_NOT_FOUND;
    [Hle] int sceCameraStop(int dev) => 0;
    [Hle] int sceCameraRead(int dev, uint read) => ERR_CAMERA_NOT_FOUND;
    [Hle] int sceCameraIsActive(int dev) => 0;
    [Hle] int sceCameraSetBrightness(int dev, int v) => 0;
    [Hle] int sceCameraSetContrast(int dev, int v) => 0;
    [Hle] int sceCameraSetEV(int dev, int v) => 0;
    [Hle] int sceCameraSetSaturation(int dev, int v) => 0;
    [Hle] int sceCameraSetSharpness(int dev, int v) => 0;
    [Hle] int sceCameraSetReverse(int dev, int v) => 0;
    [Hle] int sceCameraSetEffect(int dev, int v) => 0;
    [Hle] int sceCameraSetZoom(int dev, int v) => 0;
    [Hle] int sceCameraSetAntiFlicker(int dev, int v) => 0;
    [Hle] int sceCameraSetISO(int dev, int v) => 0;
    [Hle] int sceCameraSetWhiteBalance(int dev, int v) => 0;
    [Hle] int sceCameraSetBacklight(int dev, int v) => 0;
    [Hle] int sceCameraSetNightmode(int dev, int v) => 0;
    [Hle] int sceCameraSetExposureCeiling(int dev, int v) => 0;
    [Hle] int sceCameraSetAutoControlHold(int dev, int v) => 0;
    [Hle] int sceShutterSoundPlay(uint type) => 0;

}
