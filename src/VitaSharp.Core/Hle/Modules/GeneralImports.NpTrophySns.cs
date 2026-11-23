using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

// Exports without public names that no installed module imports (the "General" group) of SceNpTrophy and
// SceNpSnsFacebook (FW 3.74 np_trophy and np_sns_facebook read in their code, study only).

public sealed partial class NpTrophy
{
    static bool TrophyIdValid(int id) => id is >= 0 and <= 0x7F;

    static bool GroupIdValid(int id) => id == -1 || id is >= 0 and <= 0x1F;

    /// <summary>The trophy service's requests on a handle and a context (np_trophy 3.74's checks: initialised, a -1
    /// handle or context, NULL pointers, a trophy id outside 0..127 or a group id outside -1..31 0x80551604; unknown
    /// handle 0x80551608, context 0x80551609); the service has no system-side queue: accepted (defaults).</summary>
    [Hle("NID_0B4BCB51", Nid = 0x0B4BCB51, Library = "SceNpTrophy", Default = true)]
    int NpTrophyRequest0B4BCB51(int handle, uint a, int context, int trophyId, uint b, uint c, uint d) =>
        SystemRequest("0B4BCB51", SystemCheck(handle, context, a != 0 && TrophyIdValid(trophyId) && b != 0 && c != 0 && d != 0));

    [Hle("NID_11A4F171", Nid = 0x11A4F171, Library = "SceNpTrophy", Default = true)]
    int NpTrophyRequest11A4F171(int handle, uint a, int context, int trophyId, uint b) =>
        SystemRequest("11A4F171", SystemCheck(handle, context, a != 0 && TrophyIdValid(trophyId) && b != 0));

    [Hle("NID_4173F2C7", Nid = 0x4173F2C7, Library = "SceNpTrophy", Default = true)]
    int NpTrophyRequest4173F2C7(int handle, uint a, int context, int trophyId, uint b, uint c) =>
        SystemRequest("4173F2C7", SystemCheck(handle, context, a != 0 && TrophyIdValid(trophyId)));

    [Hle("NID_4DA9A081", Nid = 0x4DA9A081, Library = "SceNpTrophy", Default = true)]
    int NpTrophyRequest4DA9A081(int handle, uint a, int context, int groupId, uint b, uint c) =>
        SystemRequest("4DA9A081", SystemCheck(handle, context, a != 0 && GroupIdValid(groupId)));

    [Hle("NID_6B41EC32", Nid = 0x6B41EC32, Library = "SceNpTrophy", Default = true)]
    int NpTrophyRequest6B41EC32(int handle, uint a, int context, int groupId, uint b) =>
        SystemRequest("6B41EC32", SystemCheck(handle, context, a != 0 && GroupIdValid(groupId) && b != 0));

    [Hle("NID_EF32F9F6", Nid = 0xEF32F9F6, Library = "SceNpTrophy", Default = true)]
    int NpTrophyRequestEF32F9F6(int handle, uint a, int context, int groupId, uint b, uint c, uint d) =>
        SystemRequest("EF32F9F6", SystemCheck(handle, context, a != 0 && GroupIdValid(groupId) && b != 0 && c != 0 && d != 0));

    /// <summary>NID_6F17BD4E / NID_C1406504 (handle, a, context, const filter *f, out): the filter's kind (+0) is 1 or
    /// 2, kind 2 with a group id (+4) of -1..31.</summary>
    [Hle("NID_6F17BD4E", Nid = 0x6F17BD4E, Library = "SceNpTrophy", Default = true)]
    int NpTrophyRequest6F17BD4E(int handle, uint a, int context, uint filter, uint output) =>
        SystemRequest("6F17BD4E", SystemCheck(handle, context, a != 0 && output != 0 && FilterValid(filter)));

    [Hle("NID_C1406504", Nid = 0xC1406504, Library = "SceNpTrophy", Default = true)]
    int NpTrophyRequestC1406504(int handle, uint a, int context, uint filter, uint output) =>
        SystemRequest("C1406504", SystemCheck(handle, context, a != 0 && output != 0 && FilterValid(filter)));

