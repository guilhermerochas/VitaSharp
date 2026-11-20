using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

// SceUdcdForDriver (os0:bootimage SceUdcd 3.74, read in the module, study only): the USB device controller, through
// which the unit is a USB device of a PC or a PS3 (MTP, mass storage, serial, PSP remote play). Buses 1 and 2 (the
// calls without "Internal" are bus 2; any other bus is 0x80243003). A function driver block (SceUdcdDriver: +0 name,
// +4 endpoints, +8 endpoint table, +0xC interface, +0x40 start, +0x44 stop, +0x48 user data, +0x4C bus, +0x50 next)
// is registered, then started by name (its start callback runs), and the started drivers are activated together with
// a product id. Before any driver, the bus controller itself ("USBDeviceControllerDriver") is started. The device
// state is four words: driver 0x1000 (inactive) or 0x2000 (activated), 0x100 deactivated / 0x200 activated / 0x400
// charging, cable 0x10 (disconnected) or 0x20 (connected), connection 1 (new) / 2 (established) / 4 (suspended).
// The emulated unit has no USB cable plugged in: activation succeeds, the cable stays disconnected (0x10), so no
// host ever configures the device; requests, FIFO clears and stalls need a connected cable and fail with
// 0x80243007 after their argument checks. A wait for a state the unit is not in runs until its timeout (0x8024300C).
public sealed partial class SystemServicesVsh
{
    const int UDCD_ERR_BUSY = unchecked((int)0x80243001);
    const int UDCD_ERR_ALREADY = unchecked((int)0x80243002);
    const int UDCD_ERR_BUS = unchecked((int)0x80243003);
    const int UDCD_ERR_ACTIVE = unchecked((int)0x80243004);
    const int UDCD_ERR_IN_USE = unchecked((int)0x80243006);
    const int UDCD_ERR_NOT_READY = unchecked((int)0x80243007);
    const int UDCD_ERR_NOT_ACTIVE = unchecked((int)0x80243009);
    const int UDCD_ERR_WAIT_ARGUMENT = unchecked((int)0x8024300A);
    const int UDCD_ERR_WAIT_TIMEOUT = unchecked((int)0x8024300C);
    const int UDCD_ERR_DRIVER_NULL = unchecked((int)0x80243200);
    const int UDCD_ERR_DRIVER_NAME = unchecked((int)0x80243201);
    const int UDCD_ERR_DRIVER_CALLBACKS = unchecked((int)0x80243202);
    const int UDCD_ERR_DRIVER_INTERFACES = unchecked((int)0x80243203);
    const int UDCD_ERR_DRIVER_UNKNOWN = unchecked((int)0x80243204);
    const int UDCD_ERR_REQUEST_NULL = unchecked((int)0x80243400);
    const int UDCD_ERR_ENDPOINT_NULL = unchecked((int)0x80243401);
    const int UDCD_ERR_ENDPOINT_NUMBER = unchecked((int)0x80243402);

    /// <summary>The bus controller's own driver name (fixed by the firmware).</summary>
    const string UdcdControllerName = "USBDeviceControllerDriver";

    /// <summary>The longest driver name: 63 bytes (the module refuses a name of 64).</summary>
    const int UdcdNameLimit = 0x40;

    const uint UdcdStateInactive = 0x1000;
    const uint UdcdStateActive = 0x2000;
    const uint UdcdDeactivated = 0x100;
    const uint UdcdCableDisconnected = 0x10;
    const uint UdcdConnectionNew = 1;

    /// <summary>The default bus of the calls without "Internal".</summary>
    const int UdcdDefaultBus = 2;

    readonly object _udcdLock = new();

    /// <summary>The registered drivers not started (block address to bus).</summary>
    [SnapshotMissing] // states of builds before it: none registered
    readonly Dictionary<uint, int> _udcdRegistered = new();

    /// <summary>The started drivers (block address to bus).</summary>
    [SnapshotMissing] // states of builds before it: none started
    readonly Dictionary<uint, int> _udcdStarted = new();

    /// <summary>The buses whose controller driver is started.</summary>
    [SnapshotMissing] // states of builds before it: no controller started
    readonly HashSet<int> _udcdControllers = new();

    /// <summary>The activated buses (driver state 0x2000; the others 0x1000).</summary>
    [SnapshotMissing] // states of builds before it: none activated
    readonly HashSet<int> _udcdActiveBuses = new();

