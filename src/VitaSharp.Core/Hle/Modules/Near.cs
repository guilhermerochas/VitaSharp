using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// SceNearUtil and SceNearDialogUtil: the discontinued "near" gift/neighbour service. There is no public header and no
/// reference implementation, so the layouts are unknown and no output pointer is written (except the common-dialog
/// result word). The service is unavailable: Initialize fails after the firmware's argument checks and SceNearUtil
/// answers "not initialized" (FW 3.74 codes); the game then hides the feature, as on a console without the Near application.
/// </summary>
public sealed partial class NearUtil : HleModule
{
    /// <summary>Reused generic codes until the real SceNearUtil codes are known (SCE_COMMON_DIALOG_ERROR_*).</summary>
    internal const int ERR_NOT_SUPPORTED = unchecked((int)0x80020405), ERR_NOT_RUNNING = unchecked((int)0x80020404);

    static int Unavailable(string name)
    {
        Log.WarnOnce("near-" + name, $"{name}: Near service unavailable");
        return ERR_NOT_SUPPORTED;
    }

    // ---------------------------------------------------------------- SceNearUtil (sceNear*)
    // libSceNearUtil.suprx (FW 3.74): every call but Initialize and LaunchNearAppForUpdate starts with "not
    // initialized" (0x80104905) when the module state byte is clear, then checks its pointers (0x80104901). The state
    // is set only when Initialize reaches the Near application (sceAppMgrLaunchAppByUri and its IPC server), which
    // VitaSharp does not run, so Initialize fails after its argument checks and the rest report "not initialized".

    internal const int ERR_INVALID_ARG = unchecked((int)0x80104901), ERR_INVALID_COMM_ID = unchecked((int)0x80104902),
        ERR_ALREADY_INITIALIZED = unchecked((int)0x80104903), ERR_NO_MEMORY = unchecked((int)0x80104904),
        ERR_NOT_INITIALIZED = unchecked((int)0x80104905);

    /// <summary>The NP communication id form the module accepts: four capitals, five digits, '_', two digits.</summary>
    static bool ValidCommId(uint p)
    {
        for (uint i = 0; i < 12; i++)
        {
            byte c = GuestMemory.Read<byte>(p + i);
            bool ok = i switch { < 4 => c is >= (byte)'A' and <= (byte)'Z', 9 => c == '_', _ => c is >= (byte)'0' and <= (byte)'9' };
            if (!ok) return false;
        }
        return GuestMemory.Read<byte>(p + 12) == 0;
    }

    /// <summary>sceNearInitialize(const SceNpCommunicationId *id, const {void *buf; size_t size} *mem, ...): the
    /// module's checks (NULL id/mem/buffer, a buffer under 256 KiB, a malformed id), then the Near service, which is
    /// not available.</summary>
    [Hle("sceNearInitialize")]
    [Hle("sceNearUtil_A412E9CA", Nid = 0xA412E9CA, Library = "SceNearUtil")] // the early-SDK NID of the same entry
    int sceNearInitialize(uint commId, uint memParam, uint flags)
    {
        if (commId == 0 || memParam == 0 || GuestMemory.R32(memParam) == 0) return ERR_INVALID_ARG;
        if (GuestMemory.R32(memParam + 4) < 0x40000) return ERR_NO_MEMORY;
        if (!ValidCommId(commId)) return ERR_INVALID_COMM_ID;
        return Unavailable("sceNearInitialize");
    }

