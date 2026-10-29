using VitaSharp.Core.Hle.Np;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// SceNpMatching2 (rooms and lobbies on PSN's matching servers). With the local PSN services the matching server is
/// a local one with nobody else on it: contexts start (the context callback reports START), the server and its single
/// world are available, room and lobby searches find nothing, and creating or joining a room fails cleanly with
/// SERVICE_UNAVAILABLE in the request callback (no other player could ever join). Every request is answered through
/// its callback, from the library's own thread (<see cref="HleCallbackThread"/>), so a game never waits forever.
/// Without local services (or signed out) starting a context fails at once like an unreachable server, as before.
/// <para>Callbacks (no sceNpMatching2GetEventData on the Vita: the response comes as a pointer):
/// context <c>void (ctxId, event, eventCause, errorCode, arg)</c>, request
/// <c>void (ctxId, reqId, event, errorCode, const void *data, arg)</c>. Event, cause and error values: the PS3 library
/// (RPCS3 sceNp2.h); Vita error codes 0x80550Cxx (client) / 0x80550Dxx (server) with the PS3 low byte (inferred
/// from the client codes already in use).</para>
/// </summary>
public sealed class NpMatching2 : HleModule
{
    const int ERR_NOT_INITIALIZED = unchecked((int)0x80550c01), ERR_INVALID_ARGUMENT = unchecked((int)0x80550c03),
        ERR_CONTEXT_ALREADY_STARTED = unchecked((int)0x80550c07), ERR_CONTEXT_NOT_STARTED = unchecked((int)0x80550c08),
        ERR_CONTEXT_NOT_FOUND = unchecked((int)0x80550c0b), ERR_ROOM_NOT_FOUND = unchecked((int)0x80550c35);
    internal const int SERVER_SERVICE_UNAVAILABLE = unchecked((int)0x80550d02), SERVER_NO_SUCH_ROOM = unchecked((int)0x80550d13);

    internal const int CONTEXT_EVENT_START_OVER = 0x6F01, CONTEXT_EVENT_START = 0x6F02, CONTEXT_EVENT_STOP = 0x6F03;
    internal const int EVENT_CAUSE_CONTEXT_ERROR = 10, EVENT_CAUSE_CONTEXT_ACTION = 11;

    // SCE_NP_MATCHING2_REQUEST_EVENT_*
    internal const int EV_GET_WORLD_INFO_LIST = 0x0002, EV_GET_ROOM_MEMBER_DATA_EXTERNAL_LIST = 0x0003,
        EV_SET_ROOM_DATA_EXTERNAL = 0x0004, EV_GET_ROOM_DATA_EXTERNAL_LIST = 0x0005, EV_GET_LOBBY_INFO_LIST = 0x0006,
        EV_SET_USER_INFO = 0x0007, EV_GET_USER_INFO_LIST = 0x0008, EV_CREATE_JOIN_ROOM = 0x0101, EV_JOIN_ROOM = 0x0102,
        EV_LEAVE_ROOM = 0x0103, EV_GRANT_ROOM_OWNER = 0x0104, EV_KICKOUT_ROOM_MEMBER = 0x0105, EV_SEARCH_ROOM = 0x0106,
        EV_SEND_ROOM_CHAT_MESSAGE = 0x0107, EV_SEND_ROOM_MESSAGE = 0x0108, EV_SET_ROOM_DATA_INTERNAL = 0x0109,
        EV_GET_ROOM_DATA_INTERNAL = 0x010a, EV_SET_ROOM_MEMBER_DATA_INTERNAL = 0x010b,
        EV_GET_ROOM_MEMBER_DATA_INTERNAL = 0x010c, EV_JOIN_LOBBY = 0x0201, EV_LEAVE_LOBBY = 0x0202,
        EV_SEND_LOBBY_CHAT_MESSAGE = 0x0203, EV_SIGNALING_GET_PING_INFO = 0x0e01;

    /// <summary>The one server and world of the local matching service.</summary>
    internal const ushort ServerId = 1;
    internal const uint WorldId = 1;

    sealed class Context
    {
        public bool Started;
        public uint CtxCb, CtxArg;
        public uint ReqCb, ReqArg; // default request option (sceNpMatching2SetDefaultRequestOptParam)
    }

    readonly object _lock = new();
    bool _initialized;
    int _threadPriority = 160;
    readonly Dictionary<ushort, Context> _contexts = new();
    ushort _nextContext = 1;
    uint _nextRequest = 1;
    uint _globalCtxCb, _globalCtxArg; // PS4-style registration without a context id