    /// <summary>Per bus: the product id of the last activation.</summary>
    [SnapshotMissing]
    readonly Dictionary<int, uint> _udcdProductIds = new();

    /// <summary>Per bus: the configuration word a function driver set (+4 of its block).</summary>
    [SnapshotMissing]
    readonly Dictionary<int, uint> _udcdConfigurations = new();

    /// <summary>The value the last 0xB1454F11 call stored.</summary>
    [SnapshotMissing]
    uint _udcdStoredWord;

    static bool UdcdValidBus(int bus)
    {
        return bus is 1 or 2;
    }

    /// <summary>A driver name the module takes: non-NULL and shorter than 64 bytes; the name, or null.</summary>
    static string? UdcdName(uint name)
    {
        if (name == 0)
        {
            return null;
        }

        string text = GuestMemory.ReadCString(name, UdcdNameLimit + 1);
        return text.Length == 0 || text.Length >= UdcdNameLimit ? null : text;
    }

    static string UdcdDriverName(uint driver)
    {
        return GuestMemory.ReadCString(GuestMemory.R32(driver), UdcdNameLimit);
    }

    /// <summary>The block of the driver <paramref name="name"/> on <paramref name="bus"/> in <paramref name="table"/>.</summary>
    static uint UdcdFind(Dictionary<uint, int> table, string name, int bus)
    {
        foreach ((uint driver, int driverBus) in table)
        {
            if (driverBus == bus && UdcdDriverName(driver) == name)
            {
                return driver;
            }
        }

        return 0;
    }

    uint UdcdState(int bus)
    {
        uint driverState = UdcdActive(bus) ? UdcdStateActive : UdcdStateInactive;
        return driverState | UdcdDeactivated | UdcdCableDisconnected | UdcdConnectionNew;
    }

    bool UdcdActive(int bus)
    {
        return _udcdActiveBuses.Contains(bus);
    }

    /// <summary>Runs a driver callback (start or stop) with (size, args, user data) when a guest thread calls.</summary>
    static int UdcdCallback(uint function, uint size, uint args, uint userData)
    {
        if (function == 0 || GuestThread.Current is not { } thread)
        {
            return 0;
        }

        return (int)thread.CallGuest(function, size, args, userData);
    }

    // ---- registration

    /// <summary>ksceUdcdRegisterInternal(driver, bus): NULL 0x80243200, a bad name 0x80243201, no start or stop
    /// callback 0x80243202, more than 8 interfaces 0x80243203, a name taken 0x80243002.</summary>
    [Hle(Nid = 0x64675918, Library = "SceUdcdForDriver")]
    int ksceUdcdRegisterInternal(uint driver, int bus)
    {
        if (!UdcdValidBus(bus))
        {
            return UDCD_ERR_BUS;
        }

        if (driver == 0)
        {
            return UDCD_ERR_DRIVER_NULL;
        }

        if (UdcdName(GuestMemory.R32(driver)) is not { } name)
        {
            return UDCD_ERR_DRIVER_NAME;
        }

        if (GuestMemory.R32(driver + 0x40) == 0 || GuestMemory.R32(driver + 0x44) == 0)
        {
            return UDCD_ERR_DRIVER_CALLBACKS;
        }

        uint interfaceBlock = GuestMemory.R32(driver + 0xC);
        int expected = (int)GuestMemory.R32(interfaceBlock);
        int interfaces = (int)GuestMemory.R32(interfaceBlock + 8);
        if ((expected >= 0 ? expected + interfaces : interfaces) > 8)
        {
            return UDCD_ERR_DRIVER_INTERFACES;
        }

        lock (_udcdLock)
        {
            if (UdcdFind(_udcdRegistered, name, bus) != 0)
            {
                return UDCD_ERR_ALREADY;
            }

            _udcdRegistered[driver] = bus;
            GuestMemory.W32(driver + 0x4C, (uint)bus);
            GuestMemory.W32(driver + 0x50, 0);
        }

        return 0;
    }

    /// <summary>ksceUdcdRegister(driver): bus 2.</summary>
    [Hle(Nid = 0x4E55244D, Library = "SceUdcdForDriver")]
    int ksceUdcdRegister(uint driver)
    {
        return ksceUdcdRegisterInternal(driver, UdcdDefaultBus);
    }

