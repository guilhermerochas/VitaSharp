using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

// SceOledForDriver (os0:kd/oled.skprx 3.74, read in the module, study only): the OLED panel of the PCH-1000 handheld,
// driven over SPI 2. At start the module reads the panel's DDB (its id word must be 0, else the panel is unusable:
// 0x803F0A03; before the read ends 0x803F0A04). The power-on routine raises the panel's reset line (GPIO 0, 0), sends
// its init sequence, picks the brightness table by the DDB revision and enables the colour space setting; brightness
// needs that table (else 0x803F0A05), takes 0..0x10000 (else 0x803F0A02; 0 turns the panel off) and is kept; the
// colour space mode is 0 or 1 (else 0x803F0A02), only kept while the panel is dark. Display on, off and the other
// fixed routines queue one of the module's command sequences (PanelSequence); a pending one makes them 0x803F0A05.
// The emulated unit is a PCH-1000: its panel is there (DDB id 0, revision 0), its registers read 0 and take every
// command.
public sealed partial class SystemServicesVsh
{
    /// <summary>SceOled: a value out of range (0x803F0A02).</summary>
    const int OLED_ERR_INVALID_ARGUMENT = unchecked((int)0x803F0A02);

    /// <summary>SceOled: a sequence is pending, or the brightness table is not set yet (0x803F0A05).</summary>
    const int OLED_ERR_BUSY = unchecked((int)0x803F0A05);

    /// <summary>The brightness the panel takes at most (0x10000, kscePowerGetDisplayMaxBrightness).</summary>
    const uint OledMaxBrightness = 0x10000;

    /// <summary>The OLED pauses count one step less than the LCD's (the step that reads the pause counts).</summary>
    const int OledPauseBias = -1;

    /// <summary>The emulated panel's DDB: id 0 (a usable panel), revision 0.</summary>
    const ushort OledDdbId = 0, OledDdbRevision = 0;

    [SnapshotMissing] // states of builds before it: no sequence pending
    uint _oledSequence;

    [SnapshotMissing]
    int _oledPause;

    [SnapshotMissing]
    uint _oledSynchronous;

    [SnapshotMissing]
    uint _oledBrightness;

    [SnapshotMissing]
    uint _oledColorSpaceMode;

    [SnapshotMissing] // the power-on routine has not run: no brightness table, no colour space setting
    bool _oledPoweredOn;

    /// <summary>The pending sequence and its pause counter, for the tests.</summary>
    internal (uint Sequence, int Pause) OledSequenceState => (_oledSequence, _oledPause);

    /// <summary>Queues a sequence: refused while one is pending; run to the end when the driver is synchronous or the
    /// caller waits for it.</summary>
    int OledQueue(uint sequence, bool wait)
    {
        if (_oledSequence != 0)
        {
            return OLED_ERR_BUSY;
        }

        _oledSequence = sequence;
        _oledPause = 0;
        if (wait || _oledSynchronous != 0)
        {
            PanelSequenceRun(ref _oledSequence, ref _oledPause, OledPauseBias);
        }

        return 0;
    }

    /// <summary>ksceOledResetLineClear(): lowers the panel's reset line (GPIO 0, port 0); 0.</summary>
    [Hle(Nid = 0x26F9EEA8, Library = "SceOledForDriver")]
    int ksceOledResetLineClear()
    {
        ksceGpioPortClearThroughEntry(0, 0);
        return 0;
    }

    /// <summary>ksceOledResetLineSet(): raises the panel's reset line (GPIO 0, port 0); 0.</summary>
    [Hle(Nid = 0x6B1E0B52, Library = "SceOledForDriver")]
    int ksceOledResetLineSet()
    {
        ksceGpioPortSetThroughEntry(0, 0);
        return 0;
    }

    /// <summary>ksceOledIsResetLineLow(): 1 while the panel's reset line reads 0.</summary>
    [Hle(Nid = 0x9F4ABDDC, Library = "SceOledForDriver")]
    int ksceOledIsResetLineLow()
    {
        return ksceGpioPortReadThroughEntry(0, 0) == 0 ? 1 : 0;
    }