    readonly HleCallbackThread _callbacks = new("SceNpMatching2Callback");
    /// <summary>Tests: receives the callbacks instead of a guest thread.</summary>
    internal HleCallbackThread Callbacks => _callbacks;
    /// <summary>Tests: guest heap for response buffers (otherwise the emulator's).</summary>
    internal GuestMemory? MemoryOverride;
    GuestMemory Heap => MemoryOverride ?? Emu.Memory;

    static bool Local => PsnAccount.Current.UsesLocalServices;

    [Hle]
    int sceNpMatching2Init(uint poolSize, int threadPriority, int cpuAffinity, uint threadStackSize)
    {
        lock (_lock)
        {
            _initialized = true;
            if (threadPriority is >= 64 and <= 191) _threadPriority = threadPriority;
        }
        return 0;
    }

    [Hle]
    int sceNpMatching2Term()
    {
        lock (_lock) { _initialized = false; _contexts.Clear(); }
        _callbacks.Close();
        return 0;
    }

    [Hle]
    int sceNpMatching2CreateContext(uint npId, uint commId, uint passPhrase, Ptr<ushort> ctxId)
    {
        lock (_lock)
        {
            if (!_initialized) return ERR_NOT_INITIALIZED;
            if (ctxId.Address == 0) return ERR_INVALID_ARGUMENT;
            ushort id = _nextContext++;
            _contexts[id] = new Context { CtxCb = _globalCtxCb, CtxArg = _globalCtxArg };
            ctxId.Value = id;
            if (commId != 0) Log.Info($"NpMatching2: context {id} for {NpCommunity.ReadCommId(commId)} ({(Local ? "local matching server, no other players" : "offline")})");
        }
        return 0;
    }

    [Hle] int sceNpMatching2DestroyContext(ushort ctxId) { lock (_lock) return _contexts.Remove(ctxId) ? 0 : ERR_CONTEXT_NOT_FOUND; }

    bool Valid(ushort ctxId) { lock (_lock) return _contexts.ContainsKey(ctxId); }

    void PostContextEvent(ushort ctxId, Context c, int ev, int cause, int error) =>
        _callbacks.Post(MemoryOverride != null ? null : Emu, _threadPriority, c.CtxCb, [ctxId, (uint)ev, (uint)cause, unchecked((uint)error), c.CtxArg]);

    /// <summary>Contacts the matching server: locally it is always there, so the context callback reports START.</summary>
    [Hle]
    int sceNpMatching2ContextStart(ushort ctxId, ulong timeout)
    {
        Context? c;
        lock (_lock)
        {
            if (!_initialized) return ERR_NOT_INITIALIZED;
            if (!_contexts.TryGetValue(ctxId, out c)) return ERR_CONTEXT_NOT_FOUND;
            if (!Local) return PsnAccount.Current.ServerError;
            if (c.Started) return ERR_CONTEXT_ALREADY_STARTED;
            c.Started = true;
        }
        Log.Info($"NpMatching2: context {ctxId} started (callback 0x{c.CtxCb:X8})");
        PostContextEvent(ctxId, c, CONTEXT_EVENT_START, EVENT_CAUSE_CONTEXT_ACTION, 0);
        return 0;
    }

    [Hle]
    int sceNpMatching2ContextStop(ushort ctxId)
    {
        Context? c;
        lock (_lock)
        {
            if (!_contexts.TryGetValue(ctxId, out c)) return ERR_CONTEXT_NOT_FOUND;
            if (!c.Started) return Local ? ERR_CONTEXT_NOT_STARTED : 0;
            c.Started = false;
        }
        PostContextEvent(ctxId, c, CONTEXT_EVENT_STOP, EVENT_CAUSE_CONTEXT_ACTION, 0);
        return 0;
    }

    [Hle] int sceNpMatching2AbortContextStart(ushort ctxId) => Valid(ctxId) ? 0 : ERR_CONTEXT_NOT_FOUND;
    [Hle] int sceNpMatching2AbortRequest(ushort ctxId, uint reqId) => Valid(ctxId) ? 0 : ERR_CONTEXT_NOT_FOUND;

    /// <summary>(ctxId, cbFunc, cbFuncArg) like the PS3, or (cbFunc, cbFuncArg) like the PS4 (for every context): the
    /// first argument is a context id only when such a context exists.</summary>
    [Hle]
    int sceNpMatching2RegisterContextCallback(uint a0, uint a1, uint a2)
    {
        lock (_lock)
        {
            if (a0 <= 0xFFFF && _contexts.TryGetValue((ushort)a0, out var c)) { c.CtxCb = a1; c.CtxArg = a2; return 0; }
            if (a0 < 0x10000 && a0 != 0) return ERR_CONTEXT_NOT_FOUND;
            _globalCtxCb = a0; _globalCtxArg = a1;
            foreach (var x in _contexts.Values) { x.CtxCb = a0; x.CtxArg = a1; }
            return 0;
        }
    }

