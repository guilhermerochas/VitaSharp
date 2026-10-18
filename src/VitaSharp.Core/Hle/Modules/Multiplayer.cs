using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// Offline multiplayer layer. The emulated console is on a LAN but signed out of PSN (see Online.cs), so
/// matchmaking (SceNpMatching2) and reliable UDP peers (SceRudp) behave as they do on a console without
/// PSN access: libraries and contexts initialise, every request that would need a server or a peer fails.
/// </summary>
public sealed class NpMatching2 : HleModule
{
    // NP error family 0x8055xxxx; requests fail with the NP "signed out" error, as on a console without PSN.
    const int ERR_NOT_INITIALIZED = unchecked((int)0x80550c01), ERR_INVALID_ARGUMENT = unchecked((int)0x80550c03),
        ERR_CONTEXT_NOT_FOUND = unchecked((int)0x80550c0b), ERR_SIGNED_OUT = unchecked((int)0x80550006);

    bool _initialized;
    readonly HashSet<ushort> _contexts = new();
    ushort _nextContext = 1;

    [Hle] int sceNpMatching2Init(uint poolSize, int threadPriority, int cpuAffinity, uint threadStackSize) { _initialized = true; return 0; }
    [Hle] int sceNpMatching2Term() { _initialized = false; lock (_contexts) _contexts.Clear(); return 0; }

    [Hle]
    int sceNpMatching2CreateContext(uint npId, uint commId, uint passPhrase, Ptr<ushort> ctxId)
    {
        if (!_initialized) return ERR_NOT_INITIALIZED;
        if (ctxId.Address == 0) return ERR_INVALID_ARGUMENT;
        lock (_contexts)
        {
            ushort id = _nextContext++;
            _contexts.Add(id);
            ctxId.Value = id;
        }
        return 0;
    }

    [Hle] int sceNpMatching2DestroyContext(ushort ctxId) { lock (_contexts) return _contexts.Remove(ctxId) ? 0 : ERR_CONTEXT_NOT_FOUND; }

    bool Valid(ushort ctxId) { lock (_contexts) return _contexts.Contains(ctxId); }

    // Starting a context contacts the matching server: refused because the user is not signed in to PSN.
    [Hle] int sceNpMatching2ContextStart(ushort ctxId, ulong timeout) => Valid(ctxId) ? ERR_SIGNED_OUT : ERR_CONTEXT_NOT_FOUND;
    [Hle] int sceNpMatching2ContextStop(ushort ctxId) => Valid(ctxId) ? 0 : ERR_CONTEXT_NOT_FOUND;
    [Hle] int sceNpMatching2AbortContextStart(ushort ctxId) => Valid(ctxId) ? 0 : ERR_CONTEXT_NOT_FOUND;
    [Hle] int sceNpMatching2AbortRequest(ushort ctxId, uint reqId) => Valid(ctxId) ? 0 : ERR_CONTEXT_NOT_FOUND;

    [Hle] int sceNpMatching2RegisterContextCallback(ushort ctxId, uint cb, uint arg) => Valid(ctxId) ? 0 : ERR_CONTEXT_NOT_FOUND;
    [Hle] int sceNpMatching2RegisterLobbyEventCallback(ushort ctxId, uint cb, uint arg) => Valid(ctxId) ? 0 : ERR_CONTEXT_NOT_FOUND;
    [Hle] int sceNpMatching2RegisterLobbyMessageCallback(ushort ctxId, uint cb, uint arg) => Valid(ctxId) ? 0 : ERR_CONTEXT_NOT_FOUND;
    [Hle] int sceNpMatching2RegisterRoomEventCallback(ushort ctxId, uint cb, uint arg) => Valid(ctxId) ? 0 : ERR_CONTEXT_NOT_FOUND;
    [Hle] int sceNpMatching2RegisterRoomMessageCallback(ushort ctxId, uint cb, uint arg) => Valid(ctxId) ? 0 : ERR_CONTEXT_NOT_FOUND;
    [Hle] int sceNpMatching2RegisterSignalingCallback(ushort ctxId, uint cb, uint arg) => Valid(ctxId) ? 0 : ERR_CONTEXT_NOT_FOUND;
    [Hle] int sceNpMatching2SetDefaultRequestOptParam(ushort ctxId, uint opt) => Valid(ctxId) ? 0 : ERR_CONTEXT_NOT_FOUND;
    [Hle] int sceNpMatching2SetSignalingOptParam(ushort ctxId, uint roomId, uint opt) => Valid(ctxId) ? 0 : ERR_CONTEXT_NOT_FOUND;

    [Hle] int sceNpMatching2GetMemoryInfo(uint info) { if (info != 0) GuestMemory.Fill(info, 0, 12); return 0; }

