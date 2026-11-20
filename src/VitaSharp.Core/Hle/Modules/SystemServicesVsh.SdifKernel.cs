using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

// SceSdifForDriver (os0:kd/sdif.skprx 3.74, read in the module, study only): the SD host controllers at the block
// level. Three hosts: 0 the internal eMMC (an MMC device: os0, vs0, ur0 and the other system partitions), 1 the game
// card slot (an SD device), 2 the WLAN/Bluetooth chip (SDIO). Each host has a global context (0x24C0 bytes: +0x2410
// the device type, 1 MMC, 2 SD, 3 SDIO; +0x2414 its device context once initialized) and the device context the
// callers pass (+0 the host context, +8 the block length, +0x10 the card's CID). The emulated unit serves the eMMC's
// partitions and the game card at the file level (os0:, vs0:, ur0:, gro0:/grw0: when a card dump is in the slot) and
// has no SDIO chip below its WLAN and Bluetooth: the eMMC and the WLAN chip are initialized as after boot, the game
// card is inserted only while its dump is served; every command on the bus (sector reads and writes, CMD56, block
// length, the SDIO commands) reaches no device and ends as the module ends a command the controller aborted
// (0x8032001A), after the module's argument checks (0x8032000E). Host indexes above 2 are 0x80320001 (0x80320013 for
// the insert states).
public sealed partial class SystemServicesVsh
{
    const int SDIF_ERR_HOST = unchecked((int)0x80320001);
    const int SDIF_ERR_INVALID = unchecked((int)0x8032000E);
    const int SDIF_ERR_UNSUPPORTED = unchecked((int)0x8032000F);
    const int SDIF_ERR_NO_CONTEXT = unchecked((int)0x80320013);
    const int SDIF_ERR_ABORTED = unchecked((int)0x8032001A);

    const uint SdifHosts = 3;
    const uint SdifHostContextSize = 0x24C0;
    const uint SdifDeviceContextSize = 0x40;
    const uint SdifBlockLength = 0x200;

    /// <summary>The device types of the hosts (MMC, SD, SDIO).</summary>
    static readonly uint[] s_sdifDeviceTypes = [1, 2, 3];

    /// <summary>The three host contexts and three device contexts in kernel memory (0 until first asked for).</summary>
    [SnapshotMissing] // states of builds before it: allocated again on first use
    uint _sdifContexts;

    /// <summary>The slow mode flag the memory card calls set.</summary>
    [SnapshotMissing]
    bool _sdifSlowMode;

    /// <summary>The host contexts, allocated with their device contexts on first use.</summary>
    uint SdifContexts()
    {
        if (_sdifContexts != 0)
        {
            return _sdifContexts;
        }

        uint total = SdifHosts * (SdifHostContextSize + SdifDeviceContextSize);
        uint block = Emu.Memory.Alloc(total, "sdif contexts", MemoryRegion.Kernel);
        GuestMemory.Fill(block, 0, total);
        for (uint index = 0; index < SdifHosts; index++)
        {
            uint host = block + index * SdifHostContextSize;
            uint device = block + SdifHosts * SdifHostContextSize + index * SdifDeviceContextSize;
            GuestMemory.W32(host + 0x2410, s_sdifDeviceTypes[index]);
            GuestMemory.W32(host + 0x2420, index);
            GuestMemory.W32(device, host);
            GuestMemory.Write(device + 8, (ushort)SdifBlockLength);
        }

        _sdifContexts = block;
        return block;
    }

    uint SdifHost(uint index)
    {
        return SdifContexts() + index * SdifHostContextSize;
    }

    uint SdifDevice(uint index)
    {
        return SdifContexts() + SdifHosts * SdifHostContextSize + index * SdifDeviceContextSize;
    }