    static bool FilterValid(uint filter)
    {
        if (filter == 0)
        {
            return false;
        }

        int kind = (int)GuestMemory.R32(filter);
        return kind == 1 || (kind == 2 && GroupIdValid((int)GuestMemory.R32(filter + 4)));
    }

    [Hle("NID_722D9BAD", Nid = 0x722D9BAD, Library = "SceNpTrophy", Default = true)]
    int NpTrophyRequest722D9BAD(int handle, uint a, int context, uint b, uint c) => SystemRequest("722D9BAD", SystemCheck(handle, context, a != 0));

    [Hle("NID_69532B6A", Nid = 0x69532B6A, Library = "SceNpTrophy", Default = true)]
    int NpTrophyRequest69532B6A(int handle, uint a, int context, uint b) => SystemRequest("69532B6A", SystemCheck(handle, context, a != 0 && b != 0));

    [Hle("NID_72446B6B", Nid = 0x72446B6B, Library = "SceNpTrophy", Default = true)]
    int NpTrophyRequest72446B6B(int handle, uint a, int context, uint b) => SystemRequest("72446B6B", SystemCheck(handle, context, a != 0 && b != 0));

    [Hle("NID_F44BFD5F", Nid = 0xF44BFD5F, Library = "SceNpTrophy", Default = true)]
    int NpTrophyRequestF44BFD5F(int handle, uint a, int context, uint b) => SystemRequest("F44BFD5F", SystemCheck(handle, context, a != 0 && b != 0));

    [Hle("NID_BF78B2BC", Nid = 0xBF78B2BC, Library = "SceNpTrophy", Default = true)]
    int NpTrophyRequestBF78B2BC(int handle, uint a, int context, uint b, uint c) =>
        SystemRequest("BF78B2BC", SystemCheck(handle, context, a != 0 && b != 0 && c != 0));

    /// <summary>Handle-only requests: NID_E363C13A (handle, a, b, c, d), NID_21676448 (handle, a, x, y, b),
    /// NID_32473A4B (handle, a, b), NID_86C16F67 / NID_DCFBE482 (handle, a), NID_ADEAD655 (handle, a, x, y),
    /// NID_ED1908F7 (handle, x, a), NID_7BD69297 / NID_87E4C1B5 (handle, x).</summary>
    [Hle("NID_E363C13A", Nid = 0xE363C13A, Library = "SceNpTrophy", Default = true)]
    int NpTrophyRequestE363C13A(int handle, uint a, uint b, uint c, uint d) => SystemRequest("E363C13A", SystemCheck(handle, a != 0 && b != 0 && c != 0 && d != 0));

    [Hle("NID_21676448", Nid = 0x21676448, Library = "SceNpTrophy", Default = true)]
    int NpTrophyRequest21676448(int handle, uint a, uint x, uint y, uint b) => SystemRequest("21676448", SystemCheck(handle, a != 0 && b != 0));

    [Hle("NID_32473A4B", Nid = 0x32473A4B, Library = "SceNpTrophy", Default = true)]
    int NpTrophyRequest32473A4B(int handle, uint a, uint b) => SystemRequest("32473A4B", SystemCheck(handle, a != 0 && b != 0));

    [Hle("NID_86C16F67", Nid = 0x86C16F67, Library = "SceNpTrophy", Default = true)]
    int NpTrophyRequest86C16F67(int handle, uint a) => SystemRequest("86C16F67", SystemCheck(handle, a != 0));

    [Hle("NID_DCFBE482", Nid = 0xDCFBE482, Library = "SceNpTrophy", Default = true)]
    int NpTrophyRequestDCFBE482(int handle, uint a) => SystemRequest("DCFBE482", SystemCheck(handle, a != 0));

    [Hle("NID_ADEAD655", Nid = 0xADEAD655, Library = "SceNpTrophy", Default = true)]
    int NpTrophyRequestADEAD655(int handle, uint a, uint x, uint y) => SystemRequest("ADEAD655", SystemCheck(handle, a != 0));

    [Hle("NID_ED1908F7", Nid = 0xED1908F7, Library = "SceNpTrophy", Default = true)]
    int NpTrophyRequestED1908F7(int handle, uint x, uint a) => SystemRequest("ED1908F7", SystemCheck(handle, a != 0));

