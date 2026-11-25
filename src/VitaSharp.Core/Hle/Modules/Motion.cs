using VitaSharp.Core.Hle.Motion;
using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// SceMotion fed by the host's <see cref="Host.IMotionSource"/> (controller gyro/accelerometer, phone sensors); at rest
/// without one. Fusion maths in <see cref="MotionFusion"/>, guest structures in <see cref="MotionDevice"/>. Also the
/// camera shutter sound. SceCamera is in Camera.cs.
/// </summary>
public sealed partial class Sensors : HleModule
{
    const int ERR_OUT_OF_BOUNDS = unchecked((int)0x80360205), ERR_NOT_SAMPLING = unchecked((int)0x80360206),
        ERR_ALREADY_SAMPLING = unchecked((int)0x80360207), ERR_NULL_PARAMETER = unchecked((int)0x80360209),
        ERR_ANGLE_OUT_OF_RANGE = unchecked((int)0x8036020A);

    bool _sampling, _magnetometer;
    readonly MotionDevice _device = new();

    internal bool Sampling => _sampling;
    internal MotionDevice Device => _device;
    // tilt correction, deadband and gyro bias correction default on (psp2/motion.h), angle threshold 20 (Vita3K)
    MotionFusion Fusion => _device.Fusion;

    long NowUs => (long)Emu.Kernel.ProcessTimeUs;
    void Poll() => _device.Poll(Emu.Host.Motion, NowUs);

    [Hle] int sceMotionStartSampling() { if (_sampling) return ERR_ALREADY_SAMPLING; _sampling = true; return 0; }
    [Hle] int sceMotionStopSampling() { if (!_sampling) return ERR_NOT_SAMPLING; _sampling = false; return 0; }
    /// <summary>The current attitude becomes the origin (tilt from gravity, heading zero).</summary>
    [Hle] int sceMotionReset() { _device.Reset(); return 0; }
    // driver_us 3.74: the switch goes to the sensor driver (SceMotionDev) and is kept for GetMagnetometerState. With it
    // on and the host source's magnetometer giving a stable field, sceMotionGetState reports the NED matrix and STABLE
    // (MotionDevice.WriteState); without one, UNSTABLE and the NED matrix = the rotation matrix, as before
    [Hle] int sceMotionMagnetometerOn() { _magnetometer = true; return 0; }
    [Hle] int sceMotionMagnetometerOff() { _magnetometer = false; return 0; }
    [Hle] int sceMotionGetMagnetometerState() => _magnetometer ? 1 : 0;
    /// <summary>The library keeps the value it was given (any non-zero turns the correction on) and gives it back.</summary>
    [Hle]
    int sceMotionSetTiltCorrection(int enable)
    {
        _tiltCorrectionSetting = enable;
        Fusion.TiltCorrection = enable != 0;
        return 0;
    }

    [Hle]
    int sceMotionGetTiltCorrection() => Setting(_tiltCorrectionSetting, Fusion.TiltCorrection);

    /// <summary>The library keeps the value it was given (any non-zero turns the deadband on) and gives it back.</summary>
    [Hle]
    int sceMotionSetDeadband(int enable)
    {
        _deadbandSetting = enable;
        Fusion.Deadband = enable != 0;
        return 0;
    }

    [Hle]
    int sceMotionGetDeadband() => Setting(_deadbandSetting, Fusion.Deadband);
    [Hle] int sceMotionSetGyroBiasCorrection(int e) { Fusion.GyroBiasCorrection = e != 0; return 0; }
    [Hle] int sceMotionGetGyroBiasCorrection() => Fusion.GyroBiasCorrection ? 1 : 0;

    /// <summary>
    /// Sets the three basic-orientation thresholds of the library to <paramref name="angle"/> degrees. driver_us 3.74
    /// keeps only 0 &lt; angle &lt;= 45 (two ordered compares: 0 itself, a negative angle and NaN are 0x8036020A too).
    /// </summary>
    [Hle]
    int sceMotionSetAngleThreshold(float angle)
    {
        if (!(angle > 0f && angle <= MaxAngleThreshold))
        {
            return ERR_ANGLE_OUT_OF_RANGE;
        }

        Fusion.AngleThresholdPlane = angle;
        Fusion.AngleThresholdCone = angle;
        Fusion.AngleThreshold = angle;
        return 0;
    }

    /// <summary>The third threshold (the one sceMotionSetAngleThreshold and NID_CA0A798B set last).</summary>
    [Hle]
    float sceMotionGetAngleThreshold() => Fusion.AngleThreshold;

    /// <summary>Turns the reference heading of the fused orientation about the vertical axis.</summary>
    [Hle] int sceMotionRotateYaw(float radians) { _device.RotateYaw(radians); return 0; }

