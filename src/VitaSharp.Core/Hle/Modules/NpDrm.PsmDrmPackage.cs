using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

// The rest of SceNpDrm / ScePsmDrm / SceNpDrmPackage (bootimage SceNpDrm, FW 3.74). The license names are the
// module's AES names (SystemServicesVsh.NpDrmKernelRest); the activation data, the rif decryption keys and the
// package crypto live in the secure module, which the emulated unit does not run: those calls refuse (0x80020004)
// after their pointer checks, as defaults.
public sealed partial class SystemServicesVsh
{
    const int NPDRM_ERR_INVALID = unchecked((int)0x80870001);

    /// <summary>A user license name call as SceNpDrm 3.74's syscall makes it: a NULL name goes to the kernel export as
    /// NULL; otherwise the export writes into a zeroed 0x30-byte buffer that is copied out whatever it answers. The
    /// export is called through its entry (a kernel plugin that hooked it sees the call).</summary>
    int ThroughRifNameExport(uint name, uint nid, Func<int> direct, uint second, uint third, uint fourth)
    {
        if (name != 0)
        {
            GuestMemory.Fill(name, 0, 0x30);
        }

        return (int)Emu.FirmwareKernel.Call(NpDrmForDriverNid, nid, () => (uint)direct(), name, second, third, fourth);
    }

    /// <summary>(char name[0x30], SceUInt64 aid): the license file name of the account, by ksceNpDrmGetRifName (account
    /// 0: the current one, 0x8087000C without one).</summary>
    [Hle("_sceNpDrmGetRifName")]
    int NpDrmGetRifName(uint name, uint unused, uint accountLow, uint accountHigh) =>
        ThroughRifNameExport(name, 0xDF62F3B8, () => ksceNpDrmGetRifNameThroughEntry(name, unused, accountLow, accountHigh), unused,
            accountLow, accountHigh);

    /// <summary>(char name[0x30], SceUInt64 aid): the fixed license name (free and game card licenses) of the account,
    /// by ksceNpDrmGetFixedRifName.</summary>
    [Hle("_sceNpDrmGetFixedRifName")]
    int NpDrmGetFixedRifName(uint name, uint unused, uint accountLow, uint accountHigh) =>
        ThroughRifNameExport(name, 0x5D73448C, () => ksceNpDrmGetFixedRifNameThroughEntry(name, unused, accountLow, accountHigh),
            unused, accountLow, accountHigh);

    /// <summary>(char name[0x30], const SceNpDrmLicense *license, int useAccount): the name a license is installed
    /// under, by ksceNpDrmGetRifNameForInstall.</summary>
    [Hle("_sceNpDrmGetRifNameForInstall")]
    int NpDrmGetRifNameForInstall(uint name, uint license, uint useAccount) =>
        ThroughRifNameExport(name, 0x17573133, () => ksceNpDrmGetRifNameForInstallThroughEntry(name, license, useAccount), license,
            useAccount, 0);

    /// <summary>(char name[0x30], SceUInt64 aid): the PSM license file name of the account: the module's own name
    /// function (no export), the same AES name as the PS Vita ones with flag 0 and no current-account substitution;
    /// NULL 0x80870001.</summary>
    [Hle("scePsmDrmGetRifName")]
    int PsmDrmGetRifName(uint name, uint unused, uint accountLow, uint accountHigh)
    {
        if (name == 0)
        {
            return NPDRM_ERR_INVALID;
        }

        GuestMemory.Fill(name, 0, 0x30);
        return WriteRifName(name, 0, (ulong)accountHigh << 32 | accountLow);
    }

    /// <summary>(char name[0x30], const ScePsmDrmLicense *license): the name a PSM license is installed under. The
    /// syscall copies the license (0x400 bytes) in and the zeroed name out; the module checks the license
    /// (<see cref="PsmLicenseInstallCheck"/>: signature, header, then the activation, whose secure part answers
    /// 0x80020004) and names it by its account with flag 0. NULL license 0x808F0101; a NULL name 0x80870001 once the
    /// license passes.</summary>
    [Hle("scePsmDrmGetRifNameForInstall")]
    int PsmDrmGetRifNameForInstall(uint name, uint license)
    {
        if (name != 0)
        {
            GuestMemory.Fill(name, 0, 0x30);
        }

        if (license == 0)
        {
            return PSMDRM_ERR_INVALID;
        }

        ReadOnlySpan<byte> rif = GuestMemory.ReadSpan(license, Package.PsmLicense.Size);
        int check = PsmLicenseInstallCheck(rif, PsmLicenseModulusFor(Emu.Options.FsRoot), PsmActivationExists(),
            out ulong accountId);
        if (check != 0)
        {
            return check;
        }

        return WriteRifName(name, 0, accountId);
    }

