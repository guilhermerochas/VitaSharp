using VitaSharp.Core.Hle.Np;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// SceNpManager and SceNpCommon: NP initialisation, service state, NpId helpers and auth tickets, following the emulated
/// PSN account (<see cref="PsnAccount"/>). Signed in (default): service state SIGNED_IN, the account's NpId and region,
/// and auth tickets that fail like an unreachable server. Signed out: anything that needs the account fails with
/// SCE_NP_ERROR_SIGNED_OUT. Service state callbacks are told the state once (from sceNpCheckCallback).
/// </summary>
public sealed class NpManager : HleModule
{
    internal const int ERR_ALREADY_INITIALIZED = unchecked((int)0x80550001), ERR_NOT_INITIALIZED = unchecked((int)0x80550002),
        ERR_INVALID_ARGUMENT = unchecked((int)0x80550003), ERR_UNKNOWN_PLATFORM_TYPE = unchecked((int)0x80550004),
        ERR_SIGNED_OUT = unchecked((int)0x80550006);
    const int MGR_INVALID_ARGUMENT = unchecked((int)0x80550503);
    internal const int UTIL_INVALID_ARGUMENT = unchecked((int)0x80550601), UTIL_INVALID_NP_ID = unchecked((int)0x80550605),
        UTIL_NOT_MATCH = unchecked((int)0x80550609);

    // SceNpServiceState: UNKNOWN 0, SIGNED_OUT 1, SIGNED_IN 2, ONLINE 3 (Vita3K reports SIGNED_IN for a signed-in account).
    internal const int NP_STATE_SIGNED_OUT = 1, NP_STATE_SIGNED_IN = 2;
    static int ServiceState => PsnAccount.Current.SignedIn ? NP_STATE_SIGNED_IN : NP_STATE_SIGNED_OUT;
    readonly List<(uint Func, uint Arg, bool Notified)> _npCallbacks = new();

    readonly object _lock = new();
    bool _npInited;
    string? _commId;
    /// <summary>True between sceNpInit and sceNpTerm.</summary>
    internal bool NpInited { get { lock (_lock) return _npInited; } }
    internal string? CommunicationId { get { lock (_lock) return _commId; } }

    sealed class AuthReq { public uint Cb, Arg; public bool Done, Aborted; public byte[]? Ticket; }
    readonly Dictionary<int, AuthReq> _authReqs = new();
    readonly HashSet<int> _oauthReqs = new();
    int _nextReq = 1;

    /// <summary>SceNpCommunicationConfig (0xC): +0 npCommId*, +4 npCommPass*, +8 npCommSign*.
    /// SceNpCommunicationId (0xC): data[9], term, num (+0xA), dummy.</summary>
    [Hle]
    int sceNpInit(uint config, uint opt)
    {
        lock (_lock)
        {
            if (_npInited) return ERR_ALREADY_INITIALIZED;
            _npInited = true;
            uint id = config != 0 ? GuestMemory.R32(config) : 0;
            _commId = id != 0 ? $"{GuestMemory.ReadCString(id, 9)}_{GuestMemory.Read<byte>(id + 10):D2}" : null;
        }
        var acct = PsnAccount.Current;
        Log.Info($"sceNpInit: communication id {_commId ?? "(none)"} ({(acct.SignedIn ? $"signed in to PSN as {acct.OnlineId}, servers unreachable" : "signed out of PSN")})");
        return 0;
    }

    [Hle]
    int sceNpTerm()
    {
        lock (_lock) { _npInited = false; _commId = null; }
        lock (_npCallbacks) _npCallbacks.Clear();
        return 0;
    }

    // shared empty results of the per-frame poll (never modified)
    static readonly List<(uint Func, uint Arg, bool Notified)> NoPending = [];
    static readonly List<(int Id, uint Cb, uint Arg)> NoDone = [];

