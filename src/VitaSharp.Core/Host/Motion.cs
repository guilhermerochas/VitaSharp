using System.Numerics;

namespace VitaSharp.Core.Host;

/// <summary>
/// One accelerometer + gyroscope reading, already in the Vita's sensor axes and units (see <see cref="Input.MotionAxes"/>):
/// x towards the right edge, y towards the top edge, z out of the screen; <see cref="Acceleration"/> in G with the
/// Vita's sign convention (lying flat, screen up = (0, 0, -1)); <see cref="AngularVelocity"/> in rad/s, right-handed
/// about those axes.
/// </summary>
/// <param name="TimestampUs">Host monotonic time of the reading in microseconds (only differences are used).</param>
public readonly record struct MotionSample(ulong TimestampUs, Vector3 Acceleration, Vector3 AngularVelocity);

/// <summary>
/// Host motion sensors backing SceMotion: a controller's gyro/accelerometer (desktop, SDL), the phone's own sensors
/// (Android), or a script. Without a source, or while <see cref="HasSensors"/> is false, the emulated Vita is at rest.
/// Implementations are polled from guest threads and must be thread-safe.
/// </summary>
public interface IMotionSource
{
    /// <summary>True while a device with both an accelerometer and a gyroscope is feeding samples.</summary>
    bool HasSensors { get; }

    /// <summary>Copies the samples received since the previous call into <paramref name="dest"/>, oldest first, and
    /// returns how many were written. When more arrived than fit, the oldest are dropped.</summary>
    int ReadSamples(Span<MotionSample> dest);
}