    /// <summary>The account's activation data (act.dat): none on the emulated unit; removing it succeeds.</summary>
    [Hle("_sceNpDrmRemoveActData", Default = true)]
    int NpDrmRemoveActData(uint a) => 0;

    /// <summary>(SceUInt64 *accountId): the PSM activation data's removal, through kscePsmDrmRemoveActData.</summary>
    [Hle("scePsmDrmRemoveActData")]
    int PsmDrmRemoveActData(uint accountId) =>
        ThroughPsmDrmExport(0x4CD5375C, () => kscePsmDrmRemoveActDataThroughEntry(accountId), accountId);

    /// <summary>(5 outputs): the PSM activation's fields, through kscePsmDrmGetActInfo.</summary>
    [Hle("scePsmDrmGetActInfo", Default = true)]
    int PsmDrmGetActInfo(uint first, uint second, uint third, uint fourth)
    {
        if (first == 0)
        {
            return NPDRM_ERR_INVALID;
        }

        return ThroughPsmDrmExport(0xB09003A7, () => kscePsmDrmGetActInfoThroughEntry(first), first, second, third, fourth);
    }

    [Hle("_sceNpDrmCheckActData", Default = true)]
    [Hle("_sceNpDrmCheckDrmReset", Default = true)]
    [Hle("_sceNpDrmPresetRifProvisionalFlag", Default = true)]
    [Hle("_sceNpDrmPackageCheck", Default = true)]
    [Hle("_sceNpDrmPackageDecrypt", Default = true)]
    [Hle("_sceNpDrmPackageTransform", Default = true)]
    int NpDrmSecure(uint a) => a == 0 ? NPDRM_ERR_INVALID : KERNEL_ERR_UNSUP;

    /// <summary>
    /// (const ScePsmDrmLicense *license, ScePsmDrmKeySet *keydata, int *versionFlag, SceRtcTick *startTime,
    /// SceRtcTick *expirationTime): the key set of a PSM license (0x200 bytes: HMAC key, AES content key, signature),
    /// which the PSM runtime opens its PSSE files with (<see cref="Package.PsseFile"/>). The syscall (SceNpDrm 3.74)
    /// serves only the PSM runtime (ksceSblACMgrIsPSMRuntime of the caller, else 0x80010058) and passes its five
    /// arguments to kscePsmDrmGetRifKey as they are, through its export entry: a kernel plugin that hooks the export
    /// (a license plugin) answers instead, as on the console.
    /// </summary>
    [Hle("scePsmDrmGetRifKey")]
    int PsmDrmGetRifKey(uint license, uint keydata, uint versionFlag, uint startTime, uint expirationTime)
    {
        if (ksceSblACMgrIsPSMRuntimeThroughEntry(0) == 0)
        {
            return PSMDRM_ERR_NOT_PSM_RUNTIME;
        }

        return ThroughPsmDrmExport(0x8C8CFD01,
            () => kscePsmDrmGetRifKeyThroughEntry(license, keydata, versionFlag, startTime, expirationTime),
            license, keydata, versionFlag, startTime, expirationTime);
    }

    /// <summary>The content key (16 bytes) of <paramref name="license"/> as the PSM runtime gets it: the license and a
    /// key set buffer in the process's memory, given to scePsmDrmGetRifKey (<see cref="PsmDrmGetRifKey"/>: the PSM
    /// runtime check, the kernel export with its hooks, the activation); null unless it answers 0.</summary>
    internal byte[]? PsmContentKey(ReadOnlySpan<byte> license)
    {
        GuestMemory memory = Emu.Memory;
        uint buffer = memory.Alloc(Package.PsmLicense.Size + Package.PsmLicense.KeySetSize, "PSM license key query", MemoryRegion.User, 16);
        if (buffer == 0)
        {
            return null;
        }

        try
        {
            uint keys = buffer + Package.PsmLicense.Size;
            license[..Package.PsmLicense.Size].CopyTo(GuestMemory.Span(buffer, Package.PsmLicense.Size));
            GuestMemory.Fill(keys, 0, Package.PsmLicense.KeySetSize);
            if (PsmDrmGetRifKeyThroughEntry(buffer, keys, 0, 0, 0) != 0)
            {
                return null;
            }

            const uint ContentKeyInKeySet = Package.PsmLicense.ContentKeyOffset - Package.PsmLicense.KeySetOffset;
            return GuestMemory.ReadSpan(keys + ContentKeyInKeySet, 16).ToArray();
        }
        finally
        {
            memory.Free(buffer);
        }
    }

