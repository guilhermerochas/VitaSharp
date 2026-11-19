using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

// The last kernel exports of small FW 3.74 libraries (each read in its module's code, study only): SceCpuForDriver,
// SceDipswForDriver and ScePmMgrForDriver (SceSysmem), SceLedForDriver (ScePower), SceHidForDriver (SceHid),
// SceBusErrorForKernel (SceKernelBusError), SceCodecEngineWrapperForDebugger (SceCodecEngineWrapper) and the two
// SceSblGcAuthMgr entries that run the game card / PSM activation authentication on their own stack.

public sealed partial class SysmemKernel
{
    /// <summary>The memory types ksceKernelIsUncacheAddressInTmpFsGame counts as uncached: strongly ordered (2),
    /// device (8), inner write-back only (0x40) and non-cacheable (0x80).</summary>
    const int MmuTypeStronglyOrdered = 2, MmuTypeDevice = 8;

    /// <summary>ksceKernelIsUncacheAddressInTmpFsGame (SceCpuForDriver 0x337CBDF3)(void *va): 1 when
    /// ksceKernelMMUGetMemoryType gives an uncached type for the address, else 0 (also when it does not translate).</summary>
    [Hle(Nid = 0x337CBDF3, Library = "SceCpuForDriver")]
    int ksceKernelIsUncacheAddressInTmpFsGame(uint address)
    {
        int type = ksceKernelMMUGetMemoryType(address);
        return type is MmuTypeStronglyOrdered or MmuTypeDevice or MmuTypeInnerWriteBack or MmuTypeNonCacheable ? 1 : 0;
    }

    /// <summary>The flags word of the boot parameters (sysroot + 0x6C): bit 0 external boot, bit 2 product mode;
    /// both clear on a retail unit booted from its own flash.</summary>
    const uint BootFlags = 0;

    /// <summary>kscePmMgrGetProductMode (ScePmMgrForDriver 0x2AC815A2)(uint8 *mode): the product (factory) mode bit of
    /// the boot flags; NULL 0x800F0A16.</summary>
    [Hle(Nid = 0x2AC815A2, Library = "ScePmMgrForDriver")]
    int kscePmMgrGetProductMode(uint mode)
    {
        if (mode == 0)
        {
            return unchecked((int)0x800F0A16);
        }

        GuestMemory.Write(mode, (byte)((BootFlags >> 2) & 1));
        return 0;
    }

    /// <summary>kscePmMgrIsExternalBootMode (ScePmMgrForDriver 0xBD1F193B)(): the external boot bit of the boot
    /// flags.</summary>
    [Hle(Nid = 0xBD1F193B, Library = "ScePmMgrForDriver")]
    int kscePmMgrIsExternalBootMode()
    {
        return (int)(BootFlags & 1);
    }
}

public sealed partial class SystemServicesVsh
{
    /// <summary>The debug switches a caller reaches without the privilege check (bits 0..0x3F) and the whole range
    /// (0..0xFF); out of range 0x80021001.</summary>
    const uint DipswOpen = 0x40, DipswBits = 0x100;
    const int DIPSW_ERR = unchecked((int)0x80021001);

    [SnapshotMissing] // states of builds before it: bits 0x80..0xFF could not be set
    readonly uint[] _dipswHigh = new uint[4];

    /// <summary>Word <paramref name="index"/> of the 256 debug switches (8 words).</summary>
    internal uint DipswWord(uint index)
    {
        if (index < 4)
        {
            return _dipsw[index];
        }

        return index < 8 ? _dipswHigh[index - 4] : 0;
    }

    void DipswSet(uint bit, bool on)
    {
        uint[] words = bit < 0x80 ? _dipsw : _dipswHigh;
        uint index = (bit & 0x7F) >> 5;
        uint mask = 1u << (int)(bit & 31);
        if (on)
        {
            words[index] |= mask;
        }
        else
        {
            words[index] &= ~mask;
        }
    }

