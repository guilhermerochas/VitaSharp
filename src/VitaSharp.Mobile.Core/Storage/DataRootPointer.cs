namespace VitaSharp.Mobile.Core.Storage;

/// <summary>
/// The app's answer to "which data folder is in use?" for adb scripts (build/android-*.ps1/.sh, docs/android/README.md,
/// "Storage"): every process writes the folder it opened to <c>Android/data/&lt;package&gt;/data-root.txt</c>, next to
/// the app-specific folder (not inside it, so it is never part of a data folder or of a move). The shell user of adb can
/// read it; it goes away with the app (uninstall), so a stale answer of an earlier install cannot remain.
/// </summary>
public static class DataRootPointer
{
    public const string FileName = "data-root.txt";

    /// <summary>Where the pointer is for the app-specific folder <paramref name="appSpecificDir"/>
    /// (Android/data/&lt;package&gt;/files); null without one.</summary>
    public static string? PathFor(string? appSpecificDir)
    {
        if (string.IsNullOrEmpty(appSpecificDir))
        {
            return null;
        }

        string? package = Path.GetDirectoryName(Path.GetFullPath(appSpecificDir).TrimEnd(Path.DirectorySeparatorChar, '/'));
        return package == null ? null : Path.Combine(package, FileName);
    }

    /// <summary>Writes <paramref name="root"/> as the folder in use (best effort: false when it cannot be written).</summary>
    public static bool Write(string? appSpecificDir, string root)
    {
        string? path = PathFor(appSpecificDir);
        if (path == null)
        {
            return false;
        }

        try
        {
            string temporary = path + ".part";
            File.WriteAllText(temporary, Path.GetFullPath(root).Replace('\\', '/') + "\n");
            File.Move(temporary, path, true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
