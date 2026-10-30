using System.Numerics;
using VitaSharp.Core.Host;

namespace VitaSharp.Core.Input;

/// <summary>
/// A Vita moved by keys and the mouse instead of real sensors (no gyroscope on the host): tilt / twist controls bend
/// the device towards a target angle, mouse look rotates it freely, and the result is turned into the accelerometer +
/// gyroscope samples a real Vita would report, so SceMotion's fusion produces a quaternion, rotation matrix and basic
/// orientation that agree with the gravity the game reads.
/// <para>Model: the neutral pose is the Vita lying flat, screen up (the at-rest values SceMotion reported before any
/// source existed). The pose is <c>Yaw(z) * Pitch(x) * Roll(y)</c> in that flat frame (x right edge, y top edge,
/// z out of the screen): pitch &gt; 0 raises the top edge, roll &gt; 0 lowers the right edge, yaw &gt; 0 turns the
/// Vita counter-clockwise seen from above. Each key-driven angle follows its target (held = ±<see
/// cref="TiltSettings.MaxAngle"/>, released = 0) through a critically damped spring, so it ramps in and eases out
/// without a jump in angular velocity; mouse look adds its own pitch / yaw offsets that stay until re-centred.</para>
/// <para>Samples are produced every <see cref="StepUs"/> of host time (250 Hz, like SDL's controller sensors); the
/// gyroscope is the body-frame rotation between consecutive poses, the accelerometer the gravity seen by the new
/// pose. Platform neutral; thread-safe.</para>
/// </summary>
public sealed class VirtualMotion
{
    /// <summary>Sample spacing in microseconds.</summary>
    public const long StepUs = 4000;
    const float StepS = StepUs / 1e6f;
    /// <summary>Spring stiffness (rad/s) at speed 1: 95% of the target in ~0.4 s, peak rate ~130 degrees/s at 30.</summary>
    public const float BaseOmega = 12f;
    /// <summary>Mouse look: degrees per pixel at sensitivity 1.</summary>
    public const float MouseDegreesPerPixel = 0.2f;
    /// <summary>Fastest mouse-look / re-centre rotation (degrees/s), below SceMotion's gyro clamp (5 rev/s).</summary>
    public const float MouseMaxRate = 720f;
    /// <summary>Longest stretch of host time turned into samples at once; older time is skipped (the game did not
    /// poll for over a second).</summary>
    const long MaxCatchUpUs = 1_000_000;
    const float Deg = MathF.PI / 180f;
    const float MaxPitch = 89.5f * Deg, MouseMaxPitch = 80f * Deg;

    readonly object _lock = new();
    TiltSettings _settings = new();
    // key-driven springs: angle (rad) and its rate (rad/s)
    float _pitch, _pitchV, _roll, _rollV, _yaw, _yawV;
    // targets as -1..1 activations
    float _inPitch, _inRoll, _inYaw;
    // mouse look: target (accumulated deltas) and current offsets (rad)
    float _mousePitchTarget, _mouseYawTarget, _mousePitch, _mouseYaw;
    bool _recenterHeld;
    long _lastUs = -1;
    Quaternion _pose = Quaternion.Identity;

    public VirtualMotion(TiltSettings? settings = null) { if (settings != null) _settings = settings.Clone(); }

    public TiltSettings Settings
    {
        get { lock (_lock) return _settings.Clone(); }
        set { lock (_lock) _settings = value.Clone(); }
    }

    /// <summary>True once any tilt control or mouse look was used: until then the source reports no sensors and the
    /// Vita keeps the historic at-rest state.</summary>
    public bool Used { get { lock (_lock) return _used; } }
    bool _used;

    /// <summary>Current activations of the controls (0..1 each, as the input mapper gives them). Re-centre acts on its
    /// rising edge.</summary>
    public void SetControls(float forward, float back, float left, float right, float twistLeft, float twistRight, bool recenter)
    {
        lock (_lock)
        {
            _inPitch = Math.Clamp(back - forward, -1f, 1f);
            _inRoll = Math.Clamp(right - left, -1f, 1f);
            _inYaw = Math.Clamp(twistLeft - twistRight, -1f, 1f);
            if (_inPitch != 0 || _inRoll != 0 || _inYaw != 0) _used = true;
            if (recenter && !_recenterHeld) RecenterLocked();
            _recenterHeld = recenter;
        }
    }

    /// <summary>Reads the tilt controls of <paramref name="cp"/> from a device state (keys and / or a pad).</summary>
    public void SetControls(CompiledProfile cp, PadSnapshot? pad, IReadOnlySet<string>? keys)
    {
        float V(VitaControl c) => InputMapper.Value(cp, c, pad, keys);
        float threshold = cp.Profile.ButtonThreshold is > 0 and <= 1 ? cp.Profile.ButtonThreshold : 0.5f;
        SetControls(V(VitaControl.TiltForward), V(VitaControl.TiltBack), V(VitaControl.TiltLeft), V(VitaControl.TiltRight),
            V(VitaControl.TwistLeft), V(VitaControl.TwistRight), V(VitaControl.MotionRecenter) >= threshold);
    }

    /// <summary>Mouse look movement in pixels (screen coordinates: +x right, +y down). Moving right turns the Vita
    /// right, moving up raises its top edge (looks up through the rear camera).</summary>
    public void AddMouse(float dx, float dy)
    {
        if (dx == 0 && dy == 0) return;
        lock (_lock)
        {
            float k = MouseDegreesPerPixel * Math.Clamp(_settings.MouseSensitivity, 0.05f, 10f) * Deg;
            if (_settings.MouseInvertY) dy = -dy;
            _mouseYawTarget -= dx * k;
            _mousePitchTarget = Math.Clamp(_mousePitchTarget - dy * k, -MouseMaxPitch, MouseMaxPitch);
            _used = true;
        }
    }

