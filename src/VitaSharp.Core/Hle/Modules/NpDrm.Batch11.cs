using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

// The rest of SceNpDrm / ScePsmDrm / SceNpDrmPackage (bootimage SceNpDrm, FW 3.74). The license names follow the one
// fixed license file the emulated unit uses for every title (no PSN account binds it); the activation data, the rif
// decryption keys and the package crypto live in the secure module, which the emulated unit does not run: those
// calls refuse (0x80020004) after their pointer checks, as defaults.
public sealed partial class SystemServicesVsh
{
    const int NPDRM_ERR_INVALID = unchecked((int)0x80870001);
    const string FixedRif = "6488b73b912a753a492e2714e9b38bc7.rif";

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
    [Hle("_sceNpDrmGetRifInfo", Default = true)]
    [Hle("_sceNpDrmPresetRifProvisionalFlag", Default = true)]
    [Hle("_sceNpDrmPackageCheck", Default = true)]
    [Hle("_sceNpDrmPackageDecrypt", Default = true)]
    [Hle("_sceNpDrmPackageTransform", Default = true)]
    [Hle("scePsmDrmGetActInfo", Default = true)]
    [Hle("scePsmDrmGetRifInfo", Default = true)]
    [Hle("scePsmDrmGetRifKey", Default = true)]
    int NpDrmSecure(uint a) => a == 0 ? NPDRM_ERR_INVALID : KERNEL_ERR_UNSUP;
}
