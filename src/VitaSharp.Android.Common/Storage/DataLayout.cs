namespace VitaSharp.Android.Storage;

/// <summary>
/// The app's data folder, laid out like the desktop's (docs/android/README.md, "Storage"):
/// <code>
/// &lt;root&gt;/fs/ux0/app/TITLEID      installed games (VPK installs land here)
/// &lt;root&gt;/fs/ux0/user/00/savedata saves, as on the console (the emulator writes them)
/// &lt;root&gt;/fs/ux0/user/00/trophy   trophy unlocks
/// &lt;root&gt;/fs/vs0, os0, sa0, pd0      the installed firmware (FirmwareSetup)
/// &lt;root&gt;/logs                      last.log of the current session and the previous ones
/// &lt;root&gt;/shadercache, np, patches   as on the desktop
/// &lt;root&gt;/config/app.json           global settings (AppPreferences)
/// &lt;root&gt;/config/games/TITLEID.json per-game settings and play statistics (GameSettings)
/// &lt;root&gt;/config/input.json         controller profiles (Core InputProfileStore)
/// &lt;root&gt;/gpu_driver.json           GPU driver choice and libadrenotools options (GpuDriverScreen)
/// &lt;internal&gt;/gpu_drivers/TITLE    installed GPU driver packages, &lt;internal&gt;/gpu_driver_files the driver's redirected files
/// &lt;internal&gt;/run/game.json         the running game process (GameProcessMarker)
/// </code>
/// The root is the app-specific external storage (Context.GetExternalFilesDir(null),
/// /sdcard/Android/data/&lt;package&gt;/files) when it is mounted, else the internal files folder. Both are plain
/// POSIX paths the emulator opens and maps directly; neither needs a storage permission. &lt;internal&gt; is the internal
/// files folder (Context.FilesDir): GPU driver libraries must be there, because the linker refuses to load a library from
/// shared storage (libadrenotools: "MUST NOT be on sdcard/storage").
/// </summary>
public sealed class DataLayout(string root, string? internalRoot = null)
{
    public string Root { get; } = Path.GetFullPath(root);
    public string InternalRoot { get; } = Path.GetFullPath(internalRoot ?? root);
    public string GpuDriverSettingsFile => Path.Combine(Root, "gpu_driver.json");
    public string GpuDrivers => Path.Combine(InternalRoot, "gpu_drivers");
    public string GpuDriverFiles => Path.Combine(InternalRoot, "gpu_driver_files");
    public string GameProcessFile => Path.Combine(InternalRoot, "run", "game.json");
    public string Fs => Path.Combine(Root, "fs");
    public string Ux0 => Path.Combine(Fs, "ux0");
    public string Apps => Path.Combine(Ux0, "app");
    public string SaveData => Path.Combine(Ux0, "user", "00", "savedata");
    public string Logs => Path.Combine(Root, "logs");
    public string ShaderCache => Path.Combine(Root, "shadercache");
    public string Np => Path.Combine(Root, "np");
    public string Patches => Path.Combine(Root, "patches");
    public string Config => Path.Combine(Root, "config");
    public string AppSettingsFile => Path.Combine(Config, "app.json");
    public string GameSettingsDir => Path.Combine(Config, "games");
    public string InputProfiles => Path.Combine(Config, Core.Input.InputProfileStore.FileName);

    /// <summary>Per-game settings file. Title ids are 9 characters (PCSA00029); anything else is sanitised.</summary>
    public string GameSettingsFile(string titleId) => Path.Combine(GameSettingsDir, Sanitize(titleId) + ".json");

    /// <summary>The save folder of a title (ux0:user/00/savedata/TITLEID).</summary>
    public string SaveDir(string titleId) => Path.Combine(SaveData, Sanitize(titleId));

    public void EnsureCreated()
    {
        foreach (var d in new[] { Root, Fs, Ux0, Apps, SaveData, Logs, Config, GameSettingsDir }) Directory.CreateDirectory(d);
    }

    static string Sanitize(string s)
    {
        var chars = s.Where(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-').ToArray();
        return chars.Length > 0 ? new string(chars) : "_";
    }
}