    /// <summary>ksceUdcdUnregisterInternal(driver, bus): NULL 0x80243200, a bad name 0x80243201, the bus activated
    /// 0x80243004, a started driver 0x80243006, an unknown one 0x80243204.</summary>
    [Hle(Nid = 0x6CCD152E, Library = "SceUdcdForDriver")]
    int ksceUdcdUnregisterInternal(uint driver, int bus)
    {
        if (!UdcdValidBus(bus))
        {
            return UDCD_ERR_BUS;
        }

        if (driver == 0)
        {
            return UDCD_ERR_DRIVER_NULL;
        }

        if (UdcdName(GuestMemory.R32(driver)) is not { } name)
        {
            return UDCD_ERR_DRIVER_NAME;
        }

        lock (_udcdLock)
        {
            if (UdcdActive(bus))
            {
                return UDCD_ERR_ACTIVE;
            }

            uint registered = UdcdFind(_udcdRegistered, name, bus);
            if (registered != 0)
            {
                _udcdRegistered.Remove(registered);
                return 0;
            }

            return UdcdFind(_udcdStarted, name, bus) != 0 ? UDCD_ERR_IN_USE : UDCD_ERR_DRIVER_UNKNOWN;
        }
    }

    /// <summary>ksceUdcdUnregister(driver): bus 2.</summary>
    [Hle(Nid = 0x0DECE532, Library = "SceUdcdForDriver")]
    int ksceUdcdUnregister(uint driver)
    {
        return ksceUdcdUnregisterInternal(driver, UdcdDefaultBus);
    }

    /// <summary>ksceUdcdGetDrvStateInternal(name, bus): 1 registered, 2 started, -1 unknown; a bad name 0x80243201.</summary>
    [Hle(Nid = 0x512F77BC, Library = "SceUdcdForDriver", ReturnsValue = true)]
    int ksceUdcdGetDrvStateInternal(uint name, int bus)
    {
        if (!UdcdValidBus(bus))
        {
            return UDCD_ERR_BUS;
        }

        if (UdcdName(name) is not { } text)
        {
            return UDCD_ERR_DRIVER_NAME;
        }

        lock (_udcdLock)
        {
            if (UdcdFind(_udcdRegistered, text, bus) != 0)
            {
                return 1;
            }

            return UdcdFind(_udcdStarted, text, bus) != 0 ? 2 : -1;
        }
    }

    /// <summary>ksceUdcdGetDrvState(name): bus 2.</summary>
    [Hle(Nid = 0xC0CA5DDB, Library = "SceUdcdForDriver", ReturnsValue = true)]
    int ksceUdcdGetDrvState(uint name)
    {
        return ksceUdcdGetDrvStateInternal(name, UdcdDefaultBus);
    }

    // ---- start and stop

    /// <summary>ksceUdcdStartInternal(name, size, args, bus): the controller first (twice 0x80243002); a function
    /// driver needs the controller (0x80243007), the bus not activated (0x80243006), and to be registered
    /// (0x80243204; started already 0x80243002); its start callback's error is returned.</summary>
    [Hle(Nid = 0x90F8BCAE, Library = "SceUdcdForDriver")]
    int ksceUdcdStartInternal(uint name, uint size, uint args, int bus)
    {
        if (!UdcdValidBus(bus))
        {
            return UDCD_ERR_BUS;
        }

        if (UdcdName(name) is not { } text)
        {
            return UDCD_ERR_DRIVER_NAME;
        }

        uint driver;
        lock (_udcdLock)
        {
            if (text == UdcdControllerName)
            {
                if (_udcdControllers.Contains(bus))
                {
                    return UDCD_ERR_ALREADY;
                }

                _udcdControllers.Add(bus);
                return 0;
            }

            if (!_udcdControllers.Contains(bus))
            {
                return UDCD_ERR_NOT_READY;
            }

            if (UdcdActive(bus))
            {
                return UDCD_ERR_IN_USE;
            }

            driver = UdcdFind(_udcdRegistered, text, bus);
            if (driver == 0)
            {
                return UdcdFind(_udcdStarted, text, bus) != 0 ? UDCD_ERR_ALREADY : UDCD_ERR_DRIVER_UNKNOWN;
            }
        }

        int result = UdcdCallback(GuestMemory.R32(driver + 0x40), size, args, GuestMemory.R32(driver + 0x48));
        if (result < 0)
        {
            return result;
        }

        lock (_udcdLock)
        {
            _udcdRegistered.Remove(driver);
            _udcdStarted[driver] = bus;
            Monitor.PulseAll(_udcdLock);
        }

        return 0;
    }

