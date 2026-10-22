using VitaSharp.Core.Host;
using VitaSharp.Core.Input;

namespace VitaSharp.Tests;

public class InputMappingTests
{
    static PadSnapshot Gamepad() => new() { IsGamepad = true };

    static MappedInput Map(ControllerProfile p, PadSnapshot? pad = null, params string[] keys)
        => InputMapper.Map(new CompiledProfile(p), pad, keys.Length > 0 ? new HashSet<string>(keys) : null);

    [Theory]
    [InlineData("key:Space")]
    [InlineData("key:ShiftRight")]
    [InlineData("pad:a")]
    [InlineData("pad:touchpad")]
    [InlineData("pad:leftx-")]
    [InlineData("pad:righty+")]
    [InlineData("pad:lefttrigger")]
    [InlineData("joy:b12")]
    [InlineData("joy:a2-")]
    [InlineData("joy:a5~")]
    [InlineData("joy:h0.8")]
    public void BindingStringsRoundTrip(string s)
    {
        Assert.True(InputBinding.TryParse(s, out var b));
        Assert.Equal(s, b.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("pad:")]
    [InlineData("pad:nosuch")]
    [InlineData("joy:x1")]
    [InlineData("joy:h1")]
    [InlineData("mouse:1")]
    public void InvalidBindingsAreRejected(string s) => Assert.False(InputBinding.TryParse(s, out _));

    [Fact]
    public void ConfigJsonRoundTrip()
    {
        var cfg = new InputConfig { Keyboard = DefaultMappings.Keyboard() };
        var pad = DefaultMappings.Gamepad("030000004c050000e60c000000016800", "DualSense Wireless Controller", ControllerKind.PlayStation);
        pad.SwapConfirm = true;
        pad.LeftStick.Deadzone = 0.2f;
        pad.RightStick.InvertY = true;
        pad.RightStick.Sensitivity = 1.5f;
        pad.Set(VitaControl.PS, InputBinding.Of(PadButton.Guide));
        pad.TouchpadRearModifier = "pad:leftshoulder";
        cfg.Controllers.Add(pad);

        string json = InputProfileStore.Serialize(cfg);
        Assert.Contains("\"kind\": \"PlayStation\"", json);
        Assert.Contains("\"pad:a\"", json);
        var back = InputProfileStore.Deserialize(json)!;
        Assert.Equal(json, InputProfileStore.Serialize(back));
        var p = back.Controllers.Single();
        Assert.True(p.SwapConfirm);
        Assert.Equal(0.2f, p.LeftStick.Deadzone);
        Assert.True(p.RightStick.InvertY);
        Assert.Equal(1.5f, p.RightStick.Sensitivity);
        Assert.Equal("pad:leftshoulder", p.TouchpadRearModifier);
        Assert.Equal([InputBinding.Of(PadButton.Guide)], p.Get(VitaControl.PS));
    }

    [Fact]
    public void StoreSavesLoadsAndMatchesByGuidThenName()
    {
        var dir = Directory.CreateTempSubdirectory("vs-input-");
        try
        {
            var path = Path.Combine(dir.FullName, InputProfileStore.FileName);
            var store = new InputProfileStore(path);
            var p = DefaultMappings.Gamepad("guid-1", "Xbox Wireless Controller", ControllerKind.Xbox);
            p.Set(VitaControl.Cross, InputBinding.Of(PadButton.North));
            store.Put(p);
            var kb = DefaultMappings.Keyboard();
            kb.Set(VitaControl.Start, InputBinding.OfKey("P"));
            store.Put(kb);
            store.Save();

            var loaded = InputProfileStore.Load(path);
            Assert.Equal([InputBinding.Of(PadButton.North)], loaded.ProfileFor("GUID-1", "x", ControllerKind.Xbox, true).Get(VitaControl.Cross));
            // same controller seen with another GUID (Bluetooth instead of USB): matched by name
            Assert.Equal([InputBinding.Of(PadButton.North)], loaded.ProfileFor("guid-2", "Xbox Wireless Controller", ControllerKind.Xbox, true).Get(VitaControl.Cross));
            // unknown controller: default mapping
            Assert.Equal([InputBinding.Of(PadButton.South)], loaded.ProfileFor("guid-3", "Other", ControllerKind.Generic, true).Get(VitaControl.Cross));
            Assert.Equal([InputBinding.OfKey("P")], loaded.KeyboardProfile().Get(VitaControl.Start));

            loaded.Remove("guid-1");
            Assert.Null(loaded.FindSaved("guid-1", "Xbox Wireless Controller"));
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void MissingOrCorruptFileGivesDefaults()
    {
        var dir = Directory.CreateTempSubdirectory("vs-input-");
        try
        {
            var path = Path.Combine(dir.FullName, InputProfileStore.FileName);
            Assert.Empty(InputProfileStore.Load(path).Config.Controllers);
            File.WriteAllText(path, "{ not json");
            var s = InputProfileStore.Load(path);
            Assert.Empty(s.Config.Controllers);
            Assert.Equal([InputBinding.OfKey("X"), InputBinding.OfKey("Space")], s.KeyboardProfile().Get(VitaControl.Cross));
        }
        finally { dir.Delete(true); }
    }

    [Theory]
    [InlineData("78696e70757401000000000000000000", "Xbox Controller", ControllerKind.Xbox)] // GLFW/SDL XInput GUID
    [InlineData("030000005e040000ea02000000007801", "Controller", ControllerKind.Xbox)]
    [InlineData("030000004c050000cc09000000000000", "Controller", ControllerKind.PlayStation)] // DualShock 4 v2
    [InlineData("050000004c050000e60c000000810000", "Controller", ControllerKind.PlayStation)] // DualSense, Bluetooth
    [InlineData("030000007e0500000920000000000000", "Controller", ControllerKind.Nintendo)] // Switch Pro
    [InlineData("03000000790000000600000000000000", "USB Gamepad", ControllerKind.Generic)]
    [InlineData(null, "Wireless Controller", ControllerKind.PlayStation)]
    [InlineData(null, "8BitDo Pro 2 (Switch)", ControllerKind.Nintendo)]
    [InlineData("", "", ControllerKind.Generic)]
    public void ControllersAreClassifiedByVendorThenName(string? guid, string name, ControllerKind expected)
        => Assert.Equal(expected, ControllerClassifier.Classify(guid, name));

    [Fact]
    public void DefaultGamepadMappingIsPositionalForEveryFamily()
    {
        // the bottom face button is Cross whatever is printed on it (A on Xbox, Cross on PlayStation, B on Nintendo)
        foreach (var kind in new[] { ControllerKind.Xbox, ControllerKind.PlayStation, ControllerKind.Nintendo, ControllerKind.Generic })
        {
            var p = DefaultMappings.For("g", "n", kind, isGamepad: true);
            var pad = Gamepad();
            pad.Buttons[(int)PadButton.South] = true;
            Assert.Equal(VitaButtons.Cross, Map(p, pad).State.Buttons);
            pad.Buttons[(int)PadButton.South] = false;
            pad.Buttons[(int)PadButton.East] = true;
            Assert.Equal(VitaButtons.Circle, Map(p, pad).State.Buttons);
        }
        Assert.Equal("Cross", InputBinding.Of(PadButton.South).Describe(ControllerKind.PlayStation));
        Assert.Equal("A", InputBinding.Of(PadButton.South).Describe(ControllerKind.Xbox));
        Assert.Equal("B", InputBinding.Of(PadButton.South).Describe(ControllerKind.Nintendo));
    }

    [Fact]
    public void DefaultGamepadMatchesTheHistoricFixedMapping()
    {
        var p = DefaultMappings.Gamepad("g", "n", ControllerKind.Xbox);
        (PadButton Pad, VitaButtons Vita)[] table =
        [
            (PadButton.South, VitaButtons.Cross), (PadButton.East, VitaButtons.Circle), (PadButton.West, VitaButtons.Square),
            (PadButton.North, VitaButtons.Triangle), (PadButton.LeftShoulder, VitaButtons.LTrigger), (PadButton.RightShoulder, VitaButtons.RTrigger),
            (PadButton.Start, VitaButtons.Start), (PadButton.Back, VitaButtons.Select), (PadButton.DpadUp, VitaButtons.Up),
            (PadButton.DpadDown, VitaButtons.Down), (PadButton.DpadLeft, VitaButtons.Left), (PadButton.DpadRight, VitaButtons.Right),
            (PadButton.LeftStick, VitaButtons.L3), (PadButton.RightStick, VitaButtons.R3), (PadButton.Guide, VitaButtons.None),
        ];
        foreach (var (padButton, vita) in table)
        {
            var pad = Gamepad();
            pad.Buttons[(int)padButton] = true;
            Assert.Equal(vita, Map(p, pad).State.Buttons);
        }
        // triggers act as L/R once pressed past half way
        var t = Gamepad();
        t.Axes[(int)PadAxis.LeftTrigger] = 0.4f;
        Assert.Equal(VitaButtons.None, Map(p, t).State.Buttons);
        t.Axes[(int)PadAxis.LeftTrigger] = 0.8f;
        t.Axes[(int)PadAxis.RightTrigger] = 1f;
        Assert.Equal(VitaButtons.LTrigger | VitaButtons.RTrigger, Map(p, t).State.Buttons);
        // sticks: full deflection reaches the ends, rest is exactly 128
        var s = Gamepad();
        Assert.Equal(ControllerState.Neutral, Map(p, s).State);
        s.Axes[(int)PadAxis.LeftX] = -1f;
        s.Axes[(int)PadAxis.RightY] = 1f;
        var st = Map(p, s).State;
        Assert.Equal((byte)0, st.Lx);
        Assert.Equal((byte)128, st.Ly);
        Assert.Equal((byte)255, st.Ry);
    }

    [Fact]
    public void TriggersAsShouldersCanBeTurnedOff()
    {
        var p = DefaultMappings.Gamepad("g", "n", ControllerKind.Xbox);
        Assert.True(DefaultMappings.TriggersAsShoulders(p));
        DefaultMappings.SetTriggersAsShoulders(p, false);
        Assert.False(DefaultMappings.TriggersAsShoulders(p));
        var pad = Gamepad();
        pad.Axes[(int)PadAxis.LeftTrigger] = 1f;
        Assert.Equal(VitaButtons.None, Map(p, pad).State.Buttons);
        pad.Buttons[(int)PadButton.LeftShoulder] = true;
        Assert.Equal(VitaButtons.LTrigger, Map(p, pad).State.Buttons);
    }

    [Fact]
    public void DefaultKeyboardMatchesTheHistoricFixedMapping()
    {
        var p = DefaultMappings.Keyboard();
        Assert.Equal(VitaButtons.Cross, Map(p, null, "Space").State.Buttons);
        Assert.Equal(VitaButtons.Cross, Map(p, null, "X").State.Buttons);
        Assert.Equal(VitaButtons.Select, Map(p, null, "ShiftRight").State.Buttons);
        Assert.Equal(VitaButtons.Start | VitaButtons.Up, Map(p, null, "Enter", "Up").State.Buttons);
        var st = Map(p, null, "A", "S", "L").State;
        Assert.Equal((byte)0, st.Lx);
        Assert.Equal((byte)255, st.Ly);
        Assert.Equal((byte)255, st.Rx);
        Assert.Equal((byte)128, st.Ry);
        // opposite keys cancel instead of the last released one winning
        Assert.Equal((byte)128, Map(p, null, "A", "D").State.Lx);
    }

    [Fact]
    public void RawJoystickDefaults()
    {
        var ps = DefaultMappings.RawJoystick("g", "n", ControllerKind.PlayStation);
        var pad = new PadSnapshot { JoyButtonCount = 14, JoyAxisCount = 6, JoyHatCount = 1 };
        pad.JoyButtons[1] = true; // DirectInput DualShock 4: button 1 = Cross
        pad.JoyHats[0] = 1 | 2;
        pad.JoyAxes[5] = -1f; // right stick Y on axis 5
        var st = Map(ps, pad).State;
        Assert.Equal(VitaButtons.Cross | VitaButtons.Up | VitaButtons.Right, st.Buttons);
        Assert.Equal((byte)0, st.Ry);

        var gen = DefaultMappings.RawJoystick("g", "n", ControllerKind.Generic);
        var g = new PadSnapshot { JoyButtonCount = 12, JoyAxisCount = 4 };
        g.JoyButtons[0] = true;
        g.JoyAxes[3] = 1f;
        Assert.Equal(VitaButtons.Cross, Map(gen, g).State.Buttons);
        Assert.Equal((byte)255, Map(gen, g).State.Ry);
        // gamepad bindings never fire for a raw joystick and vice versa
        Assert.Equal(VitaButtons.None, Map(DefaultMappings.Gamepad("g", "n", ControllerKind.Generic), g).State.Buttons);
    }

    [Fact]
    public void SwapConfirmExchangesCrossAndCircle()
    {
        Assert.Equal(VitaButtons.Circle | VitaButtons.Start, InputMapper.SwapCrossCircle(VitaButtons.Cross | VitaButtons.Start));
        Assert.Equal(VitaButtons.Cross, InputMapper.SwapCrossCircle(VitaButtons.Circle));
        Assert.Equal(VitaButtons.Cross | VitaButtons.Circle, InputMapper.SwapCrossCircle(VitaButtons.Cross | VitaButtons.Circle));

        var p = DefaultMappings.Gamepad("g", "n", ControllerKind.PlayStation);
        p.SwapConfirm = true;
        var pad = Gamepad();
        pad.Buttons[(int)PadButton.South] = true;
        Assert.Equal(VitaButtons.Circle, Map(p, pad).State.Buttons);
        var kb = DefaultMappings.Keyboard();
        kb.SwapConfirm = true;
        Assert.Equal(VitaButtons.Cross, Map(kb, null, "C").State.Buttons);
    }

    [Fact]
    public void DeadzoneIsRadialAndRescaled()
    {
        var s = new StickSettings { Deadzone = 0.2f, Sensitivity = 1f };
        Assert.Equal((0f, 0f), StickMath.Apply(0.19f, 0f, s));
        Assert.Equal((0f, 0f), StickMath.Apply(0.1f, 0.1f, s)); // |v| = 0.14 < 0.2 although both axes are non-zero
        var (x, y) = StickMath.Apply(0.6f, 0f, s);
        Assert.Equal(0.5f, x, 4); // (0.6 - 0.2) / 0.8
        Assert.Equal(0f, y);
        (x, y) = StickMath.Apply(1f, 0f, s);
        Assert.Equal(1f, x, 4);
        // direction is kept on diagonals
        (x, y) = StickMath.Apply(0.5f, 0.5f, s);
        Assert.Equal(x, y, 5);
        float mag = MathF.Sqrt(0.5f);
        Assert.Equal((mag - 0.2f) / 0.8f, MathF.Sqrt(x * x + y * y), 4);
        // digital diagonals (keyboard, square range) still reach both ends
        (x, y) = StickMath.Apply(1f, 1f, new StickSettings { Deadzone = 0 });
        Assert.Equal((1f, 1f), (x, y));
    }

    [Fact]
    public void SensitivityAndInvertY()
    {
        var s = new StickSettings { Deadzone = 0f, Sensitivity = 2f, InvertY = true };
        var (x, y) = StickMath.Apply(0.3f, 0.6f, s);
        Assert.Equal(0.6f, x, 4);
        Assert.Equal(-1f, y, 4); // inverted and clamped
        Assert.Equal((byte)128, StickMath.ToByte(0));
        Assert.Equal((byte)0, StickMath.ToByte(-1));
        Assert.Equal((byte)255, StickMath.ToByte(1));
        Assert.Equal((byte)255, StickMath.ToByte(3));
    }

    [Fact]
    public void ProfileStickSettingsApplyInMapping()
    {
        var p = DefaultMappings.Gamepad("g", "n", ControllerKind.Xbox);
        p.LeftStick.Deadzone = 0.3f;
        p.LeftStick.InvertY = true;
        var pad = Gamepad();
        pad.Axes[(int)PadAxis.LeftX] = 0.25f; // inside the deadzone
        Assert.Equal((byte)128, Map(p, pad).State.Lx);
        pad.Axes[(int)PadAxis.LeftX] = 0f;
        pad.Axes[(int)PadAxis.LeftY] = 1f; // down, inverted -> up
        Assert.Equal((byte)0, Map(p, pad).State.Ly);
    }

    [Fact]
    public void TouchpadFingersGoToFrontRearOrBothWithTheTriggers()
    {
        var p = DefaultMappings.Gamepad("g", "DualSense", ControllerKind.PlayStation);
        var pad = Gamepad();
        pad.HasTouchpad = true;
        pad.FingerDown[0] = true; pad.FingerX[0] = 0.25f; pad.FingerY[0] = 0.75f;

        var m = Map(p, pad);
        Assert.Equal(1, m.FrontCount);
        Assert.Equal(0, m.BackCount);
        Assert.Equal(new TouchPoint(MappedInput.FingerId, 0.25f, 0.75f), m.Front[0]);

        // L2 held: rear touch pad only, and L2 is consumed by the touch layer (no L press)
        pad.Axes[(int)PadAxis.LeftTrigger] = 1f;
        m = Map(p, pad);
        Assert.Equal(0, m.FrontCount);
        Assert.Equal(1, m.BackCount);
        Assert.Equal(VitaButtons.None, m.State.Buttons);

        // R2 held: both panels
        pad.Axes[(int)PadAxis.LeftTrigger] = 0f;
        pad.Axes[(int)PadAxis.RightTrigger] = 1f;
        pad.FingerDown[1] = true; pad.FingerX[1] = 0.9f; pad.FingerY[1] = 0.1f;
        m = Map(p, pad);
        Assert.Equal(2, m.FrontCount);
        Assert.Equal(2, m.BackCount);
        Assert.Equal(VitaButtons.None, m.State.Buttons);

        // without a finger the triggers are plain L/R again
        pad.FingerDown[0] = pad.FingerDown[1] = false;
        m = Map(p, pad);
        Assert.Equal(VitaButtons.RTrigger, m.State.Buttons);
        Assert.Equal(0, m.FrontCount + m.BackCount);

        // the touch pad can be disabled
        p.TouchpadEnabled = false;
        pad.FingerDown[0] = true;
        Assert.Equal(0, Map(p, pad).FrontCount);
    }

    [Fact]
    public void TouchPointControls()
    {
        var p = DefaultMappings.Keyboard();
        p.Set(VitaControl.TouchFront, InputBinding.OfKey("T"));
        p.Set(VitaControl.TouchBack, InputBinding.OfKey("Y"));
        p.TouchFrontX = 0.1f; p.TouchFrontY = 0.2f;
        var m = Map(p, null, "T", "Y");
        Assert.Equal(new TouchPoint(MappedInput.PointId, 0.1f, 0.2f), m.Front[0]);
        Assert.Equal(1, m.BackCount);
        Span<TouchPoint> dest = stackalloc TouchPoint[8];
        Assert.Equal(1, m.CopyTouches(true, dest));
    }

    [Fact]
    public void CombineMergesKeyboardAndController()
    {
        var kb = Map(DefaultMappings.Keyboard(), null, "Enter", "A");
        var pad = Gamepad();
        pad.Buttons[(int)PadButton.South] = true;
        pad.Axes[(int)PadAxis.LeftY] = 1f;
        var gp = Map(DefaultMappings.Gamepad("g", "n", ControllerKind.Xbox), pad);
        var c = InputMapper.Combine(kb, gp);
        Assert.Equal(VitaButtons.Start | VitaButtons.Cross, c.State.Buttons);
        Assert.Equal((byte)0, c.State.Lx);
        Assert.Equal((byte)255, c.State.Ly);
    }

    [Fact]
    public void CaptureDetectsTheMovedInput()
    {
        var baseline = Gamepad();
        var now = Gamepad();
        Assert.Null(InputCapture.Detect(baseline, now));
        now.Axes[(int)PadAxis.RightX] = -0.9f;
        Assert.Equal(InputBinding.Of(PadAxis.RightX, -1), InputCapture.Detect(baseline, now));
        now.Buttons[(int)PadButton.Touchpad] = true; // buttons win over axes
        Assert.Equal(InputBinding.Of(PadButton.Touchpad), InputCapture.Detect(baseline, now));
        // a button already held when listening started is ignored
        baseline.Buttons[(int)PadButton.Touchpad] = true;
        Assert.Equal(InputBinding.Of(PadAxis.RightX, -1), InputCapture.Detect(baseline, now));

        var t = Gamepad();
        t.Axes[(int)PadAxis.RightTrigger] = 0.9f;
        Assert.Equal(InputBinding.Of(PadAxis.RightTrigger), InputCapture.Detect(Gamepad(), t));

        // raw joystick: a trigger resting at -1 is bound over its full range; hats bind one direction
        var rb = new PadSnapshot { JoyAxisCount = 4, JoyHatCount = 1 };
        rb.JoyAxes[3] = -1f;
        var rn = rb.Clone();
        rn.JoyAxes[3] = 0.5f;
        Assert.Equal(InputBinding.JoyAxis(3, 2), InputCapture.Detect(rb, rn));
        var rh = rb.Clone();
        rh.JoyHats[0] = 8;
        Assert.Equal(InputBinding.JoyHat(0, 8), InputCapture.Detect(rb, rh));
        // the full-range binding reads 0 at rest and 1 fully pressed
        Assert.Equal(0f, InputMapper.Value(InputBinding.JoyAxis(3, 2), rb, null));
        rn.JoyAxes[3] = 1f;
        Assert.Equal(1f, InputMapper.Value(InputBinding.JoyAxis(3, 2), rn, null));
    }
}