    [Hle]
    int sceNpCheckCallback()
    {
        List<(uint Func, uint Arg, bool Notified)> pending;
        lock (_npCallbacks)
        {
            // polled every frame: only build the lists when something is pending
            bool anyPending = false;
            foreach (var c in _npCallbacks) if (!c.Notified) { anyPending = true; break; }
            pending = anyPending ? _npCallbacks.Where(c => !c.Notified).ToList() : NoPending;
            for (int i = 0; i < _npCallbacks.Count; i++) _npCallbacks[i] = (_npCallbacks[i].Func, _npCallbacks[i].Arg, true);
        }
        foreach (var c in pending) Kernel.GuestThread.Current?.CallGuest(c.Func, (uint)ServiceState, 0, c.Arg);

        // auth (ticket) requests complete here: with the local services the callback gets the ticket size (the ticket is
        // then read with sceNpAuthGetTicket), otherwise the server error
        List<(int Id, uint Cb, uint Arg)> done;
        lock (_authReqs)
        {
            bool anyDone = false;
            foreach (var r in _authReqs.Values) if (!r.Done && !r.Aborted && r.Cb != 0) { anyDone = true; break; }
            done = !anyDone ? NoDone : _authReqs.Where(kv => !kv.Value.Done && !kv.Value.Aborted && kv.Value.Cb != 0)
                .Select(kv => (kv.Key, kv.Value.Cb, kv.Value.Arg)).ToList();
            foreach (var d in done) _authReqs[d.Id].Done = true;
        }
        foreach (var d in done)
        {
            int result;
            lock (_authReqs) result = _authReqs.TryGetValue(d.Id, out var r) && r.Ticket != null ? r.Ticket.Length : PsnAccount.Current.ServerError;
            Kernel.GuestThread.Current?.CallGuest(d.Cb, (uint)d.Id, unchecked((uint)result), d.Arg);
        }
        return 0;
    }

    [Hle] int sceNpGetServiceState(Ptr<int> state) { if (state.Address == 0) return ERR_INVALID_ARGUMENT; state.Value = ServiceState; return 0; }

    [Hle]
    int sceNpRegisterServiceStateCallback(uint cb, uint arg)
    {
        lock (_npCallbacks) _npCallbacks.Add((cb, arg, false));
        return 0;
    }

    [Hle] int sceNpUnregisterServiceStateCallback() { lock (_npCallbacks) _npCallbacks.Clear(); return 0; }

    /// <summary>The signed-in account's SceNpId (like Vita3K: online id, platform "psp2", isIdValid; the rest zero).</summary>
    [Hle]
    int sceNpManagerGetNpId(uint npId)
    {
        if (npId == 0) return MGR_INVALID_ARGUMENT;
        var acct = PsnAccount.Current;
        if (!acct.SignedIn) return ERR_SIGNED_OUT;
        WriteNpId(npId, acct.OnlineId);
        return 0;
    }

    /// <summary>SceNpId (0x24): onlineId.data[16] + term/dummy, opt[8] (platform "psp2" at +0x18), reserved[0] = 1 (valid).</summary>
    internal static void WriteNpId(uint npId, string onlineId)
    {
        GuestMemory.Fill(npId, 0, 0x24);
        var b = System.Text.Encoding.ASCII.GetBytes(onlineId);
        b.AsSpan(0, Math.Min(b.Length, 16)).CopyTo(GuestMemory.Span(npId, 16));
        NpIdUtil.SetPlatformType(npId, NpIdUtil.PlatformVita);
        GuestMemory.Write(npId + 0x1C, (byte)1);
    }

    [Hle] int sceNpManagerGetContentRatingFlag(Ptr<int> isRestricted, Ptr<int> age) { isRestricted.TrySet(0); age.TrySet(18); return 0; }

