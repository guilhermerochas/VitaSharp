using VitaSharp.Core.Hle.Np;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

// System-imported functions without public names of SceLibLocation, SceNpWebApi and SceNpCommon (FW 3.74
// liblocation, np_webapi and np_common read in their code, study only).

public sealed partial class Location
{
    internal const int ERR_NOT_INITIALIZED_INTERNAL = unchecked((int)0x80101241), ERR_NULL = unchecked((int)0x80101240),
        ERR_NO_PROVIDER = unchecked((int)0x8010124F), ERR_ALLOCATOR_SET = unchecked((int)0x8010128F);

    /// <summary>
    /// The state liblocation shares with liblocation_internal and liblocation_extension (its data, 3.74): the control
    /// block (guest pointer, set by the internal module), who set the allocator (0 nobody, 1 the module itself, 2 the
    /// internal module), the allocator's two functions, the extension's started flag and the provider state word
    /// (+0x70 of the control block). The HLE library keeps its own state host-side: no control block exists until
    /// a module hands one over.
    /// </summary>
    [SnapshotMissing]
    uint _controlBlock, _allocatorOwner, _allocAlloc, _allocFree, _providerState;

    [SnapshotMissing]
    bool _extensionStarted;

    /// <summary>NID_EFC9BD25 (SceLibLocation)(): the control block (0 while no module handed one: the extension's
    /// calls then answer 0x80101241).</summary>
    [Hle("NID_EFC9BD25", Nid = 0xEFC9BD25, Library = "SceLibLocation", Default = true)]
    uint LocationControlBlock()
    {
        return _controlBlock;
    }

    /// <summary>NID_7F74AA94 (SceLibLocation)(block): liblocation_internal hands over the control block; the allocator
    /// is then the internal module's (state 2).</summary>
    [Hle("NID_7F74AA94", Nid = 0x7F74AA94, Library = "SceLibLocation")]
    int LocationSetControlBlock(uint block)
    {
        _controlBlock = block;
        _allocatorOwner = 2;
        return 0;
    }

    /// <summary>NID_0C0BB66A (SceLibLocation)(): the address of the allocator pair (alloc, free). The HLE library
    /// keeps the pair host-side, so there is no address to give: 0 (a default).</summary>
    [Hle("NID_0C0BB66A", Nid = 0x0C0BB66A, Library = "SceLibLocation", Default = true)]
    uint LocationAllocatorAddress()
    {
        Log.InfoOnce("location-0C0BB66A", "SceLibLocation NID_0C0BB66A: the allocator pair is kept host-side (0)");
        return 0;
    }

    /// <summary>NID_822BBF06 (SceLibLocation)(const uint32 pair[2], bool force): sets the allocator when forced (the
    /// internal module: state 2) or while nobody set one; else 0x8010128F.</summary>
    [Hle("NID_822BBF06", Nid = 0x822BBF06, Library = "SceLibLocation")]
    int LocationSetAllocator(uint pair, byte force)
    {
        if (force != 1 && !(_allocatorOwner == 0 && force == 0))
        {
            return ERR_ALLOCATOR_SET;
        }

        if (pair != 0)
        {
            _allocAlloc = GuestMemory.R32(pair);
            _allocFree = GuestMemory.R32(pair + 4);
        }

        if (force == 1)
        {
            _allocatorOwner = 2;
        }

        return 0;
    }

    /// <summary>NID_991AF2D0 (SceLibLocation)(int *priority, int *affinity): the library thread's parameters
    /// (3.74 defaults: priority 0x10000100, affinity 0).</summary>
    [Hle("NID_991AF2D0", Nid = 0x991AF2D0, Library = "SceLibLocation")]
    int LocationThreadParameters(uint priority, uint affinity)
    {
        if (priority != 0)
        {
            GuestMemory.W32(priority, 0x10000100);
        }

        if (affinity != 0)
        {
            GuestMemory.W32(affinity, 0);
        }

        return 0;
    }

    /// <summary>NID_4C8B6F21 (SceLibLocation)(handle): the handle's object in the control block, when the provider is
    /// up; no control block here: 0 (the extension answers 0x80101241, a default).</summary>
    [Hle("NID_4C8B6F21", Nid = 0x4C8B6F21, Library = "SceLibLocation", Default = true)]
    uint LocationHandleObject(uint handle)
    {
        return 0;
    }

    /// <summary>NID_0341758B (SceLibLocation)(): the provider status of the control block (+100); no provider (0, a
    /// default).</summary>
    [Hle("NID_0341758B", Nid = 0x0341758B, Library = "SceLibLocation", Default = true)]
    uint LocationProviderStatus()
    {
        return _controlBlock == 0 ? 0 : GuestMemory.R32(_controlBlock + 100);
    }

    /// <summary>NID_5C7185D2 (SceLibLocation)(): the extension started (its module_start).</summary>
    [Hle("NID_5C7185D2", Nid = 0x5C7185D2, Library = "SceLibLocation")]
    int LocationExtensionStarted()
    {
        _extensionStarted = true;
        return 0;
    }