    /// <summary>ksceOledWaitReady(): waits for the start-up DDB read, already done on the emulated unit; 0.</summary>
    [Hle(Nid = 0x0CC6BCB4, Library = "SceOledForDriver")]
    int ksceOledWaitReady()
    {
        return 0;
    }

    /// <summary>ksceOledGetDDB(u16 *id, u16 *revision): the panel's DDB words (either pointer may be NULL); 0.</summary>
    [Hle(Nid = 0xC9D5987C, Library = "SceOledForDriver")]
    int ksceOledGetDDB(uint id, uint revision)
    {
        if (id != 0)
        {
            GuestMemory.Write(id, OledDdbId);
        }

        if (revision != 0)
        {
            GuestMemory.Write(revision, OledDdbRevision);
        }

        return 0;
    }

    /// <summary>ksceOledReadRegister(int command, u8 *buffer, int length): reads the panel's answer to a command;
    /// the emulated panel answers 0 bytes of value 0; 0.</summary>
    [Hle(Nid = 0x3E6AE8A2, Library = "SceOledForDriver", Default = true)]
    int ksceOledReadRegister(uint command, uint buffer, uint length)
    {
        if (buffer != 0 && length != 0xFFFFFFFF && length <= 0x100)
        {
            GuestMemory.Fill(buffer, 0, length);
        }

        return 0;
    }

    /// <summary>ksceOledWriteCommand(u8 command, const u8 *data, int length): sends a command to the panel; 0.</summary>
    [Hle(Nid = 0x2F0C4B67, Library = "SceOledForDriver", Default = true)]
    int ksceOledWriteCommand(uint command, uint data, uint length)
    {
        return 0;
    }

    /// <summary>ksceOledPowerOn(): raises the reset line, waits for the panel and sends its init sequence (pending
    /// one: 0x803F0A05), then sets the brightness table and enables the colour space setting; brightness 0.</summary>
    [Hle(Nid = 0xDDB1412B, Library = "SceOledForDriver")]
    int ksceOledPowerOn()
    {
        ksceOledResetLineSetThroughEntry();
        _oledBrightness = 0;
        int queued = OledQueue(PanelBuiltInSequence, wait: true);
        if (queued != 0)
        {
            return queued;
        }

        _oledPoweredOn = true;
        return 0;
    }

    /// <summary>ksceOledDisplayOn(): queues the display-on sequence; pending one: 0x803F0A05.</summary>
    [Hle(Nid = 0x4C7836C7, Library = "SceOledForDriver")]
    int ksceOledDisplayOn()
    {
        return OledQueue(PanelBuiltInSequence, wait: false);
    }

    /// <summary>ksceOledDisplayOff(): queues the display-off sequence (pending one: 0x803F0A05); brightness 0.</summary>
    [Hle(Nid = 0xBC84602E, Library = "SceOledForDriver")]
    int ksceOledDisplayOff()
    {
        int queued = OledQueue(PanelBuiltInSequence, wait: false);
        if (queued != 0)
        {
            return queued;
        }

        _oledBrightness = 0;
        return 0;
    }

    /// <summary>ksceOledRunFixedSequence(): queues the module's third fixed sequence; pending one: 0x803F0A05.</summary>
    [Hle(Nid = 0x4891257F, Library = "SceOledForDriver")]
    int ksceOledRunFixedSequence()
    {
        return OledQueue(PanelBuiltInSequence, wait: false);
    }

    /// <summary>ksceOledGetPanelState(): bit 2 of the panel's register 10 (the emulated panel's: 0).</summary>
    [Hle(Nid = 0x3C990F53, Library = "SceOledForDriver")]
    int ksceOledGetPanelState()
    {
        return 0;
    }

