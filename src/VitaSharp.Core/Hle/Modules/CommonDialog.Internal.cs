using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// The second Init entries of libcdlg.suprx 3.74 (decompiled, study only): the module's family table holds, next to
/// the public Init(param), a variant Init (the "OnInit2" path) that the system apps call; it takes the same slot (the
/// family's type number) and starts the same dialog, some with a second block the module copies (the net check and
/// companion ones refuse a NULL block with SCE_COMMON_DIALOG_ERROR_NULL; the Facebook one also a block whose first
/// word is 0, with 0x80106103). The HLE's families do not draw a dialog and finish at once, so the variant starts the
/// family through its public Init after those checks; the variants' other parameter checks are not modelled.
/// </summary>
public sealed partial class CommonDialog
{
    const int FacebookErrInvalidParam = unchecked((int)0x80106103);

    /// <summary>NID_243D6A36(param, block): the variant Init of the net check dialog (libcdlg family 0x3): sceNetCheckDialogInit.</summary>
    [Hle(Nid = 0x243D6A36, Library = "SceCommonDialog")]
    int NID_243D6A36(uint param, uint block)
    {
        if (param == 0 || block == 0)
        {
            return CommonDialogError.NULL;
        }

        return sceNetCheckDialogInitThroughEntry(param);
    }

    /// <summary>NID_40CD30CD(param): the variant Init of the NP friend list dialog (libcdlg family 0x9): sceNpFriendListDialogInit.</summary>
    [Hle(Nid = 0x40CD30CD, Library = "SceCommonDialog")]
    int NID_40CD30CD(uint param) => sceNpFriendListDialogInitThroughEntry(param);

    /// <summary>NID_452DA757(param, block): the variant Init of the photo import dialog (libcdlg family 0xB): scePhotoImportDialogInit.</summary>
    [Hle(Nid = 0x452DA757, Library = "SceCommonDialog")]
    int NID_452DA757(uint param, uint block) => scePhotoImportDialogInitThroughEntry(param);

    /// <summary>NID_3D3F9AD6(param): the variant Init of the NP profile dialog (libcdlg family 0xC): sceNpProfileDialogInit.</summary>
    [Hle(Nid = 0x3D3F9AD6, Library = "SceCommonDialog")]
    int NID_3D3F9AD6(uint param) => sceNpProfileDialogInitThroughEntry(param);

    /// <summary>NID_AC20DAF9(param, block): the variant Init of the photo review dialog (libcdlg family 0xD): scePhotoReviewDialogInit.</summary>
    [Hle(Nid = 0xAC20DAF9, Library = "SceCommonDialog")]
    int NID_AC20DAF9(uint param, uint block) => scePhotoReviewDialogInitThroughEntry(param);

    /// <summary>NID_FEBF87EB(param, block): the variant Init of the camera import dialog (libcdlg family 0xF): sceCameraImportDialogInit.</summary>
    [Hle(Nid = 0xFEBF87EB, Library = "SceCommonDialog")]
    int NID_FEBF87EB(uint param, uint block) => sceCameraImportDialogInitThroughEntry(param);

    /// <summary>NID_075E4490(param, block): the variant Init of the Facebook dialog (libcdlg family 0x11): sceNpSnsFacebookDialogInit.</summary>
    [Hle(Nid = 0x075E4490, Library = "SceCommonDialog")]
    int NID_075E4490(uint param, uint block)
    {
        if (param == 0 || block == 0)
        {
            return CommonDialogError.NULL;
        }

        if (GuestMemory.R32(block) == 0)
        {
            return FacebookErrInvalidParam;
        }

        return sceNpSnsFacebookDialogInitThroughEntry(param);
    }

    /// <summary>NID_AA624C1F(param): the variant Init of the NP friend list 2 dialog (libcdlg family 0x17): sceNpFriendList2DialogInit.</summary>
    [Hle(Nid = 0xAA624C1F, Library = "SceCommonDialog")]
    int NID_AA624C1F(uint param) => sceNpFriendList2DialogInitThroughEntry(param);

    /// <summary>NID_62B60B87(param, block): the variant Init of the companion util dialog (libcdlg family 0x18): sceCompanionUtilDialogInit.</summary>
    [Hle(Nid = 0x62B60B87, Library = "SceCommonDialog")]
    int NID_62B60B87(uint param, uint block)
    {
        if (param == 0 || block == 0)
        {
            return CommonDialogError.NULL;
        }

        return sceCompanionUtilDialogInitThroughEntry(param);
    }

    /// <summary>NID_9800F871(param, block): the variant Init of the IME dialog (libcdlg family 0x4): its OnInit2 checks
    /// the param as the public Init does and ignores the block: sceImeDialogInit.</summary>
    [Hle(Nid = 0x9800F871, Library = "SceCommonDialog")]
    int NID_9800F871(uint param, uint block) => sceImeDialogInitThroughEntry(param);

    /// <summary>NID_FBE52F6D(param, block): the variant Init of the video import dialog (libcdlg family 0x1C): a NULL
    /// param is SCE_COMMON_DIALOG_ERROR_NULL; the block's first word (a count) must be 1..255 and the next 32 bytes
    /// zero, or SCE_COMMON_DIALOG_ERROR_INVALID_ARGUMENT; then sceVideoImportDialogInit.</summary>
    [Hle(Nid = 0xFBE52F6D, Library = "SceCommonDialog")]
    int NID_FBE52F6D(uint param, uint block)
    {
        if (param == 0)
        {
            return CommonDialogError.NULL;
        }

        if (block == 0)
        {
            return CommonDialogError.NULL; // the module reads the block without a check
        }

        uint count = GuestMemory.R32(block);
        if (count is < 1 or > 0xFF)
        {
            return CommonDialogError.INVALID_ARGUMENT;
        }

        for (uint offset = 4; offset < 0x24; offset++)
        {
            if (GuestMemory.Read<byte>(block + offset) != 0)
            {
                return CommonDialogError.INVALID_ARGUMENT;
            }
        }

        return sceVideoImportDialogInitThroughEntry(param);
    }
}
