using System.Buffers.Binary;
using System.Security.Cryptography;
using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

// The rest of SceNpDrmForDriver (bootimage SceNpDrm 3.74, read in the module, study only), on the same license and
// activation reading as GetRifInfo:
// - the license file names: "<32 hex digits>.rif", the AES-128 encryption (with a key of the SceNpDrm module itself)
//   of the 16 bytes { flag (1 for the fixed name of a free or game card license), 0, account id }. The key is read
//   from the installed firmware's SceNpDrm (FirmwareKernelImage, 3.74's module by its NID), never from the emulator;
//   without it only the fixed name of account 0 (the one every installer of the scene uses) is known, and the other
//   names answer "not supported". GetRifNameForInstall names by content id or by the license's account.
// - the activation data (tm0:npdrm/act.dat, 0x1038 bytes): read and checked as GetRifInfo's activation check reads
//   it (its account, and for version 2 its OpenPSID); the module's signature check of the file is not reproduced,
//   so writing an activation (WriteActData) or re-signing a license (PresetRifProvisionalFlag, VerifyRifFull) is the
//   secure side's and answers "not supported" after the checks, never a result that activates or validates.
// - the current account (UpdateAccountId: the registry's /CONFIG/NP/account_id when given 0) and the debug settings
//   (loose account binding only on a QA unit: off).
// - the PSP eboot signatures and the legacy document key are made by the secure processor: "not supported" after
//   the argument checks.
public sealed partial class SystemServicesVsh
{
    /// <summary>SceNpDrm: no account to name the license for (0x8087000C).</summary>
    const int NPDRM_ERR_NO_ACCOUNT = unchecked((int)0x8087000C);

    /// <summary>SceNpDrm: activation data of the wrong size (0x80870002).</summary>
    const int NPDRM_ERR_ACT_SIZE = unchecked((int)0x80870002);

    /// <summary>The module NID of FW 3.74's SceNpDrm, and where its license name key is in its first segment.</summary>
    const uint NpDrm374ModuleNid = 0xAC6D91D5, RifNameKeyOffset = 0x11290;

    /// <summary>The size of the activation data the module reads.</summary>
    const int ActDataSize = 0x1038;

    /// <summary>The account the unit is signed in to (ksceNpDrmUpdateAccountId), or null before the first update.</summary>
    [SaveState.SnapshotMissing] // states of builds before it: never updated
    ulong? _npDrmAccountId;

    /// <summary>The value ksceNpDrmPackageSetGameExist set.</summary>
    [SaveState.SnapshotMissing]
    uint _npDrmGameExist;

    /// <summary>The current account: the one last updated, else the registry's (/CONFIG/NP/account_id, 0 without one).</summary>
    ulong NpDrmAccountId()
    {
        if (_npDrmAccountId is { } account)
        {
            return account;
        }

        return RegistryAccountId(Emu.Options.FsRoot);
    }

    /// <summary>The license name key of a SceNpDrm module ELF: 16 bytes of its first segment when it is FW 3.74's
    /// module (<paramref name="moduleNid"/>), else null.</summary>
    internal static byte[]? RifNameKeyFrom(byte[] elf, uint moduleNid) => NpDrmModuleBytes(elf, moduleNid, RifNameKeyOffset, 0x10);

    /// <summary><paramref name="size"/> bytes at <paramref name="segmentOffset"/> of the first segment of FW 3.74's
    /// SceNpDrm ELF (<paramref name="moduleNid"/>), else null.</summary>
    static byte[]? NpDrmModuleBytes(byte[] elf, uint moduleNid, uint segmentOffset, int size)
    {
        if (moduleNid != NpDrm374ModuleNid || elf.Length < 0x34)
        {
            return null;
        }

        uint programHeaders = BinaryPrimitives.ReadUInt32LittleEndian(elf.AsSpan(0x1C));
        if (programHeaders + 0x20 > elf.Length)
        {
            return null;
        }

        uint offset = BinaryPrimitives.ReadUInt32LittleEndian(elf.AsSpan((int)programHeaders + 4));
        uint fileSize = BinaryPrimitives.ReadUInt32LittleEndian(elf.AsSpan((int)programHeaders + 0x10));
        if (segmentOffset + size > fileSize || offset + segmentOffset + size > elf.Length)
        {
            return null;
        }

        return elf.AsSpan((int)(offset + segmentOffset), size).ToArray();
    }