    /// <summary>SceNpCountryCode (4: "us\0\0") and the account language (the system language, English US); signed out
    /// there is no account and so no region (coherent with sceNpManagerGetNpId).</summary>
    [Hle]
    int sceNpManagerGetAccountRegion(uint countryCode, uint languageCode)
    {
        if (countryCode == 0 || languageCode == 0) return MGR_INVALID_ARGUMENT;
        if (!PsnAccount.Current.SignedIn) return ERR_SIGNED_OUT;
        GuestMemory.W32(countryCode, 0x00007375); // "us"
        GuestMemory.W32(languageCode, (uint)AppUtil.SystemParamInt(1));
        return 0;
    }

    // the cached account parameters: layout unconfirmed (Vita3K unimplemented), so the cache is reported unavailable
    [Hle] int sceNpManagerGetCachedParam(uint param) => param == 0 ? MGR_INVALID_ARGUMENT : ERR_SIGNED_OUT;

    [Hle] int sceNpAuthCreateOAuthRequest() { lock (_oauthReqs) { int id = _nextReq++; _oauthReqs.Add(id); return id; } }
    [Hle] int sceNpAuthDeleteOAuthRequest(int reqId) { lock (_oauthReqs) return _oauthReqs.Remove(reqId) ? 0 : MGR_INVALID_ARGUMENT; }
    [Hle] int sceNpAuthAbortOAuthRequest(int reqId) => 0;
    [Hle] int sceNpAuthGetAuthorizationCode(int reqId, uint param, uint code, uint issuerId) => PsnAccount.Current.ServerError;

    // ---------------------------------------------------------------- auth tickets (SceNpCommon)

    [Hle] int sceNpAuthInit() => 0;
    [Hle] int sceNpAuthTerm() { lock (_authReqs) _authReqs.Clear(); return 0; }

    /// <summary>SceNpAuthRequestParameter: +0 size, +4 version, +8 serviceId*, +0xC cookie*, +0x10 cookieSize,
    /// +0x14 entitlementId*, +0x18 consumedCount, +0x1C ticketCb, +0x20 cbArg. The request is created; its callback
    /// runs on the next sceNpCheckCallback with the size of a locally issued ticket (<see cref="NpTicket"/>, local PSN
    /// services) or the server error.</summary>
    [Hle]
    int sceNpAuthCreateStartRequest(uint param)
    {
        if (param == 0) return ERR_INVALID_ARGUMENT;
        uint size = GuestMemory.R32(param);
        if (size < 0x24) return ERR_INVALID_ARGUMENT;
        byte[]? ticket = null;
        var acct = PsnAccount.Current;
        if (acct.UsesLocalServices)
        {
            uint sid = GuestMemory.R32(param + 8);
            string service = sid != 0 ? GuestMemory.ReadCString(sid, 0x18) : "";
            ticket = NpTicket.Build(acct.OnlineId, service, InstalledEntitlements());
        }
        lock (_authReqs)
        {
            int id = _nextReq++;
            _authReqs[id] = new AuthReq { Cb = GuestMemory.R32(param + 0x1C), Arg = GuestMemory.R32(param + 0x20), Ticket = ticket };
            Log.Debug($"sceNpAuthCreateStartRequest = {id} (ticket callback 0x{_authReqs[id].Cb:X8}, {(ticket != null ? $"local ticket {ticket.Length} bytes" : "no ticket")})");
            return id;
        }
    }

    /// <summary>The DLC installed for the running title (ux0:addcont/&lt;TITLEID&gt;/*): every one counts as owned.</summary>
    List<NpTicket.Entitlement> InstalledEntitlements()
    {
        var list = new List<NpTicket.Entitlement>();
        if (Emu == null || string.IsNullOrEmpty(Emu.TitleId)) return list;
        string dir = Path.Combine(Emu.Options.FsRoot, "ux0", "addcont", Emu.TitleId);
        if (!Directory.Exists(dir)) return list;
        foreach (var sub in Directory.GetDirectories(dir).Order())
        {
            string? contentId = null;
            var sfo = Io.Vfs.ResolveCaseInsensitive(sub, "sce_sys/param.sfo");
            try { if (File.Exists(sfo)) contentId = Package.Sfo.Load(sfo).ContentId; }
            catch (InvalidDataException) { }
            list.Add(NpTicket.Entitlement.FromContent(contentId, Emu.TitleId, Path.GetFileName(sub)));
        }
        if (list.Count > 0) Log.Info($"NP auth: {list.Count} installed add-on(s) reported as owned: {string.Join(", ", list.Select(e => e.Id))}");
        return list;
    }

