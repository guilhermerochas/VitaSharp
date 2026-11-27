namespace VitaSharp.Desktop;

/// <summary>
/// Default data folder (installed games under fs/, logs, shader caches, launcher settings), following each desktop
/// platform's conventions:
///   Windows  %APPDATA%\VitaSharp                         (unchanged from earlier versions)
///   Linux    $XDG_DATA_HOME/VitaSharp, else ~/.local/share/VitaSharp
///   macOS    ~/Library/Application Support/VitaSharp
/// VITASHARP_DATA or --data=&lt;folder&gt; override it; portable.txt beside the executable or the folder picked in the
/// settings (the pointer <see cref="PointerPath"/>) replace the default (<see cref="VitaSharp.Core.Storage.DataFolder"/>).
/// </summary>
public static class DataPaths
{
    public static string DefaultRoot()
    {
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VitaSharp");
        }

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home))
        {
            home = Environment.GetEnvironmentVariable("HOME") ?? ".";
        }

        if (OperatingSystem.IsMacOS())
        {
            return Path.Combine(home, "Library", "Application Support", "VitaSharp");
        }
        // XDG base directory spec: relative values are invalid and must be ignored
        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        string baseDir = !string.IsNullOrEmpty(xdg) && Path.IsPathRooted(xdg) ? xdg : Path.Combine(home, ".local", "share");
        return Path.Combine(baseDir, "VitaSharp");
    }
    /// <summary>The pointer file that keeps the folder picked in the settings ("Pasta de dados"), outside every data
    /// folder (the default one included, so a move never carries it):
    ///   Windows  %LOCALAPPDATA%\VitaSharp\data-root.txt
    ///   Linux    $XDG_CONFIG_HOME/VitaSharp/data-root.txt, else ~/.config/VitaSharp/data-root.txt
    ///   macOS    ~/Library/Preferences/VitaSharp/data-root.txt</summary>
    public static string PointerPath()
    {
        string name = VitaSharp.Core.Storage.DataFolder.PointerFileName;
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VitaSharp", name);
        }

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home))
        {
            home = Environment.GetEnvironmentVariable("HOME") ?? ".";
        }

        if (OperatingSystem.IsMacOS())
        {
            return Path.Combine(home, "Library", "Preferences", "VitaSharp", name);
        }

        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        string baseDir = !string.IsNullOrEmpty(xdg) && Path.IsPathRooted(xdg) ? xdg : Path.Combine(home, ".config");
        return Path.Combine(baseDir, "VitaSharp", name);
    }

    /// <summary>The folder of the executable (portable.txt and the portable data folder sit beside it).</summary>
    public static string ExecutableDir()
    {
        return AppContext.BaseDirectory;
    }

    /// <summary>This process's data folder decision (computed once: a change in the settings applies on restart).</summary>
    public static VitaSharp.Core.Storage.DataFolderResolution Current => _current.Value;

    static readonly Lazy<VitaSharp.Core.Storage.DataFolderResolution> _current = new(() => VitaSharp.Core.Storage.DataFolder.Resolve(
        new VitaSharp.Core.Storage.DataFolderInputs(Environment.GetEnvironmentVariable(VitaSharp.Core.Util.EnvVars.Data), ExecutableDir(),
            PointerPath(), DefaultRoot())));
}
