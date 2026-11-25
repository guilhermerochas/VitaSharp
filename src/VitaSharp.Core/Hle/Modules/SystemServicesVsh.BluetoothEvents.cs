using VitaSharp.Core.Host;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// SceBt's event queues (FW 3.74 SceBt in os0:kd/bootimage, read in the module, study only). The module keeps 8 slots
/// of 0x130 bytes, one per process: sceBtRegisterCallback (0xA23FB154) takes the process's slot, or a free one (none:
/// 0x802F0C02), and keeps the callback, the 64-bit mask of the event kinds it wants and a ring of 16 events.
/// Every event (FUN_810024c8: kind, status byte, a word, the device address) goes into each slot whose mask has
/// 1 &lt;&lt; kind; a full ring counts the event lost instead. The slot's callback is then notified (argument 0).
/// sceBtReadEvent (0x26BAC23D) copies the events of the caller's slot, oldest first, as 16-byte records (+0 kind,
/// +1 status, +4 the word, +8 the address), at most <c>max</c>, and returns how many; a slot that lost events
/// answers 0x802F0C04 once (and is cleared of that), a process without a slot 0x802F0C03. sceBtUnregisterCallback
/// (0x605EF71F) frees the slot (none: 0x802F0C03).
/// Here the devices that connect and disconnect are the paired keyboard and mouse (the emulator option,
/// <see cref="HostServices.HidKeyboard"/>, <see cref="HostServices.HidMouse"/>): a connection is event 5 with status
/// 0 (FUN_81003b2c), a disconnection event 6 with the reason (FUN_8100c768: the HCI reason, 0x13 "remote user
/// terminated connection" for a device that went away).
/// </summary>
public sealed partial class SystemServicesVsh
{
    private const int BtSlots = 8, BtRingEvents = 16, BtEventSize = 0x10;
    private const int BT_ERR_SLOTS_FULL = unchecked((int)0x802F0C02), BT_ERR_NOT_REGISTERED = unchecked((int)0x802F0C03),
        BT_ERR_EVENTS_LOST = unchecked((int)0x802F0C04);

    /// <summary>The Bluetooth event kinds the emulated devices raise.</summary>
    internal const byte BtEventConnected = 5, BtEventDisconnected = 6;

    /// <summary>The HCI reason of a disconnection the device asked for (0x13: remote user terminated connection).</summary>
    internal const byte BtReasonRemoteTerminated = 0x13;

    /// <summary>The key of the caller's slot: its process id + 1 (0 marks a free slot; the system's threads are pid 0).</summary>
    private static int BtSlotOwner => ProcessScope.CurrentPid + 1;

    /// <summary>The owner of each slot (<see cref="BtSlotOwner"/>, 0 = free), its callback, the event kinds it takes, its ring (head = oldest,
    /// count) and the events it lost since its last read.</summary>
    [SnapshotMissing] // states of builds before it: no slot taken
    private readonly int[] _btSlotPid = new int[BtSlots];

    [SnapshotMissing] // states of builds before it: no slot taken
    private readonly int[] _btSlotCallback = new int[BtSlots];

    [SnapshotMissing] // states of builds before it: no slot taken
    private readonly int[] _btSlotHead = new int[BtSlots];

    [SnapshotMissing] // states of builds before it: no slot taken
    private readonly int[] _btSlotCount = new int[BtSlots];

    [SnapshotMissing] // states of builds before it: no slot taken
    private readonly int[] _btSlotLost = new int[BtSlots];

    [SnapshotMissing]
    private readonly ulong[] _btSlotMask = new ulong[BtSlots];

    [SnapshotMissing]
    private readonly byte[] _btEvents = new byte[BtSlots * BtRingEvents * BtEventSize];

    /// <summary>The host this module listens to and the devices connected when it last looked.</summary>
    [SnapshotIgnore]
    private HostServices? _btHost;

    [SnapshotIgnore]
    private bool _btKeyboardConnected, _btMouseConnected;

    /// <summary>(SceUID callback, int, uint maskLow, uint maskHigh): the caller's process gets a slot (a second call
    /// updates it).</summary>
    [Hle]
    int sceBtRegisterCallback(int callback, int unused, uint maskLow, uint maskHigh)
    {
        int pid = BtSlotOwner;
        lock (_btEvents)
        {
            int slot = Array.IndexOf(_btSlotPid, pid);
            if (slot < 0)
            {
                slot = Array.IndexOf(_btSlotPid, 0);
                if (slot < 0)
                {
                    return BT_ERR_SLOTS_FULL;
                }

                _btSlotPid[slot] = pid;
                _btSlotHead[slot] = 0;
                _btSlotCount[slot] = 0;
                _btSlotLost[slot] = 0;
            }

            _btSlotCallback[slot] = callback;
            _btSlotMask[slot] = maskLow | (ulong)maskHigh << 32;
        }

        ListenToBluetoothDevices();
        return 0;
    }

