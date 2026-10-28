using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// Offline multiplayer layer. The emulated console is on a LAN but signed out of PSN (see Online.cs), so
/// matchmaking (SceNpMatching2) and reliable UDP peers (SceRudp) behave as they do on a console without
/// PSN access: libraries and contexts initialise, every request that would need a server or a peer fails.
/// </summary>
public sealed class NpMatching2 : HleModule
{
    // NP error family 0x8055xxxx; server requests fail with PsnAccount.ServerError (unreachable server, or signed out).
    const int ERR_NOT_INITIALIZED = unchecked((int)0x80550c01), ERR_INVALID_ARGUMENT = unchecked((int)0x80550c03),
        ERR_CONTEXT_NOT_FOUND = unchecked((int)0x80550c0b);

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
    [Hle] int sceNpMatching2ContextStart(ushort ctxId, ulong timeout) => Valid(ctxId) ? PsnAccount.Current.ServerError : ERR_CONTEXT_NOT_FOUND;
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
    int Request(ushort ctxId) => Valid(ctxId) ? PsnAccount.Current.ServerError : ERR_CONTEXT_NOT_FOUND;
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
    [Hle] int sceNpMatching2SignalingGetLocalNetInfo(uint info) => PsnAccount.Current.ServerError;
    [Hle] int sceNpMatching2SignalingGetPeerNetInfo(ushort ctxId, ulong roomId, ushort memberId, uint reqId) => Request(ctxId);
    [Hle] int sceNpMatching2SignalingCancelPeerNetInfo(ushort ctxId, uint reqId) => Request(ctxId);
    [Hle] int sceNpMatching2SignalingGetPeerNetInfoResult(ushort ctxId, uint reqId, uint info) => Request(ctxId);
    [Hle] int sceNpMatching2SignalingGetPingInfo(ushort ctxId, uint req, uint opt, uint reqId) => Request(ctxId);
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
