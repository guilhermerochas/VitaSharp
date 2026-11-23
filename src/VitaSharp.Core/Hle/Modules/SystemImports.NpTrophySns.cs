using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

// System-imported functions without public names of SceNpTrophy and SceNpSnsFacebook (FW 3.74 np_trophy and
// np_sns_facebook read in their code, study only).

public sealed partial class NpTrophy
{
    const int ERR_SYSTEM_HANDLE_BUSY = unchecked((int)0x80551607);

    /// <summary>The system handles and contexts (np_trophy 3.74 keeps them in the same tables as the game's, type 1).</summary>
    int SystemCheck(int handle, bool argumentsValid)
    {
        if (!_initialized)
        {
            return ERR_NOT_INITIALIZED;
        }

        if (handle == -1 || !argumentsValid)
        {
            return ERR_INVALID_ARGUMENT;
        }

        lock (_handles)
        {
            return _handles.Contains(handle) ? 0 : ERR_INVALID_HANDLE;
        }
    }

    int SystemCheck(int handle, int context, bool argumentsValid)
    {
        if (!_initialized)
        {
            return ERR_NOT_INITIALIZED;
        }

        if (handle == -1 || context == -1 || !argumentsValid)
        {
            return ERR_INVALID_ARGUMENT;
        }

        lock (_handles)
        {
            if (!_handles.Contains(handle))
            {
                return ERR_INVALID_HANDLE;
            }
        }

        lock (_contexts)
        {
            return _contexts.ContainsKey(context) ? 0 : ERR_INVALID_CONTEXT;
        }
    }

    /// <summary>The trophy service's answer to the system's requests: the emulated shell keeps the trophies the
    /// HLE library records, it has no system-side request queue: the request is accepted (a default).</summary>
    int SystemRequest(string nid, int result)
    {
        if (result != 0)
        {
            return result;
        }

        Log.InfoOnce("trophy-" + nid, $"SceNpTrophy NID_{nid}: system trophy service request accepted (no system-side queue)");
        return 0;
    }

    /// <summary>NID_17FB1D9E (SceNpTrophy)(int *handle): a system handle (type 1); NULL 0x80551604.</summary>
    [Hle("NID_17FB1D9E", Nid = 0x17FB1D9E, Library = "SceNpTrophy")]
    int NpTrophyCreateSystemHandle(uint handle)
    {
        if (!_initialized)
        {
            return ERR_NOT_INITIALIZED;
        }

        if (handle == 0)
        {
            return ERR_INVALID_ARGUMENT;
        }

        GuestMemory.W32(handle, 0xFFFFFFFF);
        return sceNpTrophyCreateHandleThroughEntry(new Ptr<int>(handle));
    }

    /// <summary>NID_44E0F73E (SceNpTrophy)(int handle): destroys a system handle (-1 0x80551604, unknown
    /// 0x80551608).</summary>
    [Hle("NID_44E0F73E", Nid = 0x44E0F73E, Library = "SceNpTrophy")]
    int NpTrophyDestroySystemHandle(int handle)
    {
        int result = SystemCheck(handle, true);
        return result != 0 ? result : sceNpTrophyDestroyHandleThroughEntry(handle);
    }

    /// <summary>NID_FD34F7CF (SceNpTrophy)(int handle): aborts a system handle's request (-1 0x80551604, unknown
    /// 0x80551608).</summary>
    [Hle("NID_FD34F7CF", Nid = 0xFD34F7CF, Library = "SceNpTrophy")]
    int NpTrophyAbortSystemHandle(int handle)
    {
        int result = SystemCheck(handle, true);
        return result != 0 ? result : sceNpTrophyAbortHandleThroughEntry(handle);
    }

