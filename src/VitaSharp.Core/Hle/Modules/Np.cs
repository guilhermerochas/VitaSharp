using VitaSharp.Core.Hle.Np;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// SceNpManager and SceNpCommon: NP initialisation, service state, NpId helpers and auth tickets, following the emulated
/// PSN account (<see cref="PsnAccount"/>). Signed in (default): the account's NpId and region; service state ONLINE with
/// the local PSN services (auth tickets issued locally), else SIGNED_IN until a NetCheckDialog in PSN_ONLINE mode connects
/// the console (then ONLINE, while server requests still fail like an unreachable server). Signed out: anything that
/// needs the account fails with SCE_NP_ERROR_SIGNED_OUT. Service state callbacks are told the state from
/// sceNpCheckCallback, and again whenever it changes.
/// </summary>
public sealed partial class NpManager : HleModule
{
    internal const int ERR_ALREADY_INITIALIZED = unchecked((int)0x80550001), ERR_NOT_INITIALIZED = unchecked((int)0x80550002),
        ERR_INVALID_ARGUMENT = unchecked((int)0x80550003), ERR_UNKNOWN_PLATFORM_TYPE = unchecked((int)0x80550004),
        ERR_SIGNED_OUT = unchecked((int)0x80550006);
    const int MGR_INVALID_ARGUMENT = unchecked((int)0x80550503);
    internal const int UTIL_INVALID_ARGUMENT = unchecked((int)0x80550601), UTIL_INVALID_NP_ID = unchecked((int)0x80550605),
        UTIL_NOT_MATCH = unchecked((int)0x80550609);

    // SceNpServiceState: UNKNOWN 0, SIGNED_OUT 1, SIGNED_IN 2 (signed in, not connected to PSN), ONLINE 3.
    internal const int NP_STATE_SIGNED_OUT = 1, NP_STATE_SIGNED_IN = 2, NP_STATE_ONLINE = 3;

    /// <summary>
    /// The NP service state. A signed-in console whose PSN services answer (the local ones) is ONLINE, like a real Vita
    /// that signed in over a working network. Without them it is SIGNED_IN until the PSN_ONLINE NetCheckDialog (which
    /// finishes OK when signed in) connects it. Games that run that dialog wait for ONLINE afterwards: WRC 4 otherwise
    /// sits on "PLEASE WAIT..." after its title screen forever (Vita3K, which always reports SIGNED_IN, has the same gap).
    /// </summary>
    internal int ServiceState
    {
        get
        {
            var acct = PsnAccount.Current;
            if (!acct.SignedIn) return NP_STATE_SIGNED_OUT;
            return acct.LocalServices || _psnConnected ? NP_STATE_ONLINE : NP_STATE_SIGNED_IN;
        }
    }
    volatile bool _psnConnected;

    /// <summary>A NetCheckDialog in PSN_ONLINE mode finished OK: the console stays connected to PSN from now on.</summary>
    internal void OnPsnOnlineCheckSucceeded() => _psnConnected = true;

    // the registered service state callbacks and the state each was last told (0: not yet)
    readonly List<(uint Func, uint Arg, int Notified)> _npCallbacks = new();

    readonly object _lock = new();
    bool _npInited;
    string? _commId;
    /// <summary>True between sceNpInit and sceNpTerm.</summary>
    internal bool NpInited { get { lock (_lock) return _npInited; } }
    internal string? CommunicationId { get { lock (_lock) return _commId; } }

    sealed partial class AuthReq { public uint Cb, Arg; public bool Done, Aborted; public byte[]? Ticket; }
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
        // its signature: a trophy context created without an id uses this one (np_trophy NID_96DF1B76)
        uint sign = config != 0 ? GuestMemory.R32(config + 8) : 0;
        if (_commId != null && sign != 0)
            TrophyUnlocks.SaveSignature(Emu.Options.FsRoot, _commId, GuestMemory.ReadSpan(sign, TrophyUnlocks.SignatureSize).ToArray());
        var acct = PsnAccount.Current;
        Log.Info($"sceNpInit: communication id {_commId ?? "(none)"} ({(acct.SignedIn ? $"signed in to PSN as {acct.OnlineId}, {(acct.LocalServices ? "local PSN services" : "servers unreachable")}" : "signed out of PSN")})");
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
    static readonly List<(uint Func, uint Arg, int Notified)> NoPending = [];
    static readonly List<(int Id, uint Cb, uint Arg)> NoDone = [];

