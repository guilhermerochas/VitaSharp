using System.Runtime.CompilerServices;
using VitaSharp.Core.Util;

namespace VitaSharp.Tests;

/// <summary>
/// The suite's own data folder: before any test runs, the test assembly points <see cref="EnvVars.Data"/> at a new
/// temporary folder of this run, so everything the front-end resolves from it (Program.DataRoot: input.json,
/// launcher.json, playtime.json, logs, shadercache, savestates, diagnostics, gpudumps, the fs/ of the firmware) is the
/// run's own and never the user's %APPDATA%\VitaSharp (~/.local/share/VitaSharp, ~/Library/Application
/// Support/VitaSharp). A value inherited from the shell is replaced too: a run is the same on every machine. Tests that
/// need a folder of their own still set the variable (and put back this one). The launcher's Vita3K detection
/// (<see cref="VitaSharp.Desktop.Launcher.FirmwarePanel.FindVita3K"/>) finds nothing, as on a machine without Vita3K.
/// The product is unchanged: only the test process sets these.
/// </summary>
internal static class TestDataIsolation
{
    /// <summary>The run's data folder (under the system's temporary folder), deleted when the test process exits.</summary>
    public static string Root { get; private set; } = "";

    /// <summary>A VitaSharp data folder with installed games and firmware that a few tests take real modules from,
    /// read only (<see cref="EnvVars.TestInstalledData"/>); null when unset or missing, and those tests are skipped.</summary>
    public static string? InstalledData
    {
        get
        {
            string? dir = Environment.GetEnvironmentVariable(EnvVars.TestInstalledData);
            return !string.IsNullOrEmpty(dir) && Directory.Exists(dir) ? Path.GetFullPath(dir) : null;
        }
    }

    /// <summary>fs/ of <see cref="InstalledData"/>, or null.</summary>
    public static string? InstalledFsRoot => InstalledData is { } d ? Path.Combine(d, "fs") : null;

    [ModuleInitializer]
    internal static void Initialize()
    {
        Root = Path.Combine(Path.GetTempPath(), "vitasharp-testdata-" + Environment.ProcessId + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Root);
        Environment.SetEnvironmentVariable(EnvVars.Data, Root);
        VitaSharp.Desktop.Launcher.FirmwarePanel.FindVita3K = NoVita3K;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Delete();
    }

    /// <summary>The launcher's Vita3K lookup in the tests: none installed.</summary>
    internal static string? NoVita3K() => null;

    private static void Delete()
    {
        try
        {
            Directory.Delete(Root, true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