    [Hle("NID_7BD69297", Nid = 0x7BD69297, Library = "SceNpTrophy", Default = true)]
    int NpTrophyRequest7BD69297(int handle, uint x) => SystemRequest("7BD69297", SystemCheck(handle, true));

    [Hle("NID_87E4C1B5", Nid = 0x87E4C1B5, Library = "SceNpTrophy", Default = true)]
    int NpTrophyRequest87E4C1B5(int handle, uint x) => SystemRequest("87E4C1B5", SystemCheck(handle, true));

    /// <summary>NID_5672DAE5 (block): a release request (initialised, else 0x80551601).</summary>
    [Hle("NID_5672DAE5", Nid = 0x5672DAE5, Library = "SceNpTrophy", Default = true)]
    int NpTrophyRequest5672DAE5(uint block) => SystemRequest("5672DAE5", _initialized ? 0 : ERR_NOT_INITIALIZED);

    /// <summary>Library-level calls to the trophy service without the library's flag (NID_03D7BB80, NID_47456D7E,
    /// NID_4D16AF03 and their forms with a value: NID_9E483C50, NID_AAF30D25): accepted.</summary>
    [Hle("NID_03D7BB80", Nid = 0x03D7BB80, Library = "SceNpTrophy", Default = true)]
    int NpTrophyService03D7BB80() => SystemRequest("03D7BB80", 0);

    [Hle("NID_47456D7E", Nid = 0x47456D7E, Library = "SceNpTrophy", Default = true)]
    int NpTrophyService47456D7E() => SystemRequest("47456D7E", 0);

    [Hle("NID_4D16AF03", Nid = 0x4D16AF03, Library = "SceNpTrophy", Default = true)]
    int NpTrophyService4D16AF03() => SystemRequest("4D16AF03", 0);

    [Hle("NID_9E483C50", Nid = 0x9E483C50, Library = "SceNpTrophy", Default = true)]
    int NpTrophyService9E483C50(uint a, uint value) => SystemRequest("9E483C50", 0);

    [Hle("NID_AAF30D25", Nid = 0xAAF30D25, Library = "SceNpTrophy", Default = true)]
    int NpTrophyServiceAAF30D25(uint a, uint b) => SystemRequest("AAF30D25", 0);
}

public sealed partial class NpSnsFacebook
{
    /// <summary>The numbered client slots opened with NID_8CB58B88 (slot 0 is the system's).</summary>
    [SnapshotMissing]
    readonly HashSet<int> _openSlots = [];

    bool AnySlotReady(int slot) => slot == 0 ? _systemInited : _inited || _openSlots.Contains(slot);