    /// <summary>Device axis pointing away from gravity (at rest: screen up, +Z). As on hardware it reflects the
    /// last sceMotionGetState.</summary>
    [Hle]
    int sceMotionGetBasicOrientation(uint v)
    {
        if (!_sampling) return ERR_NOT_SAMPLING;
        if (v == 0) return ERR_NULL_PARAMETER;
        var b = _device.BasicOrientation;
        GuestMemory.Write(v, b.X);
        GuestMemory.Write(v + 4, b.Y);
        GuestMemory.Write(v + 8, b.Z);
        return 0;
    }

    /// <summary>SceMotionDeviceLocation (0x30): accelerometer and gyro positions in mm, OLED model (PCH-1000) values
    /// from psp2/motion.h.</summary>
    [Hle]
    int sceMotionGetDeviceLocation(uint loc)
    {
        if (loc == 0) return ERR_NULL_PARAMETER;
        GuestMemory.Fill(loc, 0, 0x30);
        float[] v = [-32, 12, -8, -33, 6, -8];
        for (int i = 0; i < v.Length; i++) GuestMemory.Write(loc + (uint)i * 4, v[i]);
        return 0;
    }

    /// <summary>
    /// Raw accelerometer/gyro records (SceMotionSensorState, 0x40 each), oldest first: the latest host samples, or at
    /// rest synthetic records whose counters and timestamps advance. 64 records must be accepted (Invizimals reads
    /// 64). driver_us 3.74 checks the sampling (0x80360206), then the count (1..64, 0x80360205), then the pointer.
    /// </summary>
    [Hle]
    int sceMotionGetSensorState(uint states, int numRecords)
    {
        if (!_sampling)
        {
            return ERR_NOT_SAMPLING;
        }

        if (numRecords is <= 0 or > MotionDevice.MaxRecords)
        {
            return ERR_OUT_OF_BOUNDS;
        }

        if (states == 0)
        {
            return ERR_NULL_PARAMETER;
        }

        Poll();
        _device.WriteSensorStates(states, numRecords, NowUs);
        return 0;
    }

    internal static void WriteSensorStates(uint states, int numRecords, long nowUs, uint counter)
        => MotionDevice.WriteRestSensorStates(states, numRecords, nowUs, counter);

    /// <summary>
    /// SceMotionState (0xF8) of the fused sensors. As driver_us 3.74 does, only while the program's sampling is on
    /// (sceMotionStartSampling; 0x80360206 before), and a NULL state is 0x80360209.
    /// </summary>
    [Hle]
    int sceMotionGetState(uint state)
    {
        if (!_sampling)
        {
            return ERR_NOT_SAMPLING;
        }

        if (state == 0)
        {
            return ERR_NULL_PARAMETER;
        }

        Poll();
        _device.WriteState(state, NowUs, _magnetometer);
        return 0;
    }

    /// <summary>
    /// SceMotion NID_A408BC69 (SceDriverUser, driver_us.suprx): the basic orientation of one accelerometer sample with
    /// hysteresis, a pure function libpaf calls every frame. (const float th[3], const SceFVector3 *accel,
    /// SceFVector3 *out, const SceFVector3 *prev); th = degrees (in-plane band, Z cone, X/Y switch). A port of the
    /// firmware routine: a sample away from 1 g (more than 0.08 g over) keeps prev; an accel within th[1] degrees of
    /// +Z/-Z gives (0,0,-1)/(0,0,1); otherwise the dominant of X/Y in the screen plane, keeping prev inside the bands.
    /// </summary>
    [Hle("sceMotion_A408BC69", Nid = 0xA408BC69, Library = "SceMotion")]
    int sceMotionBasicOrientationFromAccel(uint th, uint accel, uint outVec, uint prev)
    {
        if (th == 0 || accel == 0 || outVec == 0 || prev == 0) return ERR_NULL_PARAMETER;
        float p0 = GuestMemory.Read<float>(th), p1 = GuestMemory.Read<float>(th + 4), p2 = GuestMemory.Read<float>(th + 8);
        var a = new System.Numerics.Vector3(GuestMemory.Read<float>(accel), GuestMemory.Read<float>(accel + 4), GuestMemory.Read<float>(accel + 8));
        float px = GuestMemory.Read<float>(prev), py = GuestMemory.Read<float>(prev + 4), pz = GuestMemory.Read<float>(prev + 8);
        var r = MotionFusion.BasicOrientationStep(p0, p1, p2, a, new System.Numerics.Vector3(px, py, pz));
        GuestMemory.Write(outVec, r.X);
        GuestMemory.Write(outVec + 4, r.Y);
        GuestMemory.Write(outVec + 8, r.Z);
        return 0;
    }

