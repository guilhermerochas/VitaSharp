using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

// SceUsbMassForDriver (os0:kd/umass.skprx SceUsbMass 3.74, read in the module, study only): the USB mass storage
// class driver SceSdstor reaches a USB stick through. Its block entries (device info, Read10, Write10 in 128-sector
// pieces) take the attached device, and with none attached answer 0x80010013 before anything else. The interrupt
// entries serve interrupt 0xF only (else 0x80010016): enabling and disabling set and clear the sub-interrupt's bit
// (both enabled at module start) and the poll raises the enabled sub-interrupts that a device made pending, then
// answers -1; no device makes any pending here.
public sealed partial class SystemServicesVsh
{
    /// <summary>SceUsbMass: the only interrupt it serves (0xF).</summary>
    const int UsbMassInterrupt = 0xF;

    /// <summary>SceUsbMass: no device attached (0x80010013, the kernel's busy/no-entry error).</summary>
    const int USBMASS_ERR_NO_DEVICE = unchecked((int)0x80010013);

    /// <summary>SceUsbMass: an interrupt other than 0xF (0x80010016).</summary>
    const int USBMASS_ERR_INTERRUPT = unchecked((int)0x80010016);

    [SnapshotMissing(3u)] // states of builds before it: the module's start enables sub-interrupts 0 and 1
    uint _usbMassEnabled = 3;

    /// <summary>The enabled sub-interrupt bits, for the tests.</summary>
    internal uint UsbMassEnabled => _usbMassEnabled;

    /// <summary>NID_D989A9F6 (SceUsbMassForDriver)(SceUsbMassDevInfo *info): the device's capacity; no device.</summary>
    [Hle("NID_D989A9F6", Nid = 0xD989A9F6, Library = "SceUsbMassForDriver")]
    int UsbMassGetDevInfo(uint info)
    {
        return USBMASS_ERR_NO_DEVICE;
    }

    /// <summary>NID_B80D1DF8 (SceUsbMassForDriver)(uint sector, void *buffer, uint count): Read10; no device.</summary>
    [Hle("NID_B80D1DF8", Nid = 0xB80D1DF8, Library = "SceUsbMassForDriver")]
    int UsbMassReadSector(uint sector, uint buffer, uint count)
    {
        return USBMASS_ERR_NO_DEVICE;
    }

    /// <summary>NID_081CA197 (SceUsbMassForDriver)(uint sector, const void *buffer, uint count): Write10; no device.</summary>
    [Hle("NID_081CA197", Nid = 0x081CA197, Library = "SceUsbMassForDriver")]
    int UsbMassWriteSector(uint sector, uint buffer, uint count)
    {
        return USBMASS_ERR_NO_DEVICE;
    }

    /// <summary>NID_3C821E99 (SceUsbMassForDriver)(int intr, uint sub): enables the sub-interrupt; 0.</summary>
    [Hle("NID_3C821E99", Nid = 0x3C821E99, Library = "SceUsbMassForDriver")]
    int UsbMassEnableSubInterrupt(int interrupt, uint sub)
    {
        if (interrupt != UsbMassInterrupt)
        {
            return USBMASS_ERR_INTERRUPT;
        }

        _usbMassEnabled |= 1u << (int)(sub & 31);
        return 0;
    }

    /// <summary>NID_7833D935 (SceUsbMassForDriver)(int intr, uint sub): disables the sub-interrupt; 0.</summary>
    [Hle("NID_7833D935", Nid = 0x7833D935, Library = "SceUsbMassForDriver")]
    int UsbMassDisableSubInterrupt(int interrupt, uint sub)
    {
        if (interrupt != UsbMassInterrupt)
        {
            return USBMASS_ERR_INTERRUPT;
        }

        _usbMassEnabled &= ~(1u << (int)(sub & 31));
        return 0;
    }

    /// <summary>NID_F2BAB182 (SceUsbMassForDriver)(int intr): raises the enabled pending sub-interrupts (none: no
    /// device) and answers -1.</summary>
    [Hle("NID_F2BAB182", Nid = 0xF2BAB182, Library = "SceUsbMassForDriver")]
    int UsbMassPollInterrupt(int interrupt)
    {
        return interrupt != UsbMassInterrupt ? USBMASS_ERR_INTERRUPT : -1;
    }
}
