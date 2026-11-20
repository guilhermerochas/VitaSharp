using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

// SceSblPostSsMgrForDriver (FW 3.74 bootimage SceSblPostSsMgr, read in the module, study only): the services the
// kernel builds on the secure processor after boot. What needs the secure processor (the sealed keys of PFS, the
// keystone of the parental passcode, the coredump and cloud data keys, the application keys, the product mode and SD
// commands of pm_sm, the activation, the utoken verification and the signature check of a signed param.sfo) keeps
// the module's argument checks and then answers "not supported" (0x80020004): the emulated unit does not run the
// secure processor, and no answer may stand for a key, a signature or an authentication it did not do. What the
// module answers from its own state is answered as on a retail unit (CEX, no utoken, no communication processor):
// the utoken flags are clear, the licence never expires, the physical CP clock is absent; the signed param.sfo is
// read into a kernel block as the module reads it.
public sealed partial class SystemServicesVsh
{
    /// <summary>SceSblPostSsMgr: a NULL pointer or a bad argument (0x800F0016).</summary>
    const int POSTSS_ERR_INVALID = unchecked((int)0x800F0016);

    /// <summary>A sealed key or keystone whose magic does not match (0x800F0024).</summary>
    const int POSTSS_ERR_MAGIC = unchecked((int)0x800F0024);

    /// <summary>A sealed key or keystone of a version the module does not know (0x800F0037).</summary>
    const int POSTSS_ERR_VERSION = unchecked((int)0x800F0037);

    /// <summary>A keystone of another passcode type (0x800F0002).</summary>
    const int POSTSS_ERR_KEYSTONE_TYPE = unchecked((int)0x800F0002);

    /// <summary>SceSblSsMgr's coredump and cloud data services: a bad argument; the key store not initialised.</summary>
    const int SSMGR_ERR_INVALID = unchecked((int)0x800F1716);
    const int SSMGR_ERR_NOT_INITIALIZED = unchecked((int)0x800F1727);

    /// <summary>SceSblPmMgr: a bad argument (0x800F0A16).</summary>
    const int PMMGR_ERR_INVALID = unchecked((int)0x800F0A16);

    /// <summary>SceSblRtcMgr: no communication processor (a retail unit has none: 0x800F1002).</summary>
    const int RTCMGR_ERR_NO_CP = unchecked((int)0x800F1002);

    /// <summary>SceSblLicMgr: activation is only for test and development units (0x800F1325).</summary>
    const int LICMGR_ERR_NOT_TOOL = unchecked((int)0x800F1325);

    /// <summary>SceSblUtMgr: a bad argument (0x800F1A16); no trilithium buffer (0x800F1A02).</summary>
    const int UTMGR_ERR_INVALID = unchecked((int)0x800F1A16);
    const int UTMGR_ERR_NO_TRILITHIUM = unchecked((int)0x800F1A02);

    /// <summary>SceSblSpsfoMgr: a bad path or handle (0x800F1B16); NULL or a file too large (0x800F1B24); no memory
    /// (0x800F1B0C); a bad signed param.sfo (0x800F1B16 as the verification's argument check).</summary>
    const int SPSFO_ERR_PATH = unchecked((int)0x800F1B16);
    const int SPSFO_ERR_INVALID = unchecked((int)0x800F1B24);
    const int SPSFO_ERR_NO_MEMORY = unchecked((int)0x800F1B0C);

    /// <summary>The kernel's "not found" and "I/O error" the signed param.sfo open answers.</summary>
    const int SPSFO_ERR_OPEN = unchecked((int)0x80010002);
    const int SPSFO_ERR_READ = unchecked((int)0x80010005);

    /// <summary>The largest signed param.sfo the module reads.</summary>
    const uint SpsfoMaxSize = 0x8000;

    // ------------------------------------------------------------------ sealed keys and keystones (secure)

    /// <summary>ksceSblPostSsMgrEncryptSealedkey (SceSblPostSsMgrSealedkey *out 0x50): a new sealed key of PFS;
    /// NULL 0x800F0016.</summary>
    [Hle("ksceSblPostSsMgrEncryptSealedkey", Nid = 0x08525D8D, Library = "SceSblPostSsMgrForDriver", Default = true)]
    int ksceSblPostSsMgrEncryptSealedkey(uint sealedKey) => sealedKey == 0 ? POSTSS_ERR_INVALID : KERNEL_ERR_UNSUP;