    /// <summary>ksceUdcdStart(name, size, args): bus 2.</summary>
    [Hle(Nid = 0x9FD733EA, Library = "SceUdcdForDriver")]
    int ksceUdcdStart(uint name, uint size, uint args)
    {
        return ksceUdcdStartInternal(name, size, args, UdcdDefaultBus);
    }

    /// <summary>ksceUdcdStopInternal(name, size, args, bus): the bus activated 0x80243004; the controller only with
    /// no driver started (0x80243006; not started 0x80243002); a function driver needs the controller (0x80243007) and
    /// to be started (registered only 0x80243002, unknown 0x80243204); its stop callback's error is returned.</summary>
    [Hle(Nid = 0x150FD3BB, Library = "SceUdcdForDriver")]
    int ksceUdcdStopInternal(uint name, uint size, uint args, int bus)
    {
        if (!UdcdValidBus(bus))
        {
            return UDCD_ERR_BUS;
        }

        if (UdcdName(name) is not { } text)
        {
            return UDCD_ERR_DRIVER_NAME;
        }

        uint driver;
        lock (_udcdLock)
        {
            if (UdcdActive(bus))
            {
                return UDCD_ERR_ACTIVE;
            }

            if (text == UdcdControllerName)
            {
                if (!_udcdControllers.Contains(bus))
                {
                    return UDCD_ERR_ALREADY;
                }

                if (_udcdStarted.ContainsValue(bus))
                {
                    return UDCD_ERR_IN_USE;
                }

                _udcdControllers.Remove(bus);
                return 0;
            }

            if (!_udcdControllers.Contains(bus))
            {
                return UDCD_ERR_NOT_READY;
            }

            driver = UdcdFind(_udcdStarted, text, bus);
            if (driver == 0)
            {
                return UdcdFind(_udcdRegistered, text, bus) != 0 ? UDCD_ERR_ALREADY : UDCD_ERR_DRIVER_UNKNOWN;
            }
        }

        int result = UdcdCallback(GuestMemory.R32(driver + 0x44), size, args, GuestMemory.R32(driver + 0x48));
        if (result < 0)
        {
            return result;
        }

        lock (_udcdLock)
        {
            _udcdStarted.Remove(driver);
            _udcdRegistered[driver] = bus;
            Monitor.PulseAll(_udcdLock);
        }

        return 0;
    }

    /// <summary>ksceUdcdStop(name, size, args): bus 2.</summary>
    [Hle(Nid = 0x1494293B, Library = "SceUdcdForDriver")]
    int ksceUdcdStop(uint name, uint size, uint args)
    {
        return ksceUdcdStopInternal(name, size, args, UdcdDefaultBus);
    }

    /// <summary>ksceUdcdStartCurrentInternal(?, bus): starts the bus controller (started already: 0).</summary>
    [Hle(Nid = 0x360E95B9, Library = "SceUdcdForDriver")]
    int ksceUdcdStartCurrentInternal(uint unused, int bus)
    {
        if (!UdcdValidBus(bus))
        {
            return UDCD_ERR_BUS;
        }

        lock (_udcdLock)
        {
            _udcdControllers.Add(bus);
        }

        return 0;
    }

    /// <summary>ksceUdcdStopCurrentInternal(bus): deactivates the bus, stops its started drivers and its controller.</summary>
    [Hle(Nid = 0x0F3595AE, Library = "SceUdcdForDriver")]
    int ksceUdcdStopCurrentInternal(int bus)
    {
        if (!UdcdValidBus(bus))
        {
            return UDCD_ERR_BUS;
        }

        List<uint> started;
        lock (_udcdLock)
        {
            _udcdActiveBuses.Remove(bus);
            started = _udcdStarted.Where(entry => entry.Value == bus).Select(entry => entry.Key).ToList();
        }

        foreach (uint driver in started)
        {
            UdcdCallback(GuestMemory.R32(driver + 0x44), 0, 0, GuestMemory.R32(driver + 0x48));
        }

        lock (_udcdLock)
        {
            foreach (uint driver in started)
            {
                _udcdStarted.Remove(driver);
                _udcdRegistered[driver] = bus;
            }

            _udcdControllers.Remove(bus);
            Monitor.PulseAll(_udcdLock);
        }

        return 0;
    }

