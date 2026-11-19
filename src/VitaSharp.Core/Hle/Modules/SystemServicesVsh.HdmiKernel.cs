using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

// SceHdmiForDriver (os0:kd/hdmi.skprx 3.74, read in the module, study only): the HDMI transmitter (an I2C chip) of the
// PS TV and the development kits, and its CEC link. Its settings answer 0x80370001 for a bad argument; the chip's
// settings answer 0x8037000B when the chip does not take them; a video mode outside the module's table, or one the
// chip refuses, 0x80370004; the audio mode needs a video mode first (0x80370006); the monitor information is 0x80370008
// until a monitor's EDID was read (the buffer zeroed); the raw EDID 0x80370002 before any read. The CEC calls answer
// 0x8037000E on a unit that is not a PS TV, and 0x8037000F while CEC is not running. The module keeps six callback
// slots, the CEC statistics block (0x20 bytes, reset with the time of the reset), the time the CEC was last active
// (a one-touch play within 8 s of it is refused while CEC is off) and a few plain settings. The emulated unit is a
// PCH-1000 handheld: there is no transmitter to answer and no monitor; every setting the module keeps is kept.
public sealed partial class SystemServicesVsh
{
    /// <summary>SceHdmi: a bad argument (0x80370001).</summary>
    const int HDMI_ERR_INVALID_ARGUMENT = unchecked((int)0x80370001);

    /// <summary>SceHdmi: no EDID read yet (0x80370002).</summary>
    const int HDMI_ERR_NO_EDID = unchecked((int)0x80370002);

    /// <summary>SceHdmi: a video mode outside the table or refused by the chip (0x80370004).</summary>
    const int HDMI_ERR_VIDEO_MODE = unchecked((int)0x80370004);

    /// <summary>SceHdmi: no video mode set (0x80370006).</summary>
    const int HDMI_ERR_NO_VIDEO_MODE = unchecked((int)0x80370006);

    /// <summary>SceHdmi: no monitor information (0x80370008).</summary>
    const int HDMI_ERR_NO_MONITOR = unchecked((int)0x80370008);

    /// <summary>SceHdmi: the transmitter did not take the setting (0x8037000B).</summary>
    const int HDMI_ERR_CHIP = unchecked((int)0x8037000B);

    /// <summary>SceHdmi: CEC is not available on this unit (0x8037000E).</summary>
    const int HDMI_ERR_CEC_UNSUPPORTED = unchecked((int)0x8037000E);

    /// <summary>SceHdmi: CEC is not running (0x8037000F).</summary>
    const int HDMI_ERR_CEC_OFF = unchecked((int)0x8037000F);

    const int HdmiCallbackSlots = 6, HdmiCecStatsWords = 8, HdmiMonitorInfoSize = 0xF4, HdmiEdidMinimum = 5;

    /// <summary>The window after the CEC was last active in which a one-touch play is checked (8 s, in microseconds).</summary>
    const ulong HdmiCecActiveWindowUs = 8_000_000;

    /// <summary>The video mode codes of the module's table (the first word of the mode block).</summary>
    static ReadOnlySpan<ushort> HdmiVideoModeCodes =>
    [
        0x8100, 0x8170, 0x8280, 0x82F0, 0x8300, 0x8370, 0x8480, 0x84F0, 0x8500, 0x8580, 0x8600, 0x8680, 0x8710,
        0x8730, 0x8790, 0x8B00, 0x8C00, 0x8D00, 0x8E00, 0x8F00,
    ];

    [SnapshotMissing] // states of builds before it: no callback, no video mode, nothing kept
    readonly uint[] _hdmiCallbacks = new uint[HdmiCallbackSlots];

    [SnapshotMissing]
    ushort _hdmiVideoMode;

    [SnapshotMissing]
    readonly uint[] _hdmiCecStats = new uint[HdmiCecStatsWords];

    [SnapshotMissing]
    ulong _hdmiCecLastActiveUs;

    [SnapshotMissing]
    bool _hdmiPoweredOn;

    [SnapshotMissing]
    uint _hdmiOutputSetting;

