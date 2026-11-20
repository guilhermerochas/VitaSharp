using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

// SceUsbdForDriver (os0:bootimage SceUsbd 3.74, read in the module, study only): the USB host stack's kernel side,
// used by the class drivers (mass storage, audio, Ethernet adapters, the DS3, the 3G modem) and SceUsbServ (the host
// controllers). A class driver registers { name, probe, attach, detach } (each word non-zero, else 0x80240003; a name
// already registered 0x80240002; sixteen slots shared with the composite drivers, then 0x80240004) and the stack
// offers it every attached device. Devices are (bus << 16) | address, pipes (bus << 16) | pipe. The emulated unit has
// nothing on its USB host port (the user side's sceUsbd answers the same): registering keeps the driver and offers it
// no device; a device id names nothing (0x80240006), a pipe id names nothing (0x80240003), a descriptor scan finds
// none (NULL). The three host controller buses (0-2) start, stop, suspend and resume.
public sealed partial class SystemServicesVsh
{
    /// <summary>SceUsbd: a driver name already registered (0x80240002).</summary>
    const int USBD_ERR_ALREADY_REGISTERED = unchecked((int)0x80240002);

    /// <summary>SceUsbd: no free driver slot (0x80240004).</summary>
    const int USBD_ERR_NO_SLOT = unchecked((int)0x80240004);

    /// <summary>SceUsbd: a device id that names no attached device (0x80240006).</summary>
    const int USBD_ERR_NO_DEVICE = unchecked((int)0x80240006);

    /// <summary>The driver slots (class drivers and composite drivers together).</summary>
    const int UsbdDriverSlots = 16;

    /// <summary>The host controller buses.</summary>
    const uint UsbdBuses = 3;

    /// <summary>The class and composite drivers registered, by name (the value: true for a composite driver).</summary>
    [SnapshotMissing] // states of builds before it: none registered
    readonly Dictionary<string, bool> _usbdDrivers = new(StringComparer.Ordinal);

    /// <summary>Registers a { name, probe, attach, detach } driver.</summary>
    int UsbdRegister(uint driver, bool composite)
    {
        if (!UsbdDriverComplete(driver))
        {
            return USBD_ERR_INVALID;
        }

        string name = GuestMemory.ReadCString(GuestMemory.R32(driver), 0xFF);
        lock (_usbdDrivers)
        {
            if (_usbdDrivers.ContainsKey(name))
            {
                return USBD_ERR_ALREADY_REGISTERED;
            }

            if (_usbdDrivers.Count >= UsbdDriverSlots)
            {
                return USBD_ERR_NO_SLOT;
            }

            _usbdDrivers[name] = composite;
        }

        return 0;
    }

    /// <summary>Unregisters the driver of that name and kind; one not registered is no error.</summary>
    int UsbdUnregister(uint driver, bool composite)
    {
        if (!UsbdDriverComplete(driver))
        {
            return USBD_ERR_INVALID;
        }

        string name = GuestMemory.ReadCString(GuestMemory.R32(driver), 0xFF);
        lock (_usbdDrivers)
        {
            if (_usbdDrivers.TryGetValue(name, out bool kind) && kind == composite)
            {
                _usbdDrivers.Remove(name);
            }
        }

        return 0;
    }

