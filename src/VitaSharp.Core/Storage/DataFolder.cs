namespace VitaSharp.Core.Storage;

/// <summary>Where the data folder in use came from (<see cref="DataFolder.Resolve"/>).</summary>
public enum DataFolderKind
{
    /// <summary>The platform's default folder (desktop: %APPDATA%\VitaSharp, ~/.local/share/VitaSharp,
    /// ~/Library/Application Support/VitaSharp; Android: the rules of its own storage setup).</summary>
    Default,

    /// <summary>Portable mode: <see cref="DataFolder.PortableMarkerName"/> beside the executable, the data in
    /// <see cref="DataFolder.PortableFolderName"/> beside it (as Dolphin's portable.txt and PPSSPP's portable memstick).</summary>
    Portable,

    /// <summary>A folder the user picked in the settings, named by the pointer file <see cref="DataFolder.PointerFileName"/>
    /// (kept outside every data folder).</summary>
    Custom,

    /// <summary>--data=&lt;folder&gt; or VITASHARP_DATA (developer and test runs): wins over everything else.</summary>
    Override,
}

/// <summary>What <see cref="DataFolder.Resolve"/> needs to know (all paths given, so a test never touches the user's
/// own folders).</summary>
/// <param name="Override">--data / VITASHARP_DATA, or null.</param>
/// <param name="ExecutableDir">The folder of the executable (where <see cref="DataFolder.PortableMarkerName"/> is looked
/// for), or null on a front-end without portable mode (Android).</param>
/// <param name="PointerPath">The pointer file (<see cref="DataFolder.PointerFileName"/>), outside every data folder.</param>
/// <param name="DefaultRoot">The platform's default data folder.</param>
public readonly record struct DataFolderInputs(string? Override, string? ExecutableDir, string? PointerPath, string DefaultRoot);

/// <summary>The data folder to use and why; <paramref name="Warning"/> is non-empty when a choice could not be honoured
/// (the pointer names a folder that is missing, not absolute or not writable) and the default is used instead.</summary>
public readonly record struct DataFolderResolution(string Root, DataFolderKind Kind, string Warning = "")
{
    /// <summary>One line for the log and the F8 capture, without the path itself (the capture has it as &lt;data&gt;).</summary>
    public string Describe()
    {
        string kind = Kind switch
        {
            DataFolderKind.Portable => "portable folder beside the executable (" + DataFolder.PortableMarkerName + ")",
            DataFolderKind.Custom => "folder chosen in the settings (" + DataFolder.PointerFileName + ")",
            DataFolderKind.Override => "--data / " + Util.EnvVars.Data,
            _ => "platform default",
        };
        return Warning.Length > 0 ? kind + "; warning: " + Warning : kind;
    }
}

/// <summary>
/// The one place that decides the desktop data folder and keeps the user's choice of it (docs/user-guide, "Pasta de
/// dados"). It follows the model most emulators share (Dolphin: portable.txt beside the executable and a "User folder"
/// path kept outside it; PPSSPP: a portable memstick beside the executable or a memstick folder named in a file outside
/// it; Vita3K: a pref path in its config): in this order,
/// <list type="number">
/// <item>--data / VITASHARP_DATA;</item>
/// <item><see cref="PortableMarkerName"/> beside the executable: <see cref="PortableFolderName"/> beside it;</item>
/// <item>the pointer file <see cref="PointerFileName"/> (one line, an absolute folder): that folder, when it exists and
/// can be written; otherwise the default with a warning (a drive that is not plugged in must not look like an empty
/// install in silence);</item>
/// <item>the platform default.</item>
/// </list>
/// The pointer lives outside every data folder, so moving or copying a data folder never carries it along.
/// </summary>
public static class DataFolder
{
    public const string PointerFileName = "data-root.txt";
    public const string PortableMarkerName = "portable.txt";
    public const string PortableFolderName = "data";

    /// <summary>The decision of this process, set by the front-end at start (the F8 capture's context.txt names it).</summary>
    public static DataFolderResolution? Active { get; set; }