    NpTicket? TicketAt(uint ticket, uint size)
    {
        if (ticket == 0 || size < 8 || size > 0x10000) return null;
        return NpTicket.Parse(GuestMemory.Span(ticket, (int)size).ToArray());
    }

    [Hle] int sceNpAuthDestroyRequest(int id) { lock (_authReqs) _authReqs.Remove(id); return 0; }

    /// <summary>Cancels a pending ticket request (no callback afterwards). Unknown ids are accepted: cleanup paths
    /// call this unconditionally.</summary>
    [Hle]
    int sceNpAuthAbortRequest(int id)
    {
        lock (_authReqs)
            if (_authReqs.TryGetValue(id, out var r)) r.Aborted = true;
            else Log.Debug($"sceNpAuthAbortRequest({id}): unknown request");
        return 0;
    }

    /// <summary>int (SceNpAuthRequestId id, void *buf, SceSize len): copies the ticket (a NULL buffer only checks it).</summary>
    [Hle]
    int sceNpAuthGetTicket(int id, uint buf, uint len)
    {
        byte[]? t;
        lock (_authReqs) t = _authReqs.TryGetValue(id, out var r) ? r.Ticket : null;
        if (t == null) return PsnAccount.Current.ServerError;
        if (buf != 0) t.AsSpan(0, (int)Math.Min(len, (uint)t.Length)).CopyTo(GuestMemory.Span(buf, (int)Math.Min(len, (uint)t.Length)));
        return 0;
    }

    /// <summary>The entitlement id is the add-on's content id ("UP0001-PCSE00914_00-LABEL"), without the service prefix
    /// ("PCSE00914_00-LABEL", what fits SceNpEntitlementId's 32 bytes) or just the label: all three match.</summary>
    [Hle]
    int sceNpAuthGetEntitlementById(uint ticket, uint size, string id, uint ent)
    {
        var t = TicketAt(ticket, size);
        if (t == null) return PsnAccount.Current.ServerError;
        if (ent == 0) return ERR_INVALID_ARGUMENT;
        var e = t.Entitlements.FirstOrDefault(x => x.Matches(id));
        if (e == null) return ERR_ENTITLEMENT_NOT_FOUND;
        e.Write(ent);
        return 0;
    }

    [Hle]
    int sceNpAuthGetEntitlementByIdPrefix(uint ticket, uint size, string prefix, uint ent)
    {
        var t = TicketAt(ticket, size);
        if (t == null) return PsnAccount.Current.ServerError;
        if (ent == 0) return ERR_INVALID_ARGUMENT;
        var e = t.Entitlements.FirstOrDefault(x => x.Id.StartsWith(prefix, StringComparison.Ordinal) || x.ContentId.StartsWith(prefix, StringComparison.Ordinal));
        if (e == null) return ERR_ENTITLEMENT_NOT_FOUND;
        e.Write(ent);
        return 0;
    }

    /// <summary>int (ticket, size, SceNpEntitlementId *list (32 each), SceSize num): returns the number of entitlements
    /// (all of them; at most num are written).</summary>
    [Hle]
    int sceNpAuthGetEntitlementIdList(uint ticket, uint size, uint list, uint num)
    {
        var t = TicketAt(ticket, size);
        if (t == null) return PsnAccount.Current.ServerError;
        for (int i = 0; i < Math.Min(num, (uint)t.Entitlements.Count) && list != 0; i++)
            NpTicket.WriteId(list + (uint)i * 32, t.Entitlements[i].Id);
        return t.Entitlements.Count;
    }