    [SnapshotMissing]
    uint _hdmiDisplaySetting;

    [SnapshotMissing]
    uint _hdmiMode;

    [SnapshotMissing]
    uint _hdmiControllerSetting;

    /// <summary>The callbacks, the video mode code and the plain settings, for the tests.</summary>
    internal (uint[] Callbacks, ushort VideoMode, uint Output, uint Display, uint Mode, uint Controller) HdmiKernelValues =>
        (_hdmiCallbacks, _hdmiVideoMode, _hdmiOutputSetting, _hdmiDisplaySetting, _hdmiMode, _hdmiControllerSetting);

    /// <summary>ksceHdmiGetMonitorInfo(void *info): NULL 0x80370001; no monitor's EDID: the 0xF4 bytes zeroed,
    /// 0x80370008.</summary>
    [Hle(Nid = 0x8EB0FD9C, Library = "SceHdmiForDriver", Default = true)]
    int ksceHdmiGetMonitorInfo(uint info)
    {
        if (info == 0)
        {
            return HDMI_ERR_INVALID_ARGUMENT;
        }

        GuestMemory.Fill(info, 0, HdmiMonitorInfoSize);
        return HDMI_ERR_NO_MONITOR;
    }

    /// <summary>ksceHdmiSetVideoMode(const u16 *mode): NULL 0x80370004; the code is kept (one outside the table as no
    /// mode) and sent to the chip, which does not take it (0x80370004).</summary>
    [Hle(Nid = 0x5A212BB3, Library = "SceHdmiForDriver", Default = true)]
    int ksceHdmiSetVideoMode(uint mode)
    {
        if (mode == 0)
        {
            return HDMI_ERR_VIDEO_MODE;
        }

        ushort code = GuestMemory.Read<ushort>(mode);
        _hdmiVideoMode = HdmiVideoModeCodes.IndexOf(code) >= 0 ? code : (ushort)0;
        return HDMI_ERR_VIDEO_MODE;
    }

    /// <summary>ksceHdmiSetAvMute(int on): a chip setting; the chip does not take it (0x8037000B).</summary>
    [Hle(Nid = 0xE66D5A4D, Library = "SceHdmiForDriver", Default = true)]
    int ksceHdmiSetAvMute(uint on)
    {
        return HDMI_ERR_CHIP;
    }

    /// <summary>ksceHdmiSetOutputEnable(int on): a chip setting; the chip does not take it (0x8037000B).</summary>
    [Hle(Nid = 0x99A3CABE, Library = "SceHdmiForDriver", Default = true)]
    int ksceHdmiSetOutputEnable(uint on)
    {
        return HDMI_ERR_CHIP;
    }

    /// <summary>ksceHdmiSetAudioMode(const u16 *mode): no video mode 0x80370006; a bad source (word 0 not 0 or 1)
    /// 0x80370001; else the chip does not take it (2, the module's raw chip failure).</summary>
    [Hle(Nid = 0x854EEB78, Library = "SceHdmiForDriver", Default = true)]
    int ksceHdmiSetAudioMode(uint mode)
    {
        if (_hdmiVideoMode == 0)
        {
            return HDMI_ERR_NO_VIDEO_MODE;
        }

        if (mode == 0 || GuestMemory.Read<ushort>(mode) > 1)
        {
            return HDMI_ERR_INVALID_ARGUMENT;
        }

        return 2;
    }

    /// <summary>ksceHdmiSetBlankScreen(int on): a chip setting; the chip does not take it (0x8037000B).</summary>
    [Hle(Nid = 0x08AFFB03, Library = "SceHdmiForDriver", Default = true)]
    int ksceHdmiSetBlankScreen(uint on)
    {
        return HDMI_ERR_CHIP;
    }

    /// <summary>ksceHdmiRegisterCallback(int slot, void *callback): slot 0..5 and a callback (else 0x80370001); 0.</summary>
    [Hle(Nid = 0x0F12364A, Library = "SceHdmiForDriver")]
    int ksceHdmiRegisterCallback(uint slot, uint callback)
    {
        if (slot >= HdmiCallbackSlots || callback == 0)
        {
            return HDMI_ERR_INVALID_ARGUMENT;
        }

        _hdmiCallbacks[slot] = callback;
        return 0;
    }

