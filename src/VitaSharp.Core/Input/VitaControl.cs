using VitaSharp.Core.Host;

namespace VitaSharp.Core.Input;

/// <summary>
/// Every input of the PS Vita (and PS TV) that a host control can be bound to. The analog sticks are split into four
/// half-axis directions each, so a stick can be driven by an analog axis, by buttons or by keys alike.
/// </summary>
public enum VitaControl
{
    Up, Down, Left, Right,
    Cross, Circle, Square, Triangle,
    L, R, Start, Select, PS,
    /// <summary>Stick clicks: only the PS TV has them (DualShock 3/4 on the TV), handheld games ignore them.</summary>
    L3, R3,
    LStickUp, LStickDown, LStickLeft, LStickRight,
    RStickUp, RStickDown, RStickLeft, RStickRight,
    /// <summary>Holds a finger on the front touch screen at the profile's touch point.</summary>
    TouchFront,
    /// <summary>Holds a finger on the rear touch pad at the profile's touch point.</summary>
    TouchBack,
    /// <summary>Virtual motion (<see cref="VirtualMotion"/>): while held, the emulated Vita tilts its top edge away
    /// from the player (forward) / towards the player (back), lowers its left / right edge, or twists about the
    /// screen normal, up to the profile's maximum angle; released, it eases back to neutral.</summary>
    TiltForward, TiltBack, TiltLeft, TiltRight, TwistLeft, TwistRight,
    /// <summary>Virtual motion: brings the mouse-look rotation back to neutral.</summary>
    MotionRecenter,
    /// <summary>Host hotkey, not a Vita input: pressing it pauses the whole emulation, pressing it again resumes it
    /// (<see cref="EmulationPause"/>). Keyboard default: P. On a controller a binding may be a combination of buttons
    /// held together (<see cref="InputCombo"/>); controllers have none by default.</summary>
    PauseEmulation,
}

public static class VitaControls
{
    public const int Count = (int)VitaControl.PauseEmulation + 1;

    public static readonly VitaControl[] All = Enum.GetValues<VitaControl>();

    /// <summary>The SCE_CTRL button bit of a digital control, None for stick directions and touch.</summary>
    public static VitaButtons Button(VitaControl c) => c switch
    {
        VitaControl.Up => VitaButtons.Up, VitaControl.Down => VitaButtons.Down,
        VitaControl.Left => VitaButtons.Left, VitaControl.Right => VitaButtons.Right,
        VitaControl.Cross => VitaButtons.Cross, VitaControl.Circle => VitaButtons.Circle,
        VitaControl.Square => VitaButtons.Square, VitaControl.Triangle => VitaButtons.Triangle,
        VitaControl.L => VitaButtons.LTrigger, VitaControl.R => VitaButtons.RTrigger,
        VitaControl.Start => VitaButtons.Start, VitaControl.Select => VitaButtons.Select,
        VitaControl.PS => VitaButtons.PsButton, VitaControl.L3 => VitaButtons.L3, VitaControl.R3 => VitaButtons.R3,
        _ => VitaButtons.None,
    };

    public static bool IsStickDirection(VitaControl c) => c is >= VitaControl.LStickUp and <= VitaControl.RStickRight;

    /// <summary>Controls of the virtual motion sensors (keyboard tilt), not of the Vita's buttons.</summary>
    public static bool IsMotion(VitaControl c) => c is >= VitaControl.TiltForward and <= VitaControl.MotionRecenter;

    /// <summary>Emulator hotkeys (pause), handled by the front-end and never seen by the game.</summary>
    public static bool IsHotkey(VitaControl c) => c == VitaControl.PauseEmulation;

    /// <summary>English label for the configuration screen.</summary>
    public static string Label(VitaControl c) => c switch
    {
        VitaControl.Up => "D-pad up", VitaControl.Down => "D-pad down", VitaControl.Left => "D-pad left", VitaControl.Right => "D-pad right",
        VitaControl.Cross => "Cross", VitaControl.Circle => "Circle", VitaControl.Square => "Square", VitaControl.Triangle => "Triangle",
        VitaControl.L => "L", VitaControl.R => "R", VitaControl.Start => "Start", VitaControl.Select => "Select", VitaControl.PS => "PS button",
        VitaControl.L3 => "L3 (PS TV)", VitaControl.R3 => "R3 (PS TV)",
        VitaControl.LStickUp => "Left stick up", VitaControl.LStickDown => "Left stick down",
        VitaControl.LStickLeft => "Left stick left", VitaControl.LStickRight => "Left stick right",
        VitaControl.RStickUp => "Right stick up", VitaControl.RStickDown => "Right stick down",
        VitaControl.RStickLeft => "Right stick left", VitaControl.RStickRight => "Right stick right",
        VitaControl.TouchFront => "Front touch (tap point)", VitaControl.TouchBack => "Rear touch (tap point)",
        VitaControl.TiltForward => "Tilt forward", VitaControl.TiltBack => "Tilt back",
        VitaControl.TiltLeft => "Tilt left", VitaControl.TiltRight => "Tilt right",
        VitaControl.TwistLeft => "Twist left", VitaControl.TwistRight => "Twist right", VitaControl.MotionRecenter => "Re-centre motion",
        VitaControl.PauseEmulation => "Pause emulation",
        _ => c.ToString(),
    };
}
