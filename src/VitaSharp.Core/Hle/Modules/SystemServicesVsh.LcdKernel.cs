using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

// SceLcdForDriver (os0:kd/lcd.skprx 3.74, read in the module, study only): the LCD panel of the PCH-2000 handheld,
// driven over SPI 2, and its backlight over I2C. The module does not start where SceOled is loaded. At start it reads
// the panel's DDB (no panel: the reads and commands answer 0x803F0C03). Display on/off, the two 0/1 modes (else
// 0x803F0C02; kept as they are while the display is off) and the fixed routines queue one of the module's command
// sequences (PanelSequence; a pending one: 0x803F0C05). Brightness takes 0..0x10000 (else 0x803F0C02): 0 turns the
// display off, any other level turns it on and sets the backlight target, which a thread of the module reaches once the
// power-on routine has reset the backlight. The emulated unit is a PCH-1000: there is no LCD panel to answer, and the
// backlight follows its target at once.
public sealed partial class SystemServicesVsh
{
    /// <summary>SceLcd: a value out of range (0x803F0C02).</summary>
    const int LCD_ERR_INVALID_ARGUMENT = unchecked((int)0x803F0C02);

    /// <summary>SceLcd: the panel did not answer at start (0x803F0C03).</summary>
    const int LCD_ERR_NO_PANEL = unchecked((int)0x803F0C03);

    /// <summary>SceLcd: a sequence is pending (0x803F0C05).</summary>
    const int LCD_ERR_BUSY = unchecked((int)0x803F0C05);

    /// <summary>The brightness the panel takes at most (0x10000).</summary>
    const uint LcdMaxBrightness = 0x10000;

    [SnapshotMissing] // states of builds before it: no sequence pending, display off
    uint _lcdSequence;

    [SnapshotMissing]
    int _lcdPause;

    [SnapshotMissing]
    uint _lcdSynchronous;

    [SnapshotMissing]
    bool _lcdDisplayOn;

    [SnapshotMissing]
    uint _lcdBrightness;

    [SnapshotMissing]
    uint _lcdPanelMode;

    [SnapshotMissing]
    uint _lcdColorSpaceMode;

    [SnapshotMissing] // the backlight reset of the power-on routine has not run: the target is not applied
    bool _lcdBacklightReady;

    [SnapshotMissing]
    uint _lcdBacklightTarget;

    [SnapshotMissing]
    uint _lcdBacklightLevel;

    /// <summary>The display state, brightness and backlight, for the tests.</summary>
    internal (bool On, uint Brightness, uint Target, uint Level, uint Sequence) LcdState =>
        (_lcdDisplayOn, _lcdBrightness, _lcdBacklightTarget, _lcdBacklightLevel, _lcdSequence);

    /// <summary>Queues a sequence: refused while one is pending; run to the end when the driver is synchronous or the
    /// caller waits for it.</summary>
    int LcdQueue(uint sequence, bool wait)
    {
        if (_lcdSequence != 0)
        {
            return LCD_ERR_BUSY;
        }

        _lcdSequence = sequence;
        _lcdPause = 0;
        if (wait || _lcdSynchronous != 0)
        {
            PanelSequenceRun(ref _lcdSequence, ref _lcdPause, 0);
        }

        return 0;
    }

    /// <summary>The backlight thread: once reset, the backlight reaches its target (at once on the emulated unit).</summary>
    void LcdApplyBacklight()
    {
        if (_lcdBacklightReady)
        {
            _lcdBacklightLevel = _lcdBacklightTarget;
        }
    }

    /// <summary>ksceLcdResetLinesClear(): lowers the panel's reset lines (GPIO 0, 0 and 1, 7); 0.</summary>
    [Hle(Nid = 0x949D45E4, Library = "SceLcdForDriver")]
    int ksceLcdResetLinesClear()
    {
        ksceGpioPortClear(0, 0);
        ksceGpioPortClear(1, 7);
        return 0;
    }

    /// <summary>ksceLcdResetLinesSet(): raises the panel's reset lines (GPIO 0, 0 and 1, 7); 0.</summary>
    [Hle(Nid = 0xC0A2A6EA, Library = "SceLcdForDriver")]
    int ksceLcdResetLinesSet()
    {
        ksceGpioPortSet(0, 0);
        ksceGpioPortSet(1, 7);
        return 0;
    }

    /// <summary>ksceLcdIsResetLineLow(): 1 while the reset line (GPIO 0, 0) reads 0.</summary>
    [Hle(Nid = 0x6A08623C, Library = "SceLcdForDriver")]
    int ksceLcdIsResetLineLow()
    {
        return ksceGpioPortRead(0, 0) == 0 ? 1 : 0;
    }