    // ---- activation

    /// <summary>ksceUdcdActivateInternal(productId, busPowered, bus): the controller started (0x80243007), not
    /// activated yet (0x80243002), at least one driver started (0x80243204); the bus becomes active (0x2000) with the
    /// cable still disconnected.</summary>
    [Hle(Nid = 0x9119247B, Library = "SceUdcdForDriver")]
    int ksceUdcdActivateInternal(uint productId, uint busPowered, int bus)
    {
        if (!UdcdValidBus(bus))
        {
            return UDCD_ERR_BUS;
        }

        lock (_udcdLock)
        {
            if (!_udcdControllers.Contains(bus))
            {
                return UDCD_ERR_NOT_READY;
            }

            if (UdcdActive(bus))
            {
                return UDCD_ERR_ALREADY;
            }

            if (!_udcdStarted.ContainsValue(bus))
            {
                return UDCD_ERR_DRIVER_UNKNOWN;
            }

            _udcdActiveBuses.Add(bus);
            _udcdProductIds[bus] = productId;
            Monitor.PulseAll(_udcdLock);
        }

        return 0;
    }

    /// <summary>ksceUdcdActivate(productId): bus 2.</summary>
    [Hle(Nid = 0x4FDEA423, Library = "SceUdcdForDriver")]
    int ksceUdcdActivate(uint productId)
    {
        return ksceUdcdActivateInternal(productId, 0, UdcdDefaultBus);
    }

    /// <summary>ksceUdcdDeactivateInternal(bus): not activated 0x80243009; the bus inactive again.</summary>
    [Hle(Nid = 0xFDC10F93, Library = "SceUdcdForDriver")]
    int ksceUdcdDeactivateInternal(int bus)
    {
        if (!UdcdValidBus(bus))
        {
            return UDCD_ERR_BUS;
        }

        lock (_udcdLock)
        {
            if (!UdcdActive(bus))
            {
                return UDCD_ERR_NOT_ACTIVE;
            }

            _udcdActiveBuses.Remove(bus);
            Monitor.PulseAll(_udcdLock);
        }

        return 0;
    }

    /// <summary>ksceUdcdDeactivate(): bus 2.</summary>
    [Hle(Nid = 0x8AE87657, Library = "SceUdcdForDriver")]
    int ksceUdcdDeactivate()
    {
        return ksceUdcdDeactivateInternal(UdcdDefaultBus);
    }

    // ---- state

    /// <summary>ksceUdcdGetDeviceStateInternal(bus): the four state words OR'ed.</summary>
    [Hle(Nid = 0x409DD196, Library = "SceUdcdForDriver", ReturnsValue = true)]
    int ksceUdcdGetDeviceStateInternal(int bus)
    {
        if (!UdcdValidBus(bus))
        {
            return UDCD_ERR_BUS;
        }

        lock (_udcdLock)
        {
            return (int)UdcdState(bus);
        }
    }

    /// <summary>ksceUdcdGetDeviceState(): bus 2.</summary>
    [Hle(Nid = 0xE054B5E4, Library = "SceUdcdForDriver", ReturnsValue = true)]
    int ksceUdcdGetDeviceState()
    {
        return ksceUdcdGetDeviceStateInternal(UdcdDefaultBus);
    }

    /// <summary>ksceUdcdGetDeviceInfoInternal(info, bus): 0x40 bytes zeroed, then the two words the host's
    /// configuration gave: no host has configured the device, both 0.</summary>
    [Hle(Nid = 0x45EB0177, Library = "SceUdcdForDriver")]
    int ksceUdcdGetDeviceInfoInternal(uint info, int bus)
    {
        if (!UdcdValidBus(bus))
        {
            return UDCD_ERR_BUS;
        }

        GuestMemory.Fill(info, 0, 0x40);
        return 0;
    }

    /// <summary>ksceUdcdGetDeviceInfo(info): bus 2.</summary>
    [Hle(Nid = 0xFBEA3703, Library = "SceUdcdForDriver")]
    int ksceUdcdGetDeviceInfo(uint info)
    {
        return ksceUdcdGetDeviceInfoInternal(info, UdcdDefaultBus);
    }

