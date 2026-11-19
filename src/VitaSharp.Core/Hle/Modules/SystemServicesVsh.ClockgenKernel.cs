using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

// SceClockgenForDriver (os0:kd/bootimage.skprx SceClockgen 3.74, read in the module, study only): the clock generator
// chip on I2C bus 0 (address 0xD2). At start the module reads its three registers 0x80..0x82 (the chip id, the clock
// enables, the spread setting; one byte at a time, or one block read when the read mode is 0) and keeps them with a
// copy of the boot values; the id decides what the chip has (0x2F: the wireless clock; low nibble 0xF above 0x2E: the
// second clock bit; the spread table by the id's low nibble). Each enable or disable flips one bit of register 0x81 in
// the kept copy, writes it to the chip (its answer ignored) and returns the bit's previous value; register reads past
// 2 are 0x803F0500, an audio rate other than 44100/48000 0x803F0501, a spread setting on a chip without a table
// 0x803F0502. The emulated unit's I2C buses have no device (SceI2c answers 0x803F0380): the start-up read leaves every
// register 0 (no wireless clock, no second clock bit, no spread table), as the module does without the chip.
public sealed partial class SystemServicesVsh
{
    /// <summary>SceClockgen: a register index above 2 (0x803F0500).</summary>
    const int CLOCKGEN_ERR_REGISTER = unchecked((int)0x803F0500);

    /// <summary>SceClockgen: an audio rate other than 44100 and 48000 (0x803F0501).</summary>
    const int CLOCKGEN_ERR_RATE = unchecked((int)0x803F0501);

    /// <summary>SceClockgen: no spread table for the chip's id (0x803F0502).</summary>
    const int CLOCKGEN_ERR_SPREAD = unchecked((int)0x803F0502);

    /// <summary>The chip id that has the wireless clock bit (0x2F).</summary>
    const byte ClockgenWirelessChipId = 0x2F;

    const int ClockgenRegisters = 3;

    [SnapshotMissing] // states of builds before it: the registers as read without the chip (0)
    readonly byte[] _clockgenRegisters = new byte[ClockgenRegisters];

    [SnapshotMissing]
    readonly byte[] _clockgenBootRegisters = new byte[ClockgenRegisters];

    [SnapshotMissing((byte)1)] // the module starts in the byte-at-a-time read mode
    byte _clockgenReadMode = 1;

    /// <summary>The kept registers, for the tests.</summary>
    internal byte[] ClockgenRegisterValues => _clockgenRegisters;

    /// <summary>The read mode kept, for the tests.</summary>
    internal byte ClockgenReadMode => _clockgenReadMode;

    byte ClockgenId => _clockgenRegisters[0];

    /// <summary>Whether the chip has the second clock bit (an id with low nibble 0xF above 0x2E).</summary>
    bool ClockgenHasSecondClock => (ClockgenId & 0xF) == 0xF && ClockgenId > 0x2E;

    /// <summary>Sets or clears one bit of register 0x81 in the kept copy (written to the chip, its answer ignored);
    /// the bit's previous value.</summary>
    int ClockgenFlip(byte bit, bool on)
    {
        byte enables = _clockgenRegisters[1];
        int previous = (enables & bit) != 0 ? 1 : 0;
        _clockgenRegisters[1] = on ? (byte)(enables | bit) : (byte)(enables & ~bit);
        return previous;
    }

    /// <summary>ksceClockgenSetReadMode(u8 mode): 0 reads the registers as one block, else one at a time; 0.</summary>
    [Hle(Nid = 0xEFE8A68C, Library = "SceClockgenForDriver")]
    int ksceClockgenSetReadMode(uint mode)
    {
        _clockgenReadMode = (byte)mode;
        return 0;
    }

    /// <summary>ksceClockgenReadRegisters(): reads the chip's registers again; no device answers (0x803F0380), the
    /// kept values stay.</summary>
    [Hle(Nid = 0x0BA41732, Library = "SceClockgenForDriver", Default = true)]
    int ksceClockgenReadRegisters()
    {
        return I2C_ERR_NO_ACK;
    }

    /// <summary>ksceClockgenGetChipId(): register 0x80, the chip id (0 without the chip).</summary>
    [Hle(Nid = 0xCF63B11D, Library = "SceClockgenForDriver", ReturnsValue = true)]
    int ksceClockgenGetChipId()
    {
        return ClockgenId;
    }

    /// <summary>ksceClockgenGetRegister(int index): register 0x80 + index (0..2); above 2 0x803F0500.</summary>
    [Hle(Nid = 0x09072366, Library = "SceClockgenForDriver")]
    int ksceClockgenGetRegister(uint index)
    {
        return index < ClockgenRegisters ? _clockgenRegisters[index] : CLOCKGEN_ERR_REGISTER;
    }

