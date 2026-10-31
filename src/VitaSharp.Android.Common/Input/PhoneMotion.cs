using System.Numerics;
using VitaSharp.Core.Host;
using VitaSharp.Core.Input;
using VitaSharp.Core.Util;

namespace VitaSharp.Android.Input;

/// <summary>"Movimento" (Controles): what drives the Vita's motion sensors (SceMotion) on Android.</summary>
public enum AndroidMotionMode
{
    /// <summary>The phone's own accelerometer and gyroscope (the default when it has both).</summary>
    Phone,
    /// <summary>A connected controller's accelerometer and gyroscope (DualShock 4, DualSense...; Android 12+).</summary>
    Controller,
    /// <summary>The Vita stays at rest (lying flat, screen up), as without sensors.</summary>
    Off,
}

/// <summary>The stored values of the "Movimento" setting (AppPreferences.Motion) and their labels.</summary>
public static class AndroidMotionModes
{
    public const string Auto = "", Phone = "phone", Controller = "controller", Off = "off";

    /// <summary>The setting's value: "" (automatic: the phone's sensors when it has them, else a controller's),
    /// "phone", "controller" or "off"; anything else counts as automatic.</summary>
    public static AndroidMotionMode Resolve(string? value, bool phoneHasSensors) => value switch
    {
        Phone => AndroidMotionMode.Phone,
        Controller => AndroidMotionMode.Controller,
        Off => AndroidMotionMode.Off,
        _ => phoneHasSensors ? AndroidMotionMode.Phone : AndroidMotionMode.Controller,
    };

    public static string Label(AndroidMotionMode m) => m switch
    {
        AndroidMotionMode.Phone => "Sensores do celular",
        AndroidMotionMode.Controller => "Giroscópio do controle",
        _ => "Desligado",
    };
}

/// <summary>
/// Android sensor axes to the Vita's (<see cref="MotionSample"/>: x right, y top, z out of the screen; acceleration in
/// G along gravity, lying flat = (0, 0, -1); angular velocity in rad/s). Pure math, unit-tested.
/// </summary>
public static class AndroidMotionAxes
{
    /// <summary>
    /// A phone sensor vector (SensorManager: x right, y top, z out of the screen in the device's natural orientation,
    /// portrait on a phone) in the axes of the screen as the user sees it, given Display.getRotation()
    /// (<paramref name="displayRotation"/> 0..3 = Surface.ROTATION_0/90/180/270, the rotation of the drawn image
    /// against the natural orientation; ROTATION_90 = the device turned 90 degrees counter-clockwise). The game is
    /// always landscape, so these screen axes are the Vita's. The same remap as SensorManager.remapCoordinateSystem
    /// for the display rotation; z does not change.
    /// </summary>
    public static Vector3 DeviceToScreen(Vector3 v, int displayRotation) => (displayRotation & 3) switch
    {
        1 => new Vector3(-v.Y, v.X, v.Z),
        2 => new Vector3(-v.X, -v.Y, v.Z),
        3 => new Vector3(v.Y, -v.X, v.Z),
        _ => v,
    };

    /// <summary>A phone's accelerometer (m/s², gravity read as +9.8 up the z axis when lying flat) and gyroscope
    /// (rad/s) as a Vita sample, for the display rotation of <see cref="DeviceToScreen"/>.</summary>
    public static MotionSample FromPhone(ulong timestampUs, Vector3 accelMs2, Vector3 gyroRads, int displayRotation)
        => MotionAxes.FromDevice(timestampUs, DeviceToScreen(accelMs2, displayRotation), DeviceToScreen(gyroRads, displayRotation));

    /// <summary>
    /// A controller's sensors as Android reports them (InputDevice.getSensorManager, Android 12+: the kernel's
    /// hid-playstation / hid-nintendo axes, the frame SDL also uses: x right, y out of the controller's face, z towards
    /// the player; m/s² and rad/s). <see cref="MotionOrientation.Natural"/> like the desktop: a controller lying on
    /// the desk is a Vita lying screen up.
    /// </summary>
    public static MotionSample FromController(ulong timestampUs, Vector3 accelMs2, Vector3 gyroRads)
        => MotionAxes.FromGamepad(timestampUs, accelMs2, gyroRads, MotionOrientation.Natural);