    /// <summary>The state a wait asks for is there: any of its bits set, and its driver (+0x14, optional) started.</summary>
    bool UdcdWaitSatisfied(uint wait, int bus)
    {
        uint driverName = GuestMemory.R32(wait + 0x14);
        if (driverName != 0 && UdcdName(driverName) is { } name && UdcdFind(_udcdStarted, name, bus) == 0)
        {
            return false;
        }

        uint bits = GuestMemory.R32(wait) | GuestMemory.R32(wait + 4) | GuestMemory.R32(wait + 8) | GuestMemory.R32(wait + 0xC);
        return bits == 0 || (UdcdState(bus) & bits) != 0;
    }

    /// <summary>ksceUdcdWaitStateInternal(wait, timeout, bus): wait = { driver state bits (0x1000/0x2000), activation
    /// bits (0x100/0x200/0x400), cable bits (0x10/0x20), connection bits (1/2/4/8), ?, driver name }; other bits
    /// 0x8024300A. Waits (timeout in microseconds, 0: no limit) until the driver is started and any bit asked for is
    /// in the state; the timeout is 0x8024300C.</summary>
    [Hle(Nid = 0x7AD0C8D1, Library = "SceUdcdForDriver", Cost = HleCost.SchedulerWait)]
    int ksceUdcdWaitStateInternal(uint wait, uint timeout, int bus)
    {
        if (!UdcdValidBus(bus))
        {
            return UDCD_ERR_BUS;
        }

        if (wait == 0 || (GuestMemory.R32(wait) & ~0x3000u) != 0 || (GuestMemory.R32(wait + 4) & ~0x700u) != 0
            || (GuestMemory.R32(wait + 8) & ~0x30u) != 0 || (GuestMemory.R32(wait + 0xC) & ~0xFu) != 0)
        {
            return UDCD_ERR_WAIT_ARGUMENT;
        }

        lock (_udcdLock)
        {
            int result = Wait.Until(_udcdLock, () => UdcdWaitSatisfied(wait, bus), timeout == 0 ? null : timeout, what: "udcd state");
            return result == SceError.WAIT_TIMEOUT ? UDCD_ERR_WAIT_TIMEOUT : result;
        }
    }

    /// <summary>ksceUdcdWaitState(wait, timeout): bus 2.</summary>
    [Hle(Nid = 0xD03017C0, Library = "SceUdcdForDriver", Cost = HleCost.SchedulerWait)]
    int ksceUdcdWaitState(uint wait, uint timeout)
    {
        return ksceUdcdWaitStateInternal(wait, timeout, UdcdDefaultBus);
    }

    /// <summary>ksceUdcdWaitBusInitialized(timeout, bus): bus 0-2 (else 0x80243003); the bus is initialized: 0.</summary>
    [Hle(Nid = 0x1C684884, Library = "SceUdcdForDriver")]
    int ksceUdcdWaitBusInitialized(uint timeout, uint bus)
    {
        return bus < 3 ? 0 : UDCD_ERR_BUS;
    }

    /// <summary>NID_DB7EF7AB(bus): posts the bus's state to its event flag (wakes the waits); 0.</summary>
    [Hle(Nid = 0xDB7EF7AB, Library = "SceUdcdForDriver")]
    int ksceUdcdNotifyStateInternal(int bus)
    {
        if (!UdcdValidBus(bus))
        {
            return UDCD_ERR_BUS;
        }

        lock (_udcdLock)
        {
            Monitor.PulseAll(_udcdLock);
        }

        return 0;
    }

    /// <summary>NID_EBB1E86B(): the same for bus 2.</summary>
    [Hle(Nid = 0xEBB1E86B, Library = "SceUdcdForDriver")]
    int ksceUdcdNotifyState()
    {
        return ksceUdcdNotifyStateInternal(UdcdDefaultBus);
    }

    // ---- requests and endpoints: they need a connected cable

    /// <summary>The checks of a request on a bus: with no cable connected the module refuses it before reading it.</summary>
    static int UdcdRequest(int bus)
    {
        return UdcdValidBus(bus) ? UDCD_ERR_NOT_READY : UDCD_ERR_BUS;
    }

    /// <summary>ksceUdcdReqSend(request): no cable connected (0x80243007).</summary>
    [Hle(Nid = 0x1ED0E89E, Library = "SceUdcdForDriver", Default = true)]
    int ksceUdcdReqSend(uint request)
    {
        return UdcdRequest(UdcdDefaultBus);
    }

