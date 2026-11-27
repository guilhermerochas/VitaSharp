namespace VitaSharp.Mobile.Core.Storage;

/// <summary>
/// The file path of a folder picked with the system's folder picker (ACTION_OPEN_DOCUMENT_TREE, the Storage Access
/// Framework). The emulator reads and writes its data folder with plain file calls, so a picked tree is turned into
/// the path the external storage provider (com.android.externalstorage.documents) names in its document id, the way
/// PPSSPP turns its picked memstick folder into a path when it can: "primary:Games/VitaSharp" is
/// /storage/emulated/0/Games/VitaSharp, "1234-ABCD:VitaSharp" is the SD card's /storage/1234-ABCD/VitaSharp. Other
/// providers (cloud drives, downloads) have no path and are refused. Using a path outside the app's own folders needs
/// "All files access" (<see cref="NeedsAllFilesAccess"/>).
/// </summary>
public static class SafTreePath
{
    /// <summary>The provider of the device storage and SD cards (the only one whose documents are files).</summary>
    public const string ExternalStorageAuthority = "com.android.externalstorage.documents";

    /// <summary>The path for the tree document id <paramref name="treeDocumentId"/> of <paramref name="authority"/>;
    /// null when the provider is not the device storage. <paramref name="primaryStorage"/> is
    /// /storage/emulated/0 (Environment.ExternalStorageDirectory).</summary>
    public static string? ToPath(string? authority, string? treeDocumentId, string primaryStorage)
    {
        if (authority != ExternalStorageAuthority || string.IsNullOrEmpty(treeDocumentId))
        {
            return null;
        }

        int colon = treeDocumentId.IndexOf(':');
        if (colon <= 0)
        {
            return null;
        }

        string volume = treeDocumentId[..colon];
        string relative = treeDocumentId[(colon + 1)..].Trim('/');
        if (relative.Split('/').Any(part => part == ".."))
        {
            return null;
        }

        // the volume names are fixed by the provider: "primary" for the internal shared storage, the SD card's
        // filesystem UUID (as mounted under /storage) otherwise; "home" is primary's Documents folder
        string root = volume switch
        {
            "primary" => primaryStorage.TrimEnd('/'),
            "home" => primaryStorage.TrimEnd('/') + "/Documents",
            _ when volume.All(c => char.IsAsciiHexDigit(c) || c == '-') => "/storage/" + volume,
            _ => "",
        };
        if (root.Length == 0)
        {
            return null;
        }

        return relative.Length == 0 ? root : root + "/" + relative;
    }

    /// <summary>True unless <paramref name="path"/> is inside one of the app's own folders
    /// (<paramref name="appFolders"/>: GetExternalFilesDirs, FilesDir), which the app may always write.</summary>
    public static bool NeedsAllFilesAccess(string path, IEnumerable<string?> appFolders)
    {
        foreach (string? folder in appFolders)
        {
            if (folder is { Length: > 0 } && VitaSharp.Core.Storage.DataFolder.IsSameOrInside(path, folder))
            {
                return false;
            }
        }

        return true;
    }
}
