using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

// The syscon's non-volatile storage and the secure kernel services over it (FW 3.74, read in the modules, study
// only): SceSysconForDriver's NVS reads and writes (syscon.skprx: 1, 2, 4, 8 or 16 bytes at an offset aligned to
// the size, inside the 0xB60-byte area; else 0x80250001) and SceSblSsMgrForKernel (ss_mgr.skprx): the NVS read and
// write in 8-byte syscon transfers, and the QA flags the secure processor reads at boot (a retail unit has none:
// 16 zero bytes, so no QAF name).
public sealed partial class SystemServicesVsh
{
    /// <summary>SceSyscon: an NVS transfer of another size, misaligned or outside the area.</summary>
    const int SYSCON_ERR_NVS_RANGE = unchecked((int)0x80250001);

    /// <summary>SceSblSsMgr: a NULL output; no QA flags (or no name in them).</summary>
    const int SSMGR_ERR_INVALID_ARGUMENT = unchecked((int)0x800F0716), SSMGR_ERR_NO_QAF = unchecked((int)0x800F0726);

    /// <summary>The size of the syscon's NVS area.</summary>
    const int NvsSize = 0xB60;

    /// <summary>The largest syscon NVS transfer ss_mgr makes at once.</summary>
    const uint NvsChunk = 8;

    [SnapshotMissing] // states of builds before it: the NVS was never written (all zero)
    readonly byte[] _nvs = new byte[NvsSize];

    /// <summary>The unit's QA flags (16 bytes): none on a retail unit.</summary>
    internal static readonly byte[] QaFlags = new byte[16];

    static bool NvsTransferValid(uint offset, uint size)
    {
        bool knownSize = size is 1 or 2 or 4 or 8 or 0x10;
        return knownSize && (offset & (size - 1)) == 0 && offset + size <= NvsSize;
    }

    /// <summary>ksceSysconNvsReadData (0xACAFA2B8)(uint offset, void *buffer, SceSize size).</summary>
    [Hle(Nid = 0xACAFA2B8, Library = "SceSysconForDriver")]
    int ksceSysconNvsReadData(uint offset, uint buffer, uint size)
    {
        if (!NvsTransferValid(offset, size))
        {
            return SYSCON_ERR_NVS_RANGE;
        }

        _nvs.AsSpan((int)offset, (int)size).CopyTo(GuestMemory.Span(buffer, (int)size));
        return 0;
    }

    /// <summary>ksceSysconNvsWriteData (0x10C9657A)(uint offset, const void *buffer, SceSize size).</summary>
    [Hle(Nid = 0x10C9657A, Library = "SceSysconForDriver")]
    int ksceSysconNvsWriteData(uint offset, uint buffer, uint size)
    {
        if (!NvsTransferValid(offset, size))
        {
            return SYSCON_ERR_NVS_RANGE;
        }

        GuestMemory.ReadSpan(buffer, (int)size).CopyTo(_nvs.AsSpan((int)offset, (int)size));
        return 0;
    }

    /// <summary>ksceSysconNvsSetRunMode (0x81A6060D)(int mode): the syscon command that opens the NVS for transfers;
    /// the emulated NVS is always open. 0.</summary>
    [Hle(Nid = 0x81A6060D, Library = "SceSysconForDriver")]
    int ksceSysconNvsSetRunMode(int mode)
    {
        return 0;
    }

    /// <summary>The transfer ss_mgr makes: 8-byte syscon transfers (a shorter last one), stopping at the first
    /// error.</summary>
    int NvsChunked(uint offset, uint buffer, uint size, bool write)
    {
        uint done = 0;
        while (size != 0)
        {
            uint chunk = Math.Min(size, NvsChunk);
            int result = write
                ? ksceSysconNvsWriteData(offset + done, buffer + done, chunk)
                : ksceSysconNvsReadData(offset + done, buffer + done, chunk);
            if (result != 0)
            {
                return result;
            }

            size -= chunk;
            done += chunk;
        }

        return 0;
    }

    /// <summary>ksceSblNvsReadData (0xC2EC8F5A)(uint offset, void *buffer, SceSize size): an NVS read in syscon
    /// transfers (each one's checks apply).</summary>
    [Hle(Nid = 0xC2EC8F5A, Library = "SceSblSsMgrForKernel")]
    int ksceSblNvsReadData(uint offset, uint buffer, uint size)
    {
        return NvsChunked(offset, buffer, size, write: false);
    }

    /// <summary>ksceSblNvsWriteData (0xE29E161C)(uint offset, const void *buffer, SceSize size): an NVS write in
    /// syscon transfers.</summary>
    [Hle(Nid = 0xE29E161C, Library = "SceSblSsMgrForKernel")]
    int ksceSblNvsWriteData(uint offset, uint buffer, uint size)
    {
        return NvsChunked(offset, buffer, size, write: true);
    }

    /// <summary>ksceSblQafManagerGetQAFlags (0x83D254FF)(uint8 flags[16]): the unit's QA flags (NULL 0x800F0716).</summary>
    [Hle(Nid = 0x83D254FF, Library = "SceSblSsMgrForKernel")]
    int ksceSblQafManagerGetQAFlags(uint flags)
    {
        if (flags == 0)
        {
            return SSMGR_ERR_INVALID_ARGUMENT;
        }

        QaFlags.CopyTo(GuestMemory.Span(flags, 16));
        return 0;
    }

    /// <summary>ksceSblQafManagerGetQafName (0xE2DD0378)(char *name, SceSize size): the QA flags' name (kept in the
    /// NVS at 0x400); without QA flags there is none (0x800F0726); NULL or size 0 is 0x800F0716.</summary>
    [Hle(Nid = 0xE2DD0378, Library = "SceSblSsMgrForKernel")]
    int ksceSblQafManagerGetQafName(uint name, uint size)
    {
        if (name == 0 || size == 0)
        {
            return SSMGR_ERR_INVALID_ARGUMENT;
        }

        return SSMGR_ERR_NO_QAF;
    }

    /// <summary>NID_516ECC08 (SceSblSsMgrForKernel)(uint8 *mode): ss_mgr exports its import of
    /// kscePmMgrGetProductMode under this NID (the export is the import's stub): the product (factory) mode, off on
    /// a retail unit; NULL 0x800F0A16.</summary>
    [Hle("NID_516ECC08", Nid = 0x516ECC08, Library = "SceSblSsMgrForKernel")]
    int SsMgrGetProductMode(uint mode)
    {
        if (mode == 0)
        {
            return SBL_PMMGR_ERR_INVALID_ARGUMENT;
        }

        GuestMemory.Write(mode, (byte)0);
        return 0;
    }
}
