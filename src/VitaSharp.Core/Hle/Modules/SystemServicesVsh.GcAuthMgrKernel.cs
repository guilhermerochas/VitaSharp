using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

// The kernel exports of SceSblGcAuthMgr for the game card (FW 3.74 os0:kd/gcauthmgr.skprx, read in the module, study
// only): SceSblGcAuthMgrDrmBBForDriver (the PSP-style BB MAC and cipher contexts SceNpDrm uses for game card and PSP
// licenses, and the secret and hash of the authenticated game card), SceSblGcAuthMgrSclkForDriver (the secure clock
// exchange) and SceSblGcAuthMgrMlnpsnlForDriver (the Marlin personalisation). The emulated unit has no game card
// reader and does not run the secure processor: a card is never authenticated, so its secret stays clear and no hash
// matches; every MAC, cipher, clock and personalisation step answers "not supported" (0x80020004) after the module's
// argument checks; only the context setup and clearing, which the module does in its own memory, are done.
public sealed partial class SystemServicesVsh
{
    /// <summary>SceSblGcAuthMgr: a NULL argument, or no game card authenticated (0x808A040A).</summary>
    const int GCAUTH_ERR_INVALID = unchecked((int)0x808A040A);

    /// <summary>SceSblGcAuthMgr: a size that is not a multiple of 16 (0x808A0406).</summary>
    const int GCAUTH_ERR_ALIGNMENT = unchecked((int)0x808A0406);

    /// <summary>The BB MAC context (0x28 bytes): its type, the 16-byte key and pad, the buffered length.</summary>
    const uint BbMacContextSize = 0x28;

    /// <summary>(SceSblGcAuthMgrBbMacContext *context, int type): starts a BB MAC context of the type: NULL
    /// 0x808A040A.</summary>
    [Hle("NID_48D7784E", Nid = 0x48D7784E, Library = "SceSblGcAuthMgrDrmBBForDriver")]
    int GcAuthBbMacInit(uint context, uint type)
    {
        if (context == 0)
        {
            return GCAUTH_ERR_INVALID;
        }

        GuestMemory.W32(context, type);
        GuestMemory.Fill(context + 4, 0, BbMacContextSize - 4);
        return 0;
    }

    /// <summary>(SceSblGcAuthMgrBbCipherContext *context): clears a BB cipher context (0x18 bytes): NULL 0x808A040A.</summary>
    [Hle("NID_6E6D2B89", Nid = 0x6E6D2B89, Library = "SceSblGcAuthMgrDrmBBForDriver")]
    int GcAuthBbCipherClear(uint context)
    {
        if (context == 0)
        {
            return GCAUTH_ERR_INVALID;
        }

        GuestMemory.Fill(context, 0, 0x18);
        return 0;
    }

    /// <summary>BB MAC update and finish (context, data, size...): NULL 0x808A040A, then the MAC with the secure
    /// processor's keys: not supported.</summary>
    [Hle("NID_500F5157", Nid = 0x500F5157, Library = "SceSblGcAuthMgrDrmBBForDriver", Default = true)]
    [Hle("NID_6EF3C9DB", Nid = 0x6EF3C9DB, Library = "SceSblGcAuthMgrDrmBBForDriver", Default = true)]
    [Hle("NID_3C25F9FA", Nid = 0x3C25F9FA, Library = "SceSblGcAuthMgrDrmBBForDriver", Default = true)]
    [Hle("NID_E950BE32", Nid = 0xE950BE32, Library = "SceSblGcAuthMgrDrmBBForDriver", Default = true)]
    int GcAuthBbMac(uint context, uint data) => context == 0 || data == 0 ? GCAUTH_ERR_INVALID : KERNEL_ERR_UNSUP;

    /// <summary>(context, ..., kind 1 or 2, key): the BB cipher init: NULL context or key 0x808A040A, then not
    /// supported.</summary>
    [Hle("NID_7F98E4E2", Nid = 0x7F98E4E2, Library = "SceSblGcAuthMgrDrmBBForDriver", Default = true)]
    int GcAuthBbCipherInit(uint context, uint type, uint kind, uint key) =>
        context == 0 || key == 0 ? GCAUTH_ERR_INVALID : KERNEL_ERR_UNSUP;

    /// <summary>(context, data, size): the BB cipher update and finish: NULL 0x808A040A, a size not a multiple of 16
    /// 0x808A0406, then not supported.</summary>
    [Hle("NID_C0F37F18", Nid = 0xC0F37F18, Library = "SceSblGcAuthMgrDrmBBForDriver", Default = true)]
    [Hle("NID_B13577E2", Nid = 0xB13577E2, Library = "SceSblGcAuthMgrDrmBBForDriver", Default = true)]
    int GcAuthBbCipherUpdate(uint context, uint data, uint size)
    {
        if (context == 0 || data == 0)
        {
            return GCAUTH_ERR_INVALID;
        }

        return (size & 0xF) != 0 ? GCAUTH_ERR_ALIGNMENT : KERNEL_ERR_UNSUP;
    }