    /// <summary>ksceLcdWaitReady(): waits for the start-up DDB read, which has ended (without a panel); 0.</summary>
    [Hle(Nid = 0x0C7E03D8, Library = "SceLcdForDriver")]
    int ksceLcdWaitReady()
    {
        return 0;
    }

    /// <summary>ksceLcdGetDDB(u16 *id, u16 *revision): no LCD panel answered (0x803F0C03).</summary>
    [Hle(Nid = 0xE03E120B, Library = "SceLcdForDriver", Default = true)]
    int ksceLcdGetDDB(uint id, uint revision)
    {
        return LCD_ERR_NO_PANEL;
    }

    /// <summary>ksceLcdReadRegister(int command, u8 *buffer, int length): no LCD panel (0x803F0C03).</summary>
    [Hle(Nid = 0x223C8375, Library = "SceLcdForDriver", Default = true)]
    int ksceLcdReadRegister(uint command, uint buffer, uint length)
    {
        return LCD_ERR_NO_PANEL;
    }

    /// <summary>ksceLcdWriteCommand(u8 command, const u8 *data, int length): no LCD panel (0x803F0C03).</summary>
    [Hle(Nid = 0x5127FB5E, Library = "SceLcdForDriver", Default = true)]
    int ksceLcdWriteCommand(uint command, uint data, uint length)
    {
        return LCD_ERR_NO_PANEL;
    }

    /// <summary>ksceLcdGetPanelState(): bit 2 of the panel's register 10; the read fails (0x803F0C03).</summary>
    [Hle(Nid = 0x8C965153, Library = "SceLcdForDriver", Default = true)]
    int ksceLcdGetPanelState()
    {
        return LCD_ERR_NO_PANEL;
    }

    /// <summary>ksceLcdGetBrightness(): the level last set.</summary>
    [Hle(Nid = 0x3A6D6AC3, Library = "SceLcdForDriver")]
    int ksceLcdGetBrightness()
    {
        return (int)_lcdBrightness;
    }

    /// <summary>ksceLcdGetPanelMode(): the second 0/1 mode kept.</summary>
    [Hle(Nid = 0x4C277FEC, Library = "SceLcdForDriver")]
    int ksceLcdGetPanelMode()
    {
        return (int)_lcdPanelMode;
    }

    /// <summary>ksceLcdGetDisplayColorSpaceMode(): the colour space mode kept.</summary>
    [Hle(Nid = 0x17F66722, Library = "SceLcdForDriver")]
    int ksceLcdGetDisplayColorSpaceMode()
    {
        return (int)_lcdColorSpaceMode;
    }

    /// <summary>ksceLcdSetSynchronous(int on): nonzero runs each queued sequence in the caller; 0.</summary>
    [Hle(Nid = 0x4CC1E8C1, Library = "SceLcdForDriver")]
    int ksceLcdSetSynchronous(uint on)
    {
        _lcdSynchronous = on;
        return 0;
    }

    /// <summary>ksceLcdVblankStep(): one step of the pending sequence (SceDisplay's vertical blank handler); 0.</summary>
    [Hle(Nid = 0x59CCDC97, Library = "SceLcdForDriver")]
    int ksceLcdVblankStep()
    {
        PanelSequenceStep(ref _lcdSequence, ref _lcdPause, 0);
        return 0;
    }

    /// <summary>ksceLcdQueueSequence(const u8 *sequence): leaves the caller's sequence to the vertical blank steps
    /// (pending one: 0x803F0C05); 0.</summary>
    [Hle(Nid = 0x4B6B53C0, Library = "SceLcdForDriver")]
    int ksceLcdQueueSequence(uint sequence)
    {
        if (_lcdSequence != 0)
        {
            return LCD_ERR_BUSY;
        }

        _lcdSequence = sequence;
        _lcdPause = 0;
        return 0;
    }

    /// <summary>ksceLcdSequencePending(int query): nonzero asks whether a sequence or a backlight change is pending
    /// (1/0); 0 runs the pending sequence to its end and answers 0.</summary>
    [Hle(Nid = 0x4735059C, Library = "SceLcdForDriver")]
    int ksceLcdSequencePending(uint query)
    {
        if (query != 0)
        {
            bool pending = _lcdSequence != 0 || _lcdBacklightLevel != _lcdBacklightTarget;
            return pending ? 1 : 0;
        }

        PanelSequenceRun(ref _lcdSequence, ref _lcdPause, 0);
        return 0;
    }