    /// <summary>int (ticket, size, int paramId, SceNpTicketParam *param (256-byte union)).</summary>
    [Hle]
    int sceNpAuthGetTicketParam(uint ticket, uint size, int paramId, uint param)
    {
        var t = TicketAt(ticket, size);
        if (t == null) return PsnAccount.Current.ServerError;
        if (param == 0) return ERR_INVALID_ARGUMENT;
        GuestMemory.Fill(param, 0, 256);
        return t.WriteParam(paramId, param) ? 0 : ERR_INVALID_ARGUMENT;
    }

    /// <summary>"Entitlement not found" (exact Vita code unconfirmed: SCE_NP_MANAGER_ERROR_ID_NOT_AVAIL, Vita3K).</summary>
    const int ERR_ENTITLEMENT_NOT_FOUND = unchecked((int)0x80550509);

    // ---------------------------------------------------------------- NpId helpers (SceNpCommon)

    [Hle] int sceNpCmpNpId(uint a, uint b) => NpIdUtil.Compare(a, b);

    [Hle]
    int sceNpCmpNpIdInOrder(uint a, uint b, uint order)
    {
        if (order == 0) return UTIL_INVALID_ARGUMENT;
        int r = NpIdUtil.Order(a, b, out int o);
        if (r != 0) return r;
        GuestMemory.W32(order, (uint)o);
        return 0;
    }

    [Hle]
    int sceNpCmpOnlineId(uint a, uint b)
    {
        if (a == 0 || b == 0) return UTIL_INVALID_ARGUMENT;
        return NpIdUtil.OnlineIdCompare(a, b) == 0 ? 0 : UTIL_NOT_MATCH;
    }

    [Hle] int sceNpGetPlatformType(uint npId) => NpIdUtil.GetPlatformType(npId);
    [Hle] int sceNpSetPlatformType(uint npId, int type) => NpIdUtil.SetPlatformType(npId, type);
}

/// <summary>
/// SceNpId (0x24): +0x00 onlineId.data[16], +0x10 term, +0x11 dummy[3], +0x14 opt[8] (opt[4..7] = platform, e.g.
/// "psp2"), +0x1C reserved[8] (reserved[0] = 1 when the id is valid).
/// </summary>
public static class NpIdUtil
{
    public const int PlatformNone = 0, PlatformPs3 = 1, PlatformVita = 2, PlatformPs4 = 3;
    static readonly uint[] PlatformTags = [0, 0x00337370 /* "ps3\0" */, 0x32707370 /* "psp2" */, 0x00347370 /* "ps4\0" */];

    static bool Valid(uint id) => GuestMemory.Read<byte>(id + 0x1C) == 1;

    public static int OnlineIdCompare(uint a, uint b)
    {
        var sa = GuestMemory.Span(a, 16);
        var sb = GuestMemory.Span(b, 16);
        for (int i = 0; i < 16; i++)
        {
            if (sa[i] != sb[i]) return sa[i] < sb[i] ? -1 : 1;
            if (sa[i] == 0) break;
        }
        return 0;
    }

    /// <summary>sceNpCmpNpId: online id, opt[0..3], then the platform with the PS3 rule "an unset platform matches".</summary>
    public static int Compare(uint a, uint b)
    {
        if (a == 0 || b == 0) return NpManager.UTIL_INVALID_ARGUMENT;
        if (!Valid(a) || !Valid(b)) return NpManager.UTIL_INVALID_NP_ID;
        if (OnlineIdCompare(a, b) != 0) return NpManager.UTIL_NOT_MATCH;
        if (GuestMemory.R32(a + 0x14) != GuestMemory.R32(b + 0x14)) return NpManager.UTIL_NOT_MATCH;
        uint pa = GuestMemory.R32(a + 0x18), pb = GuestMemory.R32(b + 0x18);
        if (pa != pb && (pa & 0xFF) != 0 && (pb & 0xFF) != 0) return NpManager.UTIL_NOT_MATCH;
        return 0;
    }