    /// <summary>The line of the F8 capture: which kind of folder is in use, the path only as the placeholder.</summary>
    public static string CaptureLine()
    {
        return Active is { } active ? "data folder: <data> (" + active.Describe() + ")" : "data folder: <data> (not reported by the front-end)";
    }

    /// <summary>The shader cache and code cache folders of this process, set by a front-end that keeps them apart from
    /// the data folder (Android: the app's internal storage); null: both in the data folder.</summary>
    public static (string ShaderCache, string CodeCache)? Caches { get; set; }

    /// <summary>The F8 capture's line on where the caches are, with the data folder as &lt;data&gt; and any other place as
    /// &lt;internal&gt; (never a path).</summary>
    public static string CacheLine()
    {
        if (Caches is not { } caches)
        {
            return "caches: shader cache <data>/shadercache, code cache <data>/codecache";
        }

        return $"caches: shader cache {Placeholder(caches.ShaderCache)}, code cache {Placeholder(caches.CodeCache)}";
    }

    /// <summary>A cache folder named by its place: under the data folder as &lt;data&gt;/..., else &lt;internal&gt;/... (the last
    /// two parts of the path, which are the layout's own names).</summary>
    private static string Placeholder(string folder)
    {
        string full = Path.GetFullPath(folder);
        if (Active is { } active)
        {
            string root = Path.GetFullPath(active.Root);
            string relative = Path.GetRelativePath(root, full);
            if (!relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative))
            {
                return "<data>/" + relative.Replace(Path.DirectorySeparatorChar, '/');
            }
        }