    /// <summary>ksceLcdDisplayOn(): already on 0; else queues the display-on sequence (pending one: 0x803F0C05).</summary>
    [Hle(Nid = 0x5F4124AB, Library = "SceLcdForDriver")]
    int ksceLcdDisplayOn()
    {
        if (_lcdDisplayOn)
        {
            return 0;
        }

        int queued = LcdQueue(PanelBuiltInSequence, wait: false);
        if (queued != 0)
        {
            return queued;
        }

        _lcdDisplayOn = true;
        return 0;
    }

    /// <summary>ksceLcdDisplayOff(): off already 0; else queues the display-off sequence (pending one: 0x803F0C05),
    /// brightness and backlight target 0.</summary>
    [Hle(Nid = 0x1A0A7519, Library = "SceLcdForDriver")]
    int ksceLcdDisplayOff()
    {
        if (!_lcdDisplayOn)
        {
            return 0;
        }

        int queued = LcdQueue(PanelBuiltInSequence, wait: false);
        if (queued != 0)
        {
            return queued;
        }

        _lcdDisplayOn = false;
        _lcdBrightness = 0;
        _lcdBacklightTarget = 0;
        LcdApplyBacklight();
        return 0;
    }

    /// <summary>ksceLcdRunFixedSequence(): queues the module's fixed sequence (pending one: 0x803F0C05).</summary>
    [Hle(Nid = 0x9F1DB5CD, Library = "SceLcdForDriver")]
    int ksceLcdRunFixedSequence()
    {
        return LcdQueue(PanelBuiltInSequence, wait: false);
    }

    /// <summary>ksceLcdSetBrightness(uint level): above 0x10000 0x803F0C02; 0 turns the display off, another level
    /// on (pending sequence: 0x803F0C05); the level and the backlight target kept.</summary>
    [Hle(Nid = 0x581D3A87, Library = "SceLcdForDriver")]
    int ksceLcdSetBrightness(uint level)
    {
        if (level > LcdMaxBrightness)
        {
            return LCD_ERR_INVALID_ARGUMENT;
        }

        bool turnOn = level != 0;
        if (turnOn != _lcdDisplayOn)
        {
            int queued = LcdQueue(PanelBuiltInSequence, wait: false);
            if (queued != 0)
            {
                return queued;
            }

            _lcdDisplayOn = turnOn;
        }

        _lcdBrightness = level;
        _lcdBacklightTarget = level;
        LcdApplyBacklight();
        return 0;
    }

    /// <summary>ksceLcdSetPanelMode(int mode): 0 or 1 (else 0x803F0C02); kept while off, else queued (pending one:
    /// 0x803F0C05).</summary>
    [Hle(Nid = 0x1D73D7F3, Library = "SceLcdForDriver")]
    int ksceLcdSetPanelMode(uint mode)
    {
        return LcdSetMode(mode, ref _lcdPanelMode);
    }

    /// <summary>ksceLcdSetDisplayColorSpaceMode(int mode): 0 or 1 (else 0x803F0C02); kept while off, else queued
    /// (pending one: 0x803F0C05).</summary>
    [Hle(Nid = 0xD40968FB, Library = "SceLcdForDriver")]
    int ksceLcdSetDisplayColorSpaceMode(uint mode)
    {
        return LcdSetMode(mode, ref _lcdColorSpaceMode);
    }

    int LcdSetMode(uint mode, ref uint kept)
    {
        if (mode > 1)
        {
            return LCD_ERR_INVALID_ARGUMENT;
        }

        if (_lcdDisplayOn)
        {
            int queued = LcdQueue(PanelBuiltInSequence, wait: false);
            if (queued != 0)
            {
                return queued;
            }
        }

        kept = mode;
        return 0;
    }

    /// <summary>ksceLcdRunSequence(const u8 *sequence): runs the caller's sequence to its end (pending one:
    /// 0x803F0C05); 0.</summary>
    [Hle(Nid = 0x04E9269A, Library = "SceLcdForDriver")]
    int ksceLcdRunSequence(uint sequence)
    {
        return LcdQueue(sequence, wait: true);
    }

    /// <summary>ksceLcdPowerOn(): raises the reset lines, display off, brightness and backlight 0, sends the init
    /// sequence (pending one: 0x803F0C05) and starts the backlight reset, after which the target applies; 0.</summary>
    [Hle(Nid = 0x3E7C2CCA, Library = "SceLcdForDriver")]
    int ksceLcdPowerOn()
    {
        ksceLcdResetLinesSet();
        _lcdBacklightLevel = 0;
        _lcdBacklightReady = false;
        _lcdDisplayOn = false;
        _lcdBrightness = 0;
        _lcdBacklightTarget = 0;
        int queued = LcdQueue(PanelBuiltInSequence, wait: true);
        if (queued != 0)
        {
            return queued;
        }

        _lcdBacklightReady = true;
        LcdApplyBacklight();
        return 0;
    }
}