    [Hle("sceNearFinalize")]
    [Hle("sceNearFinalizeAndLaunchNearApp")]
    [Hle("sceNearRefresh")]
    [Hle("sceNearGetMyStatus")]
    [Hle("sceNearGetNeighbors")]
    [Hle("sceNearGetNewNeighbors")]
    [Hle("sceNearGetRecentNeighbors")]
    [Hle("sceNearGetLastNeighborFoundDateTime")]
    [Hle("sceNearSetGift")]
    [Hle("sceNearSetGift2")]
    [Hle("sceNearGetGift")]
    [Hle("sceNearGetGiftStatus")]
    [Hle("sceNearDeleteGift")]
    [Hle("sceNearGetDiscoveredGifts")]
    [Hle("sceNearGetDiscoveredGiftInfo")]
    [Hle("sceNearGetDiscoveredGiftSender")]
    [Hle("sceNearGetDiscoveredGiftStatus")]
    [Hle("sceNearConvertDiscoveredGiftParam")]
    [Hle("sceNearDeleteDiscoveredGift")]
    [Hle("sceNearIgnoreDiscoveredGift")]
    [Hle("sceNearOpenDiscoveredGiftImage")]
    [Hle("sceNearReadDiscoveredGiftImage")]
    [Hle("sceNearCloseDiscoveredGiftImage")]
    [Hle("sceNearOpenReceivedGiftData")]
    [Hle("sceNearReadReceivedGiftData")]
    [Hle("sceNearCloseReceivedGiftData")]
    [Hle("sceNearLaunchNearAppForDownload")]
    // unnamed exports: wrappers of CloseDiscoveredGiftImage (46604AA3), ReadDiscoveredGiftImage (79A4283C),
    // OpenDiscoveredGiftImage (CCC99AF1), DeleteGift (D22CB2DE), ConvertDiscoveredGiftParam (DDEB6C8C), and two that
    // read the module's own state (8E47CFDC: 0x20 bytes of status, 9044B509: the neighbour list); all check the
    // state byte first
    [Hle("sceNearUtil_46604AA3", Nid = 0x46604AA3, Library = "SceNearUtil")]
    [Hle("sceNearUtil_79A4283C", Nid = 0x79A4283C, Library = "SceNearUtil")]
    [Hle("sceNearUtil_CCC99AF1", Nid = 0xCCC99AF1, Library = "SceNearUtil")]
    [Hle("sceNearUtil_D22CB2DE", Nid = 0xD22CB2DE, Library = "SceNearUtil")]
    [Hle("sceNearUtil_DDEB6C8C", Nid = 0xDDEB6C8C, Library = "SceNearUtil")]
    [Hle("sceNearUtil_8E47CFDC", Nid = 0x8E47CFDC, Library = "SceNearUtil")]
    [Hle("sceNearUtil_9044B509", Nid = 0x9044B509, Library = "SceNearUtil")]
    int sceNearNotInitialized() => ERR_NOT_INITIALIZED;

    /// <summary>LaunchNearAppForUpdate needs no initialization: it asks the shell to open the Near application's
    /// update page, which does not exist here.</summary>
    [Hle] int sceNearLaunchNearAppForUpdate() { Log.Info("sceNearLaunchNearAppForUpdate: no Near application"); return ERR_NOT_RUNNING; }

    // ---------------------------------------------------------------- SceNearUtil, early-SDK NIDs
    // Launch titles (e.g. PCSA00029) import 13 SceNearUtil NIDs absent from every database. The call sites show:
    //  - 0xA412E9CA(work*, {buffer, 0x40000}*, 0): the game mallocs 256 KiB, calls this, frees the buffer and records
    //    "failed" when the result is non-zero -> the early Initialize. Fails like sceNearInitialize.
    //  - 0xE608000B(work*): called with the same work pointer on shutdown, before freeing the buffer -> Finalize.
    //  - 0x69EE6FB3's return value is used directly as an unsigned element count of a following loop (called twice,
    //    the second time with &count), so a negative error code would be read as ~2G entries: it and the other
    //    unidentified ones return 0 ("nothing"), writing nothing. 0x49A97D5F is checked with "<= 0 -> failure".

    [Hle("sceNearUtil_E608000B", Nid = 0xE608000B, Library = "SceNearUtil")]
    int NearE608000B(uint work) => 0;

    [Hle("sceNearUtil_1AA394BA", Nid = 0x1AA394BA, Library = "SceNearUtil")]
    [Hle("sceNearUtil_1AE3EC1C", Nid = 0x1AE3EC1C, Library = "SceNearUtil")]
    [Hle("sceNearUtil_49A97D5F", Nid = 0x49A97D5F, Library = "SceNearUtil")]
    [Hle("sceNearUtil_69EE6FB3", Nid = 0x69EE6FB3, Library = "SceNearUtil")]
    [Hle("sceNearUtil_76C4807C", Nid = 0x76C4807C, Library = "SceNearUtil")]
    [Hle("sceNearUtil_8095FDEF", Nid = 0x8095FDEF, Library = "SceNearUtil")]
    [Hle("sceNearUtil_88540CEC", Nid = 0x88540CEC, Library = "SceNearUtil")]
    [Hle("sceNearUtil_9B3F2DCE", Nid = 0x9B3F2DCE, Library = "SceNearUtil")]
    [Hle("sceNearUtil_9FB9277B", Nid = 0x9FB9277B, Library = "SceNearUtil")]
    [Hle("sceNearUtil_BF2BBE1F", Nid = 0xBF2BBE1F, Library = "SceNearUtil")]
    [Hle("sceNearUtil_CA742B97", Nid = 0xCA742B97, Library = "SceNearUtil")]
    int NearEarlyNothing() => 0;

