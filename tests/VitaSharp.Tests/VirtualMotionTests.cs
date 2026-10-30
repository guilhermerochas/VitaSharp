using System.Numerics;
using VitaSharp.Core.Hle.Motion;
using VitaSharp.Core.Host;
using VitaSharp.Core.Input;

namespace VitaSharp.Tests;

/// <summary>Keyboard / mouse motion (<see cref="VirtualMotion"/>): ramps, clamping, re-centre, the samples it
/// produces and their agreement with SceMotion's fusion; the default tilt keys and input.json upgrade.</summary>
public sealed class VirtualMotionTests
{
    const float Deg = MathF.PI / 180;
    const long Step = VirtualMotion.StepUs;

    static void AssertNear(Vector3 expected, Vector3 actual, float tol = 0.01f)
        => Assert.True(Vector3.Distance(expected, actual) < tol, $"expected {expected}, got {actual}");

    /// <summary>Runs the simulation for <paramref name="seconds"/>, feeding every sample to <paramref name="sink"/>.</summary>
    static long Run(VirtualMotion vm, long t, double seconds, Action<MotionSample>? sink = null)
    {
        var buf = new MotionSample[64];
        long end = t + (long)(seconds * 1e6);
        for (; t < end; t += 16_000) // one SceMotion poll per 60 Hz frame
        {
            int n;
            while ((n = vm.Generate(t, buf)) > 0)
                for (int i = 0; i < n; i++) sink?.Invoke(buf[i]);
        }
        return t;
    }

    static VirtualMotion Hold(float fwd = 0, float back = 0, float left = 0, float right = 0, float tl = 0, float tr = 0, TiltSettings? s = null)
    {
        var vm = new VirtualMotion(s);
        vm.SetControls(fwd, back, left, right, tl, tr, false);
        return vm;
    }

    [Fact]
    public void UnusedUntilAControlMoves()
    {
        var vm = new VirtualMotion();
        Assert.False(vm.Used);
        vm.SetControls(0, 0, 0, 0, 0, 0, false);
        Assert.False(vm.Used);
        vm.SetControls(1, 0, 0, 0, 0, 0, false);
        Assert.True(vm.Used);
        var m = new VirtualMotion();
        m.AddMouse(3, 0);
        Assert.True(m.Used);
    }

    [Fact]
    public void FirstSampleIsTheNeutralPoseFlatScreenUp()
    {
        var buf = new MotionSample[4];
        Assert.Equal(1, new VirtualMotion().Generate(1_000_000, buf));
        AssertNear(new Vector3(0, 0, -1), buf[0].Acceleration);
        Assert.Equal(Vector3.Zero, buf[0].AngularVelocity);
    }

    [Fact]
    public void HeldKeyRampsSmoothlyToTheMaxAngleWithoutOvershoot()
    {
        var vm = Hold(fwd: 1);
        float prev = 0, at200 = 0, at450 = 0, maxRate = 0;
        long t = 0;
        Run(vm, 0, 1.5, s =>
        {
            float pitch = vm.Angles.Pitch;
            Assert.True(pitch <= prev + 1e-4f, "forward lowers the top edge monotonically");
            Assert.True(pitch >= -30.0001f, $"overshoot: {pitch}");
            prev = pitch;
            t = (long)s.TimestampUs;
            if (t <= 200_000) at200 = pitch;
            if (t <= 450_000) at450 = pitch;
            maxRate = Math.Max(maxRate, MathF.Abs(s.AngularVelocity.X));
        });
        Assert.InRange(at200, -26f, -10f);          // ramping, not a jump
        Assert.InRange(at450, -30f, -28.5f);        // ~95% within 0.45 s
        Assert.InRange(maxRate / Deg, 80f, 200f);   // peak ~130 degrees/s
        Assert.Equal(-30f, vm.Angles.Pitch, 0.01f);
        // gravity of a Vita whose top edge went 30 degrees down: y component negative... towards the top edge
        AssertNear(MotionAxes.TiltGravity(-30 * Deg, 0), VirtualMotion.Gravity(vm.Pose));
    }

    [Fact]
    public void ReleaseEasesBackToNeutral()
    {
        var vm = Hold(right: 1);
        long t = Run(vm, 0, 1);
        Assert.Equal(30f, vm.Angles.Roll, 0.05f);
        vm.SetControls(0, 0, 0, 0, 0, 0, false);
        float mid = 0;
        var last = default(MotionSample);
        Run(vm, t, 1, s => { if ((long)s.TimestampUs - t <= 150_000) mid = vm.Angles.Roll; last = s; });
        Assert.InRange(mid, 3f, 28f);
        Assert.Equal(0f, vm.Angles.Roll, 0.05f);
        AssertNear(new Vector3(0, 0, -1), last.Acceleration);
        Assert.True(last.AngularVelocity.Length() < 0.01f);
    }

