using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

// SceMotionDevForDriver (os0:kd/bootimage.skprx SceMotionDev 3.74, read in the module, study only), the entries the
// motion sensor's user side does not have: the module's device state (2 once the sensor answered at boot; 4 in the
// sensor firmware update mode SceSblUpdateMgr enters, 3 while it is entered), the sensor's firmware version (from the
// device info; the emulated sensor reports zeros, version 0), a word AppMgr sets and reads back, and the virtual
// driver registration SceDs3 calls (empty on 3.74). Starting and stopping the module's alarm and wake-up timers need
// state 2 (else 0x80360003); the firmware update writes need state 4 (else 0x80360002), a free update slot (else
// 0x80360003) and NULL checks (0x80360001). The emulated sensor has no firmware: the update steps succeed (0) and
// the timers start and stop (0).
public sealed partial class SystemServicesVsh
{
    /// <summary>SceMotionDev: a NULL pointer (0x80360001).</summary>
    const int MOTION_ERR_NULL = unchecked((int)0x80360001);

    /// <summary>SceMotionDev: not in the update mode (0x80360002).</summary>
    const int MOTION_ERR_NOT_UPDATING = unchecked((int)0x80360002);

    /// <summary>SceMotionDev: the device is not ready, or the update slot is taken (0x80360003).</summary>
    const int MOTION_ERR_NOT_READY = unchecked((int)0x80360003);

    /// <summary>SceMotionDev: the device state answers neither 1 nor 2 (0x80360004).</summary>
    const int MOTION_ERR_STATE = unchecked((int)0x80360004);

    /// <summary>The module's device states: ready, in the update mode.</summary>
    const int MotionReady = 2, MotionUpdating = 4;

    /// <summary>The emulated sensor's firmware version (its device info is zeros).</summary>
    static int MotionFirmwareVersion => 0;

    [SnapshotMissing(2)] // states of builds before it: the sensor ready
    int _motionState = MotionReady;

    [SnapshotMissing]
    uint _motionWord;

    [SnapshotMissing]
    int _motionMode;

    /// <summary>The device state, AppMgr's word and NID_0808D77D's mode, for the tests.</summary>
    internal (int State, uint Word, int Mode) MotionKernelValues => (_motionState, _motionWord, _motionMode);

    /// <summary>ksceMotionDevRegisterVirtualMotionDriver(driver): empty on 3.74; 0.</summary>
    [Hle(Nid = 0x7FD06731, Library = "SceMotionDevForDriver")]
    int ksceMotionDevRegisterVirtualMotionDriver(uint driver)
    {
        return 0;
    }

    /// <summary>ksceMotionDevNoiseFilterIsAvailable(): the sensor firmware is 0x1010..0x11FF; version 0: 0.</summary>
    [Hle(Nid = 0x11A17A96, Library = "SceMotionDevForDriver")]
    int ksceMotionDevNoiseFilterIsAvailable()
    {
        return (uint)(MotionFirmwareVersion - 0x1010) < 0x1F0 ? 1 : 0;
    }

    /// <summary>NID_77BA3A04 (SceMotionDevForDriver)(): the sensor firmware is older than 0x2000; version 0: 1.</summary>
    [Hle("NID_77BA3A04", Nid = 0x77BA3A04, Library = "SceMotionDevForDriver")]
    int MotionDevIsOldFirmware()
    {
        return MotionFirmwareVersion < 0x2000 ? 1 : 0;
    }

    /// <summary>NID_22CD6DCA (SceMotionDevForDriver)(uint value) (SceAppMgr): a word kept; 0.</summary>
    [Hle("NID_22CD6DCA", Nid = 0x22CD6DCA, Library = "SceMotionDevForDriver")]
    int MotionDevSetWord(uint value)
    {
        _motionWord = value;
        return 0;
    }

    /// <summary>NID_3CD0CE14 (SceMotionDevForDriver)(uint *out): the kept word; NULL is 0x80360001.</summary>
    [Hle("NID_3CD0CE14", Nid = 0x3CD0CE14, Library = "SceMotionDevForDriver")]
    int MotionDevGetWord(uint output)
    {
        if (output == 0)
        {
            return MOTION_ERR_NULL;
        }

        GuestMemory.W32(output, _motionWord);
        return 0;
    }

