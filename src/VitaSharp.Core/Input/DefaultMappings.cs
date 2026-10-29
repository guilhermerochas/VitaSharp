namespace VitaSharp.Core.Input;

/// <summary>
/// Built-in mappings used when a device has no saved profile. They reproduce the emulator's historic fixed mapping,
/// so nothing changes for players who never open the controller settings.
/// </summary>
public static class DefaultMappings
{
    /// <summary>Keyboard: arrows = D-pad, X/Space = Cross, C = Circle, Z = Square, V = Triangle, Q/E = L/R,
    /// Enter = Start, Backspace/right Shift = Select, Home = PS button, WASD = left stick, IJKL = right stick.</summary>
    public static ControllerProfile Keyboard()
    {
        var p = new ControllerProfile { Id = ControllerProfile.KeyboardId, Name = "Keyboard", Kind = ControllerKind.Keyboard };
        void K(VitaControl c, params string[] keys) { foreach (var k in keys) p.Add(c, InputBinding.OfKey(k)); }
        K(VitaControl.Up, "Up"); K(VitaControl.Down, "Down"); K(VitaControl.Left, "Left"); K(VitaControl.Right, "Right");
        K(VitaControl.Cross, "X", "Space"); K(VitaControl.Circle, "C"); K(VitaControl.Square, "Z"); K(VitaControl.Triangle, "V");
        K(VitaControl.L, "Q"); K(VitaControl.R, "E"); K(VitaControl.Start, "Enter"); K(VitaControl.Select, "Backspace", "ShiftRight");
        K(VitaControl.PS, "Home");
        K(VitaControl.LStickUp, "W"); K(VitaControl.LStickDown, "S"); K(VitaControl.LStickLeft, "A"); K(VitaControl.LStickRight, "D");
        K(VitaControl.RStickUp, "I"); K(VitaControl.RStickDown, "K"); K(VitaControl.RStickLeft, "J"); K(VitaControl.RStickRight, "L");
        // keys are digital: no deadzone needed
        p.LeftStick.Deadzone = p.RightStick.Deadzone = 0;
        return p;
    }

    /// <summary>
    /// Controller with a gamepad mapping (GLFW's SDL GameControllerDB): the standard layout is positional, so the
    /// bottom face button is Cross on every controller family — A on Xbox, Cross on DualShock/DualSense, B on Nintendo,
    /// exactly where Cross sits on a Vita. Bumpers and triggers both act as L/R (the handheld has a single pair).
    /// </summary>
    public static ControllerProfile Gamepad(string id, string name, ControllerKind kind)
    {
        var p = new ControllerProfile { Id = id, Name = name, Kind = kind };
        p.Add(VitaControl.Cross, InputBinding.Of(PadButton.South));
        p.Add(VitaControl.Circle, InputBinding.Of(PadButton.East));
        p.Add(VitaControl.Square, InputBinding.Of(PadButton.West));
        p.Add(VitaControl.Triangle, InputBinding.Of(PadButton.North));
        p.Add(VitaControl.L, InputBinding.Of(PadButton.LeftShoulder));
        p.Add(VitaControl.R, InputBinding.Of(PadButton.RightShoulder));
        SetTriggersAsShoulders(p, true);
        p.Add(VitaControl.Start, InputBinding.Of(PadButton.Start));
        p.Add(VitaControl.Select, InputBinding.Of(PadButton.Back));
        p.Add(VitaControl.PS, InputBinding.Of(PadButton.Guide)); // PS (DualShock/DualSense), Xbox guide, Home
        p.Add(VitaControl.Up, InputBinding.Of(PadButton.DpadUp));
        p.Add(VitaControl.Down, InputBinding.Of(PadButton.DpadDown));
        p.Add(VitaControl.Left, InputBinding.Of(PadButton.DpadLeft));
        p.Add(VitaControl.Right, InputBinding.Of(PadButton.DpadRight));
        p.Add(VitaControl.L3, InputBinding.Of(PadButton.LeftStick));
        p.Add(VitaControl.R3, InputBinding.Of(PadButton.RightStick));
        p.Add(VitaControl.LStickLeft, InputBinding.Of(PadAxis.LeftX, -1));
        p.Add(VitaControl.LStickRight, InputBinding.Of(PadAxis.LeftX, 1));
        p.Add(VitaControl.LStickUp, InputBinding.Of(PadAxis.LeftY, -1));
        p.Add(VitaControl.LStickDown, InputBinding.Of(PadAxis.LeftY, 1));
        p.Add(VitaControl.RStickLeft, InputBinding.Of(PadAxis.RightX, -1));
        p.Add(VitaControl.RStickRight, InputBinding.Of(PadAxis.RightX, 1));
        p.Add(VitaControl.RStickUp, InputBinding.Of(PadAxis.RightY, -1));
        p.Add(VitaControl.RStickDown, InputBinding.Of(PadAxis.RightY, 1));
        return p;
    }