    /// <summary>ksceHdmiUnregisterCallback(int slot): slot 0..5 (else 0x80370001); the module clears slot 0 whatever
    /// the slot (as its code does); 0.</summary>
    [Hle(Nid = 0xE331EC90, Library = "SceHdmiForDriver")]
    int ksceHdmiUnregisterCallback(uint slot)
    {
        if (slot >= HdmiCallbackSlots)
        {
            return HDMI_ERR_INVALID_ARGUMENT;
        }

        _hdmiCallbacks[0] = 0;
        return 0;
    }

    /// <summary>ksceHdmiSetTmdsOutput(int on): resets the chip's output stage; 0.</summary>
    [Hle(Nid = 0x0686FBD6, Library = "SceHdmiForDriver", Default = true)]
    int ksceHdmiSetTmdsOutput(uint on)
    {
        return 0;
    }

    /// <summary>ksceHdmiSetHdcp(int on): resets the chip's HDCP stage; 0.</summary>
    [Hle(Nid = 0xF20529CC, Library = "SceHdmiForDriver", Default = true)]
    int ksceHdmiSetHdcp(uint on)
    {
        return 0;
    }

    /// <summary>ksceHdmiResetTmdsOutput(int on): the output stage reset of 0x0686FBD6; 0.</summary>
    [Hle(Nid = 0xFAB3A2E9, Library = "SceHdmiForDriver", Default = true)]
    int ksceHdmiResetTmdsOutput(uint on)
    {
        return 0;
    }

    /// <summary>ksceHdmiGetEdid(void *buffer, uint size, u8 *length): NULL buffer or length, or a size under 5,
    /// 0x80370001; no EDID read yet 0x80370002.</summary>
    [Hle(Nid = 0x7AD0F6C9, Library = "SceHdmiForDriver", Default = true)]
    int ksceHdmiGetEdid(uint buffer, uint size, uint length)
    {
        if (buffer == 0 || length == 0 || size < HdmiEdidMinimum)
        {
            return HDMI_ERR_INVALID_ARGUMENT;
        }

        return HDMI_ERR_NO_EDID;
    }

    /// <summary>ksceHdmiGetSinkPhysicalAddress(): reads the sink's address from the chip; no answer (0x8037000B).</summary>
    [Hle(Nid = 0xFF5546D8, Library = "SceHdmiForDriver", Default = true)]
    int ksceHdmiGetSinkPhysicalAddress()
    {
        return HDMI_ERR_CHIP;
    }

    /// <summary>ksceHdmiPowerOn(): the first call asks the syscon to power the transmitter (accepted, 0); later ones 0.</summary>
    [Hle(Nid = 0xB04F5B6A, Library = "SceHdmiForDriver", Default = true)]
    int ksceHdmiPowerOn()
    {
        _hdmiPoweredOn = true;
        return 0;
    }

    /// <summary>ksceHdmiSetOutputSetting(int kind, uint value): kind 1 the output setting, kind 2 the display setting
    /// (other kinds nothing); 0.</summary>
    [Hle(Nid = 0x3126A070, Library = "SceHdmiForDriver")]
    int ksceHdmiSetOutputSetting(uint kind, uint value)
    {
        if (kind == 1)
        {
            _hdmiOutputSetting = value;
        }
        else if (kind == 2)
        {
            _hdmiDisplaySetting = value;
        }

        return 0;
    }

    /// <summary>ksceHdmiSetMode(int mode): 0 or 1 kept (others ignored); 0.</summary>
    [Hle(Nid = 0x47894820, Library = "SceHdmiForDriver")]
    int ksceHdmiSetMode(uint mode)
    {
        if (mode < 2)
        {
            _hdmiMode = mode;
        }

        return 0;
    }

