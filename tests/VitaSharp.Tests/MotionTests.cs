using System.Numerics;
using VitaSharp.Core.Hle.Motion;
using VitaSharp.Core.Host;
using VitaSharp.Core.Input;
using VitaSharp.Core.Memory;

namespace VitaSharp.Tests;

/// <summary>SceMotion: sensor fusion maths, axis conversions and the guest structures, fed with synthetic samples.</summary>
[Collection("guest")]
public sealed class MotionTests(GuestMemoryFixture fx)
{
    const float Dt = 0.004f; // 250 Hz, SDL's polling rate
    static readonly Vector3 Flat = new(0, 0, -1), Upright = new(0, -1, 0);

    static void AssertNear(Vector3 expected, Vector3 actual, float tol = 0.02f)
        => Assert.True(Vector3.Distance(expected, actual) < tol, $"expected {expected}, got {actual}");

    static void AssertSameRotation(Quaternion expected, Quaternion actual, float tol = 0.02f)
        => Assert.True(MathF.Abs(Quaternion.Dot(expected, actual)) > 1 - tol, $"expected {expected}, got {actual}");

    /// <summary>Device axis <paramref name="axis"/> expressed in the world.</summary>
    static Vector3 World(MotionFusion f, Vector3 axis) => Vector3.Transform(axis, f.Orientation);

    static void Feed(MotionFusion f, Vector3 accel, Vector3 gyro, int samples)
    {
        for (int i = 0; i < samples; i++) f.Update(accel, gyro, Dt);
    }

    [Fact]
    public void AtRestFlatScreenPointsUp()
    {
        var f = new MotionFusion();
        Feed(f, Flat, Vector3.Zero, 500);
        // lying flat = -90 degrees about x from the upright reference (where Vita3K's filter settles too)
        AssertSameRotation(Quaternion.CreateFromAxisAngle(Vector3.UnitX, -MathF.PI / 2), f.Orientation);
        AssertNear(Vector3.UnitY, World(f, Vector3.UnitZ));  // screen normal = world up
        AssertNear(-Vector3.UnitZ, World(f, Vector3.UnitY)); // top edge points away from the player
        Assert.Equal(new Vector3(0, 0, 1), f.BasicOrientation);
        Assert.Equal(Vector3.Zero, f.AngularVelocity);
    }

    [Fact]
    public void UprightIsIdentity()
    {
        var f = new MotionFusion();
        Feed(f, Upright, Vector3.Zero, 100);
        AssertSameRotation(Quaternion.Identity, f.Orientation);
        Assert.Equal(new Vector3(0, 1, 0), f.BasicOrientation);
    }

    [Fact]
    public void GyroYawIntegrates()
    {
        // lying flat, turning counter-clockwise (seen from above) at 90 degrees/s for 1 s
        var f = new MotionFusion();
        Feed(f, Flat, Vector3.Zero, 10);
        Feed(f, Flat, new Vector3(0, 0, MathF.PI / 2), 250);
        AssertNear(-Vector3.UnitZ, World(f, Vector3.UnitX), 0.05f); // right edge now points away from the player
        AssertNear(Vector3.UnitY, World(f, Vector3.UnitZ));         // still screen up
        AssertNear(new Vector3(0, 0, MathF.PI / 2), f.AngularVelocity, 1e-4f);
    }

    [Fact]
    public void GyroPitchWithMatchingGravityReachesUpright()
    {
        // raise the top edge 90 degrees in 1 s; the accelerometer follows the true attitude
        var f = new MotionFusion();
        Feed(f, Flat, Vector3.Zero, 10);
        for (int i = 1; i <= 250; i++)
        {
            float angle = MathF.PI / 2 * i / 250;
            f.Update(MotionAxes.TiltGravity(angle, 0), new Vector3(MathF.PI / 2, 0, 0), Dt);
        }
        Feed(f, Upright, Vector3.Zero, 50);
        AssertSameRotation(Quaternion.Identity, f.Orientation);
        Assert.Equal(new Vector3(0, 1, 0), f.BasicOrientation);
    }

    [Fact]
    public void TiltCorrectionPullsDriftBackOnlyWhenEnabled()
    {
        // the gyro claims a pitch the accelerometer does not see
        var on = new MotionFusion();
        var off = new MotionFusion { TiltCorrection = false };
        foreach (var f in new[] { on, off })
        {
            Feed(f, Flat, Vector3.Zero, 10);
            Feed(f, Flat, new Vector3(0.5f, 0, 0), 250);   // 0.5 rad of fake rotation
            Feed(f, Flat, Vector3.Zero, 2500);              // 10 s at rest
        }
        AssertNear(Vector3.UnitY, World(on, Vector3.UnitZ), 0.05f);
        Assert.True(Vector3.Distance(Vector3.UnitY, World(off, Vector3.UnitZ)) > 0.3f);
    }