    /// <summary>NID_2311B24A (SceLibLocation)(): whether the extension started.</summary>
    [Hle("NID_2311B24A", Nid = 0x2311B24A, Library = "SceLibLocation")]
    int LocationIsExtensionStarted()
    {
        return _extensionStarted ? 1 : 0;
    }

    /// <summary>NID_41C1E68F (SceLibLocation)(int state): the extension's provider state (+0x70 of the control block;
    /// kept here when there is none).</summary>
    [Hle("NID_41C1E68F", Nid = 0x41C1E68F, Library = "SceLibLocation")]
    int LocationSetProviderState(uint state)
    {
        _providerState = state;
        if (_controlBlock != 0)
        {
            GuestMemory.W32(_controlBlock + 0x70, state);
        }

        return 0;
    }

    /// <summary>NID_C1CFEC92 (SceLibLocation)(void *request): the Near app's provider request. NULL 0x80101240; with no
    /// allocator set (no provider module) 0x8010124F; otherwise the request goes to the provider: there is no
    /// positioning provider, so it ends at once (a default).</summary>
    [Hle("NID_C1CFEC92", Nid = 0xC1CFEC92, Library = "SceLibLocation", Default = true)]
    int LocationProviderRequest(uint request)
    {
        if (request == 0)
        {
            return ERR_NULL;
        }

        if (_allocatorOwner == 0)
        {
            return ERR_NO_PROVIDER;
        }

        Log.InfoOnce("location-C1CFEC92", "SceLibLocation NID_C1CFEC92: no positioning provider (request ends)");
        return 0;
    }
}

public sealed partial class NpWebApi
{
    const int ERR_WEBAPI_CONTEXT = unchecked((int)0x80552904), ERR_WEBAPI_REQUEST = unchecked((int)0x80552905),
        ERR_WEBAPI_BUSY = unchecked((int)0x80552911), ERR_WEBAPI_NOT_SENT = unchecked((int)0x8055A602);

    /// <summary>np_webapi 3.74 numbers request ids by their library context (id &gt;&gt; 16); the requests this
    /// HLE created and the contexts it knows.</summary>
    [SnapshotMissing]
    readonly HashSet<int> _contexts = [];

    [SnapshotMissing]
    readonly HashSet<long> _requests = [];

    [SnapshotMissing]
    readonly HashSet<int> _suspended = [];

    int NewContext()
    {
        int id = NewId();
        lock (_parts)
        {
            _contexts.Add(id);
        }

        return id;
    }

    bool KnownContext(int libCtxId)
    {
        lock (_parts)
        {
            return _contexts.Contains(libCtxId);
        }
    }

    /// <summary>NID_C42F1E67 (SceNpWebApi)(int libCtxId): marks the context suspended (its requests are refused
    /// while the system is busy); an unknown context 0x80552904.</summary>
    [Hle("NID_C42F1E67", Nid = 0xC42F1E67, Library = "SceNpWebApi")]
    int NpWebApiSuspendContext(int libCtxId)
    {
        if (!KnownContext(libCtxId))
        {
            return ERR_WEBAPI_CONTEXT;
        }

        lock (_parts)
        {
            _suspended.Add(libCtxId);
        }

        return 0;
    }

    /// <summary>NID_B280EA3D (SceNpWebApi)(int libCtxId): resumes the context; an unknown context 0x80552904, one
    /// still used by another call 0x80552911 (calls finish at once here, so never).</summary>
    [Hle("NID_B280EA3D", Nid = 0xB280EA3D, Library = "SceNpWebApi")]
    int NpWebApiResumeContext(int libCtxId)
    {
        if (!KnownContext(libCtxId))
        {
            return ERR_WEBAPI_CONTEXT;
        }

        lock (_parts)
        {
            _suspended.Remove(libCtxId);
        }

        return 0;
    }

    /// <summary>NID_07913970 (SceNpWebApi)(int libCtxId, const char *name): removes a named entry of the context's
    /// user list (NULL 0x80552902); an unknown context 0x80552904. Nothing was ever registered: 0.</summary>
    [Hle("NID_07913970", Nid = 0x07913970, Library = "SceNpWebApi")]
    int NpWebApiRemoveNamed(int libCtxId, uint name)
    {
        if (!KnownContext(libCtxId))
        {
            return ERR_WEBAPI_CONTEXT;
        }

        return name == 0 ? ERR_WEBAPI_ARG : 0;
    }

    /// <summary>NID_CB51F139 (SceNpWebApi)(int64 requestId, int *status): the HTTP status of a request (NULL or a
    /// zero id 0x80552902; its context unknown 0x80552904; the request unknown 0x80552905). Requests are never sent
    /// here: no HTTP request behind them, 0x8055A602.</summary>
    [Hle("NID_CB51F139", Nid = 0xCB51F139, Library = "SceNpWebApi")]
    int NpWebApiRequestStatus(long requestId, uint status)
    {
        if (status == 0 || requestId == 0)
        {
            return ERR_WEBAPI_ARG;
        }

        lock (_parts)
        {
            return _requests.Contains(requestId) ? ERR_WEBAPI_NOT_SENT : ERR_WEBAPI_REQUEST;
        }
    }

