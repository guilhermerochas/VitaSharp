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
}

public static class VitaControls
{
    public const int Count = (int)VitaControl.TouchBack + 1;

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
        _ => c.ToString(),
    };
}