    [Fact]
    public void DeadbandHidesTinyRates()
    {
        var f = new MotionFusion { GyroBiasCorrection = false };
        f.Update(Flat, new Vector3(0.02f, 0, 0), Dt);
        Assert.Equal(Vector3.Zero, f.AngularVelocity);
        f.Deadband = false;
        f.Update(Flat, new Vector3(0.02f, 0, 0), Dt);
        AssertNear(new Vector3(0.02f, 0, 0), f.AngularVelocity, 1e-6f);
    }

    [Fact]
    public void GyroBiasIsLearnedWhileStill()
    {
        var f = new MotionFusion { Deadband = false };
        var bias = new Vector3(0.05f, -0.03f, 0.02f);
        Feed(f, Flat, bias, 60000); // 4 minutes still
        AssertNear(bias, f.GyroBias, 0.005f);
        Assert.True(f.AngularVelocity.Length() < 0.005f);
    }

    [Fact]
    public void BasicOrientationHysteresis()
    {
        var f = new MotionFusion(); // threshold 20: switches 65 degrees away from the current axis
        static Vector3 Pitch(float deg) => MotionAxes.TiltGravity(deg * MathF.PI / 180, 0);
        f.Update(Pitch(0), Vector3.Zero, Dt);
        Assert.Equal(new Vector3(0, 0, 1), f.BasicOrientation);
        f.Update(Pitch(60), Vector3.Zero, Dt);
        Assert.Equal(new Vector3(0, 0, 1), f.BasicOrientation);
        f.Update(Pitch(70), Vector3.Zero, Dt);
        Assert.Equal(new Vector3(0, 1, 0), f.BasicOrientation);
        f.Update(Pitch(30), Vector3.Zero, Dt); // 60 degrees from +y: stays
        Assert.Equal(new Vector3(0, 1, 0), f.BasicOrientation);
        f.Update(Pitch(20), Vector3.Zero, Dt);
        Assert.Equal(new Vector3(0, 0, 1), f.BasicOrientation);
        // right edge up (psp2/motion.h example): +x
        f.Update(new Vector3(-1, 0, 0), Vector3.Zero, Dt);
        Assert.Equal(new Vector3(1, 0, 0), f.BasicOrientation);
        f.Update(new Vector3(0, 0, 1), Vector3.Zero, Dt); // screen down
        Assert.Equal(new Vector3(0, 0, -1), f.BasicOrientation);
    }

    [Fact]
    public void ResetAndRotateYaw()
    {
        var f = new MotionFusion();
        Feed(f, Flat, Vector3.Zero, 10);
        Feed(f, Flat, new Vector3(0, 0, 1f), 250); // turned 1 rad
        f.Reset();
        AssertSameRotation(Quaternion.CreateFromAxisAngle(Vector3.UnitX, -MathF.PI / 2), f.Orientation);
        f.RotateYaw(MathF.PI / 2);
        AssertNear(-Vector3.UnitZ, World(f, Vector3.UnitX));
        AssertNear(Vector3.UnitY, World(f, Vector3.UnitZ));
    }

    [Fact]
    public void RotationMatrixColumnsAreDeviceAxes()
    {
        var q = Quaternion.Normalize(new Quaternion(0.1f, 0.7f, -0.2f, 0.6f));
        Span<float> m = stackalloc float[16];
        MotionFusion.ToMatrix(q, m);
        AssertNear(Vector3.Transform(Vector3.UnitX, q), new Vector3(m[0], m[1], m[2]), 1e-5f);
        AssertNear(Vector3.Transform(Vector3.UnitY, q), new Vector3(m[4], m[5], m[6]), 1e-5f);
        AssertNear(Vector3.Transform(Vector3.UnitZ, q), new Vector3(m[8], m[9], m[10]), 1e-5f);
        Assert.Equal(1f, m[15]);
    }

    // ------------------------------------------------------------------ axis conversion

    [Fact]
    public void GamepadAxesNatural()
    {
        // SDL: controller lying flat reads +g on y; Vita lying flat reads -1 G on z
        var s = MotionAxes.FromGamepad(1, new Vector3(0, MotionAxes.StandardGravity, 0), Vector3.Zero);
        AssertNear(Flat, s.Acceleration, 1e-5f);
        // far edge raised until the face looks at the player (SDL -z up): Vita upright
        s = MotionAxes.FromGamepad(1, new Vector3(0, 0, -MotionAxes.StandardGravity), Vector3.Zero);
        AssertNear(Upright, s.Acceleration, 1e-5f);
        // turning about the controller's up axis = turning about the screen normal
        s = MotionAxes.FromGamepad(1, default, new Vector3(0, 2, 0));
        AssertNear(new Vector3(0, 0, 2), s.AngularVelocity, 1e-5f);
        // tilting the far edge up (+x rotation) = raising the Vita's top edge
        s = MotionAxes.FromGamepad(1, default, new Vector3(1, 0, 0));
        AssertNear(new Vector3(1, 0, 0), s.AngularVelocity, 1e-5f);
        // the conversion is a proper rotation (keeps handedness)
        s = MotionAxes.FromGamepad(1, default, new Vector3(0, 0, 3));
        AssertNear(new Vector3(0, -3, 0), s.AngularVelocity, 1e-5f);
    }

