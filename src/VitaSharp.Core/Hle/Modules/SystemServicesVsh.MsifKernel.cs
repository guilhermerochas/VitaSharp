using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

// SceMsifForDriver (os0:kd/msif.skprx 3.74, read in the module, study only): the memory card slot's interface, used by
// SceSdstor (probe, media check, access mode, sector reads and writes), SceVshBridge and SceCoredump (the card's
// information block) and SceMagicGate (the card's authentication). The sector calls take 512-byte sectors and a
// word-aligned buffer (else 0x803D0002; a NULL buffer or no sectors 0x803D0001). The module turns the card driver's
// codes into its own through a table (codes outside it are 0x803D0000). The emulated unit has its memory card
// inserted (ux0:): probe, media check and access mode succeed and the information block reports the capacity of the
// card the emulator serves (DeviceCapacity, the same source as sceAppMgrGetDevInfo). The card's contents are served at
// the file level, not as a block device: the raw sector reads, writes and erases, and the card's authentication
// protocol, fail as a failed card command does (the module's generic 0x803D0000).
public sealed partial class SystemServicesVsh
{
    /// <summary>SceMsif: a NULL pointer or a zero count (0x803D0001).</summary>
    const int MSIF_ERR_INVALID_ARGUMENT = unchecked((int)0x803D0001);

    /// <summary>SceMsif: a buffer that is not word-aligned (0x803D0002).</summary>
    const int MSIF_ERR_ALIGNMENT = unchecked((int)0x803D0002);

    /// <summary>SceMsif: a card command that failed (a driver code outside the module's table, 0x803D0000).</summary>
    const int MSIF_ERR_DEVICE = unchecked((int)0x803D0000);

    /// <summary>The information block: 15 words.</summary>
    const uint MsifMediaInfoSize = 0x3C;

    /// <summary>The card kind the block reports (2: the kind whose capacity is blocks times block size).</summary>
    const uint MsifCardKind = 2;

    /// <summary>The sector size (512) and the card's erase block (32 KiB) of the information block.</summary>
    const uint MsifSectorSize = 0x200, MsifBlockSize = 0x8000;

    [SnapshotMissing] // states of builds before it: the flag clear
    bool _msifFlag;

    /// <summary>The bytes of the inserted card: the capacity of the ux0: the emulator serves.</summary>
    ulong MsifCardBytes()
    {
        string? host = Emu.Vfs.Resolve("ux0:");
        ulong total = host == null ? DeviceCapacity.MaxBytes : DeviceCapacity.Of(host).Total;
        return total & ~(ulong)(MsifSectorSize - 1);
    }

    /// <summary>ksceMsifInit(): probes the card in the slot: inserted; 0.</summary>
    [Hle(Nid = 0x4EA579EF, Library = "SceMsifForDriver")]
    int ksceMsifInit()
    {
        return 0;
    }

    /// <summary>ksceMsifGetMediaInfo(void *info): NULL 0x803D0001; the card's block: kind, capacity (twice, the
    /// whole and the user area, in bytes), sector size 512, erase block, attribute words 0; 0.</summary>
    [Hle(Nid = 0xD0307849, Library = "SceMsifForDriver")]
    int ksceMsifGetMediaInfo(uint info)
    {
        if (info == 0)
        {
            return MSIF_ERR_INVALID_ARGUMENT;
        }

        ulong bytes = MsifCardBytes();
        GuestMemory.Fill(info, 0, MsifMediaInfoSize);
        GuestMemory.W32(info, MsifCardKind);
        GuestMemory.Write(info + 8, bytes);
        GuestMemory.Write(info + 0x10, bytes);
        GuestMemory.W32(info + 0x18, MsifSectorSize);
        GuestMemory.W32(info + 0x20, MsifBlockSize);
        return 0;
    }

    /// <summary>The checks of a sector transfer: a NULL buffer or no sectors 0x803D0001, an unaligned buffer
    /// 0x803D0002; then the raw card command fails (0x803D0000).</summary>
    static int MsifTransfer(uint buffer, uint sectors)
    {
        if (sectors == 0 || buffer == 0)
        {
            return MSIF_ERR_INVALID_ARGUMENT;
        }

        if ((buffer & 3) != 0)
        {
            return MSIF_ERR_ALIGNMENT;
        }

        return MSIF_ERR_DEVICE;
    }

    /// <summary>ksceMsifReadSector(int sector, void *buffer, int count): no raw block access (0x803D0000).</summary>
    [Hle(Nid = 0x58654AA3, Library = "SceMsifForDriver", Default = true)]
    int ksceMsifReadSector(uint sector, uint buffer, uint count)
    {
        return MsifTransfer(buffer, count);
    }

    /// <summary>ksceMsifWriteSector(int sector, const void *buffer, int count): no raw block access (0x803D0000).</summary>
    [Hle(Nid = 0x329035EF, Library = "SceMsifForDriver", Default = true)]
    int ksceMsifWriteSector(uint sector, uint buffer, uint count)
    {
        return MsifTransfer(buffer, count);
    }

    /// <summary>ksceMsifEraseSector(int sector, int count): no sectors 0x803D0001; no raw block access (0x803D0000).</summary>
    [Hle(Nid = 0x0F86BEEA, Library = "SceMsifForDriver", Default = true)]
    int ksceMsifEraseSector(uint sector, uint count)
    {
        return count == 0 ? MSIF_ERR_INVALID_ARGUMENT : MSIF_ERR_DEVICE;
    }

    /// <summary>ksceMsifSetAccessMode(int mode): 0 or 1 (else 0x803D0001); 0.</summary>
    [Hle(Nid = 0x6EDE7DBA, Library = "SceMsifForDriver")]
    int ksceMsifSetAccessMode(uint mode)
    {
        return mode > 1 ? MSIF_ERR_INVALID_ARGUMENT : 0;
    }

    /// <summary>ksceMsifAuthenticate(void *result): the card's authentication protocol, which needs the card's own
    /// answers; the command fails (0x803D0000).</summary>
    [Hle(Nid = 0x718BDFDE, Library = "SceMsifForDriver", Default = true)]
    int ksceMsifAuthenticate(uint result)
    {
        return MSIF_ERR_DEVICE;
    }

    /// <summary>ksceMsifSetFlag(): sets the flag kept for the caller; 0.</summary>
    [Hle(Nid = 0x4B751CE6, Library = "SceMsifForDriver")]
    int ksceMsifSetFlag()
    {
        _msifFlag = true;
        return 0;
    }

    /// <summary>ksceMsifClearFlag(): clears the flag; 0.</summary>
    [Hle(Nid = 0x75848756, Library = "SceMsifForDriver")]
    int ksceMsifClearFlag()
    {
        _msifFlag = false;
        return 0;
    }

    /// <summary>ksceMsifGetFlag(): the flag (1/0).</summary>
    [Hle(Nid = 0x491E25B5, Library = "SceMsifForDriver")]
    int ksceMsifGetFlag()
    {
        return _msifFlag ? 1 : 0;
    }

    /// <summary>ksceMsifGetLastTransferTime(): the time the last transfer took (microseconds, 64-bit); none: 0.</summary>
    [Hle(Nid = 0x855E1E38, Library = "SceMsifForDriver", ReturnsValue = true)]
    ulong ksceMsifGetLastTransferTime()
    {
        return 0;
    }

    /// <summary>ksceMsifCheckMedia(): after the module's 25 ms wait the slot reports the card inserted; 0.</summary>
    [Hle(Nid = 0xF997286B, Library = "SceMsifForDriver")]
    int ksceMsifCheckMedia()
    {
        return 0;
    }
}