    /// <summary>The DRM BB key services the module runs on its own stack with the secure processor (the PSP license
    /// keys, the game card's DRM keys, the PSP document key): not supported.</summary>
    [Hle("NID_30A0B441", Nid = 0x30A0B441, Library = "SceSblGcAuthMgrDrmBBForDriver", Default = true)]
    [Hle("NID_4CEA150C", Nid = 0x4CEA150C, Library = "SceSblGcAuthMgrDrmBBForDriver", Default = true)]
    [Hle("NID_050DC6DF", Nid = 0x050DC6DF, Library = "SceSblGcAuthMgrDrmBBForDriver", Default = true)]
    [Hle("NID_4B506BE7", Nid = 0x4B506BE7, Library = "SceSblGcAuthMgrDrmBBForDriver", Default = true)]
    [Hle("NID_4C5DE1AA", Nid = 0x4C5DE1AA, Library = "SceSblGcAuthMgrDrmBBForDriver", Default = true)]
    [Hle("NID_4FE89ADB", Nid = 0x4FE89ADB, Library = "SceSblGcAuthMgrDrmBBForDriver", Default = true)]
    int GcAuthDrmBbSecure() => KERNEL_ERR_UNSUP;

    /// <summary>(): the card's DRM session without a card: 0x808A040A.</summary>
    [Hle("NID_535B87BC", Nid = 0x535B87BC, Library = "SceSblGcAuthMgrDrmBBForDriver", Default = true)]
    int GcAuthDrmBbNoCard() => GCAUTH_ERR_INVALID;

    /// <summary>ksceSblGcAuthMgrDrmBBGetCartSecret (void *secret 0x20): the secret of the authenticated card; none was
    /// authenticated, so it is clear: zeros and 0.</summary>
    [Hle("ksceSblGcAuthMgrDrmBBGetCartSecret", Nid = 0xBB70DDC0, Library = "SceSblGcAuthMgrDrmBBForDriver", Default = true)]
    int ksceSblGcAuthMgrDrmBBGetCartSecret(uint secret)
    {
        if (secret != 0)
        {
            GuestMemory.Fill(secret, 0, 0x20);
        }

        return 0;
    }

    /// <summary>ksceSblGcAuthMgrDrmBBCheckCartHash (const void *hash 0x14): whether the hash is the authenticated
    /// card's; no card is authenticated, so none matches: 0x808A040A.</summary>
    [Hle("ksceSblGcAuthMgrDrmBBCheckCartHash", Nid = 0x22FD5D23, Library = "SceSblGcAuthMgrDrmBBForDriver", Default = true)]
    int ksceSblGcAuthMgrDrmBBCheckCartHash(uint hash) => GCAUTH_ERR_INVALID;

    /// <summary>ksceSblGcAuthMgrDrmBBClearCartSecret (): clears the card's secret and hash (already clear): 0.</summary>
    [Hle("ksceSblGcAuthMgrDrmBBClearCartSecret", Nid = 0xBB451E83, Library = "SceSblGcAuthMgrDrmBBForDriver", Default = true)]
    int ksceSblGcAuthMgrDrmBBClearCartSecret() => 0;

    /// <summary>(int index, uint32_t *revoked): the card's revocation bits: none (0) and 0.</summary>
    [Hle("NID_812B2B5C", Nid = 0x812B2B5C, Library = "SceSblGcAuthMgrDrmBBForDriver", Default = true)]
    int GcAuthDrmBbRevocation(uint index, uint revoked)
    {
        if (revoked != 0)
        {
            GuestMemory.W32(revoked, 0);
        }

        return 0;
    }

    /// <summary>The secure clock exchange (SceSblGcAuthMgrSclkForDriver) and the Marlin personalisation
    /// (SceSblGcAuthMgrMlnpsnlForDriver, ksceSblGcAuthMgrMlnpsnlAuth1): the secure processor's: not supported.</summary>
    [Hle("NID_11D5570B", Nid = 0x11D5570B, Library = "SceSblGcAuthMgrSclkForDriver", Default = true)]
    [Hle("NID_F723A9BA", Nid = 0xF723A9BA, Library = "SceSblGcAuthMgrSclkForDriver", Default = true)]
    [Hle("ksceSblGcAuthMgrMlnpsnlAuth1", Nid = 0xDABC01F5, Library = "SceSblGcAuthMgrMlnpsnlForDriver", Default = true)]
    [Hle("NID_296ABD68", Nid = 0x296ABD68, Library = "SceSblGcAuthMgrMlnpsnlForDriver", Default = true)]
    int GcAuthSecureExchange() => KERNEL_ERR_UNSUP;
}
