using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

// The rest of SceNpDrm / ScePsmDrm / SceNpDrmPackage (bootimage SceNpDrm, FW 3.74). The license names follow the one
// fixed license file the emulated unit uses for every title (no PSN account binds it); the activation data, the rif
// decryption keys and the package crypto live in the secure module, which the emulated unit does not run: those
// calls refuse (0x80020004) after their pointer checks, as defaults.
public sealed partial class SystemServicesVsh
{
    const int NPDRM_ERR_INVALID = unchecked((int)0x80870001);
    const string FixedRif = Package.PkgInstaller.AccountRifName;

    int RifName(uint name)
    {
        if (name == 0) return NPDRM_ERR_INVALID;
        GuestMemory.Fill(name, 0, 0x30);
        GuestMemory.WriteCString(name, FixedRif, 0x30);
        return 0;
    }

    /// <summary>(char name[0x30], u64 aid): the license file name of the account: the fixed one.</summary>
    [Hle("_sceNpDrmGetRifName", Default = true)] int NpDrmGetRifName(uint name) => RifName(name);
    /// <summary>(char name[0x30], const char *contentId, u64 aid): the name a new license is installed under.</summary>
    [Hle("_sceNpDrmGetRifNameForInstall", Default = true)] int NpDrmGetRifNameForInstall(uint name, uint contentId) => RifName(name);
    [Hle("scePsmDrmGetRifName", Default = true)] int PsmDrmGetRifName(uint name) => RifName(name);
    [Hle("scePsmDrmGetRifNameForInstall", Default = true)] int PsmDrmGetRifNameForInstall(uint name, uint contentId) => RifName(name);

    /// <summary>The account's activation data (act.dat): none on the emulated unit; removing it succeeds.</summary>
    [Hle("_sceNpDrmRemoveActData", Default = true)]
    [Hle("scePsmDrmRemoveActData", Default = true)]
    int NpDrmRemoveActData(uint a) => 0;

    [Hle("_sceNpDrmCheckActData", Default = true)]
    [Hle("_sceNpDrmCheckDrmReset", Default = true)]
    [Hle("_sceNpDrmPresetRifProvisionalFlag", Default = true)]
    [Hle("_sceNpDrmPackageCheck", Default = true)]
    [Hle("_sceNpDrmPackageDecrypt", Default = true)]
    [Hle("_sceNpDrmPackageTransform", Default = true)]
    [Hle("scePsmDrmGetActInfo", Default = true)]
    [Hle("scePsmDrmGetRifInfo", Default = true)]
    [Hle("scePsmDrmGetRifKey", Default = true)]
    int NpDrmSecure(uint a) => a == 0 ? NPDRM_ERR_INVALID : KERNEL_ERR_UNSUP;

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
        if (rif == 0 || size < 0x40 || size > 0x400) return NPDRM_ERR_INVALID;
        if (outs == 0) return KERNEL_ERR_ILLEGAL_ADDR;
        var r = GuestMemory.ReadSpan(rif, 0x40);
        uint Out(int i) => GuestMemory.R32(outs + (uint)i * 4);
        if (Out(0) is var contentId and not 0)
        {
            GuestMemory.Fill(contentId, 0, 0x30);
            r.Slice(0x10, 0x30).CopyTo(GuestMemory.Span(contentId, 0x30));
        }
        if (Out(1) is var accountId and not 0) GuestMemory.Write(accountId, System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(r[8..]));
        if (Out(2) is var version and not 0) GuestMemory.W32(version, System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(r));
        if (Out(3) is var licenseFlags and not 0) GuestMemory.W32(licenseFlags, System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(r[6..]));
        if (Out(4) is var flags and not 0) GuestMemory.W32(flags, System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(r[4..]));
        if (Out(5) is var sku and not 0) GuestMemory.W32(sku, 0);
        if (Out(6) is var start and not 0) GuestMemory.Write(start, 0UL);
        if (Out(7) is var expiration and not 0) GuestMemory.Write(expiration, 0x7FFFFFFFFFFFFFFFUL);
        if (Out(8) is var flags2 and not 0) GuestMemory.Write(flags2, 0UL);
        return 0;
    }
}