    /// <summary>NID_4D7CC67C (SceNpWebApi)(int, void *out): np_common_ps4's helper fills the output (NULL 0x80552902);
    /// it is the PS4 link's local data, none here: zeroed 16 bytes, 0.</summary>
    [Hle("NID_4D7CC67C", Nid = 0x4D7CC67C, Library = "SceNpWebApi", Default = true)]
    int NpWebApiCommonOutput(int value, uint output)
    {
        if (output == 0)
        {
            return ERR_WEBAPI_ARG;
        }

        GuestMemory.Fill(output, 0, 16);
        return 0;
    }

    /// <summary>NID_66CEC1A6 (SceNpWebApi)(int libCtxId): the system's terminate: 0 0x80552902, an unknown context
    /// 0x80552904; the context and its requests go away.</summary>
    [Hle("NID_66CEC1A6", Nid = 0x66CEC1A6, Library = "SceNpWebApi")]
    int NpWebApiTerminateForSystem(int libCtxId)
    {
        if (libCtxId == 0)
        {
            return ERR_WEBAPI_ARG;
        }

        lock (_parts)
        {
            if (!_contexts.Remove(libCtxId))
            {
                return ERR_WEBAPI_CONTEXT;
            }

            _suspended.Remove(libCtxId);
            _requests.RemoveWhere(id => (int)(id >> 16) == libCtxId);
        }

        return 0;
    }

    /// <summary>NID_0D910413 (SceNpWebApi)(int libCtxId, const char *apiGroup, const char *path, int method, const
    /// content *content, int option, int64 *requestId): the system's create request with its content (0 context
    /// 0x80552902, unknown 0x80552904); the request is kept (it is never sent).</summary>
    [Hle("NID_0D910413", Nid = 0x0D910413, Library = "SceNpWebApi")]
    int NpWebApiCreateRequestForSystem(int libCtxId, uint apiGroup, uint path, int method, uint content, int option, uint requestId)
    {
        if (libCtxId == 0)
        {
            return ERR_WEBAPI_ARG;
        }

        if (!KnownContext(libCtxId))
        {
            return ERR_WEBAPI_CONTEXT;
        }

        if (requestId == 0)
        {
            return ERR_WEBAPI_ARG;
        }

        long id = ((long)libCtxId << 16) | (Interlocked.Increment(ref _nextReq) & 0xFFFF);
        lock (_parts)
        {
            _requests.Add(id);
        }

        GuestMemory.Write(requestId, id);
        return 0;
    }

    /// <summary>NID_6CDD45B6 / NID_1D427258 / NID_C359A88A (SceNpWebApi)(int libCtxId, int id): delete a context's
    /// push event objects (filter, service filter, callback); an unknown context 0x80552904. Nothing is kept for them
    /// here: 0.</summary>
    [Hle("NID_6CDD45B6", Nid = 0x6CDD45B6, Library = "SceNpWebApi")]
    int NpWebApiDeletePushObject(int libCtxId, int id) => KnownContext(libCtxId) ? 0 : ERR_WEBAPI_CONTEXT;

    [Hle("NID_1D427258", Nid = 0x1D427258, Library = "SceNpWebApi")]
    int NpWebApiDeleteServicePushObject(int libCtxId, int id) => KnownContext(libCtxId) ? 0 : ERR_WEBAPI_CONTEXT;

    [Hle("NID_C359A88A", Nid = 0xC359A88A, Library = "SceNpWebApi")]
    int NpWebApiDeleteExtdPushObject(int libCtxId, int id) => KnownContext(libCtxId) ? 0 : ERR_WEBAPI_CONTEXT;

    /// <summary>NID_FDCBD57D / NID_C18E6DAA (SceNpWebApi)(int64 requestId, value): a request's option (timeout, extra
    /// header mode) on the request of the context (id &gt;&gt; 16; unknown 0x80552904, request 0x80552905).</summary>
    [Hle("NID_FDCBD57D", Nid = 0xFDCBD57D, Library = "SceNpWebApi")]
    int NpWebApiSetRequestOption(long requestId, uint value) => RequestOption(requestId);

    [Hle("NID_C18E6DAA", Nid = 0xC18E6DAA, Library = "SceNpWebApi")]
    int NpWebApiSetRequestOption2(long requestId, uint value) => RequestOption(requestId);

    int RequestOption(long requestId)
    {
        if (!KnownContext((int)(requestId >> 16)))
        {
            return ERR_WEBAPI_CONTEXT;
        }

        lock (_parts)
        {
            return _requests.Contains(requestId) ? 0 : ERR_WEBAPI_REQUEST;
        }
    }

    /// <summary>NID_8D83A2D0 (SceNpWebApi)(): when the shell (NPXS19999) is the process, the library's services are
    /// set up again after a sign-in change. Nothing is cached here: 0.</summary>
    [Hle("NID_8D83A2D0", Nid = 0x8D83A2D0, Library = "SceNpWebApi")]
    int NpWebApiRefreshForShell() => 0;
}