    /// <summary>A card is in host <paramref name="index"/>: the eMMC and the WLAN chip always, the game card while its
    /// dump is served (gro0: mounted).</summary>
    bool SdifCardPresent(uint index)
    {
        return index != 1 || Emu.Vfs.IsMounted("gro0");
    }

    /// <summary>The device context of host <paramref name="index"/> when its device is initialized, else 0; publishes
    /// it in the host context (+0x2414) as the module does.</summary>
    uint SdifInitializedDevice(uint index)
    {
        uint host = SdifHost(index);
        if (!SdifCardPresent(index))
        {
            GuestMemory.W32(host + 0x2414, 0);
            return 0;
        }

        uint device = SdifDevice(index);
        GuestMemory.W32(host + 0x2414, device);
        return device;
    }

    /// <summary>ksceSdifGetSdContextGlobal(index): the host context (NULL above 2).</summary>
    [Hle(Nid = 0xDC8F52F8, Library = "SceSdifForDriver", ReturnsValue = true)]
    uint ksceSdifGetSdContextGlobal(uint index)
    {
        return index < SdifHosts ? SdifHost(index) : 0;
    }

    /// <summary>ksceSdifGetSdContextPartValidateMmc(): the eMMC's device context.</summary>
    [Hle(Nid = 0x6A71987F, Library = "SceSdifForDriver", ReturnsValue = true)]
    uint ksceSdifGetSdContextPartValidateMmc()
    {
        return SdifInitializedDevice(0);
    }

    /// <summary>ksceSdifGetSdContextPartValidateSd(): the game card's device context, NULL with no card.</summary>
    [Hle(Nid = 0xB9EA5B1E, Library = "SceSdifForDriver", ReturnsValue = true)]
    uint ksceSdifGetSdContextPartValidateSd()
    {
        return SdifInitializedDevice(1);
    }

    /// <summary>ksceSdifGetSdContextPartValidateSdio(): the WLAN chip's device context.</summary>
    [Hle(Nid = 0x6A8235FC, Library = "SceSdifForDriver", ReturnsValue = true)]
    uint ksceSdifGetSdContextPartValidateSdio()
    {
        return SdifInitializedDevice(2);
    }

    /// <summary>The insert state of host <paramref name="index"/>: 1 with a card, 0 without; above 2 0x80320013.</summary>
    int SdifInsertState(uint index)
    {
        if (index >= SdifHosts)
        {
            return SDIF_ERR_NO_CONTEXT;
        }

        return SdifCardPresent(index) ? 1 : 0;
    }

    /// <summary>ksceSdifGetCardInsertState1(index): 1 with a card in the slot.</summary>
    [Hle(Nid = 0x36A2B01B, Library = "SceSdifForDriver", ReturnsValue = true)]
    int ksceSdifGetCardInsertState1(uint index)
    {
        return SdifInsertState(index);
    }

    /// <summary>ksceSdifGetCardInsertState2(index): as the first.</summary>
    [Hle(Nid = 0xFD9E5CFA, Library = "SceSdifForDriver", ReturnsValue = true)]
    int ksceSdifGetCardInsertState2(uint index)
    {
        return SdifInsertState(index);
    }

    /// <summary>Initializes the device of host <paramref name="index"/> if it is of <paramref name="type"/>: *context
    /// zeroed, then the device context; no card (or another kind) is the aborted command.</summary>
    int SdifInitialize(uint index, uint context, uint type)
    {
        if (context != 0)
        {
            GuestMemory.W32(context, 0);
        }

        if (index >= SdifHosts)
        {
            return SDIF_ERR_HOST;
        }

        if (s_sdifDeviceTypes[index] != type)
        {
            return SDIF_ERR_ABORTED;
        }

        uint device = SdifInitializedDevice(index);
        if (device == 0)
        {
            return SDIF_ERR_ABORTED;
        }

        if (context != 0)
        {
            GuestMemory.W32(context, device);
        }

        return 0;
    }