    /// <summary>ksceHdmiCecEnable(): not a PS TV (0x8037000E).</summary>
    [Hle(Nid = 0xFA30ACFC, Library = "SceHdmiForDriver")]
    int ksceHdmiCecEnable()
    {
        return HDMI_ERR_CEC_UNSUPPORTED;
    }

    /// <summary>ksceHdmiCecDisable(): not a PS TV (0x8037000E).</summary>
    [Hle(Nid = 0x291F000B, Library = "SceHdmiForDriver")]
    int ksceHdmiCecDisable()
    {
        return HDMI_ERR_CEC_UNSUPPORTED;
    }

    /// <summary>ksceHdmiCecWakeup(): not a PS TV (0x8037000E).</summary>
    [Hle(Nid = 0xD929E4E5, Library = "SceHdmiForDriver")]
    int ksceHdmiCecWakeup()
    {
        return HDMI_ERR_CEC_UNSUPPORTED;
    }

    /// <summary>ksceHdmiSetControllerSetting(uint value): kept; 0.</summary>
    [Hle(Nid = 0x05174BC2, Library = "SceHdmiForDriver")]
    int ksceHdmiSetControllerSetting(uint value)
    {
        _hdmiControllerSetting = value;
        return 0;
    }

    /// <summary>ksceHdmiCecOneTouchPlay(int kind): kind 1 and 2 need CEC running (0x8037000F); kind 0 is 0 more than
    /// 8 s after the CEC was last active, else 0x8037000F; other kinds 0x80370001.</summary>
    [Hle(Nid = 0x4C31F5E8, Library = "SceHdmiForDriver")]
    int ksceHdmiCecOneTouchPlay(uint kind)
    {
        if (kind == 0)
        {
            ulong sinceActive = Emu.Kernel.ProcessTimeUs - _hdmiCecLastActiveUs;
            return sinceActive > HdmiCecActiveWindowUs ? 0 : HDMI_ERR_CEC_OFF;
        }

        if (kind <= 2)
        {
            return HDMI_ERR_CEC_OFF;
        }

        return HDMI_ERR_INVALID_ARGUMENT;
    }

    /// <summary>ksceHdmiCecSetActive(int active): nonzero records the time the CEC was last active; 0.</summary>
    [Hle(Nid = 0xEA486A37, Library = "SceHdmiForDriver")]
    int ksceHdmiCecSetActive(uint active)
    {
        if (active != 0)
        {
            _hdmiCecLastActiveUs = Emu.Kernel.ProcessTimeUs;
        }

        return 0;
    }

    /// <summary>ksceHdmiCecGetStats(uint *stats): NULL or a first word above 0x20 0x80370001; else the eight words of
    /// the statistics block; 0.</summary>
    [Hle(Nid = 0x93AA48AC, Library = "SceHdmiForDriver")]
    int ksceHdmiCecGetStats(uint stats)
    {
        if (stats == 0 || GuestMemory.R32(stats) > 0x20)
        {
            return HDMI_ERR_INVALID_ARGUMENT;
        }

        for (int word = 0; word < HdmiCecStatsWords; word++)
        {
            GuestMemory.W32(stats + (uint)(word * 4), _hdmiCecStats[word]);
        }

        return 0;
    }

    /// <summary>ksceHdmiCecResetStats(): the statistics block: size 0x20, counters 0, the reset time; 0.</summary>
    [Hle(Nid = 0x47B753EB, Library = "SceHdmiForDriver")]
    int ksceHdmiCecResetStats()
    {
        Array.Clear(_hdmiCecStats);
        _hdmiCecStats[0] = 0x20;
        ulong now = Emu.Kernel.ProcessTimeUs;
        _hdmiCecStats[2] = (uint)now;
        _hdmiCecStats[3] = (uint)(now >> 32);
        return 0;
    }

    /// <summary>ksceHdmiCecGetPowerStatus(): CEC not running (-1).</summary>
    [Hle(Nid = 0x6D2D10D8, Library = "SceHdmiForDriver")]
    int ksceHdmiCecGetPowerStatus()
    {
        return -1;
    }
}