    /// <summary>The magic and version check of a sealed key ("pfsSKKey", version 0..2) or a keystone ("keystone",
    /// version 1).</summary>
    static int SealedHeaderCheck(uint data, ReadOnlySpan<byte> magic, ushort maxVersion, ushort exactVersion)
    {
        if (!GuestMemory.ReadSpan(data, magic.Length).SequenceEqual(magic))
        {
            return POSTSS_ERR_MAGIC;
        }

        ushort version = GuestMemory.Read<ushort>(data + 8);
        bool known = exactVersion != 0 ? version == exactVersion : version <= maxVersion;
        return known ? 0 : POSTSS_ERR_VERSION;
    }

    /// <summary>ksceSblPostSsMgrDecryptSealedkey (const SceSblPostSsMgrSealedkey *sealed, void *key 0x10): NULL
    /// 0x800F0016, a bad magic 0x800F0024, an unknown version 0x800F0037.</summary>
    [Hle("ksceSblPostSsMgrDecryptSealedkey", Nid = 0x33275F95, Library = "SceSblPostSsMgrForDriver", Default = true)]
    int ksceSblPostSsMgrDecryptSealedkey(uint sealedKey, uint key)
    {
        if (sealedKey == 0 || key == 0)
        {
            return POSTSS_ERR_INVALID;
        }

        int header = SealedHeaderCheck(sealedKey, "pfsSKKey"u8, 2, 0);
        return header != 0 ? header : KERNEL_ERR_UNSUP;
    }

    /// <summary>ksceSblPostSsMgrDebugEncryptKeystone (const void *in, SceSblPostSsMgrKeystone *out 0x40): NULL
    /// 0x800F0016.</summary>
    [Hle("ksceSblPostSsMgrDebugEncryptKeystone", Nid = 0x42474C8B, Library = "SceSblPostSsMgrForDriver", Default = true)]
    int ksceSblPostSsMgrDebugEncryptKeystone(uint input, uint keystone) =>
        input == 0 || keystone == 0 ? POSTSS_ERR_INVALID : KERNEL_ERR_UNSUP;

    /// <summary>ksceSblPostSsMgrDebugDecryptKeystone (const SceSblPostSsMgrKeystone *keystone, void *out): NULL
    /// 0x800F0016, a bad magic 0x800F0024, a version other than 1 0x800F0037.</summary>
    [Hle("ksceSblPostSsMgrDebugDecryptKeystone", Nid = 0xCC5AA5A5, Library = "SceSblPostSsMgrForDriver", Default = true)]
    int ksceSblPostSsMgrDebugDecryptKeystone(uint keystone, uint output)
    {
        if (keystone == 0 || output == 0)
        {
            return POSTSS_ERR_INVALID;
        }

        int header = SealedHeaderCheck(keystone, "keystone"u8, 0, 1);
        return header != 0 ? header : KERNEL_ERR_UNSUP;
    }

    /// <summary>ksceSblPostSsMgrVerifyKeystone (const SceSblPostSsMgrKeystone *keystone, int type): NULL 0x800F0016,
    /// a keystone of another type (+0xA) 0x800F0002.</summary>
    [Hle("ksceSblPostSsMgrVerifyKeystone", Nid = 0xDDA6FA6D, Library = "SceSblPostSsMgrForDriver", Default = true)]
    int ksceSblPostSsMgrVerifyKeystone(uint keystone, uint type)
    {
        if (keystone == 0)
        {
            return POSTSS_ERR_INVALID;
        }

        return GuestMemory.Read<ushort>(keystone + 0xA) == type ? KERNEL_ERR_UNSUP : POSTSS_ERR_KEYSTONE_TYPE;
    }

    /// <summary>ksceSblPostSsMgrVerifyKeystoneWithPasscode (const SceSblPostSsMgrKeystone *keystone, const char
    /// *passcode): NULL 0x800F0016.</summary>
    [Hle("ksceSblPostSsMgrVerifyKeystoneWithPasscode", Nid = 0xF86F1452, Library = "SceSblPostSsMgrForDriver", Default = true)]
    int ksceSblPostSsMgrVerifyKeystoneWithPasscode(uint keystone, uint passcode) =>
        keystone == 0 || passcode == 0 ? POSTSS_ERR_INVALID : KERNEL_ERR_UNSUP;