    /// <summary>ksceSdifInitializeMmcDevice(index, context out): the eMMC (host 0).</summary>
    [Hle(Nid = 0x22C82E79, Library = "SceSdifForDriver")]
    int ksceSdifInitializeMmcDevice(uint index, uint context)
    {
        return SdifInitialize(index, context, 1);
    }

    /// <summary>ksceSdifInitializeSdDevice(index, context out): the game card (host 1), with a card in the slot.</summary>
    [Hle(Nid = 0xC1271539, Library = "SceSdifForDriver")]
    int ksceSdifInitializeSdDevice(uint index, uint context)
    {
        return SdifInitialize(index, context, 2);
    }

    /// <summary>NID_35BA9DF8(index, flags): SceSdstor's re-initialization of a card: above 2 0x80320001; no card the
    /// aborted command; 0.</summary>
    [Hle(Nid = 0x35BA9DF8, Library = "SceSdifForDriver")]
    int ksceSdif_35BA9DF8(uint index, uint flags)
    {
        if (index >= SdifHosts)
        {
            return SDIF_ERR_HOST;
        }

        return SdifInitializedDevice(index) != 0 ? 0 : SDIF_ERR_ABORTED;
    }

    /// <summary>ksceSdifGetCID(context, cid): NULLs 0x8032000E; the 16 bytes at +0x10 of the device context.</summary>
    [Hle(Nid = 0x23A4EF01, Library = "SceSdifForDriver")]
    int ksceSdifGetCID(uint context, uint cid)
    {
        if (context == 0 || cid == 0)
        {
            return SDIF_ERR_INVALID;
        }

        GuestMemory.ReadSpan(context + 0x10, 0x10).CopyTo(GuestMemory.Span(cid, 0x10));
        return 0;
    }

    /// <summary>NID_3DF7E207(index, info): the host's information: its device context, the 48 MHz base clock and the
    /// bus width (8 bits for the eMMC, 4 for the others); above 2 0x80320001.</summary>
    [Hle(Nid = 0x3DF7E207, Library = "SceSdifForDriver")]
    int ksceSdif_3DF7E207(uint index, uint info)
    {
        if (index >= SdifHosts)
        {
            return SDIF_ERR_HOST;
        }

        GuestMemory.W32(info, SdifInitializedDevice(index));
        GuestMemory.W32(info + 8, 48_000_000);
        GuestMemory.W32(info + 0xC, index == 0 ? 8u : 4u);
        return 0;
    }

    /// <summary>NID_76D2B87B(index, info): the card's identification words and block length: no card the aborted
    /// command; above 2 0x80320001.</summary>
    [Hle(Nid = 0x76D2B87B, Library = "SceSdifForDriver")]
    int ksceSdif_76D2B87B(uint index, uint info)
    {
        if (index >= SdifHosts)
        {
            return SDIF_ERR_HOST;
        }

        if (!SdifCardPresent(index))
        {
            return SDIF_ERR_ABORTED;
        }

        GuestMemory.W32(info, 0);
        GuestMemory.W32(info + 4, 0);
        GuestMemory.Write(info + 8, (ushort)SdifBlockLength);
        return 0;
    }

    /// <summary>The sector calls: a NULL context or buffer, or no sectors, 0x8032000E; then the command reaches no
    /// device (0x8032001A).</summary>
    static int SdifSectors(uint context, uint buffer, uint count)
    {
        return context == 0 || buffer == 0 || count == 0 ? SDIF_ERR_INVALID : SDIF_ERR_ABORTED;
    }

    /// <summary>ksceSdifReadSectorMmc(context, sector, buffer, count).</summary>
    [Hle(Nid = 0x6F8D529B, Library = "SceSdifForDriver", Default = true)]
    int ksceSdifReadSectorMmc(uint context, uint sector, uint buffer, uint count)
    {
        return SdifSectors(context, buffer, count);
    }