    [Fact]
    public void GamepadAxesFlatIsUpright()
    {
        var s = MotionAxes.FromGamepad(1, new Vector3(0, MotionAxes.StandardGravity, 0), new Vector3(0, 1, 0), MotionOrientation.FlatIsUpright);
        AssertNear(Upright, s.Acceleration, 1e-5f);
        AssertNear(new Vector3(0, 1, 0), s.AngularVelocity, 1e-5f);
    }

    [Fact]
    public void TiltGravitySigns()
    {
        AssertNear(Flat, MotionAxes.TiltGravity(0, 0), 1e-5f);
        AssertNear(Upright, MotionAxes.TiltGravity(MathF.PI / 2, 0), 1e-5f);    // top edge up
        AssertNear(new Vector3(1, 0, 0), MotionAxes.TiltGravity(0, MathF.PI / 2), 1e-5f); // right edge down
    }

    [Fact]
    public void ProfileMotionSettingsRoundTrip()
    {
        var c = new InputConfig();
        c.Controllers.Add(new ControllerProfile { Id = "abc", MotionEnabled = false, MotionOrientation = MotionOrientation.FlatIsUpright });
        var json = InputProfileStore.Serialize(c);
        Assert.Contains("\"motionOrientation\": \"FlatIsUpright\"", json);
        var back = InputProfileStore.Deserialize(json)!.Controllers[0];
        Assert.False(back.MotionEnabled);
        Assert.Equal(MotionOrientation.FlatIsUpright, back.MotionOrientation);
        Assert.False(back.Clone().MotionEnabled);
        // old input.json files (no motion fields) keep the gyro on
        Assert.True(InputProfileStore.Deserialize("{\"controllers\":[{\"id\":\"x\"}]}")!.Controllers[0].MotionEnabled);
    }

    // ------------------------------------------------------------------ guest structures

    sealed class FakeSource : IMotionSource
    {
        public readonly Queue<MotionSample> Pending = new();
        public bool HasSensors { get; set; } = true;
        public int ReadSamples(Span<MotionSample> dest)
        {
            int n = 0;
            while (n < dest.Length && Pending.TryDequeue(out var s)) dest[n++] = s;
            return n;
        }
    }

    static Vector3 ReadVec(uint p) => new(GuestMemory.Read<float>(p), GuestMemory.Read<float>(p + 4), GuestMemory.Read<float>(p + 8));