    /// <summary>The syscalls' check: bits from 0x40 need the caller's privilege (SceSysmem asks the process's
    /// capability), which a system program has.</summary>
    int DipswCheckAccess(uint bit)
    {
        if (bit >= DipswBits)
        {
            return DIPSW_ERR;
        }

        return bit >= DipswOpen && !Emu.IsSystemProgram ? DIPSW_ERR : 0;
    }

    /// <summary>sceKernelCheckDipsw(bit) (SceDipsw 0x1C783FB2, a syscall): the bit after the access check.</summary>
    [Hle]
    int sceKernelCheckDipsw(uint bit)
    {
        int access = DipswCheckAccess(bit);
        return access != 0 ? access : (int)((DipswWord(bit >> 5) >> (int)(bit & 31)) & 1);
    }

    /// <summary>sceKernelSetDipsw(bit) (SceDipsw 0x817053D4): sets the bit after the access check.</summary>
    [Hle]
    int sceKernelSetDipsw(uint bit)
    {
        int access = DipswCheckAccess(bit);
        if (access == 0)
        {
            DipswSet(bit, true);
        }

        return access;
    }

    /// <summary>sceKernelClearDipsw(bit) (SceDipsw 0x800EDCC1): clears the bit after the access check.</summary>
    [Hle]
    int sceKernelClearDipsw(uint bit)
    {
        int access = DipswCheckAccess(bit);
        if (access == 0)
        {
            DipswSet(bit, false);
        }

        return access;
    }

    /// <summary>ksceKernelCheckDipsw (SceDipswForDriver 0xA98FC2FD)(bit): the bit, no check (kernel callers pass bits
    /// below 0x100).</summary>
    [Hle(Nid = 0xA98FC2FD, Library = "SceDipswForDriver")]
    int ksceKernelCheckDipsw(uint bit)
    {
        return (int)((DipswWord((bit & 0xFF) >> 5) >> (int)(bit & 31)) & 1);
    }

    /// <summary>ksceKernelSetDipsw (SceDipswForDriver 0x82E45FBF)(bit): sets the bit, no check.</summary>
    [Hle(Nid = 0x82E45FBF, Library = "SceDipswForDriver")]
    void ksceKernelSetDipsw(uint bit)
    {
        DipswSet(bit & 0xFF, true);
    }

    /// <summary>ksceKernelClearDipsw (SceDipswForDriver 0xF1F3E9FE)(bit): clears the bit, no check.</summary>
    [Hle(Nid = 0xF1F3E9FE, Library = "SceDipswForDriver")]
    void ksceKernelClearDipsw(uint bit)
    {
        DipswSet(bit & 0xFF, false);
    }

    /// <summary>ksceKernelGetDipswInfo (SceDipswForDriver 0xB2AD48BE)(int index): word <paramref name="index"/> of the
    /// switches (the firmware reads it unchecked; past the 8 words the HLE answers 0).</summary>
    [Hle(Nid = 0xB2AD48BE, Library = "SceDipswForDriver")]
    uint ksceKernelGetDipswInfo(uint index)
    {
        return DipswWord(index);
    }

    // ------------------------------------------------------------------ SceLedForDriver: no LEDs on the host

    /// <summary>The LEDs ScePower registers at start (slots 0..3; 4..7 stay empty), and the mode only LED 2 takes.</summary>
    const uint LedCount = 4, LedModeMax = 4, LedWithModeFour = 2;

    /// <summary>ksceLedSetMode (SceLedForDriver 0xEA24BE03)(int led, int mode, const SceLedConfiguration *config): an
    /// unregistered LED (above 3) 0x803F0140; a mode above 4, or 4 for another LED than 2, 0x803F0141; then ScePower
    /// programs the LED's GPIO blinking. The host has no such LED: accepted.</summary>
    [Hle(Nid = 0xEA24BE03, Library = "SceLedForDriver", Default = true)]
    int ksceLedSetMode(uint led, uint mode, uint config)
    {
        if (led >= LedCount)
        {
            return unchecked((int)0x803F0140);
        }

        if (mode > LedModeMax || (led != LedWithModeFour && mode == LedModeMax))
        {
            return unchecked((int)0x803F0141);
        }

        Log.InfoOnce($"led-{led}", $"ksceLedSetMode: LED {led} mode {mode} (no LED on the host)");
        return 0;
    }