    /// <summary>(const ScePsmDrmLicense *license, char contentId[0x30], SceUInt64 *accountId, SceRtcTick *startTime,
    /// SceRtcTick *expirationTime): the syscall passes its five arguments to kscePsmDrmGetRifInfo as they are
    /// (SceNpDrm 3.74), through its export entry; the checks and the fields are the kernel function's.</summary>
    [Hle("scePsmDrmGetRifInfo")]
    int PsmDrmGetRifInfo(uint license, uint contentId, uint accountId, uint startTime, uint expirationTime) =>
        ThroughPsmDrmExport(0x984F9017, () => kscePsmDrmGetRifInfoThroughEntry(license, contentId, accountId, startTime, expirationTime),
            license, contentId, accountId, startTime, expirationTime);

    /// <summary>
    /// (const void *rif, SceSize size, int checkSign, void *outs[10]): the fields of a license file. The library call has
    /// twelve arguments (rif, size, checkSign, char contentId[0x30], SceUInt64 *accountId, int *version,
    /// int *licenseFlags, int *flags, int *skuFlag, SceUInt64 *startTime, SceUInt64 *expirationTime, SceUInt64 *flags2);
    /// the syscall gets the first three and a pointer to the rest (the kernel copies 0x28 bytes from it). A rif is a PSP
    /// rif (0x98 bytes) or a PS Vita one (0x200), big-endian header: version, version flag, type, flags, account id,
    /// then the content id at 0x10. The shell asks it before it starts a PSP or PSone title
    /// (ux0:pspemu/PSP/LICENSE/&lt;content id&gt;.rif) and refuses the title unless it succeeds with account 0 (or the
    /// unit's) and a validity period that includes now. The signature is the secure module's (Sony's keys): the
    /// emulated unit does not check it, as it accepts the NoNpDrm licenses of PS Vita titles; the emulated licenses
    /// carry no period: valid from 0, never expiring (0x7FFFFFFFFFFFFFFF, the firmware's "no expiration").
    /// </summary>
    [Hle("_sceNpDrmGetRifInfo")]
    int NpDrmGetRifInfo(uint rif, uint size, uint checkSign, uint outs)
    {
        if (rif == 0 || size < 0x40 || size > 0x400)
        {
            return NPDRM_ERR_INVALID;
        }

        if (outs == 0)
        {
            return KERNEL_ERR_ILLEGAL_ADDR;
        }

        var r = GuestMemory.ReadSpan(rif, 0x40);
        uint Out(int i) => GuestMemory.R32(outs + (uint)i * 4);
        if (Out(0) is var contentId and not 0)
        {
            GuestMemory.Fill(contentId, 0, 0x30);
            r.Slice(0x10, 0x30).CopyTo(GuestMemory.Span(contentId, 0x30));
        }
        if (Out(1) is var accountId and not 0)
        {
            GuestMemory.Write(accountId, System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(r[8..]));
        }

        if (Out(2) is var version and not 0)
        {
            GuestMemory.W32(version, System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(r));
        }

        if (Out(3) is var licenseFlags and not 0)
        {
            GuestMemory.W32(licenseFlags, System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(r[6..]));
        }

        if (Out(4) is var flags and not 0)
        {
            GuestMemory.W32(flags, System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(r[4..]));
        }

        if (Out(5) is var sku and not 0)
        {
            GuestMemory.W32(sku, 0);
        }

        if (Out(6) is var start and not 0)
        {
            GuestMemory.Write(start, 0UL);
        }

        if (Out(7) is var expiration and not 0)
        {
            GuestMemory.Write(expiration, 0x7FFFFFFFFFFFFFFFUL);
        }

        if (Out(8) is var flags2 and not 0)
        {
            GuestMemory.Write(flags2, 0UL);
        }

        return 0;
    }
}
