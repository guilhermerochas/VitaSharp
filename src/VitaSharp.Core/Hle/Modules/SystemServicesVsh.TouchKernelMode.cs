using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

// SceTouchForDriver (os0:kd/bootimage.skprx SceTouch 3.74, read in the module, study only): the port enable flag
// (ScePower) and the panel firmware update mode (SceSblUpdateMgr). The module's mode word is set once the panels are
// initialised at boot; the update entry sets it to 5 (and waits for the sampling thread), leaving it sets 2, and the
// panel reset runs only in it (else 0x80350103). The emulated panels have no firmware to update: the mode is kept and
// each step succeeds. Two entries only store a word of the module (NID_062A11A2; NID_845E2115 stores it in two).
public sealed partial class SystemServicesVsh
{
    /// <summary>SceTouch: the panel firmware update mode is not on (0x80350103).</summary>
    const int TOUCH_ERR_NOT_UPDATE_MODE = unchecked((int)0x80350103);

    /// <summary>The module's mode word: 5 while the panel firmware is updated, 2 after the update mode is left.</summary>
    const int TouchModeUpdate = 5, TouchModeAfterUpdate = 2;

    [SnapshotMissing] // states of builds before it: both ports enabled
    readonly byte[] _touchDisabled = new byte[2];

    [SnapshotMissing]
    int _touchMode;

    [SnapshotMissing]
    uint _touchWord;

    /// <summary>The ports' disable states, the mode word and NID_062A11A2/NID_845E2115's word, for the tests.</summary>
    internal (byte Front, byte Back, int Mode, uint Word) TouchKernelMode => (_touchDisabled[0], _touchDisabled[1], _touchMode, _touchWord);

    /// <summary>ksceTouchSetEnableFlag(uint port, int enable): enabling clears the port's disable state; disabling an
    /// enabled port sets it to 1 while the port has a report in flight, else 2. Nothing of the emulated panel is
    /// pending at the kernel's call, so a disabled port is 2 (the module's value with nothing pending).</summary>
    [Hle(Nid = 0x328E38B0, Library = "SceTouchForDriver")]
    int ksceTouchSetEnableFlag(uint port, int enable)
    {
        if (port >= 2)
        {
            return TOUCH_ERR_INVALID_ARG;
        }

        if (enable != 0)
        {
            _touchDisabled[port] = 0;
        }
        else if (_touchDisabled[port] == 0)
        {
            _touchDisabled[port] = 2;
        }

        return 0;
    }

    /// <summary>NID_062A11A2 (SceTouchForDriver)(uint value): a word of the module kept.</summary>
    [Hle("NID_062A11A2", Nid = 0x062A11A2, Library = "SceTouchForDriver")]
    void TouchSetWord(uint value)
    {
        _touchWord = value;
    }

    /// <summary>NID_845E2115 (SceTouchForDriver)(uint value): two words of the module set to the value (kept as one).</summary>
    [Hle("NID_845E2115", Nid = 0x845E2115, Library = "SceTouchForDriver")]
    void TouchSetWords(uint value)
    {
        _touchWord = value;
    }

    /// <summary>NID_63DCF2FE (SceTouchForDriver)(): whether the module's mode word is set; the panels are initialised at
    /// boot, so 1.</summary>
    [Hle("NID_63DCF2FE", Nid = 0x63DCF2FE, Library = "SceTouchForDriver")]
    int TouchIsInitialized()
    {
        return 1;
    }

    /// <summary>NID_DDCF5145 (SceTouchForDriver)(): enters the panel firmware update mode (5); 0.</summary>
    [Hle("NID_DDCF5145", Nid = 0xDDCF5145, Library = "SceTouchForDriver")]
    int TouchEnterUpdateMode()
    {
        _touchMode = TouchModeUpdate;
        return 0;
    }

    /// <summary>NID_423F26D3 (SceTouchForDriver)(): leaves the update mode (2); 0x80350103 when not in it.</summary>
    [Hle("NID_423F26D3", Nid = 0x423F26D3, Library = "SceTouchForDriver")]
    int TouchLeaveUpdateMode()
    {
        if (_touchMode != TouchModeUpdate)
        {
            return TOUCH_ERR_NOT_UPDATE_MODE;
        }

        _touchMode = TouchModeAfterUpdate;
        return 0;
    }

    /// <summary>NID_4E6C4558 (SceTouchForDriver)(uint panels): in the update mode, resets the panels (bit 0 front, bit 1
    /// back) through syscon and reinitialises them (0); 0x80350103 when not in it.</summary>
    [Hle("NID_4E6C4558", Nid = 0x4E6C4558, Library = "SceTouchForDriver")]
    int TouchResetPanels(uint panels)
    {
        return _touchMode != TouchModeUpdate ? TOUCH_ERR_NOT_UPDATE_MODE : 0;
    }
}