public sealed partial class NpManager : IServiceThreadOwner
{
    const int NPC_INVALID_ARGUMENT = unchecked((int)0x80550303), NPC_TICKET_VERSION = unchecked((int)0x8055030C),
        NPC_TICKET_FORMAT = unchecked((int)0x80550603), NPC_TEXT_NULL = unchecked((int)0x80550601),
        NPC_TEXT_SPACE = unchecked((int)0x80550602), NPC_TEXT_EMPTY = unchecked((int)0x80550604),
        NPC_TEXT_JID = unchecked((int)0x80550605), CALLOUT_NOT_STARTED = unchecked((int)0x80558001),
        CALLOUT_STARTED = unchecked((int)0x80558002), CALLOUT_QUEUED = unchecked((int)0x80558006);

    /// <summary>The ticket tags whose length is 4 bytes (np_common 3.74's table; the others have 2).</summary>
    static bool LongTicketTag(int tag) => tag is 0x0000 or 0x3000 or 0x3010;

    /// <summary>
    /// NID_85503FA6 (SceNpCommon)(const void *ticket, uint size, ...): np_common's check of a ticket: NULL or less
    /// than 4 bytes 0x80550303; a version below 4 (the high nibble of the first byte) 0x8055030C; no body element
    /// (tag 0x3012) in the walk of its elements 0x80550603; then its first element must be a value (not empty, not
    /// a container without data), else 0x80550603.
    /// </summary>
    [Hle("NID_85503FA6", Nid = 0x85503FA6, Library = "SceNpCommon")]
    int NpCommonTicketCheck(uint ticket, uint size, uint a, uint b)
    {
        if (ticket == 0 || size < 4)
        {
            return NPC_INVALID_ARGUMENT;
        }

        ReadOnlySpan<byte> data = GuestMemory.Span(ticket, (int)size);
        if (data[0] >> 4 < 4)
        {
            return NPC_TICKET_VERSION;
        }

        int position = 4;
        while (true)
        {
            if (position + 2 >= data.Length)
            {
                return NPC_TICKET_FORMAT;
            }

            int tag = data[position] << 8 | data[position + 1];
            int header = LongTicketTag(tag) ? 6 : 4;
            if (position + header > data.Length)
            {
                return NPC_TICKET_FORMAT;
            }

            int length = header == 6
                ? data[position + 2] << 24 | data[position + 3] << 16 | data[position + 4] << 8 | data[position + 5]
                : data[position + 2] << 8 | data[position + 3];
            if (tag == 0x3012)
            {
                position += header;
                break;
            }

            position += header;
            int kind = tag & 0xF000;
            if ((kind == 0 && tag != 0) || (kind == 0x3000 && (tag & 0x1FFF) >> 12 != 0))
            {
                position += length;
                if (position > data.Length)
                {
                    return NPC_TICKET_FORMAT;
                }
            }
        }

        if (position + 4 > data.Length)
        {
            return NPC_TICKET_FORMAT;
        }

        int first = data[position] << 8 | data[position + 1];
        if (first == 0 || ((first & 0xF000) == 0x3000 && (first & 0x10) == 0))
        {
            return NPC_TICKET_FORMAT;
        }

        return 0;
    }

    // ---- the callout list: a context (0x18 bytes: thread, mutex, condition, started, stopping, first item) whose
    // items (0x18 bytes: next, function, due time (process time, us), argument) are called from its thread in order

    [SnapshotMissing]
    uint _netCallout;

    /// <summary>The started callout contexts and the names of their threads (the state of <see cref="_calloutThreads"/>;
    /// a state of a build before it has none: null, the running session's are kept).</summary>
    [SnapshotMissing]
    Dictionary<uint, string>? _calloutNames = [];

    readonly Dictionary<uint, HleCallbackThread> _calloutThreads = [];

    /// <summary>Tests: receives the due callouts instead of the library's thread.</summary>
    internal Action<uint, uint>? CalloutSink;

    /// <summary>NID_9AEF645B (SceNpCommon)(context, name, stackSize, priority, affinity): starts a callout context
    /// (its thread waits for the first due item); a started one 0x80558002.</summary>
    [Hle("NID_9AEF645B", Nid = 0x9AEF645B, Library = "SceNpCommon")]
    int NpCommonCalloutStart(uint context, uint name, uint stackSize, int priority, int affinity)
    {
        if ((int)GuestMemory.R32(context + 0xC) >= 1)
        {
            return CALLOUT_STARTED;
        }

        GuestMemory.W32(context + 0x10, 0);
        GuestMemory.W32(context + 0x14, 0);
        GuestMemory.W32(context + 0xC, 1);
        string threadName = name == 0 ? "SceNpCallout" : GuestMemory.ReadCString(name, 32);
        lock (_calloutThreads)
        {
            _calloutThreads[context] = CalloutThread(context, threadName);
            (_calloutNames ??= [])[context] = threadName;
        }

        return 0;
    }

