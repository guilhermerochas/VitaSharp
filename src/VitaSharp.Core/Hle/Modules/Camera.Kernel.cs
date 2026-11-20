using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// SceCameraForDriver, the camera driver's kernel entries (FW 3.74 SceCamera, decompiled, study only), with the
/// PCH-1000's two sensors present (0 front, 1 back; any other 0x802E0000). The driver's per-process camera state is
/// libcamera's (<see cref="Device"/>: open, started, resolution, format, the frame source), so kernel code and user
/// code see one camera: the open request (0x28..0x30 bytes) is sceCameraOpen of the calling process, a frame read is
/// its sceCameraRead, the streaming and active queries read the processes' started cameras, and the image processor
/// settings need the camera streaming in some process (0x802E000E otherwise, the driver's "not active"). The
/// sensor's registers have no source behind them: the driver lets 0x0D and 0x19 through its first write entry and
/// every other one but 0x19 (and 0x0D with 0xF000) through the second; they are kept per camera and read back as
/// written (0 until written, the sensor's power-on value VitaSharp has no source for).
/// </summary>
public sealed partial class Camera
{
    const int ERR_NOT_ACTIVE = unchecked((int)0x802E000E);

    /// <summary>The words the driver keeps: NID_22D51107's, NID_D259707C's selection and value, the owner process of
    /// NID_E81EADE1.</summary>
    [SnapshotMissing] // states of builds before it: none had been set
    uint _driverWord, _driverConfigSelection, _driverConfigValue;

    [SnapshotMissing]
    int _driverOwnerPid;

    /// <summary>Per camera: the word NID_DFBBA264 reads (0 until the sensor sets it).</summary>
    [SnapshotMissing]
    readonly uint[] _driverCameraWords = new uint[2];

    /// <summary>The sensor registers written through the driver: camera * 0x10000 + register -> value.</summary>
    [SnapshotMissing]
    readonly Dictionary<uint, uint> _sensorRegisters = new();

    /// <summary>The image processor settings (by camera and operation offset): key -> the first value.</summary>
    [SnapshotMissing]
    readonly Dictionary<uint, uint> _ispSettings = new();

    static bool IsCamera(uint camera) => (camera & ~1u) == 0;

    /// <summary>The camera module of process <paramref name="pid"/> (0: the calling thread's process).</summary>
    Camera? CameraOf(int pid)
    {
        if (Emu is null)
        {
            return this;
        }

        Emulator? process = pid == 0 && GuestThread.Current is { } thread ? thread.Process : KernelSpace.ProcessOf(Emu, pid);
        return process?.Hle.Modules.OfType<Camera>().FirstOrDefault();
    }

    /// <summary>The camera modules of every process.</summary>
    IEnumerable<Camera> EveryCamera()
    {
        if (Emu?.Vita is not { } vita)
        {
            yield return this;
            yield break;
        }

        foreach (Emulator process in vita.ProcessesAndKernel)
        {
            if (process.Hle.Modules.OfType<Camera>().FirstOrDefault() is { } module)
            {
                yield return module;
            }
        }
    }

    bool IsOpen(int device)
    {
        lock (_lock)
        {
            return _dev[device].Open;
        }
    }

    bool IsStarted(int device)
    {
        lock (_lock)
        {
            return _dev[device].Started;
        }
    }

    /// <summary>True when some process has the camera streaming.</summary>
    bool StreamingAnywhere(uint camera) => EveryCamera().Any(module => module.IsStarted((int)camera));

    /// <summary>A sensor power or control step of the calling process's open camera: 0, its camera not open
    /// 0x802E0004.</summary>
    int CallerCameraStep(uint camera)
    {
        if (!IsCamera(camera))
        {
            return ERR_PARAM;
        }

        return CameraOf(0)?.IsOpen((int)camera) == true ? 0 : ERR_NOT_OPEN;
    }

    /// <summary>NID_0E9B4B9E(camera): powers the sensor on for the caller's open camera.</summary>
    [Hle(Nid = 0x0E9B4B9E, Library = "SceCameraForDriver")]
    int NID_0E9B4B9E(uint camera) => CallerCameraStep(camera);

    /// <summary>NID_5338BE86(camera): powers the sensor off for the caller's open camera.</summary>
    [Hle(Nid = 0x5338BE86, Library = "SceCameraForDriver")]
    int NID_5338BE86(uint camera) => CallerCameraStep(camera);

    /// <summary>NID_8C3354E5(camera): the caller's capture stop step (another camera, or one not open, 0x802E0000).</summary>
    [Hle(Nid = 0x8C3354E5, Library = "SceCameraForDriver")]
    int NID_8C3354E5(uint camera) => CallerCameraStep(camera) == 0 ? 0 : ERR_PARAM;

