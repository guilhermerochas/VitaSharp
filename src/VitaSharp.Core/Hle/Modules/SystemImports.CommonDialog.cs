namespace VitaSharp.Core.Hle.Modules;

// System-imported functions without public names of SceCommonDialog (FW 3.74 libcdlg read in its code, study only):
// the four families the system software alone uses (0x10 and 0x1B, the PS4 Link app's; 0x16, the Near dialog util's;
// 0x1A, NPXS10014's) and the system's Init of the Twitter (0x15) and photo import (0xB) families.

public sealed partial class CommonDialog
{
    /// <summary>
    /// The family entries libcdlg 3.74 builds on its family table: Init (FUN_81001C46 with the block: the family's
    /// OnInit, a NULL block SCE_COMMON_DIALOG_ERROR_NULL), the argument-less requests (FUN_81001C46 without a block:
    /// the family's abort and, for 0x10 and 0x1B, a second request to the running dialog), GetStatus (FUN_81001A48),
    /// GetResult (FUN_81001772) and Term (FUN_81001802). As the HLE's other families the dialog is not drawn: Init
    /// takes the common dialog slot and finishes as the user's cancel.
    /// </summary>
    int FamilyInit(DialogType type, uint param) => SimpleInit(type, param, ResultUserCanceled);

    /// <summary>A request to the family's running dialog: NOT_IN_USE when it is not the active one.</summary>
    int FamilyRequest(DialogType type) => State.StatusOf(type) == CommonDialogState.StatusNone ? CommonDialogError.NOT_IN_USE : 0;

    // ---- family 0x10

    [Hle("NID_64F65B8D", Nid = 0x64F65B8D, Library = "SceCommonDialog")]
    int CommonDialogFamily10Init(uint param) => FamilyInit(DialogType.Libcdlg10, param);

    [Hle("NID_A68A44EA", Nid = 0xA68A44EA, Library = "SceCommonDialog")]
    int CommonDialogFamily10Abort() => State.Abort(DialogType.Libcdlg10);

    /// <summary>NID_ED2F1495: the family's second argument-less request.</summary>
    [Hle("NID_ED2F1495", Nid = 0xED2F1495, Library = "SceCommonDialog")]
    int CommonDialogFamily10Request() => FamilyRequest(DialogType.Libcdlg10);

    [Hle("NID_FA26EB30", Nid = 0xFA26EB30, Library = "SceCommonDialog")]
    int CommonDialogFamily10GetStatus() => State.StatusOf(DialogType.Libcdlg10);

    [Hle("NID_9FA21E09", Nid = 0x9FA21E09, Library = "SceCommonDialog")]
    int CommonDialogFamily10GetResult(uint result) => SimpleResult(DialogType.Libcdlg10, result);

    [Hle("NID_C2D6F7F2", Nid = 0xC2D6F7F2, Library = "SceCommonDialog")]
    int CommonDialogFamily10Term() => State.Term(DialogType.Libcdlg10);

    // ---- family 0x16 (the Near dialog util)

    [Hle("NID_47E7AD8E", Nid = 0x47E7AD8E, Library = "SceCommonDialog")]
    int CommonDialogNearInit(uint param) => FamilyInit(DialogType.Near, param);

    [Hle("NID_F2F0049E", Nid = 0xF2F0049E, Library = "SceCommonDialog")]
    int CommonDialogNearAbort() => State.Abort(DialogType.Near);

    [Hle("NID_C36C7DD1", Nid = 0xC36C7DD1, Library = "SceCommonDialog")]
    int CommonDialogNearGetStatus() => State.StatusOf(DialogType.Near);

    [Hle("NID_089FB01E", Nid = 0x089FB01E, Library = "SceCommonDialog")]
    int CommonDialogNearGetResult(uint result) => SimpleResult(DialogType.Near, result);

    [Hle("NID_35387E0C", Nid = 0x35387E0C, Library = "SceCommonDialog")]
    int CommonDialogNearTerm() => State.Term(DialogType.Near);

    // ---- family 0x1A

    [Hle("NID_A785D111", Nid = 0xA785D111, Library = "SceCommonDialog")]
    int CommonDialogFamily1AInit(uint param) => FamilyInit(DialogType.Libcdlg1A, param);

    [Hle("NID_460FDFF7", Nid = 0x460FDFF7, Library = "SceCommonDialog")]
    int CommonDialogFamily1AAbort() => State.Abort(DialogType.Libcdlg1A);

    [Hle("NID_E7F645AE", Nid = 0xE7F645AE, Library = "SceCommonDialog")]
    int CommonDialogFamily1AGetStatus() => State.StatusOf(DialogType.Libcdlg1A);

    [Hle("NID_76A52CDE", Nid = 0x76A52CDE, Library = "SceCommonDialog")]
    int CommonDialogFamily1AGetResult(uint result) => SimpleResult(DialogType.Libcdlg1A, result);

    [Hle("NID_EFABDD6F", Nid = 0xEFABDD6F, Library = "SceCommonDialog")]
    int CommonDialogFamily1ATerm() => State.Term(DialogType.Libcdlg1A);

    // ---- family 0x1B

    [Hle("NID_396D7B68", Nid = 0x396D7B68, Library = "SceCommonDialog")]
    int CommonDialogFamily1BInit(uint param) => FamilyInit(DialogType.Libcdlg1B, param);

    [Hle("NID_63B08CA3", Nid = 0x63B08CA3, Library = "SceCommonDialog")]
    int CommonDialogFamily1BAbort() => State.Abort(DialogType.Libcdlg1B);

    /// <summary>NID_7030E4A6: the family's second argument-less request.</summary>
    [Hle("NID_7030E4A6", Nid = 0x7030E4A6, Library = "SceCommonDialog")]
    int CommonDialogFamily1BRequest() => FamilyRequest(DialogType.Libcdlg1B);

    [Hle("NID_500229F0", Nid = 0x500229F0, Library = "SceCommonDialog")]
    int CommonDialogFamily1BGetStatus() => State.StatusOf(DialogType.Libcdlg1B);

    [Hle("NID_631DC45C", Nid = 0x631DC45C, Library = "SceCommonDialog")]
    int CommonDialogFamily1BGetResult(uint result) => SimpleResult(DialogType.Libcdlg1B, result);

    [Hle("NID_675248CA", Nid = 0x675248CA, Library = "SceCommonDialog")]
    int CommonDialogFamily1BTerm() => State.Term(DialogType.Libcdlg1B);

    // ---- the system's Init of other families (FUN_810017B2: the family's OnInit, or OnInit2 with a block)

    /// <summary>NID_463DB9ED(param): libcdlg_twitter's Init of the Twitter dialog (family 0x15): sceTwDialogInit.</summary>
    [Hle("NID_463DB9ED", Nid = 0x463DB9ED, Library = "SceCommonDialog")]
    int CommonDialogTwitterInit(uint param) => sceTwDialogInitThroughEntry(param);

    /// <summary>NID_FCDA2F0D(param, block): libcdlg_twitter's photo import (family 0xB) with its block (OnInit2):
    /// as NID_452DA757.</summary>
    [Hle("NID_FCDA2F0D", Nid = 0xFCDA2F0D, Library = "SceCommonDialog")]
    int CommonDialogPhotoImportInit2(uint param, uint block) => scePhotoImportDialogInitThroughEntry(param);
}
