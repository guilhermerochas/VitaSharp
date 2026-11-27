namespace VitaSharp.Mobile.Core.Storage;

/// <summary>Which data folder the user picked when both could be used (<see cref="DataRoot.Resolve"/>); kept by the
/// app until it is uninstalled (StorageSetup, the app's private preferences).</summary>
public enum StorageChoice
{
    /// <summary>Nothing picked: the default rules of <see cref="DataRoot"/>.</summary>
    None,

    /// <summary>The app-specific folder (Android/data/&lt;package&gt;/files), even with the visible one available;
    /// /storage/emulated/0/.vitasharp is left as it is.</summary>
    AppFolder,

    /// <summary>The visible folder, with the app-specific folder's own data left there untouched (not merged).</summary>
    SharedFolder,

    /// <summary>A folder the user picked in Ajustes &gt; "Pasta de dados" (<see cref="DataRootInputs.CustomFolder"/>):
    /// used as it is when it exists and can be written; otherwise the rules above, with
    /// <see cref="DataRootPlan.Warning"/>.</summary>
    CustomFolder,
}

/// <summary>What the app has to ask before it may use a data folder (<see cref="DataRootPlan.Question"/>).</summary>
public enum StorageQuestion
{
    /// <summary>Nothing: <see cref="DataRootPlan.Root"/> is the folder to use.</summary>
    None,

    /// <summary>The visible folder holds VitaSharp data (games, firmware, saves of an earlier install) but "All files
    /// access" is not granted (a reinstall resets it): the app says where the data is and offers the permission screen
    /// or the app-specific folder, instead of starting empty without a word.</summary>
    GrantForSharedData,

    /// <summary>Both folders hold the user's data (the app ran in the app-specific folder before the permission came
    /// back): nothing is moved until the user picks one folder or the merge, which overwrites nothing.</summary>
    ChooseBetweenFolders,
}

/// <summary>What the app knows about the device when it picks its data folder (<see cref="DataRoot.Resolve"/>).</summary>
/// <param name="AppSpecificDir">Context.GetExternalFilesDir(null): /storage/emulated/0/Android/data/&lt;package&gt;/files
/// (null when shared storage is not mounted).</param>
/// <param name="InternalDir">Context.FilesDir, the last resort when shared storage is not mounted.</param>
/// <param name="SharedStorageDir">The primary shared storage (Environment.ExternalStorageDirectory, /storage/emulated/0),
/// null when it is not mounted.</param>
/// <param name="HasRoot">The device is rooted (<see cref="RootDetector"/>).</param>
/// <param name="HasAllFilesAccess">"All files access" (MANAGE_EXTERNAL_STORAGE, Android 11+) or the storage permission
/// (WRITE_EXTERNAL_STORAGE, Android 8-10) is granted.</param>
/// <param name="Choice">The folder the user picked earlier (<see cref="StorageChoice"/>).</param>
/// <param name="CustomFolder">The folder of <see cref="StorageChoice.CustomFolder"/> (a real path: the system picker's
/// tree converted by <see cref="SafTreePath"/>).</param>
public readonly record struct DataRootInputs(
    string? AppSpecificDir,
    string InternalDir,
    string? SharedStorageDir,
    bool HasRoot,
    bool HasAllFilesAccess,
    StorageChoice Choice = StorageChoice.None,
    string? CustomFolder = null);

/// <summary>The data folder to use now and what the app still has to do to reach the visible one.</summary>
/// <param name="Root">The folder the app uses in this process (<see cref="DataLayout.Root"/>).</param>
/// <param name="VisibleRoot">/storage/emulated/0/.vitasharp, the folder the user can see and edit (null when shared
/// storage is not mounted).</param>
/// <param name="NeedsPermission">No root and no all-files access: the app asks for it and shows the warning; the data
/// stays in the app-specific folder meanwhile.</param>
/// <param name="MigrateFrom">The app-specific folder still holds data (or a migration was interrupted): it is moved to
/// <see cref="VisibleRoot"/> by <see cref="DataMigration"/> before <see cref="VisibleRoot"/> is used. <see cref="Root"/>
/// is the old folder until then.</param>
/// <param name="Question">What the user has to answer first (<see cref="StorageQuestion"/>); <see cref="Root"/> is the
/// app-specific folder until then.</param>
/// <param name="SharedDataUnused">The user picked the app-specific folder (<see cref="StorageChoice.AppFolder"/>) and
/// the visible folder still holds data: the library says so and lets the user pick again.</param>
/// <param name="Warning">The picked folder (<see cref="StorageChoice.CustomFolder"/>) could not be used (missing, an SD
/// card taken out, no permission): why, while the default rules decide.</param>
public readonly record struct DataRootPlan(
    string Root,
    string? VisibleRoot,
    bool NeedsPermission,
    string? MigrateFrom,
    StorageQuestion Question = StorageQuestion.None,
    bool SharedDataUnused = false,
    string Warning = "")
{
    public bool NeedsMigration => MigrateFrom != null;
}