    [Hle]
    int sceNpCheckCallback()
    {
        List<(uint Func, uint Arg, int Notified)> pending;
        int state = ServiceState;
        lock (_npCallbacks)
        {
            // polled every frame: only build the lists when a callback has not been told the current state
            bool anyPending = false;
            foreach (var c in _npCallbacks) if (c.Notified != state) { anyPending = true; break; }
            pending = anyPending ? _npCallbacks.Where(c => c.Notified != state).ToList() : NoPending;
            if (anyPending)
                for (int i = 0; i < _npCallbacks.Count; i++) _npCallbacks[i] = (_npCallbacks[i].Func, _npCallbacks[i].Arg, state);
        }
        foreach (var c in pending) Kernel.GuestThread.Current?.CallGuest(c.Func, (uint)state, 0, c.Arg);

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
        lock (_npCallbacks) _npCallbacks.Add((cb, arg, 0));
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

    // ---- system-software NIDs of np_manager.suprx (FW 3.74), no public names. np_manager is an IPC client of the
    // shell's SceNpManagerIpc service; these are its account-side requests, which only the system software makes. The
    // system itself has no PSN account (its registry's /CONFIG/NP/account_id is empty, Settings offers "Sign up"), so
    // the account requests end like the service's signed-out answer; the library's own checks come first.
    const int MGR_NOT_INITIALIZED = unchecked((int)0x80550502);
    uint _sysHandlers;
    bool _sysInited;

    /// <summary>(const void *handlers): the shell (which hosts the service) gives np_manager its handler table, as
    /// libnetctl's NID_C61F3E96; only kept.</summary>
    [Hle("sceNpManager_77DE17D4", Nid = 0x77DE17D4, Library = "SceNpManager")]
    int sceNpManagerSetServiceHandlers(uint handlers)
    {
        if (handlers == 0) return MGR_INVALID_ARGUMENT;
        _sysHandlers = handlers;
        return 0;
    }

    /// <summary>(uint poolSize, 0 = 0x1000): the system-side library initialisation (its own heap); a second call
    /// fails with ALREADY_INITIALIZED.</summary>
    [Hle("sceNpManager_C89E0B94", Nid = 0xC89E0B94, Library = "SceNpManager")]
    int sceNpManagerInitForSystem(uint poolSize)
    {
        lock (_lock)
        {
            if (_sysInited) return ERR_ALREADY_INITIALIZED;
            _sysInited = true;
            return 0;
        }
    }

    /// <summary>(int mode): np_manager connects to the service over IPMI (errors map to 0x80550581/0x80550582). The
    /// HLE service is always there.</summary>
    [Hle("sceNpManager_29C9B8A5", Nid = 0x29C9B8A5, Library = "SceNpManager")] int sceNpManagerConnectService(int mode) => 0;

    /// <summary>Requests 0xA0000 (int *out: NID_B24FC028), 0xA0005 (32 bytes: NID_CFAEB050), 0xB0015 (0x148 bytes:
    /// NID_E45E4003): the system user's account record, which is empty without an account (all zero: no account id,
    /// not signed in). Messages reads NID_B24FC028 and offers "Sign up" on 0 (an error leaves it waiting); the daily
    /// checker, the console-information sender and the activity poster skip their PSN work.</summary>
    [Hle("sceNpManager_B24FC028", Nid = 0xB24FC028, Library = "SceNpManager")] int sceNpManagerSysGetAccountInt(uint p) => ZeroRecord(p, 4);
    [Hle("sceNpManager_CFAEB050", Nid = 0xCFAEB050, Library = "SceNpManager")] int sceNpManagerSysGetAccountData(uint p) => ZeroRecord(p, 0x20);
    [Hle("sceNpManager_E45E4003", Nid = 0xE45E4003, Library = "SceNpManager")] int sceNpManagerSysGetAccountInfo(uint p) => ZeroRecord(p, 0x148);

    static int ZeroRecord(uint p, uint size)
    {
        if (p == 0) return MGR_INVALID_ARGUMENT;
        GuestMemory.Fill(p, 0, size);
        return 0;
    }

    /// <summary>(void *info, SceNpId *npId, SceNpAccountId *accountId): request 0xB001C, three optional outputs (0x44,
    /// 0x24 and 8 bytes; NULL ones go to a scratch buffer). Like the records above it is empty without an account (all
    /// zero, success): the shell's trophy server job reads the account id into TRPUSER.DAT and, on an error, deletes
    /// the file and Trophy Collection shows "Could not restore the trophy information (NP-2042-8)".</summary>
    [Hle("sceNpManager_DDF30B0E", Nid = 0xDDF30B0E, Library = "SceNpManager")]
    int sceNpManagerSysGetAccountIds(uint info, uint npId, uint accountId)
    {
        if (info != 0) GuestMemory.Fill(info, 0, 0x44);
        if (npId != 0) GuestMemory.Fill(npId, 0, 0x24);
        if (accountId != 0) GuestMemory.Fill(accountId, 0, 8);
        return 0;
    }

    /// <summary>(void *a, void *b, void *c, void *d): account details with optional outputs (NULL ones go to a scratch
    /// buffer): NID_ED12AF16 (Sign Up). Signed out.</summary>
    [Hle("sceNpManager_ED12AF16", Nid = 0xED12AF16, Library = "SceNpManager")] int sceNpManagerSysGetAccountDetails(uint a, uint b, uint c, uint d) => ERR_SIGNED_OUT;

    /// <summary>(void *callback, void *arg): a system-side event callback in np_manager's four-slot table (full:
    /// 0x80550504); the library registers it with the service and returns the service's id for it. The signed-out
    /// service never raises an event, so the callback never runs.</summary>
    [Hle("sceNpManager_351A74C2", Nid = 0x351A74C2, Library = "SceNpManager")]
    int sceNpManagerSysRegisterCallback(uint callback, uint arg)
    {
        lock (_lock)
        {
            if (_sysCallbacks >= 4) return unchecked((int)0x80550504);
            return ++_sysCallbacks;
        }
    }
    int _sysCallbacks;

    /// <summary>(void): the system-side clean-up (np_manager releases its six + seven internal objects and runs its
    /// exit hook); 0x80550002 when the system side was never initialised, but the clean-up runs either way.</summary>
    [Hle("sceNpManager_997CD5A7", Nid = 0x997CD5A7, Library = "SceNpManager")]
    int sceNpManagerSysCleanup()
    {
        lock (_lock)
        {
            int r = _sysInited ? 0 : unchecked((int)0x80550002);
            _sysCallbacks = 0;
            return r;
        }
    }

    /// <summary>
    /// (SceNpManagerCacheParam *param, 0xA4 bytes): np_manager.suprx (FW 3.74) fetches the 0x148-byte account record of
    /// NID_E45E4003 and copies its +0 (0x24 bytes, the SceNpId) to param+0 and its +0x38 (0x80 bytes, the avatar URL)
    /// to param+0x24; the record's error is returned as is, otherwise 0. The emulated account's record holds its NpId
    /// (as sceNpManagerGetNpId) and no avatar URL; without an account it is all zero, as for the system.
    /// </summary>
    [Hle]
    int sceNpManagerGetCachedParam(uint param)
    {
        if (param == 0) return MGR_INVALID_ARGUMENT;
        GuestMemory.Fill(param, 0, 0xA4);
        var acct = PsnAccount.Current;
        if (acct.SignedIn) WriteNpId(param, acct.OnlineId);
        return 0;
    }

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

    // ---------------------------------------------------------------- system requests (SceNpManager, unnamed)
    // The first-boot setup (initialsetup.self, FW 3.74) runs one when the user answers "Set Later" to "Do you want to use
    // your account on your PlayStation Vita system?" (0x81049482 there): 27112D01() prepares, E41932D4(SceUID *req, 0)
    // starts the request, a per-frame callback polls D1D191FB(req) until it is 2 (finished), then reads
    // 96534006(req, int *result) (negative: the setup shows an error) and calls FAEA7A73(); EB16514C(req) aborts it. All
    // were unimplemented, so the state never became 2 and the setup sat on "Please wait..." forever. The emulated
    // console has no server to ask: a request finishes at once with result 0.
    const int SYSREQ_FINISHED = 2;
    int _sysReqNext = 1;

    [Hle("sceNpManager_27112D01", Nid = 0x27112D01, Library = "SceNpManager")] int NpSysRequestPrepare() => 0;

    [Hle("sceNpManager_E41932D4", Nid = 0xE41932D4, Library = "SceNpManager")]
    int NpSysRequestStart(uint outReq, int arg)
    {
        if (outReq == 0) return ERR_INVALID_ARGUMENT;
        GuestMemory.W32(outReq, (uint)_sysReqNext++);
        return 0;
    }

    [Hle("sceNpManager_D1D191FB", Nid = 0xD1D191FB, Library = "SceNpManager")] int NpSysRequestState(int req) => SYSREQ_FINISHED;

    [Hle("sceNpManager_96534006", Nid = 0x96534006, Library = "SceNpManager")]
    int NpSysRequestResult(int req, uint result)
    {
        if (result != 0) GuestMemory.W32(result, 0);
        return 0;
    }

    [Hle("sceNpManager_EB16514C", Nid = 0xEB16514C, Library = "SceNpManager")] int NpSysRequestAbort(int req) => 0;

    [Hle("sceNpManager_FAEA7A73", Nid = 0xFAEA7A73, Library = "SceNpManager")] int NpSysRequestEnd() => 0;

    // Right after that request the setup's account-bind job (SceBindLibJobQueue, initialsetup 0x810490FE) runs a check:
    // h = D157DC12(3); 0F6AC47E(h, 0, 0, 3, int *value); 80036A44(h). A value other than 0 (or a negative return)
    // makes the setup say "You must update the system software to link your account" (unimplemented, the value was
    // whatever the stack held). The emulated system is up to date: the value is 0.
    [Hle("sceNpManager_D157DC12", Nid = 0xD157DC12, Library = "SceNpManager")] int NpBindCheckOpen(int type) => _sysReqNext++;

    [Hle("sceNpManager_0F6AC47E", Nid = 0x0F6AC47E, Library = "SceNpManager")]
    int NpBindCheckGetValue(int handle, int a, int b, int c, uint value)
    {
        if (value != 0) GuestMemory.W32(value, 0);
        return 0;
    }

    [Hle("sceNpManager_80036A44", Nid = 0x80036A44, Library = "SceNpManager")] int NpBindCheckClose(int handle) => 0;

    /// <summary>(char env[16]): the NP environment np_common keeps (set from the registry's /CONFIG/NP/env at start:
    /// "np", the production servers); copied with strncpy, always 0.</summary>
    [Hle]
    int sceNpCommonGetNpEnviroment(uint env)
    {
        if (env != 0) { GuestMemory.Fill(env, 0, 16); GuestMemory.WriteCString(env, "np", 16); }
        return 0;
    }

    /// <summary>(char buf[8]): the system software version as text, np_common.suprx FW 3.74: the buffer cleared, then
    /// snprintf(buf, 8, "%d.%02d") of the BCD version sceKernelGetSystemSwVersion reports ("3.74"); its length back.
    /// The PS4 Link's activity module asks for it.</summary>
    [Hle("sceNpCommon_0624AE44", Nid = 0x0624AE44, Library = "SceNpCommon")]
    int sceNpCommonGetSystemSwVersionString(uint buf)
    {
        uint v = SystemMisc.SystemSwVersion;
        string s = $"{(v >> 28) * 10 + ((v >> 24) & 0xF)}.{((v >> 20) & 0xF) * 10 + ((v >> 16) & 0xF):D2}";
        GuestMemory.Fill(buf, 0, 8);
        GuestMemory.WriteCString(buf, s, 8);
        return s.Length;
    }

    // ---------------------------------------------------------------- communication id text (SceNpCommon)
    // np_common.suprx FW 3.74 (the shell's trophy server names a title's folders this way): SceNpCommunicationId
    // (char data[9], term, uint8 num, dummy) <-> "NPWR12345_00".
    const int COMMON_NULL = unchecked((int)0x80550601), COMMON_SIZE = unchecked((int)0x80550602), COMMON_INVALID = unchecked((int)0x8055060A);

    /// <summary>(const SceNpCommunicationId *id, char *out, size_t size): size at least 13.</summary>
    [Hle("sceNpCommon_E426FEE8", Nid = 0xE426FEE8, Library = "SceNpCommon")]
    int NpCommonCommIdToString(uint id, uint str, uint size)
    {
        if (id == 0 || str == 0) return COMMON_NULL;
        if (size < 13) return COMMON_SIZE;
        byte num = GuestMemory.Read<byte>(id + 10);
        if (num > 99) return COMMON_INVALID;
        GuestMemory.Copy(str, id, 9);
        GuestMemory.WriteCString(str + 9, $"_{num:D2}", 4);
        return 0;
    }

    /// <summary>(SceNpCommunicationId *id, const char *str): "NPWR12345_00".</summary>
    [Hle("sceNpCommon_5BC54899", Nid = 0x5BC54899, Library = "SceNpCommon")]
    int NpCommonStringToCommId(uint id, uint str)
    {
        if (str == 0) return COMMON_NULL;
        string s = GuestMemory.ReadCString(str, 64);
        if (s.Length > 12 || s.Length < 10 || s[9] != '_') return COMMON_INVALID;
        GuestMemory.Fill(id, 0, 12);
        GuestMemory.Copy(id, str, 9);
        int n = 0;
        foreach (char c in s[10..]) { if (c is < '0' or > '9') break; n = n * 10 + (c - '0'); }
        GuestMemory.Write(id + 10, (byte)n);
        return 0;
    }

    // ---------------------------------------------------------------- the library heap (SceNpCommon)
    // vs0:sys/external/np_common.suprx (FW 3.74): the system software gives the library a memory block at start (the
    // shell's main initialisation: 0x14E000 bytes; a failure there ends that initialisation, and with it the services it
    // starts after, the trophy server among them). A385C8CD(a, b, size, mem) makes a mspace on it, BA6310A1 removes it,
    // and the allocator exports (named MallocNpServerName / FreeNpServerName in the NID database) allocate from it.

    uint _commonHeap;
    Clib ClibModule => Emu.Hle.Modules.OfType<Clib>().First();

    [Hle("sceNpCommon_A385C8CD", Nid = 0xA385C8CD, Library = "SceNpCommon")]
    int NpCommonHeapInit(uint a, uint b, uint size, uint mem)
    {
        if (size == 0 || mem == 0) return 0;
        _commonHeap = ClibModule.sceClibMspaceCreate(mem, size);
        return 0;
    }

    [Hle("sceNpCommon_BA6310A1", Nid = 0xBA6310A1, Library = "SceNpCommon")]
    void NpCommonHeapTerm()
    {
        if (_commonHeap != 0) ClibModule.sceClibMspaceDestroy(_commonHeap);
        _commonHeap = 0;
    }

    [Hle] uint sceNpCommonMallocNpServerName(uint size) => _commonHeap == 0 ? 0 : ClibModule.sceClibMspaceMalloc(_commonHeap, size);

    [Hle("sceNpCommon_50D71BB5", Nid = 0x50D71BB5, Library = "SceNpCommon")]
    uint NpCommonRealloc(uint ptr, uint size) => _commonHeap == 0 ? 0 : ClibModule.sceClibMspaceRealloc(_commonHeap, ptr, size);

    [Hle] void sceNpCommonFreeNpServerName(uint ptr) { if (_commonHeap != 0) ClibModule.sceClibMspaceFree(_commonHeap, ptr); }
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
public sealed partial class NpSocial : HleModule
{
    const int BASIC_INVALID_ARGUMENT = unchecked((int)0x80551D02),
        ERR_NOT_IN_PARTY = unchecked((int)0x80553402);

    // ---------------------------------------------------------------- SceNpBasic: no friends, nothing blocked

    int FriendQuery(uint friendId, uint outPtr) => friendId == 0 || outPtr == 0 ? BASIC_INVALID_ARGUMENT : PsnAccount.Current.ServerError;

    /// <summary>np_basic.suprx's system-side pair (FW 3.74) that near uses for a place's metadata: NID_6F1B58F9
    /// (const void *query, void *out, uint size) asks the server, which cannot be reached; NID_B414E768 ends the
    /// request and clears the library's request slot.</summary>
    [Hle("sceNpBasic_6F1B58F9", Nid = 0x6F1B58F9, Library = "SceNpBasic")]
    int sceNpBasicSysGetMetadata(uint query, uint outBuf, uint size) => query == 0 || outBuf == 0 ? BASIC_INVALID_ARGUMENT : PsnAccount.Current.ServerError;
    [Hle("sceNpBasic_B414E768", Nid = 0xB414E768, Library = "SceNpBasic")] int sceNpBasicSysEndRequest(uint p) => 0;
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