    [Fact]
    public void MaxAngleAndSpeedAreHonouredAndClamped()
    {
        var vm = Hold(back: 1, s: new TiltSettings { MaxAngle = 45, Speed = 2 });
        Run(vm, 0, 0.3);
        Assert.Equal(45f, vm.Angles.Pitch, 1f); // twice as fast: settled in 0.3 s
        var wild = Hold(back: 1, s: new TiltSettings { MaxAngle = 500 });
        Run(wild, 0, 3);
        Assert.Equal(85f, wild.Angles.Pitch, 0.1f);
        // opposite keys cancel
        var both = Hold(fwd: 1, back: 1);
        Run(both, 0, 0.5);
        Assert.Equal(0f, both.Angles.Pitch);
    }

    [Fact]
    public void GyroIntegratesToTheOrientationChange()
    {
        // the samples are self-consistent: integrating the gyro reproduces the pose
        var vm = Hold(fwd: 1, left: 1, tl: 1);
        var q = Quaternion.Identity;
        ulong? last = null;
        Run(vm, 0, 1, s =>
        {
            if (last is { } l)
            {
                float dt = (s.TimestampUs - l) / 1e6f;
                var w = s.AngularVelocity;
                if (w != Vector3.Zero) q = Quaternion.Normalize(q * Quaternion.CreateFromAxisAngle(Vector3.Normalize(w), w.Length() * dt));
            }
            last = s.TimestampUs;
            AssertNear(VirtualMotion.Gravity(q), s.Acceleration, 0.002f);
        });
        Assert.True(MathF.Abs(Quaternion.Dot(q, vm.Pose)) > 0.99999f);
        Assert.Equal(-30f, vm.Angles.Pitch, 0.05f);
        Assert.Equal(-30f, vm.Angles.Roll, 0.05f);
        Assert.Equal(30f, vm.Angles.Yaw, 0.05f);
    }

    [Fact]
    public void FusionAgreesWithThePose()
    {
        // SceMotion's quaternion / basic orientation follow the virtual device, yaw included
        var vm = Hold(back: 1, tr: 1, s: new TiltSettings { MaxAngle = 60 });
        var f = new MotionFusion();
        ulong? last = null;
        Run(vm, 0, 1.5, s => { f.Update(s.Acceleration, s.AngularVelocity, last is { } l ? (s.TimestampUs - l) / 1e6f : 0); last = s.TimestampUs; });
        // fusion world: flat = -90 degrees about x
        var expected = Quaternion.CreateFromAxisAngle(Vector3.UnitX, -MathF.PI / 2) * vm.Pose;
        Assert.True(MathF.Abs(Quaternion.Dot(expected, f.Orientation)) > 0.999f, $"expected {expected}, got {f.Orientation}");
        // 60 degrees up from flat stays within the hysteresis (45 + 20 degrees): still screen up
        Assert.Equal(new Vector3(0, 0, 1), f.BasicOrientation);
        var up = Hold(back: 1, s: new TiltSettings { MaxAngle = 80 });
        var g = new MotionFusion();
        last = null;
        Run(up, 0, 1.5, s => { g.Update(s.Acceleration, s.AngularVelocity, last is { } l ? (s.TimestampUs - l) / 1e6f : 0); last = s.TimestampUs; });
        Assert.Equal(new Vector3(0, 1, 0), g.BasicOrientation); // nearly upright: top edge up
    }

    [Fact]
    public void MouseLookRotatesAndRecentres()
    {
        var vm = new VirtualMotion();
        vm.AddMouse(100, -50); // right and up: 20 degrees right turn, 10 degrees top edge up
        long t = Run(vm, 0, 0.5);
        Assert.Equal(-20f, vm.Angles.Yaw, 0.01f);
        Assert.Equal(10f, vm.Angles.Pitch, 0.01f);
        // the offset stays after the mouse stops
        t = Run(vm, t, 0.5);
        Assert.Equal(-20f, vm.Angles.Yaw, 0.01f);
        // pitch is clamped
        vm.AddMouse(0, -100_000);
        t = Run(vm, t, 1);
        Assert.Equal(80f, vm.Angles.Pitch, 0.01f);
        // re-centre (rising edge) goes back smoothly, never faster than the gyro clamp
        vm.SetControls(0, 0, 0, 0, 0, 0, true);
        float maxRate = 0;
        Run(vm, t, 1, s => maxRate = Math.Max(maxRate, s.AngularVelocity.Length()));
        Assert.Equal(0f, vm.Angles.Pitch, 0.01f);
        Assert.Equal(0f, vm.Angles.Yaw, 0.01f);
        Assert.True(maxRate <= VirtualMotion.MouseMaxRate * Deg * 1.01f && maxRate < MotionFusion.GyroMax, $"rate {maxRate}");
        // invert Y + sensitivity
        var inv = new VirtualMotion(new TiltSettings { MouseInvertY = true, MouseSensitivity = 2 });
        inv.AddMouse(0, -25);
        Run(inv, 0, 0.5);
        Assert.Equal(-10f, inv.Angles.Pitch, 0.01f);
    }