    // ---------------------------------------------------------------- SceNearDialogUtil (sceNearDialog*)

    internal const int DialogNone = 0, DialogFinished = 2; // SceCommonDialogStatus
    const int ResultAborted = 2;                             // SCE_COMMON_DIALOG_RESULT_ABORTED

    int _dialogStatus = DialogNone;
    internal int DialogStatus => _dialogStatus;

    /// <summary>The dialog "opens" and finishes at once as aborted.</summary>
    [Hle] int sceNearDialogInit(uint param) { _dialogStatus = DialogFinished; return 0; }
    [Hle] int sceNearDialogGetStatus() => _dialogStatus;

    /// <summary>The result struct size is unknown (0x2C is a guess): only its first word, the common result, is written.</summary>
    [Hle]
    int sceNearDialogGetResult(uint result)
    {
        if (_dialogStatus != DialogFinished) return ERR_NOT_RUNNING;
        if (result != 0) GuestMemory.W32(result, ResultAborted);
        return 0;
    }

    [Hle] int sceNearDialogAbort() => 0;
    [Hle] int sceNearDialogTerm() { _dialogStatus = DialogNone; return 0; }

    // ---------------------------------------------------------------- SceNearDialogUtil (sceNearUtility*)

    [Hle] int sceNearUtilityInitialize() => Unavailable("sceNearUtilityInitialize");
    [Hle] int sceNearUtilityFinalize() => 0;
    [Hle] int sceNearUtilityRefresh() => ERR_NOT_RUNNING;
    [Hle] int sceNearUtilityGetMyStatus() => ERR_NOT_RUNNING;
    [Hle] int sceNearUtilityGetNeighbors() => ERR_NOT_RUNNING;
    [Hle] int sceNearUtilityGetNewNeighbors() => ERR_NOT_RUNNING;
    [Hle] int sceNearUtilityGetRecentNeighbors() => ERR_NOT_RUNNING;
    [Hle] int sceNearUtilityGetLastNeighborFoundDateTime() => ERR_NOT_RUNNING;
    [Hle] int sceNearUtilitySetGift() => ERR_NOT_RUNNING;
    [Hle] int sceNearUtilitySetGift2() => ERR_NOT_RUNNING;
    [Hle] int sceNearUtilityGetGift() => ERR_NOT_RUNNING;
    [Hle] int sceNearUtilityGetGiftStatus() => ERR_NOT_RUNNING;
    [Hle] int sceNearUtilityDeleteGift() => ERR_NOT_RUNNING;
    [Hle] int sceNearUtilityGetDiscoveredGifts() => ERR_NOT_RUNNING;
    [Hle] int sceNearUtilityGetDiscoveredGiftInfo() => ERR_NOT_RUNNING;
    [Hle] int sceNearUtilityGetDiscoveredGiftSender() => ERR_NOT_RUNNING;
    [Hle] int sceNearUtilityGetDiscoveredGiftStatus() => ERR_NOT_RUNNING;
    [Hle] int sceNearUtilityConvertDiscoveredGiftParam() => ERR_NOT_RUNNING;
    [Hle] int sceNearUtilityDeleteDiscoveredGift() => ERR_NOT_RUNNING;
    [Hle] int sceNearUtilityIgnoreDiscoveredGift() => ERR_NOT_RUNNING;
    [Hle] int sceNearUtilityOpenDiscoveredGiftImage() => ERR_NOT_RUNNING;
    [Hle] int sceNearUtilityReadDiscoveredGiftImage() => ERR_NOT_RUNNING;
    [Hle] int sceNearUtilityCloseDiscoveredGiftImage() => ERR_NOT_RUNNING;
    [Hle] int sceNearUtilityOpenReceivedGiftData() => ERR_NOT_RUNNING;
    [Hle] int sceNearUtilityReadReceivedGiftData() => ERR_NOT_RUNNING;
    [Hle] int sceNearUtilityCloseReceivedGiftData() => ERR_NOT_RUNNING;
    [Hle] int sceNearUtilityLaunchNearAppForDownload() { Log.Info("sceNearUtilityLaunchNearAppForDownload: no Near application"); return ERR_NOT_RUNNING; }
    [Hle] int sceNearUtilityLaunchNearAppForUpdate() { Log.Info("sceNearUtilityLaunchNearAppForUpdate: no Near application"); return ERR_NOT_RUNNING; }
}