    /// <summary>NID_C80794CF (SceNpTrophy)(int *context, const SceNpCommunicationId *commId, uint64 options): a system
    /// context (type 1) for a communication id; NULL pointers or options other than 0 0x80551604; the set must be
    /// installed (0x80551619).</summary>
    [Hle("NID_C80794CF", Nid = 0xC80794CF, Library = "SceNpTrophy")]
    int NpTrophyCreateSystemContext(uint context, uint commId, ulong options)
    {
        if (!_initialized)
        {
            return ERR_NOT_INITIALIZED;
        }

        if (context == 0 || commId == 0 || options != 0)
        {
            return ERR_INVALID_ARGUMENT;
        }

        GuestMemory.W32(context, 0xFFFFFFFF);
        return sceNpTrophyCreateContextThroughEntry(new Ptr<int>(context), commId, 0, 0);
    }

    /// <summary>NID_10371F25 (SceNpTrophy)(int context): destroys a system context (-1 0x80551604, unknown
    /// 0x80551609).</summary>
    [Hle("NID_10371F25", Nid = 0x10371F25, Library = "SceNpTrophy")]
    int NpTrophyDestroySystemContext(int context)
    {
        if (!_initialized)
        {
            return ERR_NOT_INITIALIZED;
        }

        if (context == -1)
        {
            return ERR_INVALID_ARGUMENT;
        }

        return sceNpTrophyDestroyContextThroughEntry(context);
    }

    /// <summary>NID_97D19AB6 (SceNpTrophy)(void *request): releases a system request block (NULL 0x80551604).</summary>
    [Hle("NID_97D19AB6", Nid = 0x97D19AB6, Library = "SceNpTrophy")]
    int NpTrophyReleaseSystemRequest(uint request)
    {
        if (!_initialized)
        {
            return ERR_NOT_INITIALIZED;
        }

        return request == 0 ? ERR_INVALID_ARGUMENT : 0;
    }

    /// <summary>NID_10182F1B / NID_2B0CC831 / NID_3AC04020 / NID_62E37599 (SceNpTrophy)(int handle, void *a, int
    /// context, void *b): the setup dialog's requests on a context (install steps): a -1 handle or context or a
    /// NULL pointer 0x80551604, an unknown handle 0x80551608, context 0x80551609; then the trophy service.</summary>
    [Hle("NID_10182F1B", Nid = 0x10182F1B, Library = "SceNpTrophy", Default = true)]
    int NpTrophySystemRequest10182F1B(int handle, uint a, int context, uint b) => SystemRequest("10182F1B", SystemCheck(handle, context, a != 0 && b != 0));

    [Hle("NID_2B0CC831", Nid = 0x2B0CC831, Library = "SceNpTrophy", Default = true)]
    int NpTrophySystemRequest2B0CC831(int handle, uint a, int context, uint b) => SystemRequest("2B0CC831", SystemCheck(handle, context, a != 0 && b != 0));

    [Hle("NID_3AC04020", Nid = 0x3AC04020, Library = "SceNpTrophy", Default = true)]
    int NpTrophySystemRequest3AC04020(int handle, uint a, int context, uint b) => SystemRequest("3AC04020", SystemCheck(handle, context, a != 0 && b != 0));

    [Hle("NID_62E37599", Nid = 0x62E37599, Library = "SceNpTrophy", Default = true)]
    int NpTrophySystemRequest62E37599(int handle, uint a, int context, uint b) => SystemRequest("62E37599", SystemCheck(handle, context, a != 0 && b != 0));

    /// <summary>NID_2EE76D88 (SceNpTrophy)(handle, a, context, b, c, d): the same with three more pointers.</summary>
    [Hle("NID_2EE76D88", Nid = 0x2EE76D88, Library = "SceNpTrophy", Default = true)]
    int NpTrophySystemRequest2EE76D88(int handle, uint a, int context, uint b, uint c, uint d) =>
        SystemRequest("2EE76D88", SystemCheck(handle, context, a != 0 && b != 0 && c != 0 && d != 0));

    /// <summary>NID_C338E582 (SceNpTrophy)(handle, a, context): a request on a context with one pointer.</summary>
    [Hle("NID_C338E582", Nid = 0xC338E582, Library = "SceNpTrophy", Default = true)]
    int NpTrophySystemRequestC338E582(int handle, uint a, int context) => SystemRequest("C338E582", SystemCheck(handle, context, a != 0));