    [Fact]
    public void LongGapIsCappedAndSamplesAreOrdered()
    {
        var vm = Hold(fwd: 1);
        var buf = new MotionSample[2048];
        vm.Generate(0, buf);
        int n = vm.Generate(10_000_000, buf); // the game did not poll for 10 s
        Assert.Equal(250, n);
        for (int i = 1; i < n; i++) Assert.Equal(buf[i - 1].TimestampUs + (ulong)Step, buf[i].TimestampUs);
        Assert.Equal(10_000_000UL, buf[n - 1].TimestampUs);
        Assert.Equal(-30f, vm.Angles.Pitch, 0.01f);
        // a short destination hands the rest over on the next call
        var small = Hold(fwd: 1);
        small.Generate(0, buf);
        Assert.Equal(4, small.Generate(40_000, buf.AsSpan(0, 4)));
        Assert.Equal(6, small.Generate(40_000, buf));
    }

    [Fact]
    public void DefaultTiltKeysAreFreeAndDriveTheMotion()
    {
        var p = DefaultMappings.Keyboard();
        var motionKeys = new HashSet<string>();
        foreach (var c in VitaControls.All)
        {
            var keys = p.Get(c).Select(b => b.Key!).ToList();
            if (VitaControls.IsMotion(c)) { Assert.Equal(2, keys.Count); keys.ForEach(k => Assert.True(motionKeys.Add(k), $"{k} bound twice")); }
        }
        foreach (var c in VitaControls.All.Where(c => !VitaControls.IsMotion(c)))
            foreach (var b in p.Get(c)) Assert.DoesNotContain(b.Key!, motionKeys);
        foreach (var k in new[] { "F8", "F9", "F10", "F11", "Home", "Escape" }) Assert.DoesNotContain(k, motionKeys);
        // keypad 8 = tilt forward, through the compiled profile; tilt keys press no Vita button
        var cp = new CompiledProfile(p);
        Assert.True(cp.HasMotionBindings);
        Assert.False(new CompiledProfile(DefaultMappings.Gamepad("g", "n", ControllerKind.Xbox)).HasMotionBindings);
        var keysHeld = new HashSet<string> { "Keypad8", "H" };
        Assert.Equal(VitaButtons.None, InputMapper.Map(cp, null, keysHeld).State.Buttons);
        var vm = new VirtualMotion();
        vm.SetControls(cp, null, keysHeld);
        Run(vm, 0, 1);
        Assert.Equal(-30f, vm.Angles.Pitch, 0.05f);
        Assert.Equal(30f, vm.Angles.Roll, 0.05f);
    }

    [Fact]
    public void OldKeyboardProfilesGetTheFreeTiltKeys()
    {
        // a version 1 input.json whose keyboard profile uses T for Cross: T stays Cross, keypad 8 tilts forward
        var old = DefaultMappings.Keyboard();
        foreach (var c in VitaControls.All.Where(VitaControls.IsMotion)) old.Set(c, null);
        old.Set(VitaControl.Cross, InputBinding.OfKey("T"));
        var store = new InputProfileStore();
        store.Put(old);
        store.Config.Version = 1;
        var path = Path.Combine(Path.GetTempPath(), $"vitasharp_input_{Guid.NewGuid():N}.json");
        File.WriteAllText(path, InputProfileStore.Serialize(store.Config));
        InputProfileStore loaded;
        try { loaded = InputProfileStore.Load(path); }
        finally { File.Delete(path); }
        Assert.Equal(1, loaded.Config.Version);
        var p = loaded.KeyboardProfile();
        Assert.Equal(new[] { "key:Keypad8" }, p.Bindings[nameof(VitaControl.TiltForward)]);
        Assert.Equal(new[] { "key:T" }, p.Bindings[nameof(VitaControl.Cross)]);
        Assert.Equal(new[] { "key:Keypad4", "key:F" }, p.Bindings[nameof(VitaControl.TiltLeft)]);
        // version 2: what the player saved (tilt keys removed) is kept
        loaded.Put(old);
        Assert.Equal(InputConfig.CurrentVersion, loaded.Config.Version);
        Assert.False(loaded.KeyboardProfile().Bindings.ContainsKey(nameof(VitaControl.TiltForward)));
        // settings round trip
        old.MotionSource = MotionSourceMode.KeyboardMouse;
        old.Tilt.MaxAngle = 42;
        loaded.Put(old);
        var back = InputProfileStore.Deserialize(InputProfileStore.Serialize(loaded.Config))!.Keyboard!;
        Assert.Equal(MotionSourceMode.KeyboardMouse, back.MotionSource);
        Assert.Equal(42f, back.Tilt.MaxAngle);
        Assert.Contains("\"KeyboardMouse\"", InputProfileStore.Serialize(loaded.Config));
    }
}