    /// <summary>NID_48314C6E (SceNpCommon)(context): stops a started callout context (its pending items are dropped
    /// with the thread).</summary>
    [Hle("NID_48314C6E", Nid = 0x48314C6E, Library = "SceNpCommon")]
    int NpCommonCalloutStop(uint context)
    {
        if (GuestMemory.R32(context + 0xC) == 0)
        {
            return 0;
        }

        GuestMemory.W32(context + 0x10, 1);
        GuestMemory.W32(context + 0xC, 0);
        lock (_calloutThreads)
        {
            if (_calloutThreads.Remove(context, out HleCallbackThread? thread))
            {
                thread.Close();
            }

            _calloutNames?.Remove(context);
        }

        return 0;
    }

    /// <summary>NID_40724BEA (SceNpCommon)(context, item, uint64 delayUs, function, argument): queues the item, due
    /// after the delay, in time order; not started 0x80558001, the item already queued 0x80558006.</summary>
    [Hle("NID_40724BEA", Nid = 0x40724BEA, Library = "SceNpCommon")]
    int NpCommonCalloutQueue(uint context, uint item, ulong delay, uint function, uint argument)
    {
        if (GuestMemory.R32(context + 0xC) == 0)
        {
            return CALLOUT_NOT_STARTED;
        }

        for (uint node = GuestMemory.R32(context + 0x14); node != 0; node = GuestMemory.R32(node))
        {
            if (node == item)
            {
                return CALLOUT_QUEUED;
            }
        }

        GuestMemory.Fill(item, 0, 0x18);
        GuestMemory.W32(item + 4, function);
        GuestMemory.W32(item + 0x10, argument);
        ulong due = Now() + delay;
        GuestMemory.Write(item + 8, due);
        uint previous = 0, next = GuestMemory.R32(context + 0x14);
        while (next != 0 && GuestMemory.Read<ulong>(next + 8) < due)
        {
            previous = next;
            next = GuestMemory.R32(next);
        }

        GuestMemory.W32(item, next);
        GuestMemory.W32(previous == 0 ? context + 0x14 : previous, item);
        return 0;
    }

    /// <summary>NID_BDCD318E (SceNpCommon)(context, item, int *removed): takes the item off the queue (*removed 1, or 0
    /// when it was not queued); not started 0x80558001.</summary>
    [Hle("NID_BDCD318E", Nid = 0xBDCD318E, Library = "SceNpCommon")]
    int NpCommonCalloutCancel(uint context, uint item, uint removed)
    {
        if (GuestMemory.R32(context + 0xC) == 0)
        {
            return CALLOUT_NOT_STARTED;
        }

        uint previous = 0, node = GuestMemory.R32(context + 0x14);
        while (node != 0 && node != item)
        {
            previous = node;
            node = GuestMemory.R32(node);
        }

        if (node != 0)
        {
            GuestMemory.W32(previous == 0 ? context + 0x14 : previous, GuestMemory.R32(item));
        }

        if (removed != 0)
        {
            GuestMemory.W32(removed, node != 0 ? 1u : 0u);
        }

        return 0;
    }

    /// <summary>Calls every item due at <paramref name="now"/> (process time, us) in time order, each one taken off
    /// the queue first, as the callout thread does.</summary>
    internal int DeliverDueCallouts(uint context, ulong now)
    {
        int delivered = 0;
        while (GuestMemory.R32(context + 0xC) != 0)
        {
            uint item = GuestMemory.R32(context + 0x14);
            if (item == 0 || now < GuestMemory.Read<ulong>(item + 8))
            {
                break;
            }

            GuestMemory.W32(context + 0x14, GuestMemory.R32(item));
            uint function = GuestMemory.R32(item + 4), argument = GuestMemory.R32(item + 0x10);
            delivered++;
            if (CalloutSink != null)
            {
                CalloutSink(function, argument);
                continue;
            }

            HleCallbackThread? thread;
            lock (_calloutThreads)
            {
                _calloutThreads.TryGetValue(context, out thread);
            }

            thread?.Post(Emu, 0x10000100, function, [argument]);
        }

        return delivered;
    }

    ulong Now() => Emu?.Kernel.ProcessTimeUs ?? 0;

    /// <summary>A callout context's thread: the context's first word is its UID once it is made (the firmware's layout),
    /// which is how a save state knows which context a saved thread serves.</summary>
    static HleCallbackThread CalloutThread(uint context, string threadName)
    {
        return new HleCallbackThread(threadName) { Created = thread => GuestMemory.W32(context, (uint)thread.Uid) };
    }

