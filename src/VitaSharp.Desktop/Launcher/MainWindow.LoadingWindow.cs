using Avalonia;
using Avalonia.Controls;
using System.Diagnostics;
using VitaSharp.Core.Boot;

namespace VitaSharp.Desktop.Launcher;

/// <summary>The game's loading window (<see cref="GameLoadingWindow"/>): opened by the click, before the game process
/// is even started, fed by the game's log lines (<see cref="BootProgress"/>, the shader cache load and the ARM code
/// precompilation), brought back to the front by a second click on the game, and closed when the game is ready or its
/// process ends.</summary>
public sealed partial class MainWindow
{
    GameLoadingWindow? _loading;

    /// <summary>The loading window open now (the headless tests), null when none is.</summary>
    internal GameLoadingWindow? LoadingWindowForTests => _loading;

    /// <summary>How the game process is started (the headless tests start a stand-in that reads its input).</summary>
    internal Func<ProcessStartInfo, Process> StartProcess = static psi => Process.Start(psi) ?? throw new InvalidOperationException("the game process did not start");

    /// <summary>A line of the game's log as the game process writes it (the headless tests feed the stand-in's lines).</summary>
    internal void OnGameOutputForTests(string line) => OnGameOutput(line);

    /// <summary>A line of the game's error output (the headless tests feed the reason of a failed load).</summary>
    internal void OnGameErrorForTests(string line) => OnGameError(line);

    /// <summary>The game's error output: its last line is the reason of a failed load (<see cref="VitaSharp.Core.TitleLaunchFailure"/>).</summary>
    void OnGameError(string line)
    {
        _lastErrorLine = line;
        AppendLog(line);
    }

    void OnGameOutput(string line)
    {
        AppendLog(line);
        OnShaderCacheLoadLine(line);
        OnCodePrecompileLine(line);
        OnLoadingLine(line);
    }

    /// <summary>Opens the loading window of <paramref name="title"/> at once and returns the game process's options
    /// for it: its window waits hidden at this window's place.</summary>
    string[] OpenLoadingWindow(string title, GameEntry? g)
    {
        CloseLoadingWindow();
        string? icon = g?.IconPath;
        string? picture = g != null ? VitaSharp.Core.Io.Vfs.ResolveCaseInsensitive(g.Path, "sce_sys/pic0.png") : null;
        var screen = new LoadingScreen(title, icon, picture != null && File.Exists(picture) ? picture : null);
        // the game window is 960x544 pixels (GLFW); this one the same size on the screen it opens on
        double scaling = Screens.ScreenFromWindow(this)?.Scaling ?? Screens.Primary?.Scaling ?? 1;
        if (scaling <= 0)
        {
            scaling = 1;
        }

        var window = new GameLoadingWindow(screen, 960 / scaling, 544 / scaling);
        window.CancelRequested += CancelLoadingWait;
        window.ClosedByUser += () =>
        {
            _loading = null;
            StopRunning();
        };
        _loading = window;
        window.Show();
        return [BootProgress.LauncherScreenOption, .. GameWindowPositionOption(window, scaling)];
    }

    /// <summary>--window-pos=X,Y: the loading window's client area on the screen, in the game window's coordinates
    /// (pixels on Windows and X11; points on macOS, where GLFW's screen coordinates are scaled).</summary>
    static string[] GameWindowPositionOption(Window window, double scaling)
    {
        try
        {
            PixelPoint at = window.PointToScreen(new Point(0, 0));
            if (OperatingSystem.IsMacOS())
            {
                at = new PixelPoint((int)Math.Round(at.X / scaling), (int)Math.Round(at.Y / scaling));
            }

            return [FormattableString.Invariant($"--window-pos={at.X},{at.Y}")];
        }
        catch (InvalidOperationException)
        {
            return [];
        }
    }

    /// <summary>A line of the game's log: the loading window follows the step it carries and goes when the game is ready.</summary>
    void OnLoadingLine(string line)
    {
        if (_loading is not { } window)
        {
            return;
        }

        window.Screen.OnLine(line);
        if (window.Screen.IsDone)
        {
            CloseLoadingWindow();
        }
    }

    /// <summary>The loading window's "Cancelar": the wait that runs stops (the game starts at once).</summary>
    void CancelLoadingWait()
    {
        switch (_loading?.Screen.Cancel())
        {
            case BootStep.ShaderCache:
                CancelShaderCacheLoad();
                break;
            case BootStep.CodePrecompile:
                CancelCodePrecompile();
                break;
            default:
                break;
        }
    }

    void CloseLoadingWindow()
    {
        if (_loading is not { } window)
        {
            return;
        }

        _loading = null;
        window.Screen.End();
        window.CloseByLauncher();
    }
}