    byte[]? RifNameKey() => RifNameKeyFor(Emu.Options.FsRoot);

    /// <summary>The license name key of the SceNpDrm installed under <paramref name="fsRoot"/>, or null.</summary>
    internal static byte[]? RifNameKeyFor(string fsRoot)
    {
        if (Firmware.FirmwareKernelImage.For(fsRoot).Named("SceNpDrm") is not { } module)
        {
            return null;
        }

        return RifNameKeyFrom(module.Elf, module.Image.ModuleNid);
    }

    /// <summary>The unit's account in the registry, /CONFIG/NP/account_id (8 bytes, little-endian), 0 without one; the
    /// account the PSM runtime names its license by. The runtime's libpsm (PCSI00011 2.01) reads it with
    /// sceRegMgrSystemParamGetBin(0x450F32) and opens RO/License/&lt;scePsmDrmGetRifName(that account)&gt;; for a
    /// license from the store it is also the license's account, which scePsmDrmGetRifNameForInstall requires to be the
    /// activation's.</summary>
    internal static ulong RegistryAccountId(string fsRoot)
    {
        byte[]? stored = Firmware.Registry.For(fsRoot).Get("/CONFIG/NP", "account_id");
        return stored is { Length: >= 8 } ? BinaryPrimitives.ReadUInt64LittleEndian(stored) : 0;
    }

    /// <summary>The file name the PSM runtime opens in a title's RO/License (scePsmDrmGetRifName of the unit's account,
    /// <see cref="RegistryAccountId"/>), with the SceNpDrm installed under <paramref name="fsRoot"/>; null without
    /// it.</summary>
    internal static string? PsmLicenseFileName(string fsRoot) =>
        RifFileName(RifNameKeyFor(fsRoot), 0, RegistryAccountId(fsRoot));

    /// <summary>The license file name for <paramref name="flag"/> and <paramref name="accountId"/>, or null when the
    /// firmware's key is not available.</summary>
    internal static string? RifFileName(byte[]? key, uint flag, ulong accountId)
    {
        if (key == null)
        {
            if (flag == 1 && accountId == 0)
            {
                return Package.PkgInstaller.AccountRifName;
            }

            return null;
        }

        Span<byte> block = stackalloc byte[0x10];
        BinaryPrimitives.WriteUInt32LittleEndian(block, flag);
        BinaryPrimitives.WriteUInt32LittleEndian(block[4..], 0);
        BinaryPrimitives.WriteUInt64LittleEndian(block[8..], accountId);
        using var aes = Aes.Create();
        aes.Key = key;
        Span<byte> encrypted = stackalloc byte[0x10];
        aes.EncryptEcb(block, encrypted, PaddingMode.None);
        return Convert.ToHexStringLower(encrypted) + ".rif";
    }

    int WriteRifName(uint name, uint flag, ulong accountId)
    {
        if (name == 0)
        {
            return NPDRM_ERR_INVALID;
        }

        if (RifFileName(RifNameKey(), flag, accountId) is not { } file)
        {
            return KERNEL_ERR_UNSUP;
        }

        GuestMemory.Fill(name, 0, 0x30);
        GuestMemory.WriteCString(name, file, 0x30);
        return 0;
    }