    // Requests that would go to the matching server.
    int Request(ushort ctxId) => Valid(ctxId) ? ERR_SIGNED_OUT : ERR_CONTEXT_NOT_FOUND;
    [Hle] int sceNpMatching2GetServerLocal(ushort ctxId, uint server) => Request(ctxId);
    [Hle] int sceNpMatching2GetWorldInfoList(ushort ctxId, uint req, uint opt, uint reqId) => Request(ctxId);
    [Hle] int sceNpMatching2GetLobbyInfoList(ushort ctxId, uint req, uint opt, uint reqId) => Request(ctxId);
    [Hle] int sceNpMatching2SearchRoom(ushort ctxId, uint req, uint opt, uint reqId) => Request(ctxId);
    [Hle] int sceNpMatching2CreateJoinRoom(ushort ctxId, uint req, uint opt, uint reqId) => Request(ctxId);
    [Hle] int sceNpMatching2JoinRoom(ushort ctxId, uint req, uint opt, uint reqId) => Request(ctxId);
    [Hle] int sceNpMatching2LeaveRoom(ushort ctxId, uint req, uint opt, uint reqId) => Request(ctxId);
    [Hle] int sceNpMatching2JoinLobby(ushort ctxId, uint req, uint opt, uint reqId) => Request(ctxId);
    [Hle] int sceNpMatching2LeaveLobby(ushort ctxId, uint req, uint opt, uint reqId) => Request(ctxId);
    [Hle] int sceNpMatching2GrantRoomOwner(ushort ctxId, uint req, uint opt, uint reqId) => Request(ctxId);
    [Hle] int sceNpMatching2KickoutRoomMember(ushort ctxId, uint req, uint opt, uint reqId) => Request(ctxId);
    [Hle] int sceNpMatching2GetRoomDataExternalList(ushort ctxId, uint req, uint opt, uint reqId) => Request(ctxId);
    [Hle] int sceNpMatching2SetRoomDataExternal(ushort ctxId, uint req, uint opt, uint reqId) => Request(ctxId);
    [Hle] int sceNpMatching2GetRoomDataInternal(ushort ctxId, uint req, uint opt, uint reqId) => Request(ctxId);
    [Hle] int sceNpMatching2SetRoomDataInternal(ushort ctxId, uint req, uint opt, uint reqId) => Request(ctxId);
    [Hle] int sceNpMatching2GetRoomMemberDataExternalList(ushort ctxId, uint req, uint opt, uint reqId) => Request(ctxId);
    [Hle] int sceNpMatching2GetRoomMemberDataInternal(ushort ctxId, uint req, uint opt, uint reqId) => Request(ctxId);
    [Hle] int sceNpMatching2SetRoomMemberDataInternal(ushort ctxId, uint req, uint opt, uint reqId) => Request(ctxId);
    [Hle] int sceNpMatching2SetUserInfo(ushort ctxId, uint req, uint opt, uint reqId) => Request(ctxId);
    [Hle] int sceNpMatching2GetUserInfoList(ushort ctxId, uint req, uint opt, uint reqId) => Request(ctxId);
    [Hle] int sceNpMatching2SendRoomMessage(ushort ctxId, uint req, uint opt, uint reqId) => Request(ctxId);
    [Hle] int sceNpMatching2SendRoomChatMessage(ushort ctxId, uint req, uint opt, uint reqId) => Request(ctxId);
    [Hle] int sceNpMatching2SendLobbyChatMessage(ushort ctxId, uint req, uint opt, uint reqId) => Request(ctxId);
    [Hle] int sceNpMatching2GetRoomMemberIdListLocal(ushort ctxId, ulong roomId, int sort, uint list, uint count) => Request(ctxId);
    [Hle] int sceNpMatching2GetRoomPasswordLocal(ushort ctxId, ulong roomId, uint withPw, uint pw) => Request(ctxId);
    [Hle] int sceNpMatching2GetSignalingOptParamLocal(ushort ctxId, ulong roomId, uint opt) => Request(ctxId);

    // Signaling: no peers are ever connected.
    [Hle] int sceNpMatching2SignalingGetConnectionStatus(ushort ctxId, ulong roomId, ushort memberId, Ptr<int> status, uint addr, uint port)
    {
        status.TrySet(0); // SCE_NP_MATCHING2_SIGNALING_CONN_STATUS_INACTIVE
        return Request(ctxId);
    }
    [Hle] int sceNpMatching2SignalingGetConnectionInfo(ushort ctxId, ulong roomId, ushort memberId, int code, uint info) => Request(ctxId);
    [Hle] int sceNpMatching2SignalingGetLocalNetInfo(uint info) => ERR_SIGNED_OUT;
    [Hle] int sceNpMatching2SignalingGetPeerNetInfo(ushort ctxId, ulong roomId, ushort memberId, uint reqId) => Request(ctxId);
    [Hle] int sceNpMatching2SignalingCancelPeerNetInfo(ushort ctxId, uint reqId) => Request(ctxId);
    [Hle] int sceNpMatching2SignalingGetPeerNetInfoResult(ushort ctxId, uint reqId, uint info) => Request(ctxId);
    [Hle] int sceNpMatching2SignalingGetPingInfo(ushort ctxId, uint req, uint opt, uint reqId) => Request(ctxId);
}