    /// <summary>
    /// The re-centre rotation: turns <paramref name="gravityG"/> (the current gravity in the Vita's axes, any length)
    /// onto (0, 0, -1), so the pose the device is held in from now on reads as the Vita lying flat, screen up (the at-rest
    /// pose of the desktop's tilt keys). Identity for a zero vector; a device face down turns half a turn about x.
    /// </summary>
    public static Quaternion Recentre(Vector3 gravityG)
    {
        if (gravityG.LengthSquared() < 1e-6f) return Quaternion.Identity;
        var a = Vector3.Normalize(gravityG);
        var b = new Vector3(0, 0, -1);
        float dot = Vector3.Dot(a, b);
        if (dot > 0.99999f) return Quaternion.Identity;
        if (dot < -0.99999f) return Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI);
        var axis = Vector3.Normalize(Vector3.Cross(a, b));
        return Quaternion.CreateFromAxisAngle(axis, MathF.Acos(Math.Clamp(dot, -1f, 1f)));
    }

    /// <summary>A sample with the re-centre rotation applied to both vectors (a constant rotation of the body frame).</summary>
    public static MotionSample Rotate(MotionSample s, Quaternion q) => q.IsIdentity ? s
        : new MotionSample(s.TimestampUs, Vector3.Transform(s.Acceleration, q), Vector3.Transform(s.AngularVelocity, q));
}

/// <summary>Which frame a <see cref="SensorMotionSource"/>'s raw vectors are in.</summary>
public enum SensorFrame { Phone, Controller }

/// <summary>
/// An accelerometer + gyroscope pair (the phone's SensorManager, or a controller's) as an <see cref="IMotionSource"/>.
/// Android delivers the two sensors as separate events: each gyroscope event becomes a sample with the latest
/// acceleration (the gyroscope drives the fusion; an accelerometer event alone only updates gravity). The re-centre
/// action (<see cref="Recenter"/>) turns the pose held at that moment into the Vita lying flat. Thread-safe: the
/// sensor thread feeds it, guest threads read it.
/// </summary>
public sealed class SensorMotionSource : IMotionSource
{
    public const int Capacity = 512;
    /// <summary>Smoothing of the gravity the re-centre uses (per accelerometer event).</summary>
    const float GravityAlpha = 0.1f;

    readonly object _lock = new();
    readonly MotionSample[] _ring = new MotionSample[Capacity];
    readonly SensorFrame _frame;
    int _head, _count;
    Vector3 _accel, _gravity;
    bool _hasAccel, _hasGyro;
    Quaternion _recentre = Quaternion.Identity;
    volatile int _rotation;

    public SensorMotionSource(SensorFrame frame, string name)
    {
        _frame = frame;
        Name = name;
    }

    /// <summary>The sensor device (logs).</summary>
    public string Name { get; }

    /// <summary>Display.getRotation() (0..3) for the phone's axes; ignored for a controller.</summary>
    public int DisplayRotation { get => _rotation; set => _rotation = value & 3; }

    /// <summary>Both sensors have delivered: from then on the Vita follows them (also while the activity is paused and
    /// the listeners are off, so a resume does not snap the game back to the at-rest pose).</summary>
    public bool HasSensors { get { lock (_lock) return _hasAccel && _hasGyro; } }

    /// <summary>The re-centre rotation in use (identity = none).</summary>
    public Quaternion RecentreRotation { get { lock (_lock) return _recentre; } }

    /// <summary>An accelerometer event: m/s² in the sensor's own frame.</summary>
    public void OnAccelerometer(Vector3 ms2)
    {
        lock (_lock)
        {
            _accel = ms2;
            _gravity = _hasAccel ? Vector3.Lerp(_gravity, ms2, GravityAlpha) : ms2;
            _hasAccel = true;
        }
    }

