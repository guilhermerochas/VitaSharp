using System.Numerics;
using VitaSharp.Core.Hle.Motion;
using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// The SceMotion functions of driver_us.suprx 3.74 the HLE still lacked: the Ext getters (port 0 = the built-in
/// sensors, 1..4 = a PS TV's external controllers, none of which is emulated), the packed sensor state, the three
/// basic-orientation thresholds, the accelerometer's zero point, and the state reads of the system's two other motion
/// clients (the location service and the internal client of Motion.ClientSampling.cs).
/// </summary>
public sealed partial class Sensors
{
    private const float MaxAngleThreshold = 45f;

    /// <summary>The packed state's centre and its counts per g (accelerometer) and per rad/s times pi (gyro).</summary>
    private const float PackedCentre = 512f;
    private const float PackedCountsPerG = 113f;
    private const double PackedGyroCounts = 246.0;
    private const int PackedMax = 0x3FF;

    // The values sceMotionSetDeadband / SetTiltCorrection were given (the library gives the same value back).
    [SnapshotMissing(1)] // states of builds before it: the default (on)
    private int _deadbandSetting = 1;

    [SnapshotMissing(1)]
    private int _tiltCorrectionSetting = 1;

    /// <summary>The setting the library keeps, or the switch itself for a state saved before the setting was kept.</summary>
    private static int Setting(int setting, bool on) => (setting != 0) == on ? setting : (on ? 1 : 0);

    /// <summary>
    /// (int port, int *value): sceMotionGetDeadband of a port. Port 0: the value (NULL 0x80360209); above 4
    /// 0x8036020B; 1..4: NULL 0x80360209, no controller 0x80360202.
    /// </summary>
    [Hle("sceMotionGetDeadbandExt")]
    private int sceMotionGetDeadbandExt(int port, uint value) => GetExtSetting(port, value, sceMotionGetDeadbandThroughEntry());

    /// <summary>(int port, int *value): sceMotionGetTiltCorrection of a port, checked as sceMotionGetDeadbandExt.</summary>
    [Hle("sceMotionGetTiltCorrectionExt")]
    private int sceMotionGetTiltCorrectionExt(int port, uint value) => GetExtSetting(port, value, sceMotionGetTiltCorrectionThroughEntry());

    private static int GetExtSetting(int port, uint value, int current)
    {
        if (port > 4)
        {
            return ERR_EXT_PORT_RANGE;
        }

        if (value == 0)
        {
            return ERR_NULL_PARAMETER;
        }

        if (port != 0)
        {
            return ERR_EXT_NO_PORT;
        }

        GuestMemory.W32(value, (uint)current);
        return 0;
    }

    /// <summary>
    /// sceMotionGetStateExt (int port, u16 state[4]): the state in the packed 10-bit form of a DualShock's motion
    /// sensors. Port 0 takes sceMotionGetState (its errors are the answer) and packs acceleration x, y, z as
    /// 512 + 113 per g and the angular velocity about y as 512 - 246/pi per rad/s, each truncated and held in
    /// 0..1023. Above 4 0x8036020B; 1..4: NULL 0x80360209, no controller 0x80360202.
    /// </summary>
    [Hle("sceMotionGetStateExt", Nid = 0x340BFBE6, Library = "SceMotion")]
    private int sceMotionGetPackedStateExt(int port, uint state)
    {
        if (port != 0)
        {
            if (port > 4)
            {
                return ERR_EXT_PORT_RANGE;
            }

            return state == 0 ? ERR_NULL_PARAMETER : ERR_EXT_NO_PORT;
        }

        if (!_sampling)
        {
            return ERR_NOT_SAMPLING;
        }

        Poll();
        (Vector3 acceleration, Vector3 angularVelocity) = _device.CurrentMotion();
        (ushort x, ushort y, ushort z, ushort gyro) = PackState(acceleration, angularVelocity);
        GuestMemory.Write(state, x);
        GuestMemory.Write(state + 2, y);
        GuestMemory.Write(state + 4, z);
        GuestMemory.Write(state + 6, gyro);
        return 0;
    }

    /// <summary>The packed DualShock-style state of sceMotionGetStateExt port 0.</summary>
    internal static (ushort X, ushort Y, ushort Z, ushort Gyro) PackState(Vector3 acceleration, Vector3 angularVelocity)
    {
        return (Pack(PackedCentre + acceleration.X * PackedCountsPerG),
            Pack(PackedCentre + acceleration.Y * PackedCountsPerG),
            Pack(PackedCentre + acceleration.Z * PackedCountsPerG),
            Pack(PackedCentre - angularVelocity.Y * PackedGyroCounts / Math.PI));
    }