    /// <summary>A driver block with its name and three callbacks.</summary>
    static bool UsbdDriverComplete(uint driver)
    {
        if (driver == 0)
        {
            return false;
        }

        for (uint word = 0; word < 4; word++)
        {
            if (GuestMemory.R32(driver + word * 4) == 0)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>ksceUsbdRegisterDriver(driver): kept; no device to probe.</summary>
    [Hle(Nid = 0x1EC94F18, Library = "SceUsbdForDriver")]
    int ksceUsbdRegisterDriver(uint driver)
    {
        return UsbdRegister(driver, false);
    }

    /// <summary>ksceUsbdRegisterCompositeLdd(driver): kept; no device to probe.</summary>
    [Hle(Nid = 0x6E53D7F4, Library = "SceUsbdForDriver")]
    int ksceUsbdRegisterCompositeLdd(uint driver)
    {
        return UsbdRegister(driver, true);
    }

    /// <summary>ksceUsbdUnregisterDriver(driver).</summary>
    [Hle(Nid = 0x216F108D, Library = "SceUsbdForDriver")]
    int ksceUsbdUnregisterDriver(uint driver)
    {
        return UsbdUnregister(driver, false);
    }

    /// <summary>ksceUsbdUnregisterCompositeLdd(driver).</summary>
    [Hle(Nid = 0x7934BB4A, Library = "SceUsbdForDriver")]
    int ksceUsbdUnregisterCompositeLdd(uint driver)
    {
        return UsbdUnregister(driver, true);
    }

    /// <summary>The host controller calls: buses 0-2 answer 0, any other 0x80240003.</summary>
    static int UsbdBus(uint bus)
    {
        return bus < UsbdBuses ? 0 : USBD_ERR_INVALID;
    }

    /// <summary>ksceUsbdHostStart(bus): starts the controller.</summary>
    [Hle(Nid = 0x5E301E18, Library = "SceUsbdForDriver")]
    int ksceUsbdHostStart(uint bus)
    {
        return UsbdBus(bus);
    }

    /// <summary>ksceUsbdHostStop(bus): stops the controller.</summary>
    [Hle(Nid = 0x3C3396BB, Library = "SceUsbdForDriver")]
    int ksceUsbdHostStop(uint bus)
    {
        return UsbdBus(bus);
    }

    /// <summary>ksceUsbdSuspend(bus): suspends the controller.</summary>
    [Hle(Nid = 0x3EBF5FFE, Library = "SceUsbdForDriver")]
    int ksceUsbdSuspend(uint bus)
    {
        return UsbdBus(bus);
    }

    /// <summary>ksceUsbdSuspendPhase2(bus, flags): the attached devices' suspend callbacks: none.</summary>
    [Hle(Nid = 0xD7AA730D, Library = "SceUsbdForDriver")]
    int ksceUsbdSuspendPhase2(uint bus, uint flags)
    {
        return UsbdBus(bus);
    }

    /// <summary>ksceUsbdResume(bus): resumes the controller.</summary>
    [Hle(Nid = 0x206634F9, Library = "SceUsbdForDriver")]
    int ksceUsbdResume(uint bus)
    {
        return UsbdBus(bus);
    }

    /// <summary>ksceUsbdOpenPipe(device, endpoint): no device (0x80240006).</summary>
    [Hle(Nid = 0x1CDBFF9F, Library = "SceUsbdForDriver", Default = true)]
    int ksceUsbdOpenPipe(uint device, uint endpoint)
    {
        return USBD_ERR_NO_DEVICE;
    }

    /// <summary>ksceUsbdClosePipe(pipe): no pipe (0x80240003).</summary>
    [Hle(Nid = 0xF304DC5C, Library = "SceUsbdForDriver", Default = true)]
    int ksceUsbdClosePipe(uint pipe)
    {
        return USBD_ERR_INVALID;
    }

    /// <summary>ksceUsbd_7938DAC7(pipe): a pipe's abort: no pipe (0x80240003).</summary>
    [Hle(Nid = 0x7938DAC7, Library = "SceUsbdForDriver", Default = true)]
    int ksceUsbd_7938DAC7(uint pipe)
    {
        return USBD_ERR_INVALID;
    }

    /// <summary>ksceUsbdScanStaticDescriptor(device, start, type): the next descriptor of that type: no device, NULL.</summary>
    [Hle(Nid = 0xBC3EF82B, Library = "SceUsbdForDriver", Default = true, ReturnsValue = true)]
    uint ksceUsbdScanStaticDescriptor(uint device, uint start, uint type)
    {
        return 0;
    }

    /// <summary>ksceUsbdControlTransfer(pipe, request, buffer, callback, arg): no pipe (0x80240003).</summary>
    [Hle(Nid = 0x2E05660F, Library = "SceUsbdForDriver", Default = true)]
    int ksceUsbdControlTransfer(uint pipe, uint request, uint buffer, uint callback, uint argument)
    {
        return USBD_ERR_INVALID;
    }

    /// <summary>ksceUsbdBulkTransfer(pipe, buffer, length, callback, arg): no pipe (0x80240003).</summary>
    [Hle(Nid = 0x235C094A, Library = "SceUsbdForDriver", Default = true)]
    int ksceUsbdBulkTransfer(uint pipe, uint buffer, uint length, uint callback, uint argument)
    {
        return USBD_ERR_INVALID;
    }

    /// <summary>ksceUsbdBulkTransfer2(pipe, buffer, length, callback, arg): no pipe (0x80240003).</summary>
    [Hle(Nid = 0x470D5289, Library = "SceUsbdForDriver", Default = true)]
    int ksceUsbdBulkTransfer2(uint pipe, uint buffer, uint length, uint callback, uint argument)
    {
        return USBD_ERR_INVALID;
    }

    /// <summary>ksceUsbdInterruptTransfer(pipe, buffer, length, callback, arg): no pipe (0x80240003).</summary>
    [Hle(Nid = 0xA0BF85B8, Library = "SceUsbdForDriver", Default = true)]
    int ksceUsbdInterruptTransfer(uint pipe, uint buffer, uint length, uint callback, uint argument)
    {
        return USBD_ERR_INVALID;
    }

    /// <summary>ksceUsbdIsochronousTransfer(pipe, request, callback, arg): no pipe (0x80240003).</summary>
    [Hle(Nid = 0x2249BF99, Library = "SceUsbdForDriver", Default = true)]
    int ksceUsbdIsochronousTransfer(uint pipe, uint request, uint callback, uint argument)
    {
        return USBD_ERR_INVALID;
    }

    /// <summary>ksceUsbdGetDeviceSpeed(device, speed): NULL 0x80240003; no device (0x80240006).</summary>
    [Hle(Nid = 0xC3BECF5F, Library = "SceUsbdForDriver", Default = true)]
    int ksceUsbdGetDeviceSpeed(uint device, uint speed)
    {
        return speed == 0 ? USBD_ERR_INVALID : USBD_ERR_NO_DEVICE;
    }

    /// <summary>ksceUsbdGetDeviceLocation(device, location): NULL 0x80240003; no device (0x80240006).</summary>
    [Hle(Nid = 0x0E73D253, Library = "SceUsbdForDriver", Default = true)]
    int ksceUsbdGetDeviceLocation(uint device, uint location)
    {
        return location == 0 ? USBD_ERR_INVALID : USBD_ERR_NO_DEVICE;
    }

    /// <summary>ksceUsbd_05073925(device, a, b): a device's hub port request: NULLs 0x80240003; no device
    /// (0x80240006).</summary>
    [Hle(Nid = 0x05073925, Library = "SceUsbdForDriver", Default = true)]
    int ksceUsbd_05073925(uint device, uint first, uint second)
    {
        return first == 0 || second == 0 ? USBD_ERR_INVALID : USBD_ERR_NO_DEVICE;
    }
}