    /// <summary>
    /// Save states: the callout threads follow the loaded contexts. A running thread the state has too (kept under its
    /// saved UID, which the context's first word holds) stays with the context that names it; a context of the state
    /// whose thread was not running here gets its library thread again (its saved thread is given back by
    /// <see cref="IServiceThreadOwner.AdoptServiceThread"/>, or a new one is made with its next callout); the contexts
    /// the state does not have end their threads. The running session's queued calls are dropped.
    /// </summary>
    void AfterSnapshotLoad()
    {
        lock (_calloutThreads)
        {
            if (_calloutNames == null)
            {
                // a state of a build before the callout threads were saved: the running session's stay
                _calloutNames = [];
                foreach (KeyValuePair<uint, HleCallbackThread> entry in _calloutThreads)
                {
                    _calloutNames[entry.Key] = entry.Value.Name;
                }

                return;
            }

            var byThread = new Dictionary<int, HleCallbackThread>();
            foreach (HleCallbackThread running in _calloutThreads.Values)
            {
                running.DropPending();
                if (running.Thread is { IsLive: true } thread)
                {
                    byThread[thread.Uid] = running;
                }
                else
                {
                    running.Close();
                }
            }

            _calloutThreads.Clear();
            foreach (KeyValuePair<uint, string> entry in _calloutNames)
            {
                uint context = entry.Key;
                if (byThread.Remove((int)GuestMemory.R32(context), out HleCallbackThread? kept))
                {
                    kept.Created = CalloutThread(context, entry.Value).Created;
                    _calloutThreads[context] = kept;
                    continue;
                }

                _calloutThreads[context] = CalloutThread(context, entry.Value);
            }

            foreach (HleCallbackThread unused in byThread.Values)
            {
                if (unused.Thread is { } thread)
                {
                    thread.RequestStop();
                }

                unused.Close();
            }
        }
    }

    /// <summary>Save states: the saved thread of a loaded callout context (the one its first word names), while this
    /// session runs none for it, is its library thread again.</summary>
    bool IServiceThreadOwner.AdoptServiceThread(GuestThread thread)
    {
        if (!ReferenceEquals(thread.Process, Emu))
        {
            return false;
        }

        lock (_calloutThreads)
        {
            foreach (KeyValuePair<uint, HleCallbackThread> entry in _calloutThreads)
            {
                if (GuestMemory.R32(entry.Key) == (uint)thread.Uid && entry.Value.Name == thread.Name)
                {
                    return entry.Value.Adopt(Emu, thread);
                }
            }
        }

        return false;
    }

    /// <summary>Save states: a callout thread the loaded state does not have is let go.</summary>
    bool IServiceThreadOwner.ReleaseServiceThread(GuestThread thread)
    {
        lock (_calloutThreads)
        {
            foreach (HleCallbackThread callout in _calloutThreads.Values)
            {
                if (callout.Release(thread))
                {
                    return true;
                }
            }
        }

        return false;
    }

    uint NetCallout()
    {
        if (_netCallout == 0)
        {
            _netCallout = Emu.Memory.Alloc(0x18, "SceNetCallout");
            GuestMemory.Fill(_netCallout, 0, 0x18);
        }

        return _netCallout;
    }

    /// <summary>NID_A7EC5CDB (SceNpCommon)(stackSize, priority): starts the library's own callout context
    /// ("SceNetCallout").</summary>
    [Hle("NID_A7EC5CDB", Nid = 0xA7EC5CDB, Library = "SceNpCommon")]
    int NpCommonNetCalloutStart(uint stackSize, int priority) => NpCommonCalloutStartThroughEntry(NetCallout(), 0, stackSize, priority, 0);

    /// <summary>NID_E2743C07 (SceNpCommon)(): stops the library's callout context.</summary>
    [Hle("NID_E2743C07", Nid = 0xE2743C07, Library = "SceNpCommon")]
    int NpCommonNetCalloutStop() => _netCallout == 0 ? 0 : NpCommonCalloutStopThroughEntry(_netCallout);

    /// <summary>NID_659ED07E (SceNpCommon)(): the library's callout context while it runs, else NULL.</summary>
    [Hle("NID_659ED07E", Nid = 0x659ED07E, Library = "SceNpCommon")]
    uint NpCommonNetCallout() => _netCallout != 0 && GuestMemory.R32(_netCallout + 0xC) != 0 ? _netCallout : 0;

    /// <summary>NID_540B307E (SceNpCommon)(item, unused, uint64 delayUs, function, argument): queues on the library's
    /// callout context.</summary>
    [Hle("NID_540B307E", Nid = 0x540B307E, Library = "SceNpCommon")]
    int NpCommonNetCalloutQueue(uint item, uint unused, ulong delay, uint function, uint argument) =>
        NpCommonCalloutQueueThroughEntry(NetCallout(), item, delay, function, argument);

    /// <summary>NID_38B88BA3 (SceNpCommon)(item, int *removed): cancels on the library's callout context.</summary>
    [Hle("NID_38B88BA3", Nid = 0x38B88BA3, Library = "SceNpCommon")]
    int NpCommonNetCalloutCancel(uint item, uint removed) => NpCommonCalloutCancelThroughEntry(NetCallout(), item, removed);

    // ---- JIDs ("user@domain/resource") and NP ids as JIDs

    static bool JidCharacter(byte c) => char.IsAsciiLetterOrDigit((char)c);

