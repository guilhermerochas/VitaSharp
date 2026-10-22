using VitaSharp.Core.Hle.Motion;
using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// SceMotion fed by the host's <see cref="Host.IMotionSource"/> (controller gyro/accelerometer, phone sensors); at rest
/// without one. Fusion maths in <see cref="MotionFusion"/>, guest structures in <see cref="MotionDevice"/>. Also the
/// camera shutter sound. SceCamera is in Camera.cs.
/// </summary>
public sealed class Sensors : HleModule
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
    // no magnetometer is emulated: only the switch is kept (magFieldStability stays UNSTABLE, nedMatrix = rotation)
    [Hle] int sceMotionMagnetometerOn() { _magnetometer = true; return 0; }
    [Hle] int sceMotionMagnetometerOff() { _magnetometer = false; return 0; }
    [Hle] int sceMotionGetMagnetometerState() => _magnetometer ? 1 : 0;
    [Hle] int sceMotionSetTiltCorrection(int e) { Fusion.TiltCorrection = e != 0; return 0; }
    [Hle] int sceMotionGetTiltCorrection() => Fusion.TiltCorrection ? 1 : 0;
    [Hle] int sceMotionSetDeadband(int e) { Fusion.Deadband = e != 0; return 0; }
    [Hle] int sceMotionGetDeadband() => Fusion.Deadband ? 1 : 0;
    [Hle] int sceMotionSetGyroBiasCorrection(int e) { Fusion.GyroBiasCorrection = e != 0; return 0; }
    [Hle] int sceMotionGetGyroBiasCorrection() => Fusion.GyroBiasCorrection ? 1 : 0;

    [Hle]
    int sceMotionSetAngleThreshold(float angle)
    {
        if (float.IsNaN(angle) || angle < 0 || angle > 45.0f) return ERR_ANGLE_OUT_OF_RANGE;
        Fusion.AngleThreshold = angle;
        return 0;
    }

    [Hle] float sceMotionGetAngleThreshold() => Fusion.AngleThreshold;

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
    /// 64). Like sceMotionGetState this stays permissive when sampling was not started.
    /// </summary>
    [Hle]
    int sceMotionGetSensorState(uint states, int numRecords)
    {
        if (states == 0) return ERR_NULL_PARAMETER;
        if (numRecords is <= 0 or > MotionDevice.MaxRecords) return ERR_OUT_OF_BOUNDS;
        Poll();
        _device.WriteSensorStates(states, numRecords, NowUs);
        return 0;
    }

    internal static void WriteSensorStates(uint states, int numRecords, long nowUs, uint counter)
        => MotionDevice.WriteRestSensorStates(states, numRecords, nowUs, counter);

    [Hle]
    int sceMotionGetState(uint state)
    {
        if (state == 0) return unchecked((int)0x80360001);
        Poll();
        _device.WriteState(state, NowUs);
        return 0;
    }

    [Hle] int sceShutterSoundPlay(uint type) => 0;
}
