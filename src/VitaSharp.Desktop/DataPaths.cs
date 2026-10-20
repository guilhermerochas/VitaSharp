namespace VitaSharp.Desktop;

/// <summary>
/// Default data folder (installed games under fs/, logs, shader caches, launcher settings), following each desktop
/// platform's conventions:
///   Windows  %APPDATA%\VitaSharp                         (unchanged from earlier versions)
///   Linux    $XDG_DATA_HOME/VitaSharp, else ~/.local/share/VitaSharp
///   macOS    ~/Library/Application Support/VitaSharp
/// VITASHARP_DATA or --data=&lt;folder&gt; override it. (An Android front-end will pass its app storage instead.)
/// </summary>
public static class DataPaths
{
    public static string DefaultRoot()
    {
        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VitaSharp");
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home)) home = Environment.GetEnvironmentVariable("HOME") ?? ".";
        if (OperatingSystem.IsMacOS())
            return Path.Combine(home, "Library", "Application Support", "VitaSharp");
        // XDG base directory spec: relative values are invalid and must be ignored
        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        string baseDir = !string.IsNullOrEmpty(xdg) && Path.IsPathRooted(xdg) ? xdg : Path.Combine(home, ".local", "share");
        return Path.Combine(baseDir, "VitaSharp");
    }
}