    /// <summary>NID_061DE3F5 (SceNpTrophy)(handle, a, b, c, d) and NID_0AC3CEA8 (handle, a, b): the setup dialog's
    /// handle requests (the second is the install of a set); NULL pointers 0x80551604, unknown handle 0x80551608.</summary>
    [Hle("NID_061DE3F5", Nid = 0x061DE3F5, Library = "SceNpTrophy", Default = true)]
    int NpTrophySystemRequest061DE3F5(int handle, uint a, uint b, uint c, uint d) => SystemRequest("061DE3F5", SystemCheck(handle, a != 0 && b != 0 && c != 0 && d != 0));

    [Hle("NID_0AC3CEA8", Nid = 0x0AC3CEA8, Library = "SceNpTrophy", Default = true)]
    int NpTrophySystemRequest0AC3CEA8(int handle, uint a, uint b) => SystemRequest("0AC3CEA8", SystemCheck(handle, a != 0 && b != 0));

    /// <summary>NID_CCDEB18B (SceNpTrophy)(handle, a): the service request 0x210002 on a handle.</summary>
    [Hle("NID_CCDEB18B", Nid = 0xCCDEB18B, Library = "SceNpTrophy", Default = true)]
    int NpTrophySystemRequestCCDEB18B(int handle, uint a) => SystemRequest("CCDEB18B", SystemCheck(handle, a != 0));

    /// <summary>NID_D70DAFA7 (SceNpTrophy)(handle, uint flags, a, b, uint64 options, void *out): a handle request with
    /// flags; a -1 handle or NULL output 0x80551604, options other than 0 0x80551604.</summary>
    [Hle("NID_D70DAFA7", Nid = 0xD70DAFA7, Library = "SceNpTrophy", Default = true)]
    int NpTrophySystemRequestD70DAFA7(int handle, uint flags, uint a, uint b, ulong options, uint output) =>
        SystemRequest("D70DAFA7", SystemCheck(handle, output != 0 && options == 0));
}

public sealed partial class NpSnsFacebook
{
    internal const int FB_ALREADY_INITIALIZED = unchecked((int)0x80559401), FB_NOT_INITIALIZED = unchecked((int)0x80559402),
        FB_INVALID_ARGUMENT = unchecked((int)0x80559403), FB_NO_ENTRY = unchecked((int)0x80559404);

    /// <summary>np_sns_facebook 3.74 keeps one connection to the shell's Facebook service per client slot; slot 0 is
    /// the system's (NID_9C6AAB52 / NID_06A167FC).</summary>
    [SnapshotMissing]
    bool _systemInited;

    bool SlotReady(int slot) => slot == 0 ? _systemInited : _inited;

    /// <summary>The Facebook service's answer: no Facebook account can be linked without its servers, so every
    /// request it serves ends with the network's error (a default).</summary>
    int ServiceRequest(string nid, int slot, bool argumentsValid = true)
    {
        if (!SlotReady(slot))
        {
            return FB_NOT_INITIALIZED;
        }

        if (!argumentsValid)
        {
            return FB_INVALID_ARGUMENT;
        }

        Log.InfoOnce("facebook-" + nid, $"SceNpSnsFacebook NID_{nid}: no Facebook service (no linked account)");
        return PsnAccount.Current.ServerError;
    }

    /// <summary>NID_9C6AAB52 (SceNpSnsFacebook)(): the system's connection to the Facebook service; twice 0x80559401.</summary>
    [Hle("NID_9C6AAB52", Nid = 0x9C6AAB52, Library = "SceNpSnsFacebook")]
    int NpSnsFacebookSystemInit()
    {
        if (_systemInited)
        {
            return FB_ALREADY_INITIALIZED;
        }

        _systemInited = true;
        return 0;
    }

