using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// SceNearUtil and SceNearDialogUtil: the discontinued "near" gift/neighbour service. There is no public header and no
/// reference implementation, so the layouts are unknown and no output pointer is written (except the common-dialog
/// result word). The service behaves as unavailable: Initialize fails, Finalize succeeds, everything else reports
/// "not running". The game then hides the feature, as on a console without the Near application.
/// </summary>
public sealed class NearUtil : HleModule
{
    /// <summary>Reused generic codes until the real SceNearUtil codes are known (SCE_COMMON_DIALOG_ERROR_*).</summary>
    internal const int ERR_NOT_SUPPORTED = unchecked((int)0x80020405), ERR_NOT_RUNNING = unchecked((int)0x80020404);

    static int Unavailable(string name)
    {
        Log.WarnOnce("near-" + name, $"{name}: Near service unavailable");
        return ERR_NOT_SUPPORTED;
    }

    // ---------------------------------------------------------------- SceNearUtil (sceNear*)

    [Hle] int sceNearInitialize() => Unavailable("sceNearInitialize");
    [Hle] int sceNearFinalize() => 0;
    [Hle] int sceNearFinalizeAndLaunchNearApp() { Log.Info("sceNearFinalizeAndLaunchNearApp: no Near application to launch"); return 0; }
    [Hle] int sceNearRefresh() => ERR_NOT_RUNNING;
    [Hle] int sceNearGetMyStatus() => ERR_NOT_RUNNING;
    [Hle] int sceNearGetNeighbors() => ERR_NOT_RUNNING;
    [Hle] int sceNearGetNewNeighbors() => ERR_NOT_RUNNING;
    [Hle] int sceNearGetRecentNeighbors() => ERR_NOT_RUNNING;
    [Hle] int sceNearGetLastNeighborFoundDateTime() => ERR_NOT_RUNNING;
    [Hle] int sceNearSetGift() => ERR_NOT_RUNNING;
    [Hle] int sceNearSetGift2() => ERR_NOT_RUNNING;
    [Hle] int sceNearGetGift() => ERR_NOT_RUNNING;
    [Hle] int sceNearGetGiftStatus() => ERR_NOT_RUNNING;
    [Hle] int sceNearDeleteGift() => ERR_NOT_RUNNING;
    [Hle] int sceNearGetDiscoveredGifts() => ERR_NOT_RUNNING;
    [Hle] int sceNearGetDiscoveredGiftInfo() => ERR_NOT_RUNNING;
    [Hle] int sceNearGetDiscoveredGiftSender() => ERR_NOT_RUNNING;
    [Hle] int sceNearGetDiscoveredGiftStatus() => ERR_NOT_RUNNING;
    [Hle] int sceNearConvertDiscoveredGiftParam() => ERR_NOT_RUNNING;
    [Hle] int sceNearDeleteDiscoveredGift() => ERR_NOT_RUNNING;
    [Hle] int sceNearIgnoreDiscoveredGift() => ERR_NOT_RUNNING;
    [Hle] int sceNearOpenDiscoveredGiftImage() => ERR_NOT_RUNNING;
    [Hle] int sceNearReadDiscoveredGiftImage() => ERR_NOT_RUNNING;
    [Hle] int sceNearCloseDiscoveredGiftImage() => ERR_NOT_RUNNING;
    [Hle] int sceNearOpenReceivedGiftData() => ERR_NOT_RUNNING;
    [Hle] int sceNearReadReceivedGiftData() => ERR_NOT_RUNNING;
    [Hle] int sceNearCloseReceivedGiftData() => ERR_NOT_RUNNING;
    [Hle] int sceNearLaunchNearAppForDownload() { Log.Info("sceNearLaunchNearAppForDownload: no Near application"); return ERR_NOT_RUNNING; }
    [Hle] int sceNearLaunchNearAppForUpdate() { Log.Info("sceNearLaunchNearAppForUpdate: no Near application"); return ERR_NOT_RUNNING; }

    // ---------------------------------------------------------------- SceNearUtil, early-SDK NIDs
    // Launch titles (e.g. PCSA00029) import 13 SceNearUtil NIDs absent from every database. The call sites show:
    //  - 0xA412E9CA(work*, {buffer, 0x40000}*, 0): the game mallocs 256 KiB, calls this, frees the buffer and records
    //    "failed" when the result is non-zero -> the early Initialize. Fails like sceNearInitialize.
    //  - 0xE608000B(work*): called with the same work pointer on shutdown, before freeing the buffer -> Finalize.
    //  - 0x69EE6FB3's return value is used directly as an unsigned element count of a following loop (called twice,
    //    the second time with &count), so a negative error code would be read as ~2G entries: it and the other
    //    unidentified ones return 0 ("nothing"), writing nothing. 0x49A97D5F is checked with "<= 0 -> failure".

    [Hle("sceNearUtil_A412E9CA", Nid = 0xA412E9CA, Library = "SceNearUtil")]
    int NearA412E9CA(uint work, uint memParam, uint flags) => Unavailable("sceNearUtil_A412E9CA (Initialize)");

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
