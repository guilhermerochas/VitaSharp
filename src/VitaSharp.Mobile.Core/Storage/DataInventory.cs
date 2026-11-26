using VitaSharp.Core.Firmware;

namespace VitaSharp.Mobile.Core.Storage;

/// <summary>What a data folder holds, for the choice between two folders (<see cref="StorageQuestion.ChooseBetweenFolders"/>).</summary>
/// <param name="Folder">The folder.</param>
/// <param name="Games">Installed titles (folders in fs/ux0/app).</param>
/// <param name="FirmwareVersion">The installed firmware's version, "" when installed without a known version, null
/// when there is none.</param>
/// <param name="Saves">Titles with saves (folders in fs/ux0/user/00/savedata).</param>
/// <param name="SaveStateTitles">Titles with save states (folders in savestates).</param>
/// <param name="Files">Files in the whole folder.</param>
/// <param name="Bytes">Their size.</param>
public readonly record struct DataSummary(
    string Folder,
    int Games,
    string? FirmwareVersion,
    int Saves,
    int SaveStateTitles,
    long Files,
    long Bytes);

/// <summary>
/// Looks at a data folder (<see cref="DataLayout"/>) without changing it: whether it holds VitaSharp data at all, whether
/// it holds anything the user made, and a summary of what is there.
/// </summary>
public static class DataInventory
{
    /// <summary>The top-level folder that holds only what the app writes by itself on every start (its own log).</summary>
    public const string LogsFolder = "logs";

    /// <summary>
    /// The folder holds a VitaSharp data folder: one of the layout's own top-level folders, or the journal of a move.
    /// Only folder lookups are used on purpose: without "All files access" Android 11+ still answers whether a folder
    /// exists in the shared storage, but hides the files another install wrote (firmware.json, the saves' files), so a
    /// file marker would not be seen exactly when it matters (after a reinstall).
    /// </summary>
    public static bool LooksLikeData(string folder)
    {
        try
        {
            if (!Directory.Exists(folder))
            {
                return false;
            }

            var layout = new DataLayout(folder);
            if (Directory.Exists(layout.Fs) || Directory.Exists(layout.Config) || Directory.Exists(layout.SaveStates))
            {
                return true;
            }

            return File.Exists(Path.Combine(folder, DataMigration.JournalFileName));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// The folder holds a file the user made or the emulator wrote for a game (games, firmware, saves, states, settings,
    /// caches...): anything but empty folders and the app's own log, which every start writes. A folder the app only
    /// started in (after a reinstall, say) has none.
    /// </summary>
    public static bool HasUserData(string folder)
    {
        try
        {
            if (!Directory.Exists(folder))
            {
                return false;
            }

            foreach (string entry in Directory.EnumerateDirectories(folder))
            {
                if (Path.GetFileName(entry) != LogsFolder && Directory.EnumerateFiles(entry, "*", SearchOption.AllDirectories).Any())
                {
                    return true;
                }
            }

            return Directory.EnumerateFiles(folder).Any(file => Path.GetFileName(file) != DataMigration.JournalFileName);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // unreadable: taken as the user's, so that nothing is merged over it without asking
            return true;
        }
    }

    /// <summary>What <paramref name="folder"/> holds (counts that cannot be read stay 0).</summary>
    public static DataSummary Summarize(string folder)
    {
        var layout = new DataLayout(folder);
        long files = 0;
        long bytes = 0;
        try
        {
            if (Directory.Exists(folder))
            {
                foreach (string file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
                {
                    files++;
                    bytes += new FileInfo(file).Length;
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // what was counted so far
        }

        string? firmware = null;
        try
        {
            if (FirmwareInfo.Read(layout.Fs) is { } installed)
            {
                firmware = installed.Version ?? "";
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException or System.Text.Json.JsonException)
        {
            firmware = null;
        }

        return new DataSummary(layout.Root, CountFolders(layout.Apps), firmware, CountFolders(layout.SaveData),
            CountFolders(layout.SaveStates), files, bytes);
    }

    private static int CountFolders(string folder)
    {
        try
        {
            return Directory.Exists(folder) ? Directory.EnumerateDirectories(folder).Count() : 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}