    // room/lobby/signaling events only come from other players: registered, never raised
    [Hle] int sceNpMatching2RegisterLobbyEventCallback(ushort ctxId, uint cb, uint arg) => Valid(ctxId) ? 0 : ERR_CONTEXT_NOT_FOUND;
    [Hle] int sceNpMatching2RegisterLobbyMessageCallback(ushort ctxId, uint cb, uint arg) => Valid(ctxId) ? 0 : ERR_CONTEXT_NOT_FOUND;
    [Hle] int sceNpMatching2RegisterRoomEventCallback(ushort ctxId, uint cb, uint arg) => Valid(ctxId) ? 0 : ERR_CONTEXT_NOT_FOUND;
    [Hle] int sceNpMatching2RegisterRoomMessageCallback(ushort ctxId, uint cb, uint arg) => Valid(ctxId) ? 0 : ERR_CONTEXT_NOT_FOUND;
    [Hle] int sceNpMatching2RegisterSignalingCallback(ushort ctxId, uint cb, uint arg) => Valid(ctxId) ? 0 : ERR_CONTEXT_NOT_FOUND;

    /// <summary>SceNpMatching2RequestOptParam (16): cbFunc +0, cbFuncArg +4, timeout +8, appReqId +0xC.</summary>
    [Hle]
    int sceNpMatching2SetDefaultRequestOptParam(ushort ctxId, uint opt)
    {
        lock (_lock)
        {
            if (!_contexts.TryGetValue(ctxId, out var c)) return ERR_CONTEXT_NOT_FOUND;
            if (opt == 0) return ERR_INVALID_ARGUMENT;
            c.ReqCb = GuestMemory.R32(opt);
            c.ReqArg = GuestMemory.R32(opt + 4);
            return 0;
        }
    }

    [Hle] int sceNpMatching2SetSignalingOptParam(ushort ctxId, uint roomId, uint opt) => Valid(ctxId) ? 0 : ERR_CONTEXT_NOT_FOUND;

    [Hle] int sceNpMatching2GetMemoryInfo(uint info) { if (info != 0) GuestMemory.Fill(info, 0, 12); return 0; }

    /// <summary>SceNpMatching2Server (4): serverId u16, status u8 (AVAILABLE 1) — the local server once started.</summary>
    [Hle]
    int sceNpMatching2GetServerLocal(ushort ctxId, uint server)
    {
        lock (_lock)
        {
            if (!_contexts.TryGetValue(ctxId, out var c)) return ERR_CONTEXT_NOT_FOUND;
            if (!Local) return PsnAccount.Current.ServerError;
            if (!c.Started) return ERR_CONTEXT_NOT_STARTED;
        }
        if (server == 0) return ERR_INVALID_ARGUMENT;
        GuestMemory.W32(server, 0);
        GuestMemory.Write(server, ServerId);
        GuestMemory.Write(server + 2, (byte)1);
        return 0;
    }

    /// <summary>
    /// A request to the matching server: writes the request id and answers through the request callback (from the
    /// option, else the context default) with <paramref name="error"/> and, on success, a response built by
    /// <paramref name="response"/> in a temporary guest buffer (freed after the callback).
    /// </summary>
    int Request(ushort ctxId, uint opt, uint assignedReqId, int ev, int error, uint responseSize = 0, Action<uint>? response = null)
    {
        Context? c;
        uint id;
        lock (_lock)
        {
            if (!_initialized) return ERR_NOT_INITIALIZED;
            if (!_contexts.TryGetValue(ctxId, out c)) return ERR_CONTEXT_NOT_FOUND;
            if (!Local) return PsnAccount.Current.ServerError;
            if (!c.Started) return ERR_CONTEXT_NOT_STARTED;
            id = _nextRequest++;
        }
        if (assignedReqId != 0) GuestMemory.W32(assignedReqId, id);
        uint cb = opt != 0 ? GuestMemory.R32(opt) : c.ReqCb, arg = opt != 0 ? GuestMemory.R32(opt + 4) : c.ReqArg;
        uint data = 0;
        if (error == 0 && responseSize > 0 && cb != 0)
        {
            data = Heap.Alloc(responseSize, "SceNpMatching2Response", MemoryRegion.User, 16);
            GuestMemory.Fill(data, 0, responseSize);
            response?.Invoke(data);
        }
        Log.Debug($"NpMatching2: request {id} event 0x{ev:X4} -> 0x{error:X8}");
        uint buf = data;
        _callbacks.Post(MemoryOverride != null ? null : Emu, _threadPriority, cb, [ctxId, id, (uint)ev, unchecked((uint)error), data, arg],
            buf != 0 ? () => Heap.Free(buf) : null);
        return 0;
    }

