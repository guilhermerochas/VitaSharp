using System.Numerics;
using VitaSharp.Core.Host;

namespace VitaSharp.Core.Input;

/// <summary>How a controller's motion maps onto the Vita.</summary>
public enum MotionOrientation
{
    /// <summary>The controller's face (buttons side) is the Vita's screen: a controller lying on the desk is a Vita
    /// lying screen up, tilting the controller towards you tilts the Vita towards you (Vita3K's mapping).</summary>
    Natural,
    /// <summary>A controller held flat is a Vita held upright in front of you, screen facing you and rear camera
    /// pointing ahead (augmented reality games that are played looking "through" the Vita).</summary>
    FlatIsUpright,
}

/// <summary>
/// Conversions from host sensor conventions to the Vita's (<see cref="MotionSample"/>: x right, y top, z out of the
/// screen; acceleration in G pointing along gravity, so lying flat reads (0, 0, -1); angular velocity in rad/s).
/// </summary>
public static class MotionAxes
{
    /// <summary>SDL_STANDARD_GRAVITY / Android SensorManager.GRAVITY_EARTH, m/s².</summary>
    public const float StandardGravity = 9.80665f;

    /// <summary>
    /// Game controller sensors as SDL reports them (SDL_GameControllerGetSensorData): held in front of you, x points
    /// right, y up out of the controller's face, z towards you; accelerometer in m/s² including gravity as a reaction
    /// (+9.8 on y when lying flat); gyroscope in rad/s. <see cref="MotionOrientation.Natural"/> is Vita3K's conversion
    /// (x, -z, y): controller y (face) = Vita z (screen), controller -z (away from you) = Vita y (top edge).
    /// </summary>
    public static MotionSample FromGamepad(ulong timestampUs, Vector3 accelMs2, Vector3 gyroRads, MotionOrientation orientation = MotionOrientation.Natural)
        => new(timestampUs, Map(accelMs2, orientation) / -StandardGravity, Map(gyroRads, orientation));

    static Vector3 Map(Vector3 v, MotionOrientation o) => o switch
    {
        // rotate +90 degrees about x: controller face up -> screen up
        MotionOrientation.Natural => new Vector3(v.X, -v.Z, v.Y),
        // the gamepad frame already is the Vita frame of a Vita held upright in front of you
        _ => v,
    };

    /// <summary>
    /// Phone / tablet sensors (Android SensorManager, SDL device sensors) in landscape, whose axes already match the
    /// Vita's (x right, y top, z out of the screen): only the accelerometer sign and unit change.
    /// </summary>
    public static MotionSample FromDevice(ulong timestampUs, Vector3 accelMs2, Vector3 gyroRads)
        => new(timestampUs, accelMs2 / -StandardGravity, gyroRads);

    /// <summary>
    /// Gravity seen by a Vita that started lying flat (screen up) and was then tilted by <paramref name="pitch"/>
    /// about its x axis (positive raises the top edge) and then <paramref name="roll"/> about its y axis (positive
    /// lowers the right edge), radians. Used by scripted input and synthetic tests.
    /// </summary>
    public static Vector3 TiltGravity(float pitch, float roll)
    {
        var deviceToFlat = Quaternion.CreateFromAxisAngle(Vector3.UnitX, pitch) * Quaternion.CreateFromAxisAngle(Vector3.UnitY, roll);
        return Vector3.Transform(new Vector3(0, 0, -1), Quaternion.Conjugate(deviceToFlat));
    }
}