    public static int Order(uint a, uint b, out int order)
    {
        order = 0;
        if (a == 0 || b == 0) return NpManager.UTIL_INVALID_ARGUMENT;
        if (!Valid(a) || !Valid(b)) return NpManager.UTIL_INVALID_NP_ID;
        order = OnlineIdCompare(a, b);
        if (order == 0) order = Math.Sign(GuestMemory.Span(a + 0x14, 4).SequenceCompareTo(GuestMemory.Span(b + 0x14, 4)));
        if (order == 0)
        {
            bool za = GuestMemory.Read<byte>(a + 0x18) == 0, zb = GuestMemory.Read<byte>(b + 0x18) == 0;
            if (!za && !zb) order = Math.Sign(GuestMemory.Span(a + 0x18, 4).SequenceCompareTo(GuestMemory.Span(b + 0x18, 4)));
        }
        return 0;
    }

    public static int GetPlatformType(uint npId)
    {
        if (npId == 0) return NpManager.ERR_INVALID_ARGUMENT;
        int t = Array.IndexOf(PlatformTags, GuestMemory.R32(npId + 0x18));
        return t >= 0 ? t : NpManager.ERR_UNKNOWN_PLATFORM_TYPE;
    }

    public static int SetPlatformType(uint npId, int type)
    {
        if (npId == 0) return NpManager.ERR_INVALID_ARGUMENT;
        if (type is < 0 or > 3) return NpManager.ERR_UNKNOWN_PLATFORM_TYPE;
        GuestMemory.W32(npId + 0x18, PlatformTags[type]);
        return 0;
    }
}

/// <summary>
/// SceNpBasic additions, SceNpMessage, SceNpActivity and the party state (SceNpPartyGameUtil): offline there are no
/// friends, messages or parties. (The older SceNpBasic/SceNpParty functions live in Online.cs, class Np.)
/// </summary>
public sealed class NpSocial : HleModule
{
    const int BASIC_INVALID_ARGUMENT = unchecked((int)0x80551D02),
        ERR_NOT_IN_PARTY = unchecked((int)0x80553402);

    // ---------------------------------------------------------------- SceNpBasic: no friends, nothing blocked

    int FriendQuery(uint friendId, uint outPtr) => friendId == 0 || outPtr == 0 ? BASIC_INVALID_ARGUMENT : PsnAccount.Current.ServerError;
    [Hle] int sceNpBasicGetFriendOnlineStatus(uint friendId, uint status) => FriendQuery(friendId, status);
    [Hle] int sceNpBasicGetFriendContextState(uint friendId, uint state) => FriendQuery(friendId, state);
    [Hle] int sceNpBasicGetGamePresenceOfFriend(uint friendId, uint presence) => FriendQuery(friendId, presence);
    [Hle] int sceNpBasicGetGameJoiningPresence(uint friendId, uint presence) => PsnAccount.Current.ServerError;

    [Hle]
    int sceNpBasicCheckIfPlayerIsBlocked(uint player, Ptr<int> blocked)
    {
        if (player == 0 || blocked.IsNull) return BASIC_INVALID_ARGUMENT;
        blocked.Value = 0;
        return 0;
    }

    [Hle] int sceNpBasicGetFriendRequestEntryCount(Ptr<uint> count) { count.TrySet(0); return 0; }
    [Hle] int sceNpBasicGetFriendRequestEntries(uint start, uint entries, uint num, Ptr<uint> retrieved) { retrieved.TrySet(0); return 0; }
    [Hle] int sceNpBasicGetRequestedFriendRequestEntryCount(Ptr<uint> count) { count.TrySet(0); return 0; }
    [Hle] int sceNpBasicGetRequestedFriendRequestEntries(uint start, uint entries, uint num, Ptr<uint> retrieved) { retrieved.TrySet(0); return 0; }
    [Hle] int sceNpBasicGetPlaySessionLogSize(int type, Ptr<uint> size) { size.TrySet(0); return 0; }
    [Hle] int sceNpBasicGetPlaySessionLog(int type, uint index, uint log) => BASIC_INVALID_ARGUMENT; // the log is empty
    [Hle] int sceNpBasicUnsetInGamePresence() => PsnAccount.Current.UsesLocalServices ? 0 : PsnAccount.Current.ServerError;