    /// <summary>SceNpMatching2GetWorldInfoListResponse {world*, worldNum} + one SceNpMatching2World (60): worldId +0,
    /// numOfLobby +4, maxNumOfTotalLobbyMember +8, cur members +12, curNumOfRoom +16, room members +20.</summary>
    [Hle]
    int sceNpMatching2GetWorldInfoList(ushort ctxId, uint req, uint opt, uint reqId) =>
        req == 0 ? ERR_INVALID_ARGUMENT : Request(ctxId, opt, reqId, EV_GET_WORLD_INFO_LIST, 0, 8 + 60, p =>
        {
            GuestMemory.W32(p, p + 8);
            GuestMemory.W32(p + 4, 1);
            GuestMemory.W32(p + 8, WorldId);
            GuestMemory.W32(p + 8 + 8, 64);
        });

    /// <summary>SceNpMatching2SearchRoomResponse: range {startIndex, total, size} + roomDataExternal* = no rooms.</summary>
    [Hle]
    int sceNpMatching2SearchRoom(ushort ctxId, uint req, uint opt, uint reqId) =>
        req == 0 ? ERR_INVALID_ARGUMENT : Request(ctxId, opt, reqId, EV_SEARCH_ROOM, 0, 16, p => GuestMemory.W32(p, 1));

    /// <summary>SceNpMatching2GetLobbyInfoListResponse: range + lobbyDataExternal* = no lobbies.</summary>
    [Hle]
    int sceNpMatching2GetLobbyInfoList(ushort ctxId, uint req, uint opt, uint reqId) =>
        req == 0 ? ERR_INVALID_ARGUMENT : Request(ctxId, opt, reqId, EV_GET_LOBBY_INFO_LIST, 0, 16, p => GuestMemory.W32(p, 1));