    // ---- SceMotionDev (the kernel sensor driver, bootimage SceMotionDev, FW 3.74): the shell's calibration updater
    // (SceCalibrationUpdate) reads the factory calibration and the magnetometer. No magnetometer is emulated and the
    // unit has no factory calibration records: the driver's own initial state.
    const int ERR_DEV_NULL = unchecked((int)0x80360001), ERR_DEV_SIZE = unchecked((int)0x80360002),
        ERR_DEV_NOT_CALIBRATED = unchecked((int)0x80360105);

    /// <summary>(void *info): 0x2C bytes the driver read from the sensor chips at start (ids and versions).</summary>
    [Hle] int sceMotionDevGetDeviceInfo(uint info) { if (info == 0) return ERR_DEV_NULL; GuestMemory.Fill(info, 0, 0x2C); return 0; }

    /// <summary>(void *calib): the 0x6C-byte gyro calibration record; empty without factory data.</summary>
    [Hle] int sceMotionDevGetGyroCalibData(uint calib) { if (calib == 0) return ERR_DEV_NULL; GuestMemory.Fill(calib, 0, 0x6C); return 0; }

    /// <summary>The magnetometer calibration the shell's updater stored (FW 3.74 SceMotionDev keeps one: 12 words and a
    /// version that counts the updates, 0 = never calibrated).</summary>
    [SnapshotMissing] // states of builds before it: never calibrated
    private readonly uint[] _magnCalib = new uint[MagnCalibWords];

    [SnapshotMissing]
    private uint _magnCalibVersion;

    /// <summary>The words of a calibration after the record's first (the version), 0x34-byte records.</summary>
    private const int MagnCalibWords = 12;
    private const uint MagnCalibBytes = 0x34;

    /// <summary>
    /// (SceMotionDevMagnCalibData *calib 0x34): the current calibration, as FW 3.74 (SceMotionDev 0xAAB4B41F,
    /// FUN_81006c68): NULL is 0x80360001, 0x80360105 until one was stored, else +0 the version and the 12 words.
    /// </summary>
    [Hle]
    int sceMotionDevGetCurrentMagnCalibData(uint calib)
    {
        if (calib == 0)
        {
            return ERR_DEV_NULL;
        }

        if (_magnCalibVersion == 0)
        {
            return ERR_DEV_NOT_CALIBRATED;
        }

        GuestMemory.W32(calib, _magnCalibVersion);
        for (int i = 0; i < MagnCalibWords; i++)
        {
            GuestMemory.W32(calib + 4 + (uint)i * 4, _magnCalib[i]);
        }

        return 0;
    }

    /// <summary>
    /// (const SceMotionDevMagnCalibData *calib, uint *version): stores a new calibration, as FW 3.74 (SceMotionDev
    /// 0x787384FA, FUN_81006af8): a program that is no system program gets 0x80360002, a NULL argument 0x80360001;
    /// the 12 words after the record's first are kept (its own version word is ignored), the version counts up and is
    /// written to <paramref name="version"/>.
    /// </summary>
    [Hle]
    int sceMotionDevUpdateMagnCalibData(uint calib, uint version)
    {
        if (!Emu.Access.IsSystemProgram)
        {
            return ERR_DEV_SIZE;
        }

        if (calib == 0 || version == 0)
        {
            return ERR_DEV_NULL;
        }

        for (int i = 0; i < MagnCalibWords; i++)
        {
            _magnCalib[i] = GuestMemory.R32(calib + 4 + (uint)i * 4);
        }

        _magnCalibVersion++;
        GuestMemory.W32(version, _magnCalibVersion);
        return 0;
    }

    /// <summary>(u8 mode[8]): the measurement mode, 7 until the driver has been switched to another one.</summary>
    [Hle]
    int sceMotionDevGetMeasMode(uint mode)
    {
        if (mode == 0) return ERR_DEV_NULL;
        GuestMemory.Fill(mode, 0, 8);
        GuestMemory.Write(mode, (byte)7);
        return 0;
    }

    /// <summary>
    /// (void *records, int count, int): the raw sensor records the calibration reads, as FW 3.74 (SceMotionDev
    /// 0xCC4FDA6A, FUN_81004420 in mode 0x10005): a NULL buffer or a count outside 1..64 is 0x80360001, a program that
    /// is no system program 0x80360002; then the records the driver's ring holds for the reader, how many. No raw
    /// sensor ring is emulated, so none is there (0, nothing written).
    /// </summary>
    [Hle]
    int sceMotionDevReadForMagnCalib(uint records, int count, int unused)
    {
        if (records == 0 || count > 0x40)
        {
            return ERR_DEV_NULL;
        }

        if (!Emu.Access.IsSystemProgram)
        {
            return ERR_DEV_SIZE;
        }

        if (count < 1)
        {
            return ERR_DEV_NULL;
        }

        return 0;
    }

    [Hle] int sceShutterSoundPlay(uint type) => 0;
}