    /// <summary>NID_06A167FC (SceNpSnsFacebook)(): closes the system's connection (0x80559402 without one).</summary>
    [Hle("NID_06A167FC", Nid = 0x06A167FC, Library = "SceNpSnsFacebook")]
    int NpSnsFacebookSystemTerm()
    {
        if (!_systemInited)
        {
            return FB_NOT_INITIALIZED;
        }

        _systemInited = false;
        return 0;
    }

    /// <summary>NID_A9C661E9 (SceNpSnsFacebook)(int slot): how many requests the slot's connection can take; none
    /// without a linked account: 0x80559404 (0x80559402 without the connection).</summary>
    [Hle("NID_A9C661E9", Nid = 0xA9C661E9, Library = "SceNpSnsFacebook", Default = true)]
    int NpSnsFacebookRequestRoom(int slot) => SlotReady(slot) ? FB_NO_ENTRY : FB_NOT_INITIALIZED;

    /// <summary>NID_501CCC39 / NID_FB99FF8E (SceNpSnsFacebook)([slot]): a request on the system slot / the given
    /// slot (the service's request id).</summary>
    [Hle("NID_501CCC39", Nid = 0x501CCC39, Library = "SceNpSnsFacebook", Default = true)]
    int NpSnsFacebookSystemCreateRequest() => ServiceRequest("501CCC39", 0);

    [Hle("NID_FB99FF8E", Nid = 0xFB99FF8E, Library = "SceNpSnsFacebook", Default = true)]
    int NpSnsFacebookCreateRequestOn(int slot) => ServiceRequest("FB99FF8E", slot);

    /// <summary>NID_6CAB3583 / NID_3574800C (SceNpSnsFacebook)(int slot): delete / abort of the slot's request (no
    /// request runs: 0 on a ready slot).</summary>
    [Hle("NID_6CAB3583", Nid = 0x6CAB3583, Library = "SceNpSnsFacebook")]
    int NpSnsFacebookDeleteRequestOn(int slot) => SlotReady(slot) ? 0 : FB_NOT_INITIALIZED;

    [Hle("NID_3574800C", Nid = 0x3574800C, Library = "SceNpSnsFacebook")]
    int NpSnsFacebookAbortRequestOn(int slot) => SlotReady(slot) ? 0 : FB_NOT_INITIALIZED;

    /// <summary>NID_8770E114 / NID_C9ACC1C1 / NID_46C23078 (SceNpSnsFacebook): waits on / polls the slot's
    /// connection for an answer (the service never answers: its network error).</summary>
    [Hle("NID_8770E114", Nid = 0x8770E114, Library = "SceNpSnsFacebook", Default = true)]
    int NpSnsFacebookWait(int slot, uint timeout) => ServiceRequest("8770E114", slot);

    [Hle("NID_C9ACC1C1", Nid = 0xC9ACC1C1, Library = "SceNpSnsFacebook", Default = true)]
    int NpSnsFacebookSystemWait(uint timeout) => ServiceRequest("C9ACC1C1", 0);

    [Hle("NID_46C23078", Nid = 0x46C23078, Library = "SceNpSnsFacebook", Default = true)]
    int NpSnsFacebookPoll(int slot, uint result) => ServiceRequest("46C23078", slot);

    /// <summary>NID_5069C04E (SceNpSnsFacebook)(int slot, char *out, uint size) and NID_64E5B424 (out, size) on the
    /// system slot: the linked account's name (NULL 0x80559403); none is linked.</summary>
    [Hle("NID_5069C04E", Nid = 0x5069C04E, Library = "SceNpSnsFacebook", Default = true)]
    int NpSnsFacebookAccountName(int slot, uint output, uint size) => ServiceRequest("5069C04E", slot, output != 0);

    [Hle("NID_64E5B424", Nid = 0x64E5B424, Library = "SceNpSnsFacebook", Default = true)]
    int NpSnsFacebookSystemAccountName(uint output, uint size) => ServiceRequest("64E5B424", 0, output != 0);

