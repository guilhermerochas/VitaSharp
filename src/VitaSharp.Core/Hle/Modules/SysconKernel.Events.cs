using VitaSharp.Core.Kernel;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// SceSyscon's event handlers called when the chip changes (syscon.skprx 3.74, decompiled, study only: the reply
/// handler around 0x81000a18 and the table at 0x81009584). The module keeps one state byte per event bit of the chip's
/// status word (the table's 25 entries are bits 0..6, 8..10, 14..23 and 25..29; event n is bit n), started from the
/// status at boot (events 5 and 6 unknown, 0xFF, until the first report). When the chip reports (its GPIO interrupt,
/// "SceSysconCmdclr"), in interrupt context: a change of the status byte's bit 3 (the AC adapter) calls event 36's
/// handler with the bit; every table bit whose value differs from its state byte (or whose state is unknown) takes the
/// new value and calls its event's handler (value, argument), in table order; bit 5 going to 0 (nothing on the multi
/// connector) calls event 37's handler with the connector's information 0x4FF00, and bit 14 going to 0 calls event
/// 38's with the micro USB information (its bytes 1 and 2 cleared). The chip's interrupt is one of the interrupt
/// scheduler's HLE sources: a change of the chip (SysconChip.EventRaised) arms it for now.
/// </summary>
public sealed partial class SysconKernel : ISnapshotTimers
{
    /// <summary>The table's event bits, in its order.</summary>
    static readonly byte[] s_eventBits = [0, 1, 2, 3, 4, 5, 6, 8, 9, 10, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 25, 26, 27, 28, 29];

    /// <summary>The handlers the chip's other reports call: the AC adapter, the multi connector, the micro USB.</summary>
    const int AcAdapterEvent = 36, MultiConnectorEvent = 37, MicroUsbEvent = 38;

    const int MultiConnectorBit = 5, MicroUsbBit = 14;

    const byte UnknownState = 0xFF;

    /// <summary>Each event bit's state as the module last saw it (0xFF: not seen yet).</summary>
    [SnapshotMissing] // states of builds before it: started again from the chip at the next registration
    readonly byte[] _eventStates = new byte[32];

    /// <summary>The status byte as the module last saw it.</summary>
    [SnapshotMissing]
    byte _statusByteState;

    /// <summary>The states were started from the chip.</summary>
    [SnapshotMissing]
    bool _eventsStarted;

    SystemServicesVsh Intrmgr => Emu.Hle.Modules.OfType<SystemServicesVsh>().First();

    /// <summary>The module's start of the states (from the chip's status, as module_start reads it from the boot
    /// parameters), and the chip's event wired to the module's interrupt.</summary>
    void StartChipEvents()
    {
        lock (_eventStates)
        {
            if (!_eventsStarted)
            {
                uint word = Chip.StatusWord;
                foreach (byte bit in s_eventBits)
                {
                    _eventStates[bit] = (byte)(word >> bit & 1);
                }

                _eventStates[5] = UnknownState;
                _eventStates[6] = UnknownState;
                _statusByteState = Chip.StatusByte;
                _eventsStarted = true;
            }
        }

        Chip.EventRaised = ChipEventRaised;
    }

    /// <summary>The chip changed: its interrupt is taken now.</summary>
    void ChipEventRaised()
    {
        SystemServicesVsh intrmgr = Intrmgr;
        intrmgr.ArmHleInterrupt(SystemServicesVsh.HleInterruptSyscon, intrmgr.IntrNowUs, ChipInterrupt);
    }

    /// <summary>The module's handling of the chip's report (interrupt context, on <paramref name="thread"/>).</summary>
    void ChipInterrupt(GuestThread thread)
    {
        uint word = Chip.StatusWord;
        byte status = Chip.StatusByte;
        bool acChanged;
        uint changed = 0;
        lock (_eventStates)
        {
            acChanged = ((status ^ _statusByteState) & SysconChip.StatusAcAdapter) != 0;
            _statusByteState = status;
            foreach (byte bit in s_eventBits)
            {
                byte value = (byte)(word >> bit & 1);
                if (_eventStates[bit] != value)
                {
                    changed |= 1u << bit;
                }

                _eventStates[bit] = value;
            }
        }

        if (acChanged)
        {
            CallEventHandler(thread, AcAdapterEvent, (status & SysconChip.StatusAcAdapter) != 0 ? 1u : 0u);
        }

        foreach (byte bit in s_eventBits)
        {
            if ((changed >> bit & 1) != 0)
            {
                CallEventHandler(thread, bit, word >> bit & 1);
            }
        }

        if ((changed >> MultiConnectorBit & 1) != 0 && (word >> MultiConnectorBit & 1) == 0)
        {
            Chip.MultiCnInfo = SysconChip.NothingOnMultiConnector;
            CallEventHandler(thread, MultiConnectorEvent, SysconChip.NothingOnMultiConnector);
        }

        if ((changed >> MicroUsbBit & 1) != 0 && (word >> MicroUsbBit & 1) == 0)
        {
            uint info = Chip.MicroUsbInfo & 0xFF0000FF;
            Chip.MicroUsbInfo = info;
            CallEventHandler(thread, MicroUsbEvent, info);
        }
    }

    void CallEventHandler(GuestThread thread, int index, uint value)
    {
        (uint callback, uint argument) = EventHandler(index);
        if (callback != 0)
        {
            thread.CallGuest(callback, value, argument);
        }
    }

    /// <summary>The chip's event is wired again after a load (a session that never registered a handler had not wired
    /// it), and a change the state had not reported yet is reported.</summary>
    void ISnapshotTimers.RearmTimers()
    {
        if (!_eventsStarted)
        {
            return;
        }

        StartChipEvents();
        bool pending;
        lock (_eventStates)
        {
            uint word = Chip.StatusWord;
            pending = ((Chip.StatusByte ^ _statusByteState) & SysconChip.StatusAcAdapter) != 0;
            foreach (byte bit in s_eventBits)
            {
                pending |= _eventStates[bit] != (byte)(word >> bit & 1);
            }
        }

        if (pending)
        {
            ChipEventRaised();
        }
    }
}