    /// <summary>ksceSdifWriteSectorMmc(context, sector, buffer, count).</summary>
    [Hle(Nid = 0x175543D2, Library = "SceSdifForDriver", Default = true)]
    int ksceSdifWriteSectorMmc(uint context, uint sector, uint buffer, uint count)
    {
        return SdifSectors(context, buffer, count);
    }

    /// <summary>ksceSdifReadSectorSd(context, sector, buffer, count).</summary>
    [Hle(Nid = 0xB9593652, Library = "SceSdifForDriver", Default = true)]
    int ksceSdifReadSectorSd(uint context, uint sector, uint buffer, uint count)
    {
        return SdifSectors(context, buffer, count);
    }

    /// <summary>ksceSdifWriteSectorSd(context, sector, buffer, count).</summary>
    [Hle(Nid = 0xE0781171, Library = "SceSdifForDriver", Default = true)]
    int ksceSdifWriteSectorSd(uint context, uint sector, uint buffer, uint count)
    {
        return SdifSectors(context, buffer, count);
    }

    /// <summary>The CMD56 calls (the game card's authentication channel): NULLs or a size other than the block length
    /// 0x8032000E; then no card answers (0x8032001A).</summary>
    static int SdifCmd56(uint context, uint buffer, uint size)
    {
        if (context == 0 || buffer == 0 || GuestMemory.Read<ushort>(context + 8) != size)
        {
            return SDIF_ERR_INVALID;
        }

        return SDIF_ERR_ABORTED;
    }

    /// <summary>ksceSdifReadCmd56(context, buffer, size).</summary>
    [Hle(Nid = 0x134E06C4, Library = "SceSdifForDriver", Default = true)]
    int ksceSdifReadCmd56(uint context, uint buffer, uint size)
    {
        return SdifCmd56(context, buffer, size);
    }

    /// <summary>ksceSdifWriteCmd56(context, buffer, size).</summary>
    [Hle(Nid = 0xB0996641, Library = "SceSdifForDriver", Default = true)]
    int ksceSdifWriteCmd56(uint context, uint buffer, uint size)
    {
        return SdifCmd56(context, buffer, size);
    }

    /// <summary>NID_B32776C7(context, length): sets the block length with a command: NULL 0x8032000E; the command
    /// reaches no device (0x8032001A) and the length stays.</summary>
    [Hle(Nid = 0xB32776C7, Library = "SceSdifForDriver", Default = true)]
    int ksceSdif_B32776C7(uint context, uint length)
    {
        return context == 0 ? SDIF_ERR_INVALID : SDIF_ERR_ABORTED;
    }

    /// <summary>NID_AF702FE7(context, value): the module keeps +0xC only when its command succeeds: it reaches no
    /// device, nothing changes.</summary>
    [Hle(Nid = 0xAF702FE7, Library = "SceSdifForDriver", Default = true)]
    int ksceSdif_AF702FE7(uint context, uint value)
    {
        return 0;
    }

    /// <summary>NID_E091BA2E(context, ?, buffer, result): NULLs 0x8032000E; *result = 1 when the device context's word
    /// +8 (its block length) is set; 0.</summary>
    [Hle(Nid = 0xE091BA2E, Library = "SceSdifForDriver")]
    int ksceSdif_E091BA2E(uint context, uint unused, uint buffer, uint result)
    {
        if (context == 0 || buffer == 0 || result == 0)
        {
            return SDIF_ERR_INVALID;
        }

        GuestMemory.W32(result, GuestMemory.R32(context + 8) != 0 ? 1u : 0u);
        return 0;
    }

    /// <summary>ksceSdifMemoryCardEnableSlowMode(): the slow mode flag of the card hosts; 0.</summary>
    [Hle(Nid = 0xF37CF8E5, Library = "SceSdifForDriver")]
    int ksceSdifMemoryCardEnableSlowMode()
    {
        _sdifSlowMode = true;
        return 0;
    }