    /// <summary>ksceSblPostSsMgrGenerateAppKey (const SceSblPostSsMgrAppKeyParam *param (+0 0x10 or 0x20), void
    /// *key): NULL or another size 0x800F0016.</summary>
    [Hle("ksceSblPostSsMgrGenerateAppKey", Nid = 0x2646DE64, Library = "SceSblPostSsMgrForDriver", Default = true)]
    int ksceSblPostSsMgrGenerateAppKey(uint param, uint key)
    {
        if (param == 0 || key == 0 || GuestMemory.R32(param) is not (0x10 or 0x20))
        {
            return POSTSS_ERR_INVALID;
        }

        return KERNEL_ERR_UNSUP;
    }

    // ------------------------------------------------------------------ coredump and cloud data keys (secure)

    /// <summary>ksceSblCoredumpKeyStoreInitialize (): the coredump keys come from the secure processor.</summary>
    [Hle("ksceSblCoredumpKeyStoreInitialize", Nid = 0xC977601E, Library = "SceSblPostSsMgrForDriver", Default = true)]
    int ksceSblCoredumpKeyStoreInitialize() => KERNEL_ERR_UNSUP;

    /// <summary>ksceSblCoredumpKeyStoreFinalize (): clears the key store (it holds nothing here): 0.</summary>
    [Hle("ksceSblCoredumpKeyStoreFinalize", Nid = 0x4100B9E9, Library = "SceSblPostSsMgrForDriver")]
    int ksceSblCoredumpKeyStoreFinalize() => 0;

    /// <summary>ksceSblCoredumpGetHmacKey (int type 1..3, SceSize size 0x20, void *key): other arguments 0x800F1716;
    /// the key store is never initialised (its keys are the secure processor's): 0x800F1727.</summary>
    [Hle("ksceSblCoredumpGetHmacKey", Nid = 0x2C463AF1, Library = "SceSblPostSsMgrForDriver", Default = true)]
    int ksceSblCoredumpGetHmacKey(uint type, uint size, uint key)
    {
        if (type - 1 >= 3 || size != 0x20 || key == 0)
        {
            return SSMGR_ERR_INVALID;
        }

        return SSMGR_ERR_NOT_INITIALIZED;
    }

    /// <summary>ksceSblCoredumpGetAesKey (int type 1..4 or 0x10000001..2, SceSize size 0x10, void *key): other
    /// arguments 0x800F1716; the key store is never initialised: 0x800F1727.</summary>
    [Hle("ksceSblCoredumpGetAesKey", Nid = 0xCB5436BD, Library = "SceSblPostSsMgrForDriver", Default = true)]
    int ksceSblCoredumpGetAesKey(uint type, uint size, uint key)
    {
        bool known = type < 0x10000000 ? type - 1 < 4 : type - 0x10000001 < 2;
        if (!known || size != 0x10 || key == 0)
        {
            return SSMGR_ERR_INVALID;
        }

        return SSMGR_ERR_NOT_INITIALIZED;
    }

    /// <summary>ksceSblSsMgrCloudDataGetEncDecCryptHandle (void *handle): NULL 0x800F1716.</summary>
    [Hle("ksceSblSsMgrCloudDataGetEncDecCryptHandle", Nid = 0xC6684F7E, Library = "SceSblPostSsMgrForDriver", Default = true)]
    int ksceSblSsMgrCloudDataGetEncDecCryptHandle(uint handle) => handle == 0 ? SSMGR_ERR_INVALID : KERNEL_ERR_UNSUP;

    /// <summary>ksceSblSsMgrCloudDataGetSignCryptHandle (int kind 1, int index 0..1, void *handle): other arguments
    /// 0x800F1716.</summary>
    [Hle("ksceSblSsMgrCloudDataGetSignCryptHandle", Nid = 0x739C981E, Library = "SceSblPostSsMgrForDriver", Default = true)]
    int ksceSblSsMgrCloudDataGetSignCryptHandle(uint kind, uint index, uint handle) =>
        kind != 1 || index > 1 || handle == 0 ? SSMGR_ERR_INVALID : KERNEL_ERR_UNSUP;

    /// <summary>ksceSblSsMgrCloudDataStop (): clears the cloud data keys (none are held): 0.</summary>
    [Hle("ksceSblSsMgrCloudDataStop", Nid = 0x2BF04B8E, Library = "SceSblPostSsMgrForDriver")]
    int ksceSblSsMgrCloudDataStop() => 0;

    // ------------------------------------------------------------------ product mode, secure clock, licence

