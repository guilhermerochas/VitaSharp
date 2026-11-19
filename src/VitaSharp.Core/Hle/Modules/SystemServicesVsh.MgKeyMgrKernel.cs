namespace VitaSharp.Core.Hle.Modules;

// SceSblMgKeyMgrForDriver (os0:kd/mgkeymgr.skprx 3.74, read in the module, study only): the MagicGate (Memory Stick
// Duo) key manager SceMagicGate uses. Every entry but one takes only pointers (NULL: 0x800F1616), then starts the
// secure module os0:sm/mgkm_sm.self through SceSblSmComm and derives, wraps or unwraps 16-byte keys in the secure
// processor and DMAC5; the emulated unit has no secure processor, so those entries answer the HLE's "the secure
// service is not available" (0x80020004) after the module's checks, as defaults. NID_B6AC3AE1 is answered by the
// module itself: 0x800F1616 for a NULL pointer, else 0x800F1625 (the operation is not supported on 3.74).
public sealed partial class SystemServicesVsh
{
    /// <summary>SceSblMgKeyMgr: a NULL pointer (0x800F1616).</summary>
    const int MGKEY_ERR_INVALID = unchecked((int)0x800F1616);

    /// <summary>SceSblMgKeyMgr: the operation NID_B6AC3AE1 refuses (0x800F1625).</summary>
    const int MGKEY_ERR_UNSUPPORTED = unchecked((int)0x800F1625);

    /// <summary>The module's answer for its pointer arguments, then the secure processor's (not available).</summary>
    static int MgKeySecure(uint first, uint second, uint third = 1, uint fourth = 1)
    {
        if (first == 0 || second == 0 || third == 0 || fourth == 0)
        {
            return MGKEY_ERR_INVALID;
        }

        return KERNEL_ERR_UNSUP;
    }

    /// <summary>NID_B6AC3AE1 (SceSblMgKeyMgrForDriver)(void *a, void *b): 0x800F1616 for NULL, else 0x800F1625.</summary>
    [Hle("NID_B6AC3AE1", Nid = 0xB6AC3AE1, Library = "SceSblMgKeyMgrForDriver")]
    int MgKeyUnsupported(uint first, uint second)
    {
        return first == 0 || second == 0 ? MGKEY_ERR_INVALID : MGKEY_ERR_UNSUPPORTED;
    }

    /// <summary>NID_20B66ADA (SceSblMgKeyMgrForDriver)(const void *key, void *src, void *dst): secure.</summary>
    [Hle("NID_20B66ADA", Nid = 0x20B66ADA, Library = "SceSblMgKeyMgrForDriver", Default = true)]
    int MgKeyDecryptWithKey(uint key, uint source, uint destination)
    {
        return MgKeySecure(key, source, destination);
    }

    /// <summary>NID_5FECCE03 (SceSblMgKeyMgrForDriver)(const void *key, void *src, void *dst): secure.</summary>
    [Hle("NID_5FECCE03", Nid = 0x5FECCE03, Library = "SceSblMgKeyMgrForDriver", Default = true)]
    int MgKeyEncryptWithKey(uint key, uint source, uint destination)
    {
        return MgKeySecure(key, source, destination);
    }

    /// <summary>NID_EA534495 (SceSblMgKeyMgrForDriver)(a, b, c, d): two keys combined, then a third; secure.</summary>
    [Hle("NID_EA534495", Nid = 0xEA534495, Library = "SceSblMgKeyMgrForDriver", Default = true)]
    int MgKeyCombineEncrypt(uint first, uint second, uint third, uint fourth)
    {
        return MgKeySecure(first, second, third, fourth);
    }

    /// <summary>NID_DF3B808D (SceSblMgKeyMgrForDriver)(a, b, c, d): two keys combined, then a third; secure.</summary>
    [Hle("NID_DF3B808D", Nid = 0xDF3B808D, Library = "SceSblMgKeyMgrForDriver", Default = true)]
    int MgKeyCombineDecrypt(uint first, uint second, uint third, uint fourth)
    {
        return MgKeySecure(first, second, third, fourth);
    }

    /// <summary>NID_BC7D86EE (SceSblMgKeyMgrForDriver)(a, b, c): secure.</summary>
    [Hle("NID_BC7D86EE", Nid = 0xBC7D86EE, Library = "SceSblMgKeyMgrForDriver", Default = true)]
    int MgKeyBc7d(uint first, uint second, uint third)
    {
        return MgKeySecure(first, second, third);
    }

    /// <summary>NID_4C82A466 (SceSblMgKeyMgrForDriver)(a, b, c): secure.</summary>
    [Hle("NID_4C82A466", Nid = 0x4C82A466, Library = "SceSblMgKeyMgrForDriver", Default = true)]
    int MgKey4c82(uint first, uint second, uint third)
    {
        return MgKeySecure(first, second, third);
    }

    /// <summary>NID_D37C3DAE (SceSblMgKeyMgrForDriver)(a, b, c): secure.</summary>
    [Hle("NID_D37C3DAE", Nid = 0xD37C3DAE, Library = "SceSblMgKeyMgrForDriver", Default = true)]
    int MgKeyD37c(uint first, uint second, uint third)
    {
        return MgKeySecure(first, second, third);
    }

    /// <summary>NID_8CFCAA52 (SceSblMgKeyMgrForDriver)(a, b, c): secure.</summary>
    [Hle("NID_8CFCAA52", Nid = 0x8CFCAA52, Library = "SceSblMgKeyMgrForDriver", Default = true)]
    int MgKey8cfc(uint first, uint second, uint third)
    {
        return MgKeySecure(first, second, third);
    }

    /// <summary>NID_F8F856FC (SceSblMgKeyMgrForDriver)(a, b, c): secure.</summary>
    [Hle("NID_F8F856FC", Nid = 0xF8F856FC, Library = "SceSblMgKeyMgrForDriver", Default = true)]
    int MgKeyF8f8(uint first, uint second, uint third)
    {
        return MgKeySecure(first, second, third);
    }

    /// <summary>NID_8ECFFD7A (SceSblMgKeyMgrForDriver)(a, b, c): secure.</summary>
    [Hle("NID_8ECFFD7A", Nid = 0x8ECFFD7A, Library = "SceSblMgKeyMgrForDriver", Default = true)]
    int MgKey8ecf(uint first, uint second, uint third)
    {
        return MgKeySecure(first, second, third);
    }

    /// <summary>NID_71FA16B0 (SceSblMgKeyMgrForDriver)(a, b): secure.</summary>
    [Hle("NID_71FA16B0", Nid = 0x71FA16B0, Library = "SceSblMgKeyMgrForDriver", Default = true)]
    int MgKey71fa(uint first, uint second)
    {
        return MgKeySecure(first, second);
    }

    /// <summary>NID_1C5388D0 (SceSblMgKeyMgrForDriver)(a, b): secure (two secure module commands).</summary>
    [Hle("NID_1C5388D0", Nid = 0x1C5388D0, Library = "SceSblMgKeyMgrForDriver", Default = true)]
    int MgKey1c53(uint first, uint second)
    {
        return MgKeySecure(first, second);
    }

    /// <summary>NID_1263FB04 (SceSblMgKeyMgrForDriver)(a, b) (SceMagicGate): secure (two secure module commands).</summary>
    [Hle("NID_1263FB04", Nid = 0x1263FB04, Library = "SceSblMgKeyMgrForDriver", Default = true)]
    int MgKey1263(uint first, uint second)
    {
        return MgKeySecure(first, second);
    }
}