    /// <summary>
    /// Joystick that is not in the mapping database: raw button/axis/hat indices. PlayStation controllers use the
    /// DualShock 4 / DualSense DirectInput layout; anything else gets the common generic USB pad layout.
    /// </summary>
    public static ControllerProfile RawJoystick(string id, string name, ControllerKind kind)
    {
        var p = new ControllerProfile { Id = id, Name = name, Kind = kind };
        void B(VitaControl c, int i) => p.Add(c, InputBinding.JoyButton(i));
        if (kind == ControllerKind.PlayStation)
        {
            B(VitaControl.Square, 0); B(VitaControl.Cross, 1); B(VitaControl.Circle, 2); B(VitaControl.Triangle, 3);
            B(VitaControl.L, 4); B(VitaControl.R, 5); B(VitaControl.L, 6); B(VitaControl.R, 7);
            B(VitaControl.Select, 8); B(VitaControl.Start, 9); B(VitaControl.L3, 10); B(VitaControl.R3, 11);
            B(VitaControl.PS, 12);
            Stick(p, 0, 1, VitaControl.LStickLeft); Stick(p, 2, 5, VitaControl.RStickLeft);
        }
        else
        {
            B(VitaControl.Cross, 0); B(VitaControl.Circle, 1); B(VitaControl.Square, 2); B(VitaControl.Triangle, 3);
            B(VitaControl.L, 4); B(VitaControl.R, 5); B(VitaControl.Select, 6); B(VitaControl.Start, 7);
            B(VitaControl.L3, 8); B(VitaControl.R3, 9);
            Stick(p, 0, 1, VitaControl.LStickLeft); Stick(p, 2, 3, VitaControl.RStickLeft);
        }
        p.Add(VitaControl.Up, InputBinding.JoyHat(0, 1));
        p.Add(VitaControl.Right, InputBinding.JoyHat(0, 2));
        p.Add(VitaControl.Down, InputBinding.JoyHat(0, 4));
        p.Add(VitaControl.Left, InputBinding.JoyHat(0, 8));
        return p;
    }

    static void Stick(ControllerProfile p, int ax, int ay, VitaControl first)
    {
        // first = the stick's ...Left control; the enum order is Up, Down, Left, Right
        var up = first - 2;
        p.Add(up, InputBinding.JoyAxis(ay, -1));
        p.Add(up + 1, InputBinding.JoyAxis(ay, 1));
        p.Add(first, InputBinding.JoyAxis(ax, -1));
        p.Add(first + 1, InputBinding.JoyAxis(ax, 1));
    }

    /// <summary>The default profile for a connected device.</summary>
    public static ControllerProfile For(string id, string name, ControllerKind kind, bool isGamepad)
        => isGamepad ? Gamepad(id, name, kind) : RawJoystick(id, name, kind);

    /// <summary>Adds or removes the analog triggers (L2/R2, LT/RT, ZL/ZR) as extra bindings of L and R.</summary>
    public static void SetTriggersAsShoulders(ControllerProfile p, bool on)
    {
        var lt = InputBinding.Of(PadAxis.LeftTrigger);
        var rt = InputBinding.Of(PadAxis.RightTrigger);
        if (on) { p.Add(VitaControl.L, lt); p.Add(VitaControl.R, rt); }
        else { p.Remove(VitaControl.L, lt); p.Remove(VitaControl.R, rt); }
    }

    public static bool TriggersAsShoulders(ControllerProfile p)
        => p.Has(VitaControl.L, InputBinding.Of(PadAxis.LeftTrigger)) && p.Has(VitaControl.R, InputBinding.Of(PadAxis.RightTrigger));
}