    /// <summary>NID_6443EEDB(camera): resets the sensor (its power line through the GPIO); 0.</summary>
    [Hle(Nid = 0x6443EEDB, Library = "SceCameraForDriver")]
    int NID_6443EEDB(uint camera) => IsCamera(camera) ? 0 : ERR_PARAM;

    /// <summary>NID_22E70D83(camera, ?, ?): 1 while some process has the camera streaming, else 0.</summary>
    [Hle(Nid = 0x22E70D83, Library = "SceCameraForDriver")]
    int NID_22E70D83(uint camera, uint first, uint second)
    {
        if (!IsCamera(camera))
        {
            return ERR_PARAM;
        }

        return StreamingAnywhere(camera) ? 1 : 0;
    }

    /// <summary>NID_E487870A(camera, info): opens the camera for the calling process (sceCameraOpen; the request 0x28..0x30
    /// bytes, else 0x802E0000).</summary>
    [Hle(Nid = 0xE487870A, Library = "SceCameraForDriver")]
    int NID_E487870A(uint camera, uint info)
    {
        if (!IsCamera(camera) || info == 0 || GuestMemory.R32(info) - 0x28 >= 9)
        {
            return ERR_PARAM;
        }

        return CameraOf(0)?.sceCameraOpen((int)camera, info) ?? ERR_PARAM;
    }

    /// <summary>NID_2D12FDF3(camera, read): a frame of the calling process's camera (sceCameraRead).</summary>
    [Hle(Nid = 0x2D12FDF3, Library = "SceCameraForDriver")]
    int NID_2D12FDF3(uint camera, uint read)
    {
        if (!IsCamera(camera))
        {
            return ERR_PARAM;
        }

        return CameraOf(0)?.sceCameraRead((int)camera, read) ?? ERR_PARAM;
    }

    /// <summary>An image processor setting (the sensor's operation at <paramref name="operation"/>): the camera must be
    /// streaming somewhere (else 0x802E000E); kept; 0.</summary>
    int IspSetting(uint camera, uint operation, uint value)
    {
        if (!IsCamera(camera))
        {
            return ERR_PARAM;
        }

        if (!StreamingAnywhere(camera))
        {
            return ERR_NOT_ACTIVE;
        }

        lock (_ispSettings)
        {
            _ispSettings[camera * 0x100 + operation] = value;
        }

        return 0;
    }

    /// <summary>NID_3590BB9B(camera, value): the image processor's +0xC4 setting.</summary>
    [Hle(Nid = 0x3590BB9B, Library = "SceCameraForDriver")]
    int NID_3590BB9B(uint camera, uint value) => IspSetting(camera, 0xC4, value);

    /// <summary>NID_36C164BE(camera, a, b): the image processor's +0xC0 setting.</summary>
    [Hle(Nid = 0x36C164BE, Library = "SceCameraForDriver")]
    int NID_36C164BE(uint camera, uint first, uint second) => IspSetting(camera, 0xC0, first);

    /// <summary>NID_93477709(camera, a, b): the image processor's +0xB8 setting.</summary>
    [Hle(Nid = 0x93477709, Library = "SceCameraForDriver")]
    int NID_93477709(uint camera, uint first, uint second) => IspSetting(camera, 0xB8, first);

    /// <summary>NID_B241F11F(camera, a, b): the image processor's +0xBC setting.</summary>
    [Hle(Nid = 0xB241F11F, Library = "SceCameraForDriver")]
    int NID_B241F11F(uint camera, uint first, uint second) => IspSetting(camera, 0xBC, first);

    /// <summary>NID_96C63737(camera, 2, mode): the sensor's mode (0, 3 or 4) of interface 2; anything else
    /// 0x802E0000.</summary>
    [Hle(Nid = 0x96C63737, Library = "SceCameraForDriver")]
    int NID_96C63737(uint camera, uint interfaceIndex, uint mode)
    {
        return IsCamera(camera) && interfaceIndex == 2 && mode is 0 or 3 or 4 ? 0 : ERR_PARAM;
    }

    /// <summary>NID_D0810455(camera, 2, out): the sensor's 8-byte identification of interface 2 (cleared; the sensor's
    /// own bytes have no source); anything else 0x802E0000.</summary>
    [Hle(Nid = 0xD0810455, Library = "SceCameraForDriver")]
    int NID_D0810455(uint camera, uint interfaceIndex, uint identification)
    {
        if (!IsCamera(camera))
        {
            return ERR_PARAM;
        }

        GuestMemory.Fill(identification, 0, 8);
        return interfaceIndex == 2 ? 0 : ERR_PARAM;
    }

    void WriteSensorRegister(uint camera, uint register, uint value)
    {
        lock (_sensorRegisters)
        {
            _sensorRegisters[(camera & 1) * 0x10000 + (register & 0xFFFF)] = value;
        }
    }