    /// <summary>ksceSblPostSsMgrExecutePmSmF00dCommand8 (void *arg): NULL 0x800F0A16; the command is pm_sm's.</summary>
    [Hle("ksceSblPostSsMgrExecutePmSmF00dCommand8", Nid = 0x4663C195, Library = "SceSblPostSsMgrForDriver", Default = true)]
    int ksceSblPostSsMgrExecutePmSmF00dCommand8(uint argument) => argument == 0 ? PMMGR_ERR_INVALID : KERNEL_ERR_UNSUP;

    /// <summary>ksceSblPostSsMgrExecutePmSmF00dCommand (int flags): a pm_sm command.</summary>
    [Hle("ksceSblPostSsMgrExecutePmSmF00dCommand", Nid = 0xADF92824, Library = "SceSblPostSsMgrForDriver", Default = true)]
    int ksceSblPostSsMgrExecutePmSmF00dCommand(uint flags) => KERNEL_ERR_UNSUP;

    /// <summary>ksceSblPmMgrSetSdModeOff (int zero): anything but 0 is 0x800F0A16; the command is pm_sm's.</summary>
    [Hle("ksceSblPmMgrSetSdModeOff", Nid = 0xFE92A318, Library = "SceSblPostSsMgrForDriver", Default = true)]
    int ksceSblPmMgrSetSdModeOff(uint zero) => zero != 0 ? PMMGR_ERR_INVALID : KERNEL_ERR_UNSUP;

    /// <summary>ksceSblPostSsMgrExecutePmSmSdF00dCommand (): the SD mode command of sd0:sm/pm_sm_sd.self.</summary>
    [Hle("ksceSblPostSsMgrExecutePmSmSdF00dCommand", Nid = 0x19B63D65, Library = "SceSblPostSsMgrForDriver", Default = true)]
    int ksceSblPostSsMgrExecutePmSmSdF00dCommand() => KERNEL_ERR_UNSUP;

    /// <summary>ksceSblRtcMgrGetCpRtcPhysical (SceRtcTick *tick): the clock of a development kit's communication
    /// processor; a retail unit has none: 0x800F1002.</summary>
    [Hle("ksceSblRtcMgrGetCpRtcPhysical", Nid = 0x942010A0, Library = "SceSblPostSsMgrForDriver", Default = true)]
    int ksceSblRtcMgrGetCpRtcPhysical(uint tick) => RTCMGR_ERR_NO_CP;

    /// <summary>ksceSblRtcMgrSetCpRtcPhysical (SceRtcTick *tick, ...): no communication processor: 0x800F1002.</summary>
    [Hle("ksceSblRtcMgrSetCpRtcPhysical", Nid = 0x3F9BDEDF, Library = "SceSblPostSsMgrForDriver", Default = true)]
    int ksceSblRtcMgrSetCpRtcPhysical(uint tick) => RTCMGR_ERR_NO_CP;

    /// <summary>ksceSblPostSsMgrGetExpireDate (SceUInt32 *date, int current): a retail unit's licence does not
    /// expire: 0xFFFFFFFF and 0 (a test or development unit reads its activation).</summary>
    [Hle("ksceSblPostSsMgrGetExpireDate", Nid = 0x4FF2682F, Library = "SceSblPostSsMgrForDriver")]
    int ksceSblPostSsMgrGetExpireDate(uint date, uint current)
    {
        bool retail = (ksceSblAimgrIsTool() == 0 || ksceSblAimgrIsToolRev4() != 0) && ksceSblAimgrIsDEX() == 0;
        if (retail)
        {
            GuestMemory.W32(date, 0xFFFFFFFF);
            return 0;
        }

        return KERNEL_ERR_UNSUP;
    }

    /// <summary>ksceSblPostSsMgrActivate (const char *path): the activation of a test or development unit; a retail
    /// one answers 0x800F1325.</summary>
    [Hle("ksceSblPostSsMgrActivate", Nid = 0x0298382B, Library = "SceSblPostSsMgrForDriver", Default = true)]
    int ksceSblPostSsMgrActivate(uint path)
    {
        if (ksceSblAimgrIsTool() == 0)
        {
            return ksceSblAimgrIsDEX() == 0 ? LICMGR_ERR_NOT_TOOL : KERNEL_ERR_UNSUP;
        }

        if (ksceSblAimgrIsToolRev4() != 0 && ksceSblAimgrIsDEX() == 0)
        {
            return LICMGR_ERR_NOT_TOOL;
        }

        return KERNEL_ERR_UNSUP;
    }

    // ------------------------------------------------------------------ the utoken (none on a retail unit)