/// <summary>
/// The one place that decides the Android app's data folder (docs/android/README.md, "Storage"):
/// <list type="bullet">
/// <item>rooted device: the app-specific folder (Android/data/&lt;package&gt;/files), where it always was;</item>
/// <item>the user picked a folder (<see cref="StorageChoice"/>): that one;</item>
/// <item>no root, no access: the app-specific folder, with <see cref="DataRootPlan.NeedsPermission"/>, and
/// <see cref="StorageQuestion.GrantForSharedData"/> when the visible folder already holds data (the app must not start
/// empty in silence while the user's games and saves wait in /storage/emulated/0/.vitasharp);</item>
/// <item>no root, all-files access granted: <c>/storage/emulated/0/.vitasharp</c>, visible and editable in any file
/// manager, after the old folder's content was moved there (<see cref="DataMigration"/>, which never overwrites a file
/// of the visible folder), or <see cref="StorageQuestion.ChooseBetweenFolders"/> when both folders hold the user's
/// data.</item>
/// </list>
/// Without shared storage mounted, the app-specific folder or the internal one, as before.
/// </summary>
public static class DataRoot
{
    /// <summary>The visible folder's name in the root of the shared storage.</summary>
    public const string VisibleFolderName = ".vitasharp";

    public static DataRootPlan Resolve(DataRootInputs inputs)
    {
        if (inputs.Choice != StorageChoice.CustomFolder)
        {
            return ResolveDefault(inputs);
        }

        string? visibleRoot = inputs.SharedStorageDir is { Length: > 0 } sharedDir ? Path.Combine(sharedDir, VisibleFolderName) : null;
        string why;
        if (inputs.CustomFolder is not { Length: > 0 } custom || !Path.IsPathFullyQualified(custom))
        {
            why = "no folder was saved with the choice";
        }
        else if (VitaSharp.Core.Storage.DataFolder.WhyUnusable(custom) is { } unusable)
        {
            why = $"{custom} {unusable}";
        }
        else
        {
            return new DataRootPlan(Path.GetFullPath(custom), visibleRoot, false, null);
        }

        // the default rules, without the pick (a missing card must not look like an empty install in silence)
        return ResolveDefault(inputs with { Choice = StorageChoice.None }) with { Warning = $"the chosen data folder: {why}; using the default folder" };
    }

    /// <summary>The rules without a picked folder (<see cref="StorageChoice.CustomFolder"/> is handled above).</summary>
    public static DataRootPlan ResolveDefault(DataRootInputs inputs)
    {
        string old = inputs.AppSpecificDir is { Length: > 0 } ext ? ext : inputs.InternalDir;
        if (inputs.SharedStorageDir is not { Length: > 0 } shared)
        {
            return new DataRootPlan(old, null, false, null);
        }

        string visible = Path.Combine(shared, VisibleFolderName);
        if (inputs.HasRoot)
        {
            return new DataRootPlan(old, visible, false, null);
        }

        if (inputs.Choice == StorageChoice.AppFolder)
        {
            return new DataRootPlan(old, visible, false, null, SharedDataUnused: DataInventory.LooksLikeData(visible));
        }

        if (!inputs.HasAllFilesAccess)
        {
            StorageQuestion question = DataInventory.LooksLikeData(visible) ? StorageQuestion.GrantForSharedData : StorageQuestion.None;
            return new DataRootPlan(old, visible, true, null, question);
        }

        string? source = inputs.AppSpecificDir is { Length: > 0 } appSpecific ? appSpecific : null;
        if (source != null && DataMigration.IsInterrupted(visible))
        {
            // a move or merge that stopped: it resumes (it overwrites nothing, whatever started it)
            return new DataRootPlan(old, visible, false, source);
        }

        if (inputs.Choice == StorageChoice.SharedFolder || source == null || !DataMigration.IsPending(source, visible))
        {
            return new DataRootPlan(visible, visible, false, null);
        }

        if (DataInventory.LooksLikeData(visible) && DataInventory.HasUserData(source))
        {
            return new DataRootPlan(old, visible, false, null, StorageQuestion.ChooseBetweenFolders);
        }

        // the first move, or only what the app wrote by itself (empty folders, its log) since a reinstall: merged in,
        // nothing of the visible folder replaced
        return new DataRootPlan(old, visible, false, source);
    }
}

/// <summary>
/// Root detection for <see cref="DataRoot"/>: an executable <c>su</c> in a folder of PATH or in one of the folders where
/// the common root solutions install it (the list root checkers such as RootBeer use).
/// </summary>
public static class RootDetector
{
    /// <summary>Where SuperSU, Magisk and the older root packages put the su binary.</summary>
    public static readonly string[] KnownSuFolders =
    [
        "/system/bin", "/system/xbin", "/sbin", "/su/bin", "/system/sbin", "/vendor/bin", "/system/bin/failsafe",
        "/data/local", "/data/local/bin", "/data/local/xbin", "/system/sd/xbin", "/debug_ramdisk",
    ];

    /// <summary>True when <c>su</c> exists in a PATH folder (<paramref name="pathVariable"/>, ':'-separated) or a known
    /// folder; <paramref name="fileExists"/> is File.Exists on the device (a parameter for the tests).</summary>
    public static bool HasSu(string? pathVariable, Func<string, bool> fileExists)
    {
        var folders = new List<string>();
        if (!string.IsNullOrEmpty(pathVariable))
        {
            folders.AddRange(pathVariable.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        folders.AddRange(KnownSuFolders);
        foreach (string folder in folders)
        {
            if (fileExists(folder.TrimEnd('/') + "/su"))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>This device: PATH and File.Exists.</summary>
    public static bool HasSu()
    {
        return HasSu(Environment.GetEnvironmentVariable("PATH"), File.Exists);
    }
}