    // rooms need other players: creating one is refused by the server, the rest finds no room
    [Hle] int sceNpMatching2CreateJoinRoom(ushort ctxId, uint req, uint opt, uint reqId) => req == 0 ? ERR_INVALID_ARGUMENT : Request(ctxId, opt, reqId, EV_CREATE_JOIN_ROOM, SERVER_SERVICE_UNAVAILABLE);
    [Hle] int sceNpMatching2JoinRoom(ushort ctxId, uint req, uint opt, uint reqId) => req == 0 ? ERR_INVALID_ARGUMENT : Request(ctxId, opt, reqId, EV_JOIN_ROOM, SERVER_NO_SUCH_ROOM);
    [Hle] int sceNpMatching2LeaveRoom(ushort ctxId, uint req, uint opt, uint reqId) => Request(ctxId, opt, reqId, EV_LEAVE_ROOM, SERVER_NO_SUCH_ROOM);
    [Hle] int sceNpMatching2JoinLobby(ushort ctxId, uint req, uint opt, uint reqId) => Request(ctxId, opt, reqId, EV_JOIN_LOBBY, SERVER_SERVICE_UNAVAILABLE);
    [Hle] int sceNpMatching2LeaveLobby(ushort ctxId, uint req, uint opt, uint reqId) => Request(ctxId, opt, reqId, EV_LEAVE_LOBBY, SERVER_SERVICE_UNAVAILABLE);
    [Hle] int sceNpMatching2GrantRoomOwner(ushort ctxId, uint req, uint opt, uint reqId) => Request(ctxId, opt, reqId, EV_GRANT_ROOM_OWNER, SERVER_NO_SUCH_ROOM);
    [Hle] int sceNpMatching2KickoutRoomMember(ushort ctxId, uint req, uint opt, uint reqId) => Request(ctxId, opt, reqId, EV_KICKOUT_ROOM_MEMBER, SERVER_NO_SUCH_ROOM);
    [Hle] int sceNpMatching2GetRoomDataExternalList(ushort ctxId, uint req, uint opt, uint reqId) => Request(ctxId, opt, reqId, EV_GET_ROOM_DATA_EXTERNAL_LIST, SERVER_NO_SUCH_ROOM);
    [Hle] int sceNpMatching2SetRoomDataExternal(ushort ctxId, uint req, uint opt, uint reqId) => Request(ctxId, opt, reqId, EV_SET_ROOM_DATA_EXTERNAL, SERVER_NO_SUCH_ROOM);
    [Hle] int sceNpMatching2GetRoomDataInternal(ushort ctxId, uint req, uint opt, uint reqId) => Request(ctxId, opt, reqId, EV_GET_ROOM_DATA_INTERNAL, SERVER_NO_SUCH_ROOM);
    [Hle] int sceNpMatching2SetRoomDataInternal(ushort ctxId, uint req, uint opt, uint reqId) => Request(ctxId, opt, reqId, EV_SET_ROOM_DATA_INTERNAL, SERVER_NO_SUCH_ROOM);
    [Hle] int sceNpMatching2GetRoomMemberDataExternalList(ushort ctxId, uint req, uint opt, uint reqId) => Request(ctxId, opt, reqId, EV_GET_ROOM_MEMBER_DATA_EXTERNAL_LIST, SERVER_NO_SUCH_ROOM);
    [Hle] int sceNpMatching2GetRoomMemberDataInternal(ushort ctxId, uint req, uint opt, uint reqId) => Request(ctxId, opt, reqId, EV_GET_ROOM_MEMBER_DATA_INTERNAL, SERVER_NO_SUCH_ROOM);
    [Hle] int sceNpMatching2SetRoomMemberDataInternal(ushort ctxId, uint req, uint opt, uint reqId) => Request(ctxId, opt, reqId, EV_SET_ROOM_MEMBER_DATA_INTERNAL, SERVER_NO_SUCH_ROOM);
    // the user's own matching attributes are accepted; other users' are not on the server
    [Hle] int sceNpMatching2SetUserInfo(ushort ctxId, uint req, uint opt, uint reqId) => Request(ctxId, opt, reqId, EV_SET_USER_INFO, 0);
    [Hle] int sceNpMatching2GetUserInfoList(ushort ctxId, uint req, uint opt, uint reqId) => Request(ctxId, opt, reqId, EV_GET_USER_INFO_LIST, SERVER_SERVICE_UNAVAILABLE);
    [Hle] int sceNpMatching2SendRoomMessage(ushort ctxId, uint req, uint opt, uint reqId) => Request(ctxId, opt, reqId, EV_SEND_ROOM_MESSAGE, SERVER_NO_SUCH_ROOM);
    [Hle] int sceNpMatching2SendRoomChatMessage(ushort ctxId, uint req, uint opt, uint reqId) => Request(ctxId, opt, reqId, EV_SEND_ROOM_CHAT_MESSAGE, SERVER_NO_SUCH_ROOM);
    [Hle] int sceNpMatching2SendLobbyChatMessage(ushort ctxId, uint req, uint opt, uint reqId) => Request(ctxId, opt, reqId, EV_SEND_LOBBY_CHAT_MESSAGE, SERVER_SERVICE_UNAVAILABLE);
    [Hle] int sceNpMatching2SignalingGetPingInfo(ushort ctxId, uint req, uint opt, uint reqId) => Request(ctxId, opt, reqId, EV_SIGNALING_GET_PING_INFO, SERVER_NO_SUCH_ROOM);

    // local queries about the joined room: the user is never in one
    int NotInRoom(ushort ctxId) => !Valid(ctxId) ? ERR_CONTEXT_NOT_FOUND : Local ? ERR_ROOM_NOT_FOUND : PsnAccount.Current.ServerError;
    [Hle] int sceNpMatching2GetRoomMemberIdListLocal(ushort ctxId, ulong roomId, int sort, uint list, uint count) => NotInRoom(ctxId);
    [Hle] int sceNpMatching2GetRoomPasswordLocal(ushort ctxId, ulong roomId, uint withPw, uint pw) => NotInRoom(ctxId);
    [Hle] int sceNpMatching2GetSignalingOptParamLocal(ushort ctxId, ulong roomId, uint opt) => NotInRoom(ctxId);

    // Signaling: no peers are ever connected.
    [Hle] int sceNpMatching2SignalingGetConnectionStatus(ushort ctxId, ulong roomId, ushort memberId, Ptr<int> status, uint addr, uint port)
    {
        status.TrySet(0); // SCE_NP_MATCHING2_SIGNALING_CONN_STATUS_INACTIVE
        return NotInRoom(ctxId);
    }
    [Hle] int sceNpMatching2SignalingGetConnectionInfo(ushort ctxId, ulong roomId, ushort memberId, int code, uint info) => NotInRoom(ctxId);
    [Hle] int sceNpMatching2SignalingGetLocalNetInfo(uint info) => PsnAccount.Current.ServerError;
    [Hle] int sceNpMatching2SignalingGetPeerNetInfo(ushort ctxId, ulong roomId, ushort memberId, uint reqId) => NotInRoom(ctxId);
    [Hle] int sceNpMatching2SignalingCancelPeerNetInfo(ushort ctxId, uint reqId) => NotInRoom(ctxId);
    [Hle] int sceNpMatching2SignalingGetPeerNetInfoResult(ushort ctxId, uint reqId, uint info) => NotInRoom(ctxId);
}