    /// <summary>ksceUdcdReqSendInternal(request, bus): no cable connected (0x80243007).</summary>
    [Hle(Nid = 0x2E3E622A, Library = "SceUdcdForDriver", Default = true)]
    int ksceUdcdReqSendInternal(uint request, int bus)
    {
        return UdcdRequest(bus);
    }

    /// <summary>ksceUdcdReqRecv(request): no cable connected (0x80243007).</summary>
    [Hle(Nid = 0xC60A74B2, Library = "SceUdcdForDriver", Default = true)]
    int ksceUdcdReqRecv(uint request)
    {
        return UdcdRequest(UdcdDefaultBus);
    }

    /// <summary>ksceUdcdReqRecvInternal(request, bus): no cable connected (0x80243007).</summary>
    [Hle(Nid = 0x175E6179, Library = "SceUdcdForDriver", Default = true)]
    int ksceUdcdReqRecvInternal(uint request, int bus)
    {
        return UdcdRequest(bus);
    }

    /// <summary>NID_AA5FD90E(request): the PSP connection's request: no cable connected (0x80243007).</summary>
    [Hle(Nid = 0xAA5FD90E, Library = "SceUdcdForDriver", Default = true)]
    int ksceUdcdReqRecvPspcm(uint request)
    {
        return UdcdRequest(UdcdDefaultBus);
    }

    /// <summary>NID_03781649(request, bus): the bus activated, a request with its endpoint (NULL 0x80243400, no
    /// endpoint 0x80243401); not activated 0x80243007.</summary>
    [Hle(Nid = 0x03781649, Library = "SceUdcdForDriver", Default = true)]
    int ksceUdcdReqQueueInternal(uint request, int bus)
    {
        if (!UdcdValidBus(bus))
        {
            return UDCD_ERR_BUS;
        }

        lock (_udcdLock)
        {
            if (!UdcdActive(bus))
            {
                return UDCD_ERR_NOT_READY;
            }
        }

        if (request == 0)
        {
            return UDCD_ERR_REQUEST_NULL;
        }

        return GuestMemory.R32(request) == 0 ? UDCD_ERR_ENDPOINT_NULL : UDCD_ERR_NOT_READY;
    }

    /// <summary>An endpoint call: NULL 0x80243401, endpoint number 0 0x80243402; with no cable connected 0x80243007.</summary>
    static int UdcdEndpoint(uint endpoint, int bus)
    {
        if (!UdcdValidBus(bus))
        {
            return UDCD_ERR_BUS;
        }

        if (endpoint == 0)
        {
            return UDCD_ERR_ENDPOINT_NULL;
        }

        return GuestMemory.R32(endpoint + 8) == 0 ? UDCD_ERR_ENDPOINT_NUMBER : UDCD_ERR_NOT_READY;
    }

    /// <summary>ksceUdcdClearFIFO(endpoint): bus 2.</summary>
    [Hle(Nid = 0x9F53D64D, Library = "SceUdcdForDriver", Default = true)]
    int ksceUdcdClearFIFO(uint endpoint)
    {
        return UdcdEndpoint(endpoint, UdcdDefaultBus);
    }

    /// <summary>ksceUdcdClearFIFOInternal(endpoint, bus).</summary>
    [Hle(Nid = 0xA35F3BAF, Library = "SceUdcdForDriver", Default = true)]
    int ksceUdcdClearFIFOInternal(uint endpoint, int bus)
    {
        return UdcdEndpoint(endpoint, bus);
    }

    /// <summary>ksceUdcdStall(endpoint): bus 2.</summary>
    [Hle(Nid = 0x34079250, Library = "SceUdcdForDriver", Default = true)]
    int ksceUdcdStall(uint endpoint)
    {
        return UdcdEndpoint(endpoint, UdcdDefaultBus);
    }

    /// <summary>ksceUdcdStallInternal(endpoint, bus).</summary>
    [Hle(Nid = 0x9B44DF29, Library = "SceUdcdForDriver", Default = true)]
    int ksceUdcdStallInternal(uint endpoint, int bus)
    {
        return UdcdEndpoint(endpoint, bus);
    }