    /// <summary>ksceNpDrmGetRifName (char name[0x30], SceUInt64 accountId): the name of an account license; account 0
    /// is the current account (0x8087000C without one).</summary>
    [Hle("ksceNpDrmGetRifName", Nid = 0xDF62F3B8, Library = "SceNpDrmForDriver")]
    int ksceNpDrmGetRifName(uint name, uint unused, uint accountLow, uint accountHigh)
    {
        ulong account = (ulong)accountHigh << 32 | accountLow;
        if (account == 0)
        {
            account = NpDrmAccountId();
            if (account == 0)
            {
                return NPDRM_ERR_NO_ACCOUNT;
            }
        }

        return WriteRifName(name, 0, account);
    }

    /// <summary>ksceNpDrmGetFixedRifName (char name[0x30], SceUInt64 accountId): the fixed name (free and game card
    /// licenses) for the account.</summary>
    [Hle("ksceNpDrmGetFixedRifName", Nid = 0x5D73448C, Library = "SceNpDrmForDriver")]
    int ksceNpDrmGetFixedRifName(uint name, uint unused, uint accountLow, uint accountHigh) =>
        WriteRifName(name, 1, (ulong)accountHigh << 32 | accountLow);

    /// <summary>ksceNpDrmGetRifNameForInstall (char name[0x30], const SceNpDrmLicense *license, int fixed): the name
    /// a license is installed under: its content id (fixed 0) or its account's name (fixed 1: the fixed name for a
    /// free, game card or account 0 license). The kind, unit and game card checks of the license first (0x80870005,
    /// 0x80870009, 0x8087000A).</summary>
    [Hle("ksceNpDrmGetRifNameForInstall", Nid = 0x17573133, Library = "SceNpDrmForDriver")]
    int ksceNpDrmGetRifNameForInstall(uint name, uint license, uint useAccount)
    {
        if (name == 0 || license == 0 || useAccount > 1)
        {
            return NPDRM_ERR_INVALID;
        }

        ReadOnlySpan<byte> rif = GuestMemory.ReadSpan(license, (int)RifVitaSize);
        ushort type = BinaryPrimitives.ReadUInt16BigEndian(rif[4..]);
        ushort flags = BinaryPrimitives.ReadUInt16BigEndian(rif[6..]);
        byte kind = (byte)flags;
        if ((kind & 0xFD) != 0 && kind != RifKindFree)
        {
            return NPDRM_ERR_BAD_DATA;
        }

        if ((flags & 0x100) != 0)
        {
            if (type == 0)
            {
                return NPDRM_ERR_BAD_DATA;
            }

            if (!rif.Slice(0xC0, 0x10).SequenceEqual(ConsoleIdentity.OpenPsId))
            {
                return NPDRM_ERR_OTHER_UNIT;
            }
        }

        bool gameCard = (flags & 0x400) != 0;
        if (gameCard)
        {
            // the game card's cart hash check is the secure processor's (SceSblGcAuthMgrDrmBB): it does not pass
            return NPDRM_ERR_GAME_CARD;
        }

        if (useAccount == 0)
        {
            string contentId = GuestMemory.ReadCString(license + 0x10, 0x30);
            GuestMemory.Fill(name, 0, 0x2F);
            GuestMemory.WriteCString(name, contentId + ".rif", 0x2F);
            return 0;
        }

        if (type == 0)
        {
            return NPDRM_ERR_BAD_DATA;
        }

        ulong account = BinaryPrimitives.ReadUInt64LittleEndian(rif[8..]);
        if (kind == RifKindFree || account == 0)
        {
            return WriteRifName(name, 1, 0);
        }

        return WriteRifName(name, 0, account);
    }

    /// <summary>ksceNpDrmUpdateAccountId (SceUInt64 accountId): the account the unit is signed in to; 0 reads the
    /// registry's.</summary>
    [Hle("ksceNpDrmUpdateAccountId", Nid = 0x116FC0D6, Library = "SceNpDrmForDriver")]
    int ksceNpDrmUpdateAccountId(uint accountLow, uint accountHigh)
    {
        ulong account = (ulong)accountHigh << 32 | accountLow;
        if (account == 0)
        {
            _npDrmAccountId = null;
            account = NpDrmAccountId();
        }

        _npDrmAccountId = account;
        return 0;
    }

