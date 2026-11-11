namespace VitaSharp.Core.Util;

/// <summary>
/// The platform-neutral part of "Ir para o jogo" (the launcher brings the running game's window to the front): which
/// of the game process's top-level windows is the game window, what has to happen to it before it is activated, and
/// the command that activates a process's window on Linux (X11) and macOS. The Win32 calls live in the desktop app.
/// </summary>
public static class GameWindowFocus
{
    /// <summary>A top-level window of the game process.</summary>
    public readonly record struct Candidate(nint Handle, string ClassName, string Title, bool Visible, bool Owned);

    /// <summary>What to do with the window before giving it the focus.</summary>
    public enum Step
    {
        /// <summary>Already shown: only bring it to the front.</summary>
        Front,
        /// <summary>Minimized: restore it, then bring it to the front.</summary>
        Restore,
        /// <summary>Hidden on purpose (a test window, VITASHARP_HIDDEN_WINDOW=1): leave it hidden.</summary>
        KeepHidden,
    }

    // windows every Win32 GUI thread owns besides its real window (input method, text services)
    private static readonly string[] s_helperClasses = ["IME", "MSCTFIME UI", "Default IME"];

    /// <summary>The game window among the process's top-level windows (0 when there is none): an unowned window
    /// with a title that is not an input-method helper, the visible one first.</summary>
    public static nint Pick(IEnumerable<Candidate> windows)
    {
        nint hidden = 0;
        foreach (Candidate w in windows)
        {
            if (w.Handle == 0 || w.Owned || w.Title.Length == 0)
            {
                continue;
            }

            if (s_helperClasses.Any(c => w.ClassName.Equals(c, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (w.Visible)
            {
                return w.Handle;
            }

            if (hidden == 0)
            {
                hidden = w.Handle;
            }
        }
        return hidden;
    }

    /// <summary>The step for a window that is <paramref name="minimized"/> and/or <paramref name="visible"/>.</summary>
    public static Step Plan(bool minimized, bool visible) =>
        minimized ? Step.Restore : visible ? Step.Front : Step.KeepHidden;

    /// <summary>The command that activates the window of process <paramref name="pid"/> on Linux (xdotool, X11)
    /// or macOS (System Events); null elsewhere (Windows uses the Win32 calls).</summary>
    public static (string File, string[] Args)? ActivateCommand(int pid, bool linux, bool macOS)
    {
        if (linux)
        {
            return ("xdotool", ["search", "--onlyvisible", "--pid", pid.ToString(System.Globalization.CultureInfo.InvariantCulture), "windowactivate"]);
        }

        if (macOS)
        {
            return ("osascript", ["-e", $"tell application \"System Events\" to set frontmost of (first process whose unix id is {pid.ToString(System.Globalization.CultureInfo.InvariantCulture)}) to true"]);
        }

        return null;
    }
}