    /// <summary>Brings the mouse-look rotation back to neutral (smoothly, at <see cref="MouseMaxRate"/>).</summary>
    public void Recenter() { lock (_lock) RecenterLocked(); }

    void RecenterLocked()
    {
        _mousePitchTarget = _mouseYawTarget = 0;
        // whole turns of yaw are the same pose: unwind the shortest way
        _mouseYaw = MathF.IEEERemainder(_mouseYaw, 2 * MathF.PI);
    }

    /// <summary>Current pitch / roll / yaw in degrees (keys + mouse), for previews.</summary>
    public (float Pitch, float Roll, float Yaw) Angles
    {
        get
        {
            lock (_lock)
                return (Math.Clamp(_pitch + _mousePitch, -MaxPitch, MaxPitch) / Deg, _roll / Deg, MathF.IEEERemainder(_yaw + _mouseYaw, 2 * MathF.PI) / Deg);
        }
    }

    /// <summary>Device-to-flat rotation of the current pose.</summary>
    public Quaternion Pose { get { lock (_lock) return _pose; } }

    /// <summary>Gravity as the accelerometer of a Vita in <paramref name="pose"/> reads it (G; flat = (0, 0, -1)).</summary>
    public static Vector3 Gravity(Quaternion pose) => Vector3.Transform(new Vector3(0, 0, -1), Quaternion.Conjugate(pose));

    /// <summary>The pose for pitch / roll / yaw in radians.</summary>
    public static Quaternion PoseOf(float pitch, float roll, float yaw)
        => Quaternion.Normalize(Quaternion.CreateFromAxisAngle(Vector3.UnitZ, yaw) * Quaternion.CreateFromAxisAngle(Vector3.UnitX, pitch)
                                * Quaternion.CreateFromAxisAngle(Vector3.UnitY, roll));

    /// <summary>
    /// Advances the simulation to host time <paramref name="nowUs"/> and writes the samples of that stretch, oldest
    /// first (at most <paramref name="dest"/>.Length; the rest follows on the next call). The first call yields one
    /// sample of the current pose.
    /// </summary>
    public int Generate(long nowUs, Span<MotionSample> dest)
    {
        if (dest.Length == 0) return 0;
        lock (_lock)
        {
            if (_lastUs < 0)
            {
                _lastUs = nowUs;
                _pose = Compose();
                dest[0] = new MotionSample((ulong)nowUs, Gravity(_pose), Vector3.Zero);
                return 1;
            }
            long steps = (nowUs - _lastUs) / StepUs;
            if (steps <= 0) return 0;
            long maxSteps = MaxCatchUpUs / StepUs;
            if (steps > maxSteps)
            {
                // a long gap: run the springs through it without samples (the fusion would ignore them anyway)
                for (long i = steps - maxSteps; i > 0; i--) Step(1);
                _lastUs += (steps - maxSteps) * StepUs;
                _pose = Compose();
                steps = maxSteps;
            }
            int n = (int)Math.Min(steps, dest.Length);
            for (int i = 0; i < n; i++)
            {
                Step((int)(steps - i));
                _lastUs += StepUs;
                var pose = Compose();
                // body-frame angular velocity: pose = previous * delta
                var delta = Quaternion.Normalize(Quaternion.Conjugate(_pose) * pose);
                if (delta.W < 0) delta = Quaternion.Negate(delta);
                var v = new Vector3(delta.X, delta.Y, delta.Z);
                float s = v.Length();
                var gyro = s > 1e-9f ? v / s * (2 * MathF.Atan2(s, delta.W) / StepS) : Vector3.Zero;
                _pose = pose;
                dest[i] = new MotionSample((ulong)_lastUs, Gravity(pose), gyro);
            }
            return n;
        }
    }

    /// <summary>One step of the springs and of the mouse look; <paramref name="remaining"/> = steps left in this
    /// stretch, over which pending mouse movement is spread.</summary>
    void Step(int remaining)
    {
        float max = Math.Clamp(_settings.MaxAngle, 1f, 85f) * Deg;
        float w = BaseOmega * Math.Clamp(_settings.Speed, 0.1f, 8f);
        Spring(ref _pitch, ref _pitchV, _inPitch * max, w);
        Spring(ref _roll, ref _rollV, _inRoll * max, w);
        Spring(ref _yaw, ref _yawV, _inYaw * max, w);
        // pending mouse movement spread over the stretch, the combined rate (pitch and yaw axes are orthogonal) capped
        float dp = (_mousePitchTarget - _mousePitch) / Math.Max(remaining, 1), dy = (_mouseYawTarget - _mouseYaw) / Math.Max(remaining, 1);
        float len = MathF.Sqrt(dp * dp + dy * dy), limit = MouseMaxRate * Deg * StepS;
        if (len > limit) { dp *= limit / len; dy *= limit / len; }
        _mousePitch += dp;
        _mouseYaw += dy;
    }

    /// <summary>Critically damped spring towards <paramref name="target"/> (semi-implicit Euler, stable at 4 ms).</summary>
    static void Spring(ref float x, ref float v, float target, float w)
    {
        v += (w * w * (target - x) - 2 * w * v) * StepS;
        x += v * StepS;
        if (MathF.Abs(target - x) < 1e-5f && MathF.Abs(v) < 1e-4f) { x = target; v = 0; }
    }

    Quaternion Compose() => PoseOf(Math.Clamp(_pitch + _mousePitch, -MaxPitch, MaxPitch), _roll, _yaw + _mouseYaw);
}
