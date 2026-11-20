using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// SceCameraForDriver, the camera driver's kernel entries (FW 3.74 SceCamera, decompiled, study only): the sensor and
/// image processor controls of each camera (0 front, 1 back: any other 0x802E0000), the register access of the sensor
/// (registers 0x0D and 0x19 are the ones the driver lets through) and the active state the application manager reads.
/// They talk to the sensors over I2C and the ISP; VitaSharp has no sensor behind them (libcamera's frames are the
/// host's camera or neutral grey, made above the driver), so the driver is the firmware's without a sensor: its
/// device state never initialised, every sensor call after its argument checks answers 0x802E0002, no camera is
/// active, and the settings that do not reach a sensor (the configuration words, the owner the application manager
/// sets) are kept.
/// </summary>
public sealed partial class Camera
{
    const int ERR_NOT_INITIALISED = unchecked((int)0x802E0002);

    /// <summary>The words the driver keeps without a sensor: NID_22D51107's, NID_D259707C's selection and value,
    /// the owner process of NID_E81EADE1.</summary>
    [SnapshotMissing] // states of builds before it: none had been set
    uint _driverWord, _driverConfigSelection, _driverConfigValue;

    [SnapshotMissing]
    int _driverOwnerPid;

    /// <summary>Per camera: the word NID_DFBBA264 reads (0 until the sensor sets it).</summary>
    [SnapshotMissing]
    readonly uint[] _driverCameraWords = new uint[2];

    static bool IsCamera(uint camera) => (camera & ~1u) == 0;

    /// <summary>The sensor calls: a camera index (else 0x802E0000), then the uninitialised sensor.</summary>
    static int SensorCall(uint camera) => IsCamera(camera) ? ERR_NOT_INITIALISED : ERR_PARAM;

    /// <summary>NID_0E9B4B9E(camera): powers the camera's sensor on.</summary>
    [Hle(Nid = 0x0E9B4B9E, Library = "SceCameraForDriver")]
    int NID_0E9B4B9E(uint camera) => SensorCall(camera);

    /// <summary>NID_5338BE86(camera): powers the camera's sensor off.</summary>
    [Hle(Nid = 0x5338BE86, Library = "SceCameraForDriver")]
    int NID_5338BE86(uint camera) => SensorCall(camera);

    /// <summary>NID_6443EEDB(camera): resets the sensor (its power line through the GPIO).</summary>
    [Hle(Nid = 0x6443EEDB, Library = "SceCameraForDriver")]
    int NID_6443EEDB(uint camera) => SensorCall(camera);

    /// <summary>NID_8C3354E5(camera): stops the camera's capture.</summary>
    [Hle(Nid = 0x8C3354E5, Library = "SceCameraForDriver")]
    int NID_8C3354E5(uint camera) => SensorCall(camera);

    /// <summary>NID_22E70D83(camera, a, b): whether the camera is streaming (1) or not (0).</summary>
    [Hle(Nid = 0x22E70D83, Library = "SceCameraForDriver")]
    int NID_22E70D83(uint camera, uint first, uint second) => SensorCall(camera);

    /// <summary>NID_2D12FDF3(camera, frame): reads a frame of the sensor.</summary>
    [Hle(Nid = 0x2D12FDF3, Library = "SceCameraForDriver")]
    int NID_2D12FDF3(uint camera, uint frame) => SensorCall(camera);

    /// <summary>NID_3590BB9B(camera, value): an image processor setting (its +0xC4 operation).</summary>
    [Hle(Nid = 0x3590BB9B, Library = "SceCameraForDriver")]
    int NID_3590BB9B(uint camera, uint value) => SensorCall(camera);

    /// <summary>NID_36C164BE(camera, a, b): an image processor setting (its +0xC0 operation).</summary>
    [Hle(Nid = 0x36C164BE, Library = "SceCameraForDriver")]
    int NID_36C164BE(uint camera, uint first, uint second) => SensorCall(camera);

    /// <summary>NID_93477709(camera, a, b): an image processor setting (its +0xB8 operation).</summary>
    [Hle(Nid = 0x93477709, Library = "SceCameraForDriver")]
    int NID_93477709(uint camera, uint first, uint second) => SensorCall(camera);

    /// <summary>NID_B241F11F(camera, a, b): an image processor setting (its +0xBC operation).</summary>
    [Hle(Nid = 0xB241F11F, Library = "SceCameraForDriver")]
    int NID_B241F11F(uint camera, uint first, uint second) => SensorCall(camera);

    /// <summary>NID_96C63737(camera, 2, mode): the sensor's mode (0, 3 or 4) of interface 2; anything else
    /// 0x802E0000.</summary>
    [Hle(Nid = 0x96C63737, Library = "SceCameraForDriver")]
    int NID_96C63737(uint camera, uint interfaceIndex, uint mode)
    {
        return IsCamera(camera) && interfaceIndex == 2 && mode is 0 or 3 or 4 ? ERR_NOT_INITIALISED : ERR_PARAM;
    }

    /// <summary>NID_D0810455(camera, 2, out): the sensor's 8-byte identification of interface 2 (cleared first);
    /// anything else 0x802E0000.</summary>
    [Hle(Nid = 0xD0810455, Library = "SceCameraForDriver")]
    int NID_D0810455(uint camera, uint interfaceIndex, uint identification)
    {
        if (!IsCamera(camera))
        {
            return ERR_PARAM;
        }

        GuestMemory.Fill(identification, 0, 8);
        return interfaceIndex == 2 ? ERR_NOT_INITIALISED : ERR_PARAM;
    }