    /// <summary>NID_B1C2EA5A(endpoint, bus): cancels the endpoint's requests: NULL 0x80243401; needs the bus
    /// activated (0x80243007); none queued, 0.</summary>
    [Hle(Nid = 0xB1C2EA5A, Library = "SceUdcdForDriver")]
    int ksceUdcdReqCancelInternal(uint endpoint, int bus)
    {
        if (!UdcdValidBus(bus))
        {
            return UDCD_ERR_BUS;
        }

        if (endpoint == 0)
        {
            return UDCD_ERR_ENDPOINT_NULL;
        }

        lock (_udcdLock)
        {
            return UdcdActive(bus) ? 0 : UDCD_ERR_NOT_READY;
        }
    }

    /// <summary>ksceUdcdReqCancelAll(endpoint): NULL 0x80243401; needs bus 2 activated (0x80243007); none queued, 0.</summary>
    [Hle(Nid = 0x38787672, Library = "SceUdcdForDriver")]
    int ksceUdcdReqCancelAll(uint endpoint)
    {
        return ksceUdcdReqCancelInternal(endpoint, UdcdDefaultBus);
    }

    // ---- the controller hooks and settings

    /// <summary>NID_E9E75C49(bus): the controller's first hook: none installed (0x80243002).</summary>
    [Hle(Nid = 0xE9E75C49, Library = "SceUdcdForDriver", Default = true)]
    int ksceUdcdControllerHook0(int bus)
    {
        return UDCD_ERR_ALREADY;
    }

    /// <summary>NID_2A234CAF(bus): the controller's second hook: none installed (0x80243002).</summary>
    [Hle(Nid = 0x2A234CAF, Library = "SceUdcdForDriver", Default = true)]
    int ksceUdcdControllerHook1(int bus)
    {
        return UDCD_ERR_ALREADY;
    }

    /// <summary>NID_E4F32EDA(block, bus): keeps +4 of the block as the bus's configuration word; 0.</summary>
    [Hle(Nid = 0xE4F32EDA, Library = "SceUdcdForDriver")]
    int ksceUdcdSetConfigurationInternal(uint block, int bus)
    {
        if (!UdcdValidBus(bus))
        {
            return UDCD_ERR_BUS;
        }

        _udcdConfigurations[bus] = GuestMemory.R32(block + 4);
        return 0;
    }

    /// <summary>NID_78BF2A28(block): the same for bus 2.</summary>
    [Hle(Nid = 0x78BF2A28, Library = "SceUdcdForDriver")]
    int ksceUdcdSetConfiguration(uint block)
    {
        return ksceUdcdSetConfigurationInternal(block, UdcdDefaultBus);
    }

    /// <summary>NID_28BFEC45(): re-enumerates bus 2 with its configuration (no host sees it); 0.</summary>
    [Hle(Nid = 0x28BFEC45, Library = "SceUdcdForDriver")]
    int ksceUdcdReenumerate()
    {
        return 0;
    }

    /// <summary>NID_2D91F8C3(mode): the device's power mode, as the user call NID_FCD876FB keeps it (1 bus-powered,
    /// charging on; other self-powered); 0.</summary>
    [Hle(Nid = 0x2D91F8C3, Library = "SceUdcdForDriver")]
    int ksceUdcdSetPowerMode(int mode)
    {
        _udcdBusPowered = mode == 1;
        return 0;
    }

    /// <summary>NID_B1454F11(value): keeps *value (NULL: nothing); 0.</summary>
    [Hle(Nid = 0xB1454F11, Library = "SceUdcdForDriver")]
    int ksceUdcdStoreWord(uint value)
    {
        if (value != 0)
        {
            _udcdStoredWord = GuestMemory.R32(value);
        }

        return 0;
    }

    /// <summary>NID_D01CFD22(): the controller's suspend step; 0.</summary>
    [Hle(Nid = 0xD01CFD22, Library = "SceUdcdForDriver")]
    int ksceUdcdSuspend()
    {
        return 0;
    }

    /// <summary>NID_9E7F7EFE(): the controller's resume step; 0.</summary>
    [Hle(Nid = 0x9E7F7EFE, Library = "SceUdcdForDriver")]
    int ksceUdcdResume()
    {
        return 0;
    }

    /// <summary>NID_1A5A3851(): the controller's power-off step; 0.</summary>
    [Hle(Nid = 0x1A5A3851, Library = "SceUdcdForDriver")]
    int ksceUdcdPowerOff()
    {
        return 0;
    }
}