    uint ReadSensorRegister(uint camera, uint register)
    {
        lock (_sensorRegisters)
        {
            return _sensorRegisters.GetValueOrDefault((camera & 1) * 0x10000 + (register & 0xFFFF));
        }
    }

    /// <summary>NID_B312CE90(camera, register, value, ?): writes sensor register 0x0D or 0x19 (others
    /// 0x802E0000); 0.</summary>
    [Hle(Nid = 0xB312CE90, Library = "SceCameraForDriver")]
    int NID_B312CE90(uint camera, uint register, uint value, uint extra)
    {
        if (register is not (0x0D or 0x19))
        {
            return ERR_PARAM;
        }

        WriteSensorRegister(camera, register, value);
        return 0;
    }

    /// <summary>NID_D56A12BD(camera, register, value, ?): writes another sensor register (0x19, and 0x0D with 0xF000,
    /// are refused: 0x802E0000); 0.</summary>
    [Hle(Nid = 0xD56A12BD, Library = "SceCameraForDriver")]
    int NID_D56A12BD(uint camera, uint register, uint value, uint extra)
    {
        if (register == 0x19 || (register == 0x0D && value == 0xF000))
        {
            return ERR_PARAM;
        }

        WriteSensorRegister(camera, register, value);
        return 0;
    }

    /// <summary>NID_C2E71BE8(camera, register, ushort *value, ?): reads a sensor register (0x19, another camera or a NULL
    /// output 0x802E0000); 0.</summary>
    [Hle(Nid = 0xC2E71BE8, Library = "SceCameraForDriver")]
    int NID_C2E71BE8(uint camera, uint register, uint output, uint extra)
    {
        if (register == 0x19 || !IsCamera(camera) || output == 0)
        {
            return ERR_PARAM;
        }

        GuestMemory.Write(output, (ushort)ReadSensorRegister(camera, register));
        return 0;
    }

    /// <summary>NID_FE3C170C(camera, register, ushort *value, ?): reads sensor register 0x0D or 0x19 (others, another
    /// camera or a NULL output 0x802E0000); 0.</summary>
    [Hle(Nid = 0xFE3C170C, Library = "SceCameraForDriver")]
    int NID_FE3C170C(uint camera, uint register, uint output, uint extra)
    {
        if (register is not (0x0D or 0x19) || !IsCamera(camera) || output == 0)
        {
            return ERR_PARAM;
        }

        GuestMemory.Write(output, (ushort)ReadSensorRegister(camera, register));
        return 0;
    }

    /// <summary>NID_EE3D4B1C(camera, out): the camera's three driver words (+0: open by some process, +4: streaming, +8:
    /// its frame width and height of the open request, 0 for none); a NULL output 0x802E0000.</summary>
    [Hle(Nid = 0xEE3D4B1C, Library = "SceCameraForDriver")]
    int NID_EE3D4B1C(uint camera, uint output)
    {
        if (!IsCamera(camera) || output == 0)
        {
            return ERR_PARAM;
        }

        bool open = false;
        bool started = false;
        uint size = 0;
        foreach (Camera module in EveryCamera())
        {
            lock (module._lock)
            {
                Device device = module._dev[camera];
                if (device.Open)
                {
                    open = true;
                    size = (uint)(device.Width | (device.Height << 16));
                }

                started |= device.Started;
            }
        }

        GuestMemory.W32(output, open ? 1u : 0u);
        GuestMemory.W32(output + 4, started ? 1u : 0u);
        GuestMemory.W32(output + 8, size);
        return 0;
    }

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

    /// <summary>NID_DFBBA264(camera, out): the camera's word (0 until the driver sets it); another camera 0x802E0000.</summary>
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

    /// <summary>NID_45EB69C9(): the driver's suspend step (the host streams stop with the cameras themselves).</summary>
    [Hle(Nid = 0x45EB69C9, Library = "SceCameraForDriver")]
    int NID_45EB69C9() => 0;

    /// <summary>NID_F7A0C99F(): the driver's resume step.</summary>
    [Hle(Nid = 0xF7A0C99F, Library = "SceCameraForDriver")]
    int NID_F7A0C99F() => 0;

    /// <summary>ksceCameraIsActive (0xEB1CC2CA)(pid, uint *active): the cameras process <paramref name="pid"/> has
    /// streaming (bit 0 front, bit 1 back); 1 when any, else 0; a NULL output 0x802E0000.</summary>
    [Hle(Nid = 0xEB1CC2CA, Library = "SceCameraForDriver")]
    int ksceCameraIsActive(int pid, uint activeOut)
    {
        if (activeOut == 0)
        {
            return ERR_PARAM;
        }

        uint active = 0;
        if (CameraOf(pid) is { } module)
        {
            active = (module.IsStarted(0) ? 1u : 0u) | (module.IsStarted(1) ? 2u : 0u);
        }

        GuestMemory.W32(activeOut, active);
        return active != 0 ? 1 : 0;
    }
}