/// <summary>SceRudp (reliable UDP over the PSN signaling layer): contexts can be created but never reach a peer.</summary>
public sealed class Rudp : HleModule
{
    // Values in use since the first implementation; the PS3 cellRudp table disagrees for several of them (see
    // docs/hle/E1-net.md, to be confirmed). Codes added later follow the PS3 table.
    const int ERR_NOT_INITIALIZED = unchecked((int)0x80770001), ERR_INVALID_CONTEXT_ID = unchecked((int)0x80770004),
        ERR_INVALID_ARGUMENT = unchecked((int)0x80770005), ERR_WOULDBLOCK = unchecked((int)0x8077000d),
        ERR_CONN_RESET = unchecked((int)0x80770019), ERR_NOT_ACCEPTABLE = unchecked((int)0x80770011),
        ERR_NOT_BOUND = unchecked((int)0x80770013), ERR_ALREADY_EXISTS = unchecked((int)0x8077001E),
        ERR_INVALID_POLL_ID = unchecked((int)0x8077001F);

    sealed class Context
    {
        public int Sock = -1;
        public ushort Vport, RemoteVport;
        public byte MuxMode;
        public bool Bound;
        public byte[]? Remote;
    }

    sealed class Poll
    {
        public int Id, MaxEntries;
        public readonly Dictionary<int, ushort> Entries = new();
        public int CancelGen;
        public bool Destroyed;
    }

    readonly object _lock = new();
    bool _initialized, _internalIoThread;
    int _nextContext = 1, _nextPoll = 1;
    ushort _mss = 1280;
    readonly Dictionary<int, Context> _contexts = new();
    readonly Dictionary<int, Poll> _polls = new();
    int _blockedPollWaits;

    /// <summary>Threads blocked in sceRudpPollWait. sceRudpPollCancel only wakes the waits already in progress, so
    /// callers that cancel another thread's wait (tests) need to know when it really started.</summary>
    internal int BlockedPollWaits { get { lock (_lock) return _blockedPollWaits; } }

    [Hle] int sceRudpInit(uint pool, uint poolSize) { _initialized = true; return 0; }

    [Hle]
    int sceRudpEnd()
    {
        lock (_lock)
        {
            _initialized = false;
            _internalIoThread = false;
            _contexts.Clear();
            foreach (var p in _polls.Values) p.Destroyed = true;
            _polls.Clear();
            Monitor.PulseAll(_lock);
        }
        return 0;
    }

    [Hle] int sceRudpEnableInternalIOThread(uint stackSize, uint priority) { if (!_initialized) return ERR_NOT_INITIALIZED; _internalIoThread = true; return 0; }

    /// <summary>Inferred: (stackSize, priority, cpuAffinityMask). No traffic offline, so no thread is created.</summary>
    [Hle] int sceRudpEnableInternalIOThread2(uint stackSize, uint priority, int cpuAffinityMask) => sceRudpEnableInternalIOThread(stackSize, priority);

    internal bool InternalIoThread => _internalIoThread;

    [Hle] int sceRudpSetEventHandler(uint handler, uint arg) => _initialized ? 0 : ERR_NOT_INITIALIZED;

    [Hle]
    int sceRudpSetMaxSegmentSize(ushort mss)
    {
        if (!_initialized) return ERR_NOT_INITIALIZED;
        if (mss == 0) return ERR_INVALID_ARGUMENT;
        _mss = mss;
        return 0;
    }

    [Hle] int sceRudpGetMaxSegmentSize(Ptr<ushort> mss) { mss.TrySet(_mss); return 0; }

    [Hle]
    int sceRudpCreateContext(uint handler, uint arg, Ptr<int> ctxId)
    {
        if (!_initialized) return ERR_NOT_INITIALIZED;
        if (ctxId.Address == 0) return ERR_INVALID_ARGUMENT;
        lock (_lock)
        {
            int id = _nextContext++;
            _contexts[id] = new Context();
            ctxId.Value = id;
        }
        return 0;
    }

    bool Valid(int ctx) { lock (_lock) return _contexts.ContainsKey(ctx); }

    [Hle]
    int sceRudpTerminate(int ctx)
    {
        lock (_lock)
        {
            if (!_contexts.Remove(ctx)) return ERR_INVALID_CONTEXT_ID;
            foreach (var p in _polls.Values) p.Entries.Remove(ctx);
            return 0;
        }
    }