    /// <summary>ksceOledSetBrightness(uint level): no brightness table yet 0x803F0A05, above 0x10000 0x803F0A02; 0
    /// queues the off sequence, any other level the table's entry for it (pending one: 0x803F0A05); the level kept.</summary>
    [Hle(Nid = 0xF9624C47, Library = "SceOledForDriver")]
    int ksceOledSetBrightness(uint level)
    {
        if (!_oledPoweredOn)
        {
            return OLED_ERR_BUSY;
        }

        if (level > OledMaxBrightness)
        {
            return OLED_ERR_INVALID_ARGUMENT;
        }

        int queued = OledQueue(PanelBuiltInSequence, wait: false);
        if (queued != 0)
        {
            return queued;
        }

        _oledBrightness = level;
        return 0;
    }

    /// <summary>ksceOledGetBrightness(): the level last set (0 after display off or power-on).</summary>
    [Hle(Nid = 0x43EF811A, Library = "SceOledForDriver")]
    int ksceOledGetBrightness()
    {
        return (int)_oledBrightness;
    }

    /// <summary>ksceOledSetDisplayColorSpaceMode(int mode): 0 or 1 (else 0x803F0A02); before power-on ignored (0);
    /// kept while the panel is dark, else queued (pending one: 0x803F0A05).</summary>
    [Hle(Nid = 0xDABBD9D3, Library = "SceOledForDriver")]
    int ksceOledSetDisplayColorSpaceMode(uint mode)
    {
        if (mode > 1)
        {
            return OLED_ERR_INVALID_ARGUMENT;
        }

        if (!_oledPoweredOn)
        {
            return 0;
        }

        if (_oledBrightness != 0)
        {
            int queued = OledQueue(PanelBuiltInSequence, wait: false);
            if (queued != 0)
            {
                return queued;
            }
        }

        _oledColorSpaceMode = mode;
        return 0;
    }

    /// <summary>ksceOledGetDisplayColorSpaceMode(): the mode kept (0 at start).</summary>
    [Hle(Nid = 0x4F8A1D4A, Library = "SceOledForDriver")]
    int ksceOledGetDisplayColorSpaceMode()
    {
        return (int)_oledColorSpaceMode;
    }

    /// <summary>ksceOledSetSynchronous(int on): nonzero runs each queued sequence in the caller (its own waits)
    /// instead of SceDisplay's vertical blank; 0.</summary>
    [Hle(Nid = 0xED2D6F19, Library = "SceOledForDriver")]
    int ksceOledSetSynchronous(uint on)
    {
        _oledSynchronous = on;
        return 0;
    }

    /// <summary>ksceOledVblankStep(): one step of the pending sequence (SceDisplay's vertical blank handler); 0.</summary>
    [Hle(Nid = 0xE30604CC, Library = "SceOledForDriver")]
    int ksceOledVblankStep()
    {
        PanelSequenceStep(ref _oledSequence, ref _oledPause, OledPauseBias);
        return 0;
    }

    /// <summary>ksceOledRunSequence(const u8 *sequence): runs the caller's sequence to its end (pending one:
    /// 0x803F0A05); 0.</summary>
    [Hle(Nid = 0xAD6C7FB8, Library = "SceOledForDriver")]
    int ksceOledRunSequence(uint sequence)
    {
        return OledQueue(sequence, wait: true);
    }

    /// <summary>ksceOledQueueSequence(const u8 *sequence): leaves the caller's sequence to the vertical blank steps
    /// (pending one: 0x803F0A05); 0.</summary>
    [Hle(Nid = 0x5AF31932, Library = "SceOledForDriver")]
    int ksceOledQueueSequence(uint sequence)
    {
        if (_oledSequence != 0)
        {
            return OLED_ERR_BUSY;
        }

        _oledSequence = sequence;
        _oledPause = 0;
        return 0;
    }

    /// <summary>ksceOledSequencePending(int query): nonzero asks whether a sequence is pending (1/0); 0 runs the
    /// pending one to its end and answers 0.</summary>
    [Hle(Nid = 0xBF5C5624, Library = "SceOledForDriver")]
    int ksceOledSequencePending(uint query)
    {
        if (query != 0)
        {
            return _oledSequence != 0 ? 1 : 0;
        }

        PanelSequenceRun(ref _oledSequence, ref _oledPause, OledPauseBias);
        return 0;
    }
}