    /// <summary>ksceNpDrmUpdateDebugSettings (): reads the debug settings; the loose account binding needs a QA unit.</summary>
    [Hle("ksceNpDrmUpdateDebugSettings", Nid = 0xA91C7443, Library = "SceNpDrmForDriver")]
    int ksceNpDrmUpdateDebugSettings() => 0;

    /// <summary>ksceNpDrmIsLooseAccountBind (): the QA debug setting: off on a retail unit.</summary>
    [Hle("ksceNpDrmIsLooseAccountBind", Nid = 0xFC84CA1A, Library = "SceNpDrmForDriver", ReturnsValue = true)]
    int ksceNpDrmIsLooseAccountBind() => 0;

    /// <summary>ksceNpDrmPackageSetGameExist (int exist): stored for the package installer's query.</summary>
    [Hle("ksceNpDrmPackageSetGameExist", Nid = 0x3BFD2850, Library = "SceNpDrmForDriver")]
    int ksceNpDrmPackageSetGameExist(uint exist)
    {
        _npDrmGameExist = exist;
        return 0;
    }

    // ------------------------------------------------------------------ activation data

    /// <summary>Reads tm0:npdrm/act.dat: 0 and the data, 0x80870006 without it, 0x80870002 of another size.</summary>
    int ReadActDataFile(out byte[] data)
    {
        data = [];
        string? host = Emu.Vfs.Resolve("tm0:npdrm/act.dat");
        if (host == null || !File.Exists(host))
        {
            return NPDRM_ERR_NO_ACTIVATION_DATA;
        }

        byte[] file = File.ReadAllBytes(host);
        if (file.Length < ActDataSize)
        {
            return NPDRM_ERR_ACT_SIZE;
        }

        data = file;
        return 0;
    }

    /// <summary>The activation's checks: its account against the current one, and for version 2 and later the
    /// OpenPSID it is bound to.</summary>
    int ActDataCheck(ReadOnlySpan<byte> data)
    {
        if (BinaryPrimitives.ReadUInt64LittleEndian(data[8..]) != NpDrmAccountId())
        {
            return NPDRM_ERR_OTHER_ACCOUNT;
        }

        if (BinaryPrimitives.ReadUInt32BigEndian(data[4..]) >= 2 && !data.Slice(0x850, 0x10).SequenceEqual(ConsoleIdentity.OpenPsId))
        {
            return NPDRM_ERR_OTHER_UNIT;
        }

        return 0;
    }

    /// <summary>ksceNpDrmReadActData (SceNpDrmActivationData *data 0x1038): the activation data after its checks;
    /// NULL 0x80870001, none 0x80870006 (the buffer cleared on every failure).</summary>
    [Hle("ksceNpDrmReadActData", Nid = 0xD91C3BCE, Library = "SceNpDrmForDriver")]
    int ksceNpDrmReadActData(uint data)
    {
        if (data == 0)
        {
            return NPDRM_ERR_INVALID;
        }

        int read = ReadActDataFile(out byte[] file);
        int result = read != 0 ? read : ActDataCheck(file);
        if (result != 0)
        {
            GuestMemory.Fill(data, 0, ActDataSize);
            return result;
        }

        file.AsSpan(0, ActDataSize).CopyTo(GuestMemory.Span(data, ActDataSize));
        return 0;
    }

    /// <summary>ksceNpDrmUpdateActData (): reloads the activation data: 0, none 0x80870006, another size 0x80870002,
    /// another account 0x80870008, another unit 0x80870009.</summary>
    [Hle("ksceNpDrmUpdateActData", Nid = 0x077926F5, Library = "SceNpDrmForDriver")]
    int ksceNpDrmUpdateActData()
    {
        int read = ReadActDataFile(out byte[] file);
        return read != 0 ? read : ActDataCheck(file);
    }

