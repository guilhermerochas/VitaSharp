using System.Numerics;
using VitaSharp.Core.Host;
using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Motion;

/// <summary>
/// State behind SceMotion: drains the host <see cref="IMotionSource"/>, runs every sample through
/// <see cref="MotionFusion"/>, keeps the last 64 raw records for sceMotionGetSensorState and writes the guest
/// structures (layouts from vita-headers psp2/motion.h). Without a live source the device is at rest (the values
/// VitaSharp always reported). Thread-safe: guest threads may call SceMotion concurrently.
/// </summary>
public sealed class MotionDevice
{
    public const int MaxRecords = 64, StateSize = 0xF8, SensorStateSize = 0x40;
    /// <summary>Spacing of the synthesised at-rest records: unconfirmed, the vblank period like Ctrl/Touch.</summary>
    public const long SampleIntervalUs = 16667;

    readonly record struct Record(Vector3 Accel, Vector3 Gyro, long TimestampUs, uint Counter);

    readonly object _lock = new();
    readonly MotionSample[] _buffer = new MotionSample[1024];
    readonly Record[] _ring = new Record[MaxRecords];
    int _head, _count;
    uint _counter, _restCounter;
    ulong _lastHostUs;
    long _lastGuestUs;
    bool _live;

    /// <summary>The magnetometer readings of the latest samples (microtesla), newest at <see cref="_fieldHead"/>; the
    /// field is stable while they agree (<see cref="FieldStable"/>).</summary>
    readonly Vector3[] _fields = new Vector3[FieldWindow];
    int _fieldHead, _fieldCount;

    /// <summary>Readings the stability looks at, and how far one may be from their mean (microtesla): a magnet or a
    /// metal object near the unit moves the field by more.</summary>
    const int FieldWindow = 8;
    const float FieldTolerance = 3f;

    /// <summary>The magnitude range of the Earth's field (about 25 to 65 uT), with some margin: outside it the reading is
    /// disturbed, not the Earth's.</summary>
    const float FieldMin = 15f, FieldMax = 90f;

    /// <summary>SceMotionMagnFieldStability (psp2/motion.h).</summary>
    public const byte FieldUnstable = 0, FieldStable = 2;

    public MotionFusion Fusion { get; } = new();

    /// <summary>True once a host source with sensors delivered samples (the state is no longer the at-rest one).</summary>
    public bool Live { get { lock (_lock) return _live; } }

    /// <summary>Pulls new samples from <paramref name="source"/> and integrates them. Each sample gets a guest
    /// timestamp: <paramref name="guestNowUs"/> for the newest, older ones spaced by their host time differences.</summary>
    public void Poll(IMotionSource? source, long guestNowUs)
    {
        lock (_lock)
        {
            if (source is not { HasSensors: true })
            {
                // the controller went away: back to rest, and the next one starts a fresh history
                if (_live)
                {
                    _live = false;
                    _count = 0;
                    _lastHostUs = 0;
                    _fieldCount = 0;
                }

                return;
            }
            // a source may hand a backlog over in several pieces
            int n = 0;
            while (n < _buffer.Length)
            {
                int got = source.ReadSamples(_buffer.AsSpan(n));
                if (got <= 0) break;
                n += got;
            }
            if (n == 0) return;
            ulong newest = _buffer[n - 1].TimestampUs;
            for (int i = 0; i < n; i++)
            {
                var s = _buffer[i];
                // over a second back in time: another source (controller <-> keyboard) with its own clock took over
                if (_lastHostUs != 0 && s.TimestampUs + 1_000_000 < _lastHostUs) _lastHostUs = 0;
                if (_lastHostUs != 0 && s.TimestampUs <= _lastHostUs) continue; // duplicate / out of order
                float dt = _lastHostUs == 0 ? 0 : (s.TimestampUs - _lastHostUs) / 1e6f;
                _lastHostUs = s.TimestampUs;
                Fusion.Update(s.Acceleration, s.AngularVelocity, dt);
                if (s.MagneticField != Vector3.Zero)
                {
                    _fields[_fieldHead] = s.MagneticField;
                    _fieldHead = (_fieldHead + 1) % FieldWindow;
                    if (_fieldCount < FieldWindow)
                    {
                        _fieldCount++;
                    }
                }
                else
                {
                    // a reading without the field (the source has no magnetometer now): no heading
                    _fieldCount = 0;
                }

                long ts = guestNowUs - (long)Math.Min(newest - s.TimestampUs, 1_000_000UL);
                if (ts <= _lastGuestUs) ts = _lastGuestUs + 1; // strictly increasing for the guest
                _lastGuestUs = ts;
                _ring[_head] = new Record(Fusion.Acceleration, s.AngularVelocity, ts, ++_counter);
                _head = (_head + 1) % MaxRecords;
                if (_count < MaxRecords) _count++;
                _live = true;
            }
        }
    }