    /// <summary>ksceSdifMemoryCardDisableSlowMode(): clears it; 0.</summary>
    [Hle(Nid = 0x29A71E7F, Library = "SceSdifForDriver")]
    int ksceSdifMemoryCardDisableSlowMode()
    {
        _sdifSlowMode = false;
        return 0;
    }

    /// <summary>Whether the memory card hosts run in slow mode.</summary>
    internal bool SdifSlowMode => _sdifSlowMode;

    /// <summary>NID_235AD556(): not supported by the module (0x8032000F).</summary>
    [Hle(Nid = 0x235AD556, Library = "SceSdifForDriver")]
    int ksceSdif_235AD556()
    {
        return SDIF_ERR_UNSUPPORTED;
    }

    /// <summary>NID_E5E5F42E(): the module's suspend (every host's state cleared): 0.</summary>
    [Hle(Nid = 0xE5E5F42E, Library = "SceSdifForDriver")]
    int ksceSdif_E5E5F42E()
    {
        return 0;
    }

    /// <summary>NID_0EB0EF86(): the module's resume: 0.</summary>
    [Hle(Nid = 0x0EB0EF86, Library = "SceSdifForDriver")]
    int ksceSdif_0EB0EF86()
    {
        return 0;
    }

    /// <summary>NID_B05EFF68(function): releases an SDIO function's host lock: nothing held.</summary>
    [Hle(Nid = 0xB05EFF68, Library = "SceSdifForDriver")]
    int ksceSdif_B05EFF68(uint function)
    {
        return 0;
    }

    /// <summary>NID_AB0222F2(context): releases the host lock of a device context: nothing held.</summary>
    [Hle(Nid = 0xAB0222F2, Library = "SceSdifForDriver")]
    int ksceSdif_AB0222F2(uint context)
    {
        return 0;
    }

    /// <summary>NID_53962379(index): the host's power-off step: 0.</summary>
    [Hle(Nid = 0x53962379, Library = "SceSdifForDriver")]
    int ksceSdif_53962379(uint index)
    {
        return 0;
    }

    /// <summary>NID_0203ECDC(part, buffer, size): a transfer through the device's block buffer: NULL 0x8032000E; the command reaches no device (0x8032001A).</summary>
    [Hle(Nid = 0x0203ECDC, Library = "SceSdifForDriver", Default = true)]
    int ksceSdif_0203ECDC(uint part, uint buffer, uint size)
    {
        return part == 0 ? SDIF_ERR_INVALID : SDIF_ERR_ABORTED;
    }

    /// <summary>NID_60642F49(part, sector, count): erases sectors: NULL 0x8032000E; the command reaches no device (0x8032001A).</summary>
    [Hle(Nid = 0x60642F49, Library = "SceSdifForDriver", Default = true)]
    int ksceSdif_60642F49(uint part, uint sector, uint count)
    {
        return part == 0 ? SDIF_ERR_INVALID : SDIF_ERR_ABORTED;
    }

    /// <summary>NID_995748EA(part, sector, buffer, result): a sector transfer with its result block: NULL 0x8032000E; the command reaches no device (0x8032001A).</summary>
    [Hle(Nid = 0x995748EA, Library = "SceSdifForDriver", Default = true)]
    int ksceSdif_995748EA(uint part, uint sector, uint buffer, uint result)
    {
        return part == 0 ? SDIF_ERR_INVALID : SDIF_ERR_ABORTED;
    }

    /// <summary>NID_0C66E36F(function, request): an SDIO function's request: NULL 0x8032000E; the command reaches no device (0x8032001A).</summary>
    [Hle(Nid = 0x0C66E36F, Library = "SceSdifForDriver", Default = true)]
    int ksceSdif_0C66E36F(uint function, uint request)
    {
        return function == 0 ? SDIF_ERR_INVALID : SDIF_ERR_ABORTED;
    }