    /// <summary>The flags the module reads from the verified utoken at boot: a retail unit has no utoken, so every
    /// flag is clear (ksceSblUtMgrHasStoreFlag, HasComTestFlag, HasNpTestFlag, HasFlag1/6/7/8/9).</summary>
    [Hle("ksceSblUtMgrHasStoreFlag", Nid = 0x9D2E2D39, Library = "SceSblPostSsMgrForDriver", ReturnsValue = true, Default = true)]
    [Hle("ksceSblUtMgrHasComTestFlag", Nid = 0x7ACCAA50, Library = "SceSblPostSsMgrForDriver", ReturnsValue = true, Default = true)]
    [Hle("ksceSblUtMgrHasNpTestFlag", Nid = 0x9FD835B0, Library = "SceSblPostSsMgrForDriver", ReturnsValue = true, Default = true)]
    [Hle("ksceSblUtMgrHasFlag1", Nid = 0x22599675, Library = "SceSblPostSsMgrForDriver", ReturnsValue = true, Default = true)]
    [Hle("ksceSblUtMgrHasFlag6", Nid = 0x9D1D8EE5, Library = "SceSblPostSsMgrForDriver", ReturnsValue = true, Default = true)]
    [Hle("ksceSblUtMgrHasFlag7", Nid = 0xC93C0A0D, Library = "SceSblPostSsMgrForDriver", ReturnsValue = true, Default = true)]
    [Hle("ksceSblUtMgrHasFlag8", Nid = 0x9B49C249, Library = "SceSblPostSsMgrForDriver", ReturnsValue = true, Default = true)]
    [Hle("ksceSblUtMgrHasFlag9", Nid = 0x1923D80D, Library = "SceSblPostSsMgrForDriver", ReturnsValue = true, Default = true)]
    int ksceSblUtMgrHasFlag() => 0;

    /// <summary>ksceSblUtMgrIsAllowComTest (pid): the communication test flag and the utoken's program: none.</summary>
    [Hle("ksceSblUtMgrIsAllowComTest", Nid = 0x128FB35A, Library = "SceSblPostSsMgrForDriver", ReturnsValue = true, Default = true)]
    int ksceSblUtMgrIsAllowComTest(int pid) => 0;

    /// <summary>ksceSblUtMgrIsAllowProgramDebug (SceUInt64 authId): the utoken's debug list: none.</summary>
    [Hle("ksceSblUtMgrIsAllowProgramDebug", Nid = 0xD8A2D465, Library = "SceSblPostSsMgrForDriver", ReturnsValue = true, Default = true)]
    int ksceSblUtMgrIsAllowProgramDebug(uint authIdLow, uint authIdHigh) => 0;

    /// <summary>ksceSblUtMgrGetTrilithiumBuffer (void *buffer 0x58): only with a utoken: 0x800F1A02.</summary>
    [Hle("ksceSblUtMgrGetTrilithiumBuffer", Nid = 0xABDD68CD, Library = "SceSblPostSsMgrForDriver", Default = true)]
    int ksceSblUtMgrGetTrilithiumBuffer(uint buffer) => UTMGR_ERR_NO_TRILITHIUM;

    /// <summary>ksceSblUtMgrVerifyAndStoreUtoken (const void *utoken, SceSize size): NULL or less than 0x800 bytes
    /// 0x800F1A16; the verification is the secure processor's.</summary>
    [Hle("ksceSblUtMgrVerifyAndStoreUtoken", Nid = 0xC2E58CE3, Library = "SceSblPostSsMgrForDriver", Default = true)]
    int ksceSblUtMgrVerifyAndStoreUtoken(uint utoken, uint size) =>
        utoken == 0 || size < 0x800 ? UTMGR_ERR_INVALID : KERNEL_ERR_UNSUP;

    // ------------------------------------------------------------------ signed param.sfo and RSA