    /// <summary>
    /// ksceNpDrmCheckActData (int *version, int *versionFlag, SceUInt64 *accountId, SceRtcTick *start, SceRtcTick
    /// *end): the fields of the activation data (each output optional; zero without one), then 0, or 0x80870006
    /// without activation data (the checks' error when it does not pass them). An activation before version 2 has no
    /// period: start 0, end "never".
    /// </summary>
    [Hle("ksceNpDrmCheckActData", Nid = 0x9265B350, Library = "SceNpDrmForDriver")]
    int ksceNpDrmCheckActData(uint version, uint versionFlag, uint accountId, uint start, uint end)
    {
        int read = ReadActDataFile(out byte[] file);
        int check = read != 0 ? read : ActDataCheck(file);
        byte[] data = check == 0 ? file : new byte[ActDataSize];
        WriteIfSet(version, BinaryPrimitives.ReadUInt16BigEndian(data));
        WriteIfSet(versionFlag, BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(2)));
        if (accountId != 0)
        {
            data.AsSpan(8, 8).CopyTo(GuestMemory.Span(accountId, 8));
        }

        bool timed = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(4)) >= 2;
        if (start != 0)
        {
            GuestMemory.Write(start, timed ? RifTime(data, 0x870, RtcTickNever) : 0UL);
        }

        if (end != 0)
        {
            GuestMemory.Write(end, timed ? RifTime(data, 0x878, RtcTickNever) : RtcTickNever);
        }

        return check;
    }

    /// <summary>ksceNpDrmRemoveActData (SceUInt64 *accountId): deletes tm0:npdrm/act.dat, reporting its account
    /// (0 without it); without activation data 0x80870006.</summary>
    [Hle("ksceNpDrmRemoveActData", Nid = 0x8B85A509, Library = "SceNpDrmForDriver")]
    int ksceNpDrmRemoveActData(uint accountId)
    {
        string? host = Emu.Vfs.Resolve("tm0:npdrm/act.dat");
        if (accountId != 0)
        {
            GuestMemory.Write(accountId, 0UL);
        }

        if (host == null || !File.Exists(host))
        {
            return NPDRM_ERR_NO_ACTIVATION_DATA;
        }

        byte[] file = File.ReadAllBytes(host);
        if (accountId != 0 && file.Length >= ActDataSize)
        {
            file.AsSpan(8, 8).CopyTo(GuestMemory.Span(accountId, 8));
        }

        File.Delete(host);
        return 0;
    }

    /// <summary>ksceNpDrmWriteActData (const SceNpDrmActivationData *data, const void *key): NULL 0x80870001; the
    /// module accepts only data whose signature it verifies, which is not reproduced: not supported.</summary>
    [Hle("ksceNpDrmWriteActData", Nid = 0x742EBAF4, Library = "SceNpDrmForDriver", Default = true)]
    int ksceNpDrmWriteActData(uint data, uint key) => data == 0 ? NPDRM_ERR_INVALID : KERNEL_ERR_UNSUP;

    // ------------------------------------------------------------------ license verification (secure)

    /// <summary>The kind, unit and game card checks VerifyRif and VerifyRifFull start with (0x80870005,
    /// 0x80870009, 0x8087000A).</summary>
    static int RifBindingCheck(ReadOnlySpan<byte> rif)
    {
        ushort type = BinaryPrimitives.ReadUInt16BigEndian(rif[4..]);
        ushort flags = BinaryPrimitives.ReadUInt16BigEndian(rif[6..]);
        byte kind = (byte)flags;
        if ((kind & 0xFD) != 0 && kind != RifKindFree)
        {
            return NPDRM_ERR_BAD_DATA;
        }

        if ((flags & 0x100) != 0)
        {
            if (type == 0)
            {
                return NPDRM_ERR_BAD_DATA;
            }

            if (!rif.Slice(0xC0, 0x10).SequenceEqual(ConsoleIdentity.OpenPsId))
            {
                return NPDRM_ERR_OTHER_UNIT;
            }
        }

        return (flags & 0x400) != 0 ? NPDRM_ERR_GAME_CARD : 0;
    }

    /// <summary>ksceNpDrmVerifyRif (const SceNpDrmLicense *license, ...) and ksceNpDrmVerifyRifFull (license): the
    /// binding checks, then the signature check of the secure side: not supported (never "verified").</summary>
    [Hle("ksceNpDrmVerifyRif", Nid = 0xFE7B17B6, Library = "SceNpDrmForDriver", Default = true)]
    [Hle("ksceNpDrmVerifyRifFull", Nid = 0xFF63672D, Library = "SceNpDrmForDriver", Default = true)]
    int ksceNpDrmVerifyRif(uint license)
    {
        if (license == 0)
        {
            return NPDRM_ERR_INVALID;
        }

        int binding = RifBindingCheck(GuestMemory.ReadSpan(license, (int)RifVitaSize));
        return binding != 0 ? binding : KERNEL_ERR_UNSUP;
    }

    /// <summary>ksceNpDrmPresetRifProvisionalFlag (SceNpDrmLicense *license, int set 0..1): changing the flag re-signs
    /// the license, the secure side's: NULL or another value 0x80870001, then the license's checks, then not
    /// supported (the license is left as it was).</summary>
    [Hle("ksceNpDrmPresetRifProvisionalFlag", Nid = 0xC070FE89, Library = "SceNpDrmForDriver", Default = true)]
    int ksceNpDrmPresetRifProvisionalFlag(uint license, uint set)
    {
        if (license == 0 || set > 1)
        {
            return NPDRM_ERR_INVALID;
        }

        if (set == 0)
        {
            return KERNEL_ERR_UNSUP;
        }

        int binding = RifBindingCheck(GuestMemory.ReadSpan(license, (int)RifVitaSize));
        return binding != 0 ? binding : KERNEL_ERR_UNSUP;
    }

    /// <summary>ksceNpDrmGetLegacyDocKey (const SceNpDrmLicense *license, const void *document, SceSize size, void
    /// *key 0x10): NULL 0x80870001 (the key cleared), a document shorter than its 0x90-byte header 0x80870005; the
    /// key is the secure processor's.</summary>
    [Hle("ksceNpDrmGetLegacyDocKey", Nid = 0x4E321BDE, Library = "SceNpDrmForDriver", Default = true)]
    int ksceNpDrmGetLegacyDocKey(uint license, uint document, uint size, uint key)
    {
        if (key == 0 || document == 0)
        {
            return NPDRM_ERR_INVALID;
        }

        GuestMemory.Fill(key, 0, 0x10);
        return size < 0x90 ? NPDRM_ERR_BAD_DATA : KERNEL_ERR_UNSUP;
    }

    /// <summary>SceNpDrmForDriver's library NID (the user bridges call its exports through their entries).</summary>
    internal const uint NpDrmForDriverNid = 0xD84DC44A;

    /// <summary>The kernel's "no such file" of the eboot's stat.</summary>
    const int NPDRM_ERR_NO_FILE = unchecked((int)0x80010002);

    /// <summary>The PSP eboot signatures (__sce_ebootpbp) of the PSP emulator. On the console the module stats and
    /// reads the eboot and has the secure processor sign (SceSblGcAuthMgrDrmBB) the request with the unit's own key:
    /// an integrity signature bound to the unit, made for any eboot (the license of a PSP title is checked
    /// elsewhere, by its rif). The emulated unit's key is <see cref="Package.PspEbootSignature"/>: the signature the
    /// package installer writes, so what the shell and the PSP emulator check is the same thing. ksceNpDrmEbootSigGenPsp
    /// / Ps1 (path, const void *sha256 0x20, void *signature 0x200, int swVersion) and GenMultiDisc (path, const void
    /// *discinfo 0x100, ...): a NULL argument 0x80870001, an eboot that is not there the stat's error, else 0 and the
    /// signature.</summary>
    int EbootSigGen(uint path, uint hash, uint signature, int hashSize, int kind)
    {
        if (path == 0 || hash == 0 || signature == 0)
        {
            return NPDRM_ERR_INVALID;
        }

        string eboot = GuestMemory.ReadCString(path, 0x100);
        if (Emu.Vfs.Resolve(eboot) is not { } host || !File.Exists(host))
        {
            return NPDRM_ERR_NO_FILE;
        }

        byte[] made = Package.PspEbootSignature.Create(eboot, GuestMemory.ReadSpan(hash, hashSize), kind);
        made.CopyTo(GuestMemory.Span(signature, made.Length));
        return 0;
    }

    [Hle("ksceNpDrmEbootSigGenPsp", Nid = 0x90B1A6D3, Library = "SceNpDrmForDriver")]
    internal int ksceNpDrmEbootSigGenPsp(uint path, uint hash, uint signature, int swVersion) =>
        EbootSigGen(path, hash, signature, 0x20, Package.PspEbootSignature.KindPsp);

    [Hle("ksceNpDrmEbootSigGenPs1", Nid = 0x6D9223E1, Library = "SceNpDrmForDriver")]
    internal int ksceNpDrmEbootSigGenPs1(uint path, uint hash, uint signature, int swVersion) =>
        EbootSigGen(path, hash, signature, 0x20, Package.PspEbootSignature.KindPs1);

    [Hle("ksceNpDrmEbootSigGenMultiDisc", Nid = 0x39A7A666, Library = "SceNpDrmForDriver")]
    internal int ksceNpDrmEbootSigGenMultiDisc(uint path, uint discInfo, uint signature, int swVersion) =>
        EbootSigGen(path, discInfo, signature, 0x100, Package.PspEbootSignature.KindMultiDisc);

    /// <summary>ksceNpDrmEbootSigVerify (path, const void *signature 0x200) and ksceNpDrmPspEbootVerify (path, const
    /// void *signature 0x100): 0 when the signature is the unit's for that eboot, 0x80870005 when it is not; a NULL
    /// argument 0x80870001.</summary>
    int EbootSigVerify(uint path, uint signature, int size)
    {
        if (path == 0 || signature == 0)
        {
            return NPDRM_ERR_INVALID;
        }

        Span<byte> read = stackalloc byte[Package.PspEbootSignature.Size];
        read.Clear();
        GuestMemory.ReadSpan(signature, size).CopyTo(read);
        string eboot = GuestMemory.ReadCString(path, 0x100);
        return Package.PspEbootSignature.Verify(eboot, read) > 0 ? 0 : NPDRM_ERR_BAD_DATA;
    }

    [Hle("ksceNpDrmEbootSigVerify", Nid = 0x7A319692, Library = "SceNpDrmForDriver")]
    internal int ksceNpDrmEbootSigVerify(uint path, uint signature) => EbootSigVerify(path, signature, 0x200);

    [Hle("ksceNpDrmPspEbootVerify", Nid = 0xB6CA3A2C, Library = "SceNpDrmForDriver")]
    internal int ksceNpDrmPspEbootVerify(uint path, uint signature) => EbootSigVerify(path, signature, 0x100);

    /// <summary>ksceNpDrmEbootSigConvert and ksceNpDrmPspEbootSigGen (path, ...): the conversion of an old
    /// signature and the PSP-format one are the secure processor's own formats, which the emulated unit's signature
    /// does not model: a NULL path or argument 0x80870001, then not supported.</summary>
    [Hle("ksceNpDrmEbootSigConvert", Nid = 0xA29B75F9, Library = "SceNpDrmForDriver", Default = true)]
    [Hle("ksceNpDrmPspEbootSigGen", Nid = 0xEF387FC4, Library = "SceNpDrmForDriver", Default = true)]
    int ksceNpDrmEbootSignature(uint path, uint first, uint signature) =>
        path == 0 || first == 0 ? NPDRM_ERR_INVALID : KERNEL_ERR_UNSUP;
}