    [Fact]
    public void StateStructFromLiveSamples()
    {
        var dev = new MotionDevice();
        var src = new FakeSource();
        // 1 s flat, then 0.5 s turning about the screen normal at 1 rad/s
        ulong t = 1_000_000;
        for (int i = 0; i < 250; i++) src.Pending.Enqueue(new MotionSample(t += 4000, Flat, Vector3.Zero));
        for (int i = 0; i < 125; i++) src.Pending.Enqueue(new MotionSample(t += 4000, Flat, new Vector3(0, 0, 1f)));
        dev.Poll(src, 5_000_000);
        Assert.True(dev.Live);

        uint st = fx.Memory.Alloc(MotionDevice.StateSize, "motionstate");
        GuestMemory.Fill(st, 0xCC, MotionDevice.StateSize);
        dev.WriteState(st, 5_000_000);
        Assert.Equal(5_000_000u, GuestMemory.R32(st));                       // timestamp of the newest sample
        AssertNear(Flat, ReadVec(st + 4), 1e-5f);                             // acceleration
        AssertNear(new Vector3(0, 0, 1f), ReadVec(st + 16), 1e-5f);           // angularVelocity
        for (uint o = 28; o < 40; o++) Assert.Equal(0, GuestMemory.Read<byte>(st + o)); // reserved1
        var q = new Quaternion(ReadVec(st + 40), GuestMemory.Read<float>(st + 52));
        var expected = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.5f) * Quaternion.CreateFromAxisAngle(Vector3.UnitX, -MathF.PI / 2);
        AssertSameRotation(expected, q, 0.01f);
        // rotation matrix x row = device x in world; NED = same (no magnetometer)
        AssertNear(Vector3.Transform(Vector3.UnitX, q), ReadVec(st + 56), 1e-4f);
        Assert.Equal(1f, GuestMemory.Read<float>(st + 56 + 60));
        for (uint i = 0; i < 64; i += 4) Assert.Equal(GuestMemory.Read<float>(st + 56 + i), GuestMemory.Read<float>(st + 120 + i));
        Assert.Equal(new Vector3(0, 0, 1), ReadVec(st + 188));                // basicOrientation
        Assert.Equal(5_000_000UL, GuestMemory.Read<ulong>(st + 200));         // hostTimestamp
        Assert.Equal(0, GuestMemory.Read<byte>(st + 244));                    // magFieldStability UNSTABLE
        Assert.Equal(0, GuestMemory.Read<byte>(st + 0xF7));                   // whole 0xF8 written
    }

    [Fact]
    public void SensorRingKeepsLastSamples()
    {
        var dev = new MotionDevice();
        var src = new FakeSource();
        ulong t = 10_000;
        for (int i = 0; i < 100; i++) src.Pending.Enqueue(new MotionSample(t += 4000, new Vector3(i, 0, -1) / 100f, new Vector3(0, i, 0)));
        dev.Poll(src, 3_000_000);
        uint p = fx.Memory.Alloc(64 * MotionDevice.SensorStateSize, "sensorring");
        dev.WriteSensorStates(p, 64, 3_000_000);
        for (uint i = 0; i < 64; i++)
        {
            uint r = p + i * 0x40;
            int sample = 36 + (int)i; // the last 64 of 100, oldest first
            Assert.Equal(sample / 100f, GuestMemory.Read<float>(r), 5);
            Assert.Equal((float)sample, GuestMemory.Read<float>(r + 0x10)); // raw gyro y
            Assert.Equal((uint)(sample + 1), GuestMemory.R32(r + 0x28));    // counter: one per sample
            Assert.Equal(3_000_000u - (63 - i) * 4000, GuestMemory.R32(r + 0x24));
            Assert.Equal((ulong)GuestMemory.R32(r + 0x24), GuestMemory.Read<ulong>(r + 0x30));
        }

        // a short history: the oldest record is extrapolated backwards
        var dev2 = new MotionDevice();
        src.Pending.Enqueue(new MotionSample(1000, Flat, Vector3.Zero));
        src.Pending.Enqueue(new MotionSample(5000, Flat, Vector3.Zero));
        dev2.Poll(src, 1_000_000);
        dev2.WriteSensorStates(p, 4, 1_000_000);
        uint[] ts = [.. Enumerable.Range(0, 4).Select(i => GuestMemory.R32(p + (uint)i * 0x40 + 0x24))];
        Assert.Equal(1_000_000u, ts[3]);
        Assert.Equal(1_000_000u - 4000, ts[2]);
        Assert.True(ts[0] < ts[1] && ts[1] < ts[2]);
        Assert.Equal(2u, GuestMemory.R32(p + 3 * 0x40 + 0x28));
        Assert.Equal(1u, GuestMemory.R32(p + 2 * 0x40 + 0x28));
        Assert.Equal(0u, GuestMemory.R32(p + 1 * 0x40 + 0x28));
    }

    [Fact]
    public void WithoutSensorsTheDeviceRests()
    {
        var dev = new MotionDevice();
        var src = new FakeSource { HasSensors = false };
        src.Pending.Enqueue(new MotionSample(1000, Upright, Vector3.One));
        dev.Poll(src, 2_000_000);
        dev.Poll(null, 2_000_000);
        Assert.False(dev.Live);
        uint st = fx.Memory.Alloc(MotionDevice.StateSize, "reststate");
        dev.WriteState(st, 2_000_000);
        AssertNear(Flat, ReadVec(st + 4), 1e-6f);
        Assert.Equal(1f, GuestMemory.Read<float>(st + 52));
        Assert.Equal(new Vector3(0, 0, 1), ReadVec(st + 188));
        uint p = fx.Memory.Alloc(64 * 0x40, "restring");
        dev.WriteSensorStates(p, 64, 2_000_000);
        dev.WriteSensorStates(p, 64, 2_000_000);
        Assert.Equal(1u, GuestMemory.R32(p + 63 * 0x40 + 0x28)); // counter advances per call at rest
        Assert.Equal(-1f, GuestMemory.Read<float>(p + 8));

        // a controller that goes away returns the device to rest
        src.HasSensors = true;
        src.Pending.Enqueue(new MotionSample(2000, Upright, Vector3.Zero));
        dev.Poll(src, 2_100_000);
        Assert.True(dev.Live);
        src.HasSensors = false;
        dev.Poll(src, 2_200_000);
        Assert.False(dev.Live);
    }
}