        string name = Path.GetFileName(full);
        string parent = Path.GetFileName(Path.GetDirectoryName(full) ?? "");
        return "<internal>/" + (parent.Length > 0 ? parent + "/" : "") + name;
    }

    public static DataFolderResolution Resolve(DataFolderInputs inputs)
    {
        if (inputs.Override is { Length: > 0 } forced)
        {
            return new DataFolderResolution(Path.GetFullPath(forced), DataFolderKind.Override);
        }

        if (inputs.ExecutableDir is { Length: > 0 } exeDir && File.Exists(Path.Combine(exeDir, PortableMarkerName)))
        {
            return new DataFolderResolution(PortableRoot(exeDir), DataFolderKind.Portable);
        }

        string? chosen = ReadPointer(inputs.PointerPath, out string problem);
        if (chosen != null)
        {
            string? unusable = WhyUnusable(chosen);
            if (unusable == null)
            {
                return new DataFolderResolution(chosen, DataFolderKind.Custom);
            }

            problem = $"the chosen data folder {chosen} {unusable}; using the default folder";
        }

        return new DataFolderResolution(Path.GetFullPath(inputs.DefaultRoot), DataFolderKind.Default, problem);
    }

    /// <summary>The portable data folder for the executable folder <paramref name="exeDir"/>.</summary>
    public static string PortableRoot(string exeDir)
    {
        return Path.GetFullPath(Path.Combine(exeDir, PortableFolderName));
    }

    /// <summary>The folder named by the pointer, or null (no pointer, or an empty one); <paramref name="problem"/> says
    /// why a pointer that exists was not accepted.</summary>
    public static string? ReadPointer(string? pointerPath, out string problem)
    {
        problem = "";
        if (string.IsNullOrEmpty(pointerPath) || !File.Exists(pointerPath))
        {
            return null;
        }

        string text;
        try
        {
            text = File.ReadAllText(pointerPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            problem = $"{PointerFileName} cannot be read ({e.Message}); using the default folder";
            return null;
        }

        string line = "";
        foreach (string candidate in text.Split('\n'))
        {
            string trimmed = candidate.Trim();
            if (trimmed.Length > 0 && !trimmed.StartsWith('#'))
            {
                line = trimmed;
                break;
            }
        }

        if (line.Length == 0)
        {
            return null;
        }

        if (!Path.IsPathFullyQualified(line))
        {
            problem = $"{PointerFileName} names a folder that is not an absolute path ({line}); using the default folder";
            return null;
        }

        return Path.GetFullPath(line);
    }

    /// <summary>Writes the choice: <paramref name="root"/> as the folder to use, or null to go back to the default (the
    /// pointer is deleted). Written to a temporary file and renamed, so a cut never leaves half a path.</summary>
    public static void WritePointer(string pointerPath, string? root)
    {
        if (root == null)
        {
            if (File.Exists(pointerPath))
            {
                File.Delete(pointerPath);
            }

            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(pointerPath))!);
        string temporary = pointerPath + ".part";
        File.WriteAllText(temporary, Path.GetFullPath(root) + "\n");
        File.Move(temporary, pointerPath, true);
    }

    /// <summary>Where a choice puts the data: the default folder, the portable one beside the executable, or
    /// <paramref name="customRoot"/>.</summary>
    public static string TargetOf(DataFolderKind kind, string? customRoot, string? exeDir, string defaultRoot)
    {
        return kind switch
        {
            DataFolderKind.Portable when exeDir is { Length: > 0 } => PortableRoot(exeDir),
            DataFolderKind.Custom when customRoot is { Length: > 0 } => Path.GetFullPath(customRoot),
            DataFolderKind.Default => Path.GetFullPath(defaultRoot),
            _ => throw new ArgumentException($"no folder for the choice {kind}"),
        };
    }

    /// <summary>Keeps the user's choice so the next start resolves it (<see cref="Resolve"/>): the default deletes the
    /// pointer and the portable marker; portable writes the marker beside the executable (and creates its folder); a
    /// custom folder writes the pointer and deletes the portable marker (it would win over the pointer). The data
    /// folders themselves are never touched here.</summary>
    public static void SaveChoice(DataFolderKind kind, string? customRoot, string pointerPath, string? exeDir)
    {
        string? marker = exeDir is { Length: > 0 } ? Path.Combine(exeDir, PortableMarkerName) : null;
        switch (kind)
        {
            case DataFolderKind.Default:
                WritePointer(pointerPath, null);
                DeleteMarker(marker);
                break;
            case DataFolderKind.Portable when marker != null:
                Directory.CreateDirectory(PortableRoot(exeDir!));
                File.WriteAllText(marker, "VitaSharp portable mode: the data folder is the \"" + PortableFolderName + "\" folder beside this file.\n");
                break;
            case DataFolderKind.Custom when customRoot is { Length: > 0 }:
                Directory.CreateDirectory(customRoot);
                WritePointer(pointerPath, customRoot);
                DeleteMarker(marker);
                break;
            default:
                throw new ArgumentException($"the choice {kind} cannot be saved");
        }
    }

    /// <summary>The portable mode can be turned on: a file can be created beside the executable (not in a read-only
    /// install folder or an app bundle that must stay unchanged).</summary>
    public static bool CanBePortable(string? exeDir)
    {
        return exeDir is { Length: > 0 } && WhyUnusable(exeDir) == null;
    }

    private static void DeleteMarker(string? marker)
    {
        if (marker != null && File.Exists(marker))
        {
            File.Delete(marker);
        }
    }

    /// <summary>Null when <paramref name="folder"/> can hold the data (it exists and a file can be created in it), else
    /// why not.</summary>
    public static string? WhyUnusable(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return "does not exist (a drive that is not connected?)";
        }

        string probe = Path.Combine(folder, ".vitasharp-write-test-" + Environment.ProcessId);
        try
        {
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "cannot be written (" + e.Message + ")";
        }
    }

    /// <summary>True when <paramref name="inner"/> is <paramref name="outer"/> or inside it (a data folder cannot be
    /// moved into itself).</summary>
    public static bool IsSameOrInside(string inner, string outer)
    {
        string a = Path.TrimEndingDirectorySeparator(Path.GetFullPath(inner));
        string b = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outer));
        StringComparison comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return a.Equals(b, comparison) || a.StartsWith(b + Path.DirectorySeparatorChar, comparison);
    }
}