    int SlotRequest(string nid, int slot, bool argumentsValid = true)
    {
        if (!AnySlotReady(slot))
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

    /// <summary>NID_34A88B2D (a, b) / NID_9663A2EC (a, b: both required, 0x80559403) / NID_EF957AD0 (): the system
    /// slot's connection with a memory setting (0x80559401 when open).</summary>
    [Hle("NID_34A88B2D", Nid = 0x34A88B2D, Library = "SceNpSnsFacebook")]
    int NpSnsFacebookSystemInitWith(uint a, uint b) => NpSnsFacebookSystemInitThroughEntry();

    [Hle("NID_9663A2EC", Nid = 0x9663A2EC, Library = "SceNpSnsFacebook")]
    int NpSnsFacebookSystemInitWithPool(uint pool, uint size)
    {
        if (_systemInited)
        {
            return FB_ALREADY_INITIALIZED;
        }

        return pool == 0 || size == 0 ? FB_INVALID_ARGUMENT : NpSnsFacebookSystemInitThroughEntry();
    }

    [Hle("NID_EF957AD0", Nid = 0xEF957AD0, Library = "SceNpSnsFacebook")]
    int NpSnsFacebookSystemInit2() => NpSnsFacebookSystemInitThroughEntry();

    /// <summary>NID_8CB58B88 (int slot, a, b): opens a numbered slot's connection (open 0x80559401, NULL 0x80559403).</summary>
    [Hle("NID_8CB58B88", Nid = 0x8CB58B88, Library = "SceNpSnsFacebook")]
    int NpSnsFacebookOpenSlot(int slot, uint a, uint b)
    {
        if (AnySlotReady(slot))
        {
            return FB_ALREADY_INITIALIZED;
        }

        if (a == 0 || b == 0)
        {
            return FB_INVALID_ARGUMENT;
        }

        if (slot == 0)
        {
            _systemInited = true;
        }
        else
        {
            _openSlots.Add(slot);
        }

        return 0;
    }

    /// <summary>NID_C15158EE (int slot) / NID_D08701B6 (): closes a slot's / the system slot's connection.</summary>
    [Hle("NID_C15158EE", Nid = 0xC15158EE, Library = "SceNpSnsFacebook")]
    int NpSnsFacebookCloseSlot(int slot)
    {
        if (!AnySlotReady(slot))
        {
            return FB_NOT_INITIALIZED;
        }

        if (slot == 0)
        {
            _systemInited = false;
        }
        else
        {
            _openSlots.Remove(slot);
        }

        return 0;
    }

    [Hle("NID_D08701B6", Nid = 0xD08701B6, Library = "SceNpSnsFacebook")]
    int NpSnsFacebookSystemTerm2() => NpSnsFacebookSystemTermThroughEntry();

    /// <summary>NID_836AA54A (): the system slot's request room (none: 0x80559404).</summary>
    [Hle("NID_836AA54A", Nid = 0x836AA54A, Library = "SceNpSnsFacebook", Default = true)]
    int NpSnsFacebookSystemRequestRoom() => NpSnsFacebookRequestRoomThroughEntry(0);

    /// <summary>NID_249B3C57 / NID_72A0685C (): delete / abort of the system slot's request.</summary>
    [Hle("NID_249B3C57", Nid = 0x249B3C57, Library = "SceNpSnsFacebook")]
    int NpSnsFacebookSystemDeleteRequest() => NpSnsFacebookDeleteRequestOnThroughEntry(0);

    [Hle("NID_72A0685C", Nid = 0x72A0685C, Library = "SceNpSnsFacebook")]
    int NpSnsFacebookSystemAbortRequest() => NpSnsFacebookAbortRequestOnThroughEntry(0);

    // the service's requests (defaults: no Facebook account can be linked), on the system slot or a given one
    [Hle("NID_0CDD6B52", Nid = 0x0CDD6B52, Library = "SceNpSnsFacebook", Default = true)]
    int NpSnsFacebookSystemPoll(uint result) => SlotRequest("0CDD6B52", 0);

    [Hle("NID_0FEBCA93", Nid = 0x0FEBCA93, Library = "SceNpSnsFacebook", Default = true)]
    int NpSnsFacebookRequest0FEBCA93(int slot, uint value, uint a, uint b) => SlotRequest("0FEBCA93", slot, a != 0 && b != 0);

    [Hle("NID_1375638F", Nid = 0x1375638F, Library = "SceNpSnsFacebook", Default = true)]
    int NpSnsFacebookRequest1375638F(int slot, uint value, uint a, uint b, uint c, uint d) => SlotRequest("1375638F", slot, a != 0 && b != 0);

    [Hle("NID_20B68EBC", Nid = 0x20B68EBC, Library = "SceNpSnsFacebook", Default = true)]
    int NpSnsFacebookRequest20B68EBC(int slot, uint a, uint b, uint c, uint d) => SlotRequest("20B68EBC", slot);

    [Hle("NID_2814F3DB", Nid = 0x2814F3DB, Library = "SceNpSnsFacebook", Default = true)]
    int NpSnsFacebookSystemRequest2814F3DB(uint a, uint b, uint c, uint d) => SlotRequest("2814F3DB", 0);

    [Hle("NID_42CD6A87", Nid = 0x42CD6A87, Library = "SceNpSnsFacebook", Default = true)]
    int NpSnsFacebookSystemRequest42CD6A87(uint a) => SlotRequest("42CD6A87", 0);

    [Hle("NID_58CAC784", Nid = 0x58CAC784, Library = "SceNpSnsFacebook", Default = true)]
    int NpSnsFacebookRequest58CAC784(int slot, uint a, uint b, uint c, uint d) => SlotRequest("58CAC784", slot);

    [Hle("NID_5EAB459F", Nid = 0x5EAB459F, Library = "SceNpSnsFacebook", Default = true)]
    int NpSnsFacebookSystemRequest5EAB459F(uint a, uint b, uint c) => SlotRequest("5EAB459F", 0);

    [Hle("NID_61DDCBB7", Nid = 0x61DDCBB7, Library = "SceNpSnsFacebook", Default = true)]
    int NpSnsFacebookSystemRequest61DDCBB7(uint a) => SlotRequest("61DDCBB7", 0);

    [Hle("NID_76B234BD", Nid = 0x76B234BD, Library = "SceNpSnsFacebook", Default = true)]
    int NpSnsFacebookSystemRequest76B234BD(uint a, uint b, uint c, uint d) => SlotRequest("76B234BD", 0);

    [Hle("NID_78636540", Nid = 0x78636540, Library = "SceNpSnsFacebook", Default = true)]
    int NpSnsFacebookSystemRequest78636540(uint value, uint a, uint b, uint c, uint d) => SlotRequest("78636540", 0, a != 0 && b != 0);

    [Hle("NID_7C1286C0", Nid = 0x7C1286C0, Library = "SceNpSnsFacebook", Default = true)]
    int NpSnsFacebookSystemSetting(uint value) => SlotRequest("7C1286C0", 0);

    [Hle("NID_7CE37858", Nid = 0x7CE37858, Library = "SceNpSnsFacebook", Default = true)]
    int NpSnsFacebookRequest7CE37858(int slot, uint a) => SlotRequest("7CE37858", slot);

    [Hle("NID_A3785735", Nid = 0xA3785735, Library = "SceNpSnsFacebook", Default = true)]
    int NpSnsFacebookRequestA3785735(int slot, uint a, uint b, uint c, uint d, uint e) => SlotRequest("A3785735", slot);

    [Hle("NID_B45419A5", Nid = 0xB45419A5, Library = "SceNpSnsFacebook", Default = true)]
    int NpSnsFacebookSystemRequestB45419A5(uint value, uint a, uint b, uint c, uint d) => SlotRequest("B45419A5", 0, a != 0 && b != 0);

    [Hle("NID_C87C016D", Nid = 0xC87C016D, Library = "SceNpSnsFacebook", Default = true)]
    int NpSnsFacebookRequestC87C016D(int slot, uint a) => SlotRequest("C87C016D", slot);

    [Hle("NID_C9798074", Nid = 0xC9798074, Library = "SceNpSnsFacebook", Default = true)]
    int NpSnsFacebookSystemRequestC9798074(uint a) => SlotRequest("C9798074", 0);

    [Hle("NID_CB51675B", Nid = 0xCB51675B, Library = "SceNpSnsFacebook", Default = true)]
    int NpSnsFacebookSystemRequestCB51675B(uint a, uint b, uint c, uint d, uint e, uint f) => SlotRequest("CB51675B", 0);

    [Hle("NID_DC06BD48", Nid = 0xDC06BD48, Library = "SceNpSnsFacebook", Default = true)]
    int NpSnsFacebookSystemRequestDC06BD48(uint a, uint b, uint c, uint d, uint e, uint f, uint g, uint h, uint i) => SlotRequest("DC06BD48", 0);

    [Hle("NID_DCCE1BB3", Nid = 0xDCCE1BB3, Library = "SceNpSnsFacebook", Default = true)]
    int NpSnsFacebookSystemRequestDCCE1BB3(uint a, uint b, uint c, uint d) => SlotRequest("DCCE1BB3", 0);

    [Hle("NID_DEDD3ECA", Nid = 0xDEDD3ECA, Library = "SceNpSnsFacebook", Default = true)]
    int NpSnsFacebookRequestDEDD3ECA(int slot, uint a) => SlotRequest("DEDD3ECA", slot);

    [Hle("NID_F3956995", Nid = 0xF3956995, Library = "SceNpSnsFacebook", Default = true)]
    int NpSnsFacebookRequestF3956995(int slot, uint value, uint a, uint b, uint c, uint d) => SlotRequest("F3956995", slot, a != 0 && b != 0);
}