    /// <summary>NID_3428884D(function): an SDIO function's reset: NULL 0x8032000E; the command reaches no device (0x8032001A).</summary>
    [Hle(Nid = 0x3428884D, Library = "SceSdifForDriver", Default = true)]
    int ksceSdif_3428884D(uint function)
    {
        return function == 0 ? SDIF_ERR_INVALID : SDIF_ERR_ABORTED;
    }

    /// <summary>NID_E80293EF(function, a, b, c): an SDIO function's enable: NULL 0x8032000E; the command reaches no device (0x8032001A).</summary>
    [Hle(Nid = 0xE80293EF, Library = "SceSdifForDriver", Default = true)]
    int ksceSdif_E80293EF(uint function, uint a, uint b, uint c)
    {
        return function == 0 ? SDIF_ERR_INVALID : SDIF_ERR_ABORTED;
    }

    /// <summary>NID_55BAEB2D(function, a, b, c): an SDIO direct command (CMD52): NULL 0x8032000E; the command reaches no device (0x8032001A).</summary>
    [Hle(Nid = 0x55BAEB2D, Library = "SceSdifForDriver", Default = true)]
    int ksceSdif_55BAEB2D(uint function, uint a, uint b, uint c)
    {
        return function == 0 ? SDIF_ERR_INVALID : SDIF_ERR_ABORTED;
    }

    /// <summary>NID_FE6F3E7B(function, a, b, c): an SDIO register write: NULL 0x8032000E; the command reaches no device (0x8032001A).</summary>
    [Hle(Nid = 0xFE6F3E7B, Library = "SceSdifForDriver", Default = true)]
    int ksceSdif_FE6F3E7B(uint function, uint a, uint b, uint c)
    {
        return function == 0 ? SDIF_ERR_INVALID : SDIF_ERR_ABORTED;
    }

    /// <summary>NID_F1A24EDD(function, a, b, c): an SDIO register read: NULL 0x8032000E; the command reaches no device (0x8032001A).</summary>
    [Hle(Nid = 0xF1A24EDD, Library = "SceSdifForDriver", Default = true)]
    int ksceSdif_F1A24EDD(uint function, uint a, uint b, uint c)
    {
        return function == 0 ? SDIF_ERR_INVALID : SDIF_ERR_ABORTED;
    }

    /// <summary>NID_D0F78D9B(function, a, b, c, d): an SDIO extended command (CMD53): NULL 0x8032000E; the command reaches no device (0x8032001A).</summary>
    [Hle(Nid = 0xD0F78D9B, Library = "SceSdifForDriver", Default = true)]
    int ksceSdif_D0F78D9B(uint function, uint a, uint b, uint c, uint d)
    {
        return function == 0 ? SDIF_ERR_INVALID : SDIF_ERR_ABORTED;
    }

    /// <summary>NID_3C4CDC8B(function, address, buffer, size, flags): an SDIO block transfer: NULL 0x8032000E; the command reaches no device (0x8032001A).</summary>
    [Hle(Nid = 0x3C4CDC8B, Library = "SceSdifForDriver", Default = true)]
    int ksceSdif_3C4CDC8B(uint function, uint address, uint buffer, uint size, uint flags)
    {
        return function == 0 ? SDIF_ERR_INVALID : SDIF_ERR_ABORTED;
    }

    /// <summary>NID_3B6AB29E(function, a, b): an SDIO function's interrupt registration: NULL 0x8032000E; the command reaches no device (0x8032001A).</summary>
    [Hle(Nid = 0x3B6AB29E, Library = "SceSdifForDriver", Default = true)]
    int ksceSdif_3B6AB29E(uint function, uint a, uint b)
    {
        return function == 0 ? SDIF_ERR_INVALID : SDIF_ERR_ABORTED;
    }