    /// <summary>NID_9A0A4832 (SceNpSnsFacebook)(int slot, value): a setting of the slot's connection.</summary>
    [Hle("NID_9A0A4832", Nid = 0x9A0A4832, Library = "SceNpSnsFacebook", Default = true)]
    int NpSnsFacebookSetting(int slot, uint value) => ServiceRequest("9A0A4832", slot);

    /// <summary>NID_6A170F5D (SceNpSnsFacebook)(int, a, b) on the system slot: two pointers required (0x80559403).</summary>
    [Hle("NID_6A170F5D", Nid = 0x6A170F5D, Library = "SceNpSnsFacebook", Default = true)]
    int NpSnsFacebookSystemRequest6A170F5D(int value, uint a, uint b) => ServiceRequest("6A170F5D", 0, a != 0 && b != 0);

    /// <summary>NID_B22D89FD / NID_FD842B21 (SceNpSnsFacebook)(int, a, b, c, d) on the system slot: posts / token
    /// requests with two pointers required (0x80559403).</summary>
    [Hle("NID_B22D89FD", Nid = 0xB22D89FD, Library = "SceNpSnsFacebook", Default = true)]
    int NpSnsFacebookSystemRequestB22D89FD(int value, uint a, uint b, uint c, uint d) => ServiceRequest("B22D89FD", 0, a != 0 && b != 0);

    [Hle("NID_FD842B21", Nid = 0xFD842B21, Library = "SceNpSnsFacebook", Default = true)]
    int NpSnsFacebookSystemRequestFD842B21(int value, uint a, uint b, uint c, uint d) => ServiceRequest("FD842B21", 0, a != 0 && b != 0);

    /// <summary>NID_4A453D97 (a..e) and NID_CB2ED352 (a..d) on the system slot; NID_18041141 (slot, a..d),
    /// NID_ABF56333 (slot, a..c), NID_C10D390C (slot, a..f), NID_9DE0B1BC (slot, a..i), NID_FA6FBAAE (slot, a..d):
    /// the dialog's Facebook requests, all served by the Facebook service.</summary>
    [Hle("NID_4A453D97", Nid = 0x4A453D97, Library = "SceNpSnsFacebook", Default = true)]
    int NpSnsFacebookSystemRequest4A453D97(uint a, uint b, uint c, uint d, uint e) => ServiceRequest("4A453D97", 0);

    [Hle("NID_CB2ED352", Nid = 0xCB2ED352, Library = "SceNpSnsFacebook", Default = true)]
    int NpSnsFacebookSystemRequestCB2ED352(uint a, uint b, uint c, uint d) => ServiceRequest("CB2ED352", 0);

    [Hle("NID_18041141", Nid = 0x18041141, Library = "SceNpSnsFacebook", Default = true)]
    int NpSnsFacebookRequest18041141(int slot, uint a, uint b, uint c, uint d) => ServiceRequest("18041141", slot);

    [Hle("NID_ABF56333", Nid = 0xABF56333, Library = "SceNpSnsFacebook", Default = true)]
    int NpSnsFacebookRequestABF56333(int slot, uint a, uint b, uint c) => ServiceRequest("ABF56333", slot);

    [Hle("NID_C10D390C", Nid = 0xC10D390C, Library = "SceNpSnsFacebook", Default = true)]
    int NpSnsFacebookRequestC10D390C(int slot, uint a, uint b, uint c, uint d, uint e, uint f) => ServiceRequest("C10D390C", slot);

    [Hle("NID_9DE0B1BC", Nid = 0x9DE0B1BC, Library = "SceNpSnsFacebook", Default = true)]
    int NpSnsFacebookRequest9DE0B1BC(int slot, uint a, uint b, uint c, uint d, uint e, uint f, uint g, uint h, uint i) =>
        ServiceRequest("9DE0B1BC", slot);

    [Hle("NID_FA6FBAAE", Nid = 0xFA6FBAAE, Library = "SceNpSnsFacebook", Default = true)]
    int NpSnsFacebookRequestFA6FBAAE(int slot, uint a, uint b, uint c, uint d) => ServiceRequest("FA6FBAAE", slot);
}