    /// <summary>
    /// NID_D2B9BCA8 (SceNpCommon)(parts *out, const char *jid, char *buffer, uint size, uint *needed): splits a JID
    /// into node, domain and resource pointers (into <paramref name="buffer"/>). NULL jid 0x80550601 (also with no
    /// buffer and no size output); only spaces 0x80550604; a buffer too small 0x80550602. The node is the leading
    /// [A-Za-z0-9+-_.] run before an '@'; the domain the following [A-Za-z0-9._-] run less trailing dots; the
    /// resource the rest after one separator.
    /// </summary>
    [Hle("NID_D2B9BCA8", Nid = 0xD2B9BCA8, Library = "SceNpCommon")]
    int NpCommonJidParse(uint parts, uint jid, uint buffer, uint size, uint needed)
    {
        if (jid == 0)
        {
            return NPC_TEXT_NULL;
        }

        bool write = buffer != 0 && parts != 0;
        if (!write && needed == 0)
        {
            return NPC_TEXT_NULL;
        }

        if (write)
        {
            GuestMemory.Fill(parts, 0, 0x10);
        }

        byte[] text = System.Text.Encoding.Latin1.GetBytes(GuestMemory.ReadCString(jid, 0x10000));
        int start = 0;
        while (start < text.Length && text[start] == ' ')
        {
            start++;
        }

        if (start == text.Length)
        {
            return NPC_TEXT_EMPTY;
        }

        int nodeLength = 0;
        while (start + nodeLength < text.Length && (JidCharacter(text[start + nodeLength]) || text[start + nodeLength] is (byte)'+' or (byte)'-' or (byte)'_' or (byte)'.'))
        {
            nodeLength++;
        }

        bool hasNode = nodeLength > 0 && start + nodeLength < text.Length && text[start + nodeLength] == '@';
        if (!hasNode)
        {
            nodeLength = 0;
        }

        int domainStart = start + nodeLength + (hasNode ? 1 : 0);
        int domainLength = 0;
        while (domainStart + domainLength < text.Length && (JidCharacter(text[domainStart + domainLength]) || text[domainStart + domainLength] is (byte)'.' or (byte)'_' or (byte)'-'))
        {
            domainLength++;
        }

        int domainEnd = domainStart + domainLength;
        while (domainLength > 0 && text[domainStart + domainLength - 1] == '.')
        {
            domainLength--;
        }

        int resourceStart = domainEnd < text.Length ? domainEnd + 1 : domainEnd;
        int resourceLength = 0;
        while (resourceStart + resourceLength < text.Length && text[resourceStart + resourceLength] != 0)
        {
            resourceLength++;
        }

        uint total = (uint)(nodeLength + 1 + domainLength + 1 + resourceLength + 1);
        if (write)
        {
            if (total > size)
            {
                GuestMemory.Fill(parts, 0, 0x10);
                return NPC_TEXT_SPACE;
            }

            uint cursor = buffer;
            cursor = WritePart(parts, 0, cursor, text.AsSpan(start, nodeLength));
            cursor = WritePart(parts, 4, cursor, text.AsSpan(domainStart, domainLength));
            WritePart(parts, 8, cursor, text.AsSpan(resourceStart, resourceLength));
        }

        if (needed != 0)
        {
            GuestMemory.W32(needed, total);
        }

        return 0;
    }

    static uint WritePart(uint parts, uint slot, uint cursor, ReadOnlySpan<byte> part)
    {
        GuestMemory.W32(parts + slot, cursor);
        part.CopyTo(GuestMemory.Span(cursor, part.Length));
        GuestMemory.Write<byte>(cursor + (uint)part.Length, 0);
        return cursor + (uint)part.Length + 1;
    }

    /// <summary>NID_85699AE2 (SceNpCommon)(const parts *jid, char *out, uint size, uint *needed): joins node@domain/
    /// resource (node and resource optional); no domain 0x80550604; a buffer too small 0x80550602.</summary>
    [Hle("NID_85699AE2", Nid = 0x85699AE2, Library = "SceNpCommon")]
    int NpCommonJidJoin(uint parts, uint output, uint size, uint needed)
    {
        if (parts == 0 || GuestMemory.R32(parts + 4) == 0 || GuestMemory.Read<byte>(GuestMemory.R32(parts + 4)) == 0)
        {
            return NPC_TEXT_EMPTY;
        }

        string node = PartText(parts, 0), domain = PartText(parts, 4), resource = PartText(parts, 8);
        string joined = (node.Length > 0 ? node + "@" : "") + domain + (resource.Length > 0 ? "/" + resource : "");
        uint length = (uint)joined.Length + 1;
        if (needed != 0)
        {
            GuestMemory.W32(needed, length);
        }

        if (output == 0)
        {
            return 0;
        }

        if (size < length)
        {
            return NPC_TEXT_SPACE;
        }

        GuestMemory.WriteCString(output, joined, (int)length);
        return 0;
    }

    static string PartText(uint parts, uint slot)
    {
        uint text = GuestMemory.R32(parts + slot);
        return text == 0 ? "" : GuestMemory.ReadCString(text, 0x10000);
    }