    /// <summary>NID_855C95E1(function, a, b): an SDIO function's interrupt release: NULL 0x8032000E; the command reaches no device (0x8032001A).</summary>
    [Hle(Nid = 0x855C95E1, Library = "SceSdifForDriver", Default = true)]
    int ksceSdif_855C95E1(uint function, uint a, uint b)
    {
        return function == 0 ? SDIF_ERR_INVALID : SDIF_ERR_ABORTED;
    }

    /// <summary>NID_0F157F49(function, a, b, c): an SDIO request's cancel: NULL 0x8032000E; the command reaches no device (0x8032001A).</summary>
    [Hle(Nid = 0x0F157F49, Library = "SceSdifForDriver", Default = true)]
    int ksceSdif_0F157F49(uint function, uint a, uint b, uint c)
    {
        return function == 0 ? SDIF_ERR_INVALID : SDIF_ERR_ABORTED;
    }

    /// <summary>NID_849E3216(function, a, b, result): an SDIO request with its result: NULL 0x8032000E; the command reaches no device (0x8032001A).</summary>
    [Hle(Nid = 0x849E3216, Library = "SceSdifForDriver", Default = true)]
    int ksceSdif_849E3216(uint function, uint a, uint b, uint result)
    {
        return function == 0 ? SDIF_ERR_INVALID : SDIF_ERR_ABORTED;
    }

    /// <summary>NID_D3C1E2B6(function, offset, size, buffer): an SDIO CIS read (offset and size up to 0x100): NULL 0x8032000E; the command reaches no device (0x8032001A).</summary>
    [Hle(Nid = 0xD3C1E2B6, Library = "SceSdifForDriver", Default = true)]
    int ksceSdif_D3C1E2B6(uint function, uint offset, uint size, uint buffer)
    {
        return function == 0 ? SDIF_ERR_INVALID : SDIF_ERR_ABORTED;
    }

    /// <summary>NID_5BAC6E70(function, offset, size, value): an SDIO CIS write: NULL 0x8032000E; the command reaches no device (0x8032001A).</summary>
    [Hle(Nid = 0x5BAC6E70, Library = "SceSdifForDriver", Default = true)]
    int ksceSdif_5BAC6E70(uint function, uint offset, uint size, uint value)
    {
        return function == 0 ? SDIF_ERR_INVALID : SDIF_ERR_ABORTED;
    }

    /// <summary>NID_180E7395(function, buffer, size): an SDIO function's block read (up to 0x100): NULL 0x8032000E; the command reaches no device (0x8032001A).</summary>
    [Hle(Nid = 0x180E7395, Library = "SceSdifForDriver", Default = true)]
    int ksceSdif_180E7395(uint function, uint buffer, uint size)
    {
        return function == 0 ? SDIF_ERR_INVALID : SDIF_ERR_ABORTED;
    }

    /// <summary>NID_1847B18C(function, a, b, c): an SDIO function's setting: NULL 0x8032000E; the command reaches no device (0x8032001A).</summary>
    [Hle(Nid = 0x1847B18C, Library = "SceSdifForDriver", Default = true)]
    int ksceSdif_1847B18C(uint function, uint a, uint b, uint c)
    {
        return function == 0 ? SDIF_ERR_INVALID : SDIF_ERR_ABORTED;
    }

    /// <summary>NID_01E8EB6C(function, buffer, size): an SDIO function's FIFO read (up to 0x100): NULL 0x8032000E; the command reaches no device (0x8032001A).</summary>
    [Hle(Nid = 0x01E8EB6C, Library = "SceSdifForDriver", Default = true)]
    int ksceSdif_01E8EB6C(uint function, uint buffer, uint size)
    {
        return function == 0 ? SDIF_ERR_INVALID : SDIF_ERR_ABORTED;
    }

    /// <summary>NID_763F1075(function): an SDIO function's state: NULL 0x8032000E; the command reaches no device (0x8032001A).</summary>
    [Hle(Nid = 0x763F1075, Library = "SceSdifForDriver", Default = true)]
    int ksceSdif_763F1075(uint function)
    {
        return function == 0 ? SDIF_ERR_INVALID : SDIF_ERR_ABORTED;
    }