    // ------------------------------------------------------------------ SceBusErrorForKernel

    /// <summary>NID_EBB1F135 (SceBusErrorForKernel)(void *dst, uint size): copies the bus error log of the shared
    /// memory (its header gives the length) and returns the bytes copied, at most <paramref name="size"/>. No bus
    /// error was ever logged on the emulated console: the log is empty, nothing is copied.</summary>
    [Hle(Nid = 0xEBB1F135, Library = "SceBusErrorForKernel", Default = true)]
    int BusErrorCopyLog(uint destination, uint size)
    {
        return 0;
    }

    // ------------------------------------------------------------------ SceCodecEngineWrapperForDebugger

    /// <summary>NID_771190E7 (SceCodecEngineWrapperForDebugger)(uint size, void *info): the codec engine's memory
    /// pool statistics for the debugger; NULL info 0x80600000, a size other than 0x1000 0x80600009. The codec engine
    /// processor is not emulated, so its pool table is not set up: 0 and info untouched, as the module answers then.</summary>
    [Hle(Nid = 0x771190E7, Library = "SceCodecEngineWrapperForDebugger", Default = true)]
    int CodecEngineDebugPoolInfo(uint size, uint info)
    {
        if (info == 0)
        {
            return unchecked((int)0x80600000);
        }

        return size != 0x1000 ? unchecked((int)0x80600009) : 0;
    }

    // ------------------------------------------------------------------ SceSblGcAuthMgr: secure processor services

    /// <summary>ksceSblGcAuthMgrGcAuthCartAuthentication (SceSblGcAuthMgrGcAuthForDriver 0x68781760)(int arg) and
    /// NID_39222A58 (SceSblGcAuthMgrPsmactForDriver)(arg): both run their body on a 0x2000-byte stack, which talks to
    /// the game card / PSM activation service of the secure processor. That service is not there: the kernel's
    /// "not supported" (0x80020004), as the other secure entries answer; never a result that authenticates.</summary>
    [Hle(Nid = 0x68781760, Library = "SceSblGcAuthMgrGcAuthForDriver", Default = true)]
    [Hle("NID_39222A58", Nid = 0x39222A58, Library = "SceSblGcAuthMgrPsmactForDriver", Default = true)]
    int GcAuthSecureService(uint argument)
    {
        return KERNEL_ERR_UNSUP;
    }
}

public sealed partial class Hid
{
    const int HID_ERR_INVALID_ARG = unchecked((int)0x803B0001), HID_ERR_NO_DEVICE = unchecked((int)0x803B0006);

    /// <summary>NID_0B7D27CF (SceHidForDriver)(int handle): takes SceHid's mutex, looks the device up (0x803B0006 when
    /// none has the handle), then makes it the selected one. No HID device is ever attached.</summary>
    [Hle(Nid = 0x0B7D27CF, Library = "SceHidForDriver", Default = true)]
    int HidSelectDevice(int handle)
    {
        return HID_ERR_NO_DEVICE;
    }

    /// <summary>NID_EB323EC6 (SceHidForDriver)(int *handle): the selected device's handle (NULL 0x803B0001); none was
    /// ever selected, so the module's initial 0.</summary>
    [Hle(Nid = 0xEB323EC6, Library = "SceHidForDriver")]
    int HidSelectedDevice(uint handle)
    {
        if (handle == 0)
        {
            return HID_ERR_INVALID_ARG;
        }

        GuestMemory.W32(handle, 0);
        return 0;
    }
}
