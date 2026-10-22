using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace VitaSharp.Desktop.Launcher;

/// <summary>Avalonia application hosting the game library window (started when VitaSharp runs without arguments).</summary>
public sealed class LauncherApp : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        RequestedThemeVariant = ThemeVariant.Dark;
    }

    /// <summary>Replaces the library window (ui-snapshot renders the settings screens instead).</summary>
    internal static Action<IClassicDesktopStyleApplicationLifetime>? StartupOverride;

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            if (StartupOverride != null) StartupOverride(desktop); else desktop.MainWindow = new MainWindow();
        base.OnFrameworkInitializationCompleted();
    }

    public static int Run(string[] args)
    {
        if (StartupOverride == null) HideOwnConsole();
        return AppBuilder.Configure<LauncherApp>()
            .UsePlatformDetect()
            .LogToTrace()
            .StartWithClassicDesktopLifetime(args);
    }

    /// <summary>When started from Explorer, Windows opens a console just for us; the launcher does not need it.</summary>
    static void HideOwnConsole()
    {
        if (!OperatingSystem.IsWindows()) return;
        var pids = new uint[2];
        if (GetConsoleProcessList(pids, 2) == 1) FreeConsole();
    }

    [DllImport("kernel32.dll")] static extern uint GetConsoleProcessList(uint[] list, uint count);
    [DllImport("kernel32.dll")] static extern bool FreeConsole();
}