    private static ushort Pack(double value)
    {
        int counts = (int)value;
        if (counts < 0)
        {
            return 0;
        }

        return (ushort)Math.Min(counts, PackedMax);
    }

    /// <summary>
    /// NID_CA0A798B (const float th[3]): sets the three basic-orientation thresholds one by one (no range check);
    /// the first is stored as 90 minus th[0]. NULL 0x80360209.
    /// </summary>
    [Hle("NID_CA0A798B", Nid = 0xCA0A798B, Library = "SceMotion")]
    private int MotionSetAngleThresholds(uint thresholds)
    {
        if (thresholds == 0)
        {
            return ERR_NULL_PARAMETER;
        }

        Fusion.AngleThresholdPlane = 90f - GuestMemory.Read<float>(thresholds);
        Fusion.AngleThresholdCone = GuestMemory.Read<float>(thresholds + 4);
        Fusion.AngleThreshold = GuestMemory.Read<float>(thresholds + 8);
        return 0;
    }

    /// <summary>NID_F4E9D62E (float th[3]): the three thresholds, the first given back as 90 minus the stored one.
    /// The library writes without checking the pointer.</summary>
    [Hle("NID_F4E9D62E", Nid = 0xF4E9D62E, Library = "SceMotion")]
    private int MotionGetAngleThresholds(uint thresholds)
    {
        GuestMemory.Write(thresholds, 90f - Fusion.AngleThresholdPlane);
        GuestMemory.Write(thresholds + 4, Fusion.AngleThresholdCone);
        GuestMemory.Write(thresholds + 8, Fusion.AngleThreshold);
        return 0;
    }

    /// <summary>
    /// NID_02AC0F4B (SceFVector3 *a, *b, *c): the accelerometer's zero point (the first vector of the library's
    /// calibration, read from the motion device at start), written to all three. The emulated sensors deliver
    /// calibrated values, so the zero point is the origin. No pointer is checked.
    /// </summary>
    [Hle("NID_02AC0F4B", Nid = 0x02AC0F4B, Library = "SceMotion")]
    private int MotionGetAccelerometerZero(uint first, uint second, uint third)
    {
        GuestMemory.Fill(first, 0, 12);
        GuestMemory.Fill(second, 0, 12);
        GuestMemory.Fill(third, 0, 12);
        return 0;
    }

    /// <summary>NID_D23686B0 (SceMotionState *state): sceMotionGetState of the internal client (NID_0D9FA7EA); a NULL
    /// state is checked before that client's sampling (0x80360209, then 0x80360206).</summary>
    [Hle("NID_D23686B0", Nid = 0xD23686B0, Library = "SceMotion")]
    private int MotionInternalGetState(uint state)
    {
        if (state == 0)
        {
            return ERR_NULL_PARAMETER;
        }

        return WriteClientState(_internalSampling, state);
    }

    /// <summary>
    /// NID_D66D5192 (SceMotionSensorState *states, int numRecords): sceMotionGetSensorState of the location service
    /// (NID_ACEFA121): its sampling (0x80360206), then the pointer (0x80360209), then the count (1..64, 0x80360205).
    /// </summary>
    [Hle("NID_D66D5192", Nid = 0xD66D5192, Library = "SceMotion")]
    private int MotionLocationGetSensorState(uint states, int numRecords)
    {
        if (!_locationSampling)
        {
            return ERR_NOT_SAMPLING;
        }

        if (states == 0)
        {
            return ERR_NULL_PARAMETER;
        }

        if (numRecords is <= 0 or > MotionDevice.MaxRecords)
        {
            return ERR_OUT_OF_BOUNDS;
        }

        Poll();
        _device.WriteSensorStates(states, numRecords, NowUs);
        return 0;
    }

    /// <summary>The state read of a client whose sampling switch is <paramref name="sampling"/>.</summary>
    private int WriteClientState(bool sampling, uint state)
    {
        if (!sampling)
        {
            return ERR_NOT_SAMPLING;
        }

        if (state == 0)
        {
            return ERR_NULL_PARAMETER;
        }

        Poll();
        _device.WriteState(state, NowUs);
        return 0;
    }
}