/// <summary>SceRudp (reliable UDP over the PSN signaling layer): contexts can be created but never reach a peer.</summary>
public sealed class Rudp : HleModule
{
    const int ERR_NOT_INITIALIZED = unchecked((int)0x80770001), ERR_INVALID_CONTEXT_ID = unchecked((int)0x80770004),
        ERR_INVALID_ARGUMENT = unchecked((int)0x80770005), ERR_WOULDBLOCK = unchecked((int)0x8077000d),
        ERR_CONN_RESET = unchecked((int)0x80770019), ERR_NOT_ACCEPTABLE = unchecked((int)0x80770011);

    bool _initialized;
    int _nextContext = 1, _nextPoll = 1;
    readonly HashSet<int> _contexts = new();

    [Hle] int sceRudpInit(uint pool, uint poolSize) { _initialized = true; return 0; }
    [Hle] int sceRudpEnd() { _initialized = false; lock (_contexts) _contexts.Clear(); return 0; }
    [Hle] int sceRudpEnableInternalIOThread(uint stackSize, uint priority) => _initialized ? 0 : ERR_NOT_INITIALIZED;
    [Hle] int sceRudpSetEventHandler(uint handler, uint arg) => _initialized ? 0 : ERR_NOT_INITIALIZED;
    [Hle] int sceRudpSetMaxSegmentSize(ushort mss) => 0;
    [Hle] int sceRudpGetMaxSegmentSize(Ptr<ushort> mss) { mss.TrySet(1280); return 0; }

    [Hle]
    int sceRudpCreateContext(uint handler, uint arg, Ptr<int> ctxId)
    {
        if (!_initialized) return ERR_NOT_INITIALIZED;
        if (ctxId.Address == 0) return ERR_INVALID_ARGUMENT;
        lock (_contexts)
        {
            int id = _nextContext++;
            _contexts.Add(id);
            ctxId.Value = id;
        }
        return 0;
    }

    bool Valid(int ctx) { lock (_contexts) return _contexts.Contains(ctx); }
    [Hle] int sceRudpTerminate(int ctx) { lock (_contexts) return _contexts.Remove(ctx) ? 0 : ERR_INVALID_CONTEXT_ID; }
    [Hle] int sceRudpBind(int ctx, int sock, ushort vport, byte muxMode) => Valid(ctx) ? 0 : ERR_INVALID_CONTEXT_ID;
    [Hle] int sceRudpInitiate(int ctx, uint addr, uint addrLen, ushort vport) => Valid(ctx) ? ERR_CONN_RESET : ERR_INVALID_CONTEXT_ID;
    [Hle] int sceRudpActivate(int ctx, uint addr, uint addrLen) => Valid(ctx) ? ERR_CONN_RESET : ERR_INVALID_CONTEXT_ID;
    [Hle] int sceRudpRead(int ctx, uint data, uint len, byte flags, uint info) => Valid(ctx) ? ERR_WOULDBLOCK : ERR_INVALID_CONTEXT_ID;
    [Hle] int sceRudpWrite(int ctx, uint data, uint len, byte flags) => Valid(ctx) ? ERR_NOT_ACCEPTABLE : ERR_INVALID_CONTEXT_ID;
    [Hle] int sceRudpFlush(int ctx) => Valid(ctx) ? 0 : ERR_INVALID_CONTEXT_ID;
    [Hle] int sceRudpGetSizeReadable(int ctx) => Valid(ctx) ? 0 : ERR_INVALID_CONTEXT_ID;
    [Hle] int sceRudpGetSizeWritable(int ctx) => Valid(ctx) ? 0 : ERR_INVALID_CONTEXT_ID;
    [Hle] int sceRudpSetOption(int ctx, int option, uint value, uint len) => Valid(ctx) ? 0 : ERR_INVALID_CONTEXT_ID;

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
    [Hle] int sceRudpNetFlush() => 0;
    [Hle] int sceRudpPollCreate(uint size) => _initialized ? Interlocked.Increment(ref _nextPoll) : ERR_NOT_INITIALIZED;
    [Hle] int sceRudpPollDestroy(int poll) => 0;
    [Hle] int sceRudpPollControl(int poll, int op, int ctx, ushort events) => 0;
    [Hle] int sceRudpPollWait(int poll, uint events, uint count, ulong timeout) => 0; // no events
    [Hle] int sceRudpPollCancel(int poll) => 0;
}