    /// <summary>NID_B312CE90(camera, register, value, ?): writes sensor register 0x0D or 0x19 (others
    /// 0x802E0000).</summary>
    [Hle(Nid = 0xB312CE90, Library = "SceCameraForDriver")]
    int NID_B312CE90(uint camera, uint register, uint value, uint extra) => register is 0x0D or 0x19 ? ERR_NOT_INITIALISED : ERR_PARAM;

    /// <summary>NID_D56A12BD(camera, register, value, ?): writes another sensor register (0x19, and 0x0D with 0xF000,
    /// are refused: 0x802E0000).</summary>
    [Hle(Nid = 0xD56A12BD, Library = "SceCameraForDriver")]
    int NID_D56A12BD(uint camera, uint register, uint value, uint extra)
    {
        return register == 0x19 || (register == 0x0D && value == 0xF000) ? ERR_PARAM : ERR_NOT_INITIALISED;
    }

    /// <summary>NID_C2E71BE8(camera, register, out, ?): reads a sensor register (0x19, another camera or a NULL output
    /// 0x802E0000).</summary>
    [Hle(Nid = 0xC2E71BE8, Library = "SceCameraForDriver")]
    int NID_C2E71BE8(uint camera, uint register, uint output, uint extra)
    {
        return register == 0x19 || !IsCamera(camera) || output == 0 ? ERR_PARAM : ERR_NOT_INITIALISED;
    }

    /// <summary>NID_FE3C170C(camera, register, out, ?): reads sensor register 0x0D or 0x19 (others, another camera or
    /// a NULL output 0x802E0000).</summary>
    [Hle(Nid = 0xFE3C170C, Library = "SceCameraForDriver")]
    int NID_FE3C170C(uint camera, uint register, uint output, uint extra)
    {
        return register is 0x0D or 0x19 && IsCamera(camera) && output != 0 ? ERR_NOT_INITIALISED : ERR_PARAM;
    }

    /// <summary>NID_E487870A(camera, info): opens the sensor with an open request (0x28..0x30 bytes, else
    /// 0x802E0000).</summary>
    [Hle(Nid = 0xE487870A, Library = "SceCameraForDriver")]
    int NID_E487870A(uint camera, uint info)
    {
        if (!IsCamera(camera) || info == 0 || GuestMemory.R32(info) - 0x28 >= 9)
        {
            return ERR_PARAM;
        }

        return ERR_NOT_INITIALISED;
    }

    /// <summary>NID_EE3D4B1C(camera, out): the camera's three state words (a NULL output 0x802E0000).</summary>
    [Hle(Nid = 0xEE3D4B1C, Library = "SceCameraForDriver")]
    int NID_EE3D4B1C(uint camera, uint output) => IsCamera(camera) && output != 0 ? ERR_NOT_INITIALISED : ERR_PARAM;

    /// <summary>NID_22D51107(value): sets a driver word.</summary>
    [Hle(Nid = 0x22D51107, Library = "SceCameraForDriver")]
    void NID_22D51107(uint value) => _driverWord = value;

    /// <summary>NID_D259707C(value, selection): the driver's configuration (selection 0 or 1, else 0x802E0000); 0.</summary>
    [Hle(Nid = 0xD259707C, Library = "SceCameraForDriver")]
    int NID_D259707C(uint value, uint selection)
    {
        if (selection >= 2)
        {
            return ERR_PARAM;
        }

        _driverConfigValue = value;
        _driverConfigSelection = selection;
        return 0;
    }

    /// <summary>NID_DFBBA264(camera, out): the camera's word (0 without a sensor); another camera 0x802E0000.</summary>
    [Hle(Nid = 0xDFBBA264, Library = "SceCameraForDriver")]
    int NID_DFBBA264(uint camera, uint output)
    {
        if (!IsCamera(camera))
        {
            return ERR_PARAM;
        }

        GuestMemory.W32(output, _driverCameraWords[camera]);
        return 0;
    }

    /// <summary>NID_E81EADE1(pid): the process the application manager gives the camera to (a negative value is
    /// handed back); 0.</summary>
    [Hle(Nid = 0xE81EADE1, Library = "SceCameraForDriver")]
    int NID_E81EADE1(int pid)
    {
        if (pid < 0)
        {
            return pid;
        }

        _driverOwnerPid = pid;
        return 0;
    }

    /// <summary>NID_45EB69C9(): the driver's suspend step (nothing to stop without a sensor).</summary>
    [Hle(Nid = 0x45EB69C9, Library = "SceCameraForDriver")]
    int NID_45EB69C9() => 0;

    /// <summary>NID_F7A0C99F(): the driver's resume step.</summary>
    [Hle(Nid = 0xF7A0C99F, Library = "SceCameraForDriver")]
    int NID_F7A0C99F() => 0;

    /// <summary>ksceCameraIsActive (0xEB1CC2CA)(pid, uint *active): the cameras the process has streaming (bit 0 front,
    /// bit 1 back): none without a sensor (0, *active 0); a NULL output 0x802E0000.</summary>
    [Hle(Nid = 0xEB1CC2CA, Library = "SceCameraForDriver")]
    int ksceCameraIsActive(int pid, uint activeOut)
    {
        if (activeOut == 0)
        {
            return ERR_PARAM;
        }

        GuestMemory.W32(activeOut, 0);
        return 0;
    }
}