    /// <summary>
    /// NID_9B7CAA67 (SceNpCommon)(SceNpId *npId, const parts *jid): an NP id from a PSN JID ("online@xx.yy.env.
    /// playstation.net/res"): NULL 0x80550601; a node missing or over 16 bytes, a domain not of two letters/digits,
    /// a dot, two more, a dot, then an environment and ".playstation.net", or a resource over 4 bytes 0x80550604.
    /// The id gets the handle, the four domain characters at +0x14 and the resource at +0x18, +0x1C = 1.
    /// </summary>
    [Hle("NID_9B7CAA67", Nid = 0x9B7CAA67, Library = "SceNpCommon")]
    int NpCommonNpIdFromJid(uint npId, uint parts)
    {
        if (parts == 0 || npId == 0)
        {
            return NPC_TEXT_NULL;
        }

        if (GuestMemory.R32(parts) == 0 || GuestMemory.R32(parts + 4) == 0)
        {
            return NPC_TEXT_EMPTY;
        }

        string node = PartText(parts, 0), domain = PartText(parts, 4);
        if (node.Length is 0 or > 16)
        {
            return NPC_TEXT_EMPTY;
        }

        if (domain.Length < 7 || !char.IsAsciiLetterOrDigit(domain[0]) || !char.IsAsciiLetterOrDigit(domain[1]) || domain[2] != '.'
            || !char.IsAsciiLetterOrDigit(domain[3]) || !char.IsAsciiLetterOrDigit(domain[4]) || domain[5] != '.')
        {
            return NPC_TEXT_EMPTY;
        }

        int environmentEnd = domain.IndexOf('.', 6);
        if (environmentEnd < 0 || domain[(environmentEnd + 1)..] != "playstation.net")
        {
            return NPC_TEXT_EMPTY;
        }

        string resource = GuestMemory.R32(parts + 8) != 0 ? PartText(parts, 8) : "";
        if (resource.Length > 4)
        {
            return NPC_TEXT_EMPTY;
        }

        GuestMemory.Fill(npId, 0, 0x24);
        GuestMemory.WriteCString(npId, node, 17);
        GuestMemory.Write<byte>(npId + 0x14, (byte)domain[0]);
        GuestMemory.Write<byte>(npId + 0x15, (byte)domain[1]);
        GuestMemory.Write<byte>(npId + 0x16, (byte)domain[3]);
        GuestMemory.Write<byte>(npId + 0x17, (byte)domain[4]);
        for (int i = 0; i < resource.Length; i++)
        {
            GuestMemory.Write<byte>(npId + 0x18 + (uint)i, (byte)resource[i]);
        }

        GuestMemory.Write<byte>(npId + 0x1C, 1);
        return 0;
    }

    /// <summary>The PSN environment of the JIDs np_common 3.74 builds ("rc").</summary>
    const string JidEnvironment = "rc";

    /// <summary>
    /// NID_40CC4D15 (SceNpCommon)(parts *out, const SceNpId *npId, char *buffer, uint size, uint *needed): the JID of
    /// an NP id ("online@xx.yy.rc.playstation.net/res"): NULL id 0x80550601 (also no buffer and no size output); an
    /// id not marked as from a JID (+0x1C != 1), with an empty handle or a domain character not a letter/digit
    /// 0x80550605; a buffer too small 0x80550602.
    /// </summary>
    [Hle("NID_40CC4D15", Nid = 0x40CC4D15, Library = "SceNpCommon")]
    int NpCommonJidFromNpId(uint parts, uint npId, uint buffer, uint size, uint needed)
    {
        if (npId == 0)
        {
            return NPC_TEXT_NULL;
        }

        if (GuestMemory.Read<byte>(npId + 0x1C) != 1)
        {
            return NPC_TEXT_JID;
        }

        bool write = buffer != 0 && parts != 0;
        if (!write && needed == 0)
        {
            return NPC_TEXT_NULL;
        }

        if (write)
        {
            GuestMemory.Fill(parts, 0, 0x10);
        }

        string node = GuestMemory.ReadCString(npId, 0x10000);
        if (node.Length == 0)
        {
            return NPC_TEXT_JID;
        }

        Span<byte> domainCharacters = GuestMemory.Span(npId + 0x14, 4);
        foreach (byte c in domainCharacters)
        {
            if (!JidCharacter(c))
            {
                return NPC_TEXT_JID;
            }
        }

        ReadOnlySpan<byte> resourceBytes = GuestMemory.Span(npId + 0x18, 4);
        int resourceLength = resourceBytes.IndexOf((byte)0);
        resourceLength = resourceLength < 0 ? 4 : resourceLength;
        string domain = $"{(char)domainCharacters[0]}{(char)domainCharacters[1]}.{(char)domainCharacters[2]}{(char)domainCharacters[3]}.{JidEnvironment}.playstation.net";
        uint total = (uint)(JidEnvironment.Length + node.Length + resourceLength + 0x19);
        if (needed != 0)
        {
            GuestMemory.W32(needed, total);
        }

        if (!write)
        {
            return 0;
        }

        if (size < total)
        {
            return NPC_TEXT_SPACE;
        }

        uint cursor = WritePart(parts, 0, buffer, System.Text.Encoding.Latin1.GetBytes(node));
        cursor = WritePart(parts, 4, cursor, System.Text.Encoding.Latin1.GetBytes(domain));
        WritePart(parts, 8, cursor, resourceBytes[..resourceLength]);
        if (needed != 0)
        {
            GuestMemory.W32(needed, (uint)node.Length + 10);
        }

        return 0;
    }
}