    /// <summary>NID_DECE963B(function, offset, size, buffer): an SDIO register block read: NULL 0x8032000E; the command reaches no device (0x8032001A).</summary>
    [Hle(Nid = 0xDECE963B, Library = "SceSdifForDriver", Default = true)]
    int ksceSdif_DECE963B(uint function, uint offset, uint size, uint buffer)
    {
        return function == 0 ? SDIF_ERR_INVALID : SDIF_ERR_ABORTED;
    }

    /// <summary>NID_5D65E66B(function, offset, size, value): an SDIO register block write: NULL 0x8032000E; the command reaches no device (0x8032001A).</summary>
    [Hle(Nid = 0x5D65E66B, Library = "SceSdifForDriver", Default = true)]
    int ksceSdif_5D65E66B(uint function, uint offset, uint size, uint value)
    {
        return function == 0 ? SDIF_ERR_INVALID : SDIF_ERR_ABORTED;
    }

    /// <summary>NID_BC45C83D(function, value): an SDIO function's clock setting: NULL 0x8032000E; the command reaches no device (0x8032001A).</summary>
    [Hle(Nid = 0xBC45C83D, Library = "SceSdifForDriver", Default = true)]
    int ksceSdif_BC45C83D(uint function, uint value)
    {
        return function == 0 ? SDIF_ERR_INVALID : SDIF_ERR_ABORTED;
    }

    /// <summary>NID_AABAA0F0(index, result): an SDIO card's function list: NULL 0x8032000E; the command reaches no device (0x8032001A).</summary>
    [Hle(Nid = 0xAABAA0F0, Library = "SceSdifForDriver", Default = true)]
    int ksceSdif_AABAA0F0(uint index, uint result)
    {
        return index == 0 ? SDIF_ERR_INVALID : SDIF_ERR_ABORTED;
    }

    /// <summary>NID_733BC373(function, value): an SDIO function's block size: NULL 0x8032000E; the command reaches no device (0x8032001A).</summary>
    [Hle(Nid = 0x733BC373, Library = "SceSdifForDriver", Default = true)]
    int ksceSdif_733BC373(uint function, uint value)
    {
        return function == 0 ? SDIF_ERR_INVALID : SDIF_ERR_ABORTED;
    }

    /// <summary>NID_53518827(index, result): the host's register dump: a host index above 2 is 0x80320001; 0.</summary>
    [Hle(Nid = 0x53518827, Library = "SceSdifForDriver")]
    int ksceSdif_53518827(uint index, uint result)
    {
        return index < SdifHosts ? 0 : SDIF_ERR_HOST;
    }

    /// <summary>NID_6CC8E28D(index): the host's card detection: a host index above 2 is 0x80320001; 0.</summary>
    [Hle(Nid = 0x6CC8E28D, Library = "SceSdifForDriver")]
    int ksceSdif_6CC8E28D(uint index)
    {
        return index < SdifHosts ? 0 : SDIF_ERR_HOST;
    }

    /// <summary>NID_B83F7518(index, result): the host's queued commands: a host index above 2 is 0x80320001; 0.</summary>
    [Hle(Nid = 0xB83F7518, Library = "SceSdifForDriver")]
    int ksceSdif_B83F7518(uint index, uint result)
    {
        return index < SdifHosts ? 0 : SDIF_ERR_HOST;
    }

    /// <summary>NID_475D8E45(index): the host's reset: a host index above 2 is 0x80320001; 0.</summary>
    [Hle(Nid = 0x475D8E45, Library = "SceSdifForDriver")]
    int ksceSdif_475D8E45(uint index)
    {
        return index < SdifHosts ? 0 : SDIF_ERR_HOST;
    }
}