    [Hle]
    int sceRudpBind(int ctx, int sock, ushort vport, byte muxMode)
    {
        lock (_lock)
        {
            if (!_contexts.TryGetValue(ctx, out var c)) return ERR_INVALID_CONTEXT_ID;
            c.Sock = sock; c.Vport = vport; c.MuxMode = muxMode; c.Bound = true;
            return 0;
        }
    }

    int Connect(int ctx, uint addr, uint addrLen, ushort vport)
    {
        lock (_lock)
        {
            if (!_contexts.TryGetValue(ctx, out var c)) return ERR_INVALID_CONTEXT_ID;
            if (addr != 0) c.Remote = GuestMemory.Span(addr, (int)Math.Clamp(addrLen, 0u, 16u)).ToArray();
            c.RemoteVport = vport;
            return ERR_CONN_RESET;
        }
    }

    [Hle] int sceRudpInitiate(int ctx, uint addr, uint addrLen, ushort vport) => Connect(ctx, addr, addrLen, vport);
    [Hle] int sceRudpActivate(int ctx, uint addr, uint addrLen) => Connect(ctx, addr, addrLen, 0);
    [Hle] int sceRudpRead(int ctx, uint data, uint len, byte flags, uint info) => Valid(ctx) ? ERR_WOULDBLOCK : ERR_INVALID_CONTEXT_ID;
    [Hle] int sceRudpWrite(int ctx, uint data, uint len, byte flags) => Valid(ctx) ? ERR_NOT_ACCEPTABLE : ERR_INVALID_CONTEXT_ID;
    [Hle] int sceRudpFlush(int ctx) => Valid(ctx) ? 0 : ERR_INVALID_CONTEXT_ID;
    [Hle] int sceRudpGetSizeReadable(int ctx) => Valid(ctx) ? 0 : ERR_INVALID_CONTEXT_ID;
    [Hle] int sceRudpGetSizeWritable(int ctx) => Valid(ctx) ? 0 : ERR_INVALID_CONTEXT_ID;
    [Hle] int sceRudpGetNumberOfPacketsToRead(int ctx) => !_initialized ? ERR_NOT_INITIALIZED : Valid(ctx) ? 0 : ERR_INVALID_CONTEXT_ID;
    [Hle] int sceRudpSetOption(int ctx, int option, uint value, uint len) => Valid(ctx) ? 0 : ERR_INVALID_CONTEXT_ID;

    /// <summary>Inferred (PS3 cellRudpGetLocalInfo): (ctx, int *soc, SceNetSockaddr *addr, SceNetSocklen_t *addrLen,
    /// u16 *vport, u8 *muxMode). Null pointers are skipped.</summary>
    [Hle]
    int sceRudpGetLocalInfo(int ctx, uint soc, uint addr, uint addrLen, uint vport, uint muxMode)
    {
        lock (_lock)
        {
            if (!_contexts.TryGetValue(ctx, out var c)) return ERR_INVALID_CONTEXT_ID;
            if (!c.Bound) return ERR_NOT_BOUND;
            if (soc != 0) GuestMemory.W32(soc, (uint)c.Sock);
            if (addr != 0)
            {
                GuestMemory.Fill(addr, 0, 16);
                GuestMemory.Write<byte>(addr, 16); GuestMemory.Write<byte>(addr + 1, 2); // AF_INET, any address
                if (addrLen != 0) GuestMemory.W32(addrLen, 16);
            }
            if (vport != 0) GuestMemory.Write(vport, c.Vport);
            if (muxMode != 0) GuestMemory.Write(muxMode, c.MuxMode);
            return 0;
        }
    }

    /// <summary>Inferred (PS3): (ctx, SceNetSockaddr *addr, SceNetSocklen_t *addrLen, u16 *vport). Returns the peer
    /// passed to Initiate/Activate (zeros when none).</summary>
    [Hle]
    int sceRudpGetRemoteInfo(int ctx, uint addr, uint addrLen, uint vport)
    {
        lock (_lock)
        {
            if (!_contexts.TryGetValue(ctx, out var c)) return ERR_INVALID_CONTEXT_ID;
            if (addr != 0)
            {
                GuestMemory.Fill(addr, 0, 16);
                if (c.Remote != null) c.Remote.CopyTo(GuestMemory.Span(addr, c.Remote.Length));
                if (addrLen != 0) GuestMemory.W32(addrLen, 16);
            }
            if (vport != 0) GuestMemory.Write(vport, c.RemoteVport);
            return 0;
        }
    }

    [Hle]
    int sceRudpGetOption(int ctx, int option, uint value, uint len)
    {
        if (!Valid(ctx)) return ERR_INVALID_CONTEXT_ID;
        if (value != 0 && len >= 4) GuestMemory.Fill(value, 0, Math.Min(len, 64u));
        return 0;
    }