    /// <summary>NID_FE6ECA41 (SceMotionDevForDriver)(int *out): 1 while the device state is 1 or 2, else 0x80360004.</summary>
    [Hle("NID_FE6ECA41", Nid = 0xFE6ECA41, Library = "SceMotionDevForDriver")]
    int MotionDevIsActive(uint output)
    {
        if (output == 0)
        {
            return MOTION_ERR_NULL;
        }

        if ((uint)(_motionState - 1) > 1)
        {
            return MOTION_ERR_STATE;
        }

        GuestMemory.W32(output, 1);
        return 0;
    }

    /// <summary>NID_0808D77D (SceMotionDevForDriver)(int mode): the module's alarm mode, kept (the alarm timer starts
    /// in the module's thread); 0.</summary>
    [Hle("NID_0808D77D", Nid = 0x0808D77D, Library = "SceMotionDevForDriver")]
    int MotionDevSetAlarmMode(int mode)
    {
        _motionMode = mode;
        return 0;
    }

    /// <summary>NID_031AF7A1 (SceMotionDevForDriver)() (SceEnumWakeUp): starts the sensor's wake-up detection; state 2.</summary>
    [Hle("NID_031AF7A1", Nid = 0x031AF7A1, Library = "SceMotionDevForDriver", Default = true)]
    int MotionDevStartWakeUp()
    {
        return _motionState == MotionReady ? 0 : MOTION_ERR_NOT_READY;
    }

    /// <summary>NID_85E3C678 (SceMotionDevForDriver)(): stops the sensor's wake-up detection; state 2.</summary>
    [Hle("NID_85E3C678", Nid = 0x85E3C678, Library = "SceMotionDevForDriver", Default = true)]
    int MotionDevStopWakeUp()
    {
        return _motionState == MotionReady ? 0 : MOTION_ERR_NOT_READY;
    }

    /// <summary>NID_FD1C528D (SceMotionDevForDriver)() (SceSblUpdateMgr): enters the update mode (state 4); 0.</summary>
    [Hle("NID_FD1C528D", Nid = 0xFD1C528D, Library = "SceMotionDevForDriver", Default = true)]
    int MotionDevEnterUpdateMode()
    {
        _motionState = MotionUpdating;
        return 0;
    }

    /// <summary>NID_A75976EE (SceMotionDevForDriver)() (SceSblUpdateMgr): resets the sensor and leaves the update mode;
    /// 0x80360002 when not in it.</summary>
    [Hle("NID_A75976EE", Nid = 0xA75976EE, Library = "SceMotionDevForDriver", Default = true)]
    int MotionDevLeaveUpdateMode()
    {
        if (_motionState != MotionUpdating)
        {
            return MOTION_ERR_NOT_UPDATING;
        }

        _motionState = MotionReady;
        return 0;
    }

    /// <summary>NID_D7DA3DA7 (SceMotionDevForDriver)(): deletes the update event flag; 0.</summary>
    [Hle("NID_D7DA3DA7", Nid = 0xD7DA3DA7, Library = "SceMotionDevForDriver")]
    int MotionDevEndUpdate()
    {
        return 0;
    }

    /// <summary>NID_09918DAE (SceMotionDevForDriver)(void *a, const char *image, uint size, int) (SceSblUpdateMgr):
    /// writes sensor firmware; NULL checks, the update mode, then success (no firmware to write).</summary>
    [Hle("NID_09918DAE", Nid = 0x09918DAE, Library = "SceMotionDevForDriver", Default = true)]
    int MotionDevWriteFirmware(uint context, uint image, uint size, int flags)
    {
        if (context == 0 || image == 0 || size == 0)
        {
            return MOTION_ERR_NULL;
        }

        return _motionState != MotionUpdating ? MOTION_ERR_NOT_UPDATING : 0;
    }

    /// <summary>NID_DB89D1BF (SceMotionDevForDriver)(void *a, void *b, void *c, int): a calibration write while ready
    /// (state 2, else 0x80360003); NULL checks; success (no sensor memory).</summary>
    [Hle("NID_DB89D1BF", Nid = 0xDB89D1BF, Library = "SceMotionDevForDriver", Default = true)]
    int MotionDevWriteCalibration(uint first, uint second, uint third, int flags)
    {
        if (first == 0 || second == 0 || third == 0)
        {
            return MOTION_ERR_NULL;
        }

        return _motionState != MotionReady ? MOTION_ERR_NOT_READY : 0;
    }
}