    /// <summary>
    /// ksceSblSpsfoMgrOpen (const char *path, SceSblSpsfoHandle *handle {SceUID block; void *data; SceSize size}):
    /// reads the file into a kernel block ("SceSblSpsfoMgr"). NULL 0x800F1B24; a path outside gro0:, ur0: and ux0:
    /// (host0: only with the QA flag) or of 0x100 characters 0x800F1B16; a missing file 0x80010002; more than 0x8000
    /// bytes 0x800F1B24; a failed read 0x80010005.
    /// </summary>
    [Hle("ksceSblSpsfoMgrOpen", Nid = 0xBDF18922, Library = "SceSblPostSsMgrForDriver")]
    int ksceSblSpsfoMgrOpen(uint path, uint handle)
    {
        if (path == 0 || handle == 0)
        {
            return SPSFO_ERR_INVALID;
        }

        GuestMemory.Fill(handle, 0, 12);
        string name = GuestMemory.ReadCString(path, 0x101);
        bool device = name.StartsWith("gro0:", StringComparison.Ordinal) || name.StartsWith("ur0:", StringComparison.Ordinal)
            || name.StartsWith("ux0:", StringComparison.Ordinal)
            || (ksceSblQafMgrIsAllowHost0Access() != 0 && ksceSblAimgrIsCEX() == 0 && name.StartsWith("host0:", StringComparison.Ordinal));
        if (!device || name.Length > 0xFF)
        {
            return SPSFO_ERR_PATH;
        }

        string? host = Emu.Vfs.Resolve(name);
        if (host == null || !File.Exists(host))
        {
            return SPSFO_ERR_OPEN;
        }

        byte[] data;
        try
        {
            data = File.ReadAllBytes(host);
        }
        catch (IOException)
        {
            return SPSFO_ERR_READ;
        }

        if ((uint)data.Length > SpsfoMaxSize)
        {
            return SPSFO_ERR_INVALID;
        }

        uint block = (uint)Mem.Alloc((uint)Math.Max(data.Length, 1), "SceSblSpsfoMgr", MemoryRegion.Kernel);
        if (block == 0)
        {
            return SPSFO_ERR_NO_MEMORY;
        }

        data.CopyTo(GuestMemory.Span(block, data.Length));
        lock (_spsfoBlocks)
        {
            int id = ++_nextSpsfoBlock;
            _spsfoBlocks[id] = block;
            GuestMemory.W32(handle, (uint)id);
        }

        GuestMemory.W32(handle + 4, block);
        GuestMemory.W32(handle + 8, (uint)data.Length);
        return 0;
    }

    /// <summary>The blocks ksceSblSpsfoMgrOpen read the files into, by the id it gave the handle (positive).</summary>
    [SaveState.SnapshotMissing] // states of builds before it: no file was open
    readonly Dictionary<int, uint> _spsfoBlocks = new();

    [SaveState.SnapshotMissing]
    int _nextSpsfoBlock;

    /// <summary>ksceSblSpsfoMgrClose (SceSblSpsfoHandle *handle): frees the block of an opened file; NULL
    /// 0x800F1B16.</summary>
    [Hle("ksceSblSpsfoMgrClose", Nid = 0xAD3B0078, Library = "SceSblPostSsMgrForDriver")]
    int ksceSblSpsfoMgrClose(uint handle)
    {
        if (handle == 0)
        {
            return SPSFO_ERR_PATH;
        }

        int id = (int)GuestMemory.R32(handle);
        if (id > 0)
        {
            lock (_spsfoBlocks)
            {
                if (_spsfoBlocks.Remove(id, out uint block))
                {
                    Mem.Free(block);
                }
            }
        }

        return 0;
    }

    /// <summary>ksceSblSpsfoMgrVerify (const SceSblSpsfoHandle *handle, ...): NULL, no data or more than 0x8000 bytes
    /// 0x800F1B16; the signature of the signed param.sfo is the secure processor's to check.</summary>
    [Hle("ksceSblSpsfoMgrVerify", Nid = 0x686B9461, Library = "SceSblPostSsMgrForDriver", Default = true)]
    int ksceSblSpsfoMgrVerify(uint handle, uint first, uint second)
    {
        if (handle == 0 || first == 0 || second == 0 || GuestMemory.R32(handle + 4) == 0
            || GuestMemory.R32(handle + 8) > SpsfoMaxSize)
        {
            return SPSFO_ERR_PATH;
        }

        return KERNEL_ERR_UNSUP;
    }

    /// <summary>ksceSblRSA2048CreateSignature / ksceSblRSA2048VerifySignature: the module's RSA-2048 PKCS#1 v1.5
    /// signing and checking with the key the caller gives. Not implemented: answered as unsupported, never as a valid
    /// signature.</summary>
    [Hle("ksceSblRSA2048CreateSignature", Nid = 0x07BAD056, Library = "SceSblPostSsMgrForDriver", Default = true)]
    [Hle("ksceSblRSA2048VerifySignature", Nid = 0x3DB69911, Library = "SceSblPostSsMgrForDriver", Default = true)]
    int ksceSblRSA2048Signature() => KERNEL_ERR_UNSUP;
}
