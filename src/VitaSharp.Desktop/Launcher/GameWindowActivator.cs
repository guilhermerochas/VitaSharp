using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using VitaSharp.Core.Util;

namespace VitaSharp.Desktop.Launcher;

/// <summary>
/// "Ir para o jogo": brings the window of the running game (a child process of the launcher, "run" / "boot-shell")
/// to the front and gives it the focus, restoring it when it is minimized. Windows: the game's top-level window is
/// found by process id, restored with ShowWindow and activated with SetForegroundWindow, which works because the
/// launcher is the foreground process when the user clicks (the thread-input attach is the fallback when it is not);
/// AllowSetForegroundWindow also lets the game take the focus itself. Linux (X11) and macOS: xdotool / System Events
/// (<see cref="GameWindowFocus.ActivateCommand"/>), best effort.
/// </summary>
static class GameWindowActivator
{
    /// <summary>Activates the game window of <paramref name="p"/>; returns a line for the log saying what was done.</summary>
    public static string Activate(Process p)
    {
        try
        {
            if (p.HasExited)
            {
                return "focus: the game has exited";
            }

            if (OperatingSystem.IsWindows())
            {
                return ActivateWin32(p.Id);
            }

            if (GameWindowFocus.ActivateCommand(p.Id, OperatingSystem.IsLinux(), OperatingSystem.IsMacOS()) is { } cmd)
            {
                var psi = new ProcessStartInfo(cmd.File) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var a in cmd.Args)
                {
                    psi.ArgumentList.Add(a);
                }

                using var tool = Process.Start(psi);
                tool?.WaitForExit(3000);
                return $"focus: {cmd.File} pid {p.Id} -> exit {(tool is { HasExited: true } ? tool.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture) : "?")}";
            }
            return "focus: not supported on this system";
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            return "focus: " + e.Message;
        }
    }

    /// <summary>The game window of process <paramref name="pid"/> (0 when it has none yet).</summary>
    public static nint FindWindow(int pid)
    {
        if (!OperatingSystem.IsWindows())
        {
            return 0;
        }

        var found = new List<GameWindowFocus.Candidate>();
        var cls = new StringBuilder(256);
        var title = new StringBuilder(256);
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out uint owner);
            if (owner != pid)
            {
                return true;
            }

            cls.Clear();
            title.Clear();
            GetClassName(h, cls, cls.Capacity);
            GetWindowText(h, title, title.Capacity);
            found.Add(new(h, cls.ToString(), title.ToString(), IsWindowVisible(h), GetWindow(h, GW_OWNER) != 0));
            return true;
        }, 0);
        return GameWindowFocus.Pick(found);
    }

    /// <summary>The window's state for the log: minimized, shown or hidden, and whether it has the focus.</summary>
    public static string Describe(nint hwnd) =>
        hwnd == 0 ? "no window" : $"{(IsIconic(hwnd) ? "minimized" : IsWindowVisible(hwnd) ? "shown" : "hidden")}{(GetForegroundWindow() == hwnd ? ", foreground" : "")}";

    static string ActivateWin32(int pid)
    {
        var hwnd = FindWindow(pid);
        if (hwnd == 0)
        {
            return $"focus: pid {pid} has no window yet";
        }

        string before = Describe(hwnd);
        AllowSetForegroundWindow((uint)pid);
        var step = GameWindowFocus.Plan(IsIconic(hwnd), IsWindowVisible(hwnd));
        if (step == GameWindowFocus.Step.KeepHidden)
        {
            return $"focus: pid {pid} window 0x{hwnd:X} is hidden (test window), left hidden";
        }

        if (step == GameWindowFocus.Step.Restore)
        {
            ShowWindow(hwnd, SW_RESTORE);
        }

        bool fg = SetForegroundWindow(hwnd);
        if (!fg)
        {
            // the launcher is not the foreground process (e.g. it was clicked from the taskbar preview): join the input
            // of the foreground thread for the call, the usual way around the foreground lock
            uint me = GetCurrentThreadId();
            uint front = GetWindowThreadProcessId(GetForegroundWindow(), out _);
            if (front != 0 && front != me && AttachThreadInput(me, front, true))
            {
                BringWindowToTop(hwnd);
                fg = SetForegroundWindow(hwnd);
                AttachThreadInput(me, front, false);
            }
        }
        return $"focus: pid {pid} window 0x{hwnd:X} {step}: {before} -> {Describe(hwnd)} (SetForegroundWindow {(fg ? "ok" : "refused")})";
    }

    const int SW_RESTORE = 9;
    const uint GW_OWNER = 4;

    delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, nint lParam);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(nint hwnd, StringBuilder name, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(nint hwnd, StringBuilder text, int max);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] static extern nint GetWindow(nint hwnd, uint cmd);
    [DllImport("user32.dll")] static extern bool ShowWindow(nint hwnd, int cmd);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] static extern bool BringWindowToTop(nint hwnd);
    [DllImport("user32.dll")] static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(uint pid);
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint from, uint to, bool attach);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();

    /// <summary>ui-render's focus check: minimizes the window the way the user would (it becomes a taskbar button
    /// of the desktop it runs on).</summary>
    public static void MinimizeForTests(nint hwnd)
    {
        if (hwnd != 0)
        {
            ShowWindow(hwnd, 6 /* SW_MINIMIZE */);
        }
    }
}