    // ---------------------------------------------------------------- SceNpMessage: no messages without PSN

    bool _msgInited;
    internal bool MessageInited => _msgInited;
    [Hle] int sceNpMessageInit() { _msgInited = true; return 0; }
    [Hle] int sceNpMessageInitWithParam(uint param) { _msgInited = true; return 0; }
    [Hle] int sceNpMessageTerm() { _msgInited = false; return 0; }
    [Hle] int sceNpMessageAbort() => 0;

    /// <summary>Only reachable after a message app event, which VitaSharp never raises.</summary>
    [Hle]
    int sceNpMessageGetMessage(uint r0, uint r1, uint r2, uint r3)
    {
        Log.WarnOnce("npmsg-get", $"sceNpMessageGetMessage(0x{r0:X}, 0x{r1:X}, 0x{r2:X}, 0x{r3:X}): no PSN messages");
        return PsnAccount.Current.ServerError;
    }

    [Hle]
    int sceNpMessageGetAttachedData(uint r0, uint r1, uint r2, uint r3)
    {
        Log.WarnOnce("npmsg-att", $"sceNpMessageGetAttachedData(0x{r0:X}, 0x{r1:X}, 0x{r2:X}, 0x{r3:X}): no PSN messages");
        return PsnAccount.Current.ServerError;
    }

    [Hle] int sceNpMessageGetMessageEntryCount() => PsnAccount.Current.ServerError;
    [Hle] int sceNpMessageGetMessageEntries() => PsnAccount.Current.ServerError;
    [Hle] int sceNpMessageGetMessageEntry() => PsnAccount.Current.ServerError;
    [Hle] int sceNpMessageSetAttachedDataUsedFlag() => PsnAccount.Current.ServerError;
    [Hle] int sceNpMessageSyncMessage() => PsnAccount.Current.ServerError;

    // ---------------------------------------------------------------- SceNpActivity: nowhere to post

    bool _activityInited;
    internal bool ActivityInited => _activityInited;
    [Hle] int sceNpActivityInit() { _activityInited = true; return 0; }
    [Hle] int sceNpActivityTerm() { _activityInited = false; return 0; }
    [Hle] int sceNpActivityPostStatus(uint param) => PsnAccount.Current.UsesLocalServices ? 0 : PsnAccount.Current.ServerError; // posted to nobody
    [Hle] int sceNpActivityPostAppStartupStatus(uint param) => PsnAccount.Current.UsesLocalServices ? 0 : PsnAccount.Current.ServerError;

    // ---------------------------------------------------------------- SceNpPartyGameUtil: never in a party

    /// <summary>Writes the "not in a party" state. Its numeric value is unconfirmed (no header, Vita3K
    /// unimplemented); 0 is used and the call is logged once.</summary>
    [Hle]
    int sceNpPartyGetState(uint state)
    {
        if (state == 0) return NpManager.ERR_INVALID_ARGUMENT;
        GuestMemory.W32(state, 0);
        Log.DebugOnce("party-state", "sceNpPartyGetState: not in a party (state 0)");
        return 0;
    }

    [Hle] int sceNpPartyGetId(uint id) => ERR_NOT_IN_PARTY;
    [Hle] int sceNpPartyGetMemberSessionInfo(uint memberId, uint info) => ERR_NOT_IN_PARTY;
    [Hle] int sceNpPartyGetGameSessionReadyState(uint state) => ERR_NOT_IN_PARTY;
}