    /// <summary>ksceClockgenSetAudioRate(int rate): 48000 sets bit 0 of 0x81, 44100 clears it, other rates
    /// 0x803F0501; the bit's previous value.</summary>
    [Hle(Nid = 0x820D3516, Library = "SceClockgenForDriver")]
    int ksceClockgenSetAudioRate(uint rate)
    {
        if (rate is not (44100 or 48000))
        {
            return CLOCKGEN_ERR_RATE;
        }

        return ClockgenFlip(0x01, rate == 48000);
    }

    /// <summary>ksceClockgenAudioClkEnable(): sets bit 4 of 0x81; its previous value.</summary>
    [Hle(Nid = 0x5130682D, Library = "SceClockgenForDriver")]
    int ksceClockgenAudioClkEnable()
    {
        return ClockgenFlip(0x10, true);
    }

    /// <summary>ksceClockgenAudioClkDisable(): clears bit 4 of 0x81; its previous value.</summary>
    [Hle(Nid = 0xF0A0242D, Library = "SceClockgenForDriver")]
    int ksceClockgenAudioClkDisable()
    {
        return ClockgenFlip(0x10, false);
    }

    /// <summary>ksceClockgenWlanBtClkEnable(): on the chip with the wireless clock (id 0x2F) sets bit 7 of 0x81 and
    /// answers 1 if it was set already; on other chips 0.</summary>
    [Hle(Nid = 0x7B28A724, Library = "SceClockgenForDriver")]
    int ksceClockgenWlanBtClkEnable()
    {
        if (ClockgenId != ClockgenWirelessChipId)
        {
            return 0;
        }

        return ClockgenFlip(0x80, true);
    }

    /// <summary>ksceClockgenWlanBtClkDisable(): on the chip with the wireless clock (id 0x2F) clears bit 7 of 0x81 and
    /// answers 1 if it was set; on other chips 0.</summary>
    [Hle(Nid = 0xB6F0A532, Library = "SceClockgenForDriver")]
    int ksceClockgenWlanBtClkDisable()
    {
        if (ClockgenId != ClockgenWirelessChipId)
        {
            return 0;
        }

        return ClockgenFlip(0x80, false);
    }

    /// <summary>ksceClockgenCameraClkEnable(): on chips other than 0x2F sets bit 3 of 0x81 (its previous value); on
    /// 0x2F 0.</summary>
    [Hle(Nid = 0x06663ECA, Library = "SceClockgenForDriver")]
    int ksceClockgenCameraClkEnable()
    {
        if (ClockgenId == ClockgenWirelessChipId)
        {
            return 0;
        }

        return ClockgenFlip(0x08, true);
    }

    /// <summary>ksceClockgenCameraClkDisable(): on chips other than 0x2F clears bit 3 of 0x81 (its previous value); on
    /// 0x2F 0.</summary>
    [Hle(Nid = 0x82F39378, Library = "SceClockgenForDriver")]
    int ksceClockgenCameraClkDisable()
    {
        if (ClockgenId == ClockgenWirelessChipId)
        {
            return 0;
        }

        return ClockgenFlip(0x08, false);
    }

    /// <summary>ksceClockgenMotionClkEnable(): with the second clock bit sets bit 1 of 0x81 (its previous value);
    /// without it 0.</summary>
    [Hle(Nid = 0x62478916, Library = "SceClockgenForDriver")]
    int ksceClockgenMotionClkEnable()
    {
        return ClockgenHasSecondClock ? ClockgenFlip(0x02, true) : 0;
    }

    /// <summary>ksceClockgenMotionClkDisable(): with the second clock bit clears bit 1 of 0x81 (its previous value);
    /// without it 0.</summary>
    [Hle(Nid = 0x66AC5580, Library = "SceClockgenForDriver")]
    int ksceClockgenMotionClkDisable()
    {
        return ClockgenHasSecondClock ? ClockgenFlip(0x02, false) : 0;
    }

    /// <summary>ksceClockgenSetSpread(int level): a negative level restores the boot spread (unchanged: the level
    /// back; else written, the chip's answer); a level needs the chip's spread table, which a chip without an id has
    /// not (0x803F0502).</summary>
    [Hle(Nid = 0xA2CA8B65, Library = "SceClockgenForDriver")]
    int ksceClockgenSetSpread(int level)
    {
        if (level >= 0)
        {
            return CLOCKGEN_ERR_SPREAD;
        }

        if (((_clockgenBootRegisters[2] ^ _clockgenRegisters[2]) & 7) == 0)
        {
            return level;
        }

        _clockgenRegisters[2] = (byte)((_clockgenRegisters[2] & 0xF8) | (_clockgenBootRegisters[2] & 7));
        return I2C_ERR_NO_ACK;
    }
}
