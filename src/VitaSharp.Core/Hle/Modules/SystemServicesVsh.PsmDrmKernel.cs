using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

// ScePsmDrmForDriver (bootimage SceNpDrm 3.74, read in the module, study only): the PlayStation Mobile licenses and
// activation of the kernel. The user library's scePsmDrm* syscalls copy their arguments into the kernel and call these
// exports, so here the user calls go through the export entries the kernel shows (FirmwareKernelModules.Call): a
// kernel plugin that hooks one of them sees the user's call, as on the console. The licenses are read as the
// emulator's PSM support reads an installed license (Package.PsmLicense: the key set of an unsigned license is in
// clear; a signed one's key set is encrypted for the account's activation and only the secure processor opens it).
// The PSM activation data (tm0:psmdrm/act.dat) is verified and decrypted by the secure side: reading or writing it
// answers "not supported" after the checks, and without it the module's "not activated" (0x808F0106).
public sealed partial class SystemServicesVsh
{
    /// <summary>ScePsmDrmForDriver's library NID.</summary>
    const uint PsmDrmForDriverNid = 0x9F4924F2;

    /// <summary>ScePsmDrm: a NULL argument (0x808F0101); no PSM activation (0x808F0106).</summary>
    const int PSMDRM_ERR_INVALID = unchecked((int)0x808F0101);
    const int PSMDRM_ERR_NOT_ACTIVATED = unchecked((int)0x808F0106);

    /// <summary>The PSM activation data of the unit.</summary>
    const string PsmActData = "tm0:psmdrm/act.dat";

    bool PsmActivationExists() => Emu.Vfs.Resolve(PsmActData) is { } host && File.Exists(host);

    /// <summary>
    /// kscePsmDrmGetRifKey (const ScePsmDrmLicense *license, ScePsmDrmKeySet *keydata, int *versionFlag,
    /// ScePsmDrmExpireTime *times): the key set of a PSM license: an unsigned license's key set (in clear) is copied
    /// out with its times; a signed one's needs the secure processor. NULL license or key set 0x808F0101.
    /// </summary>
    [Hle("kscePsmDrmGetRifKey", Nid = 0x8C8CFD01, Library = "ScePsmDrmForDriver")]
    int kscePsmDrmGetRifKey(uint license, uint keydata, uint versionFlag, uint times)
    {
        if (license == 0 || keydata == 0)
        {
            return PSMDRM_ERR_INVALID;
        }

        ReadOnlySpan<byte> rif = GuestMemory.ReadSpan(license, Package.PsmLicense.Size);
        if (!Package.PsmLicense.KeySetInClear(rif))
        {
            return KERNEL_ERR_UNSUP;
        }

        rif.Slice(Package.PsmLicense.KeySetOffset, Package.PsmLicense.KeySetSize).CopyTo(GuestMemory.Span(keydata, Package.PsmLicense.KeySetSize));
        if (versionFlag != 0)
        {
            GuestMemory.W32(versionFlag, 0);
        }

        if (times != 0)
        {
            WriteLicenseTimes(rif, times);
        }

        return 0;
    }

    /// <summary>kscePsmDrmGetRifInfo (const ScePsmDrmLicense *license, char contentId[0x30], SceUInt64 *accountId,
    /// ScePsmDrmGetRifInfoOpt *times): the plain fields of a PSM license (content ID +0x50, account ID +0x10, times
    /// +0x20/+0x28); NULL license 0x808F0101.</summary>
    [Hle("kscePsmDrmGetRifInfo", Nid = 0x984F9017, Library = "ScePsmDrmForDriver")]
    int kscePsmDrmGetRifInfo(uint license, uint contentId, uint accountId, uint times)
    {
        if (license == 0)
        {
            return PSMDRM_ERR_INVALID;
        }

        ReadOnlySpan<byte> rif = GuestMemory.ReadSpan(license, Package.PsmLicense.Size);
        if (contentId != 0)
        {
            rif.Slice(0x50, 0x30).CopyTo(GuestMemory.Span(contentId, 0x30));
        }

        if (accountId != 0)
        {
            GuestMemory.Write(accountId, System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(rif[0x10..]));
        }

        if (times != 0)
        {
            WriteLicenseTimes(rif, times);
        }

        return 0;
    }

    /// <summary>kscePsmDrmGetActInfo (5 outputs): the PSM activation's fields: without activation data 0x808F0106; its
    /// verification is the secure processor's.</summary>
    [Hle("kscePsmDrmGetActInfo", Nid = 0xB09003A7, Library = "ScePsmDrmForDriver", Default = true)]
    int kscePsmDrmGetActInfo(uint first) => PsmActivationExists() ? KERNEL_ERR_UNSUP : PSMDRM_ERR_NOT_ACTIVATED;

    /// <summary>kscePsmDrmReadActData (): loads the PSM activation: without it 0x808F0106; its verification is the
    /// secure processor's.</summary>
    [Hle("kscePsmDrmReadActData", Nid = 0x791198CE, Library = "ScePsmDrmForDriver", Default = true)]
    int kscePsmDrmReadActData() => PsmActivationExists() ? KERNEL_ERR_UNSUP : PSMDRM_ERR_NOT_ACTIVATED;

    /// <summary>kscePsmDrmWriteActData (const void *act 0x400, const void *key): NULL 0x808F0101; the activation is
    /// verified by the secure side before it is written: not supported.</summary>
    [Hle("kscePsmDrmWriteActData", Nid = 0xCB73E9D3, Library = "ScePsmDrmForDriver", Default = true)]
    int kscePsmDrmWriteActData(uint act, uint key) => act == 0 ? PSMDRM_ERR_INVALID : KERNEL_ERR_UNSUP;

    /// <summary>kscePsmDrmRemoveActData (SceUInt64 *accountId): deletes the PSM activation data (none: 0); the account
    /// reported is 0 (its fields are the secure side's to read).</summary>
    [Hle("kscePsmDrmRemoveActData", Nid = 0x4CD5375C, Library = "ScePsmDrmForDriver")]
    int kscePsmDrmRemoveActData(uint accountId)
    {
        if (accountId != 0)
        {
            GuestMemory.Write(accountId, 0UL);
        }

        if (Emu.Vfs.Resolve(PsmActData) is { } host && File.Exists(host))
        {
            File.Delete(host);
        }

        return 0;
    }

    /// <summary>A user call's kernel export, as the syscall makes it: through the export's entry when a kernel plugin
    /// hooked it, the HLE function otherwise.</summary>
    int ThroughPsmDrmExport(uint nid, Func<int> direct, params ReadOnlySpan<uint> args) =>
        (int)Emu.FirmwareKernel.Call(PsmDrmForDriverNid, nid, () => (uint)direct(), args);
}