    [Hle]
    int sceRudpGetStatus(uint status, uint len)
    {
        if (status != 0) GuestMemory.Fill(status, 0, Math.Min(len, 0x100u));
        return 0;
    }

    [Hle]
    int sceRudpGetContextStatus(int ctx, uint status, uint len)
    {
        if (!Valid(ctx)) return ERR_INVALID_CONTEXT_ID;
        if (status != 0) GuestMemory.Fill(status, 0, Math.Min(len, 0x100u));
        return 0;
    }

    [Hle] int sceRudpProcessEvents(uint timeout) => _initialized ? 0 : ERR_NOT_INITIALIZED;
    [Hle] int sceRudpNetReceived(int sock, uint data, uint len, uint from, uint fromLen) => _initialized ? 0 : ERR_NOT_INITIALIZED;

    // ------------------------------------------------------------------ poll
    // No context ever connects offline, so no event becomes ready: PollWait ends on timeout, cancel or destroy.

    [Hle]
    int sceRudpPollCreate(uint size)
    {
        if (!_initialized) return ERR_NOT_INITIALIZED;
        lock (_lock)
        {
            var p = new Poll { Id = _nextPoll++, MaxEntries = (int)Math.Min(size, 0x10000u) };
            _polls[p.Id] = p;
            return p.Id;
        }
    }

    [Hle]
    int sceRudpPollDestroy(int pollId)
    {
        lock (_lock)
        {
            if (!_polls.Remove(pollId, out var p)) return ERR_INVALID_POLL_ID;
            p.Destroyed = true;
            Monitor.PulseAll(_lock);
            return 0;
        }
    }

    /// <summary>op: ADD 1, MODIFY 2, DELETE 3 (inferred from the PS3 API).</summary>
    [Hle]
    int sceRudpPollControl(int pollId, int op, int ctxId, ushort events)
    {
        lock (_lock)
        {
            if (!_polls.TryGetValue(pollId, out var p)) return ERR_INVALID_POLL_ID;
            if (!_contexts.ContainsKey(ctxId)) return ERR_INVALID_CONTEXT_ID;
            switch (op)
            {
                case 1:
                    if (p.Entries.ContainsKey(ctxId)) return ERR_ALREADY_EXISTS;
                    if (p.MaxEntries > 0 && p.Entries.Count >= p.MaxEntries) return ERR_INVALID_ARGUMENT;
                    p.Entries[ctxId] = events;
                    return 0;
                case 2:
                    if (!p.Entries.ContainsKey(ctxId)) return ERR_INVALID_ARGUMENT;
                    p.Entries[ctxId] = events;
                    return 0;
                case 3:
                    return p.Entries.Remove(ctxId) ? 0 : ERR_INVALID_ARGUMENT;
                default:
                    return ERR_INVALID_ARGUMENT;
            }
        }
    }

    bool _pollWaitLogged;

    /// <summary>int sceRudpPollWait(int pollId, SceRudpPollEvent *events, SceSize eventLen, SceRudpUsec timeout)
    /// (inferred, 64-bit timeout in microseconds): 0 = poll, values with the top bit set = forever. Offline no event is
    /// ever ready, so it returns 0 after the timeout, or when the poll is cancelled/destroyed.</summary>
    [Hle]
    int sceRudpPollWait(int pollId, uint events, uint eventLen, ulong timeout)
    {
        if (!_pollWaitLogged) { _pollWaitLogged = true; Util.Log.Info($"sceRudpPollWait(poll={pollId}, len={eventLen}, timeout=0x{timeout:X})"); }
        if (!_initialized) return ERR_NOT_INITIALIZED;
        lock (_lock)
        {
            if (!_polls.TryGetValue(pollId, out var p)) return ERR_INVALID_POLL_ID;
            if (events == 0 && eventLen > 0) return ERR_INVALID_ARGUMENT;
            if (timeout == 0) return 0;
            int gen = p.CancelGen;
            long? limit = timeout >= 1UL << 63 ? null : (long)timeout;
            _blockedPollWaits++;
            try { Kernel.Wait.Until(_lock, () => p.Destroyed || p.CancelGen != gen, limit, what: "rudp poll"); }
            finally { _blockedPollWaits--; }
            return 0;
        }
    }

    [Hle]
    int sceRudpPollCancel(int pollId)
    {
        lock (_lock)
        {
            if (!_polls.TryGetValue(pollId, out var p)) return ERR_INVALID_POLL_ID;
            p.CancelGen++;
            Monitor.PulseAll(_lock);
            return 0;
        }
    }
}