    [Hle]
    int sceBtUnregisterCallback(int callback)
    {
        lock (_btEvents)
        {
            int slot = Array.IndexOf(_btSlotPid, BtSlotOwner);
            if (slot < 0)
            {
                return BT_ERR_NOT_REGISTERED;
            }

            _btSlotPid[slot] = 0;
            _btSlotCallback[slot] = 0;
            return 0;
        }
    }

    /// <summary>(SceBtEvent *events, int max): the caller's queued events, oldest first.</summary>
    [Hle]
    int sceBtReadEvent(uint events, int max)
    {
        ListenToBluetoothDevices(); // after a load: the listener is not part of the state
        lock (_btEvents)
        {
            int slot = Array.IndexOf(_btSlotPid, BtSlotOwner);
            if (slot < 0)
            {
                return BT_ERR_NOT_REGISTERED;
            }

            if (_btSlotLost[slot] != 0)
            {
                _btSlotLost[slot] = 0;
                return BT_ERR_EVENTS_LOST;
            }

            int copied = 0;
            while (_btSlotCount[slot] > 0 && copied != max && events != 0)
            {
                int at = (slot * BtRingEvents + _btSlotHead[slot]) * BtEventSize;
                ReadOnlySpan<byte> record = _btEvents.AsSpan(at, BtEventSize);
                uint target = events + (uint)copied * BtEventSize;
                GuestMemory.Write(target, record[0]);
                GuestMemory.Write(target + 1, record[1]);
                GuestMemory.Write(target + 2, (ushort)0);
                GuestMemory.W32(target + 4, BitConverter.ToUInt32(record[4..]));
                GuestMemory.W32(target + 8, BitConverter.ToUInt32(record[8..]));
                GuestMemory.W32(target + 0xC, BitConverter.ToUInt32(record[12..]));
                _btSlotHead[slot] = (_btSlotHead[slot] + 1) % BtRingEvents;
                _btSlotCount[slot]--;
                copied++;
            }

            return copied;
        }
    }

    /// <summary>Queues one event for every slot that takes its kind and notifies their callbacks (FUN_810024c8).</summary>
    internal void RaiseBluetoothEvent(byte kind, byte status, uint word, uint mac0, uint mac1)
    {
        Span<int> notify = stackalloc int[BtSlots];
        int toNotify = 0;
        lock (_btEvents)
        {
            for (int slot = 0; slot < BtSlots; slot++)
            {
                if (_btSlotPid[slot] == 0 || (_btSlotMask[slot] & 1UL << kind) == 0)
                {
                    continue;
                }

                if (_btSlotCount[slot] >= BtRingEvents)
                {
                    _btSlotLost[slot]++;
                }
                else
                {
                    int index = (_btSlotHead[slot] + _btSlotCount[slot]) % BtRingEvents;
                    Span<byte> record = _btEvents.AsSpan((slot * BtRingEvents + index) * BtEventSize, BtEventSize);
                    record.Clear();
                    record[0] = kind;
                    record[1] = status;
                    BitConverter.TryWriteBytes(record[4..], word);
                    BitConverter.TryWriteBytes(record[8..], mac0);
                    BitConverter.TryWriteBytes(record[12..], mac1);
                    _btSlotCount[slot]++;
                }

                notify[toNotify++] = _btSlotCallback[slot];
            }
        }

        for (int i = 0; i < toNotify; i++)
        {
            Emu?.Kernel.Objects.Get<Callback>(notify[i])?.Notify(0);
        }
    }

    /// <summary>Listens (once) to the paired keyboard and mouse connecting and disconnecting.</summary>
    private void ListenToBluetoothDevices()
    {
        if (Emu?.Host is not { } host)
        {
            return;
        }

        lock (_btEvents)
        {
            if (_btHost != null)
            {
                return;
            }

            _btHost = host;
            _btKeyboardConnected = host.HidKeyboard != null;
            _btMouseConnected = host.HidMouse != null;
            host.HidDevicesChanged += OnBluetoothDevicesChanged;
        }
    }

    private void OnBluetoothDevicesChanged()
    {
        if (_btHost is not { } host)
        {
            return;
        }

        bool keyboard = host.HidKeyboard != null, mouse = host.HidMouse != null;
        bool keyboardChanged = keyboard != _btKeyboardConnected, mouseChanged = mouse != _btMouseConnected;
        _btKeyboardConnected = keyboard;
        _btMouseConnected = mouse;
        if (keyboardChanged)
        {
            RaiseConnection(keyboard, PairedKeyboardMac0);
        }

        if (mouseChanged)
        {
            RaiseConnection(mouse, PairedMouseMac0);
        }
    }

    private void RaiseConnection(bool connected, uint mac0)
    {
        if (connected)
        {
            RaiseBluetoothEvent(BtEventConnected, 0, 0, mac0, PairedMac1);
        }
        else
        {
            RaiseBluetoothEvent(BtEventDisconnected, BtReasonRemoteTerminated, 0, mac0, PairedMac1);
        }
    }
}