    public void Reset() { lock (_lock) Fusion.Reset(); }
    public void RotateYaw(float radians) { lock (_lock) Fusion.RotateYaw(radians); }

    /// <summary>Basic orientation as of the last update (at rest: lying flat, screen up).</summary>
    public Vector3 BasicOrientation { get { lock (_lock) return _live ? Fusion.BasicOrientation : new Vector3(0, 0, 1); } }

    /// <summary>The acceleration (G) and angular velocity (rad/s) sceMotionGetState reports now (at rest: lying flat,
    /// screen up, still).</summary>
    public (Vector3 Acceleration, Vector3 AngularVelocity) CurrentMotion()
    {
        lock (_lock)
        {
            if (!_live)
            {
                return (new Vector3(0, 0, -1), Vector3.Zero);
            }

            return (Fusion.Acceleration, Fusion.AngularVelocity);
        }
    }

    /// <summary>The magnetometer readings agree with each other and have the Earth field's magnitude (under the lock).</summary>
    bool FieldIsStable(out Vector3 mean)
    {
        mean = Vector3.Zero;
        if (_fieldCount < FieldWindow)
        {
            return false;
        }

        for (int i = 0; i < _fieldCount; i++)
        {
            mean += _fields[i];
        }

        mean /= _fieldCount;
        float magnitude = mean.Length();
        if (magnitude is < FieldMin or > FieldMax)
        {
            return false;
        }

        for (int i = 0; i < _fieldCount; i++)
        {
            if (Vector3.Distance(_fields[i], mean) > FieldTolerance)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The NED matrix (SceMotionState +120): the device axes in the North-East-Down frame, in the layout of
    /// <see cref="MotionFusion.ToMatrix"/> (row x, y, z = the device x, y, z axes; columns north, east, down). Down is
    /// the gravity the accelerometer reads, east is down x the field, north is east x down (the field's dip drops out).
    /// False when gravity and the field are parallel (no heading).
    /// </summary>
    public static bool NedMatrix(Vector3 gravity, Vector3 field, Span<float> m)
    {
        if (gravity.LengthSquared() < 1e-6f || field.LengthSquared() < 1e-6f)
        {
            return false;
        }

        Vector3 down = Vector3.Normalize(gravity);
        Vector3 east = Vector3.Cross(down, field);
        if (east.LengthSquared() < 1e-6f)
        {
            return false;
        }

        east = Vector3.Normalize(east);
        Vector3 north = Vector3.Cross(east, down);
        m.Clear();
        m[0] = north.X;
        m[1] = east.X;
        m[2] = down.X;
        m[4] = north.Y;
        m[5] = east.Y;
        m[6] = down.Y;
        m[8] = north.Z;
        m[9] = east.Z;
        m[10] = down.Z;
        m[15] = 1;
        return true;
    }

    /// <summary>Writes SceMotionState (0xF8 bytes). With <paramref name="magnetometer"/> on
    /// (sceMotionMagnetometerOn) and a stable field from the source, the NED matrix comes from gravity and the field and
    /// magFieldStability is STABLE; otherwise the NED matrix is the rotation matrix and the field UNSTABLE.</summary>
    public void WriteState(uint state, long guestNowUs, bool magnetometer = false)
    {
        lock (_lock)
        {
            GuestMemory.Fill(state, 0, StateSize);
            if (!_live)
            {
                WriteRestState(state, guestNowUs);
                return;
            }
            var last = _ring[(_head + MaxRecords - 1) % MaxRecords];
            var f = Fusion;
            GuestMemory.W32(state, (uint)last.TimestampUs);              // timestamp
            WriteVector(state + 4, f.Acceleration);                      // acceleration (G)
            WriteVector(state + 16, f.AngularVelocity);                  // angularVelocity (rad/s)
            var q = f.Orientation;                                       // deviceQuat x, y, z, w
            GuestMemory.Write(state + 40, q.X);
            GuestMemory.Write(state + 44, q.Y);
            GuestMemory.Write(state + 48, q.Z);
            GuestMemory.Write(state + 52, q.W);
            Span<float> m = stackalloc float[16];
            MotionFusion.ToMatrix(q, m);
            Span<float> ned = stackalloc float[16];
            bool stable = magnetometer && FieldIsStable(out Vector3 field) && NedMatrix(f.Acceleration, field, ned);
            for (int i = 0; i < 16; i++)
            {
                GuestMemory.Write(state + 56 + (uint)i * 4, m[i]);       // rotationMatrix
                // nedMatrix: from gravity and the field; without a stable field the rotation matrix, as Vita3K
                GuestMemory.Write(state + 120 + (uint)i * 4, stable ? ned[i] : m[i]);
            }
            WriteVector(state + 188, f.BasicOrientation);                // basicOrientation
            GuestMemory.Write(state + 200, (ulong)last.TimestampUs);     // hostTimestamp
            GuestMemory.Write(state + 244, stable ? FieldStable : FieldUnstable); // magFieldStability
        }
    }

    /// <summary>The historic at-rest state: lying flat (acceleration z = -1, basic orientation z = 1) with identity
    /// quaternion and matrices, as before motion sources existed (Vita3K's fallback is similar).</summary>
    static void WriteRestState(uint state, long nowUs)
    {
        GuestMemory.W32(state, (uint)nowUs);
        GuestMemory.Write(state + 12, -1.0f);         // acceleration z
        GuestMemory.Write(state + 52, 1.0f);          // quaternion w
        for (uint i = 0; i < 4; i++)
        {
            GuestMemory.Write(state + 56 + i * 20, 1.0f);   // rotation matrix identity
            GuestMemory.Write(state + 120 + i * 20, 1.0f);  // NED matrix identity
        }
        GuestMemory.Write(state + 196, 1.0f);         // basic orientation z
        GuestMemory.Write(state + 200, nowUs);
    }

    /// <summary>
    /// Writes <paramref name="numRecords"/> SceMotionSensorState records (0x40 each), oldest first, newest last.
    /// Live: the most recent raw samples; when fewer exist the oldest is repeated with earlier timestamps/counters.
    /// At rest: synthetic records whose counter advances once per call.
    /// </summary>
    public void WriteSensorStates(uint states, int numRecords, long guestNowUs)
    {
        lock (_lock)
        {
            if (!_live)
            {
                WriteRestSensorStates(states, numRecords, guestNowUs, _restCounter++);
                return;
            }
            for (int i = 0; i < numRecords; i++)
            {
                int age = numRecords - 1 - i;                 // 0 = newest
                int have = Math.Min(age, _count - 1);
                var r = _ring[(_head + MaxRecords - 1 - have) % MaxRecords];
                int missing = age - have;                     // records older than the history: extrapolate
                long ts = r.TimestampUs - missing * SampleIntervalUs;
                WriteSensorRecord(states + (uint)i * SensorStateSize, r.Accel, r.Gyro, ts, r.Counter - (uint)missing);
            }
        }
    }

    internal static void WriteRestSensorStates(uint states, int numRecords, long nowUs, uint counter)
    {
        for (int i = 0; i < numRecords; i++)
        {
            int age = numRecords - 1 - i;
            WriteSensorRecord(states + (uint)i * SensorStateSize, new Vector3(0, 0, -1), Vector3.Zero, nowUs - age * SampleIntervalUs, counter - (uint)age);
        }
    }

    static void WriteSensorRecord(uint p, Vector3 accel, Vector3 gyro, long ts, uint counter)
    {
        GuestMemory.Fill(p, 0, SensorStateSize);
        WriteVector(p, accel);                           // accelerometer (G)
        WriteVector(p + 0x0C, gyro);                     // gyro (rad/s)
        GuestMemory.W32(p + 0x24, (uint)ts);             // timestamp (us)
        GuestMemory.W32(p + 0x28, counter);              // counter
        GuestMemory.Write(p + 0x30, (ulong)ts);          // hostTimestamp
    }

    static void WriteVector(uint p, Vector3 v)
    {
        GuestMemory.Write(p, v.X);
        GuestMemory.Write(p + 4, v.Y);
        GuestMemory.Write(p + 8, v.Z);
    }
}