    /// <summary>A gyroscope event: rad/s in the sensor's own frame, <paramref name="timestampNs"/> = SensorEvent.timestamp.</summary>
    public void OnGyroscope(long timestampNs, Vector3 rads)
    {
        lock (_lock)
        {
            _hasGyro = true;
            if (!_hasAccel) return;
            var s = Map((ulong)Math.Max(0, timestampNs / 1000), _accel, rads);
            _ring[_head] = AndroidMotionAxes.Rotate(s, _recentre);
            _head = (_head + 1) % Capacity;
            if (_count < Capacity) _count++;
        }
    }

    MotionSample Map(ulong us, Vector3 accel, Vector3 gyro) => _frame == SensorFrame.Phone
        ? AndroidMotionAxes.FromPhone(us, accel, gyro, _rotation)
        : AndroidMotionAxes.FromController(us, accel, gyro);

    /// <summary>The pose held now reads as the Vita lying flat, screen up, from the next sample on. Recentring while
    /// the device lies flat clears it.</summary>
    public void Recenter()
    {
        lock (_lock)
        {
            if (!_hasAccel) return;
            _recentre = AndroidMotionAxes.Recentre(Map(0, _gravity, Vector3.Zero).Acceleration);
            Log.Info($"Motion: re-centred {Name} (gravity {Map(0, _gravity, Vector3.Zero).Acceleration:F2} G)");
        }
    }

    /// <summary>Forgets the samples queued so far (a listener registered again after a pause).</summary>
    public void Clear() { lock (_lock) _count = 0; }

    public int ReadSamples(Span<MotionSample> dest)
    {
        lock (_lock)
        {
            int n = Math.Min(_count, dest.Length);
            int start = (_head - _count + Capacity) % Capacity;
            // more queued than fit: keep the newest
            start = (start + (_count - n)) % Capacity;
            for (int i = 0; i < n; i++) dest[i] = _ring[(start + i) % Capacity];
            _count = 0;
            return n;
        }
    }
}

/// <summary>
/// The SceMotion source of the Android game: the sensor device picked by the "Movimento" setting (the phone's or a
/// controller's) first, then the fallback the session already had (the hardware keyboard's tilt keys, a
/// <see cref="VirtualMotion"/> source that only reports sensors once a tilt key was used), like the desktop's
/// HostInput, where a controller's gyroscope wins over the keys. Off keeps the Vita at rest.
/// </summary>
public sealed class AndroidMotion : IMotionSource
{
    readonly object _lock = new();
    IMotionSource? _active;
    string? _logged;

    public AndroidMotion(AndroidMotionMode mode) => Mode = mode;

    public AndroidMotionMode Mode { get; }

    /// <summary>The sensor device in use (the activity swaps it as controllers come and go); null = none.</summary>
    public SensorMotionSource? Sensors { get; set; }

    /// <summary>The motion source the session had before (keyboard tilt keys), used while no sensor device feeds.</summary>
    public IMotionSource? Fallback { get; set; }

    IMotionSource? Pick()
    {
        if (Mode == AndroidMotionMode.Off) return null;
        if (Sensors is { HasSensors: true } s) return s;
        return Fallback is { HasSensors: true } f ? f : null;
    }

    public bool HasSensors => Pick() != null;

    public int ReadSamples(Span<MotionSample> dest)
    {
        var src = Pick();
        lock (_lock)
        {
            if (src != _active)
            {
                _active = src;
                string name = src is SensorMotionSource s ? s.Name : src != null ? "tilt keys" : "none";
                if (name != _logged) { _logged = name; Log.Info($"Motion: {name} drives the Vita's motion sensors ({Mode})"); }
            }
        }
        return src?.ReadSamples(dest) ?? 0;
    }

    /// <summary>The re-centre action (pause menu): the sensor device's pose now reads as lying flat.</summary>
    public void Recenter() => Sensors?.Recenter();
}
